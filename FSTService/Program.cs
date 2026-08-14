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

RetiredMaintenanceCommandGuard.ThrowIfPresent(args);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ServiceInstanceIdentity>();

// ─── ThreadPool tuning ──────────────────────────────────────
// Default min threads = processor count, which on small VPS (2–4 cores)
// causes starvation when DOP ≫ cores. Sync-over-async persistence
// callbacks and high-concurrency HTTP work both need thread headroom.
// Must match or exceed the configured DOP to avoid thread pool growth
// delays (1 thread per 500ms) causing cascading timeouts.
{
    var scraperDop = builder.Configuration.GetValue("Scraper:DegreeOfParallelism", 575);
    ThreadPool.GetMinThreads(out int prevWorker, out int prevIo);
    int target = Math.Max(200, Math.Max(prevWorker, scraperDop));
    ThreadPool.SetMinThreads(target, target);
    ThreadPool.GetMinThreads(out int newWorker, out int newIo);
    Console.WriteLine($"ThreadPool.SetMinThreads({target}, {target}) — was ({prevWorker}, {prevIo})");
}

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

var improvementNotificationRecoveryRequested = args.Any(
    arg => arg.Equals("--recover-improvement-notifications", StringComparison.OrdinalIgnoreCase));
var scoreHistoryDedupMaintenanceCommand =
    ScoreHistoryDedupMaintenanceCommand.Parse(args);
var soloFamilyRankingBackfillCommand =
    SoloFamilyRankingBackfillCommand.Parse(args);
var leaderboardRivalsRecomputeCommand =
    LeaderboardRivalsRecomputeCommand.Parse(args);
var initializeSchemaOnlyRequested = args.Any(
    arg => arg.Equals(
        "--initialize-schema-only",
        StringComparison.OrdinalIgnoreCase));
var rolloutReadOnlyStartupRequested = args.Any(
        arg => arg.Equals(
            "--rollout-read-only-startup",
            StringComparison.OrdinalIgnoreCase))
    || builder.Configuration.GetValue<bool>(
        $"{ScraperOptions.Section}:RolloutReadOnlyStartup");
var rolloutPostgresReadOnlyRequested = args.Any(
        arg => arg.Equals(
            "--rollout-postgres-read-only",
            StringComparison.OrdinalIgnoreCase))
    || builder.Configuration.GetValue<bool>(
        $"{ScraperOptions.Section}:RolloutPostgresReadOnly");
if (rolloutReadOnlyStartupRequested != rolloutPostgresReadOnlyRequested)
{
    throw new ArgumentException(
        "Rollout read-only startup and PostgreSQL read-only enforcement must be enabled together.");
}
if (rolloutReadOnlyStartupRequested
    && (improvementNotificationRecoveryRequested
        || scoreHistoryDedupMaintenanceCommand is not null
        || soloFamilyRankingBackfillCommand is not null
        || leaderboardRivalsRecomputeCommand is not null
        || initializeSchemaOnlyRequested))
{
    throw new ArgumentException(
        "Rollout read-only startup cannot run with schema or maintenance commands.");
}
if (scoreHistoryDedupMaintenanceCommand is not null
    && (improvementNotificationRecoveryRequested
        || soloFamilyRankingBackfillCommand is not null
        || leaderboardRivalsRecomputeCommand is not null
        || initializeSchemaOnlyRequested))
{
    throw new ArgumentException(
        "Score-history dedup maintenance cannot run with another one-shot " +
        "schema or notification command.");
}
if (soloFamilyRankingBackfillCommand is not null
    && (improvementNotificationRecoveryRequested
        || leaderboardRivalsRecomputeCommand is not null
        || initializeSchemaOnlyRequested))
{
    throw new ArgumentException(
        "Solo-family ranking backfill cannot run with another one-shot " +
        "schema or notification command.");
}
if (leaderboardRivalsRecomputeCommand is not null
    && (improvementNotificationRecoveryRequested
        || initializeSchemaOnlyRequested))
{
    throw new ArgumentException(
        "Leaderboard-rivals recompute cannot run with another one-shot " +
        "schema or notification command.");
}

var apiOnlyRequested = improvementNotificationRecoveryRequested
    || scoreHistoryDedupMaintenanceCommand is not null
    || soloFamilyRankingBackfillCommand is not null
    || leaderboardRivalsRecomputeCommand is not null
    || initializeSchemaOnlyRequested
    || args.Any(arg => arg.Equals("--api-only", StringComparison.OrdinalIgnoreCase))
    || builder.Configuration.GetValue<bool>($"{ScraperOptions.Section}:ApiOnly");
var scraperWorkerDisabled = rolloutReadOnlyStartupRequested
    || args.Any(arg => arg.Equals("--no-scraper-worker", StringComparison.OrdinalIgnoreCase))
    || builder.Configuration.GetValue<bool>($"{ScraperOptions.Section}:DisableScraperWorker");
var registrationSyncWorkerRequested = args.Any(arg => arg.Equals("--registration-sync-worker", StringComparison.OrdinalIgnoreCase))
    || builder.Configuration.GetValue<bool>($"{ScraperOptions.Section}:RegistrationSyncWorkerOnly");
var runOnceRequested = args.Any(arg => arg.Equals("--once", StringComparison.OrdinalIgnoreCase))
    || builder.Configuration.GetValue<bool>($"{ScraperOptions.Section}:RunOnce");
var backfillOnlyRequested = args.Any(arg => arg.Equals("--backfill-only", StringComparison.OrdinalIgnoreCase))
    || builder.Configuration.GetValue<bool>($"{ScraperOptions.Section}:BackfillOnly");
var hostedWorkerMode = HostedWorkerModeResolver.Resolve(
    apiOnlyRequested,
    scraperWorkerDisabled,
    registrationSyncWorkerRequested);
var hostedServicePlan = HostedWorkerModeResolver.ResolveHostedServicePlan(
    hostedWorkerMode,
    rolloutReadOnlyStartupRequested,
    runOnceRequested,
    backfillOnlyRequested);

builder.Services.Configure<ScraperOptions>(
    builder.Configuration.GetSection(ScraperOptions.Section));
builder.Services.AddOptions<FeatureOptions>()
    .Bind(builder.Configuration.GetSection(FeatureOptions.Section))
    .ValidateOnStart();
builder.Services.Configure<ClientTelemetryOptions>(
    builder.Configuration.GetSection(ClientTelemetryOptions.Section));
builder.Services.Configure<ImprovementNotificationOptions>(
    builder.Configuration.GetSection(ImprovementNotificationOptions.Section));
builder.Services.Configure<BandRankHistoryOptions>(
    builder.Configuration.GetSection(BandRankHistoryOptions.Section));
builder.Services.Configure<PublicationCommitOptions>(
    builder.Configuration.GetSection(PublicationCommitOptions.Section));
builder.Services.Configure<BandTeamRankingRebuildOptions>(
    builder.Configuration.GetSection(BandTeamRankingRebuildOptions.Section));
builder.Services.Configure<BackgroundJobOptions>(
    builder.Configuration.GetSection(BackgroundJobOptions.Section));
builder.Services.Configure<DatabaseMaintenanceOptions>(
    builder.Configuration.GetSection(DatabaseMaintenanceOptions.Section));
builder.Services.AddOptions<ApiSettings>()
    .Bind(builder.Configuration.GetSection(ApiSettings.Section))
    .Validate(settings => !string.IsNullOrWhiteSpace(settings.ApiKey),
        "Api:ApiKey must be configured.")
    .ValidateOnStart();
if (hostedWorkerMode is HostedWorkerMode.ApiOnly or HostedWorkerMode.FrontendOnly)
    builder.Services.AddSingleton<IProxyContainerRecycler, DisabledProxyContainerRecycler>();
else
    builder.Services.AddSingleton<IProxyContainerRecycler, GluetunContainerRecycler>();
builder.Services.AddSingleton<ProxyPool>();
builder.Services.AddSingleton<IProxyHealthReporter>(sp => sp.GetRequiredService<ProxyPool>());

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
        else if (args[i].Equals("--no-scraper-worker", StringComparison.OrdinalIgnoreCase))
        {
            opts.DisableScraperWorker = true;
        }
        else if (args[i].Equals("--rollout-read-only-startup", StringComparison.OrdinalIgnoreCase))
        {
            opts.RolloutReadOnlyStartup = true;
            opts.DisableScraperWorker = true;
        }
        else if (args[i].Equals("--rollout-postgres-read-only", StringComparison.OrdinalIgnoreCase))
        {
            opts.RolloutPostgresReadOnly = true;
        }
        else if (args[i].Equals("--registration-sync-worker", StringComparison.OrdinalIgnoreCase))
        {
            opts.RegistrationSyncWorkerOnly = true;
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
        else if (args[i].Equals("--solo-scrape", StringComparison.OrdinalIgnoreCase))
        {
            opts.EnabledPhases |= ScrapePhase.SoloScrape;
        }
        else if (args[i].Equals("--solo-enrichment", StringComparison.OrdinalIgnoreCase))
        {
            opts.EnabledPhases |= ScrapePhase.SoloEnrichment;
        }
        else if (args[i].Equals("--solo-refresh-users", StringComparison.OrdinalIgnoreCase))
        {
            opts.EnabledPhases |= ScrapePhase.SoloRefreshUsers;
        }
        else if (args[i].Equals("--solo-leaderboards", StringComparison.OrdinalIgnoreCase))
        {
            opts.EnabledPhases |= ScrapePhase.SoloRankings;
        }
        else if (args[i].Equals("--solo-rivals", StringComparison.OrdinalIgnoreCase))
        {
            opts.EnabledPhases |= ScrapePhase.SoloRivals;
        }
        else if (args[i].Equals("--solo-player-stats", StringComparison.OrdinalIgnoreCase))
        {
            opts.EnabledPhases |= ScrapePhase.SoloPlayerStats;
        }
        else if (args[i].Equals("--solo-precompute", StringComparison.OrdinalIgnoreCase))
        {
            opts.EnabledPhases |= ScrapePhase.SoloPrecompute;
        }
        else if (args[i].Equals("--solo-finalize", StringComparison.OrdinalIgnoreCase))
        {
            opts.EnabledPhases |= ScrapePhase.SoloFinalize;
        }
        else if (args[i].Equals("--band-scrape", StringComparison.OrdinalIgnoreCase))
        {
            opts.EnabledPhases |= ScrapePhase.BandScrape;
        }
        else if (args[i].Equals("--band-post-scrape", StringComparison.OrdinalIgnoreCase))
        {
            opts.EnabledPhases |= ScrapePhase.BandScrapePhase;
        }
        else if (args[i].Equals("--band-extraction", StringComparison.OrdinalIgnoreCase))
        {
            opts.EnabledPhases |= ScrapePhase.BandExtraction;
        }
    }
});

var apiSettings = builder.Configuration
    .GetSection(ApiSettings.Section)
    .Get<ApiSettings>() ?? new ApiSettings();

static HttpMessageHandler CreateEpicLeaderboardHandler(IServiceProvider sp, int maxConnectionsPerServer)
{
    var proxyPool = sp.GetRequiredService<ProxyPool>();
    if (proxyPool.IsEnabled)
        return new ProxyRoutingHttpMessageHandler(proxyPool);

    return new SocketsHttpHandler
    {
        MaxConnectionsPerServer = maxConnectionsPerServer,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        EnableMultipleHttp2Connections = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    };
}

// ─── HTTP clients ───────────────────────────────────────────

builder.Services.AddHttpClient<EpicAuthService>()
    .ConfigureHttpClient(c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan);

builder.Services.AddHttpClient(nameof(GlobalLeaderboardScraper))
    .ConfigureHttpClient(c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(sp => CreateEpicLeaderboardHandler(sp, maxConnectionsPerServer: 2048));

// Accessor that lets endpoints reach the active RoundRobinProxyHandler without a full DI
// refactor. Null when proxy rotation is disabled (e.g. tests, single-proxy configs).
builder.Services.AddSingleton<ProxyHandlerAccessor>();
builder.Services.AddSingleton<EpicTrafficCoordinator>();

builder.Services.AddSingleton<ILeaderboardQuerier>(sp => sp.GetRequiredService<GlobalLeaderboardScraper>());

// Promote GlobalLeaderboardScraper to singleton so the diagnostic endpoint
// (/api/diag/inflight) and scrape orchestrator resolve the SAME instance —
// otherwise AddHttpClient<T>'s default transient registration yields a fresh
// scraper per resolution and the endpoint's in-flight dictionary is empty.
builder.Services.AddSingleton<GlobalLeaderboardScraper>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var http = factory.CreateClient(nameof(GlobalLeaderboardScraper));
    var progress = sp.GetRequiredService<FSTService.Scraping.ScrapeProgressTracker>();
    var log = sp.GetRequiredService<ILogger<GlobalLeaderboardScraper>>();
    var festival = sp.GetService<FortniteFestival.Core.Services.FestivalService>();
    var trafficCoordinator = sp.GetRequiredService<EpicTrafficCoordinator>();
    var proxyHealth = sp.GetRequiredService<IProxyHealthReporter>();
    return new GlobalLeaderboardScraper(
        http,
        progress,
        log,
        festivalService: festival,
        trafficCoordinator: trafficCoordinator,
        proxyHealth: proxyHealth);
});

builder.Services.AddHttpClient<AccountNameResolver>()
    .ConfigureHttpClient(c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
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
var pgConnectionStringBuilder = new NpgsqlConnectionStringBuilder(pgConnStr)
{
    ApplicationName = soloFamilyRankingBackfillCommand is not null
        ? "fstservice-solo-family-backfill"
        : hostedWorkerMode switch
    {
        HostedWorkerMode.FullWorker => "fstworker-scraper",
        HostedWorkerMode.RegistrationSyncWorker => "fstworker-registration",
        HostedWorkerMode.ApiOnly => "fstservice-api",
        HostedWorkerMode.FrontendOnly => "fstservice-frontend",
        _ => "fstservice",
    },
};
if (rolloutPostgresReadOnlyRequested)
{
    const string readOnlyOption = "-c default_transaction_read_only=on";
    pgConnectionStringBuilder.Options = string.IsNullOrWhiteSpace(
        pgConnectionStringBuilder.Options)
        ? readOnlyOption
        : $"{pgConnectionStringBuilder.Options} {readOnlyOption}";
}
var pgDataSource = NpgsqlDataSource.Create(pgConnectionStringBuilder.ConnectionString);
builder.Services.AddSingleton(pgDataSource);
builder.Services.AddSingleton(sp =>
    PostgresRuntimeTarget.FromConnectionString(
        sp.GetRequiredService<NpgsqlDataSource>().ConnectionString));

builder.Services.AddSingleton<IMetaDatabase>(sp =>
    new FSTService.Persistence.MetaDatabase(sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<ILogger<FSTService.Persistence.MetaDatabase>>(),
        sp.GetRequiredService<IOptions<BandRankHistoryOptions>>(),
        sp.GetRequiredService<IOptions<PublicationCommitOptions>>()));
builder.Services.AddSingleton(sp => (FSTService.Persistence.MetaDatabase)sp.GetRequiredService<IMetaDatabase>());

builder.Services.AddSingleton<IPathDataStore>(sp =>
    new FSTService.Scraping.PathDataStore(sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<ILogger<FSTService.Scraping.PathDataStore>>()));
builder.Services.AddSingleton(sp => (FSTService.Scraping.PathDataStore)sp.GetRequiredService<IPathDataStore>());

builder.Services.AddSingleton<FSTService.Api.DbStatsService>();
builder.Services.AddSingleton<FSTService.Exports.PlayerDataExportService>();
builder.Services.AddSingleton<FSTService.Scraping.DurablePhaseProgressSink>();
builder.Services.AddSingleton<FSTService.Scraping.WorkerStatusPublisher>();
builder.Services.AddSingleton<FSTService.Persistence.Maintenance.IDatabasePressureMonitor, FSTService.Persistence.Maintenance.DatabasePressureMonitor>();
builder.Services.AddSingleton<FSTService.Persistence.Maintenance.DatabaseMaintenanceDryRunReporter>();
builder.Services.AddSingleton<FSTService.Persistence.Maintenance.IDatabaseRetentionMaintenanceService, FSTService.Persistence.Maintenance.DatabaseRetentionMaintenanceService>();
builder.Services.AddSingleton<FSTService.Persistence.Maintenance.DeferredRetentionMaintenanceRunner>();
builder.Services.AddSingleton<FSTService.Persistence.ImprovementNotificationService>();
builder.Services.AddSingleton<FSTService.Persistence.ImprovementNotificationRecoveryService>();
builder.Services.AddSingleton<FSTService.Persistence.ScoreHistoryDedupMaintenanceService>();
builder.Services.AddSingleton<SoloFamilyRankingBackfillService>();

// ─── Shared services ────────────────────────────────────────

builder.Services.AddSingleton<GlobalLeaderboardPersistence>(sp =>
{
    return new GlobalLeaderboardPersistence(
        sp.GetRequiredService<IMetaDatabase>(),
        sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<ILogger<GlobalLeaderboardPersistence>>(),
        sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<IOptions<FeatureOptions>>());
});

builder.Services.AddSingleton<BackfillQueue>();
builder.Services.AddSingleton<AccountNameRefreshService>();
builder.Services.AddSingleton<ScoreBackfiller>();
builder.Services.AddSingleton<PostScrapeRefresher>();
builder.Services.AddSingleton<BatchResultProcessor>();
builder.Services.AddSingleton<SongProcessingMachine>();
builder.Services.AddSingleton<CyclicalSongMachine>();
builder.Services.AddSingleton<SharedDopPool>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<ScraperOptions>>().Value;
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("SharedDopPool");
    int dop = opts.DegreeOfParallelism;
    int initialDop = Math.Clamp(opts.InitialDop, 4, dop);
    return new SharedDopPool(initialDop, minDop: Math.Min(initialDop, dop), maxDop: dop,
        opts.LowPriorityPercent, log, opts.MaxRequestsPerSecond,
        trafficCoordinator: sp.GetRequiredService<EpicTrafficCoordinator>());
});
builder.Services.AddSingleton<FirstSeenSeasonCalculator>();
builder.Services.AddSingleton<FSTService.Api.NotificationService>();
builder.Services.AddSingleton<FSTService.Scraping.UserSyncProgressTracker>();
builder.Services.AddSingleton(sp =>
    new FSTService.Api.SongsCacheService(
        sp.GetRequiredService<FSTService.Api.PublicReadGateService>(),
        () => sp
            .GetRequiredService<FSTService.Api.PublicationReadContextService>()
            .GetPointers()
            .CurrentPublicationId));
builder.Services.AddSingleton<FSTService.Api.ShopCacheService>();
builder.Services.AddSingleton<
    FSTService.Api.PublicationRecoveryCoordinator>();
builder.Services.AddSingleton<
    FSTService.Api.IPublicationRecoveryCoordinator>(
    sp => sp.GetRequiredService<
        FSTService.Api.PublicationRecoveryCoordinator>());
builder.Services.AddSingleton<FSTService.Api.PublicReadGateService>();
builder.Services.AddSingleton(sp =>
    new FSTService.Api.PublicationReadLockDataSource(
        pgConnectionStringBuilder.ConnectionString));
builder.Services.AddSingleton<FSTService.Api.PublicationReadContextService>(sp =>
    new FSTService.Api.PublicationReadContextService(
        sp.GetRequiredService<IMetaDatabase>(),
        sp.GetRequiredService<FSTService.Api.PublicationReadLockDataSource>(),
        sp.GetRequiredService<IOptions<FeatureOptions>>(),
        sp.GetRequiredService<IOptions<PublicationCommitOptions>>()));
builder.Services.AddSingleton<FSTService.Api.PublicApiCacheTelemetry>();
builder.Services.AddSingleton<FSTService.Api.RolloutReadOnlyViolationMonitor>();
builder.Services.AddKeyedSingleton<FSTService.Api.ResponseCacheService>("PlayerCache",
    (sp, _) => new FSTService.Api.ResponseCacheService(TimeSpan.FromMinutes(2),
        sp.GetRequiredService<FSTService.Api.PublicReadGateService>(),
        publicationIdProvider: () => sp
            .GetRequiredService<FSTService.Api.PublicationReadContextService>()
            .GetPointers()
            .CurrentPublicationId));
builder.Services.AddKeyedSingleton<FSTService.Api.ResponseCacheService>("LeaderboardAllCache",
    (sp, _) => new FSTService.Api.ResponseCacheService(TimeSpan.FromMinutes(5),
        sp.GetRequiredService<FSTService.Api.PublicReadGateService>(),
        publicationIdProvider: () => sp
            .GetRequiredService<FSTService.Api.PublicationReadContextService>()
            .GetPointers()
            .CurrentPublicationId));
builder.Services.AddKeyedSingleton<FSTService.Api.ResponseCacheService>("NeighborhoodCache",
    (sp, _) => new FSTService.Api.ResponseCacheService(TimeSpan.FromMinutes(2),
        sp.GetRequiredService<FSTService.Api.PublicReadGateService>(),
        publicationIdProvider: () => sp
            .GetRequiredService<FSTService.Api.PublicationReadContextService>()
            .GetPointers()
            .CurrentPublicationId));
builder.Services.AddKeyedSingleton<FSTService.Api.ResponseCacheService>("RivalsCache",
    (sp, _) => new FSTService.Api.ResponseCacheService(TimeSpan.FromMinutes(5),
        sp.GetRequiredService<FSTService.Api.PublicReadGateService>(),
        requireCachedReadsWhenFrozen: true,
        publicationIdProvider: () => sp
            .GetRequiredService<FSTService.Api.PublicationReadContextService>()
            .GetPointers()
            .CurrentPublicationId));
builder.Services.AddSingleton<RivalsCalculator>();
builder.Services.AddSingleton<RivalsOrchestrator>();
builder.Services.AddSingleton<LeaderboardRivalsCalculator>();
builder.Services.AddKeyedSingleton<FSTService.Api.ResponseCacheService>("LeaderboardRivalsCache",
    (sp, _) => new FSTService.Api.ResponseCacheService(TimeSpan.FromMinutes(5),
        sp.GetRequiredService<FSTService.Api.PublicReadGateService>(),
        requireCachedReadsWhenFrozen: true,
        publicationIdProvider: () => sp
            .GetRequiredService<FSTService.Api.PublicationReadContextService>()
            .GetPointers()
            .CurrentPublicationId));
builder.Services.AddSingleton<ScrapeLifecycleNotifier>();
builder.Services.AddSingleton<BackgroundWorkCoordinator>();
builder.Services.AddSingleton<RankingsCalculator>();
builder.Services.AddSingleton<ScrapeOrchestrator>();
builder.Services.AddSingleton<PostScrapeOrchestrator>();
builder.Services.AddSingleton<BandScrapePhase>();
builder.Services.AddSingleton<BandLeaderboardPersistence>();
builder.Services.AddSingleton<IRegisteredPlayerBandDiscoveryStrategy, DirectRegisteredPlayerBandDiscoveryStrategy>();
builder.Services.AddSingleton<RegisteredPlayerBandDiscoveryOrchestrator>(sp =>
{
    var scraper = sp.GetRequiredService<ILeaderboardQuerier>();
    var executor = (scraper as GlobalLeaderboardScraper)?.Executor;
    return new RegisteredPlayerBandDiscoveryOrchestrator(
        sp.GetRequiredService<IMetaDatabase>(),
        sp.GetRequiredService<BandLeaderboardPersistence>(),
        sp.GetRequiredService<IRegisteredPlayerBandDiscoveryStrategy>(),
        sp.GetRequiredService<ScrapeProgressTracker>(),
        sp.GetRequiredService<IOptions<ScraperOptions>>(),
        sp.GetRequiredService<ILogger<RegisteredPlayerBandDiscoveryOrchestrator>>(),
        executor);
});
builder.Services.AddSingleton<IRegisteredBandLookupStrategy, DirectRegisteredBandLookupStrategy>();
builder.Services.AddSingleton<RegisteredBandProcessingOrchestrator>(sp =>
{
    var scraper = sp.GetRequiredService<ILeaderboardQuerier>();
    var executor = (scraper as GlobalLeaderboardScraper)?.Executor;
    return new RegisteredBandProcessingOrchestrator(
        sp.GetRequiredService<IMetaDatabase>(),
        sp.GetRequiredService<BandLeaderboardPersistence>(),
        sp.GetRequiredService<IRegisteredBandLookupStrategy>(),
        sp.GetRequiredService<ScrapeProgressTracker>(),
        sp.GetRequiredService<IOptions<ScraperOptions>>(),
        sp.GetRequiredService<ILogger<RegisteredBandProcessingOrchestrator>>(),
        executor);
});
builder.Services.AddSingleton<BandSearchProjectionBuilder>();
builder.Services.AddSingleton<SoloCurrentProjectionBuilder>();
builder.Services.AddSingleton<BandCurrentProjectionBuilder>();
builder.Services.AddSingleton<PostScrapeBandExtractor>();
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
        sp.GetRequiredService<ILoggerFactory>(),
        jsonOpts,
        sp.GetRequiredService<IOptions<FeatureOptions>>().Value,
        sp.GetRequiredService<IOptions<ScraperOptions>>().Value,
        sp.GetRequiredService<LeaderboardRivalsCalculator>(),
        sp.GetRequiredService<SoloCurrentProjectionBuilder>());
});

builder.Services.AddHttpClient<ItemShopService>()
    .ConfigureHttpClient(c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
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
            sp.GetRequiredService<ImprovementNotificationService>(),
            sp.GetRequiredService<ILogger<ItemShopService>>())
        : throw new InvalidOperationException());


builder.Services.AddHttpClient(nameof(HistoryReconstructor))
    .ConfigureHttpClient(c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(sp => CreateEpicLeaderboardHandler(sp, maxConnectionsPerServer: 32))
    .AddTypedClient((http, sp) => new HistoryReconstructor(
        sp.GetRequiredService<ILeaderboardQuerier>(),
        sp.GetRequiredService<GlobalLeaderboardPersistence>(),
        http,
        sp.GetRequiredService<ScrapeProgressTracker>(),
        sp.GetRequiredService<UserSyncProgressTracker>(),
        sp.GetRequiredService<ILogger<HistoryReconstructor>>(),
        sp.GetRequiredService<IProxyHealthReporter>()));

// ─── Path Generation ────────────────────────────────────────

builder.Services.AddHttpClient("PathGeneration")
    .ConfigureHttpClient(c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 8,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });
builder.Services.AddSingleton<IPathGenerationAdmissionLeaseProvider>(sp =>
    new PostgresPathGenerationAdmissionLeaseProvider(
        pgConnectionStringBuilder.ConnectionString,
        sp.GetRequiredService<
            ILogger<PostgresPathGenerationAdmissionLeaseProvider>>()));
builder.Services.AddSingleton<PathGenerationCoordinator>(sp =>
    new PathGenerationCoordinator(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("PathGeneration"),
        sp.GetRequiredService<IPathDataStore>(),
        sp.GetRequiredService<SongsCacheService>(),
        sp.GetRequiredService<IOptions<ScraperOptions>>(),
        sp.GetRequiredService<ScrapeProgressTracker>(),
        sp.GetRequiredService<ILogger<PathGenerationCoordinator>>(),
        sp.GetRequiredService<IPathGenerationAdmissionLeaseProvider>()));
builder.Services.AddSingleton<PathArtifactResolver>();

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
if (soloFamilyRankingBackfillCommand is not null
    || leaderboardRivalsRecomputeCommand is not null)
{
    builder.Services.AddHealthChecks();
}
else
{
    builder.Services.AddHostedService(
        sp => sp.GetRequiredService<StartupInitializer>());
    if (hostedServicePlan.RegisterStalenessMonitor)
    {
        builder.Services.AddHostedService<
            FSTService.Persistence.ImprovementNotificationStalenessMonitor>();
    }
    if (hostedServicePlan.RegisterPublicationChangeMonitor)
    {
        builder.Services.AddHostedService<
            FSTService.Api.PublicationChangeMonitorService>();
    }
    builder.Services.AddHealthChecks()
        .AddCheck<StartupInitializer>("database", tags: ["ready"]);
    if (hostedServicePlan.RegisterFullWorkerServices)
    {
        builder.Services.AddHostedService<DurablePhaseProgressBridgeService>();
        builder.Services.AddHostedService<WorkerStatusHeartbeatService>();
        builder.Services.AddHostedService<ScraperWorker>();
        if (hostedServicePlan.RegisterRegistrationBackfill)
        {
            builder.Services.AddHostedService<RegistrationBackfillWorker>();
        }
        builder.Services.AddHostedService<BandRankHistoryWorker>();
    }
    else
    {
        if (hostedServicePlan.RegisterSongCatalogRefresh)
        {
            builder.Services.AddHostedService<SongCatalogRefreshWorker>();
        }
        if (hostedServicePlan.RegisterRegistrationBackfill)
        {
            builder.Services.AddHostedService<RegistrationBackfillWorker>();
        }
    }
}

// ─── Build and configure pipeline ───────────────────────────

var app = builder.Build();

if (soloFamilyRankingBackfillCommand is not null)
{
    app.Logger.LogInformation(
        "Solo-family ranking backfill one-shot mode enabled; no hosted services were registered and schema initialization will not run.");
}
else if (leaderboardRivalsRecomputeCommand is not null)
{
    app.Logger.LogInformation(
        "Leaderboard-rivals recompute one-shot mode enabled; no hosted services were registered.");
}
else if (rolloutReadOnlyStartupRequested)
{
    app.Logger.LogWarning(
        "Rollout read-only startup mode enabled; only persisted state loading and HTTP serving are active.");
}
else if (hostedWorkerMode == HostedWorkerMode.ApiOnly)
{
    app.Logger.LogInformation("API-only mode enabled; scraper hosted services were not registered. Song catalog refresh remains active.");
}
else if (hostedWorkerMode == HostedWorkerMode.FrontendOnly)
{
    app.Logger.LogInformation("API frontend mode enabled; scraper and mutation background hosted services were not registered. Song catalog refresh remains active.");
}
else if (hostedWorkerMode == HostedWorkerMode.RegistrationSyncWorker)
{
    app.Logger.LogInformation("Registration sync worker mode enabled; scheduled scrape and band rank-history workers were not registered. Song catalog refresh and registration sync remain active.");
}

if (initializeSchemaOnlyRequested)
{
    var schemaLog = app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("SchemaInitialization");
    schemaLog.LogInformation(
        "--initialize-schema-only: applying idempotent database schema...");
    await FSTService.Persistence.DatabaseInitializer.EnsureSchemaAsync(
        app.Services.GetRequiredService<NpgsqlDataSource>());
    schemaLog.LogInformation(
        "--initialize-schema-only: database schema is current. Exiting.");
    return;
}

// Explicit one-shot score_history null-timestamp dedup maintenance.
if (scoreHistoryDedupMaintenanceCommand is not null)
{
    var maintenance = app.Services.GetRequiredService<
        FSTService.Persistence.ScoreHistoryDedupMaintenanceService>();
    object report = scoreHistoryDedupMaintenanceCommand.Execute
        ? await maintenance.ExecuteAsync(
            scoreHistoryDedupMaintenanceCommand.ExpectedDigest!,
            CancellationToken.None)
        : await maintenance.DryRunAsync(CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
    if (report is ScoreHistoryDedupDryRunReport { CanExecute: false })
        Environment.ExitCode = 2;
    return;
}

// Explicit one-shot solo family ranking rebuild. Dry run is the default.
if (soloFamilyRankingBackfillCommand is not null)
{
    var backfill = app.Services.GetRequiredService<
        SoloFamilyRankingBackfillService>();
    var report = backfill.Rebuild(
        soloFamilyRankingBackfillCommand.Execute);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
    if (report.InvalidRowCount > 0)
        Environment.ExitCode = 2;
    return;
}

if (leaderboardRivalsRecomputeCommand is not null)
{
    var recomputeDataSource = app.Services.GetRequiredService<NpgsqlDataSource>();
    var persistence = app.Services
        .GetRequiredService<GlobalLeaderboardPersistence>();
    persistence.InitializeReadOnly();
    var metaDb = app.Services.GetRequiredService<IMetaDatabase>();
    var pointers = metaDb.GetPublicationPointerState();
    var currentPublicationId = pointers.CurrentPublicationId
        ?? throw new InvalidOperationException(
            "Leaderboard-rivals recompute requires a current publication.");
    using var maintenanceLease =
        metaDb.AcquireCurrentPublicationMaintenanceLease(
            currentPublicationId);
    await using (var schemaConnection =
        await recomputeDataSource.OpenConnectionAsync())
    await using (var schemaCheck = schemaConnection.CreateCommand())
    {
        schemaCheck.CommandText =
            "SELECT to_regclass('public.leaderboard_rivals_state') IS NOT NULL";
        if (await schemaCheck.ExecuteScalarAsync() is not true)
        {
            throw new InvalidOperationException(
                "Leaderboard-rivals recompute requires current release schema. Run --initialize-schema-only first.");
        }
    }
    var freeze = metaDb.GetPublicReadFreezeState();
    if (freeze.IsFrozen || pointers.WorkingPublicationId.HasValue)
    {
        throw new InvalidOperationException(
            "Leaderboard-rivals recompute requires unfrozen reads and no working publication.");
    }

    IDisposable? projectionReadPass = null;
    if (persistence.UseSnapshotOverlayWorkerReaders)
    {
        var projection = app.Services
            .GetRequiredService<SoloCurrentProjectionBuilder>();
        await projection.EnsureSchemaAsync();
        var staleScopes = await projection.LoadStaleScopesAsync();
        if (staleScopes.Count > 0
            || await projection.HasOrphanedProjectionScopesAsync())
        {
            throw new InvalidOperationException(
                $"Leaderboard-rivals recompute requires a current solo projection; stale={staleScopes.Count}.");
        }

        projectionReadPass =
            persistence.BeginValidatedCurrentProjectionReadPass();
        persistence.SetValidatedCurrentProjectionForWorkerReaders(true);
    }
    using (projectionReadPass)
    {
        var calculator = app.Services
            .GetRequiredService<LeaderboardRivalsCalculator>();
        var calculation = calculator.ComputeForUser(
            leaderboardRivalsRecomputeCommand.AccountId,
            rankingsAuthoritative: true);
        var precomputer = app.Services
            .GetRequiredService<ScrapeTimePrecomputer>();
        var entries = new List<(string Key, byte[] Json, string ETag)>();
        precomputer.PrecomputePlayerLeaderboardRivals(
            leaderboardRivalsRecomputeCommand.AccountId,
            persistence.GetInstrumentKeys(),
            metaDb.GetDisplayNames(
                [leaderboardRivalsRecomputeCommand.AccountId]),
            allowLiveFallback: false,
            storeOverride: entries);
        metaDb.BulkSetCachedResponses(entries, currentPublicationId);

        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
            new LeaderboardRivalsRecomputeReport(
                leaderboardRivalsRecomputeCommand.AccountId,
                calculation.RivalCount,
                calculation.SampleCount,
                entries.Count)));
    }
    return;
}

// One-shot precompute: --precompute
{
    var scraperOpts2 = app.Services.GetRequiredService<IOptions<ScraperOptions>>().Value;
    if (scraperOpts2.PrecomputeOnly)
    {
        var precompLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Precompute");
        var pgDs = app.Services.GetRequiredService<NpgsqlDataSource>();

        precompLog.LogInformation("--precompute: ensuring database schema...");
        await FSTService.Persistence.DatabaseInitializer.EnsureSchemaAsync(pgDs);

        // Minimal init: open instrument DBs + load song catalog (skip Item Shop HTTP calls)
        precompLog.LogInformation("--precompute: initializing databases...");
        var precompPersistence = app.Services.GetRequiredService<GlobalLeaderboardPersistence>();
        precompPersistence.Initialize();
        var precompFestivalService = app.Services.GetRequiredService<FestivalService>();
        await precompFestivalService.InitializeAsync();
        precompLog.LogInformation("--precompute: {SongCount} songs loaded, DBs ready.",
            precompFestivalService.Songs.Count);

        var soloProjection = app.Services.GetRequiredService<SoloCurrentProjectionBuilder>();
        await soloProjection.EnsureSchemaAsync();
        var projectionStats = soloProjection.Inspect();
        var staleScopes = await soloProjection.LoadStaleScopesAsync();
        if (!projectionStats.ProjectionExists
            || projectionStats.ScopeCount == 0
            || projectionStats.FailedScopeCount > 0
            || staleScopes.Count > 0)
        {
            throw new InvalidOperationException(
                "--precompute requires a complete, current solo projection " +
                $"(exists={projectionStats.ProjectionExists}, scopes={projectionStats.ScopeCount}, " +
                $"failed={projectionStats.FailedScopeCount}, stale={staleScopes.Count}).");
        }

        precompLog.LogInformation("--precompute: running precomputation...");
        var precomputer = app.Services.GetRequiredService<ScrapeTimePrecomputer>();
        await precomputer.PrecomputeAllAsync(CancellationToken.None);

        precompLog.LogInformation("--precompute: precomputed responses persisted to PostgreSQL. Exiting.");
        return;
    }
}

// One-shot published-scrape improvement notification recovery.
if (improvementNotificationRecoveryRequested)
{
    var recoveryLog = app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("ImprovementNotificationRecovery");
    var pgDs = app.Services.GetRequiredService<NpgsqlDataSource>();
    await FSTService.Persistence.DatabaseInitializer.EnsureSchemaAsync(pgDs);

    long? expectedPublishedScrapeId = null;
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].Equals("--published-scrape-id", StringComparison.OrdinalIgnoreCase))
            continue;

        if (i + 1 >= args.Length || !long.TryParse(args[i + 1], out var parsedScrapeId) || parsedScrapeId <= 0)
            throw new ArgumentException("--published-scrape-id requires a positive integer.");

        expectedPublishedScrapeId = parsedScrapeId;
        break;
    }

    var execute = !args.Any(arg => arg.Equals("--notification-dry-run", StringComparison.OrdinalIgnoreCase));
    var baselineOnly = args.Any(arg => arg.Equals("--notification-baseline-only", StringComparison.OrdinalIgnoreCase));
    var refreshProjection = !args.Any(
        arg => arg.Equals("--notification-skip-projection-refresh", StringComparison.OrdinalIgnoreCase));
    var force = args.Any(arg => arg.Equals("--notification-force", StringComparison.OrdinalIgnoreCase));

    recoveryLog.LogInformation(
        "Recovering improvement notifications for published scrape {ExpectedScrapeId}; execute={Execute}, baselineOnly={BaselineOnly}, refreshProjection={RefreshProjection}.",
        expectedPublishedScrapeId,
        execute,
        baselineOnly,
        refreshProjection);

    var recovery = app.Services.GetRequiredService<FSTService.Persistence.ImprovementNotificationRecoveryService>();
    var report = await recovery.RunPublishedScrapeAsync(
        expectedPublishedScrapeId,
        execute,
        baselineOnly,
        refreshProjection,
        projectionScopes: null,
        force,
        source: "operator-recovery",
        CancellationToken.None);

    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
    return;
}

// Security: block path traversal attempts first
app.UseMiddleware<PathTraversalGuardMiddleware>();

app.UseResponseCompression();
app.UseMiddleware<FSTService.Api.RolloutReadOnlyRequestGuardMiddleware>();

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
notificationService.SetSyncTracker(app.Services.GetRequiredService<UserSyncProgressTracker>());
notificationService.SetMetaDatabase(app.Services.GetRequiredService<IMetaDatabase>());

app.UseCors();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30),
});
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<FSTService.Api.PublicApiResponseCacheMiddleware>();
app.UseMiddleware<FSTService.Api.PublicationReadContextMiddleware>();
app.UseMiddleware<FSTService.Api.PublicationBoundaryReadLeaseMiddleware>();
app.UseMiddleware<FSTService.Api.PublicReadGateMiddleware>();
app.UseMiddleware<FSTService.Api.SelectedProfileActivityMiddleware>();

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

    // Keep retired or misspelled API routes as real 404s instead of returning
    // the embedded SPA shell with a misleading 200 response.
    app.MapFallback("/api/{**path}", () => Results.NotFound())
        .WithApiPublicationClassification(ApiPublicationRouteCatalog.AnyMethod, "/api/{**path}");

    // Fallback: serve index.html for non-API routes (SPA support).
    app.MapFallbackToFile("index.html");
}

ApiPublicationEndpointDescriptions.Validate(
    app.Services.GetRequiredService<EndpointDataSource>());

app.Run();

// Enable WebApplicationFactory<Program> for integration testing
public partial class Program { }
