namespace UrlShortener.Api;

public sealed record CreateLinkRequest(string? Url, string? Alias);

public sealed record LinkResponse(string Code, string ShortUrl, string TargetUrl, DateTimeOffset CreatedAt)
{
    public static LinkResponse From(ShortLink link, string baseUrl) =>
        new(link.Code, $"{baseUrl}/{link.Code}", link.TargetUrl, link.CreatedAt);
}

public sealed record StatsResponse(string Code, string TargetUrl, long Clicks, DateTimeOffset CreatedAt, DateTimeOffset? LastAccessedAt)
{
    public static StatsResponse From(ShortLink link) =>
        new(link.Code, link.TargetUrl, link.Clicks, link.CreatedAt, link.LastAccessedAt);
}
