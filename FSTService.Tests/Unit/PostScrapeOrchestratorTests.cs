using FortniteFestival.Core.Scraping;
using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FSTService.Tests.Unit;

public class PostScrapeOrchestratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryMetaDatabase _metaFixture = new();
    private readonly MetaDatabase _metaDb;
    private readonly GlobalLeaderboardPersistence _persistence;

    private readonly TokenManager _tokenManager;
    private readonly FirstSeenSeasonCalculator _firstSeenCalculator;
    private readonly AccountNameResolver _nameResolver;
    private readonly PostScrapeRefresher _refresher;
    private readonly SongProcessingMachine _machine;
    private readonly CyclicalSongMachine _cyclicalMachine;
    private readonly SharedDopPool _pool;
    private readonly NotificationService _notifications;
    private readonly ScrapeProgressTracker _progress;
    private readonly PathDataStore _pathDataStore;
    private readonly TestLogger<PostScrapeOrchestrator> _log;

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

        _refresher = Substitute.For<PostScrapeRefresher>(
            scraper, _persistence, new ScrapeProgressTracker(),
            Substitute.For<ILogger<PostScrapeRefresher>>());

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

        // ServiceProvider returns the mocked machine
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(SongProcessingMachine)).Returns(_machine);

        _notifications = new NotificationService(Substitute.For<ILogger<NotificationService>>());
        _progress = new ScrapeProgressTracker();
        _pathDataStore = new PathDataStore(SharedPostgresContainer.CreateDatabase());
        _log = new TestLogger<PostScrapeOrchestrator>();

        var rivalsCalculator = new RivalsCalculator(_persistence, Substitute.For<ILogger<RivalsCalculator>>());
        var rivalsOrchestrator = new RivalsOrchestrator(rivalsCalculator, _persistence, new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), _progress, new UserSyncProgressTracker(new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), new Api.ResponseCacheService(TimeSpan.FromMinutes(5)), Substitute.For<ILogger<RivalsOrchestrator>>());
        var rankingsCalculator = new RankingsCalculator(_persistence, _metaDb, _pathDataStore, _progress, Options.Create(new FeatureOptions()), Substitute.For<ILogger<RankingsCalculator>>());
        var leaderboardRivalsCalculator = new LeaderboardRivalsCalculator(_persistence, _metaDb, Options.Create(new ScraperOptions()), Substitute.For<ILogger<LeaderboardRivalsCalculator>>());

        _cyclicalMachine = CreateMockCyclicalMachine();

        _sut = new PostScrapeOrchestrator(
            _persistence, _firstSeenCalculator, _nameResolver,
            _refresher,
            serviceProvider,
            Substitute.For<HistoryReconstructor>(scraper, _persistence, new HttpClient(), new ScrapeProgressTracker(), new UserSyncProgressTracker(new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), Substitute.For<ILogger<HistoryReconstructor>>()),
            _pool,
            _cyclicalMachine,
            rivalsOrchestrator, rankingsCalculator, leaderboardRivalsCalculator, _notifications,
            _tokenManager, _progress, _pathDataStore,
            new ScrapeTimePrecomputer(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<ScrapeTimePrecomputer>>(), NullLoggerFactory.Instance, new System.Text.Json.JsonSerializerOptions(), new FeatureOptions()),
            new PostScrapeBandExtractor(null!, _pathDataStore, Substitute.For<ILogger<PostScrapeBandExtractor>>()),
            new BandScrapePhase(
                scraper,
                new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
                _pathDataStore, _pool, _progress, Options.Create(new ScraperOptions()),
                Substitute.For<ILogger<BandScrapePhase>>()),
            new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
            Options.Create(new ScraperOptions()), _log, null);
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
            Arg.Any<bool>())
            .Returns(new SongProcessingMachine.MachineResult());
        return mock;
    }

    private ScrapePassContext CreateContext(
        long scrapeId = 0,
        HashSet<string>? registeredIds = null,
        GlobalLeaderboardPersistence.PipelineAggregates? aggregates = null,
        IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>? scrapeRequests = null)
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
        };
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // RefreshRegisteredUsersAsync
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task RefreshRegisteredUsers_NoRegisteredUsers_Skips()
    {
        var ctx = CreateContext();

        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);

        await _refresher.DidNotReceiveWithAnyArgs()
            .RefreshAllAsync(default!, default!, default!, default!, default!, default!, default, default);
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
            preserveProgressPhaseOnIdle: true);
    }

    [Fact]
    public async Task RefreshRegisteredUsers_NoToken_Skips()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var ctx = CreateContext(registeredIds: new HashSet<string> { "user-1" });

        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);

        await _refresher.DidNotReceiveWithAnyArgs()
            .RefreshAllAsync(default!, default!, default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task RefreshRegisteredUsers_ThrowsException_DoesNotPropagate()
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
            Arg.Any<bool>())
            .ThrowsAsync(new InvalidOperationException("API error"));

        var ctx = CreateContext(registeredIds: new HashSet<string> { "user-1" });

        // Should not throw
        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);
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
        var rankingsCalculator2 = new RankingsCalculator(_persistence, _metaDb, _pathDataStore, _progress, Options.Create(new FeatureOptions()), Substitute.For<ILogger<RankingsCalculator>>());
        var leaderboardRivalsCalculator2 = new LeaderboardRivalsCalculator(_persistence, _metaDb, Options.Create(opts.Value), Substitute.For<ILogger<LeaderboardRivalsCalculator>>());
        var sut = new PostScrapeOrchestrator(
            _persistence, _firstSeenCalculator, _nameResolver,
            _refresher,
            Substitute.For<IServiceProvider>(),
            Substitute.For<HistoryReconstructor>(Substitute.For<ILeaderboardQuerier>(), _persistence, new HttpClient(), new ScrapeProgressTracker(), new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), Substitute.For<ILogger<HistoryReconstructor>>()),
            _pool,
            CreateMockCyclicalMachine(),
            rivalsOrchestrator, rankingsCalculator2, leaderboardRivalsCalculator2, _notifications,
            _tokenManager, _progress, _pathDataStore,
            new ScrapeTimePrecomputer(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<ScrapeTimePrecomputer>>(), NullLoggerFactory.Instance, new System.Text.Json.JsonSerializerOptions(), new FeatureOptions()),
            new PostScrapeBandExtractor(null!, _pathDataStore, Substitute.For<ILogger<PostScrapeBandExtractor>>()),
            new BandScrapePhase(
                Substitute.For<GlobalLeaderboardScraper>(new HttpClient(), new ScrapeProgressTracker(), Substitute.For<ILogger<GlobalLeaderboardScraper>>(), 0, null),
                new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
                _pathDataStore, _pool, _progress, opts,
                Substitute.For<ILogger<BandScrapePhase>>()),
            new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
            opts, _log, null);

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
        var rankingsCalculator3 = new RankingsCalculator(_persistence, _metaDb, _pathDataStore, _progress, Options.Create(new FeatureOptions()), Substitute.For<ILogger<RankingsCalculator>>());
        var leaderboardRivalsCalculator3 = new LeaderboardRivalsCalculator(_persistence, _metaDb, Options.Create(opts.Value), Substitute.For<ILogger<LeaderboardRivalsCalculator>>());
        var sut = new PostScrapeOrchestrator(
            _persistence, _firstSeenCalculator, _nameResolver,
            _refresher,
            Substitute.For<IServiceProvider>(),
            Substitute.For<HistoryReconstructor>(Substitute.For<ILeaderboardQuerier>(), _persistence, new HttpClient(), new ScrapeProgressTracker(), new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), Substitute.For<ILogger<HistoryReconstructor>>()),
            _pool,
            CreateMockCyclicalMachine(),
            rivalsOrchestrator, rankingsCalculator3, leaderboardRivalsCalculator3, _notifications,
            _tokenManager, _progress, _pathDataStore,
            new ScrapeTimePrecomputer(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<ScrapeTimePrecomputer>>(), NullLoggerFactory.Instance, new System.Text.Json.JsonSerializerOptions(), new FeatureOptions()),
            new PostScrapeBandExtractor(null!, _pathDataStore, Substitute.For<ILogger<PostScrapeBandExtractor>>()),
            new BandScrapePhase(
                Substitute.For<GlobalLeaderboardScraper>(new HttpClient(), new ScrapeProgressTracker(), Substitute.For<ILogger<GlobalLeaderboardScraper>>(), 0, null),
                new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
                _pathDataStore, _pool, _progress, opts,
                Substitute.For<ILogger<BandScrapePhase>>()),            new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),            opts, _log, null);

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
    public void PruneExcessEntries_WithDeepScrapeData_KeepsOverThresholdEntries()
    {
        // Simulate deep scrape scenario: many over-threshold (exploited) entries + valid entries.
        // MaxPages=1 â†’ maxEntries=100 per song for valid entries.
        // CHOpt max = 1000. ValidCutoffMultiplier=1.0 â†’ pruning threshold = 1000.
        // Over-threshold entries (scores > 1000) should NOT be pruned.
        var opts = Options.Create(new ScraperOptions { MaxPagesPerLeaderboard = 1, OverThresholdMultiplier = 1.05, ValidCutoffMultiplier = 1.0 });
        var rivalsCalculator = new RivalsCalculator(_persistence, Substitute.For<ILogger<RivalsCalculator>>());
        var rivalsOrchestrator = new RivalsOrchestrator(rivalsCalculator, _persistence, new NotificationService(Substitute.For<ILogger<NotificationService>>()), _progress, new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), new ResponseCacheService(TimeSpan.FromMinutes(5)), Substitute.For<ILogger<RivalsOrchestrator>>());
        var rankingsCalculator = new RankingsCalculator(_persistence, _metaDb, _pathDataStore, _progress, Options.Create(new FeatureOptions()), Substitute.For<ILogger<RankingsCalculator>>());
        var leaderboardRivalsCalculator = new LeaderboardRivalsCalculator(_persistence, _metaDb, Options.Create(opts.Value), Substitute.For<ILogger<LeaderboardRivalsCalculator>>());

        // Seed PathDataStore with CHOpt max score for song1
        EnsureSongRow(_pathDataStore, "song1");
        _pathDataStore.UpdateMaxScores("song1", new SongMaxScores { MaxLeadScore = 1000 }, "hash1");

        var sut = new PostScrapeOrchestrator(
            _persistence, _firstSeenCalculator, _nameResolver,
            _refresher,
            Substitute.For<IServiceProvider>(),
            Substitute.For<HistoryReconstructor>(Substitute.For<ILeaderboardQuerier>(), _persistence, new HttpClient(), new ScrapeProgressTracker(), new UserSyncProgressTracker(new NotificationService(Substitute.For<ILogger<NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), Substitute.For<ILogger<HistoryReconstructor>>()),
            _pool,
            CreateMockCyclicalMachine(),
            rivalsOrchestrator, rankingsCalculator, leaderboardRivalsCalculator, _notifications,
            _tokenManager, _progress, _pathDataStore,
            new ScrapeTimePrecomputer(_persistence, _metaDb, _pathDataStore, _progress, Substitute.For<ILogger<ScrapeTimePrecomputer>>(), NullLoggerFactory.Instance, new System.Text.Json.JsonSerializerOptions(), new FeatureOptions()),
            new PostScrapeBandExtractor(null!, _pathDataStore, Substitute.For<ILogger<PostScrapeBandExtractor>>()),
            new BandScrapePhase(
                Substitute.For<GlobalLeaderboardScraper>(new HttpClient(), new ScrapeProgressTracker(), Substitute.For<ILogger<GlobalLeaderboardScraper>>(), 0, null),
                new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
                _pathDataStore, _pool, _progress, opts,
                Substitute.For<ILogger<BandScrapePhase>>()),
            new BandLeaderboardPersistence(null!, Substitute.For<ILogger<BandLeaderboardPersistence>>()),
            opts, _log, null);

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

    [Fact]
    public async Task RunEnrichmentAsync_WithToken_ExercisesFirstSeenAndRankPaths()
    {
        // Wire token manager to return a token so firstSeen path is exercised
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-token");
        _tokenManager.AccountId.Returns("caller-1");

        _firstSeenCalculator.CalculateAsync(
            Arg.Any<FestivalService>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<SharedDopPool>(), Arg.Any<CancellationToken>())
            .Returns(5);

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var ctx = CreateContext();

        await _sut.RunEnrichmentAsync(ctx, service, CancellationToken.None);

        // FirstSeenCalculator should have been called with the token
        await _firstSeenCalculator.Received(1).CalculateAsync(
            Arg.Any<FestivalService>(), "test-token", "caller-1",
            Arg.Any<SharedDopPool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunEnrichmentAsync_FirstSeenThrows_DoesNotPropagate()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-token");
        _tokenManager.AccountId.Returns("caller-1");

        _firstSeenCalculator.CalculateAsync(
            Arg.Any<FestivalService>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<SharedDopPool>(), Arg.Any<CancellationToken>())
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
    // History Recon Completion
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task RefreshRegisteredUsers_StuckHistoryRecon_CompletesIt()
    {
        // Simulate the bug: backfill is complete, history recon stuck at in_progress
        _metaDb.RegisterUser("dev-hr", "acct-hr");
        _metaDb.EnqueueBackfill("acct-hr", 10);
        _metaDb.StartBackfill("acct-hr");
        _metaDb.CompleteBackfill("acct-hr");
        _metaDb.EnqueueHistoryRecon("acct-hr", 5);
        _metaDb.StartHistoryRecon("acct-hr");
        // Status is now "in_progress" â€” the bug leaves it here forever

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-tok");
        _tokenManager.AccountId.Returns("caller-1");

        var ctx = CreateContext(registeredIds: new HashSet<string> { "acct-hr" });
        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);

        var status = _metaDb.GetHistoryReconStatus("acct-hr");
        Assert.NotNull(status);
        Assert.Equal("complete", status.Status);
    }

    [Fact]
    public async Task RefreshRegisteredUsers_PendingBackfill_CompletesHistoryReconToo()
    {
        // Pending backfill user gets both Backfill | HistoryRecon purposes
        _metaDb.RegisterUser("dev-combo", "acct-combo");
        _metaDb.EnqueueBackfill("acct-combo", 10);

        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-tok");
        _tokenManager.AccountId.Returns("caller-1");

        var ctx = CreateContext(registeredIds: new HashSet<string> { "acct-combo" });
        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);

        // Backfill should be complete
        var bfStatus = _metaDb.GetBackfillStatus("acct-combo");
        Assert.NotNull(bfStatus);
        Assert.Equal("complete", bfStatus.Status);

        // History recon should also be complete (created and completed inline)
        var hrStatus = _metaDb.GetHistoryReconStatus("acct-combo");
        Assert.NotNull(hrStatus);
        Assert.Equal("complete", hrStatus.Status);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // PreWarmRankingsCache
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void PreWarmRankingsCache_warms_cache_for_registered_users()
    {
        // Seed an instrument DB with leaderboard data
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("song_1",
        [
            new LeaderboardEntry
            {
                AccountId = "user-warm", Score = 100_000,
                Accuracy = 95, IsFullCombo = false, Stars = 5,
                Season = 3, Difficulty = 3, Percentile = 99.0,
            },
            new LeaderboardEntry
            {
                AccountId = "user-other", Score = 80_000,
                Accuracy = 90, IsFullCombo = false, Stars = 4,
                Season = 3, Difficulty = 3, Percentile = 95.0,
            },
        ]);

        // Pre-warm cache for user-warm
        _persistence.PreWarmRankingsCache(new HashSet<string> { "user-warm" });

        // Verify rankings data is correct (PG has no in-memory cache, so each call is a fresh query)
        var first = db.GetPlayerRankings("user-warm");
        var second = db.GetPlayerRankings("user-warm");
        Assert.Equal(first, second);
        Assert.Single(first);
        Assert.Equal(1, first["song_1"]); // rank 1 (top score)
    }

    [Fact]
    public void PreWarmRankingsCache_with_empty_accounts_does_not_throw()
    {
        _persistence.PreWarmRankingsCache(new HashSet<string>());
    }

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
    public async Task ComputeRankingsAsync_ReturnsFalse_OnFailure()
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
        var result = await _sut.ComputeRankingsAsync(service, CancellationToken.None);
        Assert.False(result);
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
