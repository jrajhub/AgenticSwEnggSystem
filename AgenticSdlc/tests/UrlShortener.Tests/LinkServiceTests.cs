using UrlShortener.Api;
using Xunit;

namespace UrlShortener.Tests;

public class LinkServiceTests
{
    private static LinkService Build(ICodeGenerator? codes = null, ManualTimeProvider? clock = null) =>
        new(new InMemoryLinkStore(), clock ?? new ManualTimeProvider(), codes ?? new RandomCodeGenerator());

    [Fact]
    public void Create_returns_short_code_for_valid_url()
    {
        var outcome = Build().Create("https://example.com/path?q=1", null);

        Assert.Equal(CreateStatus.Created, outcome.Status);
        Assert.Equal(7, outcome.Link!.Code.Length);
        Assert.Equal("https://example.com/path?q=1", outcome.Link.TargetUrl);
    }

    [Fact]
    public void Create_is_idempotent_for_the_same_url()
    {
        var svc = Build();
        var first = svc.Create("https://example.com/a", null);
        var second = svc.Create("https://example.com/a", null);

        Assert.Equal(CreateStatus.Created, first.Status);
        Assert.Equal(CreateStatus.Existing, second.Status);
        Assert.Equal(first.Link!.Code, second.Link!.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/file")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@example.com/")]
    [InlineData("http://localhost/admin")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://10.1.2.3/")]
    [InlineData("http://192.168.0.10/")]
    [InlineData("http://172.16.5.4/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/")]
    public void Create_rejects_unsafe_or_invalid_urls(string url)
    {
        Assert.Equal(CreateStatus.InvalidUrl, Build().Create(url, null).Status);
    }

    [Fact]
    public void Create_rejects_urls_over_the_length_limit()
    {
        var url = "https://example.com/" + new string('a', LinkService.MaxUrlLength);
        Assert.Equal(CreateStatus.InvalidUrl, Build().Create(url, null).Status);
    }

    [Fact]
    public void Custom_alias_is_used_and_cannot_be_taken_twice()
    {
        var svc = Build();
        var first = svc.Create("https://example.com/a", "my-link");
        var second = svc.Create("https://example.com/b", "my-link");

        Assert.Equal("my-link", first.Link!.Code);
        Assert.Equal(CreateStatus.AliasTaken, second.Status);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("has space")]
    [InlineData("healthz")]
    [InlineData("api")]
    public void Invalid_or_reserved_alias_is_rejected(string alias)
    {
        Assert.Equal(CreateStatus.InvalidAlias, Build().Create("https://example.com", alias).Status);
    }

    [Fact]
    public void Code_collisions_are_bounded_and_reported()
    {
        var svc = Build(new FixedCodeGenerator("AAAAAAA"));
        Assert.Equal(CreateStatus.Created, svc.Create("https://example.com/1", null).Status);
        Assert.Equal(CreateStatus.Exhausted, svc.Create("https://example.com/2", null).Status);
    }

    [Fact]
    public void Resolve_counts_clicks_and_tracks_last_access()
    {
        var clock = new ManualTimeProvider();
        var svc = Build(clock: clock);
        var code = svc.Create("https://example.com/a", null).Link!.Code;

        clock.Advance(TimeSpan.FromMinutes(5));
        var resolved = svc.Resolve(code);
        var stats = svc.GetStats(code)!;

        Assert.Equal(ResolveStatus.Found, resolved.Status);
        Assert.Equal("https://example.com/a", resolved.TargetUrl);
        Assert.Equal(1, stats.Clicks);
        Assert.Equal(clock.GetUtcNow(), stats.LastAccessedAt);
    }

    [Fact]
    public void Resolve_unknown_code_is_not_found()
    {
        Assert.Equal(ResolveStatus.NotFound, Build().Resolve("nope1234").Status);
    }

    [Fact]
    public async Task Click_counter_is_thread_safe()
    {
        var svc = Build();
        var code = svc.Create("https://example.com/hot", null).Link!.Code;

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => svc.Resolve(code))));

        Assert.Equal(200, svc.GetStats(code)!.Clicks);
    }

    [Fact]
    public async Task Concurrent_creates_of_the_same_url_yield_one_code()
    {
        var svc = Build();
        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => svc.Create("https://example.com/same", null))));

        Assert.Single(results.Select(r => r.Link!.Code).Distinct());
    }
}
