namespace Agentic.Orchestration;

public sealed record PublishResult(Artifact Artifact, bool Changed);

/// <summary>Shared, versioned state of a run: artifacts plus the decision log. Thread-safe.</summary>
public sealed class WorkflowContext
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<Artifact>> _artifacts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Decision> _decisions = new();
    private readonly TimeProvider _clock;
    private int _decisionSeq;

    public WorkflowContext(string runId, TimeProvider? clock = null)
    {
        RunId = runId;
        _clock = clock ?? TimeProvider.System;
    }

    public string RunId { get; }

    /// <summary>Builds the artifact that Publish would create, without storing it (used for gates before commit).</summary>
    public Artifact Preview(ArtifactDraft draft, string producer, IReadOnlyList<string>? derivedFrom = null)
    {
        lock (_lock)
        {
            var version = (LatestNoLock(draft.Name)?.Version ?? 0) + 1;
            return new Artifact(draft.Name, draft.Kind, draft.Content, version, producer,
                derivedFrom ?? Array.Empty<string>(), Artifact.Hash(draft.Content), _clock.GetUtcNow());
        }
    }

    /// <summary>Stores a new version, or returns the current one untouched when content is identical (no downstream churn).</summary>
    public PublishResult Publish(ArtifactDraft draft, string producer, IReadOnlyList<string>? derivedFrom = null)
    {
        lock (_lock)
        {
            var latest = LatestNoLock(draft.Name);
            var hash = Artifact.Hash(draft.Content);
            if (latest is not null && latest.Sha256 == hash)
            {
                return new PublishResult(latest, false);
            }

            var artifact = new Artifact(draft.Name, draft.Kind, draft.Content, (latest?.Version ?? 0) + 1, producer,
                derivedFrom ?? Array.Empty<string>(), hash, _clock.GetUtcNow());
            if (!_artifacts.TryGetValue(draft.Name, out var list))
            {
                _artifacts[draft.Name] = list = new List<Artifact>();
            }

            list.Add(artifact);
            return new PublishResult(artifact, true);
        }
    }

    public Artifact? Latest(string name)
    {
        lock (_lock)
        {
            return LatestNoLock(name);
        }
    }

    public Artifact Require(string name) =>
        Latest(name) ?? throw new PermanentAgentException($"required artifact '{name}' is not available");

    public IReadOnlyList<Artifact> LatestArtifacts()
    {
        lock (_lock)
        {
            return _artifacts.Values.Select(l => l[^1]).OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
        }
    }

    public IReadOnlyList<Artifact> History(string name)
    {
        lock (_lock)
        {
            return _artifacts.TryGetValue(name, out var list) ? list.ToList() : new List<Artifact>();
        }
    }

    public Decision Record(string nodeId, string summary, string rationale, IEnumerable<string> inputs)
    {
        lock (_lock)
        {
            var decision = new Decision($"D-{++_decisionSeq:000}", nodeId, summary, rationale, inputs.ToList(), _clock.GetUtcNow());
            _decisions.Add(decision);
            return decision;
        }
    }

    public IReadOnlyList<Decision> Decisions
    {
        get
        {
            lock (_lock)
            {
                return _decisions.ToList();
            }
        }
    }

    /// <summary>Walks DerivedFrom links to show which inputs (and which producers) an artifact version rests on.</summary>
    public IReadOnlyList<string> TraceLineage(string name, int? version = null)
    {
        var lines = new List<string>();
        var latest = Latest(name);
        if (latest is null)
        {
            return lines;
        }

        Trace(name, version ?? latest.Version, 0, lines, new HashSet<string>());
        return lines;
    }

    private void Trace(string name, int version, int depth, List<string> lines, HashSet<string> seen)
    {
        Artifact? artifact;
        lock (_lock)
        {
            artifact = _artifacts.TryGetValue(name, out var list) ? list.FirstOrDefault(a => a.Version == version) : null;
        }

        var indent = new string(' ', depth * 2);
        if (artifact is null)
        {
            lines.Add($"{indent}{name}@v{version} (not found)");
            return;
        }

        lines.Add($"{indent}{artifact.Ref}  <- {artifact.ProducedBy}");
        if (!seen.Add(artifact.Ref))
        {
            return;
        }

        foreach (var input in artifact.DerivedFrom)
        {
            var at = input.LastIndexOf("@v", StringComparison.Ordinal);
            if (at > 0 && int.TryParse(input[(at + 2)..], out var v))
            {
                Trace(input[..at], v, depth + 1, lines, seen);
            }
        }
    }

    private Artifact? LatestNoLock(string name) =>
        _artifacts.TryGetValue(name, out var list) && list.Count > 0 ? list[^1] : null;
}
