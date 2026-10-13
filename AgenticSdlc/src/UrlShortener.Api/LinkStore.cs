using System.Collections.Concurrent;

namespace UrlShortener.Api;

/// <summary>Persistence port. Swap the in-memory implementation for Redis/SQL without touching domain rules.</summary>
public interface ILinkStore
{
    /// <summary>Adds a link; false when the code is already taken.</summary>
    bool TryAdd(ShortLink link);

    /// <summary>
    /// Adds an auto-generated link, de-duplicated by target URL.
    /// Returns false with <paramref name="existing"/> set when the URL already has a link,
    /// or false with null when the generated code collided.
    /// </summary>
    bool TryAddAuto(ShortLink link, out ShortLink? existing);

    ShortLink? Get(string code);
}

public sealed class InMemoryLinkStore : ILinkStore
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, ShortLink> _byCode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _codeByUrl = new(StringComparer.Ordinal);

    public bool TryAdd(ShortLink link)
    {
        lock (_gate)
        {
            return _byCode.TryAdd(link.Code, link);
        }
    }

    public bool TryAddAuto(ShortLink link, out ShortLink? existing)
    {
        lock (_gate)
        {
            if (_codeByUrl.TryGetValue(link.TargetUrl, out var code) && _byCode.TryGetValue(code, out var found))
            {
                existing = found;
                return false;
            }

            existing = null;
            if (!_byCode.TryAdd(link.Code, link))
            {
                return false;
            }

            _codeByUrl[link.TargetUrl] = link.Code;
            return true;
        }
    }

    public ShortLink? Get(string code) => _byCode.TryGetValue(code, out var link) ? link : null;
}
