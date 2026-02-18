using System.Threading.RateLimiting;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using FSTService;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

// ─── Load .env file (local development secrets) ────────────

var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
if (File.Exists(envPath))
{
    foreach (var line in File.ReadAllLines(envPath))
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            continue;

        var eq = trimmed.IndexOf('=');
        if (eq <= 0)
            continue;

        var key = trimmed[..eq].Trim();
        var value = trimmed[(eq + 1)..].Trim().Trim('"');
        Environment.SetEnvironmentVariable(key, value);
    }
}

var builder = WebApplication.CreateBuilder(args);

// ─── JSON options ───────────────────────────────────────────

builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

// ─── Configuration ──────────────────────────────────────────

builder.Services.Configure<ScraperOptions>(
    builder.Configuration.GetSection(ScraperOptions.Section));
builder.Services.Configure<ApiSettings>(
    builder.Configuration.GetSection(ApiSettings.Section));

// Parse CLI arguments and overlay onto options
builder.Services.PostConfigure<ScraperOptions>(opts =>
{
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].Equals("--setup", StringComparison.OrdinalIgnoreCase))
        {
            opts.SetupOnly = true;
        }
        else if (args[i].Equals("--once", StringComparison.OrdinalIgnoreCase))
        {
            opts.RunOnce = true;
        }
        else if (args[i].Equals("--resolve-only", StringComparison.OrdinalIgnoreCase))
        {
            opts.ResolveOnly = true;
        }
        else if (args[i].Equals("--api-only", StringComparison.OrdinalIgnoreCase))
        {
            opts.ApiOnly = true;
        }
        else if (args[i].Equals("--backfill-only", StringComparison.OrdinalIgnoreCase))
        {
            opts.BackfillOnly = true;
        }
        else if (args[i].Equals("--test", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            opts.TestSongQuery = args[++i];
        }
    }
});

var apiSettings = builder.Configuration
    .GetSection(ApiSettings.Section)
    .Get<ApiSettings>() ?? new ApiSettings();

// ─── HTTP clients ───────────────────────────────────────────

builder.Services.AddHttpClient<EpicAuthService>();

builder.Services.AddHttpClient<GlobalLeaderboardScraper>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 2048,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        EnableMultipleHttp2Connections = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });

builder.Services.AddHttpClient<AccountNameResolver>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 32,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });

// ─── Auth (Epic device auth) ────────────────────────────────
builder.Services.Configure<EpicOAuthSettings>(builder.Configuration.GetSection(EpicOAuthSettings.Section));
builder.Services.AddSingleton<FSTService.Scraping.ScrapeProgressTracker>();
builder.Services.AddSingleton<ICredentialStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<ScraperOptions>>().Value;
    var path = Path.GetFullPath(opts.DeviceAuthPath);
    var log = sp.GetRequiredService<ILogger<FileCredentialStore>>();
    return new FileCredentialStore(path, log);
});

builder.Services.AddSingleton<EpicAuthService>();
builder.Services.AddSingleton<TokenManager>();

// ─── Persistence ────────────────────────────────────────────

builder.Services.AddSingleton<MetaDatabase>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<ScraperOptions>>().Value;
    var metaPath = Path.Combine(Path.GetFullPath(opts.DataDirectory), "fst-meta.db");
    return new MetaDatabase(metaPath, sp.GetRequiredService<ILogger<MetaDatabase>>());
});

builder.Services.AddSingleton<GlobalLeaderboardPersistence>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<ScraperOptions>>().Value;
    return new GlobalLeaderboardPersistence(
        Path.GetFullPath(opts.DataDirectory),
        sp.GetRequiredService<MetaDatabase>(),
        sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<ILogger<GlobalLeaderboardPersistence>>());
});

// PersonalDbBuilder — generates per-device personal SQLite DBs for mobile sync
builder.Services.AddSingleton<PersonalDbBuilder>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<ScraperOptions>>().Value;
    return new PersonalDbBuilder(
        sp.GetRequiredService<GlobalLeaderboardPersistence>(),
        sp.GetRequiredService<FestivalService>(),
        sp.GetRequiredService<MetaDatabase>(),
        Path.GetFullPath(opts.DataDirectory),
        sp.GetRequiredService<ILogger<PersonalDbBuilder>>());
});

// ─── JWT / User Auth ────────────────────────────────────────

builder.Services.Configure<JwtSettings>(
    builder.Configuration.GetSection(JwtSettings.Section));

var jwtSettings = builder.Configuration
    .GetSection(JwtSettings.Section)
    .Get<JwtSettings>() ?? new JwtSettings();

builder.Services.AddSingleton<JwtTokenService>();

builder.Services.AddSingleton<BackfillQueue>();
builder.Services.AddSingleton<TokenVault>();
builder.Services.AddSingleton<ScoreBackfiller>();
builder.Services.AddSingleton<PostScrapeRefresher>();
builder.Services.AddSingleton<FirstSeenSeasonCalculator>();
builder.Services.AddSingleton<FSTService.Api.NotificationService>();

builder.Services.AddHttpClient<HistoryReconstructor>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 32,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });

builder.Services.AddSingleton<UserAuthService>();

// Core FestivalService — song catalog sync. Shared with API for /api/songs.
builder.Services.AddSingleton<FestivalService>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<ScraperOptions>>().Value;
    var dbPath = Path.GetFullPath(opts.DatabasePath);
    var dbDir = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
        Directory.CreateDirectory(dbDir);

    var persistence = new SqlitePersistence(dbPath);
    var service = new FestivalService(persistence);
    var log = sp.GetRequiredService<ILogger<FestivalService>>();
    service.Log += msg => log.LogInformation("[Core] {Message}", msg);
    return service;
});

// ─── API authentication (API key for protected endpoints) ───

builder.Services
    .AddAuthentication("ApiKey")
    .AddScheme<ApiKeyAuthOptions, ApiKeyAuthHandler>("ApiKey", opts =>
    {
        opts.ApiKey = apiSettings.ApiKey;
    })
    .AddScheme<BearerAuthOptions, BearerTokenAuthHandler>("Bearer", _ => { });
builder.Services.AddAuthorization();

// ─── Rate limiting ──────────────────────────────────────────

var isTesting = builder.Environment.IsEnvironment("Testing");

builder.Services.AddRateLimiter(opts =>
{
    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    opts.AddFixedWindowLimiter("public", window =>
    {
        window.PermitLimit = isTesting ? 100_000 : 60;
        window.Window = TimeSpan.FromMinutes(1);
        window.QueueLimit = 0;
    });

    opts.AddFixedWindowLimiter("auth", window =>
    {
        window.PermitLimit = isTesting ? 100_000 : 10;
        window.Window = TimeSpan.FromMinutes(1);
        window.QueueLimit = 0;
    });

    opts.AddFixedWindowLimiter("protected", window =>
    {
        window.PermitLimit = isTesting ? 100_000 : 30;
        window.Window = TimeSpan.FromMinutes(1);
        window.QueueLimit = 0;
    });

    opts.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        isTesting
            ? RateLimitPartition.GetNoLimiter("global")
            : RateLimitPartition.GetFixedWindowLimiter("global", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 200,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// ─── CORS ───────────────────────────────────────────────────

builder.Services.AddCors(opts =>
{
    opts.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(apiSettings.AllowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// ─── Background worker ─────────────────────────────────────

builder.Services.AddHostedService<ScraperWorker>();

// ─── Build and configure pipeline ───────────────────────────

var app = builder.Build();

// Ensure all SQLite schemas (meta + per-instrument) exist before any request or scrape
app.Services.GetRequiredService<GlobalLeaderboardPersistence>().Initialize();

// Security: block path traversal attempts first
app.UseMiddleware<PathTraversalGuardMiddleware>();

app.UseWebSockets();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Serve static files (wwwroot/) and fall back to index.html for non-API routes
app.UseDefaultFiles();
app.UseStaticFiles();

// Map API endpoints
app.MapApiEndpoints();
app.MapAuthEndpoints();

// Fallback: serve index.html for any non-API GET request (SPA support)
app.MapFallbackToFile("index.html");

app.Run();

// Enable WebApplicationFactory<Program> for integration testing
public partial class Program { }
