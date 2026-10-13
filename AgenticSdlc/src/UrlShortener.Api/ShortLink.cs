namespace UrlShortener.Api;

/// <summary>A shortened link and its analytics counters. Counters are thread-safe.</summary>
public sealed class ShortLink
{
    private long _clicks;
    private long _lastAccessTicks;

    public required string Code { get; init; }
    public required string TargetUrl { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public long Clicks => Interlocked.Read(ref _clicks);

    public DateTimeOffset? LastAccessedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastAccessTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void RegisterClick(DateTimeOffset at)
    {
        Interlocked.Increment(ref _clicks);
        Interlocked.Exchange(ref _lastAccessTicks, at.UtcTicks);
    }
}
