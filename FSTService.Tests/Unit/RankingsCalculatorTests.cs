using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
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
            _tempDir, _metaFixture.Db,
            Substitute.For<ILoggerFactory>(),
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>());
        _persistence.Initialize();

        // Create a minimal fst-service.db for PathDataStore
        var coreDbPath = Path.Combine(_tempDir, "fst-service.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={coreDbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS Songs (
                    SongId TEXT PRIMARY KEY,
                    MaxLeadScore INTEGER, MaxBassScore INTEGER, MaxDrumsScore INTEGER,
                    MaxVocalsScore INTEGER, MaxProLeadScore INTEGER, MaxProBassScore INTEGER,
                    DatFileHash TEXT, SongLastModified TEXT, PathsGeneratedAt TEXT, CHOptVersion TEXT
                );
                """;
            cmd.ExecuteNonQuery();
        }
        _pathStore = new PathDataStore(coreDbPath);

        _sut = new RankingsCalculator(_persistence, _metaFixture.Db,
            _pathStore, new ScrapeProgressTracker(), Substitute.For<ILogger<RankingsCalculator>>());
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
}
