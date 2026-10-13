using System.Collections.Concurrent;
using System.Diagnostics;

namespace Agentic.Orchestration;

public sealed class EngineOptions
{
    public int MaxParallelism { get; init; } = 4;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan NodeTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Max times a single node may be re-planned (re-run because inputs changed). Prevents oscillation.</summary>
    public int MaxReplans { get; init; } = 3;

    /// <summary>Hard budget on total agent invocations in a run (cost / runaway-loop guard).</summary>
    public int MaxTotalAttempts { get; init; } = 100;

    public bool RollbackOnFailure { get; init; } = true;
}

public sealed record RunResult(
    RunStatus Status, string? StopReason, IReadOnlyDictionary<string, NodeStatus> Nodes, MetricsReport Metrics);

/// <summary>
/// Stateful, governed DAG executor.
/// Scheduling: any node whose dependencies all succeeded runs (parallel up to MaxParallelism; joins wait for every dependency).
/// Governance: entry/exit gates, engine-embedded policy guard, undeclared-output rejection, approval checkpoints.
/// Resilience: bounded retries with feedback + backoff, fallback agent, compensation (rollback), safe-stop.
/// Adaptivity: when an artifact a finished node consumed changes version, that node is re-planned (re-run); downstream nodes
/// re-run only if the new outputs actually differ.
/// </summary>
public sealed class WorkflowEngine
{
    private sealed record PendingChange(string Name, string Kind, string Content, string Source);

    private sealed class NodeState
    {
        public NodeStatus Status = NodeStatus.Pending;
        public Dictionary<string, int> Consumed = new();
        public int Replans;
    }

    private sealed class GateFailure(string gate, string message, Severity severity) : Exception(message)
    {
        public string Gate { get; } = gate;
        public Severity Severity { get; } = severity;
    }

    private readonly WorkflowGraph _graph;
    private readonly AgentRegistry _agents;
    private readonly IApprovalProvider _approvals;
    private readonly PolicyGuard _policy;
    private readonly AuditLog _audit;
    private readonly EngineOptions _opts;
    private readonly MetricsCollector _metrics = new();
    private readonly Dictionary<string, NodeState> _states;
    private readonly object _lock = new();
    private readonly ConcurrentQueue<PendingChange> _changes = new();
    private readonly List<string> _completionOrder = new();
    private readonly HashSet<string> _needsCompensation = new();
    private readonly CancellationTokenSource _stop = new();
    private string? _stopReason;
    private bool _anyFailure;
    private int _totalAttempts;

    public WorkflowEngine(
        WorkflowGraph graph, AgentRegistry agents, IApprovalProvider approvals, PolicyGuard policy, AuditLog audit,
        EngineOptions? options = null, string? runId = null)
    {
        _graph = graph;
        _agents = agents;
        _approvals = approvals;
        _policy = policy;
        _audit = audit;
        _opts = options ?? new EngineOptions();
        RunId = runId ?? Guid.NewGuid().ToString("N")[..8];
        Context = new WorkflowContext(RunId);
        _states = graph.Nodes.Keys.ToDictionary(k => k, _ => new NodeState(), StringComparer.Ordinal);

        foreach (var node in graph.Nodes.Values)
        {
            foreach (var name in new[] { node.Agent, node.FallbackAgent }.OfType<string>())
            {
                if (!agents.Contains(name))
                {
                    throw new InvalidWorkflowException($"node '{node.Id}' references unregistered agent '{name}'");
                }
            }
        }
    }

    public string RunId { get; }
    public WorkflowContext Context { get; }
    public RunStatus Status { get; private set; } = RunStatus.NotStarted;
    public string? StopReason => _stopReason;

    /// <summary>Raised on every node status transition (used by the CLI timeline and by demo hooks that inject upstream changes).</summary>
    public event Action<string, NodeStatus>? NodeStatusChanged;

    public NodeStatus StatusOf(string nodeId)
    {
        lock (_lock)
        {
            return _states[nodeId].Status;
        }
    }

    /// <summary>Kill-switch. Stops scheduling, cancels in-flight agents cooperatively, then compensates side effects.</summary>
    public void RequestSafeStop(string reason)
    {
        lock (_lock)
        {
            if (_stopReason is not null)
            {
                return;
            }

            _stopReason = reason;
        }

        Audit("SafeStop", null, reason);
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Submit a changed upstream artifact (e.g. a revised requirement). Applied at the next scheduling tick; triggers re-planning.</summary>
    public void SubmitChange(string artifactName, string kind, string content, string source) =>
        _changes.Enqueue(new PendingChange(artifactName, kind, content, source));

    public async Task<RunResult> RunAsync(CancellationToken ct = default)
    {
        if (Status != RunStatus.NotStarted)
        {
            throw new InvalidOperationException("run already started");
        }

        Status = RunStatus.Running;
        _metrics.Start();
        Audit("RunStarted", null, $"workflow '{_graph.Name}'");

        using var registration = ct.Register(() => RequestSafeStop("cancelled by caller"));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var running = new Dictionary<string, Task>();

        while (true)
        {
            ApplyPendingChanges();
            await MarkStaleAsync();

            if (!_stop.IsCancellationRequested)
            {
                foreach (var node in ReadyNodes())
                {
                    if (running.Count >= _opts.MaxParallelism)
                    {
                        break;
                    }

                    SetStatus(node.Id, NodeStatus.Running);
                    var captured = node;
                    running[captured.Id] = Task.Run(() => ExecuteNodeAsync(captured, linked.Token), CancellationToken.None);
                }
            }

            if (running.Count == 0)
            {
                break;
            }

            await Task.WhenAny(running.Values);
            foreach (var id in running.Where(kv => kv.Value.IsCompleted).Select(kv => kv.Key).ToList())
            {
                await running[id];
                running.Remove(id);
            }
        }

        bool needRollback;
        lock (_lock)
        {
            needRollback = _anyFailure || _stopReason is not null;
        }

        if (needRollback && _opts.RollbackOnFailure)
        {
            await RollbackAsync();
        }

        foreach (var id in IdsWithStatus(NodeStatus.Pending))
        {
            SetStatus(id, NodeStatus.Blocked, _stopReason ?? "upstream did not complete");
        }

        bool allSucceeded;
        lock (_lock)
        {
            allSucceeded = _states.Values.All(s => s.Status == NodeStatus.Succeeded);
        }

        Status = allSucceeded && _stopReason is null ? RunStatus.Completed
            : _anyFailure ? RunStatus.Failed
            : _stopReason is not null ? RunStatus.SafeStopped
            : RunStatus.Failed;

        _metrics.Stop();
        Audit("RunFinished", null, Status.ToString(), new Dictionary<string, string> { ["reason"] = _stopReason ?? "" });

        Dictionary<string, NodeStatus> snapshot;
        lock (_lock)
        {
            snapshot = _states.ToDictionary(kv => kv.Key, kv => kv.Value.Status);
        }

        return new RunResult(Status, _stopReason, snapshot, _metrics.Report(snapshot.Count, snapshot.Values.Count(s => s == NodeStatus.Succeeded)));
    }

    // ---------------------------------------------------------------- scheduling

    private List<NodeSpec> ReadyNodes()
    {
        lock (_lock)
        {
            return _graph.TopologicalOrder
                .Select(id => _graph.Nodes[id])
                .Where(n => _states[n.Id].Status == NodeStatus.Pending
                            && n.DependsOn.All(d => _states[d].Status == NodeStatus.Succeeded))
                .ToList();
        }
    }

    private List<string> IdsWithStatus(NodeStatus status)
    {
        lock (_lock)
        {
            return _states.Where(kv => kv.Value.Status == status).Select(kv => kv.Key).ToList();
        }
    }

    private void ApplyPendingChanges()
    {
        while (_changes.TryDequeue(out var change))
        {
            var result = Context.Publish(new ArtifactDraft(change.Name, change.Kind, change.Content), change.Source);
            Audit("ChangeSubmitted", null, $"{result.Artifact.Ref} by {change.Source}" + (result.Changed ? "" : " (no effective change)"));
            if (result.Changed)
            {
                Context.Record("(external)", $"Upstream change: {result.Artifact.Ref}", $"submitted by {change.Source}", Array.Empty<string>());
            }
        }
    }

    /// <summary>Dynamic re-planning: a finished node whose consumed input has a newer version goes back to Pending.</summary>
    private async Task MarkStaleAsync()
    {
        var stale = new List<(NodeSpec Node, string Why)>();
        lock (_lock)
        {
            foreach (var node in _graph.Nodes.Values)
            {
                var state = _states[node.Id];
                if (state.Status != NodeStatus.Succeeded)
                {
                    continue;
                }

                foreach (var (name, version) in state.Consumed)
                {
                    var latest = Context.Latest(name);
                    if (latest is not null && latest.Version != version)
                    {
                        stale.Add((node, $"{name} v{version} -> v{latest.Version}"));
                        break;
                    }
                }
            }
        }

        foreach (var (node, why) in stale)
        {
            var state = _states[node.Id];
            if (state.Replans >= _opts.MaxReplans)
            {
                RequestSafeStop($"re-plan budget exhausted for '{node.Id}' ({why})");
                continue;
            }

            state.Replans++;
            if (node.Compensable)
            {
                await CompensateAsync(node, $"re-plan: {why}");
            }

            lock (_lock)
            {
                _completionOrder.Remove(node.Id);
            }

            _metrics.Replan();
            Audit("Replan", node.Id, why);
            Context.Record(node.Id, "Re-plan: upstream artifact changed", why, Array.Empty<string>());
            SetStatus(node.Id, NodeStatus.Pending, "stale: " + why);
        }
    }

    // ---------------------------------------------------------------- node execution

    private async Task ExecuteNodeAsync(NodeSpec node, CancellationToken ct)
    {
        try
        {
            await ExecuteCoreAsync(node, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            HandleCancelled(node);
        }
        catch (Exception ex)
        {
            Audit("EngineFault", node.Id, $"{ex.GetType().Name}: {ex.Message}");
            FailTerminal(node, "engine fault: " + ex.Message);
        }
    }

    private async Task ExecuteCoreAsync(NodeSpec node, CancellationToken ct)
    {
        var snapshot = node.Consumes.ToDictionary(n => n, n => Context.Latest(n)?.Version ?? 0, StringComparer.OrdinalIgnoreCase);
        var derivedFrom = snapshot.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}@v{kv.Value}").ToList();

        var missing = node.Consumes.Where(n => Context.Latest(n) is null).ToList();
        if (missing.Count > 0)
        {
            FailTerminal(node, $"entry gate: missing inputs {string.Join(", ", missing)}");
            return;
        }

        var entry = new GateContext(node, Context, Array.Empty<Artifact>());
        foreach (var gate in node.EntryGates)
        {
            var result = gate.Evaluate(entry);
            Audit("EntryGate", node.Id, $"{gate.Name}: {(result.Passed ? "pass" : "FAIL")} {result.Message}");
            if (!result.Passed)
            {
                _metrics.GateFailure();
                FailTerminal(node, $"entry gate '{gate.Name}' failed: {result.Message}");
                return;
            }
        }

        if (node.Approval == ApprovalMode.BeforeExecution)
        {
            var decision = await AskAsync(node, $"Approve execution of '{node.Id}'", node.Description, derivedFrom, ct);
            if (decision.Verdict != ApprovalVerdict.Approved)
            {
                FailTerminal(node, $"approval {decision.Verdict} by {decision.Approver}: {decision.Comment}");
                return;
            }
        }

        var feedback = new List<string>();
        var agentName = node.Agent;
        var maxAttempts = node.MaxRetries + 1;
        var attempt = 0;
        var usedFallback = false;

        while (true)
        {
            if (_stop.IsCancellationRequested)
            {
                HandleCancelled(node);
                return;
            }

            if (Interlocked.Increment(ref _totalAttempts) > _opts.MaxTotalAttempts)
            {
                RequestSafeStop("attempt budget exhausted");
                HandleCancelled(node);
                return;
            }

            attempt++;
            _metrics.AttemptStarted();
            var timer = Stopwatch.StartNew();
            var failure = "";
            var permanent = false;

            try
            {
                var agent = _agents.Get(agentName);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_opts.NodeTimeout);
                Audit("AttemptStarted", node.Id, $"agent={agentName} attempt={attempt}");

                var result = await agent.ExecuteAsync(new AgentRequest(node, Context, attempt, feedback.ToArray()), timeout.Token);
                var outputs = result.Artifacts.Select(a => Context.Preview(a, node.Id, derivedFrom)).ToList();
                EvaluateExitGates(node, outputs);

                if (node.Approval == ApprovalMode.BeforeCommit)
                {
                    var decision = await AskAsync(node, $"Approve outputs of '{node.Id}'", node.Description, outputs.Select(o => o.Ref).ToList(), ct);
                    if (decision.Verdict == ApprovalVerdict.Rejected)
                    {
                        throw new PermanentAgentException($"rejected by {decision.Approver}: {decision.Comment}");
                    }

                    if (decision.Verdict == ApprovalVerdict.ChangesRequested)
                    {
                        throw new GateFailure("human-review", $"changes requested by {decision.Approver}: {decision.Comment}", Severity.High);
                    }
                }

                Commit(node, result, snapshot, derivedFrom);
                _metrics.AttemptSucceeded(node.Id, timer.Elapsed);
                SetStatus(node.Id, NodeStatus.Succeeded);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                HandleCancelled(node);
                return;
            }
            catch (GateFailure gate)
            {
                _metrics.GateFailure();
                failure = $"[{gate.Gate}] {gate.Message}";
                permanent = gate.Severity == Severity.Critical;
            }
            catch (PermanentAgentException ex)
            {
                failure = ex.Message;
                permanent = true;
            }
            catch (Exception ex)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
            }

            _metrics.AttemptFailed(node.Id);
            feedback.Add(failure);
            Audit("AttemptFailed", node.Id, failure);

            if (permanent)
            {
                FailTerminal(node, failure);
                return;
            }

            if (attempt < maxAttempts)
            {
                _metrics.Retry();
                var delay = TimeSpan.FromMilliseconds(Math.Min(2000, _opts.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)));
                Audit("Retry", node.Id, $"retry {attempt}/{node.MaxRetries} after {delay.TotalMilliseconds:0}ms");
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    HandleCancelled(node);
                    return;
                }

                continue;
            }

            if (!usedFallback && node.FallbackAgent is not null)
            {
                usedFallback = true;
                agentName = node.FallbackAgent;
                attempt = 0;
                maxAttempts = 1;
                Audit("Fallback", node.Id, $"primary agent exhausted; engaging fallback '{agentName}'");
                Context.Record(node.Id, $"Fallback engaged: {agentName}", failure, derivedFrom);
                continue;
            }

            FailTerminal(node, $"retries exhausted: {failure}");
            return;
        }
    }

    private void EvaluateExitGates(NodeSpec node, IReadOnlyList<Artifact> outputs)
    {
        var missing = node.Produces.Where(p => outputs.All(o => !string.Equals(o.Name, p, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0)
        {
            throw new GateFailure("outputs-produced", $"missing declared outputs: {string.Join(", ", missing)}", Severity.High);
        }

        var undeclared = outputs.Where(o => !node.Produces.Contains(o.Name, StringComparer.OrdinalIgnoreCase)).Select(o => o.Name).ToList();
        if (undeclared.Count > 0)
        {
            throw new GateFailure("autonomy-boundary", $"agent wrote undeclared artifacts: {string.Join(", ", undeclared)}", Severity.High);
        }

        var violations = _policy.Evaluate(outputs);
        foreach (var v in violations)
        {
            _metrics.PolicyViolation();
            Audit("PolicyViolation", node.Id, $"[{v.Severity}] {v.Policy}/{v.Rule} in '{v.ArtifactName}': {v.Detail}");
        }

        var critical = violations.FirstOrDefault(v => v.Severity == Severity.Critical);
        if (critical is not null)
        {
            throw new GateFailure("policy", $"CRITICAL {critical.Policy}/{critical.Rule} in '{critical.ArtifactName}' - run must stop", Severity.Critical);
        }

        var high = violations.Where(v => v.Severity == Severity.High).ToList();
        if (high.Count > 0)
        {
            throw new GateFailure("policy", string.Join("; ", high.Select(v => $"{v.Policy}/{v.Rule} in '{v.ArtifactName}': {v.Detail}")), Severity.High);
        }

        var context = new GateContext(node, Context, outputs);
        foreach (var gate in node.ExitGates)
        {
            var result = gate.Evaluate(context);
            Audit("ExitGate", node.Id, $"{gate.Name}: {(result.Passed ? "pass" : "FAIL")} {result.Message}");
            if (!result.Passed)
            {
                throw new GateFailure(gate.Name, result.Message, result.Severity);
            }
        }
    }

    private void Commit(NodeSpec node, AgentResult result, Dictionary<string, int> snapshot, List<string> derivedFrom)
    {
        var published = result.Artifacts.Select(a => Context.Publish(a, node.Id, derivedFrom)).ToList();
        foreach (var decision in result.Decisions)
        {
            Context.Record(node.Id, decision.Summary, decision.Rationale, derivedFrom);
        }

        lock (_lock)
        {
            _states[node.Id].Consumed = snapshot;
            _completionOrder.Remove(node.Id);
            _completionOrder.Add(node.Id);
        }

        Audit("Committed", node.Id, string.Join(", ", published.Select(p => p.Artifact.Ref + (p.Changed ? "" : " (unchanged)"))));
    }

    private async Task<ApprovalDecision> AskAsync(NodeSpec node, string title, string summary, IReadOnlyList<string> artifacts, CancellationToken ct)
    {
        _metrics.Approval();
        SetStatus(node.Id, NodeStatus.AwaitingApproval);
        Audit("ApprovalRequested", node.Id, title);
        var decision = await _approvals.RequestAsync(new ApprovalRequest(RunId, node.Id, title, summary, artifacts, node.Impact), ct);
        Audit("ApprovalDecision", node.Id, $"{decision.Verdict} by {decision.Approver}: {decision.Comment}");
        Context.Record(node.Id, $"Human decision: {decision.Verdict} - {title}", $"{decision.Approver}: {decision.Comment}", artifacts);
        SetStatus(node.Id, NodeStatus.Running);
        return decision;
    }

    // ---------------------------------------------------------------- failure handling

    private void FailTerminal(NodeSpec node, string reason)
    {
        lock (_lock)
        {
            _anyFailure = true;
            if (node.Compensable)
            {
                _needsCompensation.Add(node.Id);
            }
        }

        SetStatus(node.Id, NodeStatus.Failed, reason);
        RequestSafeStop($"node '{node.Id}' failed: {reason}");
    }

    private void HandleCancelled(NodeSpec node)
    {
        lock (_lock)
        {
            if (node.Compensable)
            {
                _needsCompensation.Add(node.Id);
            }
        }

        SetStatus(node.Id, NodeStatus.Skipped, "cancelled by safe-stop");
    }

    /// <summary>Undo side effects, most recent first. Failed/cancelled compensable nodes go first, then completed ones in reverse order.</summary>
    private async Task RollbackAsync()
    {
        List<string> targets;
        lock (_lock)
        {
            targets = _needsCompensation.ToList();
            targets.AddRange(Enumerable.Reverse(_completionOrder)
                .Where(id => _graph.Nodes[id].Compensable && _states[id].Status == NodeStatus.Succeeded && !targets.Contains(id)));
        }

        foreach (var id in targets)
        {
            var wasSucceeded = StatusOf(id) == NodeStatus.Succeeded;
            if (await CompensateAsync(_graph.Nodes[id], "run stopped") && wasSucceeded)
            {
                SetStatus(id, NodeStatus.RolledBack, "compensated after run stopped");
            }
        }
    }

    private async Task<bool> CompensateAsync(NodeSpec node, string why)
    {
        try
        {
            await _agents.Get(node.Agent).CompensateAsync(node, Context, CancellationToken.None);
            _metrics.Rollback();
            Audit("Rollback", node.Id, why);
            return true;
        }
        catch (Exception ex)
        {
            Audit("RollbackFailed", node.Id, $"{ex.Message} - manual intervention required");
            return false;
        }
    }

    // ---------------------------------------------------------------- helpers

    private void SetStatus(string id, NodeStatus status, string? reason = null)
    {
        lock (_lock)
        {
            _states[id].Status = status;
        }

        Audit("NodeStatus", id, status.ToString(), reason is null ? null : new Dictionary<string, string> { ["reason"] = reason });
        try
        {
            NodeStatusChanged?.Invoke(id, status);
        }
        catch
        {
            // observers must never break orchestration
        }
    }

    private void Audit(string type, string? nodeId, string message, IReadOnlyDictionary<string, string>? data = null) =>
        _audit.Append(RunId, type, nodeId, message, data);
}
