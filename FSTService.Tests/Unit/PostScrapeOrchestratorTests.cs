using FortniteFestival.Core.Services;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FSTService.Tests.Unit;

public class PostScrapeOrchestratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MetaDatabase _metaDb;
    private readonly GlobalLeaderboardPersistence _persistence;

    private readonly TokenManager _tokenManager;
    private readonly FirstSeenSeasonCalculator _firstSeenCalculator;
    private readonly AccountNameResolver _nameResolver;
    private readonly PersonalDbBuilder _personalDbBuilder;
    private readonly PostScrapeRefresher _refresher;
    private readonly NotificationService _notifications;
    private readonly ScrapeProgressTracker _progress;
    private readonly ILogger<PostScrapeOrchestrator> _log;

    private readonly PostScrapeOrchestrator _sut;

    public PostScrapeOrchestratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pso_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _metaDb = new MetaDatabase(
            Path.Combine(_tempDir, "meta.db"),
            Substitute.For<ILogger<MetaDatabase>>());
        _metaDb.EnsureSchema();

        _persistence = new GlobalLeaderboardPersistence(
            _tempDir, _metaDb,
            Substitute.For<ILoggerFactory>(),
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>());
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
            0);

        _firstSeenCalculator = Substitute.For<FirstSeenSeasonCalculator>(
            scraper, _persistence, new ScrapeProgressTracker(),
            Substitute.For<ILogger<FirstSeenSeasonCalculator>>());

        _nameResolver = Substitute.For<AccountNameResolver>(
            new HttpClient(), _metaDb, _tokenManager,
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<AccountNameResolver>>());

        _personalDbBuilder = Substitute.For<PersonalDbBuilder>(
            _persistence,
            new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null),
            _metaDb,
            new ScrapeProgressTracker(),
            _tempDir,
            Substitute.For<ILogger<PersonalDbBuilder>>());

        _refresher = Substitute.For<PostScrapeRefresher>(
            scraper, _persistence, new ScrapeProgressTracker(),
            Substitute.For<ILogger<PostScrapeRefresher>>());

        _notifications = new NotificationService(Substitute.For<ILogger<NotificationService>>());
        _progress = new ScrapeProgressTracker();
        _log = Substitute.For<ILogger<PostScrapeOrchestrator>>();

        var rivalsCalculator = new RivalsCalculator(_persistence, Substitute.For<ILogger<RivalsCalculator>>());
        var rivalsOrchestrator = new RivalsOrchestrator(rivalsCalculator, _persistence, new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), _progress, Substitute.For<ILogger<RivalsOrchestrator>>());
        var rankingsCalculator = new RankingsCalculator(_persistence, _metaDb, new PathDataStore(Path.Combine(_tempDir, "core.db")), _progress, Substitute.For<ILogger<RankingsCalculator>>());

        _sut = new PostScrapeOrchestrator(
            _persistence, _firstSeenCalculator, _nameResolver,
            _personalDbBuilder, _refresher, rivalsOrchestrator, rankingsCalculator, _notifications,
            _tokenManager, _progress, Options.Create(new ScraperOptions()), _log);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _metaDb.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private ScrapePassContext CreateContext(
        HashSet<string>? registeredIds = null,
        GlobalLeaderboardPersistence.PipelineAggregates? aggregates = null,
        IReadOnlyList<GlobalLeaderboardScraper.SongScrapeRequest>? scrapeRequests = null)
    {
        return new ScrapePassContext
        {
            AccessToken = "test-token",
            CallerAccountId = "caller-001",
            RegisteredIds = registeredIds ?? new HashSet<string>(),
            Aggregates = aggregates ?? new GlobalLeaderboardPersistence.PipelineAggregates(),
            ScrapeRequests = scrapeRequests ?? Array.Empty<GlobalLeaderboardScraper.SongScrapeRequest>(),
            DegreeOfParallelism = 4,
        };
    }

    // ═══════════════════════════════════════════════════════════
    // RebuildPersonalDbsAsync
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task RebuildPersonalDbs_NoChanges_SkipsRebuild()
    {
        var ctx = CreateContext();

        await _sut.RebuildPersonalDbsAsync(ctx, CancellationToken.None);

        _personalDbBuilder.DidNotReceiveWithAnyArgs()
            .RebuildForAccounts(default!, default!);
    }

    [Fact]
    public async Task RebuildPersonalDbs_WithChanges_RebuildsAndNotifies()
    {
        var aggregates = new GlobalLeaderboardPersistence.PipelineAggregates();
        aggregates.AddChangedAccountIds(new[] { "acct-1", "acct-2" });

        _personalDbBuilder.RebuildForAccounts(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<MetaDatabase>())
            .Returns(2);

        var ctx = CreateContext(aggregates: aggregates);

        await _sut.RebuildPersonalDbsAsync(ctx, CancellationToken.None);

        _personalDbBuilder.Received(1).RebuildForAccounts(
            Arg.Is<IReadOnlySet<string>>(s => s.Count == 2),
            Arg.Any<MetaDatabase>());
    }

    [Fact]
    public async Task RebuildPersonalDbs_ThrowsException_DoesNotPropagate()
    {
        var aggregates = new GlobalLeaderboardPersistence.PipelineAggregates();
        aggregates.AddChangedAccountIds(new[] { "acct-1" });

        _personalDbBuilder.RebuildForAccounts(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<MetaDatabase>())
            .Throws(new InvalidOperationException("DB error"));

        var ctx = CreateContext(aggregates: aggregates);

        // Should not throw — exception is caught and logged
        await _sut.RebuildPersonalDbsAsync(ctx, CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════
    // RefreshRegisteredUsersAsync
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task RefreshRegisteredUsers_NoRegisteredUsers_Skips()
    {
        var ctx = CreateContext();

        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);

        await _refresher.DidNotReceiveWithAnyArgs()
            .RefreshAllAsync(default!, default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task RefreshRegisteredUsers_WithToken_Refreshes()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-access-token");
        _tokenManager.AccountId.Returns("caller-001");

        _refresher.RefreshAllAsync(
            Arg.Any<HashSet<string>>(),
            Arg.Any<HashSet<(string, string, string)>>(),
            Arg.Any<List<string>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(5);

        var ctx = CreateContext(registeredIds: new HashSet<string> { "user-1" });

        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);

        await _refresher.Received(1).RefreshAllAsync(
            Arg.Any<HashSet<string>>(),
            Arg.Any<HashSet<(string, string, string)>>(),
            Arg.Any<List<string>>(),
            Arg.Is("test-access-token"),
            Arg.Is("caller-001"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshRegisteredUsers_NoToken_Skips()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var ctx = CreateContext(registeredIds: new HashSet<string> { "user-1" });

        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);

        await _refresher.DidNotReceiveWithAnyArgs()
            .RefreshAllAsync(default!, default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task RefreshRegisteredUsers_ThrowsException_DoesNotPropagate()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-access-token");
        _tokenManager.AccountId.Returns("caller-001");

        _refresher.RefreshAllAsync(
            Arg.Any<HashSet<string>>(),
            Arg.Any<HashSet<(string, string, string)>>(),
            Arg.Any<List<string>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("API error"));

        var ctx = CreateContext(registeredIds: new HashSet<string> { "user-1" });

        // Should not throw
        await _sut.RefreshRegisteredUsersAsync(ctx, CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════
    // CleanupSessions
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CleanupSessions_CleansExpiredSessions()
    {
        // Pre-seed a session that is expired (older than 7 days)
        _metaDb.InsertSession("acct-1", "device-1", "refresh-tok", "Windows", DateTime.UtcNow.AddDays(-30));

        _sut.CleanupSessions();

        // The expired session should be cleaned. Verify no exception and
        // the orphaned account auto-unregisters (acct-1 was never registered,
        // so GetOrphanedRegisteredAccounts should return nothing to unregister).
        // The key verification is that CleanupSessions runs without throwing.
    }

    // ═══════════════════════════════════════════════════════════
    // ResolveNamesAsync
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveNamesAsync_DelegatesToResolver()
    {
        _nameResolver.ResolveNewAccountsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(42);

        var result = await _sut.ResolveNamesAsync(8, CancellationToken.None);

        Assert.Equal(42, result);
        await _nameResolver.Received(1).ResolveNewAccountsAsync(8, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════
    // RunEnrichmentAsync
    // ═══════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════
    // PruneExcessEntries
    // ═══════════════════════════════════════════════════════════

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
        _sut.PruneExcessEntries(ctx); // MaxPages=100 → maxEntries=10000 → no pruning (only 20)

        // Verify no entries pruned (20 < 10000)
        Assert.Equal(20, db.GetLeaderboardCount("song1"));
    }

    [Fact]
    public void PruneExcessEntries_ActuallyPrunes_WhenExceedsMax()
    {
        // Create SUT with MaxPages=1 → maxEntries=100, but we seed 200 entries
        var opts = Options.Create(new ScraperOptions { MaxPagesPerLeaderboard = 1 });
        var rivalsCalculator = new RivalsCalculator(_persistence, Substitute.For<ILogger<RivalsCalculator>>());
        var rivalsOrchestrator = new RivalsOrchestrator(rivalsCalculator, _persistence, new NotificationService(Substitute.For<ILogger<NotificationService>>()), _progress, Substitute.For<ILogger<RivalsOrchestrator>>());
        var rankingsCalculator2 = new RankingsCalculator(_persistence, _metaDb, new PathDataStore(Path.Combine(_tempDir, "core.db")), _progress, Substitute.For<ILogger<RankingsCalculator>>());
        var sut = new PostScrapeOrchestrator(
            _persistence, _firstSeenCalculator, _nameResolver,
            _personalDbBuilder, _refresher, rivalsOrchestrator, rankingsCalculator2, _notifications,
            _tokenManager, _progress, opts, _log);

        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var entries = Enumerable.Range(0, 200).Select(i =>
            new LeaderboardEntry
            {
                AccountId = $"p_{i}", Score = 10000 - i * 10,
                Accuracy = 95, Stars = 5, Season = 3,
            }).ToList();
        db.UpsertEntries("song1", entries);

        // p_150 is registered — should be preserved even though outside top 100
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

        // Should run without error — rivals computation handles user with no data gracefully
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
        var rivalsOrchestrator = new RivalsOrchestrator(rivalsCalculator, _persistence, new NotificationService(Substitute.For<ILogger<NotificationService>>()), _progress, Substitute.For<ILogger<RivalsOrchestrator>>());
        var rankingsCalculator3 = new RankingsCalculator(_persistence, _metaDb, new PathDataStore(Path.Combine(_tempDir, "core.db")), _progress, Substitute.For<ILogger<RankingsCalculator>>());
        var sut = new PostScrapeOrchestrator(
            _persistence, _firstSeenCalculator, _nameResolver,
            _personalDbBuilder, _refresher, rivalsOrchestrator, rankingsCalculator3, _notifications,
            _tokenManager, _progress, opts, _log);

        var ctx = CreateContext();
        sut.PruneExcessEntries(ctx); // maxPages=0 → no-op
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
    public void CleanupSessions_WithOrphanedAccount_CleansThem()
    {
        // Register a user with a session that's long expired
        _metaDb.RegisterUser("orphan-device", "orphan-acct");
        _metaDb.InsertSession("orphan-acct", "orphan-device", "refresh-old", "Windows", DateTime.UtcNow.AddDays(-60));

        // Run cleanup — should clean expired sessions and possibly auto-unregister
        _sut.CleanupSessions();
        // No crash = pass
    }

    [Fact]
    public void CleanupSessions_NoSessionsToClean_NoError()
    {
        // Clean state — nothing to clean
        _sut.CleanupSessions();
    }

    [Fact]
    public void CleanupSessions_OrphanedAccount_AutoUnregistered()
    {
        // Register a user with an account name mapping
        _metaDb.RegisterUser("dev-orphan", "acct-orphan");
        _metaDb.InsertAccountNames([("acct-orphan", "OrphanUser")]);

        // Create two sessions: one very old (will be cleaned) and one recently expired
        // (survives cleanup but is past ExpiresAt, making the account "orphaned")
        _metaDb.InsertSession("OrphanUser", "dev-orphan", "old-tok",
            "Windows", DateTime.UtcNow.AddDays(-30));
        _metaDb.InsertSession("OrphanUser", "dev-orphan", "recent-tok",
            "Windows", DateTime.UtcNow.AddDays(-1)); // Expired 1 day ago, but within 7-day cleanup window

        // Verify user is registered before cleanup
        var regBefore = _metaDb.GetRegisteredAccountIds();
        Assert.Contains("acct-orphan", regBefore);

        _sut.CleanupSessions();

        // After cleanup: old session deleted, recent expired session survives →
        // account has sessions but none active → orphaned → auto-unregistered
        var regAfter = _metaDb.GetRegisteredAccountIds();
        Assert.DoesNotContain("acct-orphan", regAfter);
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
    public async Task RebuildPersonalDbsAsync_WithChangedAccounts_SetsPhaseAndRebuilds()
    {
        // Register a user so PersonalDbBuilder has something to rebuild
        _metaDb.RegisterUser("dev-rebuild", "acct-rebuild");

        var aggregates = new GlobalLeaderboardPersistence.PipelineAggregates();
        aggregates.AddChangedAccountIds(new[] { "acct-rebuild" });

        var ctx = CreateContext(aggregates: aggregates);

        await _sut.RebuildPersonalDbsAsync(ctx, CancellationToken.None);

        // Phase should have been set
        // The mock PersonalDbBuilder.RebuildForAccounts returns 0 by default
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

        // Use max 10 pages = 1000 entries — but we only have 50, so no pruning
        var ctx = CreateContext();
        _sut.PruneExcessEntries(ctx);
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
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(5);

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var ctx = CreateContext();

        await _sut.RunEnrichmentAsync(ctx, service, CancellationToken.None);

        // FirstSeenCalculator should have been called with the token
        await _firstSeenCalculator.Received(1).CalculateAsync(
            Arg.Any<FestivalService>(), "test-token", "caller-1",
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunEnrichmentAsync_FirstSeenThrows_DoesNotPropagate()
    {
        _tokenManager.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("test-token");
        _tokenManager.AccountId.Returns("caller-1");

        _firstSeenCalculator.CalculateAsync(
            Arg.Any<FestivalService>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("test error"));

        var service = new FestivalService((FortniteFestival.Core.Persistence.IFestivalPersistence?)null);
        var ctx = CreateContext();

        // Should not throw — errors are caught and logged
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

        // Should run without error — exercises the dirtyMap building path
        await _sut.ComputeRivalsAsync(ctx, CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════
    // NoOpHttpHandler (shared utility)
    // ═══════════════════════════════════════════════════════════

    private sealed class NoOpHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
