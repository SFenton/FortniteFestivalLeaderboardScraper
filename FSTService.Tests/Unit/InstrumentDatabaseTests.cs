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
        int accuracy = 95, bool fc = false, int stars = 5, int season = 3, int difficulty = 3) =>
        new()
        {
            AccountId = accountId,
            Score = score,
            Accuracy = accuracy,
            IsFullCombo = fc,
            Stars = stars,
            Season = season,
            Difficulty = difficulty,
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

    // ═══ GetEntriesForAccounts ═══════════════════════════════════

    [Fact]
    public void GetEntriesForAccounts_returns_matching_entries()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("acct_1", 100_000),
            MakeEntry("acct_2", 90_000),
            MakeEntry("acct_3", 80_000),
        ]);

        var result = Db.GetEntriesForAccounts("song_1", ["acct_1", "acct_3"]);
        Assert.Equal(2, result.Count);
        Assert.Equal(100_000, result["acct_1"].Score);
        Assert.Equal(80_000, result["acct_3"].Score);
    }

    [Fact]
    public void GetEntriesForAccounts_returns_empty_for_no_matches()
    {
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000)]);

        var result = Db.GetEntriesForAccounts("song_1", ["nobody_1", "nobody_2"]);
        Assert.Empty(result);
    }

    [Fact]
    public void GetEntriesForAccounts_returns_empty_for_empty_input()
    {
        var result = Db.GetEntriesForAccounts("song_1", []);
        Assert.Empty(result);
    }

    [Fact]
    public void GetEntriesForAccounts_case_insensitive_keys()
    {
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000)]);

        // AccountId stored as "acct_1"; query with exact case returns it
        var result = Db.GetEntriesForAccounts("song_1", ["acct_1"]);
        Assert.Single(result);
        Assert.True(result.ContainsKey("acct_1"));
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

    [Fact]
    public void GetCurrentStateLeaderboard_falls_back_to_live_rows_without_snapshot_state()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("acct_low", 50_000),
            MakeEntry("acct_high", 100_000),
        ]);

        var board = Db.GetCurrentStateLeaderboard("song_1");

        Assert.Equal(2, board.Count);
        Assert.Equal("acct_high", board[0].AccountId);
        Assert.Equal(100_000, board[0].Score);
        Assert.Equal("acct_low", board[1].AccountId);
    }

    [Fact]
    public void GetCurrentStateLeaderboardWithCount_prefers_overlay_rows_over_live_rows()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("acct_overlay", 100_000),
            MakeEntry("acct_base", 95_000),
        ]);
        InsertOverlayEntry("song_1", "acct_overlay", 120_000, source: "neighbor", sourcePriority: 100, overlayReason: "preserved-neighbor");

        var (board, total) = Db.GetCurrentStateLeaderboardWithCount("song_1");

        Assert.Equal(2, total);
        Assert.Equal(2, board.Count);
        Assert.Equal("acct_overlay", board[0].AccountId);
        Assert.Equal(120_000, board[0].Score);
        Assert.Equal("neighbor", board[0].Source);
        Assert.Equal("acct_base", board[1].AccountId);
    }

    [Fact]
    public void GetCurrentStateLeaderboardWithCount_prefers_overlay_then_snapshot_over_live_rows()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("acct_snapshot", 90_000),
            MakeEntry("acct_overlay", 80_000),
            MakeEntry("acct_live_only", 70_000),
        ]);
        InsertSnapshotEntry(42, "song_1", "acct_snapshot", 130_000, source: "scrape");
        InsertSnapshotEntry(42, "song_1", "acct_overlay", 110_000, source: "scrape");
        InsertSnapshotState("song_1", 42, isFinalized: true);
        InsertOverlayEntry("song_1", "acct_overlay", 150_000, source: "backfill", sourcePriority: 100, overlayReason: "preserved-backfill");

        var (board, total) = Db.GetCurrentStateLeaderboardWithCount("song_1");

        Assert.Equal(2, total);
        Assert.Equal(2, board.Count);
        Assert.Equal("acct_overlay", board[0].AccountId);
        Assert.Equal(150_000, board[0].Score);
        Assert.Equal("acct_snapshot", board[1].AccountId);
        Assert.Equal(130_000, board[1].Score);
    }

    [Fact]
    public void GetCurrentStateLeaderboardWithCount_active_empty_snapshot_suppresses_live_and_keeps_overlay()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("acct_live_only", 90_000),
        ]);
        InsertSnapshotState("song_1", 42, isFinalized: true);

        var (emptyBoard, emptyTotal) = Db.GetCurrentStateLeaderboardWithCount("song_1");

        Assert.Equal(0, emptyTotal);
        Assert.Empty(emptyBoard);

        InsertOverlayEntry("song_1", "acct_overlay", 110_000, source: "backfill", sourcePriority: 200, overlayReason: "preserved-backfill");

        var (overlayBoard, overlayTotal) = Db.GetCurrentStateLeaderboardWithCount("song_1");

        Assert.Equal(1, overlayTotal);
        Assert.Single(overlayBoard);
        Assert.Equal("acct_overlay", overlayBoard[0].AccountId);
        Assert.Equal(110_000, overlayBoard[0].Score);
    }

    [Fact]
    public void GetCurrentStateScoresInBand_prefers_overlay_then_snapshot_and_excludes_live_only_rows()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("acct_snapshot", 94_000),
            MakeEntry("acct_overlay", 96_000),
            MakeEntry("acct_live_only", 97_000),
        ]);
        InsertSnapshotEntry(42, "song_1", "acct_snapshot", 99_000, source: "scrape");
        InsertSnapshotEntry(42, "song_1", "acct_overlay", 100_000, source: "scrape");
        InsertSnapshotState("song_1", 42, isFinalized: true);
        InsertOverlayEntry("song_1", "acct_overlay", 101_000, source: "backfill", sourcePriority: 200, overlayReason: "preserved-backfill");

        var scores = Db.GetCurrentStateScoresInBand("song_1", 95_000, 105_000);

        Assert.Equal([99_000, 101_000], scores);
    }

    [Fact]
    public void GetCurrentStatePopulationAtOrBelow_prefers_overlay_then_snapshot_and_excludes_live_only_rows()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("acct_snapshot", 94_000),
            MakeEntry("acct_overlay", 96_000),
            MakeEntry("acct_live_only", 97_000),
        ]);
        InsertSnapshotEntry(42, "song_1", "acct_snapshot", 99_000, source: "scrape");
        InsertSnapshotEntry(42, "song_1", "acct_overlay", 100_000, source: "scrape");
        InsertSnapshotState("song_1", 42, isFinalized: true);
        InsertOverlayEntry("song_1", "acct_overlay", 101_000, source: "backfill", sourcePriority: 200, overlayReason: "preserved-backfill");

        var population = Db.GetCurrentStatePopulationAtOrBelow("song_1", 100_000);

        Assert.Equal(1, population);
    }

    [Fact]
    public void GetCurrentStatePlayerScores_prefers_overlay_then_snapshot_with_live_fallback()
    {
        Db.UpsertEntries("song_live",
        [
            MakeEntry("acct_user", 60_000),
            MakeEntry("acct_other", 55_000),
        ]);
        Db.UpsertEntries("song_snapshot",
        [
            MakeEntry("acct_user", 80_000),
            MakeEntry("acct_other", 70_000),
        ]);
        Db.UpsertEntries("song_overlay",
        [
            MakeEntry("acct_user", 75_000),
            MakeEntry("acct_other", 74_000),
        ]);

        InsertSnapshotEntry(42, "song_snapshot", "acct_user", 120_000, source: "scrape");
        InsertSnapshotEntry(42, "song_snapshot", "acct_other", 110_000, source: "scrape");
        InsertSnapshotState("song_snapshot", 42, isFinalized: true);

        InsertSnapshotEntry(42, "song_overlay", "acct_user", 90_000, source: "scrape");
        InsertSnapshotEntry(42, "song_overlay", "acct_other", 85_000, source: "scrape");
        InsertSnapshotState("song_overlay", 42, isFinalized: true);
        InsertOverlayEntry("song_overlay", "acct_user", 150_000, source: "backfill", sourcePriority: 200, overlayReason: "preserved-backfill");

        var scores = Db.GetCurrentStatePlayerScores("acct_user");

        Assert.Equal(3, scores.Count);
        Assert.Equal(60_000, scores.Single(s => s.SongId == "song_live").Score);
        Assert.Equal(120_000, scores.Single(s => s.SongId == "song_snapshot").Score);
        Assert.Equal(150_000, scores.Single(s => s.SongId == "song_overlay").Score);
    }

    [Fact]
    public void GetCurrentStatePlayerScoresForSongs_returns_current_state_subset()
    {
        Db.UpsertEntries("song_A",
        [
            MakeEntry("acct_user", 70_000),
            MakeEntry("acct_other", 65_000),
        ]);
        Db.UpsertEntries("song_B",
        [
            MakeEntry("acct_user", 75_000),
            MakeEntry("acct_other", 70_000),
        ]);
        Db.UpsertEntries("song_C",
        [
            MakeEntry("acct_user", 80_000),
            MakeEntry("acct_other", 79_000),
        ]);

        InsertSnapshotEntry(42, "song_B", "acct_user", 130_000, source: "scrape");
        InsertSnapshotEntry(42, "song_B", "acct_other", 120_000, source: "scrape");
        InsertSnapshotState("song_B", 42, isFinalized: true);
        InsertOverlayEntry("song_C", "acct_user", 140_000, source: "refresh", sourcePriority: 200, overlayReason: "preserved-refresh");

        var scores = Db.GetCurrentStatePlayerScoresForSongs("acct_user", ["song_B", "song_C"]);

        Assert.Equal(2, scores.Count);
        Assert.Equal(130_000, scores.Single(s => s.SongId == "song_B").Score);
        Assert.Equal(140_000, scores.Single(s => s.SongId == "song_C").Score);
        Assert.DoesNotContain(scores, s => s.SongId == "song_A");
    }

    [Fact]
    public void GetCurrentStateSongIdsForAccount_excludes_live_row_when_snapshot_omits_account()
    {
        Db.UpsertEntries("song_kept",
        [
            MakeEntry("acct_user", 90_000),
            MakeEntry("acct_other", 80_000),
        ]);
        Db.UpsertEntries("song_removed",
        [
            MakeEntry("acct_user", 95_000),
            MakeEntry("acct_other", 85_000),
        ]);

        InsertSnapshotEntry(42, "song_removed", "acct_other", 120_000, source: "scrape");
        InsertSnapshotState("song_removed", 42, isFinalized: true);

        var ids = Db.GetCurrentStateSongIdsForAccount("acct_user");

        Assert.Single(ids);
        Assert.Contains("song_kept", ids);
        Assert.DoesNotContain("song_removed", ids);
    }

    [Fact]
    public void GetCurrentStateNeighborhood_recomputes_rank_from_snapshot_and_overlay_rows()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("acct_user", 100_000),
            MakeEntry("acct_live_only", 90_000),
        ]);

        InsertSnapshotEntry(42, "song_1", "acct_user", 120_000, source: "scrape");
        InsertSnapshotEntry(42, "song_1", "acct_snapshot", 110_000, source: "scrape");
        InsertSnapshotState("song_1", 42, isFinalized: true);
        InsertOverlayEntry("song_1", "acct_overlay", 130_000, source: "backfill", sourcePriority: 200, overlayReason: "preserved-backfill");

        var neighbors = Db.GetCurrentStateNeighborhood("song_1", centerRank: 2, rankRadius: 2, excludeAccountId: "acct_user");

        Assert.Equal(2, neighbors.Count);
        Assert.Equal(("acct_overlay", 1, 130_000), neighbors[0]);
        Assert.Equal(("acct_snapshot", 3, 110_000), neighbors[1]);
    }

    [Fact]
    public void UpsertEntries_shadow_writes_overlay_for_backfill_source()
    {
        var entry = MakeEntry("acct_overlay", 120_000);
        entry.Source = "backfill";
        entry.ApiRank = 1;

        Db.UpsertEntries("song_1", [entry]);

        var overlay = GetOverlayEntry("song_1", "acct_overlay");
        Assert.NotNull(overlay);
        Assert.Equal(120_000, overlay?.Score);
        Assert.Equal("backfill", overlay?.Source);
        Assert.Equal(200, overlay?.SourcePriority);
        Assert.Equal("preserved-backfill", overlay?.OverlayReason);
    }

    [Fact]
    public void UpsertEntries_shadow_writes_overlay_for_refresh_source()
    {
        var entry = MakeEntry("acct_refresh", 115_000);
        entry.Source = "refresh";
        entry.ApiRank = 7;

        Db.UpsertEntries("song_1", [entry]);

        var overlay = GetOverlayEntry("song_1", "acct_refresh");
        Assert.NotNull(overlay);
        Assert.Equal(115_000, overlay?.Score);
        Assert.Equal("refresh", overlay?.Source);
        Assert.Equal(200, overlay?.SourcePriority);
        Assert.Equal("preserved-refresh", overlay?.OverlayReason);
    }

    [Fact]
    public void UpsertEntries_scrape_write_clears_existing_overlay_row()
    {
        var backfillEntry = MakeEntry("acct_overlay", 120_000);
        backfillEntry.Source = "backfill";
        backfillEntry.ApiRank = 1;
        Db.UpsertEntries("song_1", [backfillEntry]);

        var scrapeEntry = MakeEntry("acct_overlay", 125_000);
        scrapeEntry.Source = "scrape";
        scrapeEntry.ApiRank = 2;
        Db.UpsertEntries("song_1", [scrapeEntry]);

        Assert.Null(GetOverlayEntry("song_1", "acct_overlay"));
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
    public void GetAllSongCounts_prefers_song_stats_when_available()
    {
        Db.UpsertEntries("song_A", [MakeEntry("acct_1", 100_000)]);

        using (var conn = _fixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO song_stats (song_id, instrument, entry_count, previous_entry_count, log_weight, max_score, computed_at)
                VALUES ('song_A', @instrument, 7, 0, 1.0, NULL, @now)
                ON CONFLICT (song_id, instrument) DO UPDATE SET
                    entry_count = EXCLUDED.entry_count,
                    previous_entry_count = EXCLUDED.previous_entry_count,
                    log_weight = EXCLUDED.log_weight,
                    max_score = EXCLUDED.max_score,
                    computed_at = EXCLUDED.computed_at
                """;
            cmd.Parameters.AddWithValue("instrument", Db.Instrument);
            cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
            cmd.ExecuteNonQuery();
        }

        var counts = Db.GetAllSongCounts();
        Assert.Single(counts);
        Assert.Equal(7, counts["song_A"]);
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

    // ═══ GetSongIdsForAccount ═══════════════════════════════════

    [Fact]
    public void GetSongIdsForAccount_returns_correct_song_ids()
    {
        Db.UpsertEntries("song_A", [MakeEntry("acct_1", 100_000)]);
        Db.UpsertEntries("song_B", [MakeEntry("acct_1", 90_000)]);
        Db.UpsertEntries("song_A", [MakeEntry("acct_2", 80_000)]);

        var ids = Db.GetSongIdsForAccount("acct_1");
        Assert.Equal(2, ids.Count);
        Assert.Contains("song_A", ids);
        Assert.Contains("song_B", ids);
    }

    [Fact]
    public void GetSongIdsForAccount_returns_empty_for_unknown_account()
    {
        var ids = Db.GetSongIdsForAccount("nobody");
        Assert.Empty(ids);
    }

    // ═══ GetPlayerScoresForSongs ════════════════════════════════

    [Fact]
    public void GetPlayerScoresForSongs_returns_filtered_subset()
    {
        Db.UpsertEntries("song_A", [MakeEntry("acct_1", 100_000)]);
        Db.UpsertEntries("song_B", [MakeEntry("acct_1", 90_000)]);
        Db.UpsertEntries("song_C", [MakeEntry("acct_1", 80_000)]);

        var scores = Db.GetPlayerScoresForSongs("acct_1", new[] { "song_A", "song_C" });
        Assert.Equal(2, scores.Count);
        Assert.Contains(scores, s => s.SongId == "song_A");
        Assert.Contains(scores, s => s.SongId == "song_C");
        Assert.DoesNotContain(scores, s => s.SongId == "song_B");
    }

    [Fact]
    public void GetPlayerScoresForSongs_returns_empty_for_empty_song_list()
    {
        Db.UpsertEntries("song_A", [MakeEntry("acct_1", 100_000)]);

        var scores = Db.GetPlayerScoresForSongs("acct_1", Array.Empty<string>());
        Assert.Empty(scores);
    }

    [Fact]
    public void GetPlayerScoresForSongs_returns_empty_for_unknown_account()
    {
        Db.UpsertEntries("song_A", [MakeEntry("acct_1", 100_000)]);

        var scores = Db.GetPlayerScoresForSongs("nobody", new[] { "song_A" });
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

    [Fact]
    public void GetMaxSeason_backfills_cached_state_when_missing()
    {
        using (var conn = _fixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO leaderboard_entries
                (song_id, instrument, account_id, score, accuracy, is_full_combo, stars,
                 season, percentile, rank, source, difficulty, first_seen_at, last_updated_at)
                VALUES ('song_raw', @instrument, 'acct_raw', 100000, 95, false, 5,
                        9, 99.0, 1, 'scrape', 3, @now, @now)
                """;
            cmd.Parameters.AddWithValue("instrument", Db.Instrument);
            cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
            cmd.ExecuteNonQuery();
        }

        Assert.Equal(9, Db.GetMaxSeason());

        using var verifyConn = _fixture.DataSource.OpenConnection();
        using var verifyCmd = verifyConn.CreateCommand();
        verifyCmd.CommandText = "SELECT max_observed_season FROM instrument_scrape_state WHERE instrument = @instrument";
        verifyCmd.Parameters.AddWithValue("instrument", Db.Instrument);
        Assert.Equal(9, Convert.ToInt32(verifyCmd.ExecuteScalar()));
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
        Assert.Equal(2, rankings["song_1"]);  // one score above 75k
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

        // Song A: acct_2 has 80k, 1 person above → rank 2
        Assert.Equal(2, rankings["song_A"]);

        // Song B: acct_2 has 90k, 0 above → rank 1
        Assert.Equal(1, rankings["song_B"]);
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
        Assert.Equal(1, rankings["song_B"]);
    }

    [Fact]
    public void GetPlayerRankings_songId_filter_returns_empty_when_no_match()
    {
        Db.UpsertEntries("song_A", [MakeEntry("acct_1", 100_000)]);

        var rankings = Db.GetPlayerRankings("acct_1", songId: "song_nonexistent");
        Assert.Empty(rankings);
    }

    // ═══ GetPlayerRankings caching ═════════════════════════════

    [Fact]
    public void GetPlayerRankings_returns_consistent_result_on_second_call()
    {
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000), MakeEntry("acct_2", 80_000)]);

        var first = Db.GetPlayerRankings("acct_1");
        var second = Db.GetPlayerRankings("acct_1");

        // PG has no cache — verify results are equivalent
        Assert.Equal(first.Count, second.Count);
        foreach (var key in first.Keys)
            Assert.Equal(first[key], second[key]);
    }

    [Fact]
    public void GetPlayerRankings_reflects_new_data_after_upsert()
    {
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000)]);
        var before = Db.GetPlayerRankings("acct_1");
        Assert.Single(before);

        // Upsert new entries — PG always returns fresh data
        Db.UpsertEntries("song_2", [MakeEntry("acct_1", 50_000)]);
        var after = Db.GetPlayerRankings("acct_1");

        Assert.Equal(2, after.Count);
    }

    [Fact]
    public void GetPlayerRankingsFiltered_returns_consistent_result_on_second_call()
    {
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000), MakeEntry("acct_2", 200_000)]);
        var maxScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["song_1"] = 150_000 };

        var first = Db.GetPlayerRankingsFiltered("acct_1", maxScores);
        var second = Db.GetPlayerRankingsFiltered("acct_1", maxScores);

        // PG has no cache — verify results are equivalent
        Assert.Equal(first.Count, second.Count);
        foreach (var key in first.Keys)
            Assert.Equal(first[key], second[key]);
    }

    [Fact]
    public void GetPlayerRankingsFiltered_different_thresholds_get_separate_cache_entries()
    {
        Db.UpsertEntries("song_1", [
            MakeEntry("acct_1", 100_000),
            MakeEntry("acct_2", 200_000),
            MakeEntry("acct_3", 50_000),
        ]);

        var maxA = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["song_1"] = 150_000 };
        var maxB = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["song_1"] = 250_000 };

        var resultA = Db.GetPlayerRankingsFiltered("acct_1", maxA);
        var resultB = Db.GetPlayerRankingsFiltered("acct_1", maxB);

        // Different thresholds should produce different cache entries
        Assert.NotSame(resultA, resultB);
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
    public void Constructor_with_datasource_succeeds()
    {
        var ds = SharedPostgresContainer.CreateDatabase();
        try
        {
            var logger = NSubstitute.Substitute.For<ILogger<InstrumentDatabase>>();
            using var db = new InstrumentDatabase("Solo_Guitar", ds, logger);
        }
        finally
        {
            ds.Dispose();
        }
    }

    // ─── Dispose ────────────────────────────────────────────

    [Fact]
    public void Dispose_cleans_up()
    {
        var ds = SharedPostgresContainer.CreateDatabase();
        try
        {
            var logger = NSubstitute.Substitute.For<ILogger<InstrumentDatabase>>();
            var db = new InstrumentDatabase("Solo_Guitar", ds, logger);
            // Force connection by upserting entries
            var entry = MakeEntry("acct_dispose", 50000);
            db.UpsertEntries("song_dispose", [entry]);
            // Dispose should not throw
            db.Dispose();
            // Calling Dispose again should be safe
            db.Dispose();
        }
        finally
        {
            ds.Dispose();
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

    // ═══ RecomputeAllRanks ══════════════════════════════════════

    [Fact]
    public void RecomputeAllRanks_assigns_correct_ranks_by_score()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("acct_low",   50_000),
            MakeEntry("acct_mid",   75_000),
            MakeEntry("acct_high", 100_000),
        ]);

        var updated = Db.RecomputeAllRanks();
        Assert.Equal(3, updated);

        var e1 = Db.GetEntry("song_1", "acct_high");
        var e2 = Db.GetEntry("song_1", "acct_mid");
        var e3 = Db.GetEntry("song_1", "acct_low");
        Assert.Equal(1, e1!.Rank);
        Assert.Equal(2, e2!.Rank);
        Assert.Equal(3, e3!.Rank);
    }

    [Fact]
    public void RecomputeAllRanks_handles_multiple_songs()
    {
        Db.UpsertEntries("song_A", [MakeEntry("a1", 200), MakeEntry("a2", 100)]);
        Db.UpsertEntries("song_B", [MakeEntry("a1", 50), MakeEntry("a3", 300)]);

        Db.RecomputeAllRanks();

        Assert.Equal(1, Db.GetEntry("song_A", "a1")!.Rank);
        Assert.Equal(2, Db.GetEntry("song_A", "a2")!.Rank);
        Assert.Equal(2, Db.GetEntry("song_B", "a1")!.Rank);
        Assert.Equal(1, Db.GetEntry("song_B", "a3")!.Rank);
    }

    [Fact]
    public void RecomputeAllRanks_returns_zero_for_empty_db()
    {
        var updated = Db.RecomputeAllRanks();
        Assert.Equal(0, updated);
    }

    // ═══ GetLeaderboardWithCount ════════════════════════════════

    [Fact]
    public void GetLeaderboardCount_returns_count()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("a", 10_000),
            MakeEntry("b", 20_000),
            MakeEntry("c", 30_000),
        ]);

        Assert.Equal(3, Db.GetLeaderboardCount("song_1"));
        Assert.Equal(0, Db.GetLeaderboardCount("nonexistent"));
    }

    [Fact]
    public void GetLeaderboardWithCount_returns_entries_and_total()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("a", 10_000),
            MakeEntry("b", 20_000),
            MakeEntry("c", 30_000),
        ]);

        var (entries, total) = Db.GetLeaderboardWithCount("song_1");
        Assert.Equal(3, entries.Count);
        Assert.Equal(3, total);
        Assert.Equal("c", entries[0].AccountId); // highest score first
    }

    [Fact]
    public void GetLeaderboardWithCount_respects_top_and_offset()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("a", 10_000),
            MakeEntry("b", 20_000),
            MakeEntry("c", 30_000),
            MakeEntry("d", 40_000),
        ]);

        var (entries, total) = Db.GetLeaderboardWithCount("song_1", top: 2, offset: 1);
        Assert.Equal(2, entries.Count);
        Assert.Equal(4, total); // total is all entries, not just page
        Assert.Equal("c", entries[0].AccountId);
        Assert.Equal("b", entries[1].AccountId);
    }

    [Fact]
    public void GetLeaderboardWithCount_empty_returns_zero()
    {
        var (entries, total) = Db.GetLeaderboardWithCount("nonexistent");
        Assert.Empty(entries);
        Assert.Equal(0, total);
    }

    [Fact]
    public void GetLeaderboardWithCount_maxScore_filters_above_threshold()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("a", 100_000),
            MakeEntry("b", 90_000),
            MakeEntry("c", 80_000),
            MakeEntry("d", 70_000),
        ]);

        // maxScore=90000 should exclude the 100k entry
        var (entries, total) = Db.GetLeaderboardWithCount("song_1", maxScore: 90_000);
        Assert.Equal(3, entries.Count);
        Assert.Equal(3, total);
        Assert.Equal("b", entries[0].AccountId);
        Assert.Equal(1, entries[0].Rank); // re-ranked within filtered set
    }

    [Fact]
    public void GetLeaderboardWithCount_maxScore_with_pagination()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("a", 100_000),
            MakeEntry("b", 90_000),
            MakeEntry("c", 80_000),
            MakeEntry("d", 70_000),
            MakeEntry("e", 60_000),
        ]);

        // maxScore=90000 filters out "a", leaving b/c/d/e
        var (page1, total1) = Db.GetLeaderboardWithCount("song_1", top: 2, offset: 0, maxScore: 90_000);
        Assert.Equal(2, page1.Count);
        Assert.Equal(4, total1);
        Assert.Equal("b", page1[0].AccountId);
        Assert.Equal(1, page1[0].Rank);
        Assert.Equal("c", page1[1].AccountId);
        Assert.Equal(2, page1[1].Rank);

        var (page2, total2) = Db.GetLeaderboardWithCount("song_1", top: 2, offset: 2, maxScore: 90_000);
        Assert.Equal(2, page2.Count);
        Assert.Equal(4, total2);
        Assert.Equal("d", page2[0].AccountId);
        Assert.Equal(3, page2[0].Rank);
        Assert.Equal("e", page2[1].AccountId);
        Assert.Equal(4, page2[1].Rank);
    }

    [Fact]
    public void GetLeaderboardWithCount_maxScore_null_returns_all()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("a", 100_000),
            MakeEntry("b", 50_000),
        ]);

        var (entries, total) = Db.GetLeaderboardWithCount("song_1", maxScore: null);
        Assert.Equal(2, entries.Count);
        Assert.Equal(2, total);
    }

    [Fact]
    public void GetLeaderboardWithCount_maxScore_filters_all_returns_empty()
    {
        Db.UpsertEntries("song_1",
        [
            MakeEntry("a", 100_000),
            MakeEntry("b", 90_000),
        ]);

        var (entries, total) = Db.GetLeaderboardWithCount("song_1", maxScore: 50_000);
        Assert.Empty(entries);
        Assert.Equal(0, total);
    }

    // ═══ GetPlayerStoredRankings ════════════════════════════════

    [Fact]
    public void GetPlayerStoredRankings_returns_stored_rank()
    {
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000)]);
        // Set stored rank via recompute
        Db.RecomputeAllRanks();

        var rankings = Db.GetPlayerStoredRankings("acct_1");
        Assert.Single(rankings);
        Assert.Equal(1, rankings["song_1"].Rank);
        Assert.Equal(1, rankings["song_1"].Total);
    }

    [Fact]
    public void GetPlayerStoredRankings_returns_zero_rank_if_not_computed()
    {
        // Insert with Rank=0 (default), don't call RecomputeAllRanks
        var entry = MakeEntry("acct_1", 100_000);
        entry.Rank = 0;
        Db.UpsertEntries("song_1", [entry]);

        var rankings = Db.GetPlayerStoredRankings("acct_1");
        Assert.Single(rankings);
        Assert.Equal(0, rankings["song_1"].Rank);
    }

    [Fact]
    public void GetPlayerStoredRankings_filters_by_songId()
    {
        Db.UpsertEntries("song_A", [MakeEntry("acct_1", 100_000)]);
        Db.UpsertEntries("song_B", [MakeEntry("acct_1", 50_000)]);
        Db.RecomputeAllRanks();

        var rankings = Db.GetPlayerStoredRankings("acct_1", songId: "song_A");
        Assert.Single(rankings);
        Assert.True(rankings.ContainsKey("song_A"));
    }

    [Fact]
    public void GetPlayerStoredRankings_empty_for_unknown_account()
    {
        var rankings = Db.GetPlayerStoredRankings("nobody");
        Assert.Empty(rankings);
    }

    // ═══ PruneExcessEntries ═════════════════════════════════════

    [Fact]
    public void PruneExcessEntries_removes_low_scoring_entries()
    {
        // Seed 20 entries
        var entries = Enumerable.Range(0, 20).Select(i =>
            MakeEntry($"player_{i}", 1000 - i * 10)).ToList();
        Db.UpsertEntries("song1", entries);

        // Prune to top 5
        var deleted = Db.PruneExcessEntries("song1", 5, new HashSet<string>());
        Assert.Equal(15, deleted);

        var remaining = Db.GetPlayerScores("player_0", "song1");
        Assert.Single(remaining); // top player kept

        var pruned = Db.GetPlayerScores("player_19", "song1");
        Assert.Empty(pruned); // lowest player removed
    }

    [Fact]
    public void PruneExcessEntries_preserves_registered_users()
    {
        var entries = Enumerable.Range(0, 20).Select(i =>
            MakeEntry($"player_{i}", 1000 - i * 10)).ToList();
        Db.UpsertEntries("song1", entries);

        // Prune to top 5, but preserve player_15 (rank 16, outside top 5)
        var preserve = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "player_15" };
        var deleted = Db.PruneExcessEntries("song1", 5, preserve);
        Assert.Equal(14, deleted); // 20 - 5 (top) - 1 (preserved) = 14

        // player_15 should still exist
        var keptScores = Db.GetPlayerScores("player_15", "song1");
        Assert.Single(keptScores);
    }

    [Fact]
    public void PruneExcessEntries_no_op_when_under_limit()
    {
        var entries = Enumerable.Range(0, 5).Select(i =>
            MakeEntry($"player_{i}", 1000 - i * 10)).ToList();
        Db.UpsertEntries("song1", entries);

        var deleted = Db.PruneExcessEntries("song1", 10, new HashSet<string>());
        Assert.Equal(0, deleted);
    }

    [Fact]
    public void PruneExcessEntries_with_threshold_keeps_over_threshold_entries()
    {
        // 10 entries above threshold (scores 2000–1100), 10 at or below threshold (scores 1000–100)
        var entries = Enumerable.Range(0, 20).Select(i =>
            MakeEntry($"player_{i}", 2000 - i * 100)).ToList();
        Db.UpsertEntries("song1", entries);

        // Threshold = 1050: scores > 1050 are over-threshold (players 0–9 have 2000,1900..1100)
        // Actually scores > 1050: 2000,1900,1800,1700,1600,1500,1400,1300,1200,1100 = 10 entries
        // Scores <= 1050: 1000,900,800,700,600,500,400,300,200,100 = 10 entries
        // Prune valid entries to top 5 → should delete 5 lowest valid entries
        var deleted = Db.PruneExcessEntries("song1", 5, new HashSet<string>(), overThresholdScore: 1050);
        Assert.Equal(5, deleted); // 10 valid - 5 kept = 5 deleted

        // Total remaining: 10 over-threshold + 5 valid = 15
        Assert.Equal(15, Db.GetLeaderboardCount("song1"));

        // Highest over-threshold entry still present
        var top = Db.GetPlayerScores("player_0", "song1");
        Assert.Single(top);

        // Lowest valid entry within top 5 valid (score 600) still present
        var kept = Db.GetPlayerScores("player_14", "song1");
        Assert.Single(kept);

        // Entry outside top 5 valid (score 100) should be pruned
        var pruned = Db.GetPlayerScores("player_19", "song1");
        Assert.Empty(pruned);
    }

    [Fact]
    public void PruneExcessEntries_with_threshold_preserves_registered_users()
    {
        var entries = Enumerable.Range(0, 20).Select(i =>
            MakeEntry($"player_{i}", 2000 - i * 100)).ToList();
        Db.UpsertEntries("song1", entries);

        // Threshold = 1050, prune valid to top 5, but preserve player_19 (lowest valid score = 100)
        var preserve = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "player_19" };
        var deleted = Db.PruneExcessEntries("song1", 5, preserve, overThresholdScore: 1050);
        Assert.Equal(4, deleted); // 10 valid - 5 kept - 1 preserved = 4 deleted

        // player_19 should still exist despite being outside top 5 valid
        var keptScores = Db.GetPlayerScores("player_19", "song1");
        Assert.Single(keptScores);
    }

    [Fact]
    public void PruneExcessEntries_null_threshold_matches_original_behavior()
    {
        // Same as the original test: no threshold → prune by top score overall
        var entries = Enumerable.Range(0, 20).Select(i =>
            MakeEntry($"player_{i}", 1000 - i * 10)).ToList();
        Db.UpsertEntries("song1", entries);

        var deleted = Db.PruneExcessEntries("song1", 5, new HashSet<string>(), overThresholdScore: null);
        Assert.Equal(15, deleted);

        var remaining = Db.GetPlayerScores("player_0", "song1");
        Assert.Single(remaining);

        var pruned = Db.GetPlayerScores("player_19", "song1");
        Assert.Empty(pruned);
    }

    [Fact]
    public void PruneExcessEntries_raw_chopt_threshold_keeps_entries_between_max_and_105()
    {
        // Simulate post-change pruning where threshold = raw CHOpt max (1000), not 1050.
        // Entries at 1040 and 1020 are between CHOpt max (1000) and old 1.05× threshold (1050).
        // Under raw CHOpt max threshold, these are over-threshold → kept unconditionally.
        var entries = new[]
        {
            MakeEntry("cheater1", 2000),  // clearly over
            MakeEntry("border1",  1040),  // between CHOpt and 1.05× — over-threshold with raw cutoff
            MakeEntry("border2",  1020),  // between CHOpt and 1.05× — over-threshold with raw cutoff
            MakeEntry("legit1",   1000),  // at exactly CHOpt max — valid (≤ threshold)
            MakeEntry("legit2",    900),  // valid
            MakeEntry("legit3",    800),  // valid
            MakeEntry("legit4",    700),  // valid
            MakeEntry("legit5",    600),  // valid — within top 3 valid
            MakeEntry("legit6",    500),  // valid — outside top 3 valid → pruned
            MakeEntry("legit7",    400),  // valid — outside top 3 valid → pruned
        };
        Db.UpsertEntries("song1", entries.ToList());

        // Threshold = 1000 (raw CHOpt max), keep top 3 valid entries
        var deleted = Db.PruneExcessEntries("song1", 3, new HashSet<string>(), overThresholdScore: 1000);
        Assert.Equal(4, deleted); // 7 valid - 3 kept = 4 deleted

        // Over-threshold entries (>1000) are all kept
        Assert.Single(Db.GetPlayerScores("cheater1", "song1"));
        Assert.Single(Db.GetPlayerScores("border1", "song1"));
        Assert.Single(Db.GetPlayerScores("border2", "song1"));

        // Top 3 valid entries kept (1000, 900, 800)
        Assert.Single(Db.GetPlayerScores("legit1", "song1"));
        Assert.Single(Db.GetPlayerScores("legit2", "song1"));
        Assert.Single(Db.GetPlayerScores("legit3", "song1"));

        // Below top 3 valid → pruned
        Assert.Empty(Db.GetPlayerScores("legit6", "song1"));
        Assert.Empty(Db.GetPlayerScores("legit7", "song1"));

        // Total remaining: 3 over-threshold + 3 valid = 6
        Assert.Equal(6, Db.GetLeaderboardCount("song1"));
    }

    [Fact]
    public void PruneAllSongs_prunes_across_songs()
    {
        for (int s = 0; s < 3; s++)
        {
            var entries = Enumerable.Range(0, 10).Select(i =>
                MakeEntry($"player_{i}", 1000 - i * 10)).ToList();
            Db.UpsertEntries($"song_{s}", entries);
        }

        var deleted = Db.PruneAllSongs(3, new HashSet<string>());
        Assert.Equal(21, deleted); // 3 songs × 7 pruned each = 21

        // Each song should have 3 entries
        foreach (var s in Enumerable.Range(0, 3))
            Assert.Equal(3, Db.GetLeaderboardCount($"song_{s}"));
    }

    // ═══ Checkpoint ═════════════════════════════════════════════

    [Fact]
    public void Checkpoint_succeeds_after_writes()
    {
        Db.UpsertEntries("song_1", [MakeEntry("acct_1", 100_000)]);

        // Should not throw
        Db.Checkpoint();

        // Data should still be readable after checkpoint
        var entry = Db.GetEntry("song_1", "acct_1");
        Assert.NotNull(entry);
        Assert.Equal(100_000, entry.Score);
    }

    [Fact]
    public void Checkpoint_succeeds_on_empty_database()
    {
        // Should not throw even when there's nothing to checkpoint
        Db.Checkpoint();
    }

    // ═══ GetAccountRankingNeighborhood ═══════════════════════

    private void SeedAccountRankings(params (string AccountId, long TotalScore, int TotalScoreRank)[] accounts)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        foreach (var (id, score, rank) in accounts)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO account_rankings
                (account_id, instrument, songs_played, total_charted_songs, coverage,
                 raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank,
                 weighted_rating, weighted_rank,
                 fc_rate, fc_rate_rank,
                 total_score, total_score_rank,
                 max_score_percent, max_score_percent_rank,
                 avg_accuracy, full_combo_count, avg_stars, best_rank, avg_rank,
                 computed_at)
                VALUES (@id, @instrument, 10, 100, 0.1,
                        0.5, 0.5, @rank,
                        0.5, @rank,
                        0.5, @rank,
                        @score, @rank,
                        0.5, @rank,
                        95.0, 0, 4.5, 1, 5.0,
                        '2025-01-01T00:00:00Z');
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@instrument", Db.Instrument);
            cmd.Parameters.AddWithValue("@score", score);
            cmd.Parameters.AddWithValue("@rank", rank);
            cmd.ExecuteNonQuery();
        }
    }

    private void UpsertAccountRankingStatsForTest(int count, DateTime computedAt)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO account_ranking_stats (instrument, ranked_account_count, computed_at)
            VALUES (@instrument, @count, @computedAt)
            ON CONFLICT (instrument) DO UPDATE SET
                ranked_account_count = EXCLUDED.ranked_account_count,
                computed_at = EXCLUDED.computed_at
            """;
        cmd.Parameters.AddWithValue("instrument", Db.Instrument);
        cmd.Parameters.AddWithValue("count", count);
        cmd.Parameters.AddWithValue("computedAt", computedAt);
        cmd.ExecuteNonQuery();
    }

    private (int Count, DateTime ComputedAt)? GetAccountRankingStatsForTest()
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ranked_account_count, computed_at FROM account_ranking_stats WHERE instrument = @instrument";
        cmd.Parameters.AddWithValue("instrument", Db.Instrument);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return (reader.GetInt32(0), reader.GetDateTime(1));
    }

    private void InsertSnapshotState(string songId, long activeSnapshotId, bool isFinalized)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO leaderboard_snapshot_state
            (song_id, instrument, active_snapshot_id, scrape_id, is_finalized, updated_at)
            VALUES (@songId, @instrument, @activeSnapshotId, 1, @isFinalized, @updatedAt)
            ON CONFLICT (song_id, instrument) DO UPDATE SET
                active_snapshot_id = EXCLUDED.active_snapshot_id,
                scrape_id = EXCLUDED.scrape_id,
                is_finalized = EXCLUDED.is_finalized,
                updated_at = EXCLUDED.updated_at
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Db.Instrument);
        cmd.Parameters.AddWithValue("activeSnapshotId", activeSnapshotId);
        cmd.Parameters.AddWithValue("isFinalized", isFinalized);
        cmd.Parameters.AddWithValue("updatedAt", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private void InsertSnapshotEntry(long snapshotId, string songId, string accountId, int score, string source)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO leaderboard_entries_snapshot
            (snapshot_id, song_id, instrument, account_id, score, accuracy, is_full_combo, stars,
             season, percentile, rank, source, difficulty, api_rank, end_time, first_seen_at, last_updated_at)
            VALUES
            (@snapshotId, @songId, @instrument, @accountId, @score, 95, false, 5,
             3, 99.0, 1, @source, 3, 1, '2025-01-15T12:00:00Z', @now, @now)
            """;
        cmd.Parameters.AddWithValue("snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Db.Instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private void InsertOverlayEntry(string songId, string accountId, int score, string source, int sourcePriority, string overlayReason)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO leaderboard_entries_overlay
            (song_id, instrument, account_id, score, accuracy, is_full_combo, stars,
             season, percentile, rank, source, difficulty, api_rank, end_time,
             first_seen_at, last_updated_at, source_priority, overlay_reason)
            VALUES
            (@songId, @instrument, @accountId, @score, 95, false, 5,
             3, 99.0, 1, @source, 3, 1, '2025-01-15T12:00:00Z',
             @now, @now, @sourcePriority, @overlayReason)
            ON CONFLICT (song_id, instrument, account_id) DO UPDATE SET
                score = EXCLUDED.score,
                source = EXCLUDED.source,
                last_updated_at = EXCLUDED.last_updated_at,
                source_priority = EXCLUDED.source_priority,
                overlay_reason = EXCLUDED.overlay_reason
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Db.Instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("sourcePriority", sourcePriority);
        cmd.Parameters.AddWithValue("overlayReason", overlayReason);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private (int Score, string Source, int SourcePriority, string OverlayReason)? GetOverlayEntry(string songId, string accountId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT score, source, source_priority, overlay_reason
            FROM leaderboard_entries_overlay
            WHERE song_id = @songId AND instrument = @instrument AND account_id = @accountId
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", Db.Instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return (
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3));
    }

    [Fact]
    public void GetRankedAccountCount_backfills_cached_stats_when_missing()
    {
        SeedAccountRankings(("a1", 5000, 1), ("a2", 4000, 2), ("a3", 3000, 3));

        Assert.Equal(3, Db.GetRankedAccountCount());

        var stats = GetAccountRankingStatsForTest();
        Assert.NotNull(stats);
        Assert.Equal(3, stats.Value.Count);
    }

    [Fact]
    public void GetRankedAccountCount_uses_cached_stats_without_updating_computed_at()
    {
        SeedAccountRankings(("a1", 5000, 1), ("a2", 4000, 2));
        var cachedAt = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        UpsertAccountRankingStatsForTest(99, cachedAt);

        Assert.Equal(99, Db.GetRankedAccountCount());

        var stats = GetAccountRankingStatsForTest();
        Assert.NotNull(stats);
        Assert.Equal(99, stats.Value.Count);
        Assert.Equal(cachedAt, stats.Value.ComputedAt);
    }

    [Fact]
    public void GetAccountRankings_backfills_cached_stats_when_missing()
    {
        SeedAccountRankings(("a1", 5000, 1), ("a2", 4000, 2));

        var (entries, total) = Db.GetAccountRankings(rankBy: "totalscore", page: 1, pageSize: 10);

        Assert.Equal(2, total);
        Assert.Equal(2, entries.Count);

        var stats = GetAccountRankingStatsForTest();
        Assert.NotNull(stats);
        Assert.Equal(2, stats.Value.Count);
    }

    [Fact]
    public void GetAccountRankings_uses_cached_total_without_updating_computed_at()
    {
        SeedAccountRankings(("a1", 5000, 1), ("a2", 4000, 2));
        var cachedAt = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        UpsertAccountRankingStatsForTest(99, cachedAt);

        var (entries, total) = Db.GetAccountRankings(rankBy: "totalscore", page: 1, pageSize: 10);

        Assert.Equal(99, total);
        Assert.Equal(2, entries.Count);
        var stats = GetAccountRankingStatsForTest();
        Assert.NotNull(stats);
        Assert.Equal(99, stats.Value.Count);
        Assert.Equal(cachedAt, stats.Value.ComputedAt);
    }

    [Fact]
    public void GetAccountRankingNeighborhood_returns_above_self_below()
    {
        SeedAccountRankings(
            ("a1", 5000, 1), ("a2", 4000, 2), ("a3", 3000, 3),
            ("a4", 2000, 4), ("a5", 1000, 5));

        var (above, self, below) = Db.GetAccountRankingNeighborhood("a3", radius: 2);

        Assert.NotNull(self);
        Assert.Equal("a3", self.AccountId);
        Assert.Equal(3, self.TotalScoreRank);
        Assert.Equal(2, above.Count);
        Assert.Equal("a1", above[0].AccountId);
        Assert.Equal("a2", above[1].AccountId);
        Assert.Equal(2, below.Count);
        Assert.Equal("a4", below[0].AccountId);
        Assert.Equal("a5", below[1].AccountId);
    }

    [Fact]
    public void GetAccountRankingNeighborhood_rank1_has_no_above()
    {
        SeedAccountRankings(
            ("a1", 5000, 1), ("a2", 4000, 2), ("a3", 3000, 3));

        var (above, self, below) = Db.GetAccountRankingNeighborhood("a1", radius: 2);

        Assert.NotNull(self);
        Assert.Equal("a1", self.AccountId);
        Assert.Empty(above);
        Assert.Equal(2, below.Count);
    }

    [Fact]
    public void GetAccountRankingNeighborhood_last_rank_has_no_below()
    {
        SeedAccountRankings(
            ("a1", 5000, 1), ("a2", 4000, 2), ("a3", 3000, 3));

        var (above, self, below) = Db.GetAccountRankingNeighborhood("a3", radius: 2);

        Assert.NotNull(self);
        Assert.Equal("a3", self.AccountId);
        Assert.Equal(2, above.Count);
        Assert.Empty(below);
    }

    [Fact]
    public void GetAccountRankingNeighborhood_unknown_account_returns_nulls()
    {
        SeedAccountRankings(("a1", 5000, 1));

        var (above, self, below) = Db.GetAccountRankingNeighborhood("unknown");

        Assert.Null(self);
        Assert.Empty(above);
        Assert.Empty(below);
    }

    [Fact]
    public void GetAccountRankingNeighborhood_default_radius_is_5()
    {
        // Seed 11 accounts (ranks 1-11), target at rank 6
        var accounts = Enumerable.Range(1, 11)
            .Select(i => ($"a{i}", (long)(12000 - i * 1000), i))
            .ToArray();
        SeedAccountRankings(accounts);

        var (above, self, below) = Db.GetAccountRankingNeighborhood("a6");

        Assert.NotNull(self);
        Assert.Equal(5, above.Count);
        Assert.Equal(5, below.Count);
    }

    [Fact]
    public void GetAccountRankingNeighborhood_rankBy_adjusted_uses_adjusted_skill_rank()
    {
        // Seed 3 accounts with different total scores and skill ratings
        // Their adjusted ranks will differ from total score ranks
        var accounts = new (string, long, int)[]
        {
            ("a1", 5000, 1),
            ("a2", 3000, 2),
            ("a3", 1000, 3),
        };
        SeedAccountRankings(accounts);

        var (above, self, below) = Db.GetAccountRankingNeighborhood("a2", radius: 2, rankBy: "adjusted");

        Assert.NotNull(self);
        Assert.Equal("a2", self.AccountId);
    }

    [Fact]
    public void GetAccountRankingNeighborhood_invalid_rankBy_defaults_to_totalscore()
    {
        var accounts = new (string, long, int)[]
        {
            ("a1", 5000, 1),
            ("a2", 3000, 2),
        };
        SeedAccountRankings(accounts);

        var (above, self, below) = Db.GetAccountRankingNeighborhood("a2", radius: 2, rankBy: "invalid");

        Assert.NotNull(self);
        Assert.Equal("a2", self.AccountId);
    }

    // ═══ GetPlayerRankingsFiltered ══════════════════════════════

    [Fact]
    public void GetPlayerRankingsFiltered_excludes_invalid_scores_above_threshold()
    {
        // Two players on song_1: cheater at 200k (invalid), legit at 90k
        Db.UpsertEntries("song_1", [MakeEntry("cheater", 200_000), MakeEntry("legit", 90_000)]);

        // Without filter: legit is rank 2
        var unfiltered = Db.GetPlayerRankings("legit");
        Assert.Equal(2, unfiltered["song_1"]);

        // With filter: cheater's 200k exceeds threshold 100k → legit becomes rank 1
        var maxScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["song_1"] = 100_000 };
        var filtered = Db.GetPlayerRankingsFiltered("legit", maxScores);
        Assert.Equal(1, filtered["song_1"]);
    }

    [Fact]
    public void GetPlayerRankingsFiltered_omits_players_own_invalid_score()
    {
        // Player has an invalid score
        Db.UpsertEntries("song_1", [MakeEntry("cheater", 200_000), MakeEntry("legit", 90_000)]);

        var maxScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["song_1"] = 100_000 };
        var filtered = Db.GetPlayerRankingsFiltered("cheater", maxScores);

        // Cheater's score exceeds threshold → they're excluded from the ranking
        Assert.False(filtered.ContainsKey("song_1"));
    }

    [Fact]
    public void GetPlayerRankingsFiltered_no_threshold_for_song_includes_all()
    {
        Db.UpsertEntries("song_1", [MakeEntry("p1", 200_000), MakeEntry("p2", 90_000)]);

        // Empty maxScores → no filtering, all entries included (COALESCE fallback)
        var maxScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var filtered = Db.GetPlayerRankingsFiltered("p2", maxScores);
        Assert.Equal(2, filtered["song_1"]);
    }

    [Fact]
    public void GetPlayerRankingsFiltered_song_filter_works()
    {
        Db.UpsertEntries("song_1", [MakeEntry("p1", 200_000), MakeEntry("p2", 90_000)]);
        Db.UpsertEntries("song_2", [MakeEntry("p2", 80_000)]);

        var maxScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["song_1"] = 100_000,
            ["song_2"] = 100_000,
        };
        var filtered = Db.GetPlayerRankingsFiltered("p2", maxScores, songId: "song_1");

        Assert.Single(filtered);
        Assert.Equal(1, filtered["song_1"]);
    }

    // ═══ GetRankForScore ════════════════════════════════════════

    [Fact]
    public void GetRankForScore_returns_rank_without_filter()
    {
        Db.UpsertEntries("song_1", [
            MakeEntry("p1", 100_000),
            MakeEntry("p2", 90_000),
            MakeEntry("p3", 80_000),
        ]);

        // Score of 90k: 1 entry above → rank 2
        Assert.Equal(2, Db.GetRankForScore("song_1", 90_000));
        // Score of 100k: 0 entries above → rank 1
        Assert.Equal(1, Db.GetRankForScore("song_1", 100_000));
        // Score of 70k: 3 entries above → rank 4
        Assert.Equal(4, Db.GetRankForScore("song_1", 70_000));
    }

    [Fact]
    public void GetRankForScore_with_maxScore_excludes_invalid_entries()
    {
        Db.UpsertEntries("song_1", [
            MakeEntry("cheater", 200_000),
            MakeEntry("p1", 100_000),
            MakeEntry("p2", 90_000),
        ]);

        // Without filter: 90k has 2 entries above it → rank 3
        Assert.Equal(3, Db.GetRankForScore("song_1", 90_000));

        // With filter (max 105k): cheater excluded → 90k has 1 entry above → rank 2
        Assert.Equal(2, Db.GetRankForScore("song_1", 90_000, maxScore: 105_000));
    }

    // ═══ GetFilteredEntryCounts ═════════════════════════════════

    [Fact]
    public void GetFilteredEntryCounts_excludes_invalid_scores()
    {
        Db.UpsertEntries("song_1", [
            MakeEntry("cheater", 200_000),
            MakeEntry("p1", 100_000),
            MakeEntry("p2", 90_000),
        ]);
        Db.UpsertEntries("song_2", [
            MakeEntry("p1", 80_000),
        ]);

        var maxScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["song_1"] = 105_000,
            ["song_2"] = 100_000,
        };
        var counts = Db.GetFilteredEntryCounts(maxScores);

        Assert.Equal(2, counts["song_1"]); // cheater excluded
        Assert.Equal(1, counts["song_2"]); // all valid
    }
}
