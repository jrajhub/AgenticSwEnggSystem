using System.Text;
using System.Text.Json;
using Agentic.Agents;
using Agentic.Cli;
using Agentic.Orchestration;

var options = CliOptions.Parse(args);
if (options is null)
{
    Console.WriteLine("""
Usage: agentic <greenfield|brownfield|ambiguous|all> [options]
  --chaos <none|transient|policy|secret|rollback|reject>   fault injection (default none)
  --runner <real|simulated>                                 'real' runs dotnet test in the sandbox (default real)
  --interactive                                             human approvals on the console (default: scripted approver)
  --out <dir>                                               output directory (default ./out)
  --repo <dir>                                              repository root containing src/UrlShortener.Api
Env: ANTHROPIC_API_KEY (+ optional ANTHROPIC_MODEL) enables LLM-generated design/risk/docs.
""");
    return 2;
}

var exit = 0;
foreach (var name in options.Scenario == "all" ? Scenarios.Names : new[] { options.Scenario })
{
    var status = await ScenarioRunner.RunAsync(Scenarios.Create(name), options);
    if (status != RunStatus.Completed)
    {
        exit = 1;
    }
}

return exit;

namespace Agentic.Cli
{
    public sealed record CliOptions(string Scenario, string Chaos, string Runner, bool Interactive, string OutDir, string RepoRoot)
    {
        public static CliOptions? Parse(string[] args)
        {
            if (args.Length == 0 || args[0].StartsWith("-"))
            {
                return null;
            }

            string Opt(string flag, string fallback)
            {
                var i = Array.IndexOf(args, flag);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
            }

            var scenario = args[0];
            if (scenario != "all" && !Scenarios.Names.Contains(scenario))
            {
                return null;
            }

            return new CliOptions(scenario, Opt("--chaos", "none"), Opt("--runner", "real"), args.Contains("--interactive"),
                Path.GetFullPath(Opt("--out", "out")), Path.GetFullPath(Opt("--repo", FindRepoRoot())));
        }

        private static string FindRepoRoot()
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "AgenticSdlc.sln")))
                    {
                        return dir.FullName;
                    }
                }
            }

            return Directory.GetCurrentDirectory();
        }
    }

    public static class ScenarioRunner
    {
        public static async Task<RunStatus> RunAsync(Scenario scenario, CliOptions opt)
        {
            var root = Path.Combine(opt.OutDir, scenario.Name);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            Directory.CreateDirectory(root);

            var chaos = ChaosProfile.From(opt.Chaos);
            var workspace = new Workspace(opt.RepoRoot, Path.Combine(root, "sandbox"), Path.Combine(root, "release"));
            ICommandRunner runner = opt.Runner == "simulated" ? new SimulatedCommandRunner() : new AllowListedCommandRunner();
            ILlmClient llm = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } key
                ? new AnthropicLlmClient(new HttpClient(), key, Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-sonnet-5-5")
                : new OfflineLlmClient();

            var deps = new AgentDeps(scenario.Playbook, workspace, runner, llm, chaos, scenario.Clarifier);
            IApprovalProvider approvals = opt.Interactive
                ? new ConsoleApprovalProvider()
                : new ScriptedApprovalProvider(r => chaos.RejectRelease && r.NodeId == "release"
                    ? new ApprovalDecision(ApprovalVerdict.Rejected, "release-manager", "Rejected by chaos profile")
                    : new ApprovalDecision(ApprovalVerdict.Approved, "scripted-approver", "auto-approved (demo mode)"));

            var audit = new AuditLog(Path.Combine(root, "audit.jsonl"));
            var policy = PolicyGuard.Default(new RequiredSectionsPolicy("user-docs", "Data classification", "Retention"));
            var engine = new WorkflowEngine(scenario.Graph, AgentFactory.CreateAll(deps), approvals, policy, audit,
                new EngineOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(150), MaxParallelism = 4 });

            Console.WriteLine();
            Console.WriteLine($"==== {scenario.Name.ToUpperInvariant()}: {scenario.Description}");
            Console.WriteLine($"run={engine.RunId} chaos={opt.Chaos} runner={opt.Runner} llm={(llm is OfflineLlmClient ? "offline-playbook" : "enabled")}");
            if (opt.Runner == "simulated")
            {
                Console.WriteLine("WARNING: simulated runner - test results are NOT real evidence.");
            }

            Console.WriteLine("Execution waves: " + string.Join("  |  ", scenario.Graph.Waves().Select(w => string.Join("+", w))));

            var started = DateTime.UtcNow;
            engine.NodeStatusChanged += (id, status) =>
                Console.WriteLine($"  [{(DateTime.UtcNow - started).TotalSeconds,6:0.00}s] {id,-13} {status}");
            engine.Context.Publish(new ArtifactDraft("raw-requirement", "document", scenario.RawRequirement), "human:requester");
            scenario.Hooks?.Invoke(engine);

            var result = await engine.RunAsync();
            Persist(root, scenario, engine, result, audit);
            Report(scenario, engine, result, audit, root);
            return result.Status;
        }

        private static void Persist(string root, Scenario scenario, WorkflowEngine engine, RunResult result, AuditLog audit)
        {
            var artifacts = Path.Combine(root, "artifacts");
            Directory.CreateDirectory(artifacts);
            foreach (var a in engine.Context.LatestArtifacts())
            {
                var ext = a.Name == "openapi" ? "yaml" : a.Kind is "code" or "test" ? "txt" : "md";
                File.WriteAllText(Path.Combine(artifacts, $"{a.Name}.v{a.Version}.{ext}"), a.Content);
            }

            File.WriteAllText(Path.Combine(root, "workflow.mmd"), scenario.Graph.ToMermaid());
            File.WriteAllText(Path.Combine(root, "metrics.json"),
                JsonSerializer.Serialize(new { result.Status, result.StopReason, result.Metrics, Nodes = result.Nodes.ToDictionary(k => k.Key, v => v.Value.ToString()) },
                    new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(root, "decisions.md"),
                string.Join("\n", engine.Context.Decisions.Select(d => $"- {d.Id} [{d.NodeId}] {d.Summary} :: {d.Rationale} (inputs: {string.Join(", ", d.Inputs)})")));
        }

        private static void Report(Scenario scenario, WorkflowEngine engine, RunResult result, AuditLog audit, string root)
        {
            var m = result.Metrics;
            Console.WriteLine();
            Console.WriteLine($"RESULT: {result.Status}" + (result.StopReason is null ? "" : $"  (stop reason: {result.StopReason})"));
            Console.WriteLine($"Nodes: {string.Join(", ", result.Nodes.OrderBy(n => n.Key).Select(n => $"{n.Key}={n.Value}"))}");
            Console.WriteLine($"Metrics: attempt-success {m.AttemptSuccessRate:P0} ({m.AttemptSuccesses}/{m.Attempts}), retries {m.Retries}, rollbacks {m.Rollbacks}, replans {m.Replans}, " +
                              $"gate failures {m.GateFailures}, policy violations {m.PolicyViolations}, approvals {m.Approvals}, " +
                              $"MTTR {(m.MttrSeconds is { } s ? $"{s:0.00}s" : "n/a")}, end-to-end {m.EndToEndSeconds:0.00}s");
            Console.WriteLine($"Audit: {audit.Events.Count} events, hash chain {(audit.VerifyChain() ? "VERIFIED" : "BROKEN")}");

            var final = new[] { "release-notes", "readiness-report" }.FirstOrDefault(n => engine.Context.Latest(n) is not null);
            if (final is not null)
            {
                Console.WriteLine($"Lineage of {final}:");
                foreach (var line in engine.Context.TraceLineage(final).Take(14))
                {
                    Console.WriteLine("  " + line);
                }
            }

            Console.WriteLine($"Outputs: {root}");
        }
    }
}
