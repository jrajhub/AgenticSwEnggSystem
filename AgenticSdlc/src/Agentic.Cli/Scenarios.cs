using Agentic.Agents;
using Agentic.Orchestration;

namespace Agentic.Cli;

public sealed record Scenario(
    string Name, string Description, string RawRequirement, Playbook Playbook, WorkflowGraph Graph,
    IClarificationProvider Clarifier, Action<WorkflowEngine>? Hooks = null);

public static class Scenarios
{
    private const string Core = "src/UrlShortener.Api/";

    public static IReadOnlyList<string> Names => new[] { "greenfield", "brownfield", "ambiguous" };

    public static Scenario Create(string name) => name switch
    {
        "greenfield" => Greenfield(),
        "brownfield" => Brownfield(),
        "ambiguous" => Ambiguous(),
        _ => throw new ArgumentException($"unknown scenario '{name}'"),
    };

    private static readonly IReadOnlyList<RiskItem> CoreRisks = new[]
    {
        new RiskItem("In-memory store loses data on restart and cannot scale out", "High", "High", "ILinkStore port isolates persistence; production uses Redis/SQL with replication."),
        new RiskItem("SSRF or abuse via crafted target URLs", "Medium", "High", "Scheme allow-list, credential rejection, private/loopback host blocking, length cap."),
        new RiskItem("Create endpoint abuse (spam, enumeration)", "Medium", "Medium", "Per-IP fixed-window rate limit; unguessable 62^7 codes from a cryptographic RNG."),
        new RiskItem("Click counter contention on hot links", "Low", "Medium", "Lock-free Interlocked counters; move to async batched writes at scale."),
        new RiskItem("Code collision under high volume", "Low", "Low", "Bounded retries (5) then 503; widen code length if collision rate rises."),
    };

    private static Scenario Greenfield() => new(
        "greenfield",
        "Build the URL shortener from a well-defined requirement.",
        "Build a URL shortener service. Users can submit a long http or https URL and receive a short link; optionally they can request a custom alias. " +
        "Visiting the short link redirects (HTTP 302) to the original URL. The service records click counts and last-access time per link and exposes them through a stats endpoint. " +
        "Creating the same URL twice returns the same short link. Create requests are limited to 30 per minute per client IP. " +
        "Reject URLs longer than 2048 characters, non-http(s) schemes and private or loopback hosts. " +
        "The service must expose a health endpoint and return RFC 7807 problem details for errors. Redirect p95 latency must be under 50 ms at 500 requests per second.",
        new Playbook
        {
            Title = "URL Shortener service",
            Assumptions = new[] { "Single-region deployment; the reference store is in-memory and replaceable behind ILinkStore." },
            DesignSections = new (string, string)[]
            {
                ("Architecture", "Stateless ASP.NET Core minimal API. LinkService owns domain rules; ILinkStore and ICodeGenerator are ports so persistence and code strategy can change without touching rules. Redirect is the hot path."),
                ("Data model", "ShortLink { Code, TargetUrl, CreatedAt, Clicks, LastAccessedAt }. Counters are lock-free (Interlocked). Auto-generated links are de-duplicated by target URL."),
                ("API contract", "POST /api/v1/links, GET /{code} (302), GET /api/v1/links/{code}/stats, GET /healthz. Errors are RFC 7807 problem details. See the openapi artifact."),
                ("Key decisions", "302 (not 301) so every click reaches the service and is counted. Random base62 codes (unguessable) rather than sequential IDs. Idempotent create for auto codes. Rate limiting per client IP."),
                ("Failure modes & mitigations", "Code collision: bounded retry then 503. Store failure: problem details 500/503, health endpoint reflects readiness. Abuse: rate limit plus host and scheme validation. Hot key: lock-free counters, cache in front of store."),
                ("Security", "http/https allow-list, no credentials in URLs, private/loopback/link-local hosts blocked, 2048-char cap, no stack traces to clients."),
            },
            Decisions = new[]
            {
                new DesignDecision("Use HTTP 302 for redirects", "Permanent redirects are cached by clients and would hide clicks from analytics."),
                new DesignDecision("Random base62 codes instead of counters", "Prevents enumeration of other users' links; collision risk handled by bounded retry."),
            },
            Risks = CoreRisks,
            ImpactKeywords = Array.Empty<string>(),
        },
        Workflows.Delivery("greenfield", brownfield: false),
        new ScriptedClarificationProvider(new Dictionary<string, string>()));

    private static Scenario Brownfield() => new(
        "brownfield",
        "Enhance the existing service with link expiry (TTL) without regressions.",
        "Enhancement to the existing URL shortener: callers may pass an optional ttlSeconds when creating a link. " +
        "Once a link has expired the redirect must return HTTP 410 Gone and must not count as a click. " +
        "Existing links without a TTL must behave exactly as before. Stats must still be retrievable for expired links. " +
        "Add unit and integration tests and update the documentation.",
        new Playbook
        {
            Title = "URL Shortener: expiring links (TTL)",
            Brownfield = true,
            WithTtl = true,
            Assumptions = new[] { "ttlSeconds is limited to 1..31,536,000 (365 days). Links created with a TTL are never de-duplicated or reused." },
            ImpactKeywords = new[] { "ShortLink", "ResolveStatus", "CreateLinkRequest", "LinkResponse", "RegisterClick", "NewLink" },
            DesignSections = new (string, string)[]
            {
                ("Architecture", "No new components. ShortLink gains ExpiresAt; LinkService enforces expiry in Resolve before counting the click; the HTTP layer maps the new Gone status to 410."),
                ("Data model", "ShortLink.ExpiresAt (nullable). Null means never expires, which preserves behavior of every existing link."),
                ("API contract", "POST accepts optional ttlSeconds; response includes expiresAt. GET /{code} returns 410 for expired links. Stats unchanged. Backward compatible."),
                ("Backward compatibility", "All new members are optional with defaults; the existing test suite runs unchanged as the regression gate."),
                ("Failure modes & mitigations", "Clock skew across nodes: use a single TimeProvider and tolerate seconds of skew. Invalid ttl: 400 problem details. Expired link deduplication: links with TTL bypass URL de-duplication so an expired link is never returned for a new request."),
                ("Rollout", "Ship dark behind the optional parameter; no data migration needed (in-memory store). Roll back by reverting the release folder."),
            },
            Decisions = new[]
            {
                new DesignDecision("Check expiry before registering the click", "Requirement: expired access must not count as a click."),
                new DesignDecision("TTL links bypass URL de-duplication", "Otherwise a later create for the same URL could return an expired link."),
            },
            Risks = CoreRisks.Concat(new[]
            {
                new RiskItem("Regression in redirect or create behavior for existing links", "Low", "High", "Full existing suite re-run in sandbox; expiry checks are additive and null-safe."),
                new RiskItem("Clock manipulation or skew extends or shortens link life", "Low", "Medium", "Injected TimeProvider; single source of time; document tolerance."),
            }).ToList(),
            Patches = new[]
            {
                new PatchOp(Core + "ShortLink.cs", "public DateTimeOffset CreatedAt { get; init; }",
                    "public DateTimeOffset CreatedAt { get; init; }\n    public DateTimeOffset? ExpiresAt { get; init; }"),
                new PatchOp(Core + "LinkService.cs", "public enum ResolveStatus { Found, NotFound }", "public enum ResolveStatus { Found, NotFound, Gone }"),
                new PatchOp(Core + "LinkService.cs", "public CreateOutcome Create(string? url, string? alias)", "public CreateOutcome Create(string? url, string? alias, TimeSpan? ttl = null)"),
                new PatchOp(Core + "LinkService.cs", "var custom = NewLink(alias, normalized);", "var custom = NewLink(alias, normalized, ttl);"),
                new PatchOp(Core + "LinkService.cs", "var candidate = NewLink(codes.Next(), normalized);", "var candidate = NewLink(codes.Next(), normalized, ttl);"),
                new PatchOp(Core + "LinkService.cs", "var added = store.TryAddAuto(candidate, out var existing);",
                    "ShortLink? existing = null;\n            var added = ttl is null ? store.TryAddAuto(candidate, out existing) : store.TryAdd(candidate);"),
                new PatchOp(Core + "LinkService.cs", "private ShortLink NewLink(string code, string url) => new()", "private ShortLink NewLink(string code, string url, TimeSpan? ttl = null) => new()"),
                new PatchOp(Core + "LinkService.cs", "CreatedAt = clock.GetUtcNow(),",
                    "CreatedAt = clock.GetUtcNow(),\n        ExpiresAt = ttl is { } lifetime ? clock.GetUtcNow() + lifetime : null,"),
                new PatchOp(Core + "LinkService.cs", "link.RegisterClick(clock.GetUtcNow());",
                    "if (link.ExpiresAt is { } expiry && expiry <= clock.GetUtcNow())\n        {\n            return new ResolveOutcome(ResolveStatus.Gone);\n        }\n\n        link.RegisterClick(clock.GetUtcNow());"),
                new PatchOp(Core + "Contracts.cs", "public sealed record CreateLinkRequest(string? Url, string? Alias);",
                    "public sealed record CreateLinkRequest(string? Url, string? Alias, int? TtlSeconds = null);"),
                new PatchOp(Core + "Contracts.cs", "public sealed record LinkResponse(string Code, string ShortUrl, string TargetUrl, DateTimeOffset CreatedAt)",
                    "public sealed record LinkResponse(string Code, string ShortUrl, string TargetUrl, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt = null)"),
                new PatchOp(Core + "Contracts.cs", "new(link.Code, $\"{baseUrl}/{link.Code}\", link.TargetUrl, link.CreatedAt);",
                    "new(link.Code, $\"{baseUrl}/{link.Code}\", link.TargetUrl, link.CreatedAt, link.ExpiresAt);"),
                new PatchOp(Core + "Program.cs", "var outcome = svc.Create(req.Url, req.Alias);",
                    "if (req.TtlSeconds is <= 0 or > 31_536_000)\n    {\n        return Results.Problem(statusCode: 400, title: \"Invalid request\", detail: \"ttlSeconds must be between 1 and 31536000.\");\n    }\n\n    var outcome = svc.Create(req.Url, req.Alias, req.TtlSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);"),
                new PatchOp(Core + "Program.cs", "_ => Results.Problem(statusCode: 404, title: \"Link not found\"),",
                    "ResolveStatus.Gone => Results.Problem(statusCode: 410, title: \"Link expired\"),\n        _ => Results.Problem(statusCode: 404, title: \"Link not found\"),"),
            },
            ExtraTestFiles = new Dictionary<string, string> { ["tests/UrlShortener.Tests/ExpiryTests.cs"] = ExpiryTests },
        },
        Workflows.Delivery("brownfield", brownfield: true),
        new ScriptedClarificationProvider(new Dictionary<string, string>()));

    private static Scenario Ambiguous() => new(
        "ambiguous",
        "Vague requirement: surface ambiguity, clarify with a human, then re-plan when the stakeholder changes the requirement mid-run.",
        "Make the URL shortener fast and scalable, with good security and analytics, etc. It should be reliable and easy to use for our marketing team. Links should expire at some point.",
        new Playbook
        {
            Title = "URL Shortener for marketing (ambiguous brief)",
            Assumptions = new[] { "Work stops at a build-ready hand-off; implementation is a follow-on once the readiness sign-off is approved." },
            DesignSections = new (string, string)[]
            {
                ("Architecture", "Stateless API behind a load balancer with a shared store (Redis/SQL) behind ILinkStore. Sizing derives from the clarified scale and latency answers."),
                ("Data model", "ShortLink with optional expiry, counters for clicks and last access. No personal data."),
                ("API contract", "REST, API-key protected create; public redirect. Optional TTL with 410 on expiry."),
                ("Failure modes & mitigations", "Store outage: degrade to 503 with health signalling. Abuse: rate limiting and host validation. Requirement drift: design is re-planned automatically when requirements change."),
                ("Security", "Authenticated create, http(s)-only targets, private hosts blocked, no PII stored."),
            },
            Decisions = new[] { new DesignDecision("Design follows clarified answers, not defaults", "Stakeholder answers override the analyst's default assumptions.") },
            Risks = CoreRisks,
        },
        Workflows.Ambiguous(),
        new ScriptedClarificationProvider(new Dictionary<string, string>
        {
            ["Performance"] = "p95 redirect < 80 ms at 300 requests/second.",
            ["Scale"] = "5M links and 20M redirects/day in year one.",
            ["Security"] = "API-key auth on create; block private hosts; store no personal data.",
            ["Reliability"] = "99.95% availability, RPO 1 minute.",
            ["Scope"] = "Create, redirect, stats, expiry.",
            ["Analytics"] = "Click count and last access only.",
            ["Usability"] = "REST API consumed by marketing tooling; no UI.",
            ["Expiry"] = "Optional per-link TTL, no default; expired returns 410.",
        }),
        engine =>
        {
            var injected = 0;
            engine.NodeStatusChanged += (id, status) =>
            {
                if (id == "design" && status == NodeStatus.Succeeded && Interlocked.Exchange(ref injected, 1) == 0)
                {
                    var current = engine.Context.Latest("requirements")!.Content;
                    const string change = "- FR-LATE: Links created without a TTL must default to a 30-day expiry (late change from the product owner).\n\n## Non-functional requirements";
                    engine.SubmitChange("requirements", "document", current.Replace("## Non-functional requirements", change), "human:product-owner");
                }
            };
        });

    private const string ExpiryTests = """
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using UrlShortener.Api;
using Xunit;

namespace UrlShortener.Tests;

public class ExpiryTests
{
    private static (LinkService Service, ManualTimeProvider Clock) Build()
    {
        var clock = new ManualTimeProvider();
        return (new LinkService(new InMemoryLinkStore(), clock, new RandomCodeGenerator()), clock);
    }

    [Fact]
    public void Link_without_ttl_never_expires()
    {
        var (svc, clock) = Build();
        var code = svc.Create("https://example.com/a", null).Link!.Code;
        clock.Advance(TimeSpan.FromDays(3650));
        Assert.Equal(ResolveStatus.Found, svc.Resolve(code).Status);
    }

    [Fact]
    public void Link_resolves_before_expiry()
    {
        var (svc, clock) = Build();
        var code = svc.Create("https://example.com/a", null, TimeSpan.FromSeconds(10)).Link!.Code;
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(ResolveStatus.Found, svc.Resolve(code).Status);
    }

    [Fact]
    public void Expired_link_returns_gone_and_is_not_counted()
    {
        var (svc, clock) = Build();
        var code = svc.Create("https://example.com/a", null, TimeSpan.FromSeconds(10)).Link!.Code;
        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.Equal(ResolveStatus.Gone, svc.Resolve(code).Status);
        Assert.Equal(0, svc.GetStats(code)!.Clicks);
    }

    [Fact]
    public void Stats_remain_available_for_expired_links()
    {
        var (svc, clock) = Build();
        var code = svc.Create("https://example.com/a", null, TimeSpan.FromSeconds(1)).Link!.Code;
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(svc.GetStats(code));
    }

    [Fact]
    public void Ttl_links_are_not_deduplicated()
    {
        var (svc, _) = Build();
        var first = svc.Create("https://example.com/same", null, TimeSpan.FromSeconds(60));
        var second = svc.Create("https://example.com/same", null, TimeSpan.FromSeconds(60));
        Assert.NotEqual(first.Link!.Code, second.Link!.Code);
    }

    [Fact]
    public void Expiry_timestamp_is_creation_time_plus_ttl()
    {
        var (svc, clock) = Build();
        var link = svc.Create("https://example.com/a", null, TimeSpan.FromMinutes(5)).Link!;
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), link.ExpiresAt);
    }
}

public class ExpiryApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Invalid_ttl_returns_400()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/links", new { url = "https://example.com/t", ttlSeconds = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_with_ttl_returns_expiry()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/links", new { url = "https://example.com/ttl", ttlSeconds = 600 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LinkResponse>();
        Assert.NotNull(body!.ExpiresAt);
    }
}
""";
}
