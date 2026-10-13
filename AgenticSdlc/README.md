# Agentic SDLC Orchestrator (.NET 8) - URL Shortener assignment

An agentic software-engineering system that takes a requirement and drives it through
**requirements -> decomposition -> design -> implementation -> testing -> review -> docs -> release readiness -> summary**
using a stateful, governed dependency graph. The product it builds and evolves is a URL shortener (ASP.NET Core minimal API).

## Layout

| Path | Purpose |
|---|---|
| `src/Agentic.Orchestration` | The orchestration engine: graph, gates, policies, approvals, audit chain, metrics, re-planning, rollback, safe-stop |
| `src/Agentic.Agents` | SDLC agents (requirements, clarification, codebase analysis, planner, design, implementation, test author/runner, review, risk, docs, release, summary), LLM client, allow-listed command runner |
| `src/Agentic.Cli` | `agentic` CLI: three scenarios (greenfield, brownfield, ambiguous), fault injection, reports |
| `src/UrlShortener.Api` | The reference URL shortener service (also the greenfield blueprint and the brownfield baseline) |
| `tests/` | Orchestrator tests (graph, retries, fallback, rollback, policy, approvals, re-plan, safe-stop, audit) and service tests (unit + integration) |
| `docs/ARCHITECTURE.md` | Design, control flow, decisions, trade-offs, scenarios |

## Setup

Requires the .NET 8 SDK (or newer) and NuGet access (xUnit and `Microsoft.AspNetCore.Mvc.Testing` are restored on first build).

```bash
dotnet build AgenticSdlc.sln
dotnet test  AgenticSdlc.sln            # orchestrator + service tests
```

## Run the scenarios

```bash
dotnet run --project src/Agentic.Cli -- greenfield
dotnet run --project src/Agentic.Cli -- brownfield
dotnet run --project src/Agentic.Cli -- ambiguous
dotnet run --project src/Agentic.Cli -- all
```

Options: `--chaos <none|transient|policy|secret|rollback|reject>`, `--runner <real|simulated>`, `--interactive`, `--out <dir>`, `--repo <dir>`.

* `real` runner executes `dotnet test` inside the sandbox copy (needs NuGet access). `simulated` is for machines without it and is labelled as simulated everywhere.
* `--interactive` makes every approval checkpoint a real console prompt.
* Set `ANTHROPIC_API_KEY` (optional `ANTHROPIC_MODEL`) to let the design, risk and docs agents generate with an LLM. Output passes the same gates; on failure the engine falls back to the deterministic template agent.

Failure demonstrations (each shows a different control):

| Command | What you will see |
|---|---|
| `greenfield --chaos transient` | Flaky agents -> bounded retries with backoff, MTTR recorded |
| `greenfield --chaos policy` | Generated code uses MD5 -> High policy violation -> agent gets feedback, fixes it on retry |
| `greenfield --chaos secret` | Hard-coded cloud key -> Critical violation -> no retry, safe-stop, sandbox rolled back |
| `greenfield --chaos rollback` | Release fails after partial write -> retries exhausted -> release folder and sandbox compensated |
| `greenfield --chaos reject` | Human rejects the release approval -> the release action never runs |
| `ambiguous` | Parallel clarification, then a late stakeholder change triggers automatic re-planning of design and dependents |

Outputs per scenario land in `out/<scenario>/`: `artifacts/` (every artifact, latest version), `audit.jsonl` (hash-chained), `metrics.json`, `decisions.md`, `workflow.mmd` (Mermaid graph), plus `sandbox/` and `release/`.

## Run the product itself

```bash
dotnet run --project src/UrlShortener.Api
curl -s -X POST localhost:5000/api/v1/links -H 'content-type: application/json' -d '{"url":"https://example.com/docs"}'
```

## Honest status

This code was written in an environment without a .NET SDK, so **it has not been compiled or executed by the author**.
Expect to fix a small number of compile or test errors on first build; the structure, contracts and tests are designed to make those quick to find.
Treat the first `dotnet build && dotnet test` as part of the work, and record the result in your submission.
