using Microsoft.Extensions.DependencyInjection;
using PercentileService;

var builder = WebApplication.CreateBuilder(args);

// ─── Load .env file ────────────
EnvFileLoader.Load(Path.Combine(Directory.GetCurrentDirectory(), ".env"));

// Configuration
builder.Services.Configure<PercentileOptions>(builder.Configuration.GetSection(PercentileOptions.Section));

// HTTP clients
builder.Services.AddHttpClient<EpicTokenManager>();
builder.Services.AddHttpClient<LeaderboardQuerier>();
builder.Services.AddHttpClient<FstClient>();

// Services
builder.Services.AddSingleton<EpicTokenManager>();
builder.Services.AddSingleton<LeaderboardQuerier>();
builder.Services.AddSingleton<FstClient>();
builder.Services.AddSingleton<PercentileScrapeProgressTracker>();

// Background workers
builder.Services.AddHostedService<TokenRefreshWorker>();
builder.Services.AddSingleton<PercentileScrapeWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PercentileScrapeWorker>());

var app = builder.Build();

// ─── API endpoints ─────────────
app.MapPost("/api/scrape", async (PercentileScrapeWorker worker, CancellationToken ct) =>
{
    await worker.RunScrapeAsync(ct);
    return Results.Ok(new { message = "Scrape completed." });
})
.WithTags("Scrape");

app.MapGet("/healthz", () => Results.Ok("ok"))
.WithTags("Health");

app.MapGet("/api/progress", (PercentileScrapeProgressTracker tracker) =>
    Results.Ok(tracker.GetProgressResponse()))
.WithTags("Progress");

app.MapPost("/api/auth/device-code", async (EpicTokenManager tokenManager, CancellationToken ct) =>
{
    try
    {
        var info = await tokenManager.StartDeviceCodeFlowAsync(ct);

        // Fire-and-forget: poll in background until the user completes login.
        // PollDeviceCodeAsync handles errors internally (timeout → throws,
        // other errors → throws). Unobserved exceptions are logged by the runtime.
        _ = tokenManager.PollDeviceCodeAsync(info, CancellationToken.None);

        return Results.Ok(new
        {
            userCode = info.UserCode,
            verificationUri = info.VerificationUri,
            verificationUriComplete = info.VerificationUriComplete,
            expiresIn = info.ExpiresIn,
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: 502,
            title: "Failed to start device code flow");
    }
})
.WithTags("Auth");

app.Run();

// Allow WebApplicationFactory<Program> to discover the entry point
public partial class Program { }
