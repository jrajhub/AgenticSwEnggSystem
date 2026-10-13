namespace Agentic.Orchestration;

public enum ApprovalVerdict { Approved, Rejected, ChangesRequested }

public sealed record ApprovalRequest(
    string RunId, string NodeId, string Title, string Summary, IReadOnlyList<string> Artifacts, Impact Impact);

public sealed record ApprovalDecision(ApprovalVerdict Verdict, string Approver, string Comment);

public interface IApprovalProvider
{
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct);
}

/// <summary>Programmable approver for tests and demos. Every decision is still audited by the engine.</summary>
public sealed class ScriptedApprovalProvider(Func<ApprovalRequest, ApprovalDecision> decide) : IApprovalProvider
{
    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct) =>
        Task.FromResult(decide(request));

    public static ScriptedApprovalProvider ApproveAll(string approver = "scripted-approver") =>
        new(_ => new ApprovalDecision(ApprovalVerdict.Approved, approver, "auto-approved by script"));
}

/// <summary>Interactive human checkpoint. Requests are serialized so parallel nodes do not interleave prompts.</summary>
public sealed class ConsoleApprovalProvider : IApprovalProvider
{
    private readonly SemaphoreSlim _one = new(1, 1);

    public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            Console.WriteLine();
            Console.WriteLine($"=== APPROVAL REQUIRED [{request.Impact} impact] node '{request.NodeId}' ===");
            Console.WriteLine(request.Title);
            Console.WriteLine(request.Summary);
            Console.WriteLine("Artifacts: " + string.Join(", ", request.Artifacts));
            Console.Write("[a]pprove / [r]eject / [c]hanges requested? ");
            var key = (Console.ReadLine() ?? "r").Trim().ToLowerInvariant();
            Console.Write("Comment: ");
            var comment = Console.ReadLine() ?? "";
            var who = Environment.UserName;
            return key switch
            {
                "a" or "approve" => new ApprovalDecision(ApprovalVerdict.Approved, who, comment),
                "c" or "changes" => new ApprovalDecision(ApprovalVerdict.ChangesRequested, who, comment),
                _ => new ApprovalDecision(ApprovalVerdict.Rejected, who, comment),
            };
        }
        finally
        {
            _one.Release();
        }
    }
}
