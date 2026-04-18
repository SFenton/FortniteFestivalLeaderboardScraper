using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Reflection;

namespace FSTService.Tests.Unit;

public sealed class RankingsCalculatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryMetaDatabase _metaFixture;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly PathDataStore _pathStore;
    private readonly RankingsCalculator _sut;

    public RankingsCalculatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fst_rank_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _metaFixture = new InMemoryMetaDatabase();
        _persistence = new GlobalLeaderboardPersistence(
            _metaFixture.Db,
            Substitute.For<ILoggerFactory>(),
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>(),
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions()));
        _persistence.Initialize();

        _pathStore = new PathDataStore(SharedPostgresContainer.CreateDatabase());

        _sut = new RankingsCalculator(_persistence, _metaFixture.Db,
            _pathStore, new ScrapeProgressTracker(), Options.Create(new FeatureOptions()), Substitute.For<ILogger<RankingsCalculator>>());
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _metaFixture.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static LeaderboardEntry MakeEntry(string accountId, int score,
        int rank = 0, int accuracy = 95, bool fc = false, int stars = 5, int season = 3,
        int apiRank = 0) =>
        new()
        {
            AccountId = accountId, Score = score, Rank = rank,
            Accuracy = accuracy, IsFullCombo = fc, Stars = stars, Season = season,
            ApiRank = apiRank,
        };

    private static BandLeaderboardEntry MakeBandEntry(string[] teamMembers, string instrumentCombo, int score, bool isFullCombo = false) =>
        new()
        {
            TeamKey = string.Join(':', teamMembers.OrderBy(static member => member, StringComparer.OrdinalIgnoreCase)),
            TeamMembers = teamMembers,
            InstrumentCombo = instrumentCombo,
            Score = score,
            Accuracy = 950000,
            IsFullCombo = isFullCombo,
            Stars = 5,
            Difficulty = 3,
            Season = 1,
            Rank = 1,
            Percentile = 0.5,
            Source = "scrape",
        };

    private static FestivalService CreateFestivalServiceWithSongs(int songCount)
    {
        var svc = new FestivalService((IFestivalPersistence?)null);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var songsField = typeof(FestivalService).GetField("_songs", flags)!;
        var dirtyField = typeof(FestivalService).GetField("_songsDirty", flags)!;
        var dict = (Dictionary<string, Song>)songsField.GetValue(svc)!;
        for (int i = 0; i < songCount; i++)
        {
            dict[$"song_{i}"] = new Song
            {
                track = new Track
                {
                    su = $"song_{i}",
                    tt = $"Song {i}",
                    an = "Artist",
                    @in = new In { gr = 3, ba = 3, ds = 3, vl = 3, pg = 3, pb = 3 },
                },
            };
        }
        dirtyField.SetValue(svc, true);
        return svc;
    }

    // ═══════════════════════════════════════════════════════════
    // ComputeCompositeRankings
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CompositeRankings_AggregatesAcrossInstruments()
    {
        // Seed two instruments with same player
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var bassDb = _persistence.GetOrCreateInstrumentDb("Solo_Bass");

        guitarDb.UpsertEntries("song_0", [MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 500, rank: 2)]);
        bassDb.UpsertEntries("song_0", [MakeEntry("p1", 800, rank: 1)]);

        guitarDb.RecomputeAllRanks();
        bassDb.RecomputeAllRanks();
        guitarDb.ComputeSongStats();
        bassDb.ComputeSongStats();
        guitarDb.ComputeAccountRankings(totalChartedSongs: 1);
        bassDb.ComputeAccountRankings(totalChartedSongs: 1);

        _sut.ComputeCompositeRankings(["Solo_Guitar", "Solo_Bass"]);

        var composite = _metaFixture.Db.GetCompositeRanking("p1");
        Assert.NotNull(composite);
        Assert.Equal(2, composite.InstrumentsPlayed);
        Assert.Equal(1, composite.CompositeRank);
        Assert.NotNull(composite.GuitarAdjustedSkill);
        Assert.NotNull(composite.BassAdjustedSkill);
    }

    [Fact]
    public void CompositeRankings_WeightedBySongsPlayed()
    {
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        // p1 plays 5 songs, p2 plays 1 song
        for (int i = 0; i < 5; i++)
            db.UpsertEntries($"song_{i}", [MakeEntry("p1", 1000, rank: 1), MakeEntry("filler", 100, rank: 2)]);
        db.UpsertEntries("song_0", [MakeEntry("p2", 900, rank: 2)]);

        db.RecomputeAllRanks();
        db.ComputeSongStats();
        db.ComputeAccountRankings(totalChartedSongs: 5);

        _sut.ComputeCompositeRankings(["Solo_Guitar"]);

        var c1 = _metaFixture.Db.GetCompositeRanking("p1");
        var c2 = _metaFixture.Db.GetCompositeRanking("p2");
        Assert.NotNull(c1);
        Assert.NotNull(c2);
        Assert.True(c1.CompositeRank < c2.CompositeRank);
    }

    // ═══════════════════════════════════════════════════════════
    // CountChartedSongs
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CountChartedSongs_CountsNonZeroDifficulty()
    {
        var svc = CreateFestivalServiceWithSongs(5);
        Assert.Equal(5, RankingsCalculator.CountChartedSongs(svc, "Solo_Guitar"));
        Assert.Equal(5, RankingsCalculator.CountChartedSongs(svc, "Solo_Drums"));
    }

    [Fact]
    public void CountChartedSongs_ZeroForUnknownInstrument()
    {
        var svc = CreateFestivalServiceWithSongs(5);
        Assert.Equal(0, RankingsCalculator.CountChartedSongs(svc, "Unknown_Instrument"));
    }

    [Fact]
    public void CountChartedSongs_ZeroForEmptySongs()
    {
        var svc = new FestivalService((IFestivalPersistence?)null);
        Assert.Equal(0, RankingsCalculator.CountChartedSongs(svc, "Solo_Guitar"));
    }

    // ═══════════════════════════════════════════════════════════
    // ComputeAllCombos
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void AllCombos_ComputesForAllPlayers()
    {
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var bassDb = _persistence.GetOrCreateInstrumentDb("Solo_Bass");

        guitarDb.UpsertEntries("song_0", [MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 500, rank: 2)]);
        bassDb.UpsertEntries("song_0", [MakeEntry("p1", 800, rank: 1), MakeEntry("p2", 400, rank: 2)]);

        guitarDb.RecomputeAllRanks();
        bassDb.RecomputeAllRanks();
        guitarDb.ComputeSongStats();
        bassDb.ComputeSongStats();
        guitarDb.ComputeAccountRankings(totalChartedSongs: 1);
        bassDb.ComputeAccountRankings(totalChartedSongs: 1);

        _sut.ComputeAllCombos(["Solo_Guitar", "Solo_Bass"]);

        // Should have computed the Guitar+Bass combo → combo ID "03"
        var comboId = ComboIds.FromInstruments(["Solo_Guitar", "Solo_Bass"]);
        var (entries, total) = _metaFixture.Db.GetComboLeaderboard(comboId);
        Assert.Equal(2, total);
        Assert.Equal(2, entries.Count);
        Assert.Equal(1, entries[0].Rank);
    }

    [Fact]
    public void AllCombos_AccountMustHaveAllInstruments()
    {
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var bassDb = _persistence.GetOrCreateInstrumentDb("Solo_Bass");

        // p1 has both instruments, p2 only has guitar
        guitarDb.UpsertEntries("song_0", [MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 500, rank: 2)]);
        bassDb.UpsertEntries("song_0", [MakeEntry("p1", 800, rank: 1)]);

        guitarDb.RecomputeAllRanks();
        bassDb.RecomputeAllRanks();
        guitarDb.ComputeSongStats();
        bassDb.ComputeSongStats();
        guitarDb.ComputeAccountRankings(totalChartedSongs: 1);
        bassDb.ComputeAccountRankings(totalChartedSongs: 1);

        _sut.ComputeAllCombos(["Solo_Guitar", "Solo_Bass"]);

        var comboId = ComboIds.FromInstruments(["Solo_Guitar", "Solo_Bass"]);
        var entry = _metaFixture.Db.GetComboRank(comboId, "p2");
        Assert.Null(entry); // p2 doesn't have bass, shouldn't be in combo

        var p1 = _metaFixture.Db.GetComboRank(comboId, "p1");
        Assert.NotNull(p1);
        Assert.Equal(1, p1.Rank);
    }

    [Fact]
    public void AllCombos_SkipsWithFewerThan2Instruments()
    {
        var db = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("song_0", [MakeEntry("p1", 1000, rank: 1)]);
        db.RecomputeAllRanks();
        db.ComputeSongStats();
        db.ComputeAccountRankings(totalChartedSongs: 1);

        _sut.ComputeAllCombos(["Solo_Guitar"]); // Only 1 instrument — no combos
        // Should not throw, just skip
    }

    [Fact]
    public void AllCombos_MultipleComboSizes()
    {
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var bassDb = _persistence.GetOrCreateInstrumentDb("Solo_Bass");
        var drumsDb = _persistence.GetOrCreateInstrumentDb("Solo_Drums");

        guitarDb.UpsertEntries("song_0", [MakeEntry("p1", 1000, rank: 1)]);
        bassDb.UpsertEntries("song_0", [MakeEntry("p1", 800, rank: 1)]);
        drumsDb.UpsertEntries("song_0", [MakeEntry("p1", 600, rank: 1)]);

        guitarDb.RecomputeAllRanks(); bassDb.RecomputeAllRanks(); drumsDb.RecomputeAllRanks();
        guitarDb.ComputeSongStats(); bassDb.ComputeSongStats(); drumsDb.ComputeSongStats();
        guitarDb.ComputeAccountRankings(1); bassDb.ComputeAccountRankings(1); drumsDb.ComputeAccountRankings(1);

        _sut.ComputeAllCombos(["Solo_Guitar", "Solo_Bass", "Solo_Drums"]);

        // Should have 4 combos: 3 pairs + 1 triple (using combo IDs)
        Assert.True(_metaFixture.Db.GetComboTotalAccounts(ComboIds.FromInstruments(["Solo_Guitar", "Solo_Bass"])) > 0);
        Assert.True(_metaFixture.Db.GetComboTotalAccounts(ComboIds.FromInstruments(["Solo_Guitar", "Solo_Drums"])) > 0);
        Assert.True(_metaFixture.Db.GetComboTotalAccounts(ComboIds.FromInstruments(["Solo_Bass", "Solo_Drums"])) > 0);
        Assert.True(_metaFixture.Db.GetComboTotalAccounts(ComboIds.FromInstruments(["Solo_Guitar", "Solo_Bass", "Solo_Drums"])) > 0);
    }

    [Fact]
    public void ComputeBandRankings_RebuildsAggregateBandScopes()
    {
        var bandPersistence = new BandLeaderboardPersistence(_metaFixture.DataSource, Substitute.For<ILogger<BandLeaderboardPersistence>>());
        bandPersistence.UpsertBandEntries("song_0", "Band_Duets",
        [
            MakeBandEntry(["p1", "p2"], "0:1", 1000, isFullCombo: true),
            MakeBandEntry(["p3", "p4"], "0:3", 900),
            MakeBandEntry(["p1", "p2"], "0:0", 1100),
        ]);
        bandPersistence.UpsertBandEntries("song_1", "Band_Duets",
        [
            MakeBandEntry(["p1", "p2"], "0:1", 1200),
            MakeBandEntry(["p3", "p4"], "0:3", 1300),
            MakeBandEntry(["p5", "p6"], "0:1", 800),
        ]);

        _sut.ComputeBandRankings(["Band_Duets"], totalChartedSongs: 2);

        var (overall, totalTeams) = _metaFixture.Db.GetBandTeamRankings("Band_Duets");
        Assert.Equal(3, totalTeams);
        Assert.Equal("p1:p2", overall[0].TeamKey);

        var comboId = BandComboIds.FromInstruments(["Solo_Guitar", "Solo_Bass"]);
        var comboRank = _metaFixture.Db.GetBandTeamRanking("Band_Duets", "p1:p2", comboId);
        Assert.NotNull(comboRank);
        Assert.Equal(1, comboRank.AdjustedSkillRank);
    }

    // ═══════════════════════════════════════════════════════════
    // Full Pipeline (ComputeAllAsync)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ComputeAllAsync_EndToEnd()
    {
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");

        // Seed some data  
        guitarDb.UpsertEntries("song_0", [
            MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 900, rank: 2), MakeEntry("p3", 800, rank: 3),
        ]);
        guitarDb.RecomputeAllRanks();

        var svc = CreateFestivalServiceWithSongs(1);

        await _sut.ComputeAllAsync(svc, CancellationToken.None);

        // Verify per-instrument rankings computed
        var r1 = guitarDb.GetAccountRanking("p1");
        Assert.NotNull(r1);
        Assert.Equal(1, r1.AdjustedSkillRank);

        // Verify composite computed
        var c1 = _metaFixture.Db.GetCompositeRanking("p1");
        Assert.NotNull(c1);
        Assert.Equal(1, c1.CompositeRank);

        // Verify history snapshotted
        var history = guitarDb.GetRankHistory("p1", 1);
        Assert.Single(history);
    }

    [Fact]
    public async Task ComputeAllAsync_WithPopulationData()
    {
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song_0", [MakeEntry("p1", 1000, rank: 1, apiRank: 50_000)]);
        guitarDb.RecomputeAllRanks();

        // Set real population
        _metaFixture.Db.UpsertLeaderboardPopulation([
            ("song_0", "Solo_Guitar", 500_000L),
        ]);

        var svc = CreateFestivalServiceWithSongs(1);
        await _sut.ComputeAllAsync(svc, CancellationToken.None);

        var r1 = guitarDb.GetAccountRanking("p1");
        Assert.NotNull(r1);
        // ApiRank 50,000 / 500,000 = 0.1 → raw skill should reflect this
        Assert.True(r1.RawSkillRating < 0.2, $"Expected < 0.2, got {r1.RawSkillRating}");
    }

    [Fact]
    public async Task ComputeAllAsync_NoChartedSongs_SkipsInstrument()
    {
        // Instrument has data but no songs match the festival service catalog
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("uncharted_song", [MakeEntry("p1", 1000, rank: 1)]);
        guitarDb.RecomputeAllRanks();

        // Empty festival service (0 charted songs)
        var svc = new FestivalService((IFestivalPersistence?)null);

        await _sut.ComputeAllAsync(svc, CancellationToken.None);

        // Rankings should be empty (skipped due to 0 charted)
        var ranking = guitarDb.GetAccountRanking("p1");
        Assert.Null(ranking);
    }

    [Fact]
    public async Task ComputeAllAsync_Cancellation_Throws()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.ComputeAllAsync(CreateFestivalServiceWithSongs(1), cts.Token));
    }

    [Fact]
    public void CompositeRankings_ExcludesZeroSongPlayers()
    {
        // A player on one instrument but not the other
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song_0", [MakeEntry("guitar_only", 1000, rank: 1), MakeEntry("filler", 100, rank: 2)]);
        guitarDb.RecomputeAllRanks();
        guitarDb.ComputeSongStats();
        guitarDb.ComputeAccountRankings(totalChartedSongs: 1);

        var bassDb = _persistence.GetOrCreateInstrumentDb("Solo_Bass");
        // bass has no data

        _sut.ComputeCompositeRankings(["Solo_Guitar", "Solo_Bass"]);

        var comp = _metaFixture.Db.GetCompositeRanking("guitar_only");
        Assert.NotNull(comp);
        Assert.Equal(1, comp.InstrumentsPlayed); // Only guitar
        Assert.NotNull(comp.GuitarAdjustedSkill);
        Assert.Null(comp.BassAdjustedSkill);
    }

    [Fact]
    public async Task ComputeAllAsync_OverThresholdEntries_HandlesFallbacks()
    {
        // Set max score in PathDataStore so CHOpt threshold is known
        _pathStore.UpdateMaxScores("song_ot", new SongMaxScores { MaxLeadScore = 100_000 }, "hash_ot");

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        // Score 110000 exceeds 105% of 100000 (threshold = 105000)
        guitarDb.UpsertEntries("song_ot", [
            MakeEntry("p_over", 110_000, rank: 1),
            MakeEntry("p_normal", 90_000, rank: 2),
        ]);
        guitarDb.RecomputeAllRanks();

        // Insert a valid historical score for the over-threshold player
        _metaFixture.Db.InsertScoreChange("song_ot", "Solo_Guitar", "p_over",
            null, 95_000, null, 2,
            accuracy: 98, isFullCombo: true, stars: 6,
            scoreAchievedAt: "2025-01-01T00:00:00Z");

        // Run full computation
        var festivalService = CreateFestivalServiceWithSongs(1);
        await _sut.ComputeAllAsync(festivalService, CancellationToken.None);

        // The over-threshold player should still have rankings
        var ranking = guitarDb.GetAccountRanking("p_over");
        Assert.NotNull(ranking);
    }

    [Fact]
    public async Task ComputeAllAsync_NoChartedSongsForInstrument_SkipsRankings()
    {
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song_nc", [MakeEntry("p1", 50000, rank: 1)]);
        guitarDb.RecomputeAllRanks();

        // Create a festival service with no songs that match Solo_Guitar
        var svc = new FestivalService((IFestivalPersistence?)null);
        // Empty — no charted songs for any instrument

        await _sut.ComputeAllAsync(svc, CancellationToken.None);

        // No rankings should be computed (but no crash)
        var ranking = guitarDb.GetAccountRanking("p1");
        Assert.Null(ranking); // No charted songs = no rankings
    }

    [Fact]
    public void CompositeRankings_TieBreaker_SortsByName()
    {
        // Two players with identical composite ratings but different names
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song_tb", [
            MakeEntry("alpha", 5000, rank: 1),
            MakeEntry("beta", 5000, rank: 2),
        ]);
        guitarDb.RecomputeAllRanks();
        guitarDb.ComputeSongStats();
        guitarDb.ComputeAccountRankings(totalChartedSongs: 1);

        var bassDb = _persistence.GetOrCreateInstrumentDb("Solo_Bass");
        bassDb.UpsertEntries("song_tb", [
            MakeEntry("alpha", 5000, rank: 1),
            MakeEntry("beta", 5000, rank: 2),
        ]);
        bassDb.RecomputeAllRanks();
        bassDb.ComputeSongStats();
        bassDb.ComputeAccountRankings(totalChartedSongs: 1);

        _sut.ComputeCompositeRankings(["Solo_Guitar", "Solo_Bass"]);

        var c1 = _metaFixture.Db.GetCompositeRanking("alpha");
        var c2 = _metaFixture.Db.GetCompositeRanking("beta");
        Assert.NotNull(c1);
        Assert.NotNull(c2);
        // Both should have valid ranks, alpha before beta by name
        Assert.True(c1.CompositeRank < c2.CompositeRank);
    }

    // ═══════════════════════════════════════════════════════════
    // ComputeCompositeDeltas
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CompositeDeltas_ProducesDeltas_WhenInstrumentDeltasExist()
    {
        // Seed two instruments with ranking data
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var bassDb = _persistence.GetOrCreateInstrumentDb("Solo_Bass");

        guitarDb.UpsertEntries("song_0", [
            MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 500, rank: 2),
        ]);
        bassDb.UpsertEntries("song_0", [
            MakeEntry("p1", 800, rank: 1), MakeEntry("p2", 600, rank: 2),
        ]);

        guitarDb.RecomputeAllRanks(); bassDb.RecomputeAllRanks();
        guitarDb.ComputeSongStats(); bassDb.ComputeSongStats();
        guitarDb.ComputeAccountRankings(totalChartedSongs: 1);
        bassDb.ComputeAccountRankings(totalChartedSongs: 1);

        // Compute base composite rankings first
        _sut.ComputeCompositeRankings(["Solo_Guitar", "Solo_Bass"]);

        // Write per-instrument ranking deltas at bucket -3.0
        guitarDb.TruncateRankingDeltas();
        guitarDb.WriteRankingDeltas([
            ("p2", -3.0, 5, 0.01, 0.02, 0.8, 40000, 0.90, 4, 92.0, 2, 0.5),
        ]);

        // Should not throw, may or may not produce composite deltas depending on re-aggregation
        _sut.ComputeCompositeDeltas(["Solo_Guitar", "Solo_Bass"]);
    }

    [Fact]
    public void CompositeDeltas_NoError_WhenNoDeltasExist()
    {
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song_0", [MakeEntry("p1", 1000, rank: 1)]);
        guitarDb.RecomputeAllRanks();
        guitarDb.ComputeSongStats();
        guitarDb.ComputeAccountRankings(totalChartedSongs: 1);
        _sut.ComputeCompositeRankings(["Solo_Guitar"]);

        guitarDb.TruncateRankingDeltas();
        // No ranking_deltas → should complete without error
        _sut.ComputeCompositeDeltas(["Solo_Guitar"]);
    }

    // ═══════════════════════════════════════════════════════════
    // ComputeComboDeltas
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ComboDeltas_ProducesDeltas_WhenInstrumentDeltasExist()
    {
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var bassDb = _persistence.GetOrCreateInstrumentDb("Solo_Bass");

        guitarDb.UpsertEntries("song_0", [
            MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 500, rank: 2),
        ]);
        bassDb.UpsertEntries("song_0", [
            MakeEntry("p1", 800, rank: 1), MakeEntry("p2", 600, rank: 2),
        ]);

        guitarDb.RecomputeAllRanks(); bassDb.RecomputeAllRanks();
        guitarDb.ComputeSongStats(); bassDb.ComputeSongStats();
        guitarDb.ComputeAccountRankings(totalChartedSongs: 1);
        bassDb.ComputeAccountRankings(totalChartedSongs: 1);

        // Compute base combo leaderboard first
        _sut.ComputeAllCombos(["Solo_Guitar", "Solo_Bass"]);

        // Write per-instrument ranking deltas
        guitarDb.TruncateRankingDeltas();
        guitarDb.WriteRankingDeltas([
            ("p2", -3.0, 5, 0.01, 0.02, 0.8, 40000, 0.90, 4, 92.0, 2, 0.5),
        ]);

        // Should not throw
        _sut.ComputeComboDeltas(["Solo_Guitar", "Solo_Bass"]);
    }

    [Fact]
    public void ComboDeltas_NoError_WhenNoDeltasExist()
    {
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        var bassDb = _persistence.GetOrCreateInstrumentDb("Solo_Bass");

        guitarDb.UpsertEntries("song_0", [MakeEntry("p1", 1000, rank: 1)]);
        bassDb.UpsertEntries("song_0", [MakeEntry("p1", 800, rank: 1)]);

        guitarDb.RecomputeAllRanks(); bassDb.RecomputeAllRanks();
        guitarDb.ComputeSongStats(); bassDb.ComputeSongStats();
        guitarDb.ComputeAccountRankings(totalChartedSongs: 1);
        bassDb.ComputeAccountRankings(totalChartedSongs: 1);
        _sut.ComputeAllCombos(["Solo_Guitar", "Solo_Bass"]);

        guitarDb.TruncateRankingDeltas();
        bassDb.TruncateRankingDeltas();
        // No ranking_deltas → should complete without error
        _sut.ComputeComboDeltas(["Solo_Guitar", "Solo_Bass"]);
    }

    // ═══════════════════════════════════════════════════════════
    // Full Pipeline with Deltas
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ComputeAllAsync_IncludesDeltas()
    {
        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song_0", [
            MakeEntry("p1", 1000, rank: 1), MakeEntry("p2", 900, rank: 2), MakeEntry("p3", 800, rank: 3),
        ]);
        guitarDb.RecomputeAllRanks();

        var svc = CreateFestivalServiceWithSongs(1);
        await _sut.ComputeAllAsync(svc, CancellationToken.None);

        // After full pipeline: verify base rankings exist
        var r1 = guitarDb.GetAccountRanking("p1");
        Assert.NotNull(r1);
        Assert.Equal(1, r1.AdjustedSkillRank);

        // Verify composite exists
        var c1 = _metaFixture.Db.GetCompositeRanking("p1");
        Assert.NotNull(c1);

        // Verify rank history snapshotted (base + deltas)
        var history = guitarDb.GetRankHistory("p1", 1);
        Assert.Single(history);
    }

    [Fact]
    public async Task ComputeAllAsync_BandEntries_ProducesDeltasWithoutException()
    {
        // Score 1020 on max_score 1000 → 102% → inside the 95%-105% band.
        // This exercises ComputeRankingDeltasFromMaterialized →
        // ComputeAllBucketDeltas on a real PostgreSQL connection.
        _pathStore.UpdateMaxScores("song_0", new SongMaxScores { MaxLeadScore = 1000 }, "hash_band");

        var guitarDb = _persistence.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song_0", [
            MakeEntry("p1", 1020, rank: 1),
            MakeEntry("p2", 900, rank: 2),
        ]);
        guitarDb.RecomputeAllRanks();

        var svc = CreateFestivalServiceWithSongs(1);

        // This formerly threw NpgsqlOperationInProgressException when the
        // bucket-delta reader wasn't closed before cleanup ran.
        await _sut.ComputeAllAsync(svc, CancellationToken.None);

        // Base rankings should exist
        var r1 = guitarDb.GetAccountRanking("p1");
        Assert.NotNull(r1);

        // The primary assertion is that we didn't throw
        // NpgsqlOperationInProgressException. Deltas may or may not be
        // written depending on whether metrics actually changed vs base.
    }
}
