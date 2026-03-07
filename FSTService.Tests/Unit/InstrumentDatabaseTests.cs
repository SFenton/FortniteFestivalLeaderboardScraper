using FSTService.Tests.Helpers;
using FSTService.Scraping;
using FSTService.Persistence;
using Microsoft.Extensions.Logging;

namespace FSTService.Tests.Unit;

public sealed class InstrumentDatabaseTests : IDisposable
{
    private readonly TempInstrumentDatabase _fixture = new();
    private Persistence.InstrumentDatabase Db => _fixture.Db;

    public void Dispose() => _fixture.Dispose();

    private static LeaderboardEntry MakeEntry(string accountId, int score,
        int accuracy = 95, bool fc = false, int stars = 5, int season = 3) =>
        new()
        {
            AccountId = accountId,
            Score = score,
            Accuracy = accuracy,
            IsFullCombo = fc,
            Stars = stars,
            Season = season,
            Percentile = 99.0,
            EndTime = "2025-01-15T12:00:00Z",
        };

    // ═══ UpsertEntries ══════════════════════════════════════════

    [Fact]
    public void UpsertEntries_inserts_new_rows()
    {
        var entries = new List<LeaderboardEntry>
        {
            MakeEntry("acct_1", 100_000),
            MakeEntry("acct_2", 90_000),
        };

        var affected = Db.UpsertEntries("song_1", entries);
        Assert.Equal(2, affected);
        Assert.Equal(2, Db.GetTotalEntryCount());
    }

    [Fact]
    public void UpsertEntries_updates_changed_score()
    {
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 80_000)]);
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000)]);

        var entry = Db.GetEntry("song_1", "acct_1");
        Assert.NotNull(entry);
        Assert.Equal(100_000, entry.Score);
        Assert.Equal(1, Db.GetTotalEntryCount());
    }

    [Fact]
    public void UpsertEntries_skips_identical_score()
    {
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000)]);
        var affected = Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000)]);
        Assert.Equal(0, affected);
    }

    [Fact]
    public void UpsertEntries_returns_zero_for_empty_list()
    {
        var affected = Db.UpsertEntries("song_1", []);
        Assert.Equal(0, affected);
    }

    // ═══ GetEntry ═══════════════════════════════════════════════

    [Fact]
    public void GetEntry_returns_entry_when_exists()
    {
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000, accuracy: 98, fc: true, stars: 6)]);

        var entry = Db.GetEntry("song_1", "acct_1");
        Assert.NotNull(entry);
        Assert.Equal("acct_1", entry.AccountId);
        Assert.Equal(100_000, entry.Score);
        Assert.Equal(98, entry.Accuracy);
        Assert.True(entry.IsFullCombo);
        Assert.Equal(6, entry.Stars);
        Assert.Equal(3, entry.Season);
    }

    [Fact]
    public void GetEntry_returns_null_when_not_found()
    {
        var entry = Db.GetEntry("song_1", "nobody");
        Assert.Null(entry);
    }

    // ═══ GetLeaderboard ═════════════════════════════════════════

    [Fact]
    public void GetLeaderboard_orders_by_score_descending()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("acct_low",  50_000),
            MakeEntry("acct_mid",  75_000),
            MakeEntry("acct_high", 100_000),
        ]);

        var board = Db.GetLeaderboard("song_1");
        Assert.Equal(3, board.Count);
        Assert.Equal("acct_high", board[0].AccountId);
        Assert.Equal("acct_mid", board[1].AccountId);
        Assert.Equal("acct_low", board[2].AccountId);
    }

    [Fact]
    public void GetLeaderboard_respects_top_limit()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("a", 10_000),
            MakeEntry("b", 20_000),
            MakeEntry("c", 30_000),
        ]);

        var board = Db.GetLeaderboard("song_1", top: 2);
        Assert.Equal(2, board.Count);
        Assert.Equal("c", board[0].AccountId);
        Assert.Equal("b", board[1].AccountId);
    }

    [Fact]
    public void GetLeaderboard_returns_empty_for_unknown_song()
    {
        var board = Db.GetLeaderboard("nonexistent");
        Assert.Empty(board);
    }

    // ═══ GetAllSongCounts ═══════════════════════════════════════

    [Fact]
    public void GetAllSongCounts_returns_counts_per_song()
    {
        Db.UpsertEntries("song_A", [MakeEntry("acct_1", 100_000), MakeEntry("acct_2", 90_000)]);
        Db.UpsertEntries("song_B", [MakeEntry("acct_1", 80_000)]);

        var counts = Db.GetAllSongCounts();
        Assert.Equal(2, counts.Count);
        Assert.Equal(2, counts["song_A"]);
        Assert.Equal(1, counts["song_B"]);
    }

    [Fact]
    public void GetAllSongCounts_returns_empty_when_no_entries()
    {
        var counts = Db.GetAllSongCounts();
        Assert.Empty(counts);
    }

    // ═══ GetPlayerScores ════════════════════════════════════════

    [Fact]
    public void GetPlayerScores_returns_all_songs_for_account()
    {
        Db.UpsertEntries("song_A", [MakeEntry("acct_1", 100_000)]);
        Db.UpsertEntries("song_B", [MakeEntry("acct_1", 90_000)]);
        Db.UpsertEntries("song_A", [MakeEntry("acct_2", 80_000)]);

        var scores = Db.GetPlayerScores("acct_1");
        Assert.Equal(2, scores.Count);
        Assert.All(scores, s => Assert.Equal("Solo_Guitar", s.Instrument));
    }

    [Fact]
    public void GetPlayerScores_returns_empty_for_unknown_account()
    {
        var scores = Db.GetPlayerScores("nobody");
        Assert.Empty(scores);
    }

    [Fact]
    public void GetPlayerScores_filters_by_songId()
    {
        Db.UpsertEntries("song_A", [MakeEntry("acct_1", 100_000)]);
        Db.UpsertEntries("song_B", [MakeEntry("acct_1", 90_000)]);

        var scores = Db.GetPlayerScores("acct_1", songId: "song_A");
        Assert.Single(scores);
        Assert.Equal("song_A", scores[0].SongId);
    }

    [Fact]
    public void GetPlayerScores_songId_filter_returns_empty_when_no_match()
    {
        Db.UpsertEntries("song_A", [MakeEntry("acct_1", 100_000)]);

        var scores = Db.GetPlayerScores("acct_1", songId: "song_nonexistent");
        Assert.Empty(scores);
    }

    // ═══ GetAnySongId ═══════════════════════════════════════════

    [Fact]
    public void GetAnySongId_returns_null_when_empty()
    {
        var songId = Db.GetAnySongId();
        Assert.Null(songId);
    }

    [Fact]
    public void GetAnySongId_returns_song_when_data_exists()
    {
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000)]);
        var songId = Db.GetAnySongId();
        Assert.Equal("song_1", songId);
    }

    // ═══ GetTotalEntryCount ═════════════════════════════════════

    [Fact]
    public void GetTotalEntryCount_counts_across_songs()
    {
        Db.UpsertEntries("song_A", [MakeEntry("acct_1", 100_000)]);
        Db.UpsertEntries("song_B", [MakeEntry("acct_1", 90_000), MakeEntry("acct_2", 80_000)]);

        Assert.Equal(3, Db.GetTotalEntryCount());
    }

    // ═══ EndTime ════════════════════════════════════════════════

    [Fact]
    public void Upsert_preserves_null_EndTime()
    {
        var entry = MakeEntry("acct_1", 100_000);
        entry.EndTime = null;
        Db.UpsertEntries("song_1", [entry]);

        var result = Db.GetEntry("song_1", "acct_1");
        Assert.NotNull(result);
        Assert.Null(result.EndTime);
    }

    // ═══ GetMinSeason ═══════════════════════════════════════════

    [Fact]
    public void GetMinSeason_returns_null_for_empty_db()
    {
        Assert.Null(Db.GetMinSeason("song_1"));
    }

    [Fact]
    public void GetMinSeason_returns_null_when_all_zero()
    {
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000, season: 0)]);
        Assert.Null(Db.GetMinSeason("song_1"));
    }

    [Fact]
    public void GetMinSeason_returns_min_positive_season()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("acct_1", 100_000, season: 3),
            MakeEntry("acct_2", 90_000, season: 5),
            MakeEntry("acct_3", 80_000, season: 1),
        ]);

        Assert.Equal(1, Db.GetMinSeason("song_1"));
    }

    [Fact]
    public void GetMinSeason_ignores_other_songs()
    {
        Db.UpsertEntries("song_A", [MakeEntry("acct_1", 100_000, season: 2)]);
        Db.UpsertEntries("song_B", [MakeEntry("acct_2", 90_000, season: 1)]);

        Assert.Equal(2, Db.GetMinSeason("song_A"));
    }

    // ═══ GetMaxSeason ═══════════════════════════════════════════

    [Fact]
    public void GetMaxSeason_returns_null_for_empty_db()
    {
        Assert.Null(Db.GetMaxSeason());
    }

    [Fact]
    public void GetMaxSeason_returns_null_when_all_zero()
    {
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000, season: 0)]);
        Assert.Null(Db.GetMaxSeason());
    }

    [Fact]
    public void GetMaxSeason_returns_max_across_all_songs()
    {
        Db.UpsertEntries("song_A", [MakeEntry("acct_1", 100_000, season: 3)]);
        Db.UpsertEntries("song_B", [MakeEntry("acct_2", 90_000, season: 7)]);
        Db.UpsertEntries("song_C", [MakeEntry("acct_3", 80_000, season: 5)]);

        Assert.Equal(7, Db.GetMaxSeason());
    }

    // ═══ GetPlayerRankings ══════════════════════════════════════

    [Fact]
    public void GetPlayerRankings_returns_empty_for_unknown_account()
    {
        var rankings = Db.GetPlayerRankings("nobody");
        Assert.Empty(rankings);
    }

    [Fact]
    public void GetPlayerRankings_computes_rank_and_total()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("acct_top",  100_000),
            MakeEntry("acct_mid",   75_000),
            MakeEntry("acct_low",   50_000),
        ]);

        var rankings = Db.GetPlayerRankings("acct_mid");
        Assert.Single(rankings);
        Assert.True(rankings.ContainsKey("song_1"));
        var (rank, total) = rankings["song_1"];
        Assert.Equal(2, rank);  // one score above 75k
        Assert.Equal(3, total);
    }

    [Fact]
    public void GetPlayerRankings_covers_multiple_songs()
    {
        Db.UpsertEntries("song_A",
        [
            MakeEntry("acct_1", 100_000),
            MakeEntry("acct_2",  80_000),
        ]);
        Db.UpsertEntries("song_B",
        [
            MakeEntry("acct_1",  60_000),
            MakeEntry("acct_2",  90_000),
            MakeEntry("acct_3",  70_000),
        ]);

        var rankings = Db.GetPlayerRankings("acct_2");
        Assert.Equal(2, rankings.Count);

        // Song A: acct_2 has 80k, 1 person above → rank 2 of 2
        Assert.Equal(2, rankings["song_A"].Rank);
        Assert.Equal(2, rankings["song_A"].Total);

        // Song B: acct_2 has 90k, 0 above → rank 1 of 3
        Assert.Equal(1, rankings["song_B"].Rank);
        Assert.Equal(3, rankings["song_B"].Total);
    }

    [Fact]
    public void GetPlayerRankings_filters_by_songId()
    {
        Db.UpsertEntries("song_A",
        [
            MakeEntry("acct_1", 100_000),
            MakeEntry("acct_2",  80_000),
        ]);
        Db.UpsertEntries("song_B",
        [
            MakeEntry("acct_1",  60_000),
            MakeEntry("acct_2",  90_000),
        ]);

        var rankings = Db.GetPlayerRankings("acct_2", songId: "song_B");
        Assert.Single(rankings);
        Assert.True(rankings.ContainsKey("song_B"));
        Assert.Equal(1, rankings["song_B"].Rank);
        Assert.Equal(2, rankings["song_B"].Total);
    }

    [Fact]
    public void GetPlayerRankings_songId_filter_returns_empty_when_no_match()
    {
        Db.UpsertEntries("song_A", [MakeEntry("acct_1", 100_000)]);

        var rankings = Db.GetPlayerRankings("acct_1", songId: "song_nonexistent");
        Assert.Empty(rankings);
    }

    // ═══ Upsert edge cases ══════════════════════════════════════

    [Fact]
    public void Upsert_updates_rank_when_new_rank_is_positive()
    {
        var entry = MakeEntry("acct_1", 100_000);
        entry.Rank = 0;
        Db.UpsertEntries("song_1", [entry]);

        var updated = MakeEntry("acct_1", 100_000);
        updated.Rank = 5;
        Db.UpsertEntries("song_1", [updated]);

        var result = Db.GetEntry("song_1", "acct_1");
        Assert.NotNull(result);
        Assert.Equal(5, result.Rank);
    }

    [Fact]
    public void Upsert_updates_percentile_when_old_was_zero()
    {
        var entry = MakeEntry("acct_1", 100_000);
        entry.Percentile = 0;
        Db.UpsertEntries("song_1", [entry]);

        var updated = MakeEntry("acct_1", 100_000);
        updated.Percentile = 95.5;
        Db.UpsertEntries("song_1", [updated]);

        var result = Db.GetEntry("song_1", "acct_1");
        Assert.NotNull(result);
        Assert.Equal(95.5, result.Percentile, 0.01);
    }

    // ─── Constructor directory creation ─────────────────────

    [Fact]
    public void Constructor_creates_directory_if_not_exists()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fst_inst_dir_{Guid.NewGuid():N}", "sub");
        var dbPath = Path.Combine(dir, "test.db");
        try
        {
            var logger = NSubstitute.Substitute.For<ILogger<InstrumentDatabase>>();
            using var db = new InstrumentDatabase("Solo_Guitar", dbPath, logger);
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(dir)!, true); } catch { }
        }
    }

    // ─── Dispose ────────────────────────────────────────────

    [Fact]
    public void Dispose_cleans_up_persistent_connection()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fst_inst_dispose_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(dir, "test.db");
        Directory.CreateDirectory(dir);
        try
        {
            var logger = NSubstitute.Substitute.For<ILogger<InstrumentDatabase>>();
            var db = new InstrumentDatabase("Solo_Guitar", dbPath, logger);
            db.EnsureSchema();
            // Force persistent connection creation by upserting entries
            var entry = MakeEntry("acct_dispose", 50000);
            db.UpsertEntries("song_dispose", [entry]);
            // Dispose should not throw
            db.Dispose();
            // Calling Dispose again should be safe
            db.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ─── GetPlayerScores ────────────────────────────────────

    [Fact]
    public void GetPlayerScores_returns_scores_for_account()
    {
        var entry1 = MakeEntry("acct_scores", 100_000);
        entry1.Accuracy = 95;
        entry1.IsFullCombo = true;
        entry1.Stars = 5;
        entry1.Percentile = 99.5;
        Db.UpsertEntries("song_1", [entry1]);

        var entry2 = MakeEntry("acct_scores", 80_000);
        Db.UpsertEntries("song_2", [entry2]);

        var scores = Db.GetPlayerScores("acct_scores");
        Assert.Equal(2, scores.Count);
        Assert.Contains(scores, s => s.SongId == "song_1" && s.Score == 100_000);
    }

    [Fact]
    public void GetPlayerScores_returns_empty_for_unknown()
    {
        var scores = Db.GetPlayerScores("nobody");
        Assert.Empty(scores);
    }

    // ─── Instrument property ────────────────────────────────

    [Fact]
    public void Instrument_property_returns_instrument_name()
    {
        Assert.Equal("Solo_Guitar", Db.Instrument);
    }

    // ─── MigrateDropColumn with existing column ─────────────

    [Fact]
    public void EnsureSchema_drops_PointsEarned_column_when_present()
    {
        // Create a DB with the old schema that includes the PointsEarned column
        var dir = Path.Combine(Path.GetTempPath(), $"fst_inst_migrate_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(dir, "test.db");
        Directory.CreateDirectory(dir);
        try
        {
            // Create the DB with the old schema (including PointsEarned)
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE LeaderboardEntries (
                        SongId        TEXT    NOT NULL,
                        AccountId     TEXT    NOT NULL,
                        Score         INTEGER NOT NULL,
                        Accuracy      INTEGER,
                        IsFullCombo   INTEGER,
                        Stars         INTEGER,
                        Season        INTEGER,
                        Percentile    REAL,
                        EndTime       TEXT,
                        PointsEarned  INTEGER DEFAULT 0,
                        FirstSeenAt   TEXT    NOT NULL DEFAULT '2025-01-01',
                        LastUpdatedAt TEXT    NOT NULL DEFAULT '2025-01-01',
                        PRIMARY KEY (SongId, AccountId)
                    );
                    CREATE INDEX IF NOT EXISTS IX_Song ON LeaderboardEntries (SongId, Score DESC);
                    CREATE INDEX IF NOT EXISTS IX_Account ON LeaderboardEntries (AccountId);";
                cmd.ExecuteNonQuery();

                // Insert a row to verify the column exists
                using var insert = conn.CreateCommand();
                insert.CommandText = "INSERT INTO LeaderboardEntries (SongId, AccountId, Score, PointsEarned) VALUES ('s1', 'a1', 100, 42)";
                insert.ExecuteNonQuery();
            }

            // Now create InstrumentDatabase pointing at this DB and call EnsureSchema
            var logger = NSubstitute.Substitute.For<ILogger<InstrumentDatabase>>();
            using var db = new InstrumentDatabase("Solo_Guitar", dbPath, logger);
            db.EnsureSchema();

            // Verify column was dropped
            using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            verify.Open();
            using var check = verify.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('LeaderboardEntries') WHERE name = 'PointsEarned'";
            var exists = (long)(check.ExecuteScalar() ?? 0);
            Assert.Equal(0, exists);

            // Verify data is still intact
            using var data = verify.CreateCommand();
            data.CommandText = "SELECT Score FROM LeaderboardEntries WHERE SongId = 's1'";
            Assert.Equal(100L, (long)data.ExecuteScalar()!);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
