using System.Text.RegularExpressions;

namespace Agentic.Orchestration;

public sealed record PolicyViolation(string Policy, string Rule, Severity Severity, string ArtifactName, string Detail);

public interface IPolicy
{
    string Name { get; }

    IEnumerable<PolicyViolation> Evaluate(Artifact artifact);
}

/// <summary>Security: committed secrets are Critical (safe-stop, no retry).</summary>
public sealed class SecretScanPolicy : IPolicy
{
    private static readonly (string Rule, Regex Pattern)[] Rules =
    {
        ("aws-access-key", new Regex(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled)),
        ("private-key", new Regex(@"-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.Compiled)),
        ("hardcoded-credential", new Regex(@"(password|passwd|secret|api[_-]?key|token)\s*[:=]\s*""[^""\s]{8,}""", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    };

    public string Name => "secret-scan";

    public IEnumerable<PolicyViolation> Evaluate(Artifact artifact)
    {
        foreach (var (rule, pattern) in Rules)
        {
            if (pattern.IsMatch(artifact.Content))
            {
                yield return new PolicyViolation(Name, rule, Severity.Critical, artifact.Name, "possible secret in artifact content");
            }
        }
    }
}

/// <summary>Security: known-dangerous constructs in generated code are High (blocked, agent gets feedback and retries).</summary>
public sealed class InsecureCodePolicy : IPolicy
{
    private static readonly (string Rule, Regex Pattern)[] Rules =
    {
        ("weak-hash", new Regex(@"\b(MD5|SHA1)\.(Create|HashData)", RegexOptions.Compiled)),
        ("unsafe-deserialization", new Regex(@"\bBinaryFormatter\b", RegexOptions.Compiled)),
        ("process-execution", new Regex(@"\bProcess\.Start\b", RegexOptions.Compiled)),
        ("tls-validation-disabled", new Regex(@"DangerousAcceptAnyServerCertificateValidator|ServerCertificateValidationCallback\s*[+=]", RegexOptions.Compiled)),
        ("sql-concatenation", new Regex(@"""\s*(SELECT|INSERT|UPDATE|DELETE)\b[^""]*""\s*\+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    };

    public string Name => "insecure-code";

    public IEnumerable<PolicyViolation> Evaluate(Artifact artifact)
    {
        if (artifact.Kind is not ("code" or "test"))
        {
            yield break;
        }

        foreach (var (rule, pattern) in Rules)
        {
            if (pattern.IsMatch(artifact.Content))
            {
                yield return new PolicyViolation(Name, rule, Severity.High, artifact.Name, $"forbidden construct '{rule}' in generated code");
            }
        }
    }
}

/// <summary>Compliance: an artifact must contain named sections (e.g. data classification and retention in user docs).</summary>
public sealed class RequiredSectionsPolicy(string artifactName, params string[] sections) : IPolicy
{
    public string Name => $"required-sections:{artifactName}";

    public IEnumerable<PolicyViolation> Evaluate(Artifact artifact)
    {
        if (!string.Equals(artifact.Name, artifactName, StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        foreach (var section in sections)
        {
            if (!Regex.IsMatch(artifact.Content, $@"(?im)^#{{1,4}}\s*{Regex.Escape(section)}"))
            {
                yield return new PolicyViolation(Name, "missing-section", Severity.High, artifact.Name, $"missing required section '{section}'");
            }
        }
    }
}

/// <summary>Engine-embedded guardrails. Agents cannot opt out: every output of every node passes through here before commit.</summary>
public sealed class PolicyGuard(IEnumerable<IPolicy> policies)
{
    private readonly IReadOnlyList<IPolicy> _policies = policies.ToList();

    public static PolicyGuard Default(params IPolicy[] extra) =>
        new(new IPolicy[] { new SecretScanPolicy(), new InsecureCodePolicy() }.Concat(extra));

    public IReadOnlyList<PolicyViolation> Evaluate(IEnumerable<Artifact> artifacts) =>
        artifacts.SelectMany(a => _policies.SelectMany(p => p.Evaluate(a))).ToList();
}
