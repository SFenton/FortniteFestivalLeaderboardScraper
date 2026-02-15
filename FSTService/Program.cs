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
        else if (args[i].Equals("--test", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            opts.TestSongQuery = args[++i];
        }
    }
});

var scraperOpts = builder.Configuration
    .GetSection(ScraperOptions.Section)
    .Get<ScraperOptions>() ?? new ScraperOptions();
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
builder.Services.AddSingleton<FSTService.Scraping.ScrapeProgressTracker>();
builder.Services.AddSingleton<ICredentialStore>(sp =>
{
    var path = Path.GetFullPath(scraperOpts.DeviceAuthPath);
    var log = sp.GetRequiredService<ILogger<FileCredentialStore>>();
    return new FileCredentialStore(path, log);
});

builder.Services.AddSingleton<EpicAuthService>();
builder.Services.AddSingleton<TokenManager>();

// ─── Persistence ────────────────────────────────────────────

var dataDir = Path.GetFullPath(scraperOpts.DataDirectory);
builder.Services.AddSingleton<MetaDatabase>(sp =>
{
    var metaPath = Path.Combine(dataDir, "fst-meta.db");
    return new MetaDatabase(metaPath, sp.GetRequiredService<ILogger<MetaDatabase>>());
});

builder.Services.AddSingleton<GlobalLeaderboardPersistence>(sp =>
    new GlobalLeaderboardPersistence(
        dataDir,
        sp.GetRequiredService<MetaDatabase>(),
        sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<ILogger<GlobalLeaderboardPersistence>>()));

// PersonalDbBuilder — generates per-device personal SQLite DBs for mobile sync
builder.Services.AddSingleton<PersonalDbBuilder>(sp =>
    new PersonalDbBuilder(
        sp.GetRequiredService<GlobalLeaderboardPersistence>(),
        sp.GetRequiredService<FestivalService>(),
        dataDir,
        sp.GetRequiredService<ILogger<PersonalDbBuilder>>()));

// Core FestivalService — song catalog sync. Shared with API for /api/songs.
builder.Services.AddSingleton<FestivalService>(sp =>
{
    var dbPath = Path.GetFullPath(scraperOpts.DatabasePath);
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
    });
builder.Services.AddAuthorization();

// ─── Rate limiting ──────────────────────────────────────────

builder.Services.AddRateLimiter(opts =>
{
    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    opts.AddFixedWindowLimiter("public", window =>
    {
        window.PermitLimit = 60;
        window.Window = TimeSpan.FromMinutes(1);
        window.QueueLimit = 0;
    });

    opts.AddFixedWindowLimiter("protected", window =>
    {
        window.PermitLimit = 30;
        window.Window = TimeSpan.FromMinutes(1);
        window.QueueLimit = 0;
    });

    opts.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter("global", _ => new FixedWindowRateLimiterOptions
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

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Map API endpoints
app.MapApiEndpoints();

app.Run();
