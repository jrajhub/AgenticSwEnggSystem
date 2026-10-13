using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using UrlShortener.Api;
using Xunit;

namespace UrlShortener.Tests;

public class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient NewClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Create_then_redirect_then_stats_roundtrip()
    {
        var client = NewClient();

        var create = await client.PostAsJsonAsync("/api/v1/links", new { url = "https://example.com/roundtrip" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var link = await create.Content.ReadFromJsonAsync<LinkResponse>();
        Assert.NotNull(link);

        var redirect = await client.GetAsync($"/{link!.Code}");
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal("https://example.com/roundtrip", redirect.Headers.Location!.ToString());

        var stats = await client.GetFromJsonAsync<StatsResponse>($"/api/v1/links/{link.Code}/stats");
        Assert.Equal(1, stats!.Clicks);
    }

    [Fact]
    public async Task Second_create_of_same_url_returns_200_with_same_code()
    {
        var client = NewClient();
        var first = await (await client.PostAsJsonAsync("/api/v1/links", new { url = "https://example.com/dup" }))
            .Content.ReadFromJsonAsync<LinkResponse>();
        var again = await client.PostAsJsonAsync("/api/v1/links", new { url = "https://example.com/dup" });

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(first!.Code, (await again.Content.ReadFromJsonAsync<LinkResponse>())!.Code);
    }

    [Fact]
    public async Task Invalid_url_returns_problem_details_400()
    {
        var response = await NewClient().PostAsJsonAsync("/api/v1/links", new { url = "http://localhost/x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("problem+json", response.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task Unknown_code_returns_404()
    {
        var response = await NewClient().GetAsync("/doesnotexist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Alias_conflict_returns_409()
    {
        var client = NewClient();
        await client.PostAsJsonAsync("/api/v1/links", new { url = "https://example.com/x", alias = "taken-alias" });
        var conflict = await client.PostAsJsonAsync("/api/v1/links", new { url = "https://example.com/y", alias = "taken-alias" });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_is_available()
    {
        var response = await NewClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

public class RateLimitTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Create_is_rate_limited_per_client()
    {
        var client = factory.CreateClient();
        HttpStatusCode last = HttpStatusCode.OK;

        for (var i = 0; i < 31; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/links", new { url = $"https://example.com/{i}" });
            last = response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}
