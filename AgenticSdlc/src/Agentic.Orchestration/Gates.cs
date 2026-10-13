using System.Text.RegularExpressions;

namespace Agentic.Orchestration;

public sealed record GateResult(bool Passed, string Message, Severity Severity = Severity.High)
{
    public static GateResult Pass(string message = "ok") => new(true, message, Severity.Info);
    public static GateResult Fail(string message, Severity severity = Severity.High) => new(false, message, severity);
}

/// <summary>Outputs are the not-yet-committed artifacts for exit gates; empty for entry gates.</summary>
public sealed record GateContext(NodeSpec Node, WorkflowContext Context, IReadOnlyList<Artifact> Outputs);

public interface IGate
{
    string Name { get; }

    GateResult Evaluate(GateContext context);
}

public sealed class PredicateGate(string name, Func<GateContext, GateResult> evaluate) : IGate
{
    public string Name => name;

    public GateResult Evaluate(GateContext context) => evaluate(context);
}

public static class Gates
{
    public static IGate Predicate(string name, Func<GateContext, GateResult> evaluate) => new PredicateGate(name, evaluate);

    public static IGate ArtifactMatches(string artifact, string regex, string? name = null) =>
        new PredicateGate(name ?? $"{artifact}-matches", g =>
        {
            var a = Find(g, artifact);
            if (a is null)
            {
                return GateResult.Fail($"artifact '{artifact}' not found");
            }

            return Regex.IsMatch(a.Content, regex, RegexOptions.Multiline)
                ? GateResult.Pass()
                : GateResult.Fail($"'{artifact}' does not satisfy required pattern /{regex}/");
        });

    public static IGate ArtifactLacks(string artifact, string text, string? name = null) =>
        new PredicateGate(name ?? $"{artifact}-lacks", g =>
        {
            var a = Find(g, artifact);
            if (a is null)
            {
                return GateResult.Fail($"artifact '{artifact}' not found");
            }

            return a.Content.Contains(text, StringComparison.Ordinal)
                ? GateResult.Fail($"'{artifact}' contains forbidden marker '{text}'")
                : GateResult.Pass();
        });

    private static Artifact? Find(GateContext g, string name) =>
        g.Outputs.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)) ?? g.Context.Latest(name);
}
