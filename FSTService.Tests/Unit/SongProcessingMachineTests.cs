using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for the parallel <see cref="SongProcessingMachine"/> with <see cref="SharedDopPool"/>.
/// </summary>
public class SongProcessingMachineTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaDb = new();
    private readonly string _dataDir;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly ILeaderboardQuerier _scraper = Substitute.For<ILeaderboardQuerier>();
    private readonly ScrapeProgressTracker _progress = new();
    private readonly ILogger<SongProcessingMachine> _machineLog = Substitute.For<ILogger<SongProcessingMachine>>();
    private readonly ILogger<BatchResultProcessor> _processorLog = Substitute.For<ILogger<BatchResultProcessor>>();
    private readonly SharedDopPool _pool;

    public SongProcessingMachineTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"fst_machine_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        loggerFactory.CreateLogger<InstrumentDatabase>().Returns(Substitute.For<ILogger<InstrumentDatabase>>());
        var persLog = Substitute.For<ILogger<GlobalLeaderboardPersistence>>();
        _persistence = new GlobalLeaderboardPersistence(_dataDir, _metaDb.Db, loggerFactory, persLog);
        _persistence.Initialize();
        _pool = new SharedDopPool(32, minDop: 2, maxDop: 64, lowPriorityPercent: 20, Substitute.For<ILogger>());
    }

    public void Dispose()
    {
        _pool.Dispose();
        _persistence.Dispose();
        _metaDb.Dispose();
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private SongProcessingMachine CreateMachine()
    {
        var processor = new BatchResultProcessor(_persistence, _processorLog);
        return new SongProcessingMachine(_scraper, processor, _persistence, _progress, _machineLog);
    }

    private static readonly SeasonWindowInfo[] TestSeasonWindows =
    [
        new() { SeasonNumber = 12, EventId = "season012_test", WindowId = "season012" },
        new() { SeasonNumber = 13, EventId = "season013_test", WindowId = "season013" },
    ];

    // ─── Empty inputs ────────────────────────────────────────

    [Fact]
    public async Task RunAsync_NoUsers_CompletesImmediately()
    {
        var machine = CreateMachine();
        var result = await machine.RunAsync(
            ["song1", "song2"], [], TestSeasonWindows,
            "token", "caller", _pool, ct: CancellationToken.None);

        Assert.Equal(0, result.EntriesUpdated);
        Assert.Equal(0, result.UsersProcessed);
    }

    [Fact]
    public async Task RunAsync_NoSongs_CompletesImmediately()
    {
        var machine = CreateMachine();
        var users = new[] { new UserWorkItem { AccountId = "user1", Purposes = WorkPurpose.Backfill, AllTimeNeeded = true } };

        var result = await machine.RunAsync(
            [], users, TestSeasonWindows,
            "token", "caller", _pool, ct: CancellationToken.None);

        Assert.Equal(0, result.EntriesUpdated);
    }

    // ─── All songs processed in parallel ────────────────────

    [Fact]
    public async Task RunAsync_AllSongsProcessedForAllUsers()
    {
        _scraper.LookupMultipleAccountsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var targets = callInfo.ArgAt<IReadOnlyList<string>>(2);
                return targets.Select(id => new LeaderboardEntry
                {
                    AccountId = id, Score = 100_000, Rank = 42, Percentile = 0.01,
                    Stars = 5, Accuracy = 950000, Season = 13,
                }).ToList();
            });

        var machine = CreateMachine();
        var users = new[]
        {
            new UserWorkItem { AccountId = "user1", Purposes = WorkPurpose.Backfill, AllTimeNeeded = true },
            new UserWorkItem { AccountId = "user2", Purposes = WorkPurpose.Backfill, AllTimeNeeded = true },
        };

        var result = await machine.RunAsync(
            ["song1", "song2"], users, TestSeasonWindows,
            "token", "caller", _pool, ct: CancellationToken.None);

        // 2 songs × 6 instruments × 2 users = 24 entries
        Assert.Equal(24, result.EntriesUpdated);
        Assert.Equal(2, result.UsersProcessed);

        // Should have made 12 batch calls (2 songs × 6 instruments), each containing both users
        await _scraper.Received(12).LookupMultipleAccountsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Is<IReadOnlyList<string>>(l => l.Count == 2),
            "token", "caller", Arg.Any<AdaptiveConcurrencyLimiter?>(), Arg.Any<CancellationToken>());
    }

    // ─── Seasonal queries per user needs ────────────────────

    [Fact]
    public async Task RunAsync_SeasonalLookup_QueriesOnlyNeededSeasons()
    {
        _scraper.LookupMultipleAccountsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<LeaderboardEntry>());

        _scraper.LookupMultipleAccountSessionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<AdaptiveConcurrencyLimiter?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SessionHistoryEntry>());

        var machine = CreateMachine();
        var users = new[]
        {
            new UserWorkItem { AccountId = "user1", Purposes = WorkPurpose.PostScrape, AllTimeNeeded = true, SeasonsNeeded = [13] },
            new UserWorkItem { AccountId = "user2", Purposes = WorkPurpose.HistoryRecon, AllTimeNeeded = false, SeasonsNeeded = [12, 13] },
        };

        await machine.RunAsync(
            ["song1"], users, TestSeasonWindows,
            "token", "caller", _pool, ct: CancellationToken.None);

        // Season 13: both users → 6 calls with 2 targets
        await _scraper.Received(6).LookupMultipleAccountSessionsAsync(
            "song1", Arg.Any<string>(), "season013",
            Arg.Is<IReadOnlyList<string>>(l => l.Count == 2),
            "token", "caller", Arg.Any<AdaptiveConcurrencyLimiter?>(), Arg.Any<CancellationToken>());

        // Season 12: only user2 → 6 calls with 1 target
        await _scraper.Received(6).LookupMultipleAccountSessionsAsync(
            "song1", Arg.Any<string>(), "season012",
            Arg.Is<IReadOnlyList<string>>(l => l.Count == 1),
            "token", "caller", Arg.Any<AdaptiveConcurrencyLimiter?>(), Arg.Any<CancellationToken>());
    }

    // ─── Batch chunking ─────────────────────────────────────

    [Fact]
    public async Task RunAsync_ChunksLargeBatches()
    {
        _scraper.LookupMultipleAccountsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<LeaderboardEntry>());

        var machine = CreateMachine();
        var users = Enumerable.Range(0, 5).Select(i =>
            new UserWorkItem { AccountId = $"user{i}", Purposes = WorkPurpose.Backfill, AllTimeNeeded = true }).ToList();

        // Batch size 3 → 2 chunks (3 + 2)
        await machine.RunAsync(
            ["song1"], users, TestSeasonWindows,
            "token", "caller", _pool, batchSize: 3, ct: CancellationToken.None);

        // 1 song × 6 instruments × 2 chunks = 12 calls
        await _scraper.Received(12).LookupMultipleAccountsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>());
    }

    // ─── Already-checked pairs skipped ──────────────────────

    [Fact]
    public async Task RunAsync_SkipsAlreadyCheckedPairs()
    {
        _scraper.LookupMultipleAccountsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<LeaderboardEntry>());

        var machine = CreateMachine();
        var users = new[]
        {
            new UserWorkItem
            {
                AccountId = "user1", Purposes = WorkPurpose.Backfill, AllTimeNeeded = true,
                AlreadyChecked = new HashSet<(string, string)>
                {
                    ("song1", "Solo_Guitar"), ("song1", "Solo_Bass"), ("song1", "Solo_Vocals"),
                    ("song1", "Solo_Drums"), ("song1", "Solo_PeripheralGuitar"), ("song1", "Solo_PeripheralBass"),
                },
            },
        };

        await machine.RunAsync(
            ["song1"], users, TestSeasonWindows,
            "token", "caller", _pool, ct: CancellationToken.None);

        // All instruments already checked → no API calls
        await _scraper.DidNotReceive().LookupMultipleAccountsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>());
    }

    // ─── Cancellation ───────────────────────────────────────

    [Fact]
    public async Task RunAsync_Cancellation_ThrowsOperationCanceledException()
    {
        _scraper.LookupMultipleAccountsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await Task.Delay(5000, callInfo.ArgAt<CancellationToken>(6));
                return new List<LeaderboardEntry>();
            });

        var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        var machine = CreateMachine();
        var users = new[] { new UserWorkItem { AccountId = "user1", Purposes = WorkPurpose.Backfill, AllTimeNeeded = true } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            machine.RunAsync(
                ["song1", "song2", "song3"], users, TestSeasonWindows,
                "token", "caller", _pool, ct: cts.Token));
    }

    // ─── High vs low priority uses pool correctly ───────────

    [Fact]
    public async Task RunAsync_HighPriority_UsesPoolDirectly()
    {
        _scraper.LookupMultipleAccountsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<LeaderboardEntry>());

        var machine = CreateMachine();
        var users = new[] { new UserWorkItem { AccountId = "user1", Purposes = WorkPurpose.PostScrape, AllTimeNeeded = true } };

        // Should complete without error using high priority
        var result = await machine.RunAsync(
            ["song1"], users, TestSeasonWindows,
            "token", "caller", _pool, isHighPriority: true, ct: CancellationToken.None);

        Assert.Equal(1, result.UsersProcessed);
    }

    [Fact]
    public async Task RunAsync_LowPriority_UsesPoolGated()
    {
        _scraper.LookupMultipleAccountsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<LeaderboardEntry>());

        var machine = CreateMachine();
        var users = new[] { new UserWorkItem { AccountId = "user1", Purposes = WorkPurpose.Backfill, AllTimeNeeded = true } };

        // Should complete without error using low priority
        var result = await machine.RunAsync(
            ["song1"], users, TestSeasonWindows,
            "token", "caller", _pool, isHighPriority: false, ct: CancellationToken.None);

        Assert.Equal(1, result.UsersProcessed);
    }

    // ─── Song-level concurrency cap ─────────────────────────

    [Fact]
    public async Task RunAsync_MaxConcurrentSongs_LimitsConcurrency()
    {
        int peakConcurrency = 0;
        int currentConcurrency = 0;

        _scraper.LookupMultipleAccountsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var c = Interlocked.Increment(ref currentConcurrency);
                // Track peak
                int oldPeak;
                do { oldPeak = Volatile.Read(ref peakConcurrency); }
                while (c > oldPeak && Interlocked.CompareExchange(ref peakConcurrency, c, oldPeak) != oldPeak);

                await Task.Delay(50, callInfo.ArgAt<CancellationToken>(6));
                Interlocked.Decrement(ref currentConcurrency);

                return new List<LeaderboardEntry>();
            });

        var machine = CreateMachine();
        var users = new[] { new UserWorkItem { AccountId = "user1", Purposes = WorkPurpose.Backfill, AllTimeNeeded = true } };

        // 10 songs, maxConcurrentSongs=2. Each song has 6 instruments, so max concurrent calls = 2 × 6 = 12.
        var songIds = Enumerable.Range(0, 10).Select(i => $"song{i}").ToList();

        var result = await machine.RunAsync(
            songIds, users, TestSeasonWindows,
            "token", "caller", _pool, isHighPriority: true,
            batchSize: 500, reportProgress: false,
            maxConcurrentSongs: 2, ct: CancellationToken.None);

        // With 2 concurrent songs × 6 instruments = 12 max concurrent calls.
        // Without the cap it would be 10 × 6 = 60.
        Assert.True(peakConcurrency <= 12,
            $"Peak concurrency was {peakConcurrency}, expected ≤ 12 (2 songs × 6 instruments)");
        Assert.Equal(1, result.UsersProcessed);
    }

    [Fact]
    public async Task RunAsync_MaxConcurrentSongsZero_NoLimit()
    {
        _scraper.LookupMultipleAccountsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AdaptiveConcurrencyLimiter?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<LeaderboardEntry>());

        var machine = CreateMachine();
        var users = new[] { new UserWorkItem { AccountId = "user1", Purposes = WorkPurpose.Backfill, AllTimeNeeded = true } };

        // maxConcurrentSongs=0 → no semaphore created, all songs run freely
        var result = await machine.RunAsync(
            ["song1", "song2", "song3"], users, TestSeasonWindows,
            "token", "caller", _pool, isHighPriority: true,
            batchSize: 500, reportProgress: false,
            maxConcurrentSongs: 0, ct: CancellationToken.None);

        Assert.Equal(1, result.UsersProcessed);
    }
}
