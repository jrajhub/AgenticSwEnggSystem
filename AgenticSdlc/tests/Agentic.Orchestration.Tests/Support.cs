using Agentic.Orchestration;

namespace Agentic.Orchestration.Tests;

public sealed class DelegateAgent(string name, Func<AgentRequest, Task<AgentResult>> execute, Func<NodeSpec, Task>? compensate = null) : IAgent
{
    public int Calls;
    public int Compensations;

    public string Name => name;

    public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return execute(request);
    }

    public async Task CompensateAsync(NodeSpec node, WorkflowContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref Compensations);
        if (compensate is not null)
        {
            await compensate(node);
        }
    }
}

public static class T
{
    public static NodeSpec Node(string id, string agent, string[]? dependsOn = null, string[]? consumes = null, string[]? produces = null,
        int retries = 2, string? fallback = null, bool compensable = false, Impact impact = Impact.Low,
        ApprovalMode approval = ApprovalMode.None, IGate[]? exitGates = null) => new()
    {
        Id = id, Agent = agent, DependsOn = dependsOn ?? Array.Empty<string>(), Consumes = consumes ?? Array.Empty<string>(),
        Produces = produces ?? Array.Empty<string>(), MaxRetries = retries, FallbackAgent = fallback, Compensable = compensable,
        Impact = impact, Approval = approval, ExitGates = exitGates ?? Array.Empty<IGate>(),
    };

    public static Task<AgentResult> Out(string name, string content, string kind = "document") =>
        Task.FromResult(AgentResult.Of(new ArtifactDraft(name, kind, content)));

    public static WorkflowEngine Engine(WorkflowGraph graph, AgentRegistry agents, IApprovalProvider? approvals = null,
        PolicyGuard? policy = null, EngineOptions? options = null) =>
        new(graph, agents, approvals ?? ScriptedApprovalProvider.ApproveAll(), policy ?? PolicyGuard.Default(), new AuditLog(),
            options ?? new EngineOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1) });
}
