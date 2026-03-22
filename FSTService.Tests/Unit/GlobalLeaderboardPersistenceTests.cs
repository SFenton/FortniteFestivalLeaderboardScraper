using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for <see cref="GlobalLeaderboardPersistence"/> focused on PersistResult,
/// score change detection, and pipeline aggregation.
/// </summary>
public sealed class GlobalLeaderboardPersistenceTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaFixture = new();
    private readonly string _dataDir;

    public GlobalLeaderboardPersistenceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"fst_glp_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        _metaFixture.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private GlobalLeaderboardPersistence CreatePersistence()
    {
        var loggerFactory = new NullLoggerFactory();
        var glp = new GlobalLeaderboardPersistence(
            _dataDir,
            _metaFixture.Db,
            loggerFactory,
            NullLogger<GlobalLeaderboardPersistence>.Instance);
        glp.Initialize();
        return glp;
    }

    private static GlobalLeaderboardResult MakeResult(
        string songId, string instrument, params (string AccountId, int Score)[] entries)
    {
        return new GlobalLeaderboardResult
        {
            SongId = songId,
            Instrument = instrument,
            Entries = entries.Select(e => new LeaderboardEntry
            {
                AccountId = e.AccountId,
                Score = e.Score,
                Accuracy = 95,
                IsFullCombo = false,
                Stars = 5,
                Season = 3,
                Percentile = 99.0,
            }).ToList(),
        };
    }

    // ═══ Basic Persistence ══════════════════════════════════════

    [Fact]
    public void PersistResult_inserts_entries()
    {
        using var glp = CreatePersistence();
        var result = MakeResult("song_1", "Solo_Guitar",
            ("acct_1", 100_000), ("acct_2", 90_000));

        var pr = glp.PersistResult(result);
        Assert.Equal(2, pr.RowsAffected);
    }

    [Fact]
    public void PersistResult_upserts_on_score_change()
    {
        using var glp = CreatePersistence();

        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_1", 80_000)));
        var pr = glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)));

        Assert.Equal(1, pr.RowsAffected);
    }

    [Fact]
    public void PersistResult_inserts_account_ids_into_meta()
    {
        using var glp = CreatePersistence();
        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_new", 100_000)));

        var unresolved = _metaFixture.Db.GetUnresolvedAccountIds();
        Assert.Contains("acct_new", unresolved);
    }

    // ═══ Score Change Detection ═════════════════════════════════

    [Fact]
    public void PersistResult_detects_score_change_for_registered_user()
    {
        using var glp = CreatePersistence();
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_1" };

        // Insert initial scores
        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_1", 80_000)));

        // Update with new scores
        var pr = glp.PersistResult(
            MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)),
            registered);

        Assert.Equal(1, pr.ScoreChangesDetected);
        Assert.Contains("acct_1", pr.ChangedAccountIds);

        // Verify change was recorded in meta DB
        var history = _metaFixture.Db.GetScoreHistory("acct_1");
        Assert.Single(history);
        Assert.Equal(80_000, history[0].OldScore);
        Assert.Equal(100_000, history[0].NewScore);
    }

    [Fact]
    public void PersistResult_detects_new_entry_for_registered_user()
    {
        using var glp = CreatePersistence();
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_1" };

        var pr = glp.PersistResult(
            MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)),
            registered);

        Assert.Equal(1, pr.ScoreChangesDetected);
        Assert.Contains("acct_1", pr.ChangedAccountIds);

        var history = _metaFixture.Db.GetScoreHistory("acct_1");
        Assert.Single(history);
        Assert.Null(history[0].OldScore);
        Assert.Equal(100_000, history[0].NewScore);
    }

    [Fact]
    public void PersistResult_no_change_for_same_score()
    {
        using var glp = CreatePersistence();
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_1" };

        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)));
        var pr = glp.PersistResult(
            MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)),
            registered);

        Assert.Equal(0, pr.ScoreChangesDetected);
        Assert.Empty(pr.ChangedAccountIds);
    }

    [Fact]
    public void PersistResult_ignores_unregistered_users()
    {
        using var glp = CreatePersistence();
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_tracked" };

        // Only acct_untracked changes, but it's not registered
        glp.PersistResult(MakeResult("song_1", "Solo_Guitar",
            ("acct_untracked", 50_000), ("acct_tracked", 100_000)));

        var pr = glp.PersistResult(
            MakeResult("song_1", "Solo_Guitar",
                ("acct_untracked", 60_000), ("acct_tracked", 100_000)),
            registered);

        Assert.Equal(0, pr.ScoreChangesDetected);
    }

    // ═══ Multi-Instrument Support ═══════════════════════════════

    [Fact]
    public void PersistResult_works_across_instruments()
    {
        using var glp = CreatePersistence();

        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)));
        glp.PersistResult(MakeResult("song_1", "Solo_Bass", ("acct_1", 90_000)));

        var counts = glp.GetEntryCounts();
        Assert.Equal(1, counts["Solo_Guitar"]);
        Assert.Equal(1, counts["Solo_Bass"]);
    }

    // ═══ GetPlayerProfile ═══════════════════════════════════════

    [Fact]
    public void GetPlayerProfile_aggregates_across_instruments()
    {
        using var glp = CreatePersistence();

        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)));
        glp.PersistResult(MakeResult("song_1", "Solo_Bass", ("acct_1", 90_000)));
        glp.PersistResult(MakeResult("song_2", "Solo_Guitar", ("acct_1", 80_000)));

        var profile = glp.GetPlayerProfile("acct_1");
        Assert.Equal(3, profile.Count);
    }

    [Fact]
    public void GetPlayerProfile_returns_empty_for_unknown_player()
    {
        using var glp = CreatePersistence();
        var profile = glp.GetPlayerProfile("nobody");
        Assert.Empty(profile);
    }

    // ═══ GetLeaderboard ═════════════════════════════════════════

    [Fact]
    public void GetLeaderboard_returns_sorted_entries()
    {
        using var glp = CreatePersistence();

        glp.PersistResult(MakeResult("song_1", "Solo_Guitar",
            ("acct_low", 50_000), ("acct_high", 100_000)));

        var board = glp.GetLeaderboard("song_1", "Solo_Guitar");
        Assert.NotNull(board);
        Assert.Equal(2, board.Count);
        Assert.Equal("acct_high", board[0].AccountId);
        Assert.Equal("acct_low", board[1].AccountId);
    }

    [Fact]
    public void GetLeaderboard_returns_null_for_unknown_instrument()
    {
        using var glp = CreatePersistence();
        var board = glp.GetLeaderboard("song_1", "NonExistentInstrument");
        Assert.Null(board);
    }

    // ═══ Pipeline Aggregates ════════════════════════════════════

    [Fact]
    public void PipelineAggregates_tracks_seen_registered_entries()
    {
        var agg = new GlobalLeaderboardPersistence.PipelineAggregates();

        agg.AddSeenRegisteredEntries([
            ("acct_1", "song_1", "Solo_Guitar"),
            ("acct_1", "song_2", "Solo_Guitar"),
            ("acct_2", "song_1", "Solo_Bass"),
        ]);

        Assert.Equal(3, agg.SeenRegisteredEntries.Count);
    }

    [Fact]
    public void PipelineAggregates_thread_safe_counters()
    {
        var agg = new GlobalLeaderboardPersistence.PipelineAggregates();

        Parallel.For(0, 100, _ =>
        {
            agg.AddEntries(1);
            agg.AddChanges(1);
            agg.IncrementSongsWithData();
        });

        Assert.Equal(100, agg.TotalEntries);
        Assert.Equal(100, agg.TotalChanges);
        Assert.Equal(100, agg.SongsWithData);
    }

    [Fact]
    public void PipelineAggregates_changed_account_ids_deduplicates()
    {
        var agg = new GlobalLeaderboardPersistence.PipelineAggregates();

        agg.AddChangedAccountIds(["acct_1", "acct_2"]);
        agg.AddChangedAccountIds(["acct_1", "acct_3"]);

        Assert.Equal(3, agg.ChangedAccountIds.Count);
    }

    // ═══ Initialize ═════════════════════════════════════════════

    [Fact]
    public void Initialize_creates_all_instrument_dbs()
    {
        using var glp = CreatePersistence();
        var keys = glp.GetInstrumentKeys();
        Assert.True(keys.Count >= 6, "Should have at least 6 instrument databases");
        Assert.Contains("Solo_Guitar", keys);
        Assert.Contains("Solo_Bass", keys);
        Assert.Contains("Solo_Drums", keys);
        Assert.Contains("Solo_Vocals", keys);
    }

    // ═══ GetEntryCounts ═════════════════════════════════════════

    [Fact]
    public void GetEntryCounts_returns_zeros_when_empty()
    {
        using var glp = CreatePersistence();
        var counts = glp.GetEntryCounts();
        Assert.True(counts.Count >= 6);
        Assert.All(counts.Values, c => Assert.Equal(0, c));
    }

    [Fact]
    public void GetEntryCounts_reflects_persisted_data()
    {
        using var glp = CreatePersistence();
        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)));
        glp.PersistResult(MakeResult("song_1", "Solo_Bass", ("acct_1", 90_000), ("acct_2", 80_000)));

        var counts = glp.GetEntryCounts();
        Assert.Equal(1, counts["Solo_Guitar"]);
        Assert.Equal(2, counts["Solo_Bass"]);
    }

    // ═══ GetInstrumentKeys ══════════════════════════════════════

    [Fact]
    public void GetInstrumentKeys_returns_all_known_instruments()
    {
        using var glp = CreatePersistence();
        var keys = glp.GetInstrumentKeys();
        Assert.Contains("Solo_Guitar", keys);
        Assert.Contains("Solo_Bass", keys);
        Assert.Contains("Solo_Drums", keys);
        Assert.Contains("Solo_Vocals", keys);
        Assert.Contains("Solo_PeripheralGuitar", keys);
        Assert.Contains("Solo_PeripheralBass", keys);
    }

    // ═══ GetMinSeasonAcrossInstruments ═══════════════════════════

    [Fact]
    public void GetMinSeasonAcrossInstruments_returns_null_when_empty()
    {
        using var glp = CreatePersistence();
        Assert.Null(glp.GetMinSeasonAcrossInstruments("song_1"));
    }

    [Fact]
    public void GetMinSeasonAcrossInstruments_finds_min_across_dbs()
    {
        using var glp = CreatePersistence();
        // Guitar has season 3, Bass has season 1
        var guitarResult = new GlobalLeaderboardResult
        {
            SongId = "song_1",
            Instrument = "Solo_Guitar",
            Entries = [new LeaderboardEntry { AccountId = "a", Score = 100, Season = 3 }],
        };
        var bassResult = new GlobalLeaderboardResult
        {
            SongId = "song_1",
            Instrument = "Solo_Bass",
            Entries = [new LeaderboardEntry { AccountId = "b", Score = 90, Season = 1 }],
        };
        glp.PersistResult(guitarResult);
        glp.PersistResult(bassResult);

        Assert.Equal(1, glp.GetMinSeasonAcrossInstruments("song_1"));
    }

    // ═══ GetMaxSeasonAcrossInstruments ═══════════════════════════

    [Fact]
    public void GetMaxSeasonAcrossInstruments_returns_null_when_empty()
    {
        using var glp = CreatePersistence();
        Assert.Null(glp.GetMaxSeasonAcrossInstruments());
    }

    [Fact]
    public void GetMaxSeasonAcrossInstruments_finds_max_across_dbs()
    {
        using var glp = CreatePersistence();
        var guitarResult = new GlobalLeaderboardResult
        {
            SongId = "song_1",
            Instrument = "Solo_Guitar",
            Entries = [new LeaderboardEntry { AccountId = "a", Score = 100, Season = 3 }],
        };
        var bassResult = new GlobalLeaderboardResult
        {
            SongId = "song_2",
            Instrument = "Solo_Bass",
            Entries = [new LeaderboardEntry { AccountId = "b", Score = 90, Season = 7 }],
        };
        glp.PersistResult(guitarResult);
        glp.PersistResult(bassResult);

        Assert.Equal(7, glp.GetMaxSeasonAcrossInstruments());
    }

    // ═══ Pipeline StartWriters + DrainWriters ═══════════════════

    [Fact]
    public async Task StartWriters_and_DrainWriters_process_items()
    {
        using var glp = CreatePersistence();
        var agg = glp.StartWriters();

        await glp.EnqueueResultAsync(
            MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)),
            registeredAccountIds: null);

        await glp.DrainWritersAsync();

        Assert.True(agg.TotalEntries > 0);
        Assert.Equal(1, glp.GetEntryCounts()["Solo_Guitar"]);
    }

    [Fact]
    public async Task EnqueueResultAsync_throws_if_writers_not_started()
    {
        using var glp = CreatePersistence();
        // Don't call StartWriters
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            glp.EnqueueResultAsync(
                MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000)),
                registeredAccountIds: null).AsTask());
    }

    [Fact]
    public async Task EnqueueResultAsync_unknown_instrument_drops_result()
    {
        using var glp = CreatePersistence();
        glp.StartWriters();

        // Enqueue a result with an instrument that has no writer channel
        await glp.EnqueueResultAsync(
            MakeResult("song_1", "UnknownInstrument", ("acct_1", 100_000)),
            registeredAccountIds: null);

        await glp.DrainWritersAsync();

        // The result should be silently dropped — no entries persisted
        Assert.False(glp.GetEntryCounts().ContainsKey("UnknownInstrument"));
    }

    [Fact]
    public void Constructor_creates_directory_if_not_exists()
    {
        var subDir = Path.Combine(_dataDir, "subdir_" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(subDir));

        var loggerFactory = new NullLoggerFactory();
        using var glp = new GlobalLeaderboardPersistence(
            subDir,
            _metaFixture.Db,
            loggerFactory,
            NullLogger<GlobalLeaderboardPersistence>.Instance);

        Assert.True(Directory.Exists(subDir));
    }

    // ═══ GetLeaderboardWithCount ════════════════════════════════

    [Fact]
    public void GetOrCreateInstrumentDb_CreatesNewDb_ForUnknownInstrument()
    {
        using var glp = CreatePersistence();
        // "Solo_Keys" doesn't exist in AllInstruments — triggers the create-new path
        var db = glp.GetOrCreateInstrumentDb("Solo_Keys");
        Assert.NotNull(db);
        Assert.Equal("Solo_Keys", db.Instrument);
        // Verify it's queryable
        Assert.Equal(0, db.GetTotalEntryCount());
    }

    [Fact]
    public void GetLeaderboardWithCount_returns_entries_and_count()
    {
        using var glp = CreatePersistence();
        var db = glp.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("song_1", new[]
        {
            new LeaderboardEntry { AccountId = "a1", Score = 300 },
            new LeaderboardEntry { AccountId = "a2", Score = 200 },
        });

        var result = glp.GetLeaderboardWithCount("song_1", "Solo_Guitar", top: 10);
        Assert.NotNull(result);
        var (entries, total) = result.Value;
        Assert.Equal(2, entries.Count);
        Assert.Equal(2, total);
    }

    [Fact]
    public void GetLeaderboardWithCount_unknown_instrument_returns_null()
    {
        using var glp = CreatePersistence();
        var result = glp.GetLeaderboardWithCount("song_1", "UnknownInst");
        Assert.Null(result);
    }

    // ═══ RecomputeAllRanks ══════════════════════════════════════

    [Fact]
    public void RecomputeAllRanks_updates_all_instruments()
    {
        using var glp = CreatePersistence();

        var guitarDb = glp.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song_1", new[]
        {
            new LeaderboardEntry { AccountId = "a1", Score = 300 },
            new LeaderboardEntry { AccountId = "a2", Score = 200 },
        });

        var bassDb = glp.GetOrCreateInstrumentDb("Solo_Bass");
        bassDb.UpsertEntries("song_1", new[]
        {
            new LeaderboardEntry { AccountId = "a3", Score = 500 },
        });

        var total = glp.RecomputeAllRanks();
        Assert.Equal(3, total);

        Assert.Equal(1, guitarDb.GetEntry("song_1", "a1")!.Rank);
        Assert.Equal(2, guitarDb.GetEntry("song_1", "a2")!.Rank);
        Assert.Equal(1, bassDb.GetEntry("song_1", "a3")!.Rank);
    }

    // ═══ GetSongCountsForInstruments ════════════════════════════

    [Fact]
    public void GetSongCountsForInstruments_returns_all_song_counts()
    {
        using var glp = CreatePersistence();
        var guitarDb = glp.GetOrCreateInstrumentDb("Solo_Guitar");
        guitarDb.UpsertEntries("song_1", new[]
        {
            new LeaderboardEntry { AccountId = "a1", Score = 300 },
            new LeaderboardEntry { AccountId = "a2", Score = 200 },
        });
        var bassDb = glp.GetOrCreateInstrumentDb("Solo_Bass");
        bassDb.UpsertEntries("song_1", new[]
        {
            new LeaderboardEntry { AccountId = "a3", Score = 500 },
        });

        var counts = glp.GetSongCountsForInstruments();
        Assert.Equal(2, counts[("song_1", "Solo_Guitar")]);
        Assert.Equal(1, counts[("song_1", "Solo_Bass")]);
    }

    // ═══ GetLeaderboardCount (GLP layer) ════════════════════════

    [Fact]
    public void GetLeaderboardCount_returns_count_for_known_instrument()
    {
        using var glp = CreatePersistence();
        var db = glp.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("song_1", new[]
        {
            new LeaderboardEntry { AccountId = "a1", Score = 100 },
            new LeaderboardEntry { AccountId = "a2", Score = 200 },
        });

        var count = glp.GetLeaderboardCount("song_1", "Solo_Guitar");
        Assert.Equal(2, count);
    }

    [Fact]
    public void GetLeaderboardCount_returns_null_for_unknown_instrument()
    {
        using var glp = CreatePersistence();
        var count = glp.GetLeaderboardCount("song_1", "Unknown_Instrument");
        Assert.Null(count);
    }

    // ═══ GetLeaderboard (GLP layer) ═════════════════════════════

    [Fact]
    public void GetLeaderboard_GlpLayer_returns_null_for_unknown_instrument()
    {
        using var glp = CreatePersistence();
        var entries = glp.GetLeaderboard("song_1", "Unknown_Instrument");
        Assert.Null(entries);
    }

    // ═══ PruneAllInstruments ════════════════════════════════

    [Fact]
    public void PruneAllInstruments_RemovesExcessEntries()
    {
        using var glp = CreatePersistence();
        glp.Initialize();

        var db = glp.GetOrCreateInstrumentDb("Solo_Guitar");
        var entries = Enumerable.Range(0, 50).Select(i =>
            new LeaderboardEntry
            {
                AccountId = $"p_{i}", Score = 5000 - i * 10,
                Accuracy = 95, Stars = 5, Season = 3,
            }).ToList();
        db.UpsertEntries("song1", entries);

        var preserve = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "p_40" };
        var deleted = glp.PruneAllInstruments(10, preserve);

        Assert.Equal(39, deleted); // 50 - 10 (top) - 1 (preserved) = 39
        Assert.Equal(11, db.GetLeaderboardCount("song1")); // 10 + 1 preserved
    }

    [Fact]
    public void PruneAllInstruments_ZeroMax_ReturnsZero()
    {
        using var glp = CreatePersistence();
        glp.Initialize();

        var deleted = glp.PruneAllInstruments(0, new HashSet<string>());
        Assert.Equal(0, deleted);
    }

    [Fact]
    public void IsReady_AfterInitialize_ReturnsTrue()
    {
        using var glp = CreatePersistence();
        glp.Initialize();

        Assert.True(glp.IsReady());
    }

    [Fact]
    public void IsReady_WithoutDbs_ReturnsFalse()
    {
        // Create but don't initialize — no instrument DBs exist
        var tempDir = Path.Combine(Path.GetTempPath(), $"fst_ready_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var metaFixture = new InMemoryMetaDatabase();
            var loggerFactory = new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory();
            var glp = new GlobalLeaderboardPersistence(
                tempDir, metaFixture.Db, loggerFactory,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GlobalLeaderboardPersistence>.Instance);
            // Don't call Initialize — no DBs
            Assert.False(glp.IsReady());
            glp.Dispose();
            metaFixture.Dispose();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
