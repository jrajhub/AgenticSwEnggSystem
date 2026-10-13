using System.Security.Cryptography;
using System.Text;

namespace Agentic.Orchestration;

public enum NodeStatus { Pending, Running, AwaitingApproval, Succeeded, Failed, RolledBack, Blocked, Skipped }
public enum RunStatus { NotStarted, Running, Completed, Failed, SafeStopped }
public enum Impact { Low, High }
public enum Severity { Info, Warning, High, Critical }
public enum ApprovalMode { None, BeforeExecution, BeforeCommit }

/// <summary>Immutable, versioned output of a node. DerivedFrom records the exact input versions (decision lineage).</summary>
public sealed record Artifact(
    string Name, string Kind, string Content, int Version, string ProducedBy,
    IReadOnlyList<string> DerivedFrom, string Sha256, DateTimeOffset At)
{
    public string Ref => $"{Name}@v{Version}";

    public static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

public sealed record ArtifactDraft(string Name, string Kind, string Content);
public sealed record DecisionDraft(string Summary, string Rationale);
public sealed record Decision(string Id, string NodeId, string Summary, string Rationale, IReadOnlyList<string> Inputs, DateTimeOffset At);

public sealed record AgentResult(IReadOnlyList<ArtifactDraft> Artifacts, IReadOnlyList<DecisionDraft> Decisions)
{
    public static AgentResult Of(params ArtifactDraft[] artifacts) => new(artifacts, Array.Empty<DecisionDraft>());

    public AgentResult Decide(string summary, string rationale) =>
        this with { Decisions = Decisions.Append(new DecisionDraft(summary, rationale)).ToList() };
}

public sealed record AgentRequest(NodeSpec Node, WorkflowContext Context, int Attempt, IReadOnlyList<string> Feedback);

/// <summary>Retryable failure (timeouts, flaky tools, LLM errors).</summary>
public sealed class TransientAgentException(string message) : Exception(message);

/// <summary>Non-retryable failure: stop and escalate to a human.</summary>
public sealed class PermanentAgentException(string message) : Exception(message);

public sealed class InvalidWorkflowException(string message) : Exception(message);

public interface IAgent
{
    string Name { get; }

    Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct);

    /// <summary>Undo side effects of a previous execution. Must be idempotent.</summary>
    Task CompensateAsync(NodeSpec node, WorkflowContext context, CancellationToken ct) => Task.CompletedTask;
}

public sealed class AgentRegistry
{
    private readonly Dictionary<string, IAgent> _agents = new(StringComparer.Ordinal);

    public AgentRegistry Register(IAgent agent)
    {
        _agents[agent.Name] = agent;
        return this;
    }

    public bool Contains(string name) => _agents.ContainsKey(name);

    public IAgent Get(string name) =>
        _agents.TryGetValue(name, out var agent) ? agent : throw new InvalidWorkflowException($"agent '{name}' is not registered");
}

/// <summary>One stage in the SDLC graph, with its contract (inputs/outputs), gates, approval mode and failure policy.</summary>
public sealed class NodeSpec
{
    public required string Id { get; init; }
    public required string Agent { get; init; }
    public string Description { get; init; } = "";
    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Consumes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Produces { get; init; } = Array.Empty<string>();
    public IReadOnlyList<IGate> EntryGates { get; init; } = Array.Empty<IGate>();
    public IReadOnlyList<IGate> ExitGates { get; init; } = Array.Empty<IGate>();
    public Impact Impact { get; init; } = Impact.Low;
    public ApprovalMode Approval { get; init; } = ApprovalMode.None;
    public int MaxRetries { get; init; } = 2;
    public string? FallbackAgent { get; init; }

    /// <summary>True when execution has side effects that CompensateAsync can undo.</summary>
    public bool Compensable { get; init; }
}
