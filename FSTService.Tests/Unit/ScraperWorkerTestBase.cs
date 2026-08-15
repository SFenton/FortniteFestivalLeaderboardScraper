using System.Reflection;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

/// <summary>
/// Shared infrastructure for ScraperWorker test classes.
/// Each subclass gets its own instance per test (xUnit creates a new instance per [Fact]).
/// Splitting into multiple classes enables xUnit's default cross-class parallelism.
/// </summary>
public abstract class ScraperWorkerTestBase : IDisposable
{
    protected readonly string _tempDir;
    protected readonly InMemoryMetaDatabase _metaFixture = new();
    protected readonly MetaDatabase _metaDb;
    protected readonly GlobalLeaderboardPersistence _persistence;

    protected readonly TokenManager _tokenManager;
    protected readonly GlobalLeaderboardScraper _scraper;
    protected readonly AccountNameResolver _nameResolver;
    protected readonly ScoreBackfiller _backfiller;
    protected readonly BackfillQueue _backfillQueue;
    protected readonly SongProcessingMachine _machine;
    protected readonly CyclicalSongMachine _cyclicalMachine;
    protected readonly SharedDopPool _pool;
    protected readonly HistoryReconstructor _historyReconstructor;
    protected readonly FirstSeenSeasonCalculator _firstSeenCalculator;
    protected readonly FestivalService _festivalService;
    protected readonly ScrapeProgressTracker _progress;
    protected readonly IHostApplicationLifetime _lifetime;
    protected readonly ILogger<ScraperWorker> _log;
    private MetaDatabase? _shopMetaDb;

    protected ScraperWorkerTestBase()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sw_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _metaDb = new MetaDatabase(_metaFixture.DataSource,
            Substitute.For<ILogger<MetaDatabase>>());

        _persistence = new GlobalLeaderboardPersistence(
            _metaDb,
            Substitute.For<ILoggerFactory>(),
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>(),
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions()));
        _persistence.Initialize();

        var noOpHandler = new NoOpHttpHandler();
        var dummyHttp = new HttpClient(noOpHandler);
        var epicAuth = new EpicAuthService(dummyHttp, Substitute.For<ILogger<EpicAuthService>>());

        _tokenManager = Substitute.For<TokenManager>(
            epicAuth,
            Substitute.For<ICredentialStore>(),
            Substitute.For<ILogger<TokenManager>>());

        _scraper = Substitute.For<GlobalLeaderboardScraper>(
            new HttpClient(),
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<GlobalLeaderboardScraper>>(),
            0,
            null);

        _nameResolver = Substitute.For<AccountNameResolver>(
            new HttpClient(), _metaDb, _tokenManager,
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<AccountNameResolver>>());

        _backfiller = Substitute.For<ScoreBackfiller>(
            _scraper, _persistence, new ScrapeProgressTracker(),
            new UserSyncProgressTracker(new NotificationService(NullLogger<NotificationService>.Instance), NullLogger<UserSyncProgressTracker>.Instance),
            Substitute.For<ILogger<ScoreBackfiller>>());

        _backfillQueue = new BackfillQueue();

        _progress = new ScrapeProgressTracker();

        _machine = Substitute.For<SongProcessingMachine>(
            (ILeaderboardQuerier)_scraper,
            new BatchResultProcessor(_persistence, Substitute.For<ILogger<BatchResultProcessor>>()),
            _persistence, _progress,
            new UserSyncProgressTracker(new NotificationService(NullLogger<NotificationService>.Instance), NullLogger<UserSyncProgressTracker>.Instance),
            Substitute.For<ILogger<SongProcessingMachine>>(),
            (ResilientHttpExecutor?)null);
        _machine.RunAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<UserWorkItem>>(),
            Arg.Any<IReadOnlyList<Persistence.SeasonWindowInfo>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SharedDopPool>(),
            Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new SongProcessingMachine.MachineResult());

        _pool = new SharedDopPool(16, minDop: 2, maxDop: 64, lowPriorityPercent: 20, Substitute.For<ILogger>());

        _cyclicalMachine = CreateMockCyclicalMachine();

        _historyReconstructor = Substitute.For<HistoryReconstructor>(
            _scraper, _persistence, new HttpClient(), _progress,
            new UserSyncProgressTracker(new NotificationService(NullLogger<NotificationService>.Instance), NullLogger<UserSyncProgressTracker>.Instance),
            Substitute.For<ILogger<HistoryReconstructor>>());

        _firstSeenCalculator = Substitute.For<FirstSeenSeasonCalculator>(
            _scraper, _persistence, _progress,
            Substitute.For<ILogger<FirstSeenSeasonCalculator>>());

        _festivalService = CreateServiceWithSongs(("test-song-1", "Test Song", "Test Artist"));

        _lifetime = Substitute.For<IHostApplicationLifetime>();
        _log = Substitute.For<ILogger<ScraperWorker>>();
    }

    public void Dispose()
    {
        _shopMetaDb?.Dispose();
        _persistence.Dispose();
        _metaDb.Dispose();
        _metaFixture.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    protected ScraperWorker CreateWorker(
        ScraperOptions? opts = null,
        PublicationCommitOptions? publicationCommitOptions = null)
        => CreateWorkerWithHttp(
            opts,
            null,
            publicationCommitOptions);

    protected ScraperWorker CreateWorkerWithHttp(
        ScraperOptions? opts,
        HttpMessageHandler? httpHandler,
        PublicationCommitOptions?
            publicationCommitOptions = null)
    {
        opts ??= new ScraperOptions
        {
            DataDirectory = _tempDir,
            DeviceAuthPath = Path.Combine(_tempDir, "device.json"),
        };
        opts.EnabledPhases = opts.EnabledPhases == ScrapePhase.None
            ? ScrapePhase.SoloScrape
            : opts.EnabledPhases;
        var options = Options.Create(opts);

        var http = httpHandler != null ? new HttpClient(httpHandler) : new HttpClient();
        var pathDataStore = new PathDataStore(SharedPostgresContainer.CreateDatabase());
        var songsCache = new Api.SongsCacheService();
        var pathGeneration = new PathGenerationCoordinator(
            http,
            pathDataStore,
            songsCache,
            options,
            _progress,
            Substitute.For<ILogger<PathGenerationCoordinator>>(),
            UncontendedPathGenerationAdmissionLeaseProvider.Instance);

        var notifications = new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>());

        var rivalsCalculator = new RivalsCalculator(_persistence, Substitute.For<ILogger<RivalsCalculator>>());
        var rivalsOrchestrator = new RivalsOrchestrator(rivalsCalculator, _persistence, new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), _progress, new UserSyncProgressTracker(new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), new Api.ResponseCacheService(TimeSpan.FromMinutes(5)), Substitute.For<ILogger<RivalsOrchestrator>>());

        var rankingsCalculator = new RankingsCalculator(_persistence, _persistence.Meta, pathDataStore, _progress, Substitute.For<ILogger<RankingsCalculator>>());
        var leaderboardRivalsCalculator = new LeaderboardRivalsCalculator(_persistence, _persistence.Meta, options, Substitute.For<ILogger<LeaderboardRivalsCalculator>>());

        var precomputer = new ScrapeTimePrecomputer(_persistence, _persistence.Meta, pathDataStore, _progress, Substitute.For<ILogger<ScrapeTimePrecomputer>>(), NullLoggerFactory.Instance, new System.Text.Json.JsonSerializerOptions(), new FeatureOptions());

        var bandPersistence = new BandLeaderboardPersistence(
            _metaFixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());
        var improvementNotificationOptions = Options.Create(
            new ImprovementNotificationOptions
            {
                Enabled = true,
                IncludePlayers = false,
                IncludeBands = false,
                IncludeSongEvents = false,
                IncludeRankings = false,
                RefreshSoloProjection = false,
            });
        var improvementNotifications = new ImprovementNotificationService(
            _metaFixture.DataSource,
            Substitute.For<ILogger<ImprovementNotificationService>>());
        var improvementNotificationRecovery = new ImprovementNotificationRecoveryService(
            improvementNotifications,
            new SoloCurrentProjectionBuilder(
                _metaFixture.DataSource,
                Substitute.For<ILogger<SoloCurrentProjectionBuilder>>()),
            improvementNotificationOptions,
            Substitute.For<ILogger<ImprovementNotificationRecoveryService>>());

        var postScrapeOrchestrator = new PostScrapeOrchestrator(
            _persistence, _firstSeenCalculator, _nameResolver,
            _historyReconstructor,
            _pool,
            _cyclicalMachine,
            rivalsOrchestrator, rankingsCalculator, leaderboardRivalsCalculator, notifications,
            _tokenManager, _progress, new FSTService.Scraping.UserSyncProgressTracker(new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), Substitute.For<ILogger<FSTService.Scraping.UserSyncProgressTracker>>()), pathDataStore, precomputer,
            new PostScrapeBandExtractor(_metaFixture.DataSource, pathDataStore, Substitute.For<ILogger<PostScrapeBandExtractor>>()),
            new BandScrapePhase(
                _scraper, bandPersistence,
                pathDataStore, _pool, _progress, options,
                Substitute.For<ILogger<BandScrapePhase>>()),
            bandPersistence,
            options,
            Substitute.For<ILogger<PostScrapeOrchestrator>>(),
            null,
            improvementNotifications: improvementNotifications,
            improvementNotificationOptions: improvementNotificationOptions,
            improvementNotificationRecovery: improvementNotificationRecovery);

        var resultProcessor = new BatchResultProcessor(_persistence, Substitute.For<ILogger<BatchResultProcessor>>());
        var userSyncTracker = new UserSyncProgressTracker(
            new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()),
            Substitute.For<ILogger<UserSyncProgressTracker>>());

        var backfillOrchestrator = new BackfillOrchestrator(
            _backfillQueue, _historyReconstructor,
            rivalsOrchestrator, notifications, _persistence,
            _tokenManager, _progress, userSyncTracker, options,
            _cyclicalMachine, _pool,
            resultProcessor, precomputer,
            new Api.ResponseCacheService(TimeSpan.FromMinutes(5)),
            Substitute.For<ILogger<BackfillOrchestrator>>());

        _shopMetaDb = new FSTService.Persistence.MetaDatabase(
                SharedPostgresContainer.CreateDatabase(),
                Substitute.For<ILogger<FSTService.Persistence.MetaDatabase>>());

        var shopService = new FSTService.Scraping.ItemShopService(
            new HttpClient(),
            _festivalService,
            _shopMetaDb,
            Substitute.For<ILogger<FSTService.Scraping.ItemShopService>>());

        var dbInitializer = new StartupInitializer(
            _persistence, _metaFixture.DataSource, _festivalService, shopService,
            _lifetime,
            options,
            Substitute.For<ILogger<StartupInitializer>>());
        dbInitializer.StartAsync(CancellationToken.None);
        dbInitializer.WaitForReadyAsync().GetAwaiter().GetResult();

        var scrapeOrchestrator = new ScrapeOrchestrator(
            _scraper, _persistence, bandPersistence, pathDataStore, _pool, _progress, options,
            Substitute.For<ILogger<ScrapeOrchestrator>>());

        var playerCache = new Api.ResponseCacheService(TimeSpan.FromMinutes(2));
        var leaderboardAllCache = new Api.ResponseCacheService(TimeSpan.FromMinutes(5));
        var neighborhoodCache = new Api.ResponseCacheService(TimeSpan.FromMinutes(2));
        var rivalsCache = new Api.ResponseCacheService(TimeSpan.FromMinutes(5));
        var leaderboardRivalsCache = new Api.ResponseCacheService(TimeSpan.FromMinutes(5));
        var publicReadGate = new PublicReadGateService(_metaDb, NullLogger<PublicReadGateService>.Instance);
        var publicationReadContext = new PublicationReadContextService(
            _metaDb,
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions()));
        var lifecycle = new ScrapeLifecycleNotifier(
            playerCache, leaderboardAllCache, neighborhoodCache, rivalsCache, leaderboardRivalsCache,
            _metaDb, publicReadGate, publicationReadContext,
            Substitute.For<ILogger<ScrapeLifecycleNotifier>>());

        return new ScraperWorker(
            _tokenManager, _scraper, _persistence,
            _festivalService, dbInitializer,
            scrapeOrchestrator, postScrapeOrchestrator, backfillOrchestrator,
            _cyclicalMachine,
            pathGeneration, pathDataStore,
            songsCache,
            playerCache,
            leaderboardAllCache,
            lifecycle,
            precomputer,
            _progress,
            new BackgroundWorkCoordinator(),
            new FSTService.Scraping.UserSyncProgressTracker(
                new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()),
                Substitute.For<ILogger<FSTService.Scraping.UserSyncProgressTracker>>()),
            notifications,
            options,
            Options.Create(new Microsoft.AspNetCore.Http.Json.JsonOptions()),
            _lifetime,
            _log,
            publicationCommitOptions:
                Options.Create(
                    publicationCommitOptions
                    ?? new PublicationCommitOptions()));
    }

    protected BackfillOrchestrator CreateBackfillOrchestrator(ScraperOptions? opts = null)
    {
        var options = Options.Create(opts ?? new ScraperOptions
        {
            DataDirectory = _tempDir,
            DeviceAuthPath = Path.Combine(_tempDir, "device.json"),
        });
        var notifications = new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>());
        var rivalsCalculator = new RivalsCalculator(_persistence, Substitute.For<ILogger<RivalsCalculator>>());
        var rivalsOrchestrator = new RivalsOrchestrator(rivalsCalculator, _persistence, new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), _progress, new UserSyncProgressTracker(new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), new Api.ResponseCacheService(TimeSpan.FromMinutes(5)), Substitute.For<ILogger<RivalsOrchestrator>>());
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(SongProcessingMachine)).Returns(_machine);
        return new BackfillOrchestrator(
            _backfillQueue, _historyReconstructor,
            rivalsOrchestrator, notifications, _persistence,
            _tokenManager, _progress,
            new UserSyncProgressTracker(
                new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()),
                Substitute.For<ILogger<UserSyncProgressTracker>>()),
            options,
            _cyclicalMachine, _pool,
            new BatchResultProcessor(_persistence, Substitute.For<ILogger<BatchResultProcessor>>()),
            new ScrapeTimePrecomputer(_persistence, _persistence.Meta, new PathDataStore(SharedPostgresContainer.CreateDatabase()), _progress, Substitute.For<ILogger<ScrapeTimePrecomputer>>(), NullLoggerFactory.Instance, new System.Text.Json.JsonSerializerOptions(), new FeatureOptions()),
            new Api.ResponseCacheService(TimeSpan.FromMinutes(5)),
            Substitute.For<ILogger<BackfillOrchestrator>>());
    }

    /// <summary>Create a mock CyclicalSongMachine whose AttachAsync returns an empty result.</summary>
    protected CyclicalSongMachine CreateMockCyclicalMachine()
    {
        var mock = Substitute.For<CyclicalSongMachine>();
        mock.AttachAsync(
            Arg.Any<IReadOnlyList<UserWorkItem>>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyList<Persistence.SeasonWindowInfo>>(),
            Arg.Any<SongMachineSource>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>(),
            Arg.Any<EpicTrafficKind>(),
            Arg.Any<CyclicalSongMachine.AttachmentOptions?>())
            .Returns(call =>
            {
                var users = call.ArgAt<IReadOnlyList<UserWorkItem>>(0);
                var songs = call.ArgAt<IReadOnlyList<string>>(1);
                foreach (var user in users)
                {
                    foreach (var songId in songs)
                    foreach (var instrument in GlobalLeaderboardScraper.AllInstruments)
                    {
                        if (user.Purposes.HasFlag(WorkPurpose.Backfill))
                        {
                            _metaDb.MarkBackfillSongChecked(
                                user.AccountId,
                                songId,
                                instrument,
                                entryFound: false);
                        }

                        if (user.Purposes.HasFlag(WorkPurpose.HistoryRecon))
                        {
                            _metaDb.MarkHistoryReconSongProcessed(
                                user.AccountId,
                                songId,
                                instrument,
                                user.HistoryReconstructionVersion,
                                user.HistoryWindowFingerprint,
                                user.HistoryAdmissionRevision);
                        }
                    }
                }

                return new SongProcessingMachine.MachineResult
                {
                    UsersProcessed = users.Count,
                };
            });
        return mock;
    }

    protected async Task InvokePrivateAsync(ScraperWorker worker, string methodName, params object[] args)
    {
        var method = typeof(ScraperWorker).GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var result = method!.Invoke(worker, args);
        if (result is Task task) await task;
    }

    protected async Task<T> InvokePrivateAsync<T>(ScraperWorker worker, string methodName, params object[] args)
    {
        var method = typeof(ScraperWorker).GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var result = method!.Invoke(worker, args);
        if (result is Task<T> typedTask) return await typedTask;
        throw new InvalidOperationException($"Method {methodName} did not return Task<{typeof(T).Name}>");
    }

    protected long CountScrapeRuns()
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM scrape_log";
        return (long)cmd.ExecuteScalar()!;
    }

    protected bool RelationExists(string relationName)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT to_regclass(@relationName) IS NOT NULL";
        cmd.Parameters.AddWithValue(
            "relationName",
            relationName);
        return cmd.ExecuteScalar() is true;
    }

    protected void DropRelation(string relationName)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"DROP TABLE IF EXISTS {BandRankingStorageNames.QuoteIdentifier(relationName)}";
        cmd.ExecuteNonQuery();
    }

    protected static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException(
            "Condition was not satisfied before timeout.");
    }

    protected string? GetPublicationCommitIntentOwner()
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT publication_commit_intent_owner
            FROM scrape_publication_state
            WHERE id = TRUE
            """;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull
            ? null
            : Convert.ToString(
                value,
                System.Globalization.CultureInfo.InvariantCulture);
    }

    protected void CreatePublicationCommitFailureTrigger(
        string name)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE OR REPLACE FUNCTION "{name}_fn"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF OLD.public_reads_frozen_reason =
                        '{PublicReadFreezeState.PublicationCommitIntentReason}'
                   AND NOT NEW.public_reads_frozen
                THEN
                    RAISE EXCEPTION 'injected commit failure'
                        USING ERRCODE = 'P0001';
                END IF;
                RETURN NEW;
            END
            $$;

            CREATE TRIGGER "{name}"
            BEFORE UPDATE ON scrape_publication_state
            FOR EACH ROW
            EXECUTE FUNCTION "{name}_fn"();
            """;
        cmd.ExecuteNonQuery();
    }

    protected void DropPublicationCommitFailureTrigger(
        string name)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DROP TRIGGER IF EXISTS "{name}"
                ON scrape_publication_state;
            DROP FUNCTION IF EXISTS "{name}_fn"();
            """;
        cmd.ExecuteNonQuery();
    }

    protected FestivalService CreateServiceWithSongs(
        params (string id, string title, string artist)[] songs)
    {
        var providerPayload = songs.ToDictionary(
            static song => song.id,
            static song => (object)new
            {
                _title = song.title,
                track = new
                {
                    su = song.id,
                    tt = song.title,
                    an = song.artist,
                    @in = new
                    {
                        gr = 5,
                        ba = 3,
                        vl = 4,
                        ds = 2,
                    },
                },
            });
        var contentClient = new HttpClient(
            new StaticProviderHandler(
                System.Net.HttpStatusCode.OK,
                System.Text.Json.JsonSerializer.Serialize(
                    providerPayload)))
        {
            BaseAddress = new Uri(
                "https://fortnitecontent-website-prod07.ol.epicgames.com"),
        };
        var service = new FestivalService(
            new FestivalPersistence(_metaFixture.DataSource),
            contentClient);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var songsField = typeof(FestivalService).GetField("_songs", flags)!;
        var dirtyField = typeof(FestivalService).GetField("_songsDirty", flags)!;
        var dict = (Dictionary<string, Song>)songsField.GetValue(service)!;
        foreach (var (id, title, artist) in songs)
        {
            dict[id] = new Song
            {
                track = new Track
                {
                    su = id, tt = title, an = artist,
                    @in = new In { gr = 5, ba = 3, vl = 4, ds = 2 }
                }
            };
        }
        dirtyField.SetValue(service, true);
        return service;
    }

    protected FestivalService CreateServiceWithFailedProviderSync(
        params (string id, string title, string artist)[] songs)
    {
        var service = new FestivalService(
            new FestivalPersistence(_metaFixture.DataSource),
            new HttpClient(
                new StaticProviderHandler(
                    System.Net.HttpStatusCode.ServiceUnavailable,
                    """{"errorCode":"errors.com.epicgames.service_unavailable"}"""))
            {
                BaseAddress = new Uri(
                    "https://fortnitecontent-website-prod07.ol.epicgames.com"),
            });
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var songsField = typeof(FestivalService).GetField("_songs", flags)!;
        var dirtyField = typeof(FestivalService).GetField("_songsDirty", flags)!;
        var dict = (Dictionary<string, Song>)songsField.GetValue(service)!;
        foreach (var (id, title, artist) in songs)
        {
            dict[id] = new Song
            {
                track = new Track
                {
                    su = id,
                    tt = title,
                    an = artist,
                    @in = new In(),
                },
            };
        }
        dirtyField.SetValue(service, true);
        return service;
    }

    private sealed class StaticProviderHandler : HttpMessageHandler
    {
        private readonly System.Net.HttpStatusCode _statusCode;
        private readonly string _content;

        public StaticProviderHandler(
            System.Net.HttpStatusCode statusCode,
            string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(
                    _content,
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
    }

    protected static void WriteBE(Stream s, int v) { s.WriteByte((byte)(v>>24)); s.WriteByte((byte)(v>>16)); s.WriteByte((byte)(v>>8)); s.WriteByte((byte)v); }
    protected static void WriteBE16(Stream s, int v) { s.WriteByte((byte)(v>>8)); s.WriteByte((byte)v); }

    protected sealed class NoOpHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
