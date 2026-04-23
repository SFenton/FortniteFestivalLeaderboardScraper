using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class RankingsEndpointsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryMetaDatabase _metaFixture;
    private readonly GlobalLeaderboardPersistence _persistence;

    public RankingsEndpointsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fst_rankep_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _metaFixture = new InMemoryMetaDatabase();
        _persistence = new GlobalLeaderboardPersistence(
            _metaFixture.Db,
            Substitute.For<ILoggerFactory>(),
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>(),
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions()));
        _persistence.Initialize();
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _metaFixture.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static LeaderboardEntry MakeEntry(string accountId, int score,
        int rank = 0, int accuracy = 95, bool fc = false, int stars = 5) =>
        new() { AccountId = accountId, Score = score, Rank = rank,
                Accuracy = accuracy, IsFullCombo = fc, Stars = stars, Season = 3 };

    private void SeedAndComputeRankings(string instrument, int songCount, int playersPerSong)
    {
        var db = _persistence.GetOrCreateInstrumentDb(instrument);
        for (int s = 0; s < songCount; s++)
        {
            var entries = Enumerable.Range(0, playersPerSong)
                .Select(i => MakeEntry($"p{i}", 10000 - i * 100, rank: i + 1))
                .ToList();
            db.UpsertEntries($"song_{s}", entries);
        }
        db.RecomputeAllRanks();
        db.ComputeSongStats();
        db.ComputeAccountRankings(totalChartedSongs: songCount);
    }

    // ═══════════════════════════════════════════════════════════
    // Per-instrument endpoint data availability
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void GetAccountRankings_ReturnsData()
    {
        SeedAndComputeRankings("Solo_Guitar", songCount: 2, playersPerSong: 5);
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        var (entries, total) = db.GetAccountRankings("adjusted", 1, 50);
        Assert.Equal(5, total);
        Assert.Equal(5, entries.Count);
        // Sorted by AdjustedSkillRank
        Assert.Equal(1, entries[0].AdjustedSkillRank);
    }

    [Fact]
    public void GetAccountRankings_RankByWeighted()
    {
        SeedAndComputeRankings("Solo_Guitar", songCount: 2, playersPerSong: 5);
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        var (entries, _) = db.GetAccountRankings("weighted", 1, 50);
        Assert.Equal(1, entries[0].WeightedRank);
    }

    [Fact]
    public void GetAccountRankings_RankByFcRate()
    {
        SeedAndComputeRankings("Solo_Guitar", songCount: 1, playersPerSong: 3);
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        var (entries, _) = db.GetAccountRankings("fcrate", 1, 50);
        Assert.Equal(1, entries[0].FcRateRank);
    }

    [Fact]
    public void GetAccountRankings_RankByTotalScore()
    {
        SeedAndComputeRankings("Solo_Guitar", songCount: 1, playersPerSong: 3);
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        var (entries, _) = db.GetAccountRankings("totalscore", 1, 50);
        Assert.Equal(1, entries[0].TotalScoreRank);
    }

    [Fact]
    public void GetAccountRankings_RankByMaxScore()
    {
        SeedAndComputeRankings("Solo_Guitar", songCount: 1, playersPerSong: 3);
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        var (entries, _) = db.GetAccountRankings("maxscore", 1, 50);
        Assert.Equal(1, entries[0].MaxScorePercentRank);
    }

    [Fact]
    public void GetAccountRankings_DefaultRankBy()
    {
        SeedAndComputeRankings("Solo_Guitar", songCount: 1, playersPerSong: 2);
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // "unknown" should fall back to adjusted
        var (entries, _) = db.GetAccountRankings("unknown_mode", 1, 50);
        Assert.NotEmpty(entries);
    }

    // ═══════════════════════════════════════════════════════════
    // Single account lookup
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void GetAccountRanking_ExistingAccount()
    {
        SeedAndComputeRankings("Solo_Guitar", songCount: 1, playersPerSong: 3);
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        var ranking = db.GetAccountRanking("p0");
        Assert.NotNull(ranking);
        Assert.Equal("p0", ranking.AccountId);
        Assert.True(ranking.TotalScore > 0);
    }

    [Fact]
    public void GetAccountRanking_Nonexistent()
    {
        SeedAndComputeRankings("Solo_Guitar", songCount: 1, playersPerSong: 1);
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        Assert.Null(db.GetAccountRanking("nobody"));
    }

    // ═══════════════════════════════════════════════════════════
    // Composite endpoint data availability
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CompositeRankings_Available_AfterComputation()
    {
        SeedAndComputeRankings("Solo_Guitar", songCount: 1, playersPerSong: 3);
        SeedAndComputeRankings("Solo_Bass", songCount: 1, playersPerSong: 3);

        var pathStore = new PathDataStore(SharedPostgresContainer.CreateDatabase());
        var calc = new RankingsCalculator(_persistence, _metaFixture.Db,
            pathStore, new ScrapeProgressTracker(), Options.Create(new FeatureOptions()), Substitute.For<ILogger<RankingsCalculator>>());
        calc.ComputeCompositeRankings(["Solo_Guitar", "Solo_Bass"]);

        var (entries, total) = _metaFixture.Db.GetCompositeRankings(1, 50);
        Assert.Equal(3, total);
        Assert.Equal(3, entries.Count);
        Assert.True(entries[0].CompositeRank == 1);
    }

    // ═══════════════════════════════════════════════════════════
    // Rank history endpoint
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void RankHistory_Available_AfterSnapshot()
    {
        SeedAndComputeRankings("Solo_Guitar", songCount: 1, playersPerSong: 3);
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        db.SnapshotRankHistory();

        var history = db.GetRankHistory("p0", days: 7);
        Assert.Single(history);
        Assert.True(history[0].AdjustedSkillRank > 0);
        Assert.NotNull(history[0].SnapshotTakenAt);
    }

    // ═══════════════════════════════════════════════════════════
    // Display name enrichment
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void DisplayNames_AvailableFromMetaDb()
    {
        _metaFixture.Db.InsertAccountNames(new List<(string AccountId, string? DisplayName)>
        {
            ("p0", "PlayerZero"),
            ("p1", "PlayerOne"),
        });

        Assert.Equal("PlayerZero", _metaFixture.Db.GetDisplayName("p0"));
        Assert.Null(_metaFixture.Db.GetDisplayName("unknown"));
    }
}
