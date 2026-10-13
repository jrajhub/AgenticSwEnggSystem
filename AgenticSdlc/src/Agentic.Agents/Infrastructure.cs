using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentic.Orchestration;

namespace Agentic.Agents;

// ------------------------------------------------------------------ LLM

public interface ILlmClient
{
    /// <summary>Returns generated text, or null when no model is configured (agents then use their deterministic playbook).</summary>
    Task<string?> TryCompleteAsync(string system, string prompt, CancellationToken ct);
}

public sealed class OfflineLlmClient : ILlmClient
{
    public Task<string?> TryCompleteAsync(string system, string prompt, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>Minimal Anthropic Messages API client. Enabled when ANTHROPIC_API_KEY is set. Output still passes every engine gate.</summary>
public sealed class AnthropicLlmClient(HttpClient http, string apiKey, string model) : ILlmClient
{
    public async Task<string?> TryCompleteAsync(string system, string prompt, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(new
        {
            model,
            max_tokens = 4000,
            system,
            messages = new[] { new { role = "user", content = prompt } },
        });

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new TransientAgentException($"LLM call failed with HTTP {(int)response.StatusCode}");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var text = new StringBuilder();
        foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
        {
            if (block.TryGetProperty("text", out var t))
            {
                text.Append(t.GetString());
            }
        }

        return text.ToString();
    }
}

// ------------------------------------------------------------------ command execution (tool guardrail)

public sealed record CommandResult(int ExitCode, string Output);

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string exe, IReadOnlyList<string> args, string workDir, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Agents may only run 'dotnet build' / 'dotnet test'. Everything else is refused (least privilege). No shell is involved.</summary>
public sealed class AllowListedCommandRunner : ICommandRunner
{
    private static readonly HashSet<string> AllowedVerbs = new(StringComparer.Ordinal) { "build", "test" };

    public async Task<CommandResult> RunAsync(string exe, IReadOnlyList<string> args, string workDir, TimeSpan timeout, CancellationToken ct)
    {
        if (exe != "dotnet" || args.Count == 0 || !AllowedVerbs.Contains(args[0]))
        {
            throw new PermanentAgentException($"command blocked by allow-list: {exe} {string.Join(' ', args)}");
        }

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";

        using var process = Process.Start(psi) ?? throw new TransientAgentException("could not start dotnet");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        return new CommandResult(process.ExitCode, await stdout + await stderr);
    }
}

/// <summary>Deterministic stand-in for machines without the SDK/NuGet access. The test report labels results as simulated.</summary>
public sealed class SimulatedCommandRunner : ICommandRunner
{
    public Task<CommandResult> RunAsync(string exe, IReadOnlyList<string> args, string workDir, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult(new CommandResult(0, "[SIMULATED] Passed!  - Failed:     0, Passed:    24, Skipped:     0, Total:    24"));
}

// ------------------------------------------------------------------ human clarification

public interface IClarificationProvider
{
    Task<IReadOnlyDictionary<string, string>> AnswerAsync(IReadOnlyList<string> questions, CancellationToken ct);
}

/// <summary>Stakeholder stand-in: answers a question when it contains a configured keyword, otherwise accepts the stated assumption.</summary>
public sealed class ScriptedClarificationProvider(IReadOnlyDictionary<string, string> answersByKeyword) : IClarificationProvider
{
    public Task<IReadOnlyDictionary<string, string>> AnswerAsync(IReadOnlyList<string> questions, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        foreach (var q in questions)
        {
            var match = answersByKeyword.FirstOrDefault(kv => q.Contains(kv.Key, StringComparison.OrdinalIgnoreCase));
            result[q] = match.Value ?? "Stated assumption accepted.";
        }

        return Task.FromResult<IReadOnlyDictionary<string, string>>(result);
    }
}

// ------------------------------------------------------------------ workspace

public sealed class Workspace(string repoRoot, string sandboxDir, string releaseDir)
{
    public string RepoRoot => repoRoot;
    public string SandboxDir => sandboxDir;
    public string ReleaseDir => releaseDir;
    public string BlueprintSrc => Path.Combine(repoRoot, "src", "UrlShortener.Api");
    public string BlueprintTests => Path.Combine(repoRoot, "tests", "UrlShortener.Tests");
    public string SandboxSrc => Path.Combine(sandboxDir, "src", "UrlShortener.Api");
    public string SandboxTests => Path.Combine(sandboxDir, "tests", "UrlShortener.Tests");

    public static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            throw new PermanentAgentException($"source directory not found: {source}");
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(source, file))
            {
                continue;
            }

            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    public static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>Concatenates source files under a directory with '// FILE:' headers so policies can scan them as one artifact.</summary>
    public string Bundle(string directory)
    {
        var sb = new StringBuilder();
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(directory, f) && Path.GetExtension(f) is ".cs" or ".csproj" or ".json")
            .OrderBy(f => f, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(sandboxDir, file).Replace('\\', '/');
            sb.Append("// FILE: ").AppendLine(relative);
            sb.AppendLine(File.ReadAllText(file).Replace("\r\n", "\n"));
        }

        return sb.ToString();
    }

    private static bool IsBuildOutput(string root, string path) =>
        Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "bin" or "obj");
}

// ------------------------------------------------------------------ playbook + chaos

public sealed record PatchOp(string File, string Find, string Replace);
public sealed record RiskItem(string Risk, string Likelihood, string Impact, string Mitigation);
public sealed record DesignDecision(string Summary, string Rationale);

/// <summary>Scenario knowledge used by the deterministic (offline) agents. With an LLM configured, agents generate instead and fall back to this.</summary>
public sealed class Playbook
{
    public required string Title { get; init; }
    public bool Brownfield { get; init; }
    public IReadOnlyList<(string Heading, string Body)> DesignSections { get; init; } = Array.Empty<(string, string)>();
    public IReadOnlyList<DesignDecision> Decisions { get; init; } = Array.Empty<DesignDecision>();
    public IReadOnlyList<RiskItem> Risks { get; init; } = Array.Empty<RiskItem>();
    public IReadOnlyList<string> Assumptions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ImpactKeywords { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PatchOp> Patches { get; init; } = Array.Empty<PatchOp>();

    /// <summary>Extra files (relative to the sandbox root) added by the test-author agent.</summary>
    public IReadOnlyDictionary<string, string> ExtraTestFiles { get; init; } = new Dictionary<string, string>();

    public bool WithTtl { get; init; }
}

/// <summary>Fault injection for demonstrating retries, policy blocks, safe-stop and rollback.</summary>
public sealed class ChaosProfile
{
    public Dictionary<string, int> TransientFailures { get; } = new();
    public bool InsecureCodeOnFirstAttempt { get; init; }
    public bool HardcodedSecret { get; init; }
    public bool PartialReleaseFailure { get; init; }
    public bool RejectRelease { get; init; }

    public static ChaosProfile From(string mode)
    {
        var profile = mode switch
        {
            "policy" => new ChaosProfile { InsecureCodeOnFirstAttempt = true },
            "secret" => new ChaosProfile { HardcodedSecret = true },
            "rollback" => new ChaosProfile { PartialReleaseFailure = true },
            "reject" => new ChaosProfile { RejectRelease = true },
            _ => new ChaosProfile(),
        };

        if (mode == "transient")
        {
            profile.TransientFailures["implement"] = 2;
            profile.TransientFailures["run-tests"] = 1;
        }

        return profile;
    }
}

public sealed record AgentDeps(
    Playbook Playbook, Workspace Workspace, ICommandRunner Runner, ILlmClient Llm, ChaosProfile Chaos, IClarificationProvider Clarifier);

public abstract class AgentBase(string name, AgentDeps deps) : IAgent
{
    protected AgentDeps Deps => deps;

    public string Name => name;

    public abstract Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct);

    public virtual Task CompensateAsync(NodeSpec node, WorkflowContext context, CancellationToken ct) => Task.CompletedTask;

    protected void MaybeFailTransiently(AgentRequest request)
    {
        if (deps.Chaos.TransientFailures.TryGetValue(request.Node.Id, out var failures) && request.Attempt <= failures)
        {
            throw new TransientAgentException($"chaos: simulated transient failure in '{request.Node.Id}' (attempt {request.Attempt})");
        }
    }

    /// <summary>Uses the LLM when configured, otherwise the deterministic playbook output.</summary>
    protected async Task<string> GenerateAsync(string system, string prompt, Func<string> offline, CancellationToken ct)
    {
        var text = await deps.Llm.TryCompleteAsync(system, prompt, ct);
        return string.IsNullOrWhiteSpace(text) ? offline() : text;
    }

    protected static string FeedbackText(AgentRequest request) =>
        request.Feedback.Count == 0 ? "" : "\n\nYour previous attempt was rejected by governance gates; fix these issues:\n- " + string.Join("\n- ", request.Feedback);

    protected static string Section(string markdown, string heading)
    {
        var lines = markdown.Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimEnd().Equals("## " + heading, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            return "";
        }

        var body = lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("## ", StringComparison.Ordinal));
        return string.Join("\n", body).Trim();
    }
}
