using System.Text;
using System.Text.RegularExpressions;
using Agentic.Orchestration;

namespace Agentic.Agents;

/// <summary>
/// Implementation. Works only inside a sandbox copy of the codebase (never the real repo).
/// Greenfield: materializes the reference blueprint. Brownfield: applies exact-match patch operations and aborts on drift.
/// </summary>
public sealed class ImplementationAgent(AgentDeps deps) : AgentBase("implementation-agent", deps)
{
    public override Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        MaybeFailTransiently(request);
        var ws = deps.Workspace;
        Workspace.DeleteDirectory(ws.SandboxSrc);
        Workspace.CopyDirectory(ws.BlueprintSrc, ws.SandboxSrc);

        var summary = new StringBuilder("# Change summary\n\n");
        if (deps.Playbook.Brownfield)
        {
            foreach (var patch in deps.Playbook.Patches)
            {
                ApplyPatch(ws.SandboxDir, patch);
                summary.AppendLine($"- `{patch.File}`: replaced `{Shorten(patch.Find)}` with `{Shorten(patch.Replace)}`");
            }
        }
        else
        {
            summary.AppendLine("- Greenfield: created `src/UrlShortener.Api` from the reference blueprint.");
        }

        // Fault injection for governance demos: attempt 1 only, never after feedback.
        if (deps.Chaos.InsecureCodeOnFirstAttempt && request.Attempt == 1)
        {
            File.WriteAllText(Path.Combine(ws.SandboxSrc, "WeakHash.cs"),
                "using System.Security.Cryptography;\nnamespace UrlShortener.Api;\npublic static class WeakHash { public static byte[] Compute(byte[] d) => MD5.HashData(d); }\n");
        }

        if (deps.Chaos.HardcodedSecret)
        {
            File.WriteAllText(Path.Combine(ws.SandboxSrc, "Secrets.cs"),
                "namespace UrlShortener.Api;\npublic static class Secrets { public const string CloudKey = \"AKIAABCDEFGHIJKLMNOP\"; }\n");
        }

        var bundle = ws.Bundle(ws.SandboxSrc);
        return Task.FromResult(AgentResult.Of(
                new ArtifactDraft("source-bundle", "code", bundle),
                new ArtifactDraft("change-summary", "document", summary.ToString()))
            .Decide(deps.Playbook.Brownfield ? $"Applied {deps.Playbook.Patches.Count} patch operations in sandbox" : "Materialized implementation in sandbox",
                "Work happens in an isolated sandbox; promotion to a release folder is a separate, approved step."));
    }

    public override Task CompensateAsync(NodeSpec node, WorkflowContext context, CancellationToken ct)
    {
        Workspace.DeleteDirectory(deps.Workspace.SandboxSrc);
        return Task.CompletedTask;
    }

    private static void ApplyPatch(string sandboxRoot, PatchOp patch)
    {
        var path = Path.Combine(sandboxRoot, patch.File);
        if (!File.Exists(path))
        {
            throw new PermanentAgentException($"patch target missing (codebase drift?): {patch.File}");
        }

        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var first = text.IndexOf(patch.Find, StringComparison.Ordinal);
        if (first < 0 || text.IndexOf(patch.Find, first + 1, StringComparison.Ordinal) >= 0)
        {
            throw new PermanentAgentException($"patch anchor must match exactly once in {patch.File} (codebase drift?): '{Shorten(patch.Find)}'");
        }

        File.WriteAllText(path, text.Remove(first, patch.Find.Length).Insert(first, patch.Replace));
    }

    private static string Shorten(string s) => (s.Length > 60 ? s[..57] + "..." : s).Replace("\n", " ");
}

/// <summary>Test authoring: existing regression suite plus scenario-specific tests, in the sandbox.</summary>
public sealed class TestAuthorAgent(AgentDeps deps) : AgentBase("test-author-agent", deps)
{
    public override Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        MaybeFailTransiently(request);
        var ws = deps.Workspace;
        Workspace.DeleteDirectory(ws.SandboxTests);
        Workspace.CopyDirectory(ws.BlueprintTests, ws.SandboxTests);
        foreach (var (relative, content) in deps.Playbook.ExtraTestFiles)
        {
            var target = Path.Combine(ws.SandboxDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }

        return Task.FromResult(AgentResult.Of(new ArtifactDraft("test-bundle", "test", ws.Bundle(ws.SandboxTests)))
            .Decide($"Prepared test suite with {deps.Playbook.ExtraTestFiles.Count} scenario-specific file(s)",
                "Regression suite is always included so enhancements cannot silently break existing behavior."));
    }

    public override Task CompensateAsync(NodeSpec node, WorkflowContext context, CancellationToken ct)
    {
        Workspace.DeleteDirectory(deps.Workspace.SandboxTests);
        return Task.CompletedTask;
    }
}

/// <summary>Validation: runs the real build/test toolchain through the allow-listed runner and reports honestly (including simulated runs).</summary>
public sealed class TestRunnerAgent(AgentDeps deps) : AgentBase("test-runner-agent", deps)
{
    public override async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        MaybeFailTransiently(request);
        var result = await deps.Runner.RunAsync(
            "dotnet", new[] { "test", Path.Combine("tests", "UrlShortener.Tests"), "--nologo", "-v", "m" },
            deps.Workspace.SandboxDir, TimeSpan.FromMinutes(8), ct);

        var match = Regex.Match(result.Output, @"Failed:\s*(\d+),\s*Passed:\s*(\d+)");
        var failed = match.Success ? int.Parse(match.Groups[1].Value) : 1;
        var passed = match.Success ? int.Parse(match.Groups[2].Value) : 0;
        var simulated = result.Output.Contains("[SIMULATED]", StringComparison.Ordinal);
        var tail = string.Join("\n", result.Output.Replace("\r", "").Split('\n').TakeLast(25));

        var report = $"# Test report\n\n- Runner: {(simulated ? "SIMULATED (no real tests were executed)" : "dotnet test")}\n- Exit code: {result.ExitCode}\n- Failed: {failed}\n- Passed: {passed}\n\n```\n{tail}\n```\n";
        return AgentResult.Of(new ArtifactDraft("test-report", "document", report))
            .Decide(failed == 0 ? $"All {passed} tests passed" : $"{failed} failing or unparseable result",
                simulated ? "Simulated runner used; results are NOT evidence of correctness." : "Executed in the sandbox through the allow-listed command runner.");
    }
}

/// <summary>Static security/quality review of the generated source. Findings are explicit PASS/FAIL lines consumed by a gate.</summary>
public sealed class ReviewAgent(AgentDeps deps) : AgentBase("review-agent", deps)
{
    private static readonly (string Check, string Marker)[] Checks =
    {
        ("Target URL is parsed and scheme allow-listed (http/https)", "UriSchemeHttps"),
        ("URL validation uses Uri.TryCreate (no ad-hoc parsing)", "Uri.TryCreate"),
        ("Private/loopback hosts are blocked (SSRF / abuse)", "IPAddress"),
        ("Create endpoint is rate limited", "RequireRateLimiting"),
        ("Errors are returned as problem details, not stack traces", "AddProblemDetails"),
        ("Health endpoint exposed", "MapHealthChecks"),
        ("Short codes come from a cryptographic RNG", "RandomNumberGenerator"),
    };

    public override Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        MaybeFailTransiently(request);
        var source = request.Context.Require("source-bundle").Content;
        var sb = new StringBuilder("# Security and quality review\n\n");
        foreach (var (check, marker) in Checks)
        {
            sb.AppendLine($"- [{(source.Contains(marker, StringComparison.Ordinal) ? "PASS" : "FAIL")}] {check}");
        }

        if (deps.Playbook.WithTtl)
        {
            var ok = source.Contains("ExpiresAt", StringComparison.Ordinal) && source.Contains("ResolveStatus.Gone", StringComparison.Ordinal);
            sb.AppendLine($"- [{(ok ? "PASS" : "FAIL")}] Expiry is enforced on redirect and returns 410");
            var order = source.IndexOf("ResolveStatus.Gone", StringComparison.Ordinal) < source.IndexOf("RegisterClick(clock", StringComparison.Ordinal);
            sb.AppendLine($"- [{(order ? "PASS" : "FAIL")}] Expiry is checked before the click is counted");
        }

        return Task.FromResult(AgentResult.Of(new ArtifactDraft("security-review", "document", sb.ToString()))
            .Decide("Completed static review", "Checks are marker-based heuristics; they complement, not replace, tests and human review."));
    }
}

public sealed class RiskAgent(string name, AgentDeps deps) : AgentBase(name, deps)
{
    public override async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        MaybeFailTransiently(request);
        var design = request.Context.Require("design-doc").Content;
        string Offline()
        {
            var sb = new StringBuilder("# Risk register\n\n| Risk | Likelihood | Impact | Mitigation |\n|---|---|---|---|\n");
            foreach (var r in deps.Playbook.Risks)
            {
                sb.AppendLine($"| {r.Risk} | {r.Likelihood} | {r.Impact} | {r.Mitigation} |");
            }

            return sb.ToString();
        }

        var text = await GenerateAsync(
            "You are a security and reliability reviewer. Produce a Markdown risk register table: Risk | Likelihood | Impact | Mitigation.",
            $"Design document:\n{design}{FeedbackText(request)}", Offline, ct);
        return AgentResult.Of(new ArtifactDraft("risk-register", "document", text))
            .Decide($"Recorded {deps.Playbook.Risks.Count} risks", "Risks are derived from design failure modes and trade-offs.");
    }
}

public sealed class TestPlanAgent(AgentDeps deps) : AgentBase("test-plan-agent", deps)
{
    public override Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        MaybeFailTransiently(request);
        var requirements = request.Context.Require("requirements").Content;
        var sb = new StringBuilder("# Test plan\n\n## Functional cases\n");
        var n = 1;
        foreach (var line in requirements.Split('\n').Where(l => l.StartsWith("- FR-") || l.StartsWith("- NFR-")))
        {
            sb.AppendLine($"- TC-{n++}: verify \"{line[2..]}\" (given a valid request, when exercised, then the stated behavior holds)");
        }

        sb.AppendLine();
        sb.AppendLine("## Non-functional cases");
        sb.AppendLine("- Load: sustained target RPS with p95 latency check; soak test for memory growth.");
        sb.AppendLine("- Security: SSRF host matrix, oversized URL, credential-bearing URL, alias abuse.");
        sb.AppendLine("- Resilience: store failure, code-collision exhaustion, rate-limit rejection.");
        return Task.FromResult(AgentResult.Of(new ArtifactDraft("test-plan", "document", sb.ToString()))
            .Decide($"Derived {n - 1} functional test cases", "One verification case per requirement item plus standard NFR suites."));
    }
}

/// <summary>Documentation. Engine policy requires 'Data classification' and 'Retention' sections (compliance guardrail).</summary>
public sealed class DocsAgent(string name, AgentDeps deps) : AgentBase(name, deps)
{
    public override async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        MaybeFailTransiently(request);
        var design = request.Context.Require("design-doc").Content;
        var risks = request.Context.Latest("risk-register")?.Content ?? "";
        string Offline()
        {
            var sb = new StringBuilder($"# {deps.Playbook.Title} - User and operations guide\n\n");
            sb.AppendLine("## Overview");
            sb.AppendLine(Section(design, "Architecture").Split('\n').FirstOrDefault() ?? "See design document.");
            sb.AppendLine();
            sb.AppendLine("## Running");
            sb.AppendLine("`dotnet run --project src/UrlShortener.Api` then `POST /api/v1/links {\"url\":\"https://example.com\"}`.");
            sb.AppendLine();
            sb.AppendLine("## API");
            sb.AppendLine("See the `openapi` artifact. Errors use RFC 7807 problem details.");
            sb.AppendLine();
            sb.AppendLine("## Data classification");
            sb.AppendLine("Target URLs: Internal. Click counters and last-access timestamps: Internal. No IP address, user agent or other personal data is stored.");
            sb.AppendLine();
            sb.AppendLine("## Retention");
            sb.AppendLine("Links persist until deleted or expired. Analytics counters share the link's lifetime. The reference store is in-memory and non-durable.");
            sb.AppendLine();
            sb.AppendLine("## Operations");
            sb.AppendLine("Health: `GET /healthz`. Create is limited to 30 requests/minute per client IP. Alert on 5xx rate and 429 spikes.");
            if (risks.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Known risks");
                sb.AppendLine(risks);
            }

            return sb.ToString();
        }

        var text = await GenerateAsync(
            "You write operations-ready documentation in Markdown. Include '## Data classification' and '## Retention' sections.",
            $"Design:\n{design}\n\nRisks:\n{risks}{FeedbackText(request)}", Offline, ct);
        return AgentResult.Of(new ArtifactDraft("user-docs", "document", text))
            .Decide("Authored user and operations documentation", "Compliance sections are mandatory and enforced by an engine policy.");
    }
}

/// <summary>High-impact action: promotes sandbox outputs into a release folder. Side effects are compensable (folder removal).</summary>
public sealed class ReleaseAgent(AgentDeps deps) : AgentBase("release-agent", deps)
{
    public override Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        MaybeFailTransiently(request);
        var ws = deps.Workspace;
        var ctx = request.Context;
        var source = ctx.Require("source-bundle");
        var tests = ctx.Require("test-report");

        Workspace.DeleteDirectory(ws.ReleaseDir);
        Workspace.CopyDirectory(ws.SandboxSrc, Path.Combine(ws.ReleaseDir, "src", "UrlShortener.Api"));
        Workspace.CopyDirectory(ws.SandboxTests, Path.Combine(ws.ReleaseDir, "tests", "UrlShortener.Tests"));
        File.WriteAllText(Path.Combine(ws.ReleaseDir, "README.md"), ctx.Require("user-docs").Content);

        if (deps.Chaos.PartialReleaseFailure)
        {
            throw new TransientAgentException("chaos: publish step failed after partial write");
        }

        var notes = new StringBuilder("# Release notes\n\n");
        notes.AppendLine($"- Source: {source.Ref} (sha256 {source.Sha256[..12]})");
        notes.AppendLine($"- Tests: {tests.Ref}");
        notes.AppendLine($"- Review: {ctx.Require("security-review").Ref}");
        notes.AppendLine($"- Docs: {ctx.Require("user-docs").Ref}");
        notes.AppendLine($"- Release folder: {ws.ReleaseDir}");
        File.WriteAllText(Path.Combine(ws.ReleaseDir, "RELEASE_NOTES.md"), notes.ToString());

        return Task.FromResult(AgentResult.Of(new ArtifactDraft("release-notes", "document", notes.ToString()))
            .Decide("Promoted verified artifacts to the release folder", "Promotion ran only after human approval and passing readiness gates."));
    }

    public override Task CompensateAsync(NodeSpec node, WorkflowContext context, CancellationToken ct)
    {
        Workspace.DeleteDirectory(deps.Workspace.ReleaseDir);
        return Task.CompletedTask;
    }
}

/// <summary>Definition-of-ready check for the ambiguous scenario: no code, just a verifiable hand-off decision.</summary>
public sealed class ReadinessAgent(AgentDeps deps) : AgentBase("readiness-agent", deps)
{
    public override Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        MaybeFailTransiently(request);
        var ctx = request.Context;
        var questions = ctx.Require("open-questions").Content;
        var answers = ctx.Require("clarifications").Content;
        var total = questions.Trim() == "None" ? 0 : questions.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        var answered = Regex.Matches(answers, @"Answer:\s*\S").Count;
        var unresolved = Math.Max(0, total - answered);

        var sb = new StringBuilder("# Build readiness report\n\n");
        sb.AppendLine($"- Open questions: {total}");
        sb.AppendLine($"- Answered: {answered}");
        sb.AppendLine($"- Unresolved: {unresolved}");
        sb.AppendLine($"- Requirements: {ctx.Require("requirements").Ref}");
        sb.AppendLine($"- Design: {ctx.Require("design-doc").Ref}");
        sb.AppendLine($"- Test plan: {ctx.Require("test-plan").Ref}");
        sb.AppendLine($"- Risk register: {ctx.Require("risk-register").Ref}");
        sb.AppendLine();
        sb.AppendLine(unresolved == 0 ? "Verdict: READY to start implementation." : "Verdict: NOT READY.");
        return Task.FromResult(AgentResult.Of(new ArtifactDraft("readiness-report", "document", sb.ToString()))
            .Decide(unresolved == 0 ? "Requirement is build-ready" : "Requirement is not build-ready", "All open questions must have stakeholder answers before build starts."));
    }
}

/// <summary>Final engineering summary: plan/rationale, artifacts, risks/trade-offs/validation, assumptions, limitations.</summary>
public sealed class SummaryAgent(AgentDeps deps) : AgentBase("summary-agent", deps)
{
    public override Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        var ctx = request.Context;
        var sb = new StringBuilder($"# Final engineering summary - {deps.Playbook.Title}\n\n");

        sb.AppendLine("## Plan and rationale");
        sb.AppendLine(ctx.Latest("task-plan")?.Content.Replace("# Task plan", "").Trim() ?? "(no task plan)");
        sb.AppendLine();
        sb.AppendLine("Key decisions (lineage-recorded):");
        foreach (var d in ctx.Decisions.Where(d => d.NodeId != "summarize"))
        {
            sb.AppendLine($"- {d.Id} [{d.NodeId}] {d.Summary} - {d.Rationale}");
        }

        sb.AppendLine();
        sb.AppendLine("## Artifacts");
        foreach (var a in ctx.LatestArtifacts().Where(a => a.Name != "engineering-summary"))
        {
            sb.AppendLine($"- {a.Ref} ({a.Kind}) produced by `{a.ProducedBy}`, sha256 {a.Sha256[..10]}");
        }

        sb.AppendLine();
        sb.AppendLine("## Risks, trade-offs and validation");
        sb.AppendLine(ctx.Latest("risk-register")?.Content.Replace("# Risk register", "").Trim() ?? "(no risk register)");
        foreach (var name in new[] { "test-report", "security-review", "readiness-report" })
        {
            if (ctx.Latest(name) is { } artifact)
            {
                sb.AppendLine();
                sb.AppendLine($"### {name}");
                sb.AppendLine(artifact.Content.Split('\n').Where(l => l.StartsWith("- ") || l.StartsWith("Verdict")).Aggregate("", (acc, l) => acc + l + "\n"));
            }
        }

        sb.AppendLine("## Assumptions");
        sb.AppendLine(Section(ctx.Latest("requirements")?.Content ?? "", "Assumptions"));
        sb.AppendLine();
        sb.AppendLine("## Limitations");
        sb.AppendLine("- Reference store is in-memory (non-durable, single node); production needs Redis/SQL behind ILinkStore.");
        sb.AppendLine("- Offline agents replay a deterministic playbook; semantic quality of generated content depends on the configured LLM when enabled.");
        sb.AppendLine("- Security review is heuristic (marker-based) and does not replace SAST/DAST or a human review.");
        sb.AppendLine("- Rollback compensates sandbox/release folders only; it does not model external systems.");
        sb.AppendLine("- Approvals are scripted unless --interactive is used.");

        return Task.FromResult(AgentResult.Of(new ArtifactDraft("engineering-summary", "document", sb.ToString())));
    }
}

public static class AgentFactory
{
    public static AgentRegistry CreateAll(AgentDeps deps)
    {
        var offline = deps with { Llm = new OfflineLlmClient() };
        return new AgentRegistry()
            .Register(new RequirementsAgent(deps))
            .Register(new ClarificationAgent(deps))
            .Register(new CodebaseAnalysisAgent(deps))
            .Register(new DecompositionAgent(deps))
            .Register(new DesignAgent("design-agent", deps))
            .Register(new DesignAgent("design-agent-template", offline))
            .Register(new ImplementationAgent(deps))
            .Register(new TestAuthorAgent(deps))
            .Register(new TestRunnerAgent(deps))
            .Register(new ReviewAgent(deps))
            .Register(new RiskAgent("risk-agent", deps))
            .Register(new RiskAgent("risk-agent-template", offline))
            .Register(new TestPlanAgent(deps))
            .Register(new DocsAgent("docs-agent", deps))
            .Register(new DocsAgent("docs-agent-template", offline))
            .Register(new ReleaseAgent(deps))
            .Register(new ReadinessAgent(deps))
            .Register(new SummaryAgent(deps));
    }
}
