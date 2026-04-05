using System.Threading.RateLimiting;
using FortniteFestival.Core.Services;
using FSTService;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
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
        else if (args[i].Equals("--precompute", StringComparison.OrdinalIgnoreCase))
        {
            opts.PrecomputeOnly = true;
        }
    }
});

var apiSettings = builder.Configuration
    .GetSection(ApiSettings.Section)
    .Get<ApiSettings>() ?? new ApiSettings();

// ─── HTTP clients ───────────────────────────────────────────

builder.Services.AddHttpClient<EpicAuthService>();

builder.Services.AddHttpClient<GlobalLeaderboardScraper>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30))
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
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30))
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

// ─── Persistence (PostgreSQL) ───────────────────────────────

var pgConnStr = builder.Configuration.GetConnectionString("PostgreSQL")
    ?? throw new InvalidOperationException("ConnectionStrings:PostgreSQL is required.");
var pgDataSource = NpgsqlDataSource.Create(pgConnStr);
builder.Services.AddSingleton(pgDataSource);

builder.Services.AddSingleton<IMetaDatabase>(sp =>
    new FSTService.Persistence.MetaDatabase(sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<ILogger<FSTService.Persistence.MetaDatabase>>()));
builder.Services.AddSingleton(sp => (FSTService.Persistence.MetaDatabase)sp.GetRequiredService<IMetaDatabase>());

builder.Services.AddSingleton<IPathDataStore>(sp =>
    new FSTService.Scraping.PathDataStore(sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<ILogger<FSTService.Scraping.PathDataStore>>()));
builder.Services.AddSingleton(sp => (FSTService.Scraping.PathDataStore)sp.GetRequiredService<IPathDataStore>());

// ─── Shared services ────────────────────────────────────────

builder.Services.AddSingleton<GlobalLeaderboardPersistence>(sp =>
{
    return new GlobalLeaderboardPersistence(
        sp.GetRequiredService<IMetaDatabase>(),
        sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<ILogger<GlobalLeaderboardPersistence>>(),
        sp.GetRequiredService<NpgsqlDataSource>());
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
    int dop = opts.DegreeOfParallelism;
    return new SharedDopPool(dop, minDop: 4, maxDop: dop,
        opts.LowPriorityPercent, log, opts.MaxRequestsPerSecond);
});
builder.Services.AddSingleton<FirstSeenSeasonCalculator>();
builder.Services.AddSingleton<FSTService.Api.NotificationService>();
builder.Services.AddSingleton<FSTService.Scraping.UserSyncProgressTracker>();
builder.Services.AddSingleton<FSTService.Api.SongsCacheService>();
builder.Services.AddSingleton<FSTService.Api.ShopCacheService>();
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
builder.Services.AddSingleton<LeaderboardRivalsCalculator>();
builder.Services.AddKeyedSingleton<FSTService.Api.ResponseCacheService>("LeaderboardRivalsCache",
    (_, _) => new FSTService.Api.ResponseCacheService(TimeSpan.FromMinutes(5)));
builder.Services.AddSingleton<RankingsCalculator>();
builder.Services.AddSingleton<ScrapeOrchestrator>();
builder.Services.AddSingleton<PostScrapeOrchestrator>();
builder.Services.AddSingleton<BackfillOrchestrator>();
builder.Services.AddSingleton<ScrapeTimePrecomputer>(sp =>
{
    var jsonOpts = sp.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
        .Value.SerializerOptions;
    return new ScrapeTimePrecomputer(
        sp.GetRequiredService<GlobalLeaderboardPersistence>(),
        sp.GetRequiredService<IMetaDatabase>(),
        sp.GetRequiredService<IPathDataStore>(),
        sp.GetRequiredService<ScrapeProgressTracker>(),
        sp.GetRequiredService<ILogger<ScrapeTimePrecomputer>>(),
        jsonOpts);
});

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
            sp.GetRequiredService<IMetaDatabase>(),
            sp.GetRequiredService<ILogger<ItemShopService>>())
        : throw new InvalidOperationException());


builder.Services.AddHttpClient<HistoryReconstructor>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 32,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });

// ─── Path Generation ────────────────────────────────────────

builder.Services.AddHttpClient<PathGenerator>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30))
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
    var persistence = new FSTService.Persistence.FestivalPersistence(
        sp.GetRequiredService<NpgsqlDataSource>());

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
    opts.OnRejected = async (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }
        else
        {
            // Default to 1 second for per-second windows
            context.HttpContext.Response.Headers.RetryAfter = "1";
        }
        await Task.CompletedTask;
    };

    static string GetClientIp(HttpContext ctx)
        => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    static RateLimitPartition<string> CreateFixedWindowPolicy(HttpContext context, bool noLimit)
        => noLimit
            ? RateLimitPartition.GetNoLimiter("test")
            : RateLimitPartition.GetFixedWindowLimiter(GetClientIp(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
            });

    opts.AddPolicy("public", context => CreateFixedWindowPolicy(context, isTesting));
    opts.AddPolicy("auth", context => CreateFixedWindowPolicy(context, isTesting));
    opts.AddPolicy("protected", context => CreateFixedWindowPolicy(context, isTesting));

    opts.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        CreateFixedWindowPolicy(context, isTesting));
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

// StartupInitializer must run before ScraperWorker (hosted services start in registration order)
builder.Services.AddSingleton<StartupInitializer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StartupInitializer>());
builder.Services.AddHealthChecks()
    .AddCheck<StartupInitializer>("database", tags: ["ready"]);
builder.Services.AddHostedService<ScraperWorker>();

// ─── Build and configure pipeline ───────────────────────────

var app = builder.Build();

// Initialize PostgreSQL schema
{
    var pgDs = app.Services.GetRequiredService<NpgsqlDataSource>();
    await FSTService.Persistence.DatabaseInitializer.EnsureSchemaAsync(pgDs);
}

// One-shot precompute: --precompute
{
    var scraperOpts2 = app.Services.GetRequiredService<IOptions<ScraperOptions>>().Value;
    if (scraperOpts2.PrecomputeOnly)
    {
        var precompLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Precompute");

        // Minimal init: open instrument DBs + load song catalog (skip Item Shop HTTP calls)
        precompLog.LogInformation("--precompute: initializing databases...");
        var precompPersistence = app.Services.GetRequiredService<GlobalLeaderboardPersistence>();
        precompPersistence.Initialize();
        var precompFestivalService = app.Services.GetRequiredService<FestivalService>();
        await precompFestivalService.InitializeAsync();
        precompLog.LogInformation("--precompute: {SongCount} songs loaded, DBs ready.",
            precompFestivalService.Songs.Count);

        precompLog.LogInformation("--precompute: running precomputation...");
        var precomputer = app.Services.GetRequiredService<ScrapeTimePrecomputer>();
        await precomputer.PrecomputeAllAsync(CancellationToken.None);

        var dataDir = Path.GetFullPath(scraperOpts2.DataDirectory);
        var precomputeDir = Path.Combine(dataDir, ScrapeTimePrecomputer.PrecomputedSubdir);
        await precomputer.SaveToDiskAsync(precomputeDir, CancellationToken.None);

        precompLog.LogInformation("--precompute: saved {Count} responses to {Dir}. Exiting.",
            precomputer.Count, precomputeDir);
        return;
    }
}

// Security: block path traversal attempts first
app.UseMiddleware<PathTraversalGuardMiddleware>();

app.UseResponseCompression();

// Wire up cross-references between NotificationService and ItemShopService
var shopService = app.Services.GetRequiredService<ItemShopService>();
var notificationService = app.Services.GetRequiredService<NotificationService>();
var songsCacheService = app.Services.GetRequiredService<SongsCacheService>();
var shopCacheService = app.Services.GetRequiredService<ShopCacheService>();
var festivalService = app.Services.GetRequiredService<FortniteFestival.Core.Services.FestivalService>();
var jsonOpts = app.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
    .Value.SerializerOptions;
shopService.SetNotificationService(notificationService);
shopService.SetShopCacheService(shopCacheService);
shopService.SetJsonSerializerOptions(jsonOpts);
notificationService.SetShopProvider(shopService);
notificationService.SetFestivalService(festivalService);

app.UseCors();
app.UseWebSockets();
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Serve static files (wwwroot/) and fall back to index.html for non-API routes,
// but only when the web app has been embedded (e.g. single-container deployment).
var webRootPath = app.Environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
var hasEmbeddedWebApp = File.Exists(Path.Combine(webRootPath, "index.html"));
if (hasEmbeddedWebApp)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

// Map API endpoints
app.MapApiEndpoints();

if (hasEmbeddedWebApp)
{
    app.MapStaticAssets();

    // Fallback: serve index.html for any non-API GET request (SPA support)
    app.MapFallbackToFile("index.html");
}

app.Run();

// Enable WebApplicationFactory<Program> for integration testing
public partial class Program { }
