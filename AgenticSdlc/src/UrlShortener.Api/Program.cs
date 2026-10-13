using System.Threading.RateLimiting;
using UrlShortener.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ILinkStore, InMemoryLinkStore>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICodeGenerator, RandomCodeGenerator>();
builder.Services.AddSingleton<LinkService>();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("create", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();

app.MapHealthChecks("/healthz");

app.MapPost("/api/v1/links", (CreateLinkRequest req, LinkService svc, HttpContext http) =>
{
    var outcome = svc.Create(req.Url, req.Alias);
    var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
    return outcome.Status switch
    {
        CreateStatus.Created => Results.Created($"/api/v1/links/{outcome.Link!.Code}/stats", LinkResponse.From(outcome.Link!, baseUrl)),
        CreateStatus.Existing => Results.Ok(LinkResponse.From(outcome.Link!, baseUrl)),
        CreateStatus.AliasTaken => Results.Problem(statusCode: 409, title: "Alias unavailable", detail: outcome.Error),
        CreateStatus.Exhausted => Results.Problem(statusCode: 503, title: "Temporarily unavailable", detail: outcome.Error),
        _ => Results.Problem(statusCode: 400, title: "Invalid request", detail: outcome.Error),
    };
}).RequireRateLimiting("create");

app.MapGet("/api/v1/links/{code}/stats", (string code, LinkService svc) =>
{
    var link = svc.GetStats(code);
    return link is null
        ? Results.Problem(statusCode: 404, title: "Link not found")
        : Results.Ok(StatsResponse.From(link));
});

app.MapGet("/{code}", (string code, LinkService svc) =>
{
    var outcome = svc.Resolve(code);
    return outcome.Status switch
    {
        ResolveStatus.Found => Results.Redirect(outcome.TargetUrl!, permanent: false),
        _ => Results.Problem(statusCode: 404, title: "Link not found"),
    };
});

app.Run();

public partial class Program;
