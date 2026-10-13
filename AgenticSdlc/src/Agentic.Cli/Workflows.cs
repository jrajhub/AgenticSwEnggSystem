using System.Text.RegularExpressions;
using Agentic.Orchestration;

namespace Agentic.Cli;

/// <summary>The three SDLC dependency graphs. Parallel branches synchronize at joins; high-impact nodes carry approval checkpoints.</summary>
public static class Workflows
{
    private static readonly string[] External = { "raw-requirement" };

    private static IGate ReleaseReadiness() => Gates.Predicate("release-readiness", g =>
    {
        var tests = g.Context.Latest("test-report");
        var review = g.Context.Latest("security-review");
        if (tests is null || !Regex.IsMatch(tests.Content, @"(?m)^- Failed:\s*0\s*$") || !Regex.IsMatch(tests.Content, @"(?m)^- Passed:\s*[1-9]"))
        {
            return GateResult.Fail("tests are not green");
        }

        if (review is null || review.Content.Contains("[FAIL]", StringComparison.Ordinal))
        {
            return GateResult.Fail("security review has failing checks");
        }

        return GateResult.Pass(tests.Content.Contains("SIMULATED", StringComparison.Ordinal)
            ? "tests green (SIMULATED runner - not production evidence)"
            : "tests green, review clean");
    });

    public static WorkflowGraph Delivery(string name, bool brownfield)
    {
        var nodes = new List<NodeSpec>
        {
            new()
            {
                Id = "analyze", Agent = "requirements-agent", Description = "Normalize requirement, detect ambiguity",
                Consumes = new[] { "raw-requirement" }, Produces = new[] { "requirements", "open-questions" },
            },
        };

        if (brownfield)
        {
            nodes.Add(new()
            {
                Id = "impact", Agent = "codebase-agent", DependsOn = new[] { "analyze" }, Description = "Codebase reasoning: impacted modules and flows",
                Consumes = new[] { "requirements" }, Produces = new[] { "impact-analysis" },
            });
        }

        var planDeps = brownfield ? new[] { "impact" } : new[] { "analyze" };
        var planInputs = brownfield ? new[] { "requirements", "impact-analysis" } : new[] { "requirements" };
        var designInputs = brownfield ? new[] { "requirements", "task-plan", "impact-analysis" } : new[] { "requirements", "task-plan" };

        nodes.AddRange(new NodeSpec[]
        {
            new()
            {
                Id = "decompose", Agent = "planner-agent", DependsOn = planDeps, Description = "Decompose into dependent tasks and waves",
                Consumes = planInputs, Produces = new[] { "task-plan" },
            },
            new()
            {
                Id = "design", Agent = "design-agent", FallbackAgent = "design-agent-template", DependsOn = new[] { "decompose" },
                Description = "Architecture, API contract, failure modes (architecture sign-off)",
                Consumes = designInputs, Produces = new[] { "design-doc", "openapi" },
                Impact = Impact.High, Approval = ApprovalMode.BeforeCommit,
                ExitGates = new[] { Gates.ArtifactMatches("design-doc", @"^## Failure modes", "design-has-failure-modes") },
            },
            new()
            {
                Id = "risk", Agent = "risk-agent", FallbackAgent = "risk-agent-template", DependsOn = new[] { "design" },
                Description = "Risk register from design", Consumes = new[] { "design-doc" }, Produces = new[] { "risk-register" },
            },
            new()
            {
                Id = "implement", Agent = "implementation-agent", DependsOn = new[] { "design" }, Compensable = true,
                Description = brownfield ? "Apply patches to sandbox copy of existing code" : "Generate implementation in sandbox",
                Consumes = new[] { "design-doc", "openapi" }, Produces = new[] { "source-bundle", "change-summary" },
                ExitGates = new[] { Gates.ArtifactMatches("source-bundle", @"^// FILE: src/UrlShortener\.Api/Program\.cs", "has-entrypoint") },
            },
            new()
            {
                Id = "author-tests", Agent = "test-author-agent", DependsOn = new[] { "design" }, Compensable = true,
                Description = "Assemble regression + scenario tests", Consumes = new[] { "design-doc" }, Produces = new[] { "test-bundle" },
            },
            new()
            {
                Id = "run-tests", Agent = "test-runner-agent", DependsOn = new[] { "implement", "author-tests" },
                Description = "Build and run tests (join of implement and author-tests)",
                Consumes = new[] { "source-bundle", "test-bundle" }, Produces = new[] { "test-report" },
                ExitGates = new[]
                {
                    Gates.ArtifactMatches("test-report", @"^- Failed:\s*0\s*$", "no-failing-tests"),
                    Gates.ArtifactMatches("test-report", @"^- Passed:\s*[1-9]", "tests-actually-ran"),
                },
            },
            new()
            {
                Id = "review", Agent = "review-agent", DependsOn = new[] { "implement" },
                Description = "Static security and quality review", Consumes = new[] { "source-bundle" }, Produces = new[] { "security-review" },
                ExitGates = new[] { Gates.ArtifactLacks("security-review", "[FAIL]", "review-clean") },
            },
            new()
            {
                Id = "docs", Agent = "docs-agent", FallbackAgent = "docs-agent-template", DependsOn = new[] { "risk" },
                Description = "User and operations documentation", Consumes = new[] { "design-doc", "risk-register" }, Produces = new[] { "user-docs" },
            },
            new()
            {
                Id = "release", Agent = "release-agent", DependsOn = new[] { "run-tests", "review", "docs" }, Compensable = true,
                Description = "Promote verified artifacts to the release folder",
                Consumes = new[] { "source-bundle", "test-report", "security-review", "user-docs" }, Produces = new[] { "release-notes" },
                Impact = Impact.High, Approval = ApprovalMode.BeforeExecution, MaxRetries = 1,
                EntryGates = new[] { ReleaseReadiness() },
            },
            new()
            {
                Id = "summarize", Agent = "summary-agent", DependsOn = new[] { "release" },
                Description = "Final engineering summary",
                Consumes = new[] { "requirements", "task-plan", "risk-register", "test-report", "release-notes" }, Produces = new[] { "engineering-summary" },
            },
        });

        return new WorkflowGraph(name, nodes, External);
    }

    public static WorkflowGraph Ambiguous() => new("ambiguous", new NodeSpec[]
    {
        new()
        {
            Id = "analyze", Agent = "requirements-agent", Description = "Normalize requirement, detect ambiguity",
            Consumes = new[] { "raw-requirement" }, Produces = new[] { "requirements", "open-questions" },
        },
        new()
        {
            Id = "clarify", Agent = "clarification-agent", DependsOn = new[] { "analyze" }, Description = "Human clarification checkpoint",
            Consumes = new[] { "open-questions" }, Produces = new[] { "clarifications" },
        },
        new()
        {
            Id = "decompose", Agent = "planner-agent", DependsOn = new[] { "analyze" }, Description = "Decompose (runs in parallel with clarification)",
            Consumes = new[] { "requirements" }, Produces = new[] { "task-plan" },
        },
        new()
        {
            Id = "design", Agent = "design-agent", FallbackAgent = "design-agent-template", DependsOn = new[] { "clarify", "decompose" },
            Description = "Design using clarified requirements (join of clarify and decompose)",
            Consumes = new[] { "requirements", "clarifications", "task-plan" }, Produces = new[] { "design-doc", "openapi" },
            Impact = Impact.High, Approval = ApprovalMode.BeforeCommit,
            ExitGates = new[] { Gates.ArtifactMatches("design-doc", @"^## Failure modes", "design-has-failure-modes") },
        },
        new()
        {
            Id = "risk", Agent = "risk-agent", FallbackAgent = "risk-agent-template", DependsOn = new[] { "design" },
            Description = "Risk register", Consumes = new[] { "design-doc" }, Produces = new[] { "risk-register" },
        },
        new()
        {
            Id = "test-plan", Agent = "test-plan-agent", DependsOn = new[] { "design" },
            Description = "Test plan derived from requirements", Consumes = new[] { "requirements", "design-doc" }, Produces = new[] { "test-plan" },
        },
        new()
        {
            Id = "docs", Agent = "docs-agent", FallbackAgent = "docs-agent-template", DependsOn = new[] { "risk" },
            Description = "Documentation", Consumes = new[] { "design-doc", "risk-register" }, Produces = new[] { "user-docs" },
        },
        new()
        {
            Id = "readiness", Agent = "readiness-agent", DependsOn = new[] { "risk", "test-plan" },
            Description = "Definition-of-ready sign-off before build",
            Consumes = new[] { "requirements", "open-questions", "clarifications", "design-doc", "test-plan", "risk-register" },
            Produces = new[] { "readiness-report" }, Impact = Impact.High, Approval = ApprovalMode.BeforeCommit,
            ExitGates = new[] { Gates.ArtifactMatches("readiness-report", @"^- Unresolved:\s*0\s*$", "no-unresolved-questions") },
        },
        new()
        {
            Id = "summarize", Agent = "summary-agent", DependsOn = new[] { "readiness", "docs" }, Description = "Final engineering summary",
            Consumes = new[] { "requirements", "task-plan", "risk-register", "readiness-report" }, Produces = new[] { "engineering-summary" },
        },
    }, External);
}
