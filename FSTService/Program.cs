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

// ─── Response compression ───────────────────────────────────

builder.Services.AddResponseCompression(opts =>
{
    opts.EnableForHttps = true;
    opts.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    opts.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(opts =>
{
    opts.Level = System.IO.Compression.CompressionLevel.Optimal;
});

// ─── Configuration ──────────────────────────────────────────

builder.Services.Configure<ScraperOptions>(
    builder.Configuration.GetSection(ScraperOptions.Section));
builder.Services.Configure<FeatureOptions>(
    builder.Configuration.GetSection(FeatureOptions.Section));
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

builder.Services.AddSingleton<ILeaderboardQuerier>(sp => sp.GetRequiredService<GlobalLeaderboardScraper>());

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
        sp.GetRequiredService<FSTService.Scraping.ScrapeProgressTracker>(),
        Path.GetFullPath(opts.DataDirectory),
        sp.GetRequiredService<ILogger<PersonalDbBuilder>>());
});

builder.Services.AddSingleton<BackfillQueue>();
builder.Services.AddSingleton<ScoreBackfiller>();
builder.Services.AddSingleton<PostScrapeRefresher>();
builder.Services.AddSingleton<BatchResultProcessor>();
builder.Services.AddTransient<SongProcessingMachine>();
builder.Services.AddSingleton<SharedDopPool>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<ScraperOptions>>().Value;
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("SharedDopPool");
    return new SharedDopPool(opts.MachineDop, opts.MachineMinDop, opts.MachineMaxDop,
        opts.MachineLowPriorityPercent, log);
});
builder.Services.AddSingleton<FirstSeenSeasonCalculator>();
builder.Services.AddSingleton<FSTService.Api.NotificationService>();
builder.Services.AddSingleton<FSTService.Api.SongsCacheService>();
builder.Services.AddKeyedSingleton<FSTService.Api.ResponseCacheService>("PlayerCache",
    (_, _) => new FSTService.Api.ResponseCacheService(TimeSpan.FromMinutes(2)));
builder.Services.AddKeyedSingleton<FSTService.Api.ResponseCacheService>("LeaderboardAllCache",
    (_, _) => new FSTService.Api.ResponseCacheService(TimeSpan.FromMinutes(5)));
builder.Services.AddKeyedSingleton<FSTService.Api.ResponseCacheService>("NeighborhoodCache",
    (_, _) => new FSTService.Api.ResponseCacheService(TimeSpan.FromMinutes(2)));
builder.Services.AddKeyedSingleton<FSTService.Api.ResponseCacheService>("RivalsCache",
    (_, _) => new FSTService.Api.ResponseCacheService(TimeSpan.FromMinutes(5)));
builder.Services.AddSingleton<RivalsCalculator>();
builder.Services.AddSingleton<RivalsOrchestrator>();
builder.Services.AddSingleton<RankingsCalculator>();
builder.Services.AddSingleton<ScrapeOrchestrator>();
builder.Services.AddSingleton<PostScrapeOrchestrator>();
builder.Services.AddSingleton<BackfillOrchestrator>();

builder.Services.AddHttpClient<ItemShopService>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });
builder.Services.AddSingleton<ItemShopService>(sp =>
    sp.GetRequiredService<IHttpClientFactory>()
      .CreateClient(nameof(ItemShopService))
      is var http
        ? new ItemShopService(
            http,
            sp.GetRequiredService<FestivalService>(),
            sp.GetRequiredService<MetaDatabase>(),
            sp.GetRequiredService<ILogger<ItemShopService>>())
        : throw new InvalidOperationException());


builder.Services.AddHttpClient<HistoryReconstructor>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 32,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });

// ─── Path Generation ────────────────────────────────────────

builder.Services.AddSingleton<PathDataStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<ScraperOptions>>().Value;
    var dbPath = Path.GetFullPath(opts.DatabasePath);
    return new PathDataStore(dbPath);
});

builder.Services.AddHttpClient<PathGenerator>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 8,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });

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
    });
builder.Services.AddAuthorization();

// ─── Rate limiting ──────────────────────────────────────────

var isTesting = builder.Environment.IsEnvironment("Testing");

builder.Services.AddRateLimiter(opts =>
{
    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    opts.AddFixedWindowLimiter("public", window =>
    {
        window.PermitLimit = isTesting ? 100_000 : 300;
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
                PermitLimit = 600,
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

// DatabaseInitializer must run before ScraperWorker (hosted services start in registration order)
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DatabaseInitializer>());
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseInitializer>("database", tags: ["ready"]);
builder.Services.AddHostedService<ScraperWorker>();

// ─── Build and configure pipeline ───────────────────────────

var app = builder.Build();

// Security: block path traversal attempts first
app.UseMiddleware<PathTraversalGuardMiddleware>();

app.UseResponseCompression();

// Wire up cross-references between NotificationService and ItemShopService
var shopService = app.Services.GetRequiredService<ItemShopService>();
var notificationService = app.Services.GetRequiredService<NotificationService>();
var songsCacheService = app.Services.GetRequiredService<SongsCacheService>();
shopService.SetNotificationService(notificationService);
shopService.SetSongsCacheService(songsCacheService);
notificationService.SetShopProvider(shopService);

app.UseCors();
app.UseWebSockets();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Serve static files (wwwroot/) and fall back to index.html for non-API routes
app.UseDefaultFiles();
app.UseStaticFiles();

// Map API endpoints
app.MapApiEndpoints();
app.MapStaticAssets();

// Fallback: serve index.html for any non-API GET request (SPA support)
app.MapFallbackToFile("index.html");

app.Run();

// Enable WebApplicationFactory<Program> for integration testing
public partial class Program { }
