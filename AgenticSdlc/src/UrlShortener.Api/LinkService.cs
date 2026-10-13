using System.Text.RegularExpressions;

namespace UrlShortener.Api;

public enum CreateStatus { Created, Existing, InvalidUrl, InvalidAlias, AliasTaken, Exhausted }

public enum ResolveStatus { Found, NotFound }

public sealed record CreateOutcome(CreateStatus Status, ShortLink? Link = null, string? Error = null);

public sealed record ResolveOutcome(ResolveStatus Status, string? TargetUrl = null);

/// <summary>Domain rules for creating and resolving short links. No HTTP concerns live here.</summary>
public sealed class LinkService(ILinkStore store, TimeProvider clock, ICodeGenerator codes)
{
    public const int MaxUrlLength = 2048;

    private static readonly Regex AliasPattern = new("^[A-Za-z0-9_-]{4,32}$", RegexOptions.Compiled);
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "api", "healthz", "admin", "static" };

    public CreateOutcome Create(string? url, string? alias)
    {
        if (!UrlValidator.TryNormalize(url, out var normalized, out var error))
        {
            return new CreateOutcome(CreateStatus.InvalidUrl, Error: error);
        }

        if (!string.IsNullOrEmpty(alias))
        {
            if (!AliasPattern.IsMatch(alias) || Reserved.Contains(alias))
            {
                return new CreateOutcome(CreateStatus.InvalidAlias, Error: "alias must be 4-32 characters (letters, digits, '_' or '-') and not reserved.");
            }

            var custom = NewLink(alias, normalized);
            return store.TryAdd(custom)
                ? new CreateOutcome(CreateStatus.Created, custom)
                : new CreateOutcome(CreateStatus.AliasTaken, Error: "alias is already in use.");
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = NewLink(codes.Next(), normalized);
            var added = store.TryAddAuto(candidate, out var existing);
            if (added)
            {
                return new CreateOutcome(CreateStatus.Created, candidate);
            }

            if (existing is not null)
            {
                return new CreateOutcome(CreateStatus.Existing, existing);
            }
        }

        return new CreateOutcome(CreateStatus.Exhausted, Error: "could not allocate a unique code.");
    }

    public ResolveOutcome Resolve(string code)
    {
        var link = store.Get(code);
        if (link is null)
        {
            return new ResolveOutcome(ResolveStatus.NotFound);
        }

        link.RegisterClick(clock.GetUtcNow());
        return new ResolveOutcome(ResolveStatus.Found, link.TargetUrl);
    }

    public ShortLink? GetStats(string code) => store.Get(code);

    private ShortLink NewLink(string code, string url) => new()
    {
        Code = code,
        TargetUrl = url,
        CreatedAt = clock.GetUtcNow(),
    };
}
