using System.Text;
using System.Text.RegularExpressions;
using Agentic.Orchestration;

namespace Agentic.Agents;

/// <summary>Requirement understanding: normalizes free text into FR/NFR items and surfaces ambiguity as explicit questions + assumptions.</summary>
public sealed class RequirementsAgent(AgentDeps deps) : AgentBase("requirements-agent", deps)
{
    private static readonly (string Pattern, string Question, string Assumption)[] VagueRules =
    {
        (@"\b(fast|quick(ly)?|performan\w+|efficient|real[- ]time)\b",
            "Performance: what p95 redirect latency and peak requests/second must be met?",
            "p95 redirect latency < 100 ms at 200 requests/second peak."),
        (@"\b(scalab\w+|scale|large|many|huge)\b",
            "Scale: how many links and redirects per day, and over what growth horizon?",
            "10M links and 50M redirects/day within 12 months; stateless API plus a shared store, scaled horizontally."),
        (@"\b(secur\w+)\b",
            "Security: which controls are mandatory (authentication on create, abuse/malware screening, data classification)?",
            "Anonymous redirect; authenticated create (API key); http(s)-only targets; private hosts blocked; no PII stored."),
        (@"\b(reliab\w+|robust|highly available|availability)\b",
            "Reliability: what availability SLO and RPO/RTO apply?",
            "99.9% monthly availability, RPO 5 minutes, RTO 30 minutes."),
        (@"\b(etc|and so on|and more)\b",
            "Scope: which items does 'etc.' cover? Please enumerate them.",
            "Scope is limited to create, redirect, analytics (clicks, last access) and expiry."),
        (@"\banalytics\b",
            "Analytics: which dimensions are required (clicks, referrer, geo, device) and what privacy limits apply?",
            "Click count and last-access time only; no IP, geo or user-agent stored."),
        (@"\b(easy|user[- ]friendly|intuitive)\b",
            "Usability: who are the users and what are the acceptance criteria for 'easy'?",
            "API-first delivery; a UI is out of scope for this iteration."),
        (@"\b(expire|expiry|at some point|eventually)\b",
            "Expiry: what is the default TTL, is it configurable per link, and what should an expired link return?",
            "Optional per-link TTL (max 365 days), no default expiry; expired links return 410 Gone and are not counted."),
    };

    private static readonly Regex NonFunctional = new(
        @"p95|latency|per minute|\brate\b|limit|availability|health|problem details|RFC|\bms\b|scal|secur|reliab",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public override Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        MaybeFailTransiently(request);
        var raw = request.Context.Require("raw-requirement").Content;
        var sentences = Regex.Split(raw.Replace("\r", ""), @"(?<=[.!?])\s+|\n+")
            .Select(s => s.Trim()).Where(s => s.Length > 3).ToList();

        var questions = new List<string>();
        var assumptions = new List<string>(deps.Playbook.Assumptions);
        foreach (var sentence in sentences.Where(s => !s.Any(char.IsDigit)))
        {
            foreach (var (pattern, question, assumption) in VagueRules)
            {
                if (Regex.IsMatch(sentence, pattern, RegexOptions.IgnoreCase) && !questions.Contains(question))
                {
                    questions.Add(question);
                    assumptions.Add(assumption);
                }
            }
        }

        var functional = sentences.Where(s => !NonFunctional.IsMatch(s)).ToList();
        var nonFunctional = sentences.Where(s => NonFunctional.IsMatch(s)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# Requirements - {deps.Playbook.Title}");
        sb.AppendLine();
        sb.AppendLine("## Normalized problem statement");
        sb.AppendLine($"Deliver: {deps.Playbook.Title}. {sentences[0]}");
        sb.AppendLine();
        sb.AppendLine("## Functional requirements");
        for (var i = 0; i < functional.Count; i++)
        {
            sb.AppendLine($"- FR-{i + 1}: {functional[i]}");
        }

        sb.AppendLine();
        sb.AppendLine("## Non-functional requirements");
        for (var i = 0; i < nonFunctional.Count; i++)
        {
            sb.AppendLine($"- NFR-{i + 1}: {nonFunctional[i]}");
        }

        sb.AppendLine();
        sb.AppendLine("## Assumptions");
        for (var i = 0; i < assumptions.Count; i++)
        {
            sb.AppendLine($"- A-{i + 1}: {assumptions[i]}");
        }

        sb.AppendLine();
        sb.AppendLine("## Open questions");
        if (questions.Count == 0)
        {
            sb.AppendLine("- None: requirement is sufficiently specified.");
        }

        for (var i = 0; i < questions.Count; i++)
        {
            sb.AppendLine($"- Q{i + 1}: {questions[i]}");
        }

        sb.AppendLine();
        sb.AppendLine("## Out of scope");
        sb.AppendLine("- User interface, billing, multi-tenant administration.");

        var open = questions.Count == 0
            ? "None"
            : string.Join("\n", questions.Select((q, i) => $"Q{i + 1}: {q}"));

        return Task.FromResult(AgentResult.Of(
                new ArtifactDraft("requirements", "document", sb.ToString()),
                new ArtifactDraft("open-questions", "document", open))
            .Decide(questions.Count == 0 ? "Requirement is unambiguous" : $"Detected {questions.Count} ambiguities",
                questions.Count == 0 ? "No vague, unmeasured terms found." : "Vague, unmeasured terms need explicit answers; defaults recorded as assumptions."));
    }
}

/// <summary>Human-in-the-loop clarification: routes open questions to the stakeholder provider and records the answers.</summary>
public sealed class ClarificationAgent(AgentDeps deps) : AgentBase("clarification-agent", deps)
{
    public override async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        var open = request.Context.Require("open-questions").Content;
        if (open.Trim() == "None")
        {
            return AgentResult.Of(new ArtifactDraft("clarifications", "document", "No clarification required."));
        }

        var questions = open.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(q => q.Trim()).ToList();
        var answers = await deps.Clarifier.AnswerAsync(questions, ct);
        var sb = new StringBuilder("# Stakeholder clarifications\n\n");
        foreach (var q in questions)
        {
            sb.AppendLine($"- {q}");
            sb.AppendLine($"  - Answer: {answers.GetValueOrDefault(q, "no answer")}");
        }

        return AgentResult.Of(new ArtifactDraft("clarifications", "document", sb.ToString()))
            .Decide("Collected stakeholder answers", $"{questions.Count} question(s) routed to the human clarification checkpoint.");
    }
}

/// <summary>Brownfield codebase reasoning: finds directly impacted files via keyword hits and one-hop dependents via type references.</summary>
public sealed class CodebaseAnalysisAgent(AgentDeps deps) : AgentBase("codebase-agent", deps)
{
    private static readonly Regex TypeDecl = new(@"\b(?:class|record|interface|enum|struct)\s+(\w+)", RegexOptions.Compiled);

    public override Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        MaybeFailTransiently(request);
        var requirements = request.Context.Require("requirements").Content;
        var root = deps.Workspace.BlueprintSrc;
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), f => File.ReadAllText(f));

        var types = files.ToDictionary(kv => kv.Key, kv => TypeDecl.Matches(kv.Value).Select(m => m.Groups[1].Value).Distinct().ToList());
        var keywords = deps.Playbook.ImpactKeywords.ToList();

        var direct = files.ToDictionary(kv => kv.Key, kv => keywords.Count(k => kv.Value.Contains(k, StringComparison.Ordinal)))
            .Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ToList();
        var directFiles = direct.Select(kv => kv.Key).ToHashSet();

        var dependents = files.Where(kv => !directFiles.Contains(kv.Key))
            .Where(kv => directFiles.Any(d => types[d].Any(t => Regex.IsMatch(kv.Value, $@"\b{Regex.Escape(t)}\b"))))
            .Select(kv => kv.Key).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# Impact analysis");
        sb.AppendLine();
        sb.AppendLine($"Keywords searched: {string.Join(", ", keywords)}");
        sb.AppendLine();
        sb.AppendLine("| File | Declares | Keyword hits | Impact |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var (file, hits) in direct)
        {
            sb.AppendLine($"| {file} | {string.Join(", ", types[file])} | {hits} | Direct |");
        }

        foreach (var file in dependents)
        {
            sb.AppendLine($"| {file} | {string.Join(", ", types[file])} | 0 | Dependent |");
        }

        sb.AppendLine();
        sb.AppendLine("## Data and control flow touched");
        sb.AppendLine("- Create: HTTP contract -> LinkService.Create -> ILinkStore -> response mapping.");
        sb.AppendLine("- Redirect: HTTP route -> LinkService.Resolve -> click registration -> 302/404 mapping.");
        sb.AppendLine("- Stats: read-only path; must keep working for every link state.");
        sb.AppendLine();
        sb.AppendLine("## Regression surface");
        sb.AppendLine("- Existing tests in tests/UrlShortener.Tests must pass unchanged; new behavior is covered by added tests.");

        return Task.FromResult(AgentResult.Of(new ArtifactDraft("impact-analysis", "document", sb.ToString()))
            .Decide($"{direct.Count} directly impacted and {dependents.Count} dependent file(s)",
                "Direct = files mentioning change-related symbols; dependent = files referencing types declared in direct files."));
    }
}

/// <summary>Task decomposition with explicit dependencies and parallel waves.</summary>
public sealed class DecompositionAgent(AgentDeps deps) : AgentBase("planner-agent", deps)
{
    private sealed record TaskItem(string Id, string Title, List<string> DependsOn);

    public override Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        MaybeFailTransiently(request);
        var requirements = request.Context.Require("requirements").Content;
        var functional = Section(requirements, "Functional requirements")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimStart('-', ' ')).ToList();

        var tasks = new List<TaskItem> { new("T1", "Confirm requirements and assumptions (human checkpoint)", new()) };
        var designDeps = new List<string> { "T1" };
        var impact = request.Context.Latest("impact-analysis");
        if (impact is not null)
        {
            var files = impact.Content.Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal) && l.Contains("| Direct |"))
                .Select(l => l.Split('|')[1].Trim()).ToList();
            tasks.Add(new("T2", $"Review impacted modules: {string.Join(", ", files)}", new() { "T1" }));
            designDeps.Add("T2");
        }

        var design = $"T{tasks.Count + 1}";
        tasks.Add(new(design, "Design architecture, API contract and failure modes (approval)", designDeps));

        var implementIds = new List<string>();
        foreach (var fr in functional)
        {
            var id = $"T{tasks.Count + 1}";
            tasks.Add(new(id, $"Implement {Trim(fr)}", new() { design }));
            implementIds.Add(id);
        }

        var testIds = new List<string>();
        foreach (var impl in implementIds)
        {
            var id = $"T{tasks.Count + 1}";
            tasks.Add(new(id, $"Test for {impl}", new() { impl }));
            testIds.Add(id);
        }

        var risk = $"T{tasks.Count + 1}";
        tasks.Add(new(risk, "Threat model and risk register", new() { design }));
        var docs = $"T{tasks.Count + 1}";
        tasks.Add(new(docs, "Documentation (with compliance sections)", new List<string> { design, risk }));
        tasks.Add(new($"T{tasks.Count + 1}", "Release readiness review and approval", testIds.Concat(new[] { docs, risk }).ToList()));

        var depth = new Dictionary<string, int>();
        foreach (var t in tasks)
        {
            depth[t.Id] = t.DependsOn.Count == 0 ? 0 : t.DependsOn.Max(d => depth[d]) + 1;
        }

        var sb = new StringBuilder("# Task plan\n\n| Task | Description | Depends on | Wave |\n|---|---|---|---|\n");
        foreach (var t in tasks)
        {
            sb.AppendLine($"| {t.Id} | {t.Title} | {(t.DependsOn.Count == 0 ? "-" : string.Join(", ", t.DependsOn))} | {depth[t.Id]} |");
        }

        sb.AppendLine();
        sb.AppendLine("Tasks sharing a wave have no mutual dependency and may execute in parallel; a task starts only when all dependencies are done.");

        return Task.FromResult(AgentResult.Of(new ArtifactDraft("task-plan", "document", sb.ToString()))
            .Decide($"Decomposed into {tasks.Count} tasks across {depth.Values.Max() + 1} waves",
                "One implementation and one test task per functional requirement; design, risk and docs gate the release review."));
    }

    private static string Trim(string s) => s.Length <= 70 ? s : s[..67] + "...";
}

/// <summary>Architecture/design: assembles the design doc and OpenAPI contract. LLM-generated when configured; playbook otherwise.</summary>
public sealed class DesignAgent(string name, AgentDeps deps) : AgentBase(name, deps)
{
    public override async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        MaybeFailTransiently(request);
        var requirements = request.Context.Require("requirements").Content;
        var impact = request.Context.Latest("impact-analysis")?.Content;
        var clarifications = request.Context.Latest("clarifications")?.Content;

        string Offline()
        {
            var sb = new StringBuilder($"# Design - {deps.Playbook.Title}\n\n");
            sb.AppendLine("## Requirements traceability");
            foreach (var line in requirements.Split('\n').Where(l => l.StartsWith("- FR-") || l.StartsWith("- NFR-")))
            {
                sb.AppendLine(line);
            }

            sb.AppendLine();
            foreach (var (heading, body) in deps.Playbook.DesignSections)
            {
                sb.AppendLine($"## {heading}");
                sb.AppendLine(body);
                sb.AppendLine();
            }

            if (impact is not null)
            {
                sb.AppendLine("## Impact analysis (codebase reasoning)");
                sb.AppendLine(string.Join("\n", impact.Split('\n').Where(l => l.StartsWith("|"))));
                sb.AppendLine();
            }

            if (clarifications is not null)
            {
                sb.AppendLine("## Stakeholder clarifications applied");
                sb.AppendLine(clarifications.Replace("# Stakeholder clarifications", "").Trim());
            }

            return sb.ToString();
        }

        var doc = await GenerateAsync(
            "You are a staff software architect. Write a concise design document in Markdown with '## ' sections including Architecture, Data model, API contract, Failure modes & mitigations and Security.",
            $"Requirements:\n{requirements}\n\nImpact analysis:\n{impact ?? "n/a (greenfield)"}\n\nClarifications:\n{clarifications ?? "n/a"}{FeedbackText(request)}",
            Offline, ct);

        var openApi = OpenApiTemplate.Build(deps.Playbook.WithTtl);
        var result = AgentResult.Of(
            new ArtifactDraft("design-doc", "document", doc),
            new ArtifactDraft("openapi", "document", openApi));
        foreach (var d in deps.Playbook.Decisions)
        {
            result = result.Decide(d.Summary, d.Rationale);
        }

        return result;
    }
}

public static class OpenApiTemplate
{
    public static string Build(bool withTtl)
    {
        var ttlProp = withTtl
            ? "\n        ttlSeconds: { type: integer, minimum: 1, maximum: 31536000, description: Optional lifetime of the link }"
            : "";
        var gone = withTtl ? "\n        '410': { description: Link expired (not counted as a click) }" : "";
        return $@"openapi: 3.0.3
info:
  title: URL Shortener API
  version: {(withTtl ? "1.1.0" : "1.0.0")}
paths:
  /api/v1/links:
    post:
      summary: Create a short link (idempotent per target URL unless alias/ttl is given)
      requestBody:
        required: true
        content:
          application/json:
            schema:
              type: object
              required: [url]
              properties:
                url: {{ type: string, maxLength: 2048 }}
                alias: {{ type: string, pattern: '^[A-Za-z0-9_-]{{4,32}}$' }}{ttlProp}
      responses:
        '201': {{ description: Created }}
        '200': {{ description: Existing link returned }}
        '400': {{ description: Invalid request (problem+json) }}
        '409': {{ description: Alias already in use }}
        '429': {{ description: Rate limit exceeded }}
  /{{code}}:
    get:
      summary: Redirect to the target URL (HTTP 302)
      responses:
        '302': {{ description: Redirect }}
        '404': {{ description: Unknown code }}{gone}
  /api/v1/links/{{code}}/stats:
    get:
      summary: Click count and last access time
      responses:
        '200': {{ description: Stats }}
        '404': {{ description: Unknown code }}
  /healthz:
    get:
      summary: Liveness/readiness
      responses:
        '200': {{ description: Healthy }}
";
    }
}
