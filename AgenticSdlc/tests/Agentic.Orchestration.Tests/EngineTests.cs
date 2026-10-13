using Xunit;

namespace Agentic.Orchestration.Tests;

public class EngineTests
{
    private sealed class SlowAgent : IAgent
    {
        public string Name => "slow";

        public async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return AgentResult.Of(new ArtifactDraft("a", "document", "x"));
        }
    }

    private static WorkflowGraph Linear(params NodeSpec[] nodes) => new("t", nodes);

    [Fact]
    public async Task Runs_diamond_with_parallel_branches_and_a_join()
    {
        var running = 0;
        var peak = 0;
        async Task<AgentResult> Branch(string artifact)
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            do { seen = peak; } while (now > seen && Interlocked.CompareExchange(ref peak, now, seen) != seen);
            await Task.Delay(80);
            Interlocked.Decrement(ref running);
            return AgentResult.Of(new ArtifactDraft(artifact, "document", artifact));
        }

        var joinSawBoth = false;
        var agents = new AgentRegistry()
            .Register(new DelegateAgent("start", _ => T.Out("seed", "s")))
            .Register(new DelegateAgent("left", _ => Branch("l")))
            .Register(new DelegateAgent("right", _ => Branch("r")))
            .Register(new DelegateAgent("join", r =>
            {
                joinSawBoth = r.Context.Latest("l") is not null && r.Context.Latest("r") is not null;
                return T.Out("done", "d");
            }));

        var graph = Linear(
            T.Node("s", "start", produces: new[] { "seed" }),
            T.Node("l", "left", dependsOn: new[] { "s" }, consumes: new[] { "seed" }, produces: new[] { "l" }),
            T.Node("r", "right", dependsOn: new[] { "s" }, consumes: new[] { "seed" }, produces: new[] { "r" }),
            T.Node("j", "join", dependsOn: new[] { "l", "r" }, consumes: new[] { "l", "r" }, produces: new[] { "done" }));

        var result = await T.Engine(graph, agents).RunAsync();

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.True(joinSawBoth);
        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task Retries_transient_failures_with_bounded_attempts_and_reports_mttr()
    {
        var agent = new DelegateAgent("flaky", r => r.Attempt < 3
            ? throw new TransientAgentException("boom")
            : T.Out("a", "ok"));
        var result = await T.Engine(Linear(T.Node("n", "flaky", produces: new[] { "a" })), new AgentRegistry().Register(agent)).RunAsync();

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(3, agent.Calls);
        Assert.Equal(2, result.Metrics.Retries);
        Assert.NotNull(result.Metrics.MttrSeconds);
    }

    [Fact]
    public async Task Exhausted_retries_fail_the_run_and_block_dependents()
    {
        var bad = new DelegateAgent("bad", _ => throw new TransientAgentException("always"));
        var never = new DelegateAgent("never", _ => T.Out("b", "b"));
        var graph = Linear(
            T.Node("a", "bad", retries: 1, produces: new[] { "a" }),
            T.Node("b", "never", dependsOn: new[] { "a" }, produces: new[] { "b" }));

        var result = await T.Engine(graph, new AgentRegistry().Register(bad).Register(never)).RunAsync();

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(2, bad.Calls);
        Assert.Equal(0, never.Calls);
        Assert.Equal(NodeStatus.Failed, result.Nodes["a"]);
        Assert.Equal(NodeStatus.Blocked, result.Nodes["b"]);
    }

    [Fact]
    public async Task Fallback_agent_is_used_after_primary_exhausts_retries()
    {
        var primary = new DelegateAgent("primary", _ => throw new TransientAgentException("llm down"));
        var fallback = new DelegateAgent("fallback", _ => T.Out("a", "from template"));
        var result = await T.Engine(
            Linear(T.Node("n", "primary", retries: 1, fallback: "fallback", produces: new[] { "a" })),
            new AgentRegistry().Register(primary).Register(fallback)).RunAsync();

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(2, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task Terminal_failure_rolls_back_completed_compensable_nodes()
    {
        var side = new DelegateAgent("side", _ => T.Out("a", "a"));
        var boom = new DelegateAgent("boom", _ => throw new PermanentAgentException("nope"));
        var graph = Linear(
            T.Node("a", "side", compensable: true, produces: new[] { "a" }),
            T.Node("b", "boom", dependsOn: new[] { "a" }, produces: new[] { "b" }));

        var result = await T.Engine(graph, new AgentRegistry().Register(side).Register(boom)).RunAsync();

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(1, side.Compensations);
        Assert.Equal(NodeStatus.RolledBack, result.Nodes["a"]);
        Assert.Equal(1, result.Metrics.Rollbacks);
    }

    [Fact]
    public async Task Critical_policy_violation_stops_immediately_without_retry()
    {
        var leaky = new DelegateAgent("leaky", _ => T.Out("src", "var k = \"AKIAABCDEFGHIJKLMNOP\";", "code"));
        var result = await T.Engine(Linear(T.Node("n", "leaky", produces: new[] { "src" })), new AgentRegistry().Register(leaky)).RunAsync();

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(1, leaky.Calls);
        Assert.Contains("policy", result.StopReason);
        Assert.Equal(1, result.Metrics.PolicyViolations);
    }

    [Fact]
    public async Task High_severity_violation_is_fed_back_and_the_agent_self_corrects()
    {
        string? seenFeedback = null;
        var agent = new DelegateAgent("coder", r =>
        {
            if (r.Attempt == 1)
            {
                return T.Out("src", "var h = MD5.HashData(x);", "code");
            }

            seenFeedback = string.Join(" ", r.Feedback);
            return T.Out("src", "var h = SHA256.HashData(x);", "code");
        });
        var result = await T.Engine(Linear(T.Node("n", "coder", produces: new[] { "src" })), new AgentRegistry().Register(agent)).RunAsync();

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Contains("weak-hash", seenFeedback);
        Assert.Equal(1, result.Metrics.GateFailures);
    }

    [Fact]
    public async Task Undeclared_outputs_violate_the_autonomy_boundary()
    {
        var rogue = new DelegateAgent("rogue", _ => Task.FromResult(AgentResult.Of(
            new ArtifactDraft("a", "document", "ok"), new ArtifactDraft("extra", "document", "sneaky"))));
        var result = await T.Engine(Linear(T.Node("n", "rogue", retries: 0, produces: new[] { "a" })), new AgentRegistry().Register(rogue)).RunAsync();

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("autonomy-boundary", result.StopReason);
    }

    [Fact]
    public async Task Rejected_approval_before_execution_prevents_the_action()
    {
        var deploy = new DelegateAgent("deploy", _ => T.Out("d", "deployed"));
        var approver = new ScriptedApprovalProvider(_ => new ApprovalDecision(ApprovalVerdict.Rejected, "cab", "not now"));
        var graph = Linear(T.Node("deploy", "deploy", produces: new[] { "d" }, impact: Impact.High, approval: ApprovalMode.BeforeExecution));

        var result = await T.Engine(graph, new AgentRegistry().Register(deploy), approver).RunAsync();

        Assert.Equal(0, deploy.Calls);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(1, result.Metrics.Approvals);
    }

    [Fact]
    public async Task Changes_requested_before_commit_triggers_rework()
    {
        var round = 0;
        var approver = new ScriptedApprovalProvider(_ => ++round == 1
            ? new ApprovalDecision(ApprovalVerdict.ChangesRequested, "architect", "add failure modes")
            : new ApprovalDecision(ApprovalVerdict.Approved, "architect", "ok"));
        string? feedback = null;
        var design = new DelegateAgent("design", r => { feedback = string.Join(" ", r.Feedback); return T.Out("d", "v" + r.Attempt); });
        var graph = Linear(T.Node("design", "design", produces: new[] { "d" }, impact: Impact.High, approval: ApprovalMode.BeforeCommit));

        var result = await T.Engine(graph, new AgentRegistry().Register(design), approver).RunAsync();

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(2, design.Calls);
        Assert.Contains("add failure modes", feedback);
    }

    [Fact]
    public async Task Upstream_change_replans_consumers_and_cascades_only_when_outputs_differ()
    {
        var reqAgent = new DelegateAgent("req", _ => T.Out("plan", "plan-v1"));
        var designAgent = new DelegateAgent("design", r => T.Out("design", "design for " + r.Context.Require("req").Content));
        var docsAgent = new DelegateAgent("docs", r => T.Out("docs", "docs for " + r.Context.Require("plan").Content)); // consumes plan, not req

        var graphWithExternal = new WorkflowGraph("t", new[]
        {
            T.Node("plan", "req", consumes: new[] { "req" }, produces: new[] { "plan" }),
            T.Node("design", "design", dependsOn: new[] { "plan" }, consumes: new[] { "req" }, produces: new[] { "design" }),
            T.Node("docs", "docs", dependsOn: new[] { "design" }, consumes: new[] { "plan" }, produces: new[] { "docs" }),
        }, new[] { "req" });

        var engine = T.Engine(graphWithExternal, new AgentRegistry().Register(reqAgent).Register(designAgent).Register(docsAgent));
        engine.Context.Publish(new ArtifactDraft("req", "document", "v1"), "human");
        var injected = false;
        engine.NodeStatusChanged += (id, s) =>
        {
            if (id == "design" && s == NodeStatus.Succeeded && !injected)
            {
                injected = true;
                engine.SubmitChange("req", "document", "v2", "human:po");
            }
        };

        var result = await engine.RunAsync();

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(2, reqAgent.Calls);   // plan re-ran
        Assert.Equal(2, designAgent.Calls);
        Assert.Equal(1, docsAgent.Calls);  // plan output identical -> no cascade
        Assert.Equal("design for v2", engine.Context.Latest("design")!.Content);
        Assert.True(result.Metrics.Replans >= 2);
    }

    [Fact]
    public async Task Replan_budget_is_bounded()
    {
        var counter = 0;
        var agent = new DelegateAgent("churn", _ => T.Out("a", "x" + Interlocked.Increment(ref counter)));
        var consumer = new DelegateAgent("consumer", _ => T.Out("b", "b"));
        var graph = new WorkflowGraph("t", new[]
        {
            T.Node("a", "churn", produces: new[] { "a" }),
            T.Node("b", "consumer", dependsOn: new[] { "a" }, consumes: new[] { "a" }, produces: new[] { "b" }),
        });
        var engine = T.Engine(graph, new AgentRegistry().Register(agent).Register(consumer), options: new EngineOptions
        {
            RetryBaseDelay = TimeSpan.FromMilliseconds(1), MaxReplans = 2,
        });
        // 'a' keeps changing each time 'b' completes, so b would be re-planned forever without a budget.
        engine.NodeStatusChanged += (id, s) =>
        {
            if (id == "b" && s == NodeStatus.Succeeded)
            {
                engine.SubmitChange("a", "document", "ext" + Guid.NewGuid(), "ext");
            }
        };

        var result = await engine.RunAsync();

        Assert.NotEqual(RunStatus.Completed, result.Status);
        Assert.Contains("re-plan budget", result.StopReason);
    }

    [Fact]
    public async Task Safe_stop_halts_scheduling_and_cancels_in_flight_work()
    {
        var slow = new SlowAgent();
        var next = new DelegateAgent("next", _ => T.Out("b", "b"));
        var graph = Linear(
            T.Node("a", "slow", produces: new[] { "a" }),
            T.Node("b", "next", dependsOn: new[] { "a" }, produces: new[] { "b" }));
        var engine = T.Engine(graph, new AgentRegistry().Register(slow).Register(next));
        engine.NodeStatusChanged += (id, s) =>
        {
            if (id == "a" && s == NodeStatus.Running)
            {
                Task.Run(async () => { await Task.Delay(50); engine.RequestSafeStop("operator kill-switch"); });
            }
        };

        var run = engine.RunAsync();
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(run, finished);
        var result = await run;
        Assert.Equal(RunStatus.SafeStopped, result.Status);
        Assert.Equal(0, next.Calls);
    }

    [Fact]
    public async Task Audit_trail_is_hash_chained_and_tamper_evident()
    {
        var engine = T.Engine(Linear(T.Node("n", "a", produces: new[] { "x" })), new AgentRegistry().Register(new DelegateAgent("a", _ => T.Out("x", "1"))));
        await engine.RunAsync();
        var audit = new AuditLog();
        audit.Append("r", "A", null, "one");
        audit.Append("r", "B", null, "two");
        audit.Append("r", "C", null, "three");

        Assert.True(audit.VerifyChain());
        var tampered = audit.Events.ToList();
        tampered[1] = tampered[1] with { Message = "forged" };
        Assert.False(AuditLog.Verify(tampered));
    }

    [Fact]
    public async Task Lineage_traces_decisions_back_to_inputs()
    {
        var agents = new AgentRegistry()
            .Register(new DelegateAgent("a", _ => T.Out("req", "r")))
            .Register(new DelegateAgent("b", _ => T.Out("design", "d")));
        var graph = Linear(
            T.Node("a", "a", produces: new[] { "req" }),
            T.Node("b", "b", dependsOn: new[] { "a" }, consumes: new[] { "req" }, produces: new[] { "design" }));
        var engine = T.Engine(graph, agents);
        await engine.RunAsync();

        var lineage = engine.Context.TraceLineage("design");
        Assert.Contains(lineage, l => l.Contains("design@v1") && l.Contains("<- b"));
        Assert.Contains(lineage, l => l.Contains("req@v1") && l.Contains("<- a"));
    }
}
