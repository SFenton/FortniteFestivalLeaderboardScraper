using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

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

    private GlobalLeaderboardPersistence CreatePersistence(FeatureOptions? features = null)
    {
        var loggerFactory = new NullLoggerFactory();
        var glp = new GlobalLeaderboardPersistence(
            _metaFixture.Db,
            loggerFactory,
            NullLogger<GlobalLeaderboardPersistence>.Instance,
            _metaFixture.DataSource,
            Options.Create(features ?? new FeatureOptions()));
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

    private int GetSnapshotRowCount(long snapshotId, string songId, string instrument)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM leaderboard_entries_snapshot WHERE snapshot_id = @snapshotId AND song_id = @songId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int GetLiveRowCount(string songId, string instrument)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM leaderboard_entries WHERE song_id = @songId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task CleanupActiveScrapeWritersAsync_DisposesActiveDiskSpool()
    {
        using var glp = CreatePersistence();
        var spoolRoot = Path.Combine(_dataDir, "spool");
        var spool = glp.StartSpoolWriter(1001, spoolRoot);
        spool.Enqueue("song-a", "Solo_Guitar", new[]
        {
            new LeaderboardEntry
            {
                AccountId = "account-a",
                Score = 12345,
                Accuracy = 95,
                Stars = 5,
                Season = 34,
                Difficulty = 3,
                Percentile = 90,
                Rank = 1,
                Source = "scrape",
            },
        });
        var spoolDir = spool.SpoolDirectory;
        Assert.True(Directory.Exists(spoolDir));

        await glp.CleanupActiveScrapeWritersAsync();
        await glp.CleanupActiveScrapeWritersAsync();

        Assert.False(Directory.Exists(spoolDir));
    }

    private (long ActiveSnapshotId, long ScrapeId, bool IsFinalized)? GetSnapshotState(string songId, string instrument)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT active_snapshot_id, scrape_id, is_finalized FROM leaderboard_snapshot_state WHERE song_id = @songId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetBoolean(2));
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

    [Fact]
    public async Task FlushSpoolAsync_shadow_writes_snapshot_rows_for_scrape_run()
    {
        using var glp = CreatePersistence();

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
                EndTime = "2025-01-15T12:00:00Z",
            }
        ]);

        await glp.FlushSpoolAsync();

        Assert.Equal(1, GetSnapshotRowCount(42, "song_1", "Solo_Guitar"));
    }

    [Fact]
    public async Task FlushSpoolAsync_default_maintains_legacy_live_rows()
    {
        using var glp = CreatePersistence();

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            }
        ]);

        await glp.FlushSpoolAsync();

        Assert.Equal(1, GetSnapshotRowCount(42, "song_1", "Solo_Guitar"));
        Assert.Equal(1, GetLiveRowCount("song_1", "Solo_Guitar"));
    }

    [Fact]
    public async Task FlushSpoolAsync_when_legacy_live_writes_disabled_writes_snapshot_only()
    {
        using var glp = CreatePersistence(new FeatureOptions { WriteLegacyLiveLeaderboardDuringScrape = false });

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            }
        ]);

        await glp.FlushSpoolAsync();
        var activated = glp.FinalizeShadowSnapshots(42);
        var currentState = glp.GetCurrentStateLeaderboard("song_1", "Solo_Guitar", top: 10);

        Assert.Equal(1, GetSnapshotRowCount(42, "song_1", "Solo_Guitar"));
        Assert.Equal(0, GetLiveRowCount("song_1", "Solo_Guitar"));
        Assert.Equal(1, activated);
        var entry = Assert.Single(currentState!);
        Assert.Equal("acct_1", entry.AccountId);
        Assert.Equal(100_000, entry.Score);
    }

    [Fact]
    public async Task OnlineSoloWriter_when_legacy_live_writes_disabled_writes_snapshot_only()
    {
        using var glp = CreatePersistence(new FeatureOptions { WriteLegacyLiveLeaderboardDuringScrape = false });

        glp.StartOnlineSoloWriter(42, channelCapacity: 2, maxBatchPages: 2, writerCount: 1);
        await glp.EnqueueOnlineSoloPageAsync("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
            }
        ]);
        await glp.DrainOnlineSoloWriterAsync();
        var activated = glp.FinalizeShadowSnapshots(42);
        var currentState = glp.GetCurrentStateLeaderboard("song_1", "Solo_Guitar", top: 10);

        Assert.Equal(1, GetSnapshotRowCount(42, "song_1", "Solo_Guitar"));
        Assert.Equal(0, GetLiveRowCount("song_1", "Solo_Guitar"));
        Assert.Equal(1, activated);
        var entry = Assert.Single(currentState!);
        Assert.Equal("acct_1", entry.AccountId);
        Assert.Equal(100_000, entry.Score);
    }

    [Fact]
    public async Task FinalizeShadowSnapshots_marks_snapshot_state_for_scrape_run()
    {
        using var glp = CreatePersistence();

        glp.StartSpoolWriter(42, _dataDir);
        glp.EnqueueSpoolPage("song_1", "Solo_Guitar",
        [
            new LeaderboardEntry
            {
                AccountId = "acct_1",
                Score = 100_000,
                Accuracy = 95,
                Stars = 5,
                Season = 3,
                Difficulty = 3,
                Percentile = 99.0,
                Rank = 1,
                ApiRank = 1,
                Source = "scrape",
                EndTime = "2025-01-15T12:00:00Z",
            }
        ]);

        await glp.FlushSpoolAsync();
        var activated = glp.FinalizeShadowSnapshots(42);

        Assert.Equal(1, activated);
        var state = GetSnapshotState("song_1", "Solo_Guitar");
        Assert.NotNull(state);
        Assert.Equal(42, state?.ActiveSnapshotId);
        Assert.Equal(42, state?.ScrapeId);
        Assert.True(state?.IsFinalized);
    }

    [Fact]
    public void FinalizeShadowSnapshots_activates_expected_pairs_without_snapshot_rows()
    {
        using var glp = CreatePersistence();

        var activated = glp.FinalizeShadowSnapshots(
            42,
            expectedPairs: [("song_empty", "Solo_Guitar")]);

        Assert.Equal(1, activated);
        Assert.Equal(0, GetSnapshotRowCount(42, "song_empty", "Solo_Guitar"));
        var state = GetSnapshotState("song_empty", "Solo_Guitar");
        Assert.NotNull(state);
        Assert.Equal(42, state?.ActiveSnapshotId);
        Assert.Equal(42, state?.ScrapeId);
        Assert.True(state?.IsFinalized);
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
        Assert.Single(pr.DirtyRivalSongs);
        Assert.Equal(RivalsDirtyReason.SelfScoreChange, pr.DirtyRivalSongs[0].DirtyReason);
        Assert.Equal("song_1", pr.DirtyRivalSongs[0].SongId);

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

    [Fact]
    public void PersistResult_marks_registered_user_self_rank_dirty_when_untracked_player_moves_nearby()
    {
        using var glp = CreatePersistence();
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_tracked" };

        var initial = new GlobalLeaderboardResult
        {
            SongId = "song_1",
            Instrument = "Solo_Guitar",
            Entries = new List<LeaderboardEntry>
            {
                new() { AccountId = "acct_1", Score = 120_000, Rank = 1, Accuracy = 99, IsFullCombo = true, Stars = 6, Season = 3, Percentile = 100.0 },
                new() { AccountId = "acct_tracked", Score = 110_000, Rank = 2, Accuracy = 98, IsFullCombo = true, Stars = 6, Season = 3, Percentile = 99.5 },
                new() { AccountId = "acct_3", Score = 100_000, Rank = 3, Accuracy = 97, IsFullCombo = false, Stars = 5, Season = 3, Percentile = 99.0 },
                new() { AccountId = "acct_untracked", Score = 70_000, Rank = 4, Accuracy = 95, IsFullCombo = false, Stars = 5, Season = 3, Percentile = 95.0 },
            },
        };

        var moved = new GlobalLeaderboardResult
        {
            SongId = "song_1",
            Instrument = "Solo_Guitar",
            Entries = new List<LeaderboardEntry>
            {
                new() { AccountId = "acct_1", Score = 120_000, Rank = 1, Accuracy = 99, IsFullCombo = true, Stars = 6, Season = 3, Percentile = 100.0 },
                new() { AccountId = "acct_untracked", Score = 115_000, Rank = 2, Accuracy = 98, IsFullCombo = true, Stars = 6, Season = 3, Percentile = 99.8 },
                new() { AccountId = "acct_tracked", Score = 110_000, Rank = 3, Accuracy = 98, IsFullCombo = true, Stars = 6, Season = 3, Percentile = 99.5 },
                new() { AccountId = "acct_3", Score = 100_000, Rank = 4, Accuracy = 97, IsFullCombo = false, Stars = 5, Season = 3, Percentile = 99.0 },
            },
        };

        glp.PersistResult(initial, registered);
        var pr = glp.PersistResult(moved, registered);

        Assert.Equal(0, pr.ScoreChangesDetected);
        Assert.Contains("acct_tracked", pr.ChangedAccountIds);
        Assert.Single(pr.DirtyRivalSongs);
        Assert.Equal("acct_tracked", pr.DirtyRivalSongs[0].AccountId);
        Assert.Equal(RivalsDirtyReason.SelfRankChange, pr.DirtyRivalSongs[0].DirtyReason);
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

    [Fact]
    public void PipelineAggregates_dirty_rival_songs_deduplicates_by_song_key()
    {
        var agg = new GlobalLeaderboardPersistence.PipelineAggregates();

        agg.AddDirtyRivalSongs(
        [
            new RivalDirtySongRow
            {
                AccountId = "acct_1",
                Instrument = "Solo_Guitar",
                SongId = "song_1",
                DirtyReason = RivalsDirtyReason.SelfScoreChange,
                DetectedAt = "2026-01-01T00:00:00Z",
            },
        ]);
        agg.AddDirtyRivalSongs(
        [
            new RivalDirtySongRow
            {
                AccountId = "acct_1",
                Instrument = "Solo_Guitar",
                SongId = "song_1",
                DirtyReason = RivalsDirtyReason.NeighborWindowChange,
                DetectedAt = "2026-01-01T00:00:01Z",
            },
            new RivalDirtySongRow
            {
                AccountId = "acct_2",
                Instrument = "Solo_Bass",
                SongId = "song_2",
                DirtyReason = RivalsDirtyReason.SelfRankChange,
                DetectedAt = "2026-01-01T00:00:02Z",
            },
        ]);

        Assert.Equal(2, agg.DirtyRivalSongs.Count);
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
    public void Constructor_creates_persistence_without_directory()
    {
        var loggerFactory = new NullLoggerFactory();
        using var glp = new GlobalLeaderboardPersistence(
            _metaFixture.Db,
            loggerFactory,
            NullLogger<GlobalLeaderboardPersistence>.Instance,
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions()));
    }

    // ═══ GetLeaderboardWithCount ════════════════════════════════

    [Fact]
    public void GetOrCreateInstrumentDb_Throws_ForUnknownInstrument()
    {
        using var glp = CreatePersistence();
        var ex = Assert.Throws<ArgumentException>(() => glp.GetOrCreateInstrumentDb("Solo_Keys"));
        Assert.Contains("Unknown instrument key", ex.Message);
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

    [Fact]
    public void RecomputeAllRanks_skips_unchanged_ranks()
    {
        using var glp = CreatePersistence();
        var db = glp.GetOrCreateInstrumentDb("Solo_Guitar");
        db.UpsertEntries("song_1", new[]
        {
            new LeaderboardEntry { AccountId = "a1", Score = 300 },
            new LeaderboardEntry { AccountId = "a2", Score = 200 },
        });

        // First call assigns ranks from 0 → correct values
        var first = glp.RecomputeAllRanks();
        Assert.Equal(2, first);

        // Second call with no changes should skip all rows (ranks already correct)
        var second = glp.RecomputeAllRanks();
        Assert.Equal(0, second);
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
    public void PruneAllInstruments_WhenLegacyLiveWritesDisabled_ReturnsZeroAndLeavesRows()
    {
        using var glp = CreatePersistence(new FeatureOptions { WriteLegacyLiveLeaderboardDuringScrape = false });
        glp.Initialize();

        var db = glp.GetOrCreateInstrumentDb("Solo_Guitar");
        var entries = Enumerable.Range(0, 50).Select(i =>
            new LeaderboardEntry
            {
                AccountId = $"p_{i}", Score = 5000 - i * 10,
                Accuracy = 95, Stars = 5, Season = 3,
            }).ToList();
        db.UpsertEntries("song1", entries);

        var deleted = glp.PruneAllInstruments(10, new HashSet<string>());

        Assert.Equal(0, deleted);
        Assert.Equal(50, db.GetLeaderboardCount("song1"));
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
                metaFixture.Db, loggerFactory,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GlobalLeaderboardPersistence>.Instance,
                metaFixture.DataSource,
                Options.Create(new FeatureOptions()));
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

    // ═══ CheckpointAll ══════════════════════════════════════════

    [Fact]
    public void CheckpointAll_succeeds_after_writes()
    {
        using var glp = CreatePersistence();

        glp.PersistResult(MakeResult("song_1", "Solo_Guitar",
            ("acct_1", 100_000), ("acct_2", 90_000)));

        // Should not throw — checkpoints all instrument DBs + meta DB
        glp.CheckpointAll();

        // Data should still be readable
        var db = glp.GetOrCreateInstrumentDb("Solo_Guitar");
        Assert.Equal(2, db.GetLeaderboardCount("song_1"));
    }

    [Fact]
    public void CheckpointAll_succeeds_on_empty_databases()
    {
        using var glp = CreatePersistence();

        // Should not throw even with no data written
        glp.CheckpointAll();
    }

    // ═══ Deferred Account IDs ═══════════════════════════════════

    [Fact]
    public void PipelineAggregates_deferred_account_ids_accumulates_and_deduplicates()
    {
        var agg = new GlobalLeaderboardPersistence.PipelineAggregates();

        agg.AddDeferredAccountIds(["acct_1", "acct_2", "acct_3"]);
        agg.AddDeferredAccountIds(["acct_2", "acct_3", "acct_4"]);

        Assert.Equal(4, agg.DeferredAccountIds.Count);
    }

    [Fact]
    public void PipelineAggregates_deferred_account_ids_thread_safe()
    {
        var agg = new GlobalLeaderboardPersistence.PipelineAggregates();

        Parallel.For(0, 1000, i =>
        {
            agg.AddDeferredAccountIds([$"acct_{i % 100}"]);
        });

        Assert.Equal(100, agg.DeferredAccountIds.Count);
    }

    [Fact]
    public async Task PersistResult_defers_account_ids_when_writers_active()
    {
        using var glp = CreatePersistence();
        var agg = glp.StartWriters();

        await glp.EnqueueResultAsync(
            MakeResult("song_1", "Solo_Guitar", ("acct_1", 100_000), ("acct_2", 90_000)),
            registeredAccountIds: null);

        await glp.DrainWritersAsync();

        // Account IDs should be accumulated in aggregates, not written yet
        Assert.True(agg.DeferredAccountIds.Count >= 2);
        Assert.Contains("acct_1", agg.DeferredAccountIds);
        Assert.Contains("acct_2", agg.DeferredAccountIds);
    }

    [Fact]
    public async Task FlushDeferredAccountIds_writes_to_meta_db()
    {
        using var glp = CreatePersistence();
        var agg = glp.StartWriters();

        await glp.EnqueueResultAsync(
            MakeResult("song_1", "Solo_Guitar", ("acct_flush_1", 100_000), ("acct_flush_2", 90_000)),
            registeredAccountIds: null);

        await glp.DrainWritersAsync();

        // Before flush — account IDs should be in deferred set
        Assert.True(agg.DeferredAccountIds.Count >= 2);

        // Flush
        var inserted = glp.FlushDeferredAccountIds();

        // After flush — account IDs should be in meta DB
        var unresolved = _metaFixture.Db.GetUnresolvedAccountIds();
        Assert.Contains("acct_flush_1", unresolved);
        Assert.Contains("acct_flush_2", unresolved);
        Assert.True(inserted >= 2);
    }

    [Fact]
    public void PersistResult_writes_account_ids_immediately_without_writers()
    {
        using var glp = CreatePersistence();

        // PersistResult without StartWriters — should write immediately
        glp.PersistResult(MakeResult("song_1", "Solo_Guitar", ("acct_direct", 100_000)));

        var unresolved = _metaFixture.Db.GetUnresolvedAccountIds();
        Assert.Contains("acct_direct", unresolved);
    }

    [Fact]
    public async Task FlushDeferredAccountIds_catches_database_exception_and_returns_zero()
    {
        // Arrange: mock IMetaDatabase that throws on InsertAccountIds
        var mockMeta = Substitute.For<IMetaDatabase>();
        mockMeta.When(m => m.InsertAccountIds(Arg.Any<IEnumerable<string>>()))
                .Do(_ => throw new InvalidOperationException("Simulated DB timeout"));

        using var glp = new GlobalLeaderboardPersistence(
            mockMeta, new NullLoggerFactory(),
            NullLogger<GlobalLeaderboardPersistence>.Instance,
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions()));
        glp.Initialize();
        var agg = glp.StartWriters();

        await glp.EnqueueResultAsync(
            MakeResult("song_1", "Solo_Guitar", ("acct_err_1", 100_000)),
            registeredAccountIds: null);
        await glp.DrainWritersAsync();

        Assert.True(agg.DeferredAccountIds.Count >= 1);

        // Act: flush should catch the exception and return 0
        var inserted = glp.FlushDeferredAccountIds();

        // Assert: no exception thrown, returns 0
        Assert.Equal(0, inserted);
    }
}
