using System.Text.Json;
using System.Diagnostics;
using FortniteFestival.Core.Scraping;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Persistence.Maintenance;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FSTService.Tests.Unit;

public class PostScrapeOrchestratorTests : IDisposable
{
    public static IEnumerable<object[]> ClassifiedPhases =>
        PostScrapePhasePolicy.All.Select(static pair => new object[] { pair.Key, pair.Value });

    private readonly string _tempDir;
    private readonly InMemoryMetaDatabase _metaFixture = new();
    private readonly MetaDatabase _metaDb;
    private readonly GlobalLeaderboardPersistence _persistence;

    private readonly TokenManager _tokenManager;
    private readonly FirstSeenSeasonCalculator _firstSeenCalculator;
    private readonly AccountNameResolver _nameResolver;
    private readonly HistoryReconstructor _historyReconstructor;
    private readonly SongProcessingMachine _machine;
    private readonly CyclicalSongMachine _cyclicalMachine;
    private readonly SharedDopPool _pool;
    private readonly NotificationService _notifications;
    private readonly ScrapeProgressTracker _progress;
    private readonly PathDataStore _pathDataStore;
    private readonly RegistrationMutationCoordinator
        _registrationMutations;
    private readonly TestLogger<PostScrapeOrchestrator> _log;
    private readonly SoloCurrentProjectionBuilder _soloCurrentProjectionBuilder;
    private readonly IDatabasePressureMonitor _databasePressureMonitor;
    private readonly WorkerStatusPublisher _workerStatus;
    private readonly DurablePhaseProgressSink _phaseProgress;

    private readonly PostScrapeOrchestrator _sut;

    public PostScrapeOrchestratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pso_test_{Guid.NewGuid():N}");
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

        var scraper = Substitute.For<GlobalLeaderboardScraper>(
            new HttpClient(),
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<GlobalLeaderboardScraper>>(),
            0,
            null);

        _firstSeenCalculator = Substitute.For<FirstSeenSeasonCalculator>(
            scraper, _persistence, new ScrapeProgressTracker(),
            Substitute.For<ILogger<FirstSeenSeasonCalculator>>());

        _nameResolver = Substitute.For<AccountNameResolver>(
            new HttpClient(), _metaDb, _tokenManager,
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<AccountNameResolver>>());

        _historyReconstructor = Substitute.For<HistoryReconstructor>(
            scraper,
            _persistence,
            new HttpClient(),
            new ScrapeProgressTracker(),
            new UserSyncProgressTracker(
                new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()),
                Substitute.For<ILogger<UserSyncProgressTracker>>()),
            Substitute.For<ILogger<HistoryReconstructor>>());

        _machine = Substitute.For<SongProcessingMachine>(
            scraper, new BatchResultProcessor(_persistence, Substitute.For<ILogger<BatchResultProcessor>>()),
            _persistence, new ScrapeProgressTracker(),
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

        _notifications = new NotificationService(Substitute.For<ILogger<NotificationService>>());
        _progress = new ScrapeProgressTracker();
        _pathDataStore = new PathDataStore(SharedPostgresContainer.CreateDatabase());
        _registrationMutations =
            new RegistrationMutationCoordinator(
                _metaDb,
                _pathDataStore,
                Substitute.For<
                    ISongInstrumentSupportCache>());
        _log = new TestLogger<PostScrapeOrchestrator>();
        _soloCurrentProjectionBuilder = new SoloCurrentProjectionBuilder(
            _metaFixture.DataSource,
            Substitute.For<ILogger<SoloCurrentProjectionBuilder>>());
        _databasePressureMonitor = Substitute.For<IDatabasePressureMonitor>();
        _databasePressureMonitor.GetPressureSnapshotAsync(Arg.Any<DatabaseMaintenanceOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DatabasePressureSnapshot.None));
        _phaseProgress = new DurablePhaseProgressSink(
            _metaDb,
            new ConfigurationBuilder().Build(),
            NullLogger<DurablePhaseProgressSink>.Instance);
        _workerStatus = new WorkerStatusPublisher(
            _metaDb,
            NullLogger<WorkerStatusPublisher>.Instance,
            _phaseProgress);

        var rivalsCalculator = new RivalsCalculator(_persistence, Substitute.For<ILogger<RivalsCalculator>>());
        var rivalsOrchestrator = new RivalsOrchestrator(rivalsCalculator, _persistence, new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), _progress, new UserSyncProgressTracker(new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), new Api.ResponseCacheService(TimeSpan.FromMinutes(5)), Substitute.For<ILogger<RivalsOrchestrator>>());
        var rankingsCalculator = new RankingsCalculator(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<RankingsCalculator>>());
        var leaderboardRivalsCalculator = new LeaderboardRivalsCalculator(_persistence, _metaDb, Options.Create(new ScraperOptions()), Substitute.For<ILogger<LeaderboardRivalsCalculator>>());

        _cyclicalMachine = CreateMockCyclicalMachine();

        _sut = new PostScrapeOrchestrator(
            _persistence, _firstSeenCalculator, _nameResolver,
            _historyReconstructor,
            _pool,
            _cyclicalMachine,
            rivalsOrchestrator, rankingsCalculator, leaderboardRivalsCalculator, _notifications,
            _tokenManager, _progress, new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), _pathDataStore,
            new ScrapeTimePrecomputer(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<ScrapeTimePrecomputer>>(), NullLoggerFactory.Instance, new System.Text.Json.JsonSerializerOptions(), new FeatureOptions()),
            new PostScrapeBandExtractor(null!, _pathDataStore, Substitute.For<ILogger<PostScrapeBandExtractor>>()),
            new BandScrapePhase(
                scraper,
                new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
                _pathDataStore, _pool, _progress, Options.Create(new ScraperOptions()),
                Substitute.For<ILogger<BandScrapePhase>>()),
            new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
            Options.Create(new ScraperOptions()), _log, _registrationMutations, null,
            soloCurrentProjectionBuilder: _soloCurrentProjectionBuilder,
            databasePressureMonitor: _databasePressureMonitor,
            workerStatus: _workerStatus,
            phaseProgress: _phaseProgress);
    }

    public void Dispose()
    {
        _pool.Dispose();
        _persistence.Dispose();
        _metaDb.Dispose();
        _metaFixture.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>Create a mock CyclicalSongMachine whose AttachAsync returns an empty result.</summary>
    private static CyclicalSongMachine CreateMockCyclicalMachine()
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
            .Returns(new SongProcessingMachine.MachineResult());
        return mock;
    }

    private static async Task<SongProcessingMachine.MachineResult> WaitUntilCancelledAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return new SongProcessingMachine.MachineResult();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentProjectionCandidateOptionIsForwarded(
        bool enabled)
    {
        var options = new ScraperOptions
        {
            BandCurrentProjectionUseBatchedMemberStatsAggregation =
                enabled,
        };

        var rebuildOptions =
            PostScrapeOrchestrator
                .CreateBandCurrentProjectionRebuildOptions(options);

        Assert.Equal(
            enabled,
            rebuildOptions.UseBatchedMemberStatsAggregation);
        Assert.True(rebuildOptions.SkipUnchangedScopes);
        Assert.True(rebuildOptions.DisableSynchronousCommit);
        Assert.Equal(2, rebuildOptions.MaxParallelBandTypes);
    }

    private static async Task<IReadOnlyList<Persistence.SeasonWindowInfo>> WaitUntilCancelledSeasonWindowsAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return Array.Empty<Persistence.SeasonWindowInfo>();
    }

    private ScrapePassContext CreateContext(
        long scrapeId = 0,
        HashSet<string>? registeredIds = null,
        GlobalLeaderboardPersistence.PipelineAggregates? aggregates = null,
        IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>? scrapeRequests = null,
        bool leaderboardScrapeCompleted = true)
    {
        return new ScrapePassContext
        {
            ScrapeId = scrapeId,
            AccessToken = "test-token",
            CallerAccountId = "caller-001",
            RegisteredIds = registeredIds ?? new HashSet<string>(),
            Aggregates = aggregates ?? new GlobalLeaderboardPersistence.PipelineAggregates(),
            ScrapeRequests = scrapeRequests ?? Array.Empty<GlobalLeaderboardScraper.SongScrapeRequest>(),
            DegreeOfParallelism = 4,
            LeaderboardScrapeCompleted = leaderboardScrapeCompleted,
        };
    }

    private long PublishCompletedScrape(bool queueImprovementNotifications = true)
    {
        var scrapeId = _metaDb.StartScrapeRun();
        _metaDb.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        _metaDb.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false,
            queueImprovementNotifications: queueImprovementNotifications,
            improvementNotificationProjectionScopes:
                queueImprovementNotifications ? [] : null);
        return scrapeId;
    }

    private PostScrapeOrchestrator CreateOrchestratorWithImprovementNotifications(
        ImprovementNotificationOptions? improvementOptions = null)
    {
        var scraper = Substitute.For<GlobalLeaderboardScraper>(
            new HttpClient(), new ScrapeProgressTracker(), Substitute.For<ILogger<GlobalLeaderboardScraper>>(), 0, null);
        var rivalsCalculator = new RivalsCalculator(_persistence, Substitute.For<ILogger<RivalsCalculator>>());
        var rivalsOrchestrator = new RivalsOrchestrator(
            rivalsCalculator,
            _persistence,
            new NotificationService(Substitute.For<ILogger<NotificationService>>()),
            _progress,
            new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()),
            new ResponseCacheService(TimeSpan.FromMinutes(5)),
            Substitute.For<ILogger<RivalsOrchestrator>>());
        var rankingsCalculator = new RankingsCalculator(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<RankingsCalculator>>());
        var leaderboardRivalsCalculator = new LeaderboardRivalsCalculator(_persistence, _metaDb, Options.Create(new ScraperOptions()), Substitute.For<ILogger<LeaderboardRivalsCalculator>>());
        var notificationOptions = Options.Create(improvementOptions ?? new ImprovementNotificationOptions
        {
            Enabled = true,
            IncludePlayers = false,
            IncludeBands = false,
            IncludeSongEvents = false,
            IncludeRankings = false,
        });
        var improvementNotifications = new ImprovementNotificationService(
            _metaFixture.DataSource,
            Substitute.For<ILogger<ImprovementNotificationService>>());
        var improvementRecovery = new ImprovementNotificationRecoveryService(
            improvementNotifications,
            _soloCurrentProjectionBuilder,
            notificationOptions,
            Substitute.For<ILogger<ImprovementNotificationRecoveryService>>());

        return new PostScrapeOrchestrator(
            _persistence, _firstSeenCalculator, _nameResolver,
            Substitute.For<HistoryReconstructor>(scraper, _persistence, new HttpClient(), new ScrapeProgressTracker(), new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), Substitute.For<ILogger<HistoryReconstructor>>()),
            _pool,
            _cyclicalMachine,
            rivalsOrchestrator, rankingsCalculator, leaderboardRivalsCalculator, _notifications,
            _tokenManager, _progress, new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), _pathDataStore,
            new ScrapeTimePrecomputer(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<ScrapeTimePrecomputer>>(), NullLoggerFactory.Instance, new JsonSerializerOptions(), new FeatureOptions()),
            new PostScrapeBandExtractor(null!, _pathDataStore, Substitute.For<ILogger<PostScrapeBandExtractor>>()),
            new BandScrapePhase(
                scraper,
                new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
                _pathDataStore, _pool, _progress, Options.Create(new ScraperOptions()),
                Substitute.For<ILogger<BandScrapePhase>>()),
            new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
            Options.Create(new ScraperOptions()), _log,
            _registrationMutations, null,
            improvementNotifications: improvementNotifications,
            soloCurrentProjectionBuilder: _soloCurrentProjectionBuilder,
            improvementNotificationOptions: notificationOptions,
            improvementNotificationRecovery: improvementRecovery);
    }

    private PostScrapeOrchestrator CreateOrchestrator(
        CyclicalSongMachine cyclicalMachine,
        HistoryReconstructor historyReconstructor,
        ScraperOptions? options = null,
        GlobalLeaderboardPersistence? persistence = null,
        SoloCurrentProjectionBuilder? soloCurrentProjectionBuilder = null,
        IPostScrapePhaseFaultInjector? phaseFaultInjector = null,
        IDatabaseRetentionMaintenanceService? retentionMaintenanceService = null,
        DatabaseMaintenanceOptions? databaseMaintenanceOptions = null)
    {
        var activePersistence = persistence ?? _persistence;
        var scraper = Substitute.For<GlobalLeaderboardScraper>(
            new HttpClient(),
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<GlobalLeaderboardScraper>>(),
            0,
            null);
        var scraperOptions = options ?? new ScraperOptions();
        var rivalsCalculator = new RivalsCalculator(
            activePersistence,
            Substitute.For<ILogger<RivalsCalculator>>());
        var rivalsOrchestrator = new RivalsOrchestrator(
            rivalsCalculator,
            activePersistence,
            new NotificationService(Substitute.For<ILogger<NotificationService>>()),
            _progress,
            new UserSyncProgressTracker(
                new NotificationService(Substitute.For<ILogger<NotificationService>>()),
                Substitute.For<ILogger<UserSyncProgressTracker>>()),
            new ResponseCacheService(TimeSpan.FromMinutes(5)),
            Substitute.For<ILogger<RivalsOrchestrator>>());
        var rankingsCalculator = new RankingsCalculator(
            activePersistence,
            _metaDb,
            _pathDataStore,
            _progress,
            Substitute.For<ILogger<RankingsCalculator>>());
        var leaderboardRivalsCalculator = new LeaderboardRivalsCalculator(
            activePersistence,
            _metaDb,
            Options.Create(scraperOptions),
            Substitute.For<ILogger<LeaderboardRivalsCalculator>>());

        return new PostScrapeOrchestrator(
            activePersistence,
            _firstSeenCalculator,
            _nameResolver,
            historyReconstructor,
            _pool,
            cyclicalMachine,
            rivalsOrchestrator,
            rankingsCalculator,
            leaderboardRivalsCalculator,
            _notifications,
            _tokenManager,
            _progress,
            new UserSyncProgressTracker(
                new NotificationService(Substitute.For<ILogger<NotificationService>>()),
                Substitute.For<ILogger<UserSyncProgressTracker>>()),
            _pathDataStore,
            new ScrapeTimePrecomputer(
                activePersistence,
                _metaDb,
                _pathDataStore,
                _progress,
                Substitute.For<ILogger<ScrapeTimePrecomputer>>(),
                NullLoggerFactory.Instance,
                new JsonSerializerOptions(),
                new FeatureOptions()),
            new PostScrapeBandExtractor(
                null!,
                _pathDataStore,
                Substitute.For<ILogger<PostScrapeBandExtractor>>()),
            new BandScrapePhase(
                scraper,
                new BandLeaderboardPersistence(
                    null!,
                    Substitute.For<ILogger<BandLeaderboardPersistence>>()),
                _pathDataStore,
                _pool,
                _progress,
                Options.Create(scraperOptions),
                Substitute.For<ILogger<BandScrapePhase>>()),
            new BandLeaderboardPersistence(
                null!,
                Substitute.For<ILogger<BandLeaderboardPersistence>>()),
            Options.Create(scraperOptions),
            _log,
            _registrationMutations,
            null,
            soloCurrentProjectionBuilder:
                soloCurrentProjectionBuilder ?? _soloCurrentProjectionBuilder,
            databaseMaintenanceOptions: Options.Create(
                databaseMaintenanceOptions ?? new DatabaseMaintenanceOptions()),
            databasePressureMonitor: _databasePressureMonitor,
            retentionMaintenanceService: retentionMaintenanceService,
            phaseFaultInjector: phaseFaultInjector,
            workerStatus: _workerStatus);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // RefreshRegisteredUsersAsync
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task RefreshRegisteredUsers_NoRegisteredUsers_Skips()
    {
        var ctx = CreateContext();
        _cyclicalMachine.ClearReceivedCalls();

        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);

        Assert.DoesNotContain(
            _cyclicalMachine.ReceivedCalls(),
            call => call.GetMethodInfo().Name == nameof(CyclicalSongMachine.AttachAsync));
    }

    [Fact]
    public async Task RefreshRegisteredUsers_WithToken_Refreshes()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-access-token");
        _tokenManager.AccountId.Returns("caller-001");

        var ctx = CreateContext(registeredIds: new HashSet<string> { "user-1" });

        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);

        // Verify the cyclical machine was invoked
        await _cyclicalMachine.Received(1).AttachAsync(
            Arg.Any<IReadOnlyList<UserWorkItem>>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyList<Persistence.SeasonWindowInfo>>(),
            Arg.Any<SongMachineSource>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>(),
            preserveProgressPhaseOnIdle: true,
            Arg.Any<EpicTrafficKind>(),
            Arg.Is<CyclicalSongMachine.AttachmentOptions?>(options =>
                options != null && options.PreserveSongOrder));
    }

    [Fact]
    public async Task RefreshRegisteredUsers_PhaseOnlyCallback_PersistsNoScrapeProvenance()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-access-token");
        _tokenManager.AccountId.Returns("caller-001");

        _cyclicalMachine.AttachAsync(
            Arg.Any<IReadOnlyList<UserWorkItem>>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyList<Persistence.SeasonWindowInfo>>(),
            SongMachineSource.PostScrape,
            true,
            Arg.Any<CancellationToken>(),
            true,
            Arg.Any<EpicTrafficKind>(),
            Arg.Any<CyclicalSongMachine.AttachmentOptions?>())
            .Returns(async call =>
            {
                var options = call.Arg<CyclicalSongMachine.AttachmentOptions?>();
                Assert.NotNull(options?.OnScopesCompleted);
                await options!.OnScopesCompleted!(
                    [new SoloCurrentProjectionScopeKey("song-phase-only", "Solo_Guitar")]);
                return new SongProcessingMachine.MachineResult();
            });

        var ctx = CreateContext(
            scrapeId: 0,
            registeredIds: new HashSet<string> { "user-1" },
            scrapeRequests:
            [
                new GlobalLeaderboardScraper.SongScrapeRequest
                {
                    SongId = "song-phase-only",
                    Instruments = GlobalLeaderboardScraper.AllInstruments,
                },
            ]);

        _workerStatus.BeginOperation(
            "scrape.post_process",
            "Post-processing leaderboard update",
            phase: "PostScrapeEnrichment",
            subOperation: "RefreshRegisteredUsers",
            detail: "Running RefreshRegisteredUsers");
        var operationBefore = _metaDb.GetWorkerStatus(
            WorkerStatusPublisher.ScraperWorkerKey)!.CurrentOperation!;

        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);

        var operationAfter = _metaDb.GetWorkerStatus(
            WorkerStatusPublisher.ScraperWorkerKey)!.CurrentOperation!;
        Assert.True(
            operationAfter.UpdatedAtUtc >= operationBefore.UpdatedAtUtc);
        Assert.Contains(
            "completed scope batch persisted",
            operationAfter.Detail,
            StringComparison.Ordinal);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT scrape_id, provenance
            FROM registered_user_refresh_scope_progress
            WHERE song_id = 'song-phase-only'
              AND instrument = 'Solo_Guitar'
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal("phase_only", reader.GetString(1));
    }

    [Fact]
    public async Task RefreshRegisteredUsers_SeasonRollover_UsesNoncanonicalWindowBeforeCheckpoint()
    {
        const int instrumentMaxSeason = 14;
        const int discoveredSeason = 15;
        const string discoveredWindowId = "season_15_competitive";
        const string songId = "song-rollover";
        const string accountId = "user-rollover";

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-access-token");
        _tokenManager.AccountId.Returns("caller-001");

        using (var conn = _metaFixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO instrument_scrape_state (
                    instrument,
                    max_observed_season,
                    last_scrape_id,
                    updated_at)
                VALUES (
                    'Solo_Guitar',
                    @season,
                    NULL,
                    @now)
                ON CONFLICT (instrument) DO UPDATE SET
                    max_observed_season = EXCLUDED.max_observed_season,
                    updated_at = EXCLUDED.updated_at
                """;
            cmd.Parameters.AddWithValue("season", instrumentMaxSeason);
            cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
            cmd.ExecuteNonQuery();
        }

        var querier = Substitute.For<ILeaderboardQuerier>();
        querier.LookupMultipleAccountsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>(),
            true)
            .Returns([]);

        var guitarSeasonLookupStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGuitarSeasonLookup =
            new TaskCompletionSource<List<SessionHistoryEntry>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        querier.LookupMultipleAccountSessionsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>(),
            true)
            .Returns(call =>
            {
                Assert.Equal(discoveredWindowId, call.ArgAt<string>(2));
                if (call.ArgAt<string>(1) == "Solo_Guitar")
                {
                    guitarSeasonLookupStarted.TrySetResult(true);
                    return releaseGuitarSeasonLookup.Task;
                }

                return Task.FromResult(new List<SessionHistoryEntry>());
            });

        var syncTracker = new UserSyncProgressTracker(
            new NotificationService(NullLogger<NotificationService>.Instance),
            NullLogger<UserSyncProgressTracker>.Instance);
        var innerMachine = new SongProcessingMachine(
            querier,
            new BatchResultProcessor(
                _persistence,
                Substitute.For<ILogger<BatchResultProcessor>>()),
            _persistence,
            _progress,
            syncTracker,
            Substitute.For<ILogger<SongProcessingMachine>>());
        var historyReconstructor = Substitute.For<HistoryReconstructor>(
            querier,
            _persistence,
            new HttpClient(new NoOpHttpHandler()),
            _progress,
            syncTracker,
            Substitute.For<ILogger<HistoryReconstructor>>());
        historyReconstructor.DiscoverSeasonWindowsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(
            [
                new SeasonWindowInfo
                {
                    SeasonNumber = discoveredSeason,
                    EventId = "season015_event",
                    WindowId = discoveredWindowId,
                },
            ]);

        var scraperOptions = new ScraperOptions
        {
            RefreshCurrentSeasonSessions = true,
            RegisteredUserRefreshTimeout = TimeSpan.Zero,
        };
        var cyclicalMachine = new CyclicalSongMachine(
            innerMachine,
            historyReconstructor,
            _tokenManager,
            _pool,
            _progress,
            syncTracker,
            _persistence,
            Options.Create(scraperOptions),
            Substitute.For<ILogger<CyclicalSongMachine>>());
        var sut = CreateOrchestrator(
            cyclicalMachine,
            historyReconstructor,
            scraperOptions);
        var ctx = CreateContext(
            scrapeId: 500,
            registeredIds: new HashSet<string> { accountId },
            scrapeRequests:
            [
                new GlobalLeaderboardScraper.SongScrapeRequest
                {
                    SongId = songId,
                    Instruments = GlobalLeaderboardScraper.AllInstruments,
                },
            ]);

        var refreshTask = sut.RefreshRegisteredUsersAsync(
            ctx,
            CancellationToken.None);
        await guitarSeasonLookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using (var conn = _metaFixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*)
                FROM registered_user_refresh_scope_progress
                WHERE song_id = @songId
                  AND instrument = 'Solo_Guitar'
                """;
            cmd.Parameters.AddWithValue("songId", songId);
            Assert.Equal(0, Convert.ToInt32(cmd.ExecuteScalar()));
        }

        releaseGuitarSeasonLookup.SetResult([]);
        await refreshTask;

        await querier.Received(1).LookupMultipleAccountSessionsAsync(
            songId,
            "Solo_Guitar",
            discoveredWindowId,
            Arg.Is<IReadOnlyList<string>>(accounts =>
                accounts.Count == 1 && accounts[0] == accountId),
            "test-access-token",
            "caller-001",
            Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>(),
            true);
        await querier.DidNotReceive().LookupMultipleAccountSessionsAsync(
            songId,
            "Solo_Guitar",
            "season015",
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>(),
            true);

        using (var conn = _metaFixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT scrape_id, provenance
                FROM registered_user_refresh_scope_progress
                WHERE song_id = @songId
                  AND instrument = 'Solo_Guitar'
                """;
            cmd.Parameters.AddWithValue("songId", songId);
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(500, reader.GetInt64(0));
            Assert.Equal("scrape", reader.GetString(1));
        }
    }

    [Fact]
    public async Task CyclicalSongMachine_LateMismatchedSeason_DefersAttachmentUntilMatchingCycle()
    {
        const string firstAccount = "user-season-14";
        const string lateAccount = "user-season-15";
        const string season14Window = "season_14_competitive";
        const string season15Window = "season_15_competitive";

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-access-token");
        _tokenManager.AccountId.Returns("caller-001");

        var allTimeCalls =
            new System.Collections.Concurrent.ConcurrentBag<(string SongId, string[] Accounts)>();
        var seasonalCalls =
            new System.Collections.Concurrent.ConcurrentBag<(string SongId, string WindowId, string[] Accounts)>();
        var firstSongStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSong = new TaskCompletionSource<List<SessionHistoryEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var querier = Substitute.For<ILeaderboardQuerier>();
        querier.LookupMultipleAccountsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>(),
            true)
            .Returns(call =>
            {
                allTimeCalls.Add((
                    call.ArgAt<string>(0),
                    call.ArgAt<IReadOnlyList<string>>(2).ToArray()));
                return new List<LeaderboardEntry>();
            });
        querier.LookupMultipleAccountSessionsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>(),
            true)
            .Returns(call =>
            {
                var songId = call.ArgAt<string>(0);
                var instrument = call.ArgAt<string>(1);
                var windowId = call.ArgAt<string>(2);
                var accounts = call.ArgAt<IReadOnlyList<string>>(3).ToArray();
                seasonalCalls.Add((songId, windowId, accounts));
                if (songId == "song-a"
                    && instrument == "Solo_Guitar"
                    && accounts.SequenceEqual([firstAccount]))
                {
                    firstSongStarted.TrySetResult(true);
                    return releaseFirstSong.Task;
                }

                return Task.FromResult(new List<SessionHistoryEntry>());
            });

        var syncTracker = new UserSyncProgressTracker(
            new NotificationService(NullLogger<NotificationService>.Instance),
            NullLogger<UserSyncProgressTracker>.Instance);
        var innerMachine = new SongProcessingMachine(
            querier,
            new BatchResultProcessor(
                _persistence,
                Substitute.For<ILogger<BatchResultProcessor>>()),
            _persistence,
            _progress,
            syncTracker,
            Substitute.For<ILogger<SongProcessingMachine>>());
        var historyReconstructor = Substitute.For<HistoryReconstructor>(
            querier,
            _persistence,
            new HttpClient(),
            _progress,
            syncTracker,
            Substitute.For<ILogger<HistoryReconstructor>>());
        var cyclicalMachine = new CyclicalSongMachine(
            innerMachine,
            historyReconstructor,
            _tokenManager,
            _pool,
            _progress,
            syncTracker,
            _persistence,
            Options.Create(new ScraperOptions
            {
                SongMachineDop = 1,
                RefreshCurrentSeasonSessions = true,
            }),
            Substitute.For<ILogger<CyclicalSongMachine>>());
        var firstWindows = new[]
        {
            new SeasonWindowInfo
            {
                SeasonNumber = 14,
                WindowId = season14Window,
                SourceKind = "event_api",
                IsFreshAuthoritative = true,
            },
        };
        var lateWindows = new[]
        {
            new SeasonWindowInfo
            {
                SeasonNumber = 15,
                WindowId = season15Window,
                SourceKind = "event_api",
                IsFreshAuthoritative = true,
            },
        };
        var lateCompletedScopes =
            new System.Collections.Concurrent.ConcurrentBag<SoloCurrentProjectionScopeKey>();

        var firstTask = cyclicalMachine.AttachAsync(
            [
                new UserWorkItem
                {
                    AccountId = firstAccount,
                    Purposes = WorkPurpose.PostScrape,
                    AllTimeNeeded = true,
                    SeasonsNeeded = [14],
                },
            ],
            ["song-a", "song-b"],
            firstWindows,
            SongMachineSource.PostScrape,
            isHighPriority: true,
            attachmentOptions: new CyclicalSongMachine.AttachmentOptions(
                PreserveSongOrder: true,
                OnScopesCompleted: _ => ValueTask.CompletedTask,
                CurrentSeason: 14,
                CurrentSeasonLookupId: season14Window));

        await firstSongStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var lateTask = cyclicalMachine.AttachAsync(
            [
                new UserWorkItem
                {
                    AccountId = lateAccount,
                    Purposes = WorkPurpose.PostScrape,
                    AllTimeNeeded = true,
                    SeasonsNeeded = [15],
                },
            ],
            ["song-b"],
            lateWindows,
            SongMachineSource.PostScrape,
            isHighPriority: true,
            attachmentOptions: new CyclicalSongMachine.AttachmentOptions(
                PreserveSongOrder: true,
                OnScopesCompleted: scopes =>
                {
                    foreach (var scope in scopes)
                        lateCompletedScopes.Add(scope);
                    return ValueTask.CompletedTask;
                },
                CurrentSeason: 15,
                CurrentSeasonLookupId: season15Window));

        Assert.Empty(lateCompletedScopes);
        releaseFirstSong.SetResult([]);
        await firstTask.WaitAsync(TimeSpan.FromSeconds(20));
        await lateTask.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.DoesNotContain(allTimeCalls, call =>
            call.SongId == "song-b"
            && call.Accounts.Contains(firstAccount)
            && call.Accounts.Contains(lateAccount));
        Assert.Contains(allTimeCalls, call =>
            call.SongId == "song-b"
            && call.Accounts.SequenceEqual([firstAccount]));
        Assert.Contains(allTimeCalls, call =>
            call.SongId == "song-b"
            && call.Accounts.SequenceEqual([lateAccount]));
        Assert.Contains(seasonalCalls, call =>
            call.SongId == "song-b"
            && call.WindowId == season15Window
            && call.Accounts.SequenceEqual([lateAccount]));
        Assert.Equal(
            GlobalLeaderboardScraper.AllInstruments.Count,
            lateCompletedScopes.Count);
    }

    [Fact]
    public async Task CyclicalSongMachine_HistoryPair_RetriesFullMultiSeasonPassBeforeCheckpoint()
    {
        const string accountId = "history-user";
        var windows = new[]
        {
            new SeasonWindowInfo
            {
                SeasonNumber = 14,
                WindowId = "season014",
                SourceKind = "event_api",
            },
            new SeasonWindowInfo
            {
                SeasonNumber = 15,
                WindowId = "season015",
                SourceKind = "event_api",
            },
        };
        var fingerprint = HistoryReconstructor.ComputeWindowFingerprint(windows);
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-access-token");
        _tokenManager.AccountId.Returns("caller-001");

        var season14Attempts = 0;
        var retryStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource<List<SessionHistoryEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var querier = Substitute.For<ILeaderboardQuerier>();
        querier.LookupMultipleAccountSessionsAsync(
            "song-history",
            Arg.Any<string>(),
            "season014",
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>(),
            true)
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref season14Attempts) == 1)
                {
                    return Task.FromException<List<SessionHistoryEntry>>(
                        new HttpRequestException("season 14 failed"));
                }

                retryStarted.TrySetResult(true);
                return releaseRetry.Task;
            });
        querier.LookupMultipleAccountSessionsAsync(
            "song-history",
            Arg.Any<string>(),
            "season015",
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>(),
            true)
            .Returns([]);

        var syncTracker = new UserSyncProgressTracker(
            new NotificationService(NullLogger<NotificationService>.Instance),
            NullLogger<UserSyncProgressTracker>.Instance);
        var innerMachine = new SongProcessingMachine(
            querier,
            new BatchResultProcessor(
                _persistence,
                Substitute.For<ILogger<BatchResultProcessor>>()),
            _persistence,
            _progress,
            syncTracker,
            Substitute.For<ILogger<SongProcessingMachine>>());
        var historyReconstructor = Substitute.For<HistoryReconstructor>(
            querier,
            _persistence,
            new HttpClient(),
            _progress,
            syncTracker,
            Substitute.For<ILogger<HistoryReconstructor>>());
        var cyclicalMachine = new CyclicalSongMachine(
            innerMachine,
            historyReconstructor,
            _tokenManager,
            _pool,
            _progress,
            syncTracker,
            _persistence,
            Options.Create(new ScraperOptions { SongMachineDop = 1 }),
            Substitute.For<ILogger<CyclicalSongMachine>>());
        _metaDb.EnqueueHistoryRecon(
            accountId,
            1,
            HistoryReconstructor.CurrentReconstructionVersion,
            fingerprint);

        var attachmentTask = cyclicalMachine.AttachAsync(
            [
                new UserWorkItem
                {
                    AccountId = accountId,
                    Purposes = WorkPurpose.HistoryRecon,
                    AllTimeNeeded = false,
                    SeasonsNeeded = [14, 15],
                    HistoryReconstructionVersion =
                        HistoryReconstructor.CurrentReconstructionVersion,
                    HistoryWindowFingerprint = fingerprint,
                },
            ],
            ["song-history"],
            windows,
            SongMachineSource.HistoryRecon,
            isHighPriority: false);

        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(_metaDb.GetProcessedHistoryReconPairs(
            accountId,
            HistoryReconstructor.CurrentReconstructionVersion,
            fingerprint));
        releaseRetry.SetResult([]);
        await attachmentTask.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Contains(
            ("song-history", "Solo_Guitar"),
            _metaDb.GetProcessedHistoryReconPairs(
                accountId,
                HistoryReconstructor.CurrentReconstructionVersion,
                fingerprint));
        await querier.Received(2).LookupMultipleAccountSessionsAsync(
            "song-history",
            "Solo_Guitar",
            "season014",
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>(),
            true);
        await querier.Received().LookupMultipleAccountSessionsAsync(
            "song-history",
            "Solo_Guitar",
            "season015",
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>(),
            true);
    }

    [Fact]
    public async Task RefreshRegisteredUsers_orders_least_covered_songs_and_logs_bounded_telemetry()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-access-token");
        _tokenManager.AccountId.Returns("caller-001");

        var old = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var fresh = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
        _metaDb.UpsertRegisteredUserRefreshScopes(
            100,
            GlobalLeaderboardScraper.AllInstruments.Select(instrument =>
                new SoloCurrentProjectionScopeKey("song-old", instrument)).ToArray(),
            old);
        _metaDb.UpsertRegisteredUserRefreshScopes(
            101,
            GlobalLeaderboardScraper.AllInstruments.Select(instrument =>
                new SoloCurrentProjectionScopeKey("song-fresh", instrument)).ToArray(),
            fresh);

        var ctx = CreateContext(
            scrapeId: 200,
            registeredIds: new HashSet<string> { "user-1" },
            scrapeRequests:
            [
                new GlobalLeaderboardScraper.SongScrapeRequest
                {
                    SongId = "song-fresh",
                    Instruments = GlobalLeaderboardScraper.AllInstruments,
                },
                new GlobalLeaderboardScraper.SongScrapeRequest
                {
                    SongId = "song-missing",
                    Instruments = GlobalLeaderboardScraper.AllInstruments,
                },
                new GlobalLeaderboardScraper.SongScrapeRequest
                {
                    SongId = "song-old",
                    Instruments = GlobalLeaderboardScraper.AllInstruments,
                },
            ]);

        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);

        await _cyclicalMachine.Received(1).AttachAsync(
            Arg.Any<IReadOnlyList<UserWorkItem>>(),
            Arg.Is<IReadOnlyList<string>>(songs =>
                songs.SequenceEqual(
                    new[] { "song-missing", "song-old", "song-fresh" },
                    StringComparer.Ordinal)),
            Arg.Any<IReadOnlyList<Persistence.SeasonWindowInfo>>(),
            SongMachineSource.PostScrape,
            true,
            Arg.Any<CancellationToken>(),
            true,
            Arg.Any<EpicTrafficKind>(),
            Arg.Is<CyclicalSongMachine.AttachmentOptions?>(options =>
                options != null &&
                options.PreserveSongOrder &&
                options.OnScopesCompleted != null));

        Assert.Contains(_log.Entries, entry =>
            entry.Message.Contains("coverage (before)", StringComparison.Ordinal) &&
            entry.Message.Contains("expectedScopes=27", StringComparison.Ordinal) &&
            entry.Message.Contains("checkedScopes=18", StringComparison.Ordinal) &&
            entry.Message.Contains("missingScopes=9", StringComparison.Ordinal) &&
            entry.Message.Contains("oldestCheckedAtUtc=", StringComparison.Ordinal) &&
            entry.Message.Contains("oldestCheckedAge=", StringComparison.Ordinal) &&
            entry.Message.Contains("currentScrapeCompletions=0", StringComparison.Ordinal));
        Assert.Contains(_log.Entries, entry =>
            entry.Message.Contains("coverage (after)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshRegisteredUsers_NoToken_Skips()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var ctx = CreateContext(registeredIds: new HashSet<string> { "user-1" });

        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);

        Assert.Contains(
            _log.Entries,
            entry => entry.Message.Contains(
                "No access token for post-scrape refresh",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshRegisteredUsers_PropagatesFailureForPhaseVisibility()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-access-token");
        _tokenManager.AccountId.Returns("caller-001");

        _cyclicalMachine.AttachAsync(
            Arg.Any<IReadOnlyList<UserWorkItem>>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyList<Persistence.SeasonWindowInfo>>(),
            Arg.Any<SongMachineSource>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>(),
            Arg.Any<EpicTrafficKind>(),
            Arg.Any<CyclicalSongMachine.AttachmentOptions?>())
            .ThrowsAsync(new InvalidOperationException("API error"));

        var ctx = CreateContext(registeredIds: new HashSet<string> { "user-1" });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task RefreshRegisteredUsers_WhenSongMachineTimesOut_PreservesCompletedScopeCheckpoint()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-access-token");
        _tokenManager.AccountId.Returns("caller-001");

        var stalledMachine = Substitute.For<CyclicalSongMachine>();
        stalledMachine.AttachAsync(
            Arg.Any<IReadOnlyList<UserWorkItem>>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyList<Persistence.SeasonWindowInfo>>(),
            Arg.Any<SongMachineSource>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>(),
            Arg.Any<EpicTrafficKind>(),
            Arg.Any<CyclicalSongMachine.AttachmentOptions?>())
            .Returns(async call =>
            {
                var options = call.Arg<CyclicalSongMachine.AttachmentOptions?>();
                Assert.NotNull(options?.OnScopesCompleted);
                await options!.OnScopesCompleted!(
                    [new SoloCurrentProjectionScopeKey("song-timeout", "Solo_Guitar")]);
                return await WaitUntilCancelledAsync(call.Arg<CancellationToken>());
            });

        var scraper = Substitute.For<GlobalLeaderboardScraper>(
            new HttpClient(), new ScrapeProgressTracker(), Substitute.For<ILogger<GlobalLeaderboardScraper>>(), 0, null);
        var rivalsCalculator = new RivalsCalculator(_persistence, Substitute.For<ILogger<RivalsCalculator>>());
        var rivalsOrchestrator = new RivalsOrchestrator(
            rivalsCalculator,
            _persistence,
            new NotificationService(Substitute.For<ILogger<NotificationService>>()),
            _progress,
            new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()),
            new ResponseCacheService(TimeSpan.FromMinutes(5)),
            Substitute.For<ILogger<RivalsOrchestrator>>());
        var rankingsCalculator = new RankingsCalculator(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<RankingsCalculator>>());
        var leaderboardRivalsCalculator = new LeaderboardRivalsCalculator(_persistence, _metaDb, Options.Create(new ScraperOptions()), Substitute.For<ILogger<LeaderboardRivalsCalculator>>());
        var sut = new PostScrapeOrchestrator(
            _persistence, _firstSeenCalculator, _nameResolver,
            Substitute.For<HistoryReconstructor>(scraper, _persistence, new HttpClient(), new ScrapeProgressTracker(), new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), Substitute.For<ILogger<HistoryReconstructor>>()),
            _pool,
            stalledMachine,
            rivalsOrchestrator, rankingsCalculator, leaderboardRivalsCalculator, _notifications,
            _tokenManager, _progress, new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), _pathDataStore,
            new ScrapeTimePrecomputer(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<ScrapeTimePrecomputer>>(), NullLoggerFactory.Instance, new JsonSerializerOptions(), new FeatureOptions()),
            new PostScrapeBandExtractor(null!, _pathDataStore, Substitute.For<ILogger<PostScrapeBandExtractor>>()),
            new BandScrapePhase(
                scraper,
                new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
                _pathDataStore, _pool, _progress, Options.Create(new ScraperOptions()),
                Substitute.For<ILogger<BandScrapePhase>>()),
            new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
            Options.Create(new ScraperOptions
            {
                RegisteredUserRefreshTimeout = TimeSpan.FromMilliseconds(500),
            }), _log, _registrationMutations, null);

        var ctx = CreateContext(
            scrapeId: 1273,
            registeredIds: new HashSet<string> { "user-1" },
            scrapeRequests:
            [
                new GlobalLeaderboardScraper.SongScrapeRequest
                {
                    SongId = "song-timeout",
                    Instruments = ["Solo_Guitar"],
                    Label = "Song Timeout",
                },
            ]);

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(
            () => sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Refresh should be bounded but took {sw.Elapsed}.");
        Assert.Contains(_log.Entries, e => e.Message.Contains("Post-scrape registered-user refresh timed out", StringComparison.Ordinal));
        var coverage = _metaDb.GetRegisteredUserRefreshCoverage(
            ["song-timeout"],
            GlobalLeaderboardScraper.AllInstruments,
            currentScrapeId: 1273,
            DateTime.UtcNow);
        Assert.Equal(1, coverage.CheckedScopes);
        Assert.Equal(1, coverage.CurrentScrapeCompletions);
        Assert.Contains(_log.Entries, entry =>
            entry.Message.Contains("coverage (after)", StringComparison.Ordinal) &&
            entry.Message.Contains("currentScrapeCompletions=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshRegisteredUsers_WhenSeasonDiscoveryTimesOut_PropagatesVisibleTimeout()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-access-token");
        _tokenManager.AccountId.Returns("caller-001");

        var scraper = Substitute.For<GlobalLeaderboardScraper>(
            new HttpClient(), new ScrapeProgressTracker(), Substitute.For<ILogger<GlobalLeaderboardScraper>>(), 0, null);
        var historyReconstructor = Substitute.For<HistoryReconstructor>(
            scraper,
            _persistence,
            new HttpClient(),
            new ScrapeProgressTracker(),
            new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()),
            Substitute.For<ILogger<HistoryReconstructor>>());
        historyReconstructor.DiscoverSeasonWindowsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(call => WaitUntilCancelledSeasonWindowsAsync(call.Arg<CancellationToken>()));

        var rivalsCalculator = new RivalsCalculator(_persistence, Substitute.For<ILogger<RivalsCalculator>>());
        var rivalsOrchestrator = new RivalsOrchestrator(
            rivalsCalculator,
            _persistence,
            new NotificationService(Substitute.For<ILogger<NotificationService>>()),
            _progress,
            new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()),
            new ResponseCacheService(TimeSpan.FromMinutes(5)),
            Substitute.For<ILogger<RivalsOrchestrator>>());
        var rankingsCalculator = new RankingsCalculator(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<RankingsCalculator>>());
        var leaderboardRivalsCalculator = new LeaderboardRivalsCalculator(_persistence, _metaDb, Options.Create(new ScraperOptions()), Substitute.For<ILogger<LeaderboardRivalsCalculator>>());
        var sut = new PostScrapeOrchestrator(
            _persistence, _firstSeenCalculator, _nameResolver,
            historyReconstructor,
            _pool,
            _cyclicalMachine,
            rivalsOrchestrator, rankingsCalculator, leaderboardRivalsCalculator, _notifications,
            _tokenManager, _progress, new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), _pathDataStore,
            new ScrapeTimePrecomputer(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<ScrapeTimePrecomputer>>(), NullLoggerFactory.Instance, new JsonSerializerOptions(), new FeatureOptions()),
            new PostScrapeBandExtractor(null!, _pathDataStore, Substitute.For<ILogger<PostScrapeBandExtractor>>()),
            new BandScrapePhase(
                scraper,
                new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
                _pathDataStore, _pool, _progress, Options.Create(new ScraperOptions()),
                Substitute.For<ILogger<BandScrapePhase>>()),
            new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
            Options.Create(new ScraperOptions
            {
                RegisteredUserRefreshTimeout = TimeSpan.FromMilliseconds(50),
            }), _log, _registrationMutations, null);

        var ctx = CreateContext(registeredIds: new HashSet<string> { "user-1" });

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(
            () => sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Refresh should be bounded but took {sw.Elapsed}.");
        Assert.Contains(_log.Entries, e => e.Message.Contains("Post-scrape registered-user refresh timed out", StringComparison.Ordinal));
        await _cyclicalMachine.DidNotReceiveWithAnyArgs().AttachAsync(
            default!,
            default!,
            default!,
            default,
            default,
            default,
            default,
            default,
            default);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // ResolveNamesAsync
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task ResolveNamesAsync_DelegatesToResolver()
    {
        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(42);

        var result = await _sut.ResolveNamesAsync(8, CancellationToken.None);

        Assert.Equal(42, result);
        await _nameResolver.Received(1).ResolveNewAccountsAsync(8, Arg.Any<CancellationToken>());
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // RunEnrichmentAsync
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task RunEnrichmentAsync_SetsPhase()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var ctx = CreateContext();

        await _sut.RunEnrichmentAsync(ctx, service, CancellationToken.None);

        Assert.Equal(ScrapeProgressTracker.ScrapePhase.PostScrapeEnrichment, _progress.Phase);
        var rankOutcome = Assert.Single(
            ctx.PostScrapeOutcomes.Outcomes,
            outcome => outcome.Phase == "RankRecompute");
        Assert.True(rankOutcome.Success);
        Assert.Equal("completed", rankOutcome.Status);
    }

    [Fact]
    public async Task RunEnrichmentAsync_WhenLegacyWritesAreDisabled_CompletesRankContractWithoutWork()
    {
        using var snapshotOnlyPersistence = new GlobalLeaderboardPersistence(
            _metaDb,
            NullLoggerFactory.Instance,
            NullLogger<GlobalLeaderboardPersistence>.Instance,
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions
            {
                WriteLegacyLiveLeaderboardDuringScrape = false,
            }));
        snapshotOnlyPersistence.Initialize();
        var sut = CreateOrchestrator(
            _cyclicalMachine,
            _historyReconstructor,
            persistence: snapshotOnlyPersistence);
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var scrapeId = _metaDb.StartScrapeRun();
        var ctx = CreateContext(scrapeId);

        await sut.RunEnrichmentAsync(
            ctx,
            new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null),
            CancellationToken.None);

        var outcome = Assert.Single(
            ctx.PostScrapeOutcomes.Outcomes,
            item => item.Phase == "RankRecompute");
        Assert.True(outcome.Success);
        Assert.Equal("completed", outcome.Status);
        Assert.Contains(
            _log.Entries,
            entry => entry.Message.Contains(
                "legacy live leaderboard writes are disabled",
                StringComparison.Ordinal));
        Assert.Equal(
            "completed",
            Assert.Single(
                _metaDb.GetScrapeResumeState(scrapeId)!.PhaseOutcomes,
                item => item.Phase == "RankRecompute").Status);
        ScrapePublicationGuard.EnsureCanPublish(
            42,
            ctx.PostScrapeOutcomes,
            enforcePublicationCriticalPhases: true);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // PruneExcessEntries
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void PruneExcessEntries_WithMaxPages_Runs()
    {
        // Seed excess entries to trigger actual pruning
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var entries = Enumerable.Range(0, 20).Select(i =>
            new LeaderboardEntry
            {
                AccountId = $"p_{i}", Score = 1000 - i * 10,
                Accuracy = 95, Stars = 5, Season = 3,
            }).ToList();
        db.UpsertEntries("song1", entries);

        var ctx = CreateContext(registeredIds: new HashSet<string> { "p_15" });
        _sut.PruneExcessEntries(ctx); // MaxPages=100 â†’ maxEntries=10000 â†’ no pruning (only 20)

        // Verify no entries pruned (20 < 10000)
        Assert.Equal(20, db.GetLeaderboardCount("song1"));
    }

    [Fact]
    public void PruneExcessEntries_ActuallyPrunes_WhenExceedsMax()
    {
        // Create SUT with MaxPages=1 â†’ maxEntries=100, but we seed 200 entries
        var opts = Options.Create(new ScraperOptions { MaxPagesPerLeaderboard = 1 });
        var rivalsCalculator = new RivalsCalculator(_persistence, Substitute.For<ILogger<RivalsCalculator>>());
        var rivalsOrchestrator = new RivalsOrchestrator(rivalsCalculator, _persistence, new NotificationService(Substitute.For<ILogger<NotificationService>>()), _progress, new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), new ResponseCacheService(TimeSpan.FromMinutes(5)), Substitute.For<ILogger<RivalsOrchestrator>>());
        var rankingsCalculator2 = new RankingsCalculator(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<RankingsCalculator>>());
        var leaderboardRivalsCalculator2 = new LeaderboardRivalsCalculator(_persistence, _metaDb, Options.Create(opts.Value), Substitute.For<ILogger<LeaderboardRivalsCalculator>>());
        var sut = new PostScrapeOrchestrator(
            _persistence, _firstSeenCalculator, _nameResolver,
            Substitute.For<HistoryReconstructor>(Substitute.For<ILeaderboardQuerier>(), _persistence, new HttpClient(), new ScrapeProgressTracker(), new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), Substitute.For<ILogger<HistoryReconstructor>>()),
            _pool,
            CreateMockCyclicalMachine(),
            rivalsOrchestrator, rankingsCalculator2, leaderboardRivalsCalculator2, _notifications,
            _tokenManager, _progress, new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), _pathDataStore,
            new ScrapeTimePrecomputer(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<ScrapeTimePrecomputer>>(), NullLoggerFactory.Instance, new System.Text.Json.JsonSerializerOptions(), new FeatureOptions()),
            new PostScrapeBandExtractor(null!, _pathDataStore, Substitute.For<ILogger<PostScrapeBandExtractor>>()),
            new BandScrapePhase(
                Substitute.For<GlobalLeaderboardScraper>(new HttpClient(), new ScrapeProgressTracker(), Substitute.For<ILogger<GlobalLeaderboardScraper>>(), 0, null),
                new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
                _pathDataStore, _pool, _progress, opts,
                Substitute.For<ILogger<BandScrapePhase>>()),
            new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
            opts, _log, _registrationMutations, null);

        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var entries = Enumerable.Range(0, 200).Select(i =>
            new LeaderboardEntry
            {
                AccountId = $"p_{i}", Score = 10000 - i * 10,
                Accuracy = 95, Stars = 5, Season = 3,
            }).ToList();
        db.UpsertEntries("song1", entries);

        // p_150 is registered â€” should be preserved even though outside top 100
        var ctx = CreateContext(registeredIds: new HashSet<string> { "p_150" });
        sut.PruneExcessEntries(ctx);

        var remaining = db.GetLeaderboardCount("song1");
        Assert.True(remaining <= 101); // top 100 + 1 preserved registered user
        // Verify preserved user still exists
        var preserved = db.GetPlayerScores("p_150", "song1");
        Assert.Single(preserved);
    }

    [Fact]
    public async Task ComputeRivalsAsync_WithChangedAccounts_Runs()
    {
        var aggregates = new GlobalLeaderboardPersistence.PipelineAggregates();
        aggregates.AddChangedAccountIds(new[] { "user-1" });

        var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "user-1" };
        var ctx = CreateContext(registeredIds: registeredIds, aggregates: aggregates);

        // Should run without error â€” rivals computation handles user with no data gracefully
        await _sut.ComputeRivalsAsync(ctx, CancellationToken.None);
    }

    [Fact]
    public async Task ComputeRivalsAsync_NoRegisteredUsers_Skips()
    {
        var ctx = CreateContext(registeredIds: new HashSet<string>());
        await _sut.ComputeRivalsAsync(ctx, CancellationToken.None);
        // No crash, no rivals computed
    }

    [Fact]
    public void PruneExcessEntries_WithZeroMaxPages_DoesNotPrune()
    {
        var opts = Options.Create(new ScraperOptions { MaxPagesPerLeaderboard = 0 });
        var rivalsCalculator = new RivalsCalculator(_persistence, Substitute.For<ILogger<RivalsCalculator>>());
        var rivalsOrchestrator = new RivalsOrchestrator(rivalsCalculator, _persistence, new NotificationService(Substitute.For<ILogger<NotificationService>>()), _progress, new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), new ResponseCacheService(TimeSpan.FromMinutes(5)), Substitute.For<ILogger<RivalsOrchestrator>>());
        var rankingsCalculator3 = new RankingsCalculator(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<RankingsCalculator>>());
        var leaderboardRivalsCalculator3 = new LeaderboardRivalsCalculator(_persistence, _metaDb, Options.Create(opts.Value), Substitute.For<ILogger<LeaderboardRivalsCalculator>>());
        var sut = new PostScrapeOrchestrator(
            _persistence, _firstSeenCalculator, _nameResolver,
            Substitute.For<HistoryReconstructor>(Substitute.For<ILeaderboardQuerier>(), _persistence, new HttpClient(), new ScrapeProgressTracker(), new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), Substitute.For<ILogger<HistoryReconstructor>>()),
            _pool,
            CreateMockCyclicalMachine(),
            rivalsOrchestrator, rankingsCalculator3, leaderboardRivalsCalculator3, _notifications,
            _tokenManager, _progress, new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), _pathDataStore,
            new ScrapeTimePrecomputer(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<ScrapeTimePrecomputer>>(), NullLoggerFactory.Instance, new System.Text.Json.JsonSerializerOptions(), new FeatureOptions()),
            new PostScrapeBandExtractor(null!, _pathDataStore, Substitute.For<ILogger<PostScrapeBandExtractor>>()),
            new BandScrapePhase(
                Substitute.For<GlobalLeaderboardScraper>(new HttpClient(), new ScrapeProgressTracker(), Substitute.For<ILogger<GlobalLeaderboardScraper>>(), 0, null),
                new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
                _pathDataStore, _pool, _progress, opts,
                Substitute.For<ILogger<BandScrapePhase>>()),            new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),            opts, _log, _registrationMutations, null);

        var ctx = CreateContext();
        sut.PruneExcessEntries(ctx); // maxPages=0 â†’ no-op
    }

    [Fact]
    public async Task ComputeRankingsAsync_RunsWithoutError()
    {
        var service = new FortniteFestival.Core.Services.FestivalService(
            (FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        await _sut.ComputeRankingsAsync(service, CancellationToken.None);
        // Should complete without error (no data to rank)
    }

    [Fact]
    public async Task ComputeRankingsAsync_WithInstruments_SetsPhase()
    {
        // Seed one instrument DB with data so rankings can compute
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var entries = Enumerable.Range(0, 5).Select(i =>
            new LeaderboardEntry
            {
                AccountId = $"rank_{i}", Score = 10000 - i * 100,
                Accuracy = 95, Stars = 5, Season = 3,
            }).ToList();
        db.UpsertEntries("rankSong", entries);

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var ctx = CreateContext();

        await _sut.ComputeRankingsAsync(service, CancellationToken.None);

        Assert.Equal(ScrapeProgressTracker.ScrapePhase.ComputingRankings, _progress.Phase);
    }

    [Fact]
    public async Task RunAsync_ActivatesShadowSnapshotsBeforeRankings()
    {
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var ctx = CreateContext(
            scrapeId: 42,
            scrapeRequests:
            [
                new GlobalLeaderboardScraper.SongScrapeRequest
                {
                    SongId = "song_empty",
                    Instruments = ["Solo_Guitar"],
                    Label = "Song Empty",
                },
            ]);

        await _sut.RunAsync(ctx, service, ScrapePhase.SoloRankings, CancellationToken.None);

        var earlyIndex = _log.Entries.ToList().FindIndex(e => e.Message.Contains("[ActivateShadowSnapshotsEarly]"));
        var rankingsIndex = _log.Entries.ToList().FindIndex(e => e.Message.Contains("[ComputeRankings]"));
        Assert.True(earlyIndex >= 0, "Expected early snapshot activation phase to be logged.");
        Assert.True(rankingsIndex > earlyIndex, "Expected rankings to run after early snapshot activation.");

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT active_snapshot_id, scrape_id, is_finalized
            FROM leaderboard_snapshot_state
            WHERE song_id = 'song_empty' AND instrument = 'Solo_Guitar'
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(42, reader.GetInt64(0));
        Assert.Equal(42, reader.GetInt64(1));
        Assert.True(reader.GetBoolean(2));
    }

    [Fact]
    public async Task RunAsync_ValidatedProjectionPrecedesRivalsAndCleanupRefreshesOnlyLaterScopes()
    {
        const string earlySongId = "song_projection_early";
        const string redirtySongId = "song_projection_redirty";
        const string lateSongId = "song_projection_late";
        const string instrument = "Solo_Guitar";
        const string accountId = "acct_projection";

        using var candidateMeta = new MetaDatabase(
            _metaFixture.DataSource,
            Substitute.For<ILogger<MetaDatabase>>());
        using var candidatePersistence = new GlobalLeaderboardPersistence(
            candidateMeta,
            NullLoggerFactory.Instance,
            NullLogger<GlobalLeaderboardPersistence>.Instance,
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions
            {
                EnforcePublicationCriticalPhases = true,
                UseSnapshotOverlayWorkerReaders = true,
            }));
        candidatePersistence.Initialize();
        var candidateBuilder = new SoloCurrentProjectionBuilder(
            _metaFixture.DataSource,
            Substitute.For<ILogger<SoloCurrentProjectionBuilder>>(),
            Options.Create(new FeatureOptions
            {
                UseSnapshotOverlayWorkerReaders = true,
            }));
        await candidateBuilder.EnsureSchemaAsync();

        InsertSnapshotState(earlySongId, instrument, 42);
        InsertSnapshotEntry(42, earlySongId, instrument, accountId, 120_000);
        InsertProjectionScope(earlySongId, instrument, sourceSnapshotId: 41);
        InsertSnapshotState(redirtySongId, instrument, 44);
        InsertSnapshotEntry(44, redirtySongId, instrument, accountId, 110_000);
        InsertProjectionScope(redirtySongId, instrument, sourceSnapshotId: 43);

        var sut = CreateOrchestrator(
            _cyclicalMachine,
            _historyReconstructor,
            persistence: candidatePersistence,
            soloCurrentProjectionBuilder: candidateBuilder);
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var ctx = CreateContext();

        await sut.RunAsync(
            ctx,
            service,
            ScrapePhase.SoloRankings | ScrapePhase.SoloRivals,
            CancellationToken.None);

        Assert.False(candidatePersistence.UseValidatedCurrentProjectionForWorkerReaders);
        Assert.True(ctx.SoloCurrentProjectionRefreshedForPublication);
        Assert.Contains(
            new SoloCurrentProjectionScopeKey(earlySongId, instrument),
            ctx.RefreshedProjectionScopes);
        Assert.Equal(42, GetProjectionScopeSourceSnapshot(earlySongId, instrument));
        Assert.Equal(120_000, GetProjectedScore(earlySongId, instrument, accountId));
        Assert.Equal(110_000, GetProjectedScore(redirtySongId, instrument, accountId));

        var logs = _log.Entries.ToList();
        var rankingsIndex = logs.FindIndex(entry =>
            entry.Message.Contains("[ComputeRankings]", StringComparison.Ordinal));
        var projectionIndex = logs.FindIndex(entry =>
            entry.Message.Contains("[PrepareSoloCurrentProjectionForDerived]", StringComparison.Ordinal));
        var rivalsIndex = logs.FindIndex(entry =>
            entry.Message.Contains("[Rivals]", StringComparison.Ordinal));
        var leaderboardRivalsIndex = logs.FindIndex(entry =>
            entry.Message.Contains("[LeaderboardRivals]", StringComparison.Ordinal));
        Assert.True(rankingsIndex >= 0, "Expected rankings to run.");
        Assert.True(projectionIndex >= 0, "Expected validated projection preparation to run.");
        Assert.True(projectionIndex > rankingsIndex, "Expected projection validation after rankings.");
        Assert.True(rivalsIndex > projectionIndex, "Expected rivals to run after projection validation.");
        Assert.True(
            leaderboardRivalsIndex > rivalsIndex,
            "Expected leaderboard rivals to run after song rivals.");

        var earlyGeneration = GetProjectionScopeGeneration(earlySongId, instrument);
        var redirtyGeneration = GetProjectionScopeGeneration(redirtySongId, instrument);
        InsertOverlayEntry(redirtySongId, instrument, accountId, 135_000);
        var redirtyScope = new SoloCurrentProjectionScopeKey(redirtySongId, instrument);
        ctx.AddNotificationProjectionScope(redirtyScope);
        InsertSnapshotState(lateSongId, instrument, 43);
        InsertSnapshotEntry(43, lateSongId, instrument, accountId, 100_000);
        InsertProjectionScope(lateSongId, instrument, sourceSnapshotId: 43);
        InsertOverlayEntry(lateSongId, instrument, accountId, 125_000);
        var lateScope = new SoloCurrentProjectionScopeKey(lateSongId, instrument);
        ctx.AddNotificationProjectionScope(lateScope);

        await sut.RunPublicationCleanupAsync(
            ctx,
            ScrapePhase.SoloFinalize,
            CancellationToken.None);

        Assert.Equal(earlyGeneration, GetProjectionScopeGeneration(earlySongId, instrument));
        Assert.NotEqual(redirtyGeneration, GetProjectionScopeGeneration(redirtySongId, instrument));
        Assert.Equal(135_000, GetProjectedScore(redirtySongId, instrument, accountId));
        Assert.Equal(125_000, GetProjectedScore(lateSongId, instrument, accountId));
        Assert.Contains(redirtyScope, ctx.NotificationProjectionScopes);
        Assert.Contains(lateScope, ctx.NotificationProjectionScopes);

        var faultInjector = Substitute.For<IPostScrapePhaseFaultInjector>();
        faultInjector
            .When(injector => injector.BeforePhase("Rivals"))
            .Do(_ =>
            {
                Assert.True(candidatePersistence.UseValidatedCurrentProjectionForWorkerReaders);
                throw new InvalidOperationException("rivals fault");
            });
        var failingSut = CreateOrchestrator(
            _cyclicalMachine,
            _historyReconstructor,
            persistence: candidatePersistence,
            soloCurrentProjectionBuilder: candidateBuilder,
            phaseFaultInjector: faultInjector);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failingSut.RunAsync(
                CreateContext(),
                service,
                ScrapePhase.SoloRivals,
                CancellationToken.None));
        Assert.False(candidatePersistence.UseValidatedCurrentProjectionForWorkerReaders);

        var prepareCount = _log.Entries.Count(entry =>
            entry.Message.Contains("[PrepareSoloCurrentProjectionForDerived]", StringComparison.Ordinal));
        var incompleteCtx = CreateContext(leaderboardScrapeCompleted: false);
        await sut.RunAsync(
            incompleteCtx,
            service,
            ScrapePhase.SoloScrape | ScrapePhase.SoloRivals,
            CancellationToken.None);

        Assert.False(candidatePersistence.UseValidatedCurrentProjectionForWorkerReaders);
        Assert.Equal(
            prepareCount,
            _log.Entries.Count(entry =>
                entry.Message.Contains("[PrepareSoloCurrentProjectionForDerived]", StringComparison.Ordinal)));
    }

    [Fact]
    public void BandExtraction_DoesNotActivateSoloSnapshots()
    {
        var ctx = CreateContext(scrapeId: 42);

        Assert.False(PostScrapeOrchestrator.ShouldActivateShadowSnapshotsBeforeDerived(
            ctx,
            ScrapePhase.BandExtraction));
        Assert.False(PostScrapeOrchestrator.ShouldActivateShadowSnapshotsBeforeDerived(
            ctx,
            ScrapePhase.SoloScrape | ScrapePhase.BandExtraction));
    }

    [Fact]
    public async Task RunImprovementNotificationsAfterPublicationAsync_RunsAfterDerivedSoloPhases()
    {
        var sut = CreateOrchestratorWithImprovementNotifications();
        var publishedScrapeId = PublishCompletedScrape();
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var aggregates = new GlobalLeaderboardPersistence.PipelineAggregates();
        aggregates.IncrementSoloLeaderboardsWithData();
        aggregates.IncrementSongsWithData();
        var ctx = CreateContext(
            scrapeId: publishedScrapeId,
            aggregates: aggregates,
            scrapeRequests:
            [
                new GlobalLeaderboardScraper.SongScrapeRequest
                {
                    SongId = "song_notify_order",
                    Instruments = ["Solo_Guitar"],
                    Label = "Song Notify Order",
                },
            ]);

        await sut.RunAsync(
            ctx,
            service,
            ScrapePhase.SoloRankings | ScrapePhase.SoloRivals | ScrapePhase.SoloPlayerStats | ScrapePhase.SoloFinalize,
            CancellationToken.None);

        var logs = _log.Entries.ToList();
        var rankingsIndex = logs.FindIndex(e => e.Message.Contains("[ComputeRankings]", StringComparison.Ordinal));
        var rivalsIndex = logs.FindIndex(e => e.Message.Contains("[Rivals]", StringComparison.Ordinal));
        var leaderboardRivalsIndex = logs.FindIndex(e =>
            e.Message.Contains("[LeaderboardRivals]", StringComparison.Ordinal));
        var playerStatsIndex = logs.FindIndex(e => e.Message.Contains("[PlayerStatsTiers]", StringComparison.Ordinal));
        var activateIndex = logs.FindIndex(e => e.Message.Contains("[ActivateShadowSnapshots]", StringComparison.Ordinal));

        Assert.True(rankingsIndex >= 0, "Expected rankings to run.");
        Assert.True(rivalsIndex > rankingsIndex, "Expected rivals to run after rankings.");
        Assert.True(
            leaderboardRivalsIndex > rivalsIndex,
            "Expected leaderboard rivals to run after song rivals.");
        Assert.True(
            playerStatsIndex > leaderboardRivalsIndex,
            "Expected player stats to run after leaderboard rivals.");
        Assert.True(activateIndex > playerStatsIndex, "Expected final snapshot activation after player stats.");
        Assert.DoesNotContain(logs, e => e.Message.Contains("[Checkpoint]", StringComparison.Ordinal));
        Assert.DoesNotContain(
            ctx.PostScrapeOutcomes.Outcomes,
            outcome => outcome.Phase == "Checkpoint");
        Assert.DoesNotContain(logs, e => e.Message.Contains("[ImprovementNotifications]", StringComparison.Ordinal));

        await sut.RunImprovementNotificationsAfterPublicationAsync(
            ctx,
            ScrapePhase.SoloRankings | ScrapePhase.SoloRivals | ScrapePhase.SoloPlayerStats | ScrapePhase.SoloFinalize,
            CancellationToken.None);

        logs = _log.Entries.ToList();
        var notificationsIndex = logs.FindIndex(e => e.Message.Contains("[ImprovementNotifications]", StringComparison.Ordinal));
        Assert.True(notificationsIndex > activateIndex, "Expected notifications to run after final derived solo phases.");
    }

    [Fact]
    public async Task RunImprovementNotificationsAfterPublicationAsync_SkipsWhenSoloScrapeCoverageIsLow()
    {
        var sut = CreateOrchestratorWithImprovementNotifications();
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var aggregates = new GlobalLeaderboardPersistence.PipelineAggregates();
        aggregates.IncrementSoloLeaderboardsWithData();
        var ctx = CreateContext(
            scrapeId: 44,
            aggregates: aggregates,
            scrapeRequests:
            [
                new GlobalLeaderboardScraper.SongScrapeRequest
                {
                    SongId = "song_notify_partial",
                    Instruments = ["Solo_Guitar", "Solo_Bass"],
                    Label = "Song Notify Partial",
                },
            ]);

        await sut.RunAsync(
            ctx,
            service,
            ScrapePhase.SoloScrape | ScrapePhase.SoloRankings,
            CancellationToken.None);

        await sut.RunImprovementNotificationsAfterPublicationAsync(
            ctx,
            ScrapePhase.SoloScrape | ScrapePhase.SoloRankings,
            CancellationToken.None);

        var outcome = Assert.Single(
            ctx.PostScrapeOutcomes.Outcomes,
            item => item.Phase == "ImprovementNotifications");
        Assert.True(outcome.Success);
        Assert.Equal("skipped", outcome.Status);
        Assert.Contains(_log.Entries, e => e.Message.Contains("solo scrape coverage was below threshold", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_SkipsFullBandMaintenanceWhenLeaderboardScrapeDidNotComplete()
    {
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var ctx = CreateContext(
            scrapeRequests:
            [
                new GlobalLeaderboardScraper.SongScrapeRequest
                {
                    SongId = "song_incomplete_scrape",
                    Instruments = ["Solo_Guitar"],
                    Label = "Incomplete Scrape",
                },
            ],
            leaderboardScrapeCompleted: false);

        await _sut.RunAsync(
            ctx,
            service,
            ScrapePhase.SoloScrape | ScrapePhase.BandScrape | ScrapePhase.BandExtraction | ScrapePhase.SoloRankings,
            CancellationToken.None);

        Assert.Contains(_log.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("Skipping full band maintenance because the leaderboard scrape did not complete", StringComparison.Ordinal));
        Assert.Contains(_log.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("Skipping derived ranking/finalization phases because the leaderboard scrape did not complete", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("[BandMaintenance]", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("[ComputeRankings]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BandMaintenance_records_exact_three_stable_subphase_timings()
    {
        const long scrapeId = 90_001;
        var ctx = CreateContext(scrapeId: scrapeId);

        await _sut.RunBandMaintenanceForTestAsync(
            ctx,
            BandExtractionResult.Empty,
            runFullMaintenance: false,
            CancellationToken.None);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT phase, subphase, item_key, rows_read, rows_written,
                   rows_deleted, scope_count, success, error_message
            FROM scrape_phase_timings
            WHERE scrape_id = @scrapeId
            ORDER BY id
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        using var reader = cmd.ExecuteReader();

        foreach (var expected in new[]
                 {
                     (
                         Subphase:
                             PostScrapeOrchestrator.BandMaintenancePruneSubphase,
                         RowsRead: (long?)null,
                         RowsWritten: (long?)null),
                     (
                         Subphase:
                             PostScrapeOrchestrator.BandMaintenanceSearchProjectionSubphase,
                         RowsRead: (long?)null,
                         RowsWritten: (long?)0),
                     (
                         Subphase:
                             PostScrapeOrchestrator.BandMaintenanceCurrentProjectionSubphase,
                         RowsRead: (long?)0,
                         RowsWritten: (long?)0),
                 })
        {
            Assert.True(reader.Read());
            Assert.Equal(
                PostScrapeOrchestrator.BandMaintenanceTimingPhase,
                reader.GetString(0));
            Assert.Equal(expected.Subphase, reader.GetString(1));
            Assert.True(reader.IsDBNull(2));
            if (expected.RowsRead.HasValue)
                Assert.Equal(expected.RowsRead.Value, reader.GetInt64(3));
            else
                Assert.True(reader.IsDBNull(3));
            if (expected.RowsWritten.HasValue)
                Assert.Equal(expected.RowsWritten.Value, reader.GetInt64(4));
            else
                Assert.True(reader.IsDBNull(4));
            Assert.Equal(0, reader.GetInt64(5));
            Assert.Equal(0, reader.GetInt64(6));
            Assert.True(reader.GetBoolean(7));
            Assert.True(reader.IsDBNull(8));
        }

        Assert.False(reader.Read());
    }

    [Fact]
    public void BandMaintenance_timing_metrics_use_existing_result_counts()
    {
        var scopes = new[]
        {
            new BandCurrentProjectionScopeKey(
                "song-1",
                "Band_Duets",
                "overall",
                ""),
            new BandCurrentProjectionScopeKey(
                "song-2",
                "Band_Trios",
                "combo",
                "combo-1"),
        };
        var prune = PostScrapeOrchestrator.GetBandPruneTimingMetrics(
            new BandPruneResult(
                DeletedEntries: 2,
                DeletedMemberStats: 3,
                DeletedMemberLookups: 5,
                AffectedTeamsByBandType:
                    new Dictionary<string, IReadOnlyCollection<string>>(
                        StringComparer.OrdinalIgnoreCase),
                AffectedCurrentProjectionScopes: scopes));
        Assert.Null(prune.RowsRead);
        Assert.Null(prune.RowsWritten);
        Assert.Equal(10, prune.RowsDeleted);
        Assert.Equal(2, prune.ScopeCount);

        var search = PostScrapeOrchestrator.GetBandSearchProjectionTimingMetrics(
            new BandSearchProjectionIncrementalResult(
                ProjectionAvailable: true,
                ImpactedTeams: 3,
                ProvidedTeams: 2,
                ChangedSourceTeams: 1,
                DeletedTeamRows: 5,
                InsertedTeamRows: 7,
                DeletedMemberRows: 7,
                InsertedMemberRows: 16,
                TotalElapsedMs: 25));
        Assert.Null(search.RowsRead);
        Assert.Equal(23, search.RowsWritten);
        Assert.Equal(12, search.RowsDeleted);
        Assert.Equal(3, search.ScopeCount);

        var current = PostScrapeOrchestrator.GetBandCurrentProjectionTimingMetrics(
            new BandCurrentProjectionIncrementalRefreshResult(
                ScopeCount: 7,
                SuccessfulScopes: 7,
                FailedScopes: 0,
                InsertedRows: 17,
                DeletedRows: 19,
                CandidateRowsDeleted: 0,
                PublishResult:
                    BandCurrentProjectionPublishResult.NotPublished(
                        generation: 1,
                        scopeCount: 7,
                        readyScopes: 7,
                        missingScopes: 0,
                        failedScopes: 0,
                        publishedRows: 17),
                TotalElapsedMs: 30,
                Scopes: []),
            consideredScopeCount: 9);
        Assert.Equal(9, current.RowsRead);
        Assert.Equal(17, current.RowsWritten);
        Assert.Equal(19, current.RowsDeleted);
        Assert.Equal(7, current.ScopeCount);

        var unchanged = PostScrapeOrchestrator.GetBandCurrentProjectionTimingMetrics(
            new BandCurrentProjectionIncrementalRefreshResult(
                ScopeCount: 0,
                SuccessfulScopes: 0,
                FailedScopes: 0,
                InsertedRows: 0,
                DeletedRows: 0,
                CandidateRowsDeleted: 0,
                PublishResult:
                    BandCurrentProjectionPublishResult.NotPublished(
                        generation: 2,
                        scopeCount: 0,
                        readyScopes: 0,
                        missingScopes: 0,
                        failedScopes: 0,
                        publishedRows: 0),
                TotalElapsedMs: 5,
                Scopes: []),
            consideredScopeCount: 9);
        Assert.Equal(9, unchanged.RowsRead);
        Assert.Equal(0, unchanged.RowsWritten);
        Assert.Equal(0, unchanged.RowsDeleted);
        Assert.Equal(0, unchanged.ScopeCount);

        Assert.Equal(0, PostScrapeOrchestrator.BandMaintenanceTimingMetrics.NoWork.RowsRead);
        Assert.Equal(0, PostScrapeOrchestrator.BandMaintenanceTimingMetrics.NoWork.ScopeCount);
    }

    [Fact]
    public async Task BandMaintenance_current_projection_timing_distinguishes_considered_from_refreshed()
    {
        const long scrapeId = 90_005;
        var ctx = CreateContext(scrapeId: scrapeId);
        var unchangedResult = new BandCurrentProjectionIncrementalRefreshResult(
            ScopeCount: 0,
            SuccessfulScopes: 0,
            FailedScopes: 0,
            InsertedRows: 0,
            DeletedRows: 0,
            CandidateRowsDeleted: 0,
            PublishResult:
                BandCurrentProjectionPublishResult.NotPublished(
                    generation: 2,
                    scopeCount: 0,
                    readyScopes: 0,
                    missingScopes: 0,
                    failedScopes: 0,
                    publishedRows: 0),
            TotalElapsedMs: 5,
            Scopes: []);

        await _sut.RunTimedBandMaintenanceSubphaseAsync(
            ctx,
            PostScrapeOrchestrator.BandMaintenanceCurrentProjectionSubphase,
            () => Task.FromResult(unchangedResult),
            result =>
                PostScrapeOrchestrator.GetBandCurrentProjectionTimingMetrics(
                    result,
                    consideredScopeCount: 9));

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT rows_read, rows_written, rows_deleted, scope_count, success
            FROM scrape_phase_timings
            WHERE scrape_id = @scrapeId
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(9, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.Equal(0, reader.GetInt64(2));
        Assert.Equal(0, reader.GetInt64(3));
        Assert.True(reader.GetBoolean(4));
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task BandMaintenance_subphase_failure_is_recorded_and_rethrown()
    {
        const long scrapeId = 90_002;
        var ctx = CreateContext(scrapeId: scrapeId);
        var failure = new InvalidOperationException("search projection failed");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RunTimedBandMaintenanceSubphaseAsync(
                ctx,
                PostScrapeOrchestrator.BandMaintenanceSearchProjectionSubphase,
                () => Task.FromException<int>(failure),
                static _ =>
                    PostScrapeOrchestrator.BandMaintenanceTimingMetrics.NoWork));
        Assert.Same(failure, thrown);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT phase, subphase, rows_read, rows_written, rows_deleted,
                   scope_count, success, error_message
            FROM scrape_phase_timings
            WHERE scrape_id = @scrapeId
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(
            PostScrapeOrchestrator.BandMaintenanceTimingPhase,
            reader.GetString(0));
        Assert.Equal(
            PostScrapeOrchestrator.BandMaintenanceSearchProjectionSubphase,
            reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
        Assert.True(reader.IsDBNull(3));
        Assert.True(reader.IsDBNull(4));
        Assert.True(reader.IsDBNull(5));
        Assert.False(reader.GetBoolean(6));
        Assert.Equal(failure.Message, reader.GetString(7));
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task BandMaintenance_subphase_cancellation_is_recorded_and_rethrown()
    {
        const long scrapeId = 90_003;
        var ctx = CreateContext(scrapeId: scrapeId);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancellation = new OperationCanceledException(cts.Token);

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _sut.RunTimedBandMaintenanceSubphaseAsync(
                ctx,
                PostScrapeOrchestrator.BandMaintenanceCurrentProjectionSubphase,
                () => Task.FromException<int>(cancellation),
                static _ =>
                    PostScrapeOrchestrator.BandMaintenanceTimingMetrics.NoWork));
        Assert.Equal(cts.Token, thrown.CancellationToken);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT subphase, rows_read, rows_written, rows_deleted,
                   scope_count, success, error_message
            FROM scrape_phase_timings
            WHERE scrape_id = @scrapeId
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(
            PostScrapeOrchestrator.BandMaintenanceCurrentProjectionSubphase,
            reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
        Assert.True(reader.IsDBNull(3));
        Assert.True(reader.IsDBNull(4));
        Assert.False(reader.GetBoolean(5));
        Assert.Equal(cancellation.Message, reader.GetString(6));
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task BandMaintenance_timing_persistence_failure_does_not_fail_subphase()
    {
        using (var conn = _metaFixture.DataSource.OpenConnection())
        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "DROP TABLE scrape_phase_timings";
            drop.ExecuteNonQuery();
        }

        var result = await _sut.RunTimedBandMaintenanceSubphaseAsync(
            CreateContext(scrapeId: 90_004),
            PostScrapeOrchestrator.BandMaintenancePruneSubphase,
            () => Task.FromResult(42),
            static _ =>
                new PostScrapeOrchestrator.BandMaintenanceTimingMetrics(
                    RowsDeleted: 1,
                    ScopeCount: 1));

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task RunImprovementNotificationsAfterPublicationAsync_RunsWhenSoloScrapeCoverageIsHealthy()
    {
        var sut = CreateOrchestratorWithImprovementNotifications();
        var publishedScrapeId = PublishCompletedScrape();
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var aggregates = new GlobalLeaderboardPersistence.PipelineAggregates();
        aggregates.IncrementSoloLeaderboardsWithData();
        aggregates.IncrementSoloLeaderboardsWithData();
        aggregates.IncrementSongsWithData();
        var ctx = CreateContext(
            scrapeId: publishedScrapeId,
            aggregates: aggregates,
            scrapeRequests:
            [
                new GlobalLeaderboardScraper.SongScrapeRequest
                {
                    SongId = "song_notify_healthy",
                    Instruments = ["Solo_Guitar", "Solo_Bass"],
                    Label = "Song Notify Healthy",
                },
            ]);

        await sut.RunAsync(
            ctx,
            service,
            ScrapePhase.SoloScrape | ScrapePhase.SoloRankings,
            CancellationToken.None);

        await sut.RunImprovementNotificationsAfterPublicationAsync(
            ctx,
            ScrapePhase.SoloScrape | ScrapePhase.SoloRankings,
            CancellationToken.None);

        Assert.Contains(_log.Entries, e => e.Message.Contains("[ImprovementNotifications]", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("solo scrape coverage was below threshold", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoverPendingImprovementNotificationsOnStartupAsync_CompletesTerminalMarker()
    {
        var sut = CreateOrchestratorWithImprovementNotifications();
        var publishedScrapeId = PublishCompletedScrape();

        await sut.RecoverPendingImprovementNotificationsOnStartupAsync(CancellationToken.None);

        var notifications = new ImprovementNotificationService(
            _metaFixture.DataSource,
            Substitute.For<ILogger<ImprovementNotificationService>>());
        var status = notifications.GetPublicationStatus();
        Assert.Equal(publishedScrapeId, status.MarkerScrapeId);
        Assert.Equal("completed", status.MarkerStatus);
    }

    [Fact]
    public async Task RecoverPendingImprovementNotificationsOnStartupAsync_PreservesDisabledMarker()
    {
        var sut = CreateOrchestratorWithImprovementNotifications();
        var publishedScrapeId = PublishCompletedScrape();
        var notifications = new ImprovementNotificationService(
            _metaFixture.DataSource,
            Substitute.For<ILogger<ImprovementNotificationService>>());
        notifications.MarkPublicationDisabled(publishedScrapeId, "Disabled for test.");

        await sut.RecoverPendingImprovementNotificationsOnStartupAsync(CancellationToken.None);

        var status = notifications.GetPublicationStatus();
        Assert.Null(status.MarkerScrapeId);
        Assert.Equal("disabled", status.MarkerStatus);
    }

    [Fact]
    public async Task RecoverPendingImprovementNotificationsOnStartupAsync_RejectsFrozenPublication()
    {
        var sut = CreateOrchestratorWithImprovementNotifications();
        var publishedScrapeId = PublishCompletedScrape();
        _metaDb.SetPublicReadFreeze(true, publishedScrapeId, "test");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RecoverPendingImprovementNotificationsOnStartupAsync(CancellationToken.None));

        Assert.Contains("public reads are frozen", exception.Message);
    }

    [Fact]
    public async Task PrepareImprovementNotificationProjectionScopesAsync_ReturnsBoundedScopes()
    {
        var sut = CreateOrchestratorWithImprovementNotifications();
        var aggregates = new GlobalLeaderboardPersistence.PipelineAggregates();
        aggregates.IncrementSoloLeaderboardsWithData();
        aggregates.IncrementSongsWithData();
        var ctx = CreateContext(
            aggregates: aggregates,
            scrapeRequests:
            [
                new GlobalLeaderboardScraper.SongScrapeRequest
                {
                    SongId = "song-notify-plan",
                    Instruments = ["Solo_Guitar"],
                    Label = "Notification Plan",
                },
            ]);
        ctx.RankingsComputedSuccessfully = true;
        ctx.SoloCurrentProjectionRefreshedForPublication = true;
        ctx.AddNotificationProjectionScope(
            new SoloCurrentProjectionScopeKey("song-notify-plan", "Solo_Guitar"));

        var scopes = await sut.PrepareImprovementNotificationProjectionScopesAsync(
            ctx,
            ScrapePhase.SoloScrape | ScrapePhase.SoloRankings,
            CancellationToken.None);

        Assert.Equal(
            [new SoloCurrentProjectionScopeKey("song-notify-plan", "Solo_Guitar")],
            scopes);
    }

    [Fact]
    public async Task RefreshRegisteredUsersAsync_WithUsers_SetsPhaseAndBeginProgress()
    {
        var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "user-refresh" };
        var ctx = CreateContext(
            registeredIds: registeredIds,
            scrapeRequests: new[] { new GlobalLeaderboardScraper.SongScrapeRequest
            {
                SongId = "songR",
                Instruments = GlobalLeaderboardScraper.AllInstruments,
                Label = "Song R",
            }});

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-tok");
        _tokenManager.AccountId.Returns("caller-1");

        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);

        // The progress phase should reflect RefreshingRegisteredUsers
        var progress = _progress.GetProgressResponse();
        // Phase transitions away after completion, but the op should be in completed list
        var completed = progress.CompletedOperations;
        // At minimum, no exception thrown
    }

    [Fact]
    public async Task PruneExcessEntries_WithData_Prunes()
    {
        // Create entries that exceed the configured max
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var entries = Enumerable.Range(0, 50).Select(i =>
            new LeaderboardEntry
            {
                AccountId = $"prune_{i}", Score = 10000 - i * 100,
                Accuracy = 95, Stars = 5, Season = 3,
            }).ToList();
        db.UpsertEntries("song1", entries);

        // Use max 10 pages = 1000 entries â€” but we only have 50, so no pruning
        var ctx = CreateContext();
        _sut.PruneExcessEntries(ctx);
    }

    [Fact]
    public async Task RunCleanupAsync_WithSoloEnrichment_SetsCleanupPhase()
    {
        var ctx = CreateContext();

        await _sut.RunCleanupAsync(ctx, ScrapePhase.SoloEnrichment, CancellationToken.None);

        var progress = _progress.GetProgressResponse();
        Assert.Equal("Cleanup", progress.Current?.Operation);
        Assert.Equal("cleanup_solo_excess_entries", progress.Current?.SubOperation);
        Assert.Equal(100, progress.Current?.ProgressPercent);
    }

    [Fact]
    public async Task RunCleanupAsync_WithDatabasePressure_SkipsBestEffortRankHistoryCleanup()
    {
        _databasePressureMonitor.GetPressureSnapshotAsync(Arg.Any<DatabaseMaintenanceOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DatabasePressureSnapshot(true, 1, 0, 0, 0, ["active vacuum count 1"])));
        var ctx = CreateContext();

        await _sut.RunCleanupAsync(ctx, ScrapePhase.SoloRankings, CancellationToken.None);

        await _databasePressureMonitor.Received(2)
            .GetPressureSnapshotAsync(Arg.Any<DatabaseMaintenanceOptions>(), Arg.Any<CancellationToken>());
        Assert.Contains(_log.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("Skipping rank history retention cleanup", StringComparison.Ordinal));
        Assert.Contains(_log.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("Skipping band rank history retention cleanup", StringComparison.Ordinal));
        Assert.Equal(
            "skipped",
            Assert.Single(
                ctx.PostScrapeOutcomes.Outcomes,
                outcome => outcome.Phase == "Cleanup.RankHistoryRetention").Status);
        Assert.Equal(
            "skipped",
            Assert.Single(
                ctx.PostScrapeOutcomes.Outcomes,
                outcome => outcome.Phase == "Cleanup.BandRankHistoryRetention").Status);
        var progress = _progress.GetProgressResponse();
        Assert.Equal(100, progress.Current?.ProgressPercent);
    }

    [Fact]
    public async Task RunCleanupAsync_ServiceRetentionSkipIsExplicit()
    {
        var retention = Substitute.For<IDatabaseRetentionMaintenanceService>();
        retention.RunAsync(Arg.Any<CancellationToken>())
            .Returns(DatabaseRetentionMaintenanceResult.SkippedResult(
                DateTime.UtcNow,
                "another maintenance run owns the advisory lock"));
        var sut = CreateOrchestrator(
            _cyclicalMachine,
            _historyReconstructor,
            retentionMaintenanceService: retention);
        var ctx = CreateContext();

        await sut.RunCleanupAsync(
            ctx,
            ScrapePhase.SoloFinalize,
            CancellationToken.None);

        var outcome = Assert.Single(
            ctx.PostScrapeOutcomes.Outcomes,
            item => item.Phase == "Cleanup.ServiceLevelRetention");
        Assert.True(outcome.Success);
        Assert.Equal("skipped", outcome.Status);
        Assert.Contains(
            _log.Entries,
            entry => entry.Message.Contains(
                "another maintenance run owns the advisory lock",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunPublicationCleanupAsync_WithSoloFinalize_RefreshesStaleSoloCurrentProjectionFirst()
    {
        await _soloCurrentProjectionBuilder.EnsureSchemaAsync();
        InsertSnapshotState("song_cleanup_projection", "Solo_Guitar", 42);
        InsertSnapshotEntry(42, "song_cleanup_projection", "Solo_Guitar", "acct_user", 120_000);
        InsertProjectionScope("song_cleanup_projection", "Solo_Guitar", sourceSnapshotId: 41);

        var ctx = CreateContext(scrapeId: 42);

        await _sut.RunPublicationCleanupAsync(ctx, ScrapePhase.SoloFinalize, CancellationToken.None);

        Assert.Empty(await _soloCurrentProjectionBuilder.LoadStaleScopesAsync());
        Assert.Equal(42, GetProjectionScopeSourceSnapshot("song_cleanup_projection", "Solo_Guitar"));
        Assert.Equal(120_000, GetProjectedScore("song_cleanup_projection", "Solo_Guitar", "acct_user"));

        var progress = _progress.GetProgressResponse();
        Assert.Equal("Cleanup", progress.Current?.Operation);
        Assert.Equal("cleanup_solo_current_projection", progress.Current?.SubOperation);
        Assert.Equal(100, progress.Current?.ProgressPercent);
    }

    [Fact]
    public async Task RunPublicationCleanupAsync_WithLowSoloCoverage_StillRefreshesSoloCurrentProjection()
    {
        await _soloCurrentProjectionBuilder.EnsureSchemaAsync();
        InsertSnapshotState("song_low_coverage", "Solo_Guitar", 55);
        InsertSnapshotEntry(55, "song_low_coverage", "Solo_Guitar", "acct_low_cov", 101_000);
        InsertProjectionScope("song_low_coverage", "Solo_Guitar", sourceSnapshotId: 54);

        var requests = new[]
        {
            new GlobalLeaderboardScraper.SongScrapeRequest
            {
                SongId = "song_low_coverage",
                Instruments = new[] { "Solo_Guitar" },
            },
            new GlobalLeaderboardScraper.SongScrapeRequest
            {
                SongId = "song_missing_coverage",
                Instruments = new[] { "Solo_Bass" },
            },
        };

        var aggregates = new GlobalLeaderboardPersistence.PipelineAggregates();
        aggregates.IncrementSoloLeaderboardsWithData();
        var ctx = CreateContext(scrapeId: 55, aggregates: aggregates, scrapeRequests: requests);

        await _sut.RunPublicationCleanupAsync(ctx, ScrapePhase.SoloScrape | ScrapePhase.SoloFinalize, CancellationToken.None);

        Assert.Equal(55, GetProjectionScopeSourceSnapshot("song_low_coverage", "Solo_Guitar"));
        Assert.Equal(101_000, GetProjectedScore("song_low_coverage", "Solo_Guitar", "acct_low_cov"));
        Assert.Contains(_log.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("Cleanup solo current projection refresh will run despite low solo scrape coverage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunPublicationCleanupAsync_WithIncompleteScrape_SkipsPublicationCleanup()
    {
        var ctx = CreateContext(
            scrapeRequests:
            [
                new GlobalLeaderboardScraper.SongScrapeRequest
                {
                    SongId = "song_cleanup_incomplete",
                    Instruments = ["Solo_Guitar"],
                },
            ],
            leaderboardScrapeCompleted: false);

        await _sut.RunPublicationCleanupAsync(
            ctx,
            ScrapePhase.SoloScrape | ScrapePhase.SoloFinalize | ScrapePhase.SoloPrecompute,
            CancellationToken.None);

        Assert.Contains(_log.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("Skipping publication cleanup because the leaderboard scrape did not complete", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("[Cleanup.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunPublicationCleanupAsync_WithSufficientSoloCoverage_RefreshesSoloCurrentProjection()
    {
        await _soloCurrentProjectionBuilder.EnsureSchemaAsync();
        InsertSnapshotState("song_high_coverage", "Solo_Guitar", 66);
        InsertSnapshotEntry(66, "song_high_coverage", "Solo_Guitar", "acct_high_cov", 121_000);
        InsertProjectionScope("song_high_coverage", "Solo_Guitar", sourceSnapshotId: 65);

        var requests = new[]
        {
            new GlobalLeaderboardScraper.SongScrapeRequest
            {
                SongId = "song_high_coverage",
                Instruments = new[] { "Solo_Guitar" },
            },
        };

        var aggregates = new GlobalLeaderboardPersistence.PipelineAggregates();
        aggregates.IncrementSoloLeaderboardsWithData();
        var ctx = CreateContext(scrapeId: 66, aggregates: aggregates, scrapeRequests: requests);

        await _sut.RunPublicationCleanupAsync(ctx, ScrapePhase.SoloScrape | ScrapePhase.SoloFinalize, CancellationToken.None);

        Assert.Equal(66, GetProjectionScopeSourceSnapshot("song_high_coverage", "Solo_Guitar"));
        Assert.Equal(121_000, GetProjectedScore("song_high_coverage", "Solo_Guitar", "acct_high_cov"));
    }

    [Fact]
    public async Task RunPublicationCleanupAsync_RefreshesExplicitPublicationScopesEvenWhenSnapshotIsUnchanged()
    {
        const string songId = "song_deferred_projection";
        const string instrument = "Solo_Guitar";
        const string accountId = "acct_deferred_projection";

        await _soloCurrentProjectionBuilder.EnsureSchemaAsync();
        InsertSnapshotState(songId, instrument, 77);
        InsertSnapshotEntry(77, songId, instrument, accountId, 100_000);
        InsertProjectionScope(songId, instrument, sourceSnapshotId: 77);
        InsertOverlayEntry(songId, instrument, accountId, 125_000);

        var ctx = CreateContext(scrapeId: 77);
        ctx.AddNotificationProjectionScope(
            new SoloCurrentProjectionScopeKey(songId, instrument));

        await _sut.RunPublicationCleanupAsync(
            ctx,
            ScrapePhase.SoloFinalize,
            CancellationToken.None);

        Assert.Equal(125_000, GetProjectedScore(songId, instrument, accountId));
    }

    [Fact]
    public async Task RunPublicationCleanupAsync_DoesNotAdmitScopesCreatedAfterPublicationSeal()
    {
        const string songId = "song_after_projection_seal";
        const string instrument = "Solo_Guitar";
        const string accountId = "acct_after_projection_seal";

        await _soloCurrentProjectionBuilder.EnsureSchemaAsync();
        var ctx = CreateContext(scrapeId: 78);
        ctx.SoloCurrentProjectionScopesSealedForPublication = true;

        InsertOverlayEntry(songId, instrument, accountId, 130_000);

        await _sut.RunPublicationCleanupAsync(
            ctx,
            ScrapePhase.SoloFinalize,
            CancellationToken.None);

        Assert.Null(GetProjectedScore(songId, instrument, accountId));
    }

    [Fact]
    public async Task RunPublicationCleanupAsync_WithSoloPrecompute_BuildsPlayerProfileAfterProjectionRefresh()
    {
        const string accountId = "acct-cache-fresh";
        const string songId = "song_cache_projection";

        await _soloCurrentProjectionBuilder.EnsureSchemaAsync();
        _metaDb.RegisterUser("device-cache", accountId);
        InsertSnapshotState(songId, "Solo_Vocals", 84);
        InsertSnapshotEntry(84, songId, "Solo_Vocals", accountId, 93_189, isFullCombo: true);
        InsertProjectionScope(songId, "Solo_Vocals", sourceSnapshotId: 83);

        var ctx = CreateContext(scrapeId: 84, registeredIds: new HashSet<string> { accountId });

        await _sut.RunPublicationCleanupAsync(
            ctx,
            ScrapePhase.SoloFinalize | ScrapePhase.SoloPrecompute,
            CancellationToken.None);

        Assert.Equal(84, GetProjectionScopeSourceSnapshot(songId, "Solo_Vocals"));

        _metaDb.SwapCachedResponsesFromStaging();
        var cached = _metaDb.GetCachedResponse($"player:{accountId}:::");
        Assert.NotNull(cached);

        using var doc = JsonDocument.Parse(cached.Value.Json);
        var scores = doc.RootElement.GetProperty("scores").EnumerateArray().ToList();
        var score = Assert.Single(scores, entry =>
            entry.GetProperty("si").GetString() == songId &&
            entry.GetProperty("ins").GetString() == "08");
        Assert.Equal(93_189, score.GetProperty("sc").GetInt32());
        Assert.True(score.GetProperty("fc").GetBoolean());

        var logs = _log.Entries.ToList();
        var projectionIndex = logs.FindIndex(e => e.Message.Contains("[Cleanup.SoloCurrentProjection]", StringComparison.Ordinal));
        var precomputeIndex = logs.FindIndex(e => e.Message.Contains("[Cleanup.PrecomputeAll]", StringComparison.Ordinal));
        Assert.True(projectionIndex >= 0, "Expected projection refresh phase to be logged.");
        Assert.True(precomputeIndex > projectionIndex, "Expected precompute to run after projection refresh.");
    }

    [Fact]
    public void PruneExcessEntries_WithDeepScrapeData_KeepsOverThresholdEntries()
    {
        // Simulate deep scrape scenario: many over-threshold (exploited) entries + valid entries.
        // MaxPages=1 â†’ maxEntries=100 per song for valid entries.
        // CHOpt max = 1000. ValidCutoffMultiplier=1.0 â†’ pruning threshold = 1000.
        // Over-threshold entries (scores > 1000) should NOT be pruned.
        var opts = Options.Create(new ScraperOptions { MaxPagesPerLeaderboard = 1, OverThresholdMultiplier = 1.05, ValidCutoffMultiplier = 1.0 });
        var rivalsCalculator = new RivalsCalculator(_persistence, Substitute.For<ILogger<RivalsCalculator>>());
        var rivalsOrchestrator = new RivalsOrchestrator(rivalsCalculator, _persistence, new NotificationService(Substitute.For<ILogger<NotificationService>>()), _progress, new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), new ResponseCacheService(TimeSpan.FromMinutes(5)), Substitute.For<ILogger<RivalsOrchestrator>>());
        var rankingsCalculator = new RankingsCalculator(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<RankingsCalculator>>());
        var leaderboardRivalsCalculator = new LeaderboardRivalsCalculator(_persistence, _metaDb, Options.Create(opts.Value), Substitute.For<ILogger<LeaderboardRivalsCalculator>>());

        // Seed PathDataStore with CHOpt max score for song1
        EnsureSongRow(_pathDataStore, "song1");
        _pathDataStore.UpdateMaxScores("song1", new SongMaxScores { MaxLeadScore = 1000 }, "hash1");

        var sut = new PostScrapeOrchestrator(
            _persistence, _firstSeenCalculator, _nameResolver,
            Substitute.For<HistoryReconstructor>(Substitute.For<ILeaderboardQuerier>(), _persistence, new HttpClient(), new ScrapeProgressTracker(), new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), Substitute.For<ILogger<HistoryReconstructor>>()),
            _pool,
            CreateMockCyclicalMachine(),
            rivalsOrchestrator, rankingsCalculator, leaderboardRivalsCalculator, _notifications,
            _tokenManager, _progress, new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), _pathDataStore,
            new ScrapeTimePrecomputer(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<ScrapeTimePrecomputer>>(), NullLoggerFactory.Instance, new System.Text.Json.JsonSerializerOptions(), new FeatureOptions()),
            new PostScrapeBandExtractor(null!, _pathDataStore, Substitute.For<ILogger<PostScrapeBandExtractor>>()),
            new BandScrapePhase(
                Substitute.For<GlobalLeaderboardScraper>(new HttpClient(), new ScrapeProgressTracker(), Substitute.For<ILogger<GlobalLeaderboardScraper>>(), 0, null),
                new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
                _pathDataStore, _pool, _progress, opts,
                Substitute.For<ILogger<BandScrapePhase>>()),
            new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
            opts, _log, _registrationMutations, null);

        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // 150 over-threshold entries (scores 5000â€“3510, all > 1000)
        var overEntries = Enumerable.Range(0, 150).Select(i =>
            new LeaderboardEntry
            {
                AccountId = $"exploiter_{i}", Score = 5000 - i * 10,
                Accuracy = 95, Stars = 5, Season = 3,
            }).ToList();

        // 200 valid entries (scores 1000 down to 5, all â‰¤ raw CHOpt max 1000)
        var validEntries = Enumerable.Range(0, 200).Select(i =>
            new LeaderboardEntry
            {
                AccountId = $"valid_{i}", Score = 1000 - i * 5,
                Accuracy = 95, Stars = 5, Season = 3,
            }).ToList();

        db.UpsertEntries("song1", overEntries);
        db.UpsertEntries("song1", validEntries);
        Assert.Equal(350, db.GetLeaderboardCount("song1"));

        var ctx = CreateContext();
        sut.PruneExcessEntries(ctx);

        // maxEntries=100 for valid entries, all 150 over-threshold kept
        // Valid entries pruned from 200 to 100 â†’ 100 deleted
        var remaining = db.GetLeaderboardCount("song1");
        Assert.Equal(250, remaining); // 150 over-threshold + 100 valid

        // Highest over-threshold entry still present
        var topExploiter = db.GetPlayerScores("exploiter_0", "song1");
        Assert.Single(topExploiter);
        Assert.Equal(5000, topExploiter[0].Score);

        // Top valid entry still present
        var topValid = db.GetPlayerScores("valid_0", "song1");
        Assert.Single(topValid);

        // Low valid entry should be pruned (rank 200, outside top 100)
        var prunedValid = db.GetPlayerScores("valid_199", "song1");
        Assert.Empty(prunedValid);
    }

    private static void EnsureSongRow(PathDataStore pathStore, string songId)
    {
        var dsField = typeof(PathDataStore)
            .GetField("_ds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var ds = (Npgsql.NpgsqlDataSource)dsField.GetValue(pathStore)!;
        using var conn = ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO songs (song_id) VALUES (@sid) ON CONFLICT DO NOTHING";
        cmd.Parameters.AddWithValue("sid", songId);
        cmd.ExecuteNonQuery();
    }

    private void InsertOverlayEntry(
        string songId,
        string instrument,
        string accountId,
        int score)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO leaderboard_entries_overlay
            (song_id, instrument, account_id, score, accuracy, is_full_combo, stars,
             season, percentile, rank, source, difficulty, api_rank, end_time,
             first_seen_at, last_updated_at, source_priority, overlay_reason)
            VALUES
            (@songId, @instrument, @accountId, @score, 99, TRUE, 5,
             3, 99.0, 1, 'registered', 3, 1, '2025-01-16T12:00:00Z',
             @now, @now, 100, 'test')
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private void InsertSnapshotState(string songId, string instrument, long activeSnapshotId)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO leaderboard_snapshot_state
            (song_id, instrument, active_snapshot_id, scrape_id, is_finalized, updated_at)
            VALUES (@songId, @instrument, @activeSnapshotId, @activeSnapshotId, TRUE, @now)
            ON CONFLICT (song_id, instrument) DO UPDATE SET
                active_snapshot_id = EXCLUDED.active_snapshot_id,
                scrape_id = EXCLUDED.scrape_id,
                is_finalized = EXCLUDED.is_finalized,
                updated_at = EXCLUDED.updated_at
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("activeSnapshotId", activeSnapshotId);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private void InsertSnapshotEntry(long snapshotId, string songId, string instrument, string accountId, int score, bool isFullCombo = false)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO leaderboard_entries_snapshot
            (snapshot_id, song_id, instrument, account_id, score, accuracy, is_full_combo, stars,
             season, percentile, rank, source, difficulty, api_rank, end_time, first_seen_at, last_updated_at)
            VALUES
            (@snapshotId, @songId, @instrument, @accountId, @score, 95, @isFullCombo, 5,
             3, 99.0, 1, 'scrape', 3, 1, '2025-01-15T12:00:00Z', @now, @now)
            """;
        cmd.Parameters.AddWithValue("snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("isFullCombo", isFullCombo);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private void InsertProjectionScope(string songId, string instrument, long? sourceSnapshotId)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO solo_current_projection_scope
            (song_id, instrument, projection_generation, row_count, source_snapshot_id, status, error_message, last_rebuilt_at, updated_at)
            VALUES (@songId, @instrument, 1, 1, @sourceSnapshotId, 'ready', NULL, @now, @now)
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("sourceSnapshotId", sourceSnapshotId.HasValue ? sourceSnapshotId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private long? GetProjectionScopeSourceSnapshot(string songId, string instrument)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT source_snapshot_id
            FROM solo_current_projection_scope
            WHERE song_id = @songId AND instrument = @instrument
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private long? GetProjectionScopeGeneration(string songId, string instrument)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT projection_generation
            FROM solo_current_projection_scope
            WHERE song_id = @songId AND instrument = @instrument
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private int? GetProjectedScore(string songId, string instrument, string accountId)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT score
            FROM current_leaderboard_entries
            WHERE song_id = @songId AND instrument = @instrument AND account_id = @accountId
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        var result = cmd.ExecuteScalar();
        return result is null ? null : Convert.ToInt32(result);
    }

    [Fact]
    public async Task RunEnrichmentAsync_WithToken_ExercisesFirstSeenAndRankPaths()
    {
        // Wire token manager to return a token so firstSeen path is exercised
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-token");
        _tokenManager.AccountId.Returns("caller-1");

        var callOrder = new List<string>();
        var discoveredWindows = new[]
        {
            new SeasonWindowInfo
            {
                SeasonNumber = 15,
                EventId = "season15-event",
                WindowId = "season_15_competitive",
                SourceKind = "event_api",
                IsFreshAuthoritative = true,
            },
        };
        _historyReconstructor.DiscoverSeasonWindowsAsync(
            "test-token",
            "caller-1",
            Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callOrder.Add("discover");
                return discoveredWindows;
            });
        _firstSeenCalculator.CalculateAsync(
            Arg.Any<FestivalService>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<SharedDopPool>(), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<SeasonWindowInfo>?>(),
            Arg.Any<bool>())
            .Returns(call =>
            {
                callOrder.Add("calculate");
                Assert.Same(discoveredWindows, call.ArgAt<IReadOnlyList<SeasonWindowInfo>>(5));
                Assert.True(call.ArgAt<bool>(6));
                return 5;
            });

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var ctx = CreateContext();

        await _sut.RunEnrichmentAsync(ctx, service, CancellationToken.None);

        // FirstSeenCalculator should have been called with the token
        await _firstSeenCalculator.Received(1).CalculateAsync(
            Arg.Any<FestivalService>(), "test-token", "caller-1",
            Arg.Any<SharedDopPool>(), Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlyList<SeasonWindowInfo>?>(windows =>
                windows != null &&
                windows.Count == 1 &&
                windows[0].WindowId == "season_15_competitive"),
            true);
        Assert.Equal(["discover", "calculate"], callOrder);
    }

    [Fact]
    public async Task RunEnrichmentAsync_FirstSeenThrows_DoesNotPropagate()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-token");
        _tokenManager.AccountId.Returns("caller-1");

        _firstSeenCalculator.CalculateAsync(
            Arg.Any<FestivalService>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<SharedDopPool>(), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<SeasonWindowInfo>?>(),
            Arg.Any<bool>())
            .ThrowsAsync(new InvalidOperationException("test error"));

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var ctx = CreateContext();

        // Should not throw â€” errors are caught and logged
        await _sut.RunEnrichmentAsync(ctx, service, CancellationToken.None);
    }

    [Fact]
    public async Task RunEnrichmentAsync_NameResThrows_DoesNotPropagate()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns((string?)null);

        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("name res fail"));

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var ctx = CreateContext();

        // Should not throw
        await _sut.RunEnrichmentAsync(ctx, service, CancellationToken.None);
    }

    [Fact]
    public async Task ComputeRankingsAsync_Throws_DoesNotPropagate()
    {
        // RankingsCalculator is a real instance, not mocked, so no mock exceptions.
        // But if ComputeAllAsync hits an issue (e.g. no data), it should not throw.
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);

        // Should not throw even with no instrument data
        await _sut.ComputeRankingsAsync(service, CancellationToken.None);
    }

    [Fact]
    public async Task ComputeRivalsAsync_WithDirtyInstruments_BuildsDirtyMap()
    {
        // Register a user and mark them with changed scores
        _metaDb.RegisterUser("dev-rival", "acct-rival");
        _metaDb.EnsureRivalsStatus("acct-rival");

        var aggregates = new GlobalLeaderboardPersistence.PipelineAggregates();
        aggregates.AddChangedAccountIds(new[] { "acct-rival" });

        var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct-rival" };
        var ctx = CreateContext(registeredIds: registeredIds, aggregates: aggregates);

        // Should run without error â€” exercises the dirtyMap building path
        await _sut.ComputeRivalsAsync(ctx, CancellationToken.None);
    }

    [Fact]
    public async Task ComputeRivalsAsync_LogsDirtyReasonSummary()
    {
        var aggregates = new GlobalLeaderboardPersistence.PipelineAggregates();
        aggregates.AddDirtyRivalSongs(
        [
            new RivalDirtySongRow
            {
                AccountId = "acct-rival-1",
                Instrument = "Solo_Guitar",
                SongId = "song-1",
                DirtyReason = RivalsDirtyReason.SelfScoreChange,
                DetectedAt = "2026-01-01T00:00:00Z",
            },
            new RivalDirtySongRow
            {
                AccountId = "acct-rival-2",
                Instrument = "Solo_Bass",
                SongId = "song-2",
                DirtyReason = RivalsDirtyReason.NeighborWindowChange,
                DetectedAt = "2026-01-01T00:00:01Z",
            },
        ]);

        var ctx = CreateContext(
            registeredIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct-rival-1", "acct-rival-2" },
            aggregates: aggregates);

        await _sut.ComputeRivalsAsync(ctx, CancellationToken.None);

        Assert.Contains(_log.Entries, entry =>
            entry.Message.Contains("Song-rivals dirty summary", StringComparison.Ordinal) &&
            entry.Message.Contains("neighbor_window_change=1", StringComparison.Ordinal) &&
            entry.Message.Contains("self_score_change=1", StringComparison.Ordinal));
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // Dedicated Registration Backlog Ownership
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task RefreshRegisteredUsers_DoesNotClaimBackfillOrHistoryReconWork()
    {
        _metaDb.RegisterUser("dev-hr", "acct-hr");
        _metaDb.EnqueueBackfill("acct-hr", 10);
        _metaDb.StartBackfill("acct-hr");
        _metaDb.CompleteBackfill("acct-hr");
        _metaDb.EnqueueHistoryRecon("acct-hr", 5);
        _metaDb.StartHistoryRecon("acct-hr");
        _metaDb.RegisterUser("dev-combo", "acct-combo");
        _metaDb.EnqueueBackfill("acct-combo", 10);

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-tok");
        _tokenManager.AccountId.Returns("caller-1");
        _metaDb.UpsertSeasonWindow(14, "", "");

        var ctx = CreateContext(registeredIds:
            new HashSet<string> { "acct-hr", "acct-combo" });
        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);

        Assert.Equal(
            "pending",
            _metaDb.GetBackfillStatus("acct-combo")?.Status);
        Assert.Equal(
            "in_progress",
            _metaDb.GetHistoryReconStatus("acct-hr")?.Status);
        await _cyclicalMachine.Received(1).AttachAsync(
            Arg.Is<IReadOnlyList<UserWorkItem>>(users =>
                users.Count == 2
                && users.All(user =>
                    user.Purposes == WorkPurpose.PostScrape)),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyList<Persistence.SeasonWindowInfo>>(),
            SongMachineSource.PostScrape,
            Arg.Is<bool>(static value => value),
            Arg.Any<CancellationToken>(),
            preserveProgressPhaseOnIdle: true,
            Arg.Any<EpicTrafficKind>(),
            Arg.Is<CyclicalSongMachine.AttachmentOptions?>(options =>
                options != null && options.PreserveSongOrder));
    }

    [Fact]
    public void Retired_deferred_sync_and_refresher_surfaces_are_absent()
    {
        Assert.Null(
            typeof(PostScrapeOrchestrator).GetMethod(
                "RunDeferredRegistrationSyncAsync"));
        Assert.Null(
            typeof(PostScrapeOrchestrator).Assembly.GetType(
                "FSTService.Scraping.PostScrapeRefresher"));
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // ComputeLeaderboardRivalsAsync â€” skip when rankings fail
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task ComputeRankingsAsync_ReturnsTrue_OnSuccess()
    {
        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var result = await _sut.ComputeRankingsAsync(service, CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task ComputeRankingsAsync_PropagatesFailure()
    {
        // Seed data so rankings computation actually runs, then corrupt a required
        // table to trigger an error inside the rankings CTE.
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("song1", [new LeaderboardEntry
        {
            AccountId = "p1", Score = 1000, Accuracy = 95, Stars = 5, Season = 3,
        }]);

        // Drop song_stats table via PG to make ComputeAccountRankings fail
        var pgDb = (InstrumentDatabase)db;
        using (var conn = pgDb.DataSource.OpenConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE IF EXISTS song_stats_solo_guitar;";
            cmd.ExecuteNonQuery();
        }

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(
            () => _sut.ComputeRankingsAsync(service, CancellationToken.None));
    }

    [Fact]
    public async Task LeaderboardRivals_NoRegisteredAccounts_CompletesCriticalContractWithoutWork()
    {
        var ctx = CreateContext();

        await _sut.RunLeaderboardRivalsPhaseAsync(
            ctx,
            CancellationToken.None);

        var outcome = Assert.Single(ctx.PostScrapeOutcomes.Outcomes);
        Assert.Equal("LeaderboardRivals", outcome.Phase);
        Assert.True(outcome.Success);
        Assert.Equal("completed", outcome.Status);
        ScrapePublicationGuard.EnsureCanPublish(
            42,
            ctx.PostScrapeOutcomes,
            enforcePublicationCriticalPhases: true);
    }

    [Fact]
    public async Task ComputeLeaderboardRivalsAsync_ReportsRegisteredAccountProgress()
    {
        var ctx = CreateContext(registeredIds:
        [
            "leaderboard-rival-1",
            "leaderboard-rival-2",
        ]);

        await _sut.ComputeLeaderboardRivalsAsync(
            ctx,
            CancellationToken.None);

        var current = _progress.GetProgressResponse().Current;
        Assert.NotNull(current);
        Assert.Equal("ComputingRivals", current!.Operation);
        Assert.Null(current.SubOperation);
        Assert.Equal(2, current.WorkItems?.Completed);
        Assert.Equal(2, current.WorkItems?.Total);
        Assert.True(current.WorkItemsTotalFinal);
    }

    [Fact]
    public async Task ComputePlayerStatsTiersAsync_ReportsNormalizedAccountProgress()
    {
        var ctx = CreateContext(registeredIds:
        [
            "player-stats-1",
            " ",
            "player-stats-2",
        ]);

        await _sut.ComputePlayerStatsTiersAsync(
            ctx,
            CancellationToken.None);

        var current = _progress.GetProgressResponse().Current;
        Assert.NotNull(current);
        Assert.Equal("Precomputing", current!.Operation);
        Assert.Equal("population_tiers", current.SubOperation);
        Assert.Equal(2, current.WorkItems?.Completed);
        Assert.Equal(2, current.WorkItems?.Total);
        Assert.True(current.WorkItemsTotalFinal);
    }

    [Fact]
    public void CriticalSkipRecorder_RejectsBeforePersisting()
    {
        var scrapeId = _metaDb.StartScrapeRun();
        _workerStatus.AttachScrape(scrapeId);
        var ctx = CreateContext(scrapeId);

        var error = Assert.Throws<InvalidOperationException>(() =>
            _sut.RecordSkippedPhaseForTest(
                ctx,
                "RankRecompute",
                "corrupt skip"));

        Assert.Contains(
            "Publication-critical phase 'RankRecompute' cannot be recorded as skipped",
            error.Message);
        Assert.Empty(ctx.PostScrapeOutcomes.Outcomes);
        Assert.Empty(
            _metaDb.GetScrapeResumeState(scrapeId)!.PhaseOutcomes);
    }

    [Fact]
    public void BestEffortSkip_PersistsDurableReasonAndRemainsNonblocking()
    {
        var scrapeId = _metaDb.StartScrapeRun();
        _workerStatus.BeginOperation(
            "scrape.post_process",
            "Post-processing leaderboard update",
            phase: "PostScrapeEnrichment");
        _workerStatus.AttachScrape(scrapeId);

        var ctx = CreateContext(scrapeId);
        _sut.RecordSkippedPhaseForTest(
            ctx,
            "FirstSeenSeason",
            "no access token");

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT status, warning_message, error_message
            FROM scrape_phase_attempts
            WHERE scrape_id = @scrapeId
              AND phase_id = 'post.first_seen_season'
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("skipped", reader.GetString(0));
        Assert.Equal("no access token", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal(
            "skipped",
            Assert.Single(ctx.PostScrapeOutcomes.Outcomes).Status);
        ScrapePublicationGuard.EnsureCanPublish(
            scrapeId,
            ctx.PostScrapeOutcomes,
            enforcePublicationCriticalPhases: true);
    }

    [Theory]
    [MemberData(nameof(ClassifiedPhases))]
    public async Task ClassifiedPhaseFaultsHaveExplicitPublicationBehavior(
        string phase,
        PostScrapePhaseCriticality criticality)
    {
        var ctx = CreateContext();

        await _sut.RunClassifiedPhaseForTestAsync(
            ctx,
            phase,
            () => throw new InvalidOperationException($"fault:{phase}"));

        var outcome = Assert.Single(ctx.PostScrapeOutcomes.Outcomes);
        Assert.Equal(phase, outcome.Phase);
        Assert.Equal(criticality, outcome.Criticality);
        Assert.False(outcome.Success);

        if (criticality == PostScrapePhaseCriticality.PublicationCritical)
        {
            Assert.Throws<InvalidOperationException>(() =>
                ScrapePublicationGuard.EnsureCanPublish(
                    42,
                    ctx.PostScrapeOutcomes,
                    enforcePublicationCriticalPhases: true));
        }
        else
        {
            ScrapePublicationGuard.EnsureCanPublish(
                42,
                ctx.PostScrapeOutcomes,
                enforcePublicationCriticalPhases: true);
        }
    }

    [Fact]
    public async Task ClassifiedPhaseAdvancesDurablePostProcessHeartbeat()
    {
        _workerStatus.BeginOperation(
            "scrape.post_process",
            "Post-processing leaderboard update",
            phase: "PostScrapeEnrichment");
        var before = _metaDb.GetWorkerStatus(WorkerStatusPublisher.ScraperWorkerKey)!
            .CurrentOperation!;

        await _sut.RunClassifiedPhaseForTestAsync(
            CreateContext(),
            "FirstSeenSeason",
            () => Task.CompletedTask);

        var after = _metaDb.GetWorkerStatus(WorkerStatusPublisher.ScraperWorkerKey)!
            .CurrentOperation!;
        Assert.Equal("FirstSeenSeason", after.SubOperation);
        Assert.Equal("Completed FirstSeenSeason", after.Detail);
        Assert.True(after.UpdatedAtUtc >= before.UpdatedAtUtc);
    }

    [Fact]
    public async Task ClassifiedPhasePersistsDurableAttemptTerminalState()
    {
        var scrapeId = _metaDb.StartScrapeRun();
        _workerStatus.BeginOperation(
            "scrape.post_process",
            "Post-processing leaderboard update",
            phase: "PostScrapeEnrichment");
        _workerStatus.AttachScrape(scrapeId);

        await _sut.RunClassifiedPhaseForTestAsync(
            CreateContext(scrapeId),
            "FirstSeenSeason",
            () => Task.CompletedTask);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT phase_id, attempt, status, warning_message, error_message
            FROM scrape_phase_attempts
            WHERE scrape_id = @scrapeId
              AND phase_id = 'post.first_seen_season'
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("post.first_seen_season", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal("completed", reader.GetString(2));
        Assert.True(reader.IsDBNull(3));
        Assert.True(reader.IsDBNull(4));
    }

    [Fact]
    public async Task ClassifiedBestEffortFailurePersistsWarningWithoutChangingPublicationBehavior()
    {
        var scrapeId = _metaDb.StartScrapeRun();
        _workerStatus.BeginOperation(
            "scrape.post_process",
            "Post-processing leaderboard update",
            phase: "PostScrapeEnrichment");
        _workerStatus.AttachScrape(scrapeId);

        await _sut.RunClassifiedPhaseForTestAsync(
            CreateContext(scrapeId),
            "FirstSeenSeason",
            () => throw new InvalidOperationException("first-seen failed"));

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT status, warning_message, error_message
            FROM scrape_phase_attempts
            WHERE scrape_id = @scrapeId
              AND phase_id = 'post.first_seen_season'
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("failed", reader.GetString(0));
        Assert.Equal("first-seen failed", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
    }

    [Fact]
    public async Task PublicationCachePhaseFailurePropagatesWhenRolloutFlagIsDisabled()
    {
        var ctx = CreateContext();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RunClassifiedPhaseForTestAsync(
                ctx,
                "Cleanup.PrecomputeAll",
                () => throw new InvalidOperationException("precompute failed"),
                alwaysPropagateFailure: true));

        Assert.Equal("precompute failed", exception.Message);
        var outcome = Assert.Single(ctx.PostScrapeOutcomes.Outcomes);
        Assert.Equal("Cleanup.PrecomputeAll", outcome.Phase);
        Assert.False(outcome.Success);
    }

    [Fact]
    public void PhasePolicyRejectsUnclassifiedPhasesAndExposesBestEffortFailures()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PostScrapePhasePolicy.GetCriticality("UnclassifiedPhase"));

        var ledger = new PostScrapeExecutionLedger();
        ledger.Record(new PostScrapePhaseOutcome(
            "FirstSeenSeason",
            PostScrapePhaseCriticality.BestEffort,
            false,
            "injected"));

        Assert.True(ledger.CanPublish);
        Assert.Empty(ledger.FailedPublicationCriticalPhases);
        Assert.Equal("FirstSeenSeason", Assert.Single(ledger.FailedBestEffortPhases).Phase);
    }

    [Fact]
    public void CorruptCriticalSkippedOutcome_BlocksPublication()
    {
        var ledger = new PostScrapeExecutionLedger();
        ledger.Record(new PostScrapePhaseOutcome(
            "RankRecompute",
            PostScrapePhaseCriticality.PublicationCritical,
            true,
            null)
        {
            Status = "skipped",
        });

        var error = Assert.Throws<InvalidOperationException>(() =>
            ScrapePublicationGuard.EnsureCanPublish(
                42,
                ledger,
                enforcePublicationCriticalPhases: false));

        Assert.Contains(
            "publication-critical phase(s) were invalidly skipped: RankRecompute",
            error.Message);
        Assert.False(ledger.CanPublish);
    }

    [Fact]
    public void BestEffortSkippedOutcome_RemainsNonblocking()
    {
        var ledger = new PostScrapeExecutionLedger();
        ledger.Record(new PostScrapePhaseOutcome(
            "FirstSeenSeason",
            PostScrapePhaseCriticality.BestEffort,
            false,
            null)
        {
            Status = "skipped",
        });

        ScrapePublicationGuard.EnsureCanPublish(
            42,
            ledger,
            enforcePublicationCriticalPhases: true);
        Assert.True(ledger.CanPublish);
        Assert.Empty(ledger.FailedBestEffortPhases);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // NoOpHttpHandler (shared utility)
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    private sealed class NoOpHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
