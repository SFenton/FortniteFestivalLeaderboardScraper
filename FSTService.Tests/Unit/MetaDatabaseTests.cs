using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class MetaDatabaseTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    private Persistence.MetaDatabase Db => _fixture.Db;
    private NpgsqlDataSource DataSource => _fixture.DataSource;

    public void Dispose() => _fixture.Dispose();

    // ═══ ScrapeLog ══════════════════════════════════════════════

    [Fact]
    public void StartScrapeRun_returns_positive_id()
    {
        var id = Db.StartScrapeRun();
        Assert.True(id > 0);
    }

    [Fact]
    public void CompleteScrapeRun_updates_record()
    {
        var id = Db.StartScrapeRun();
        Db.CompleteScrapeRun(id, 100, 50_000, 200, 1_000_000);

        var last = Db.GetLastCompletedScrapeRun();
        Assert.NotNull(last);
        Assert.Equal(id, last.Id);
        Assert.Equal(100, last.SongsScraped);
        Assert.Equal(50_000, last.TotalEntries);
        Assert.NotNull(last.CompletedAt);
    }

    [Fact]
    public void GetLastCompletedScrapeRun_returns_null_when_empty()
    {
        var last = Db.GetLastCompletedScrapeRun();
        Assert.Null(last);
    }

    // ═══ AccountNames ═══════════════════════════════════════════

    [Fact]
    public void InsertAccountIds_creates_unresolved_entries()
    {
        var inserted = Db.InsertAccountIds(["acct_1", "acct_2"]);
        Assert.Equal(2, inserted);

        var unresolved = Db.GetUnresolvedAccountIds();
        Assert.Contains("acct_1", unresolved);
        Assert.Contains("acct_2", unresolved);
    }

    [Fact]
    public void InsertAccountIds_ignores_duplicates()
    {
        Db.InsertAccountIds(["acct_1"]);
        var inserted = Db.InsertAccountIds(["acct_1"]);
        Assert.Equal(0, inserted);
    }

    [Fact]
    public void InsertAccountNames_resolves_display_names()
    {
        Db.InsertAccountIds(["acct_1"]);
        Db.InsertAccountNames([("acct_1", "PlayerOne")]);

        var name = Db.GetDisplayName("acct_1");
        Assert.Equal("PlayerOne", name);

        Assert.Equal(0, Db.GetUnresolvedAccountCount());
    }

    [Fact]
    public void GetAccountIdForUsername_finds_by_display_name()
    {
        Db.InsertAccountNames([("acct_1", "PlayerOne")]);
        var id = Db.GetAccountIdForUsername("PlayerOne");
        Assert.Equal("acct_1", id);
    }

    [Fact]
    public void GetAccountIdForUsername_is_case_insensitive()
    {
        Db.InsertAccountNames([("acct_1", "PlayerOne")]);
        var id = Db.GetAccountIdForUsername("playerone");
        Assert.Equal("acct_1", id);
    }

    [Fact]
    public void GetAccountIdForUsername_returns_null_for_unknown()
    {
        var id = Db.GetAccountIdForUsername("nobody");
        Assert.Null(id);
    }

    // ═══ RegisteredUsers ════════════════════════════════════════

    [Fact]
    public void GetRegisteredAccountIds_returns_distinct_accounts()
    {
        Db.RegisterUser("dev_1", "acct_1");
        Db.RegisterUser("dev_2", "acct_1");
        Db.RegisterUser("dev_3", "acct_2");

        var ids = Db.GetRegisteredAccountIds();
        Assert.Equal(2, ids.Count);
        Assert.Contains("acct_1", ids);
        Assert.Contains("acct_2", ids);
    }

    // ═══ ScoreHistory ═══════════════════════════════════════════

    [Fact]
    public void InsertScoreChange_and_GetScoreHistory_roundtrip()
    {
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 100_000, null, 42,
            accuracy: 95, isFullCombo: false, stars: 5, percentile: 99.5, season: 3,
            scoreAchievedAt: "2025-01-15T12:00:00Z");

        var history = Db.GetScoreHistory("acct_1");
        Assert.Single(history);

        var entry = history[0];
        Assert.Equal("song_1", entry.SongId);
        Assert.Equal("Solo_Guitar", entry.Instrument);
        Assert.Null(entry.OldScore);
        Assert.Equal(100_000, entry.NewScore);
        Assert.Equal(95, entry.Accuracy);
        Assert.False(entry.IsFullCombo);
        Assert.Equal(5, entry.Stars);
        Assert.Equal(3, entry.Season);
        Assert.Null(entry.SeasonRank);
        Assert.Null(entry.AllTimeRank);
    }

    [Fact]
    public void GetScoreHistory_respects_limit()
    {
        for (int i = 0; i < 10; i++)
            Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, i * 1000, null, i);

        var history = Db.GetScoreHistory("acct_1", limit: 3);
        Assert.Equal(3, history.Count);
    }

    [Fact]
    public void GetScoreHistory_filters_by_songId()
    {
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 100_000, null, 1);
        Db.InsertScoreChange("song_2", "Solo_Guitar", "acct_1", null, 90_000, null, 2);
        Db.InsertScoreChange("song_1", "Solo_Bass", "acct_1", null, 80_000, null, 3);

        var history = Db.GetScoreHistory("acct_1", songId: "song_1");
        Assert.Equal(2, history.Count);
        Assert.All(history, h => Assert.Equal("song_1", h.SongId));
    }

    [Fact]
    public void GetScoreHistory_songId_filter_returns_empty_when_no_match()
    {
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 100_000, null, 1);

        var history = Db.GetScoreHistory("acct_1", songId: "song_nonexistent");
        Assert.Empty(history);
    }

    [Fact]
    public void InsertScoreChange_roundtrips_SeasonRank_and_AllTimeRank()
    {
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 200_000, null, 50,
            accuracy: 90, isFullCombo: true, stars: 5, percentile: 98.0, season: 10,
            scoreAchievedAt: "2025-06-01T00:00:00Z",
            seasonRank: 742, allTimeRank: 9989);

        var history = Db.GetScoreHistory("acct_1");
        Assert.Single(history);

        var entry = history[0];
        Assert.Equal(742, entry.SeasonRank);
        Assert.Equal(9989, entry.AllTimeRank);
    }

    // ═══ InsertScoreChanges (batch) ═════════════════════════════

    [Fact]
    public void InsertScoreChanges_batch_inserts_multiple()
    {
        var changes = new List<ScoreChangeRecord>
        {
            new()
            {
                SongId = "song_1", Instrument = "Solo_Guitar", AccountId = "acct_1",
                OldScore = null, NewScore = 100_000, OldRank = null, NewRank = 1,
                Accuracy = 95, IsFullCombo = true, Stars = 5, Percentile = 99.0,
                Season = 10, ScoreAchievedAt = "2025-01-01T00:00:00Z", AllTimeRank = 1,
            },
            new()
            {
                SongId = "song_2", Instrument = "Solo_Bass", AccountId = "acct_2",
                OldScore = 50_000, NewScore = 80_000, OldRank = 100, NewRank = 50,
                Accuracy = 88, IsFullCombo = false, Stars = 4, Percentile = 85.0,
                Season = 10, ScoreAchievedAt = "2025-01-02T00:00:00Z", AllTimeRank = 50,
            },
        };

        var inserted = Db.InsertScoreChanges(changes);
        Assert.Equal(2, inserted);

        var history1 = Db.GetScoreHistory("acct_1");
        Assert.Single(history1);
        Assert.Equal(100_000, history1[0].NewScore);

        var history2 = Db.GetScoreHistory("acct_2");
        Assert.Single(history2);
        Assert.Equal(80_000, history2[0].NewScore);
        Assert.Equal(50_000, history2[0].OldScore);
    }

    [Fact]
    public void InsertScoreChanges_batch_empty_returns_zero()
    {
        var inserted = Db.InsertScoreChanges([]);
        Assert.Equal(0, inserted);
    }

    [Fact]
    public void InsertScoreChanges_batch_deduplicates_with_conflict()
    {
        // Insert initial record
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 100_000, null, 1,
            scoreAchievedAt: "2025-01-01T00:00:00Z", seasonRank: 5);

        // Batch-insert same key with allTimeRank — should merge via COALESCE
        var changes = new List<ScoreChangeRecord>
        {
            new()
            {
                SongId = "song_1", Instrument = "Solo_Guitar", AccountId = "acct_1",
                OldScore = null, NewScore = 100_000, OldRank = null, NewRank = 1,
                ScoreAchievedAt = "2025-01-01T00:00:00Z", AllTimeRank = 42,
            },
        };

        Db.InsertScoreChanges(changes);

        var history = Db.GetScoreHistory("acct_1");
        Assert.Single(history);
        Assert.Equal(5, history[0].SeasonRank);   // preserved from first insert
        Assert.Equal(42, history[0].AllTimeRank);  // merged from batch
    }

    // ═══ BackfillStatus ═════════════════════════════════════════

    [Fact]
    public void Backfill_lifecycle_pending_to_complete()
    {
        Db.EnqueueBackfill("acct_1", 1000);
        var status = Db.GetBackfillStatus("acct_1");
        Assert.NotNull(status);
        Assert.Equal("pending", status.Status);
        Assert.Equal(1000, status.TotalSongsToCheck);

        Db.StartBackfill("acct_1");
        status = Db.GetBackfillStatus("acct_1");
        Assert.Equal("in_progress", status!.Status);
        Assert.NotNull(status.StartedAt);

        Db.UpdateBackfillProgress("acct_1", 500, 25);
        status = Db.GetBackfillStatus("acct_1");
        Assert.Equal(500, status!.SongsChecked);
        Assert.Equal(25, status.EntriesFound);

        Db.CompleteBackfill("acct_1");
        status = Db.GetBackfillStatus("acct_1");
        Assert.Equal("complete", status!.Status);
        Assert.NotNull(status.CompletedAt);
    }

    [Fact]
    public void Backfill_error_state()
    {
        Db.EnqueueBackfill("acct_1", 100);
        Db.StartBackfill("acct_1");
        Db.FailBackfill("acct_1", "API timeout");

        var status = Db.GetBackfillStatus("acct_1");
        Assert.Equal("error", status!.Status);
        Assert.Equal("API timeout", status.ErrorMessage);
    }

    [Fact]
    public void GetPendingBackfills_returns_pending_and_in_progress()
    {
        Db.EnqueueBackfill("acct_1", 100);
        Db.EnqueueBackfill("acct_2", 200);
        Db.StartBackfill("acct_1");
        Db.EnqueueBackfill("acct_3", 300);
        Db.StartBackfill("acct_3");
        Db.CompleteBackfill("acct_3");

        var pending = Db.GetPendingBackfills();
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, p => p.AccountId == "acct_1");
        Assert.Contains(pending, p => p.AccountId == "acct_2");
    }

    [Fact]
    public void BackfillProgress_tracks_checked_pairs()
    {
        Db.MarkBackfillSongChecked("acct_1", "song_1", "Solo_Guitar", true);
        Db.MarkBackfillSongChecked("acct_1", "song_2", "Solo_Guitar", false);

        var checkedPairs = Db.GetCheckedBackfillPairs("acct_1");
        Assert.Equal(2, checkedPairs.Count);
        Assert.Contains(("song_1", "Solo_Guitar"), checkedPairs);
        Assert.Contains(("song_2", "Solo_Guitar"), checkedPairs);
    }

    [Fact]
    public void EnqueueBackfill_does_not_reset_completed()
    {
        Db.EnqueueBackfill("acct_1", 100);
        Db.StartBackfill("acct_1");
        Db.CompleteBackfill("acct_1");

        // Re-enqueue should not overwrite 'complete' status
        Db.EnqueueBackfill("acct_1", 200);
        var status = Db.GetBackfillStatus("acct_1");
        Assert.Equal("complete", status!.Status);
    }

    // ═══ HistoryReconStatus ═════════════════════════════════════

    [Fact]
    public void HistoryRecon_lifecycle_pending_to_complete()
    {
        Db.EnqueueHistoryRecon("acct_1", 500);
        var status = Db.GetHistoryReconStatus("acct_1");
        Assert.NotNull(status);
        Assert.Equal("pending", status.Status);
        Assert.Equal(500, status.TotalSongsToProcess);

        Db.StartHistoryRecon("acct_1");
        status = Db.GetHistoryReconStatus("acct_1");
        Assert.Equal("in_progress", status!.Status);

        Db.UpdateHistoryReconProgress("acct_1", 250, 800, 50);
        status = Db.GetHistoryReconStatus("acct_1");
        Assert.Equal(250, status!.SongsProcessed);
        Assert.Equal(800, status.SeasonsQueried);
        Assert.Equal(50, status.HistoryEntriesFound);

        Db.CompleteHistoryRecon("acct_1");
        status = Db.GetHistoryReconStatus("acct_1");
        Assert.Equal("complete", status!.Status);
    }

    [Fact]
    public void HistoryRecon_error_state()
    {
        Db.EnqueueHistoryRecon("acct_1", 100);
        Db.StartHistoryRecon("acct_1");
        Db.FailHistoryRecon("acct_1", "Network error");

        var status = Db.GetHistoryReconStatus("acct_1");
        Assert.Equal("error", status!.Status);
        Assert.Equal("Network error", status.ErrorMessage);
    }

    [Fact]
    public void HistoryReconProgress_tracks_processed_pairs()
    {
        Db.MarkHistoryReconSongProcessed("acct_1", "song_1", "Solo_Guitar");
        Db.MarkHistoryReconSongProcessed("acct_1", "song_2", "Solo_Bass");

        var processed = Db.GetProcessedHistoryReconPairs("acct_1");
        Assert.Equal(2, processed.Count);
        Assert.Contains(("song_1", "Solo_Guitar"), processed);
        Assert.Contains(("song_2", "Solo_Bass"), processed);
    }

    [Fact]
    public void GetPendingHistoryRecons_returns_pending_and_in_progress()
    {
        Db.EnqueueHistoryRecon("acct_1", 100);
        Db.EnqueueHistoryRecon("acct_2", 200);
        Db.EnqueueHistoryRecon("acct_3", 300);
        Db.StartHistoryRecon("acct_3");
        Db.CompleteHistoryRecon("acct_3");

        var pending = Db.GetPendingHistoryRecons();
        Assert.Equal(2, pending.Count);
    }

    // ═══ SeasonWindows ══════════════════════════════════════════

    [Fact]
    public void UpsertSeasonWindow_and_GetSeasonWindows_roundtrip()
    {
        Db.UpsertSeasonWindow(1, "evt_1", "season_1");
        Db.UpsertSeasonWindow(2, "evt_2", "season_2");

        var windows = Db.GetSeasonWindows();
        Assert.Equal(2, windows.Count);
        Assert.Equal(1, windows[0].SeasonNumber);
        Assert.Equal("season_1", windows[0].WindowId);
        Assert.Equal(2, windows[1].SeasonNumber);
    }

    [Fact]
    public void UpsertSeasonWindow_updates_existing()
    {
        Db.UpsertSeasonWindow(1, "evt_1", "season_1");
        Db.UpsertSeasonWindow(1, "evt_1_updated", "season_1_new");

        var windows = Db.GetSeasonWindows();
        var window = windows.First(w => w.SeasonNumber == 1);
        Assert.Equal("evt_1_updated", window.EventId);
        Assert.Equal("season_1_new", window.WindowId);
    }

    // ═══ SongFirstSeenSeason ════════════════════════════════════

    [Fact]
    public void UpsertFirstSeenSeason_roundtrip()
    {
        Db.UpsertFirstSeenSeason("song_1", 5, 4, 5, "found_at_season_5", 2);
        var dict = Db.GetAllFirstSeenSeasons();
        Assert.Equal(5, dict["song_1"].FirstSeenSeason);
        Assert.Equal(2, dict["song_1"].CalculationVersion);
    }

    [Fact]
    public void GetSongIdsWithFirstSeenVersion_returns_matching_set()
    {
        Db.UpsertFirstSeenSeason("song_1", 5, 4, 5, null, 2);
        Db.UpsertFirstSeenSeason("song_2", null, 3, 3, "not_found", 1);

        var v2Set = Db.GetSongIdsWithFirstSeenVersion(2);
        Assert.Single(v2Set);
        Assert.Contains("song_1", v2Set);

        var v1Set = Db.GetSongIdsWithFirstSeenVersion(1);
        Assert.Single(v1Set);
        Assert.Contains("song_2", v1Set);
    }

    [Fact]
    public void GetAllFirstSeenSeasons_returns_dictionary()
    {
        Db.UpsertFirstSeenSeason("song_1", 5, 4, 5, null, 2);
        Db.UpsertFirstSeenSeason("song_2", null, 3, 3, null, 1);

        var dict = Db.GetAllFirstSeenSeasons();
        Assert.Equal(2, dict.Count);
        Assert.Equal(5, dict["song_1"].FirstSeenSeason);
        Assert.Equal(5, dict["song_1"].EstimatedSeason);
        Assert.Equal(2, dict["song_1"].CalculationVersion);
        Assert.Null(dict["song_2"].FirstSeenSeason);
        Assert.Equal(3, dict["song_2"].EstimatedSeason);
        Assert.Equal(1, dict["song_2"].CalculationVersion);
    }

    [Fact]
    public void UpsertFirstSeenSeason_updates_existing()
    {
        Db.UpsertFirstSeenSeason("song_1", 5, 4, 5, "initial", 1);
        Db.UpsertFirstSeenSeason("song_1", 3, 2, 3, "updated", 2);

        var dict = Db.GetAllFirstSeenSeasons();
        Assert.Equal(3, dict["song_1"].FirstSeenSeason);
        Assert.Equal(3, dict["song_1"].EstimatedSeason);
        Assert.Equal(2, dict["song_1"].CalculationVersion);
    }

    [Fact]
    public void UpsertFirstSeenSeason_nullable_firstSeen()
    {
        Db.UpsertFirstSeenSeason("song_1", null, 3, 3, null, 2);

        var dict = Db.GetAllFirstSeenSeasons();
        Assert.Null(dict["song_1"].FirstSeenSeason);
        Assert.Equal(3, dict["song_1"].EstimatedSeason);
    }

    // ═══ RegisterUser / UnregisterUser ══════════════════════════

    [Fact]
    public void RegisterUser_returns_true_for_new()
    {
        var isNew = Db.RegisterUser("dev1", "acct1");
        Assert.True(isNew);
    }

    [Fact]
    public void RegisterUser_returns_false_for_duplicate()
    {
        Db.RegisterUser("dev1", "acct1");
        var isNew = Db.RegisterUser("dev1", "acct1");
        Assert.False(isNew);
    }

    [Fact]
    public void UnregisterUser_returns_true_when_removed()
    {
        Db.RegisterUser("dev1", "acct1");
        var removed = Db.UnregisterUser("dev1", "acct1");
        Assert.True(removed);
    }

    [Fact]
    public void UnregisterUser_returns_false_when_not_found()
    {
        var removed = Db.UnregisterUser("dev1", "acct1");
        Assert.False(removed);
    }

    [Fact]
    public void UnregisterUser_last_device_cascades_to_full_cleanup()
    {
        Db.RegisterUser("dev1", "acct1");
        Db.UpsertPlayerStats(new PlayerStatsDto
        {
            AccountId = "acct1", Instrument = "Solo_Guitar", SongsPlayed = 10,
        });
        Db.EnqueueBackfill("acct1", 50);
        Db.EnqueueHistoryRecon("acct1", 50);

        var removed = Db.UnregisterUser("dev1", "acct1");
        Assert.True(removed);

        // All per-account data should be cleaned up
        Assert.Empty(Db.GetPlayerStats("acct1"));
        Assert.Null(Db.GetBackfillStatus("acct1"));
        Assert.Null(Db.GetHistoryReconStatus("acct1"));
    }

    [Fact]
    public void UnregisterUser_not_last_device_does_not_cascade()
    {
        Db.RegisterUser("dev1", "acct1");
        Db.RegisterUser("dev2", "acct1");
        Db.UpsertPlayerStats(new PlayerStatsDto
        {
            AccountId = "acct1", Instrument = "Solo_Guitar", SongsPlayed = 10,
        });
        Db.EnqueueBackfill("acct1", 50);

        var removed = Db.UnregisterUser("dev1", "acct1");
        Assert.True(removed);

        // Per-account data should still exist (dev2 still registered)
        Assert.Single(Db.GetPlayerStats("acct1"));
        Assert.NotNull(Db.GetBackfillStatus("acct1"));
        Assert.Contains("acct1", Db.GetRegisteredAccountIds());
    }

    [Fact]
    public void TouchWebRegistrationActivity_refreshes_stale_web_registration()
    {
        Db.RegisterUser("web-tracker", "acct1");
        SetWebRegistrationActivity("acct1", DateTime.UtcNow.AddHours(-8));

        Db.TouchWebRegistrationActivity("acct1");

        var pruned = Db.PruneStaleWebRegistrations(DateTime.UtcNow.AddHours(-4));
        Assert.Equal(0, pruned);
        Assert.Contains("acct1", Db.GetRegisteredAccountIds());
    }

    [Fact]
    public void PruneStaleWebRegistrations_removes_only_web_tracker_rows_non_destructively()
    {
        Db.RegisterUser("web-tracker", "acct1");
        Db.UpsertPlayerStats(new PlayerStatsDto
        {
            AccountId = "acct1", Instrument = "Solo_Guitar", SongsPlayed = 10,
        });
        SetWebRegistrationActivity("acct1", DateTime.UtcNow.AddHours(-8));

        var pruned = Db.PruneStaleWebRegistrations(DateTime.UtcNow.AddHours(-4));

        Assert.Equal(1, pruned);
        Assert.DoesNotContain("acct1", Db.GetRegisteredAccountIds());
        Assert.Single(Db.GetPlayerStats("acct1"));
    }

    [Fact]
    public void PruneStaleWebRegistrations_preserves_non_web_registrations_for_same_account()
    {
        Db.RegisterUser("web-tracker", "acct1");
        Db.RegisterUser("mobile-device", "acct1");
        SetWebRegistrationActivity("acct1", DateTime.UtcNow.AddHours(-8));

        var pruned = Db.PruneStaleWebRegistrations(DateTime.UtcNow.AddHours(-4));

        Assert.Equal(1, pruned);
        Assert.Contains("acct1", Db.GetRegisteredAccountIds());
    }

    private void SetWebRegistrationActivity(string accountId, DateTime lastActivityAt)
    {
        using var conn = DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE registered_users SET last_activity_at = @lastActivityAt, registered_at = @registeredAt WHERE device_id = @deviceId AND account_id = @accountId";
        cmd.Parameters.AddWithValue("deviceId", "web-tracker");
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("lastActivityAt", lastActivityAt);
        cmd.Parameters.AddWithValue("registeredAt", lastActivityAt);
        cmd.ExecuteNonQuery();
    }

    // ═══ GetAllFirstSeenSeasons ═════════════════════════════════

    [Fact]
    public void GetAllFirstSeenSeasons_returns_all_entries()
    {
        Db.UpsertFirstSeenSeason("song_a", 3, 2, 3, "found", 2);
        Db.UpsertFirstSeenSeason("song_b", null, null, 5, "estimated", 2);

        var all = Db.GetAllFirstSeenSeasons();
        Assert.Equal(2, all.Count);
        Assert.Equal(3, all["song_a"].FirstSeenSeason);
        Assert.Null(all["song_b"].FirstSeenSeason);
        Assert.Equal(5, all["song_b"].EstimatedSeason);
    }

    // ═══ InsertScoreChange with full params ═════════════════════

    [Fact]
    public void InsertScoreChange_with_all_optional_params()
    {
        Db.InsertScoreChange("song1", "Solo_Guitar", "acct1",
            oldScore: 50000, newScore: 100000, oldRank: 100, newRank: 50,
            accuracy: 95, isFullCombo: true, stars: 5, percentile: 99.5,
            season: 3, scoreAchievedAt: "2025-01-15T12:00:00Z",
            seasonRank: 10, allTimeRank: 25);

        var history = Db.GetScoreHistory("acct1", limit: 10);
        Assert.Single(history);
        var entry = history[0];
        Assert.Equal(50000, entry.OldScore);
        Assert.Equal(100000, entry.NewScore);
        Assert.Equal(95, entry.Accuracy);
        Assert.True(entry.IsFullCombo);
        Assert.Equal(5, entry.Stars);
        Assert.Equal(99.5, entry.Percentile);
        Assert.Equal(3, entry.Season);
        Assert.Equal(10, entry.SeasonRank);
        Assert.Equal(25, entry.AllTimeRank);
    }

    // ═══ GetDisplayName ═════════════════════════════════════════

    [Fact]
    public void GetDisplayName_returns_resolved_name()
    {
        Db.InsertAccountNames([("acct_dn", "DisplayUser")]);
        Assert.Equal("DisplayUser", Db.GetDisplayName("acct_dn"));
    }

    [Fact]
    public void GetDisplayName_returns_null_for_unknown()
    {
        Assert.Null(Db.GetDisplayName("nobody"));
    }

    // ═══ LeaderboardPopulation ══════════════════════════════════

    [Fact]
    public void UpsertLeaderboardPopulation_inserts_and_queries()
    {
        var items = new List<(string, string, long)>
        {
            ("song1", "Solo_Guitar", 100_000),
            ("song2", "Solo_Drums", 50_000),
        };

        Db.UpsertLeaderboardPopulation(items);

        Assert.Equal(100_000, Db.GetLeaderboardPopulation("song1", "Solo_Guitar"));
        Assert.Equal(50_000, Db.GetLeaderboardPopulation("song2", "Solo_Drums"));
    }

    [Fact]
    public void GetLeaderboardPopulation_returns_minus1_when_not_found()
    {
        Assert.Equal(-1, Db.GetLeaderboardPopulation("missing", "Solo_Guitar"));
    }

    [Fact]
    public void UpsertLeaderboardPopulation_updates_existing()
    {
        Db.UpsertLeaderboardPopulation([("song1", "Solo_Guitar", 100_000)]);
        Db.UpsertLeaderboardPopulation([("song1", "Solo_Guitar", 200_000)]);

        Assert.Equal(200_000, Db.GetLeaderboardPopulation("song1", "Solo_Guitar"));
    }

    [Fact]
    public void GetAllLeaderboardPopulation_returns_all_entries()
    {
        var items = new List<(string, string, long)>
        {
            ("songA", "Solo_Guitar", 10),
            ("songA", "Solo_Bass", 20),
            ("songB", "Solo_Guitar", 30),
        };

        Db.UpsertLeaderboardPopulation(items);
        var all = Db.GetAllLeaderboardPopulation();

        Assert.Equal(3, all.Count);
        Assert.Equal(10, all[("songA", "Solo_Guitar")]);
        Assert.Equal(20, all[("songA", "Solo_Bass")]);
        Assert.Equal(30, all[("songB", "Solo_Guitar")]);
    }

    [Fact]
    public void UpsertLeaderboardPopulation_empty_list_no_op()
    {
        Db.UpsertLeaderboardPopulation([]); // should not throw
        var all = Db.GetAllLeaderboardPopulation();
        Assert.Empty(all);
    }

    // ═══ RaiseLeaderboardPopulationFloor ════════════════════════

    [Fact]
    public void RaisePopulationFloor_inserts_when_no_existing_data()
    {
        Db.RaiseLeaderboardPopulationFloor("song1", "Solo_Guitar", 150_000);
        Assert.Equal(150_000, Db.GetLeaderboardPopulation("song1", "Solo_Guitar"));
    }

    [Fact]
    public void RaisePopulationFloor_raises_when_higher()
    {
        Db.UpsertLeaderboardPopulation([("song1", "Solo_Guitar", 100_000)]);
        Db.RaiseLeaderboardPopulationFloor("song1", "Solo_Guitar", 200_000);
        Assert.Equal(200_000, Db.GetLeaderboardPopulation("song1", "Solo_Guitar"));
    }

    [Fact]
    public void RaisePopulationFloor_does_not_lower_existing()
    {
        Db.UpsertLeaderboardPopulation([("song1", "Solo_Guitar", 300_000)]);
        Db.RaiseLeaderboardPopulationFloor("song1", "Solo_Guitar", 100_000);
        Assert.Equal(300_000, Db.GetLeaderboardPopulation("song1", "Solo_Guitar"));
    }

    [Fact]
    public void RaisePopulationFloor_ignores_zero_and_negative()
    {
        Db.RaiseLeaderboardPopulationFloor("song1", "Solo_Guitar", 0);
        Assert.Equal(-1, Db.GetLeaderboardPopulation("song1", "Solo_Guitar"));

        Db.RaiseLeaderboardPopulationFloor("song1", "Solo_Guitar", -5);
        Assert.Equal(-1, Db.GetLeaderboardPopulation("song1", "Solo_Guitar"));
    }

    // ═══ PlayerStats ════════════════════════════════════════════

    [Fact]
    public void UpsertPlayerStats_inserts_new_row()
    {
        Db.UpsertPlayerStats(new Persistence.PlayerStatsDto
        {
            AccountId = "acct_1",
            Instrument = "Solo_Guitar",
            SongsPlayed = 50,
            FullComboCount = 10,
            GoldStarCount = 5,
            AvgAccuracy = 95.5,
            BestRank = 3,
            BestRankSongId = "song_best",
            TotalScore = 5_000_000,
            PercentileDist = "{\"1\":2,\"5\":10}",
            AvgPercentile = "Top 3%",
            OverallPercentile = "Top 10%",
        });

        var stats = Db.GetPlayerStats("acct_1");
        Assert.Single(stats);
        var s = stats[0];
        Assert.Equal("Solo_Guitar", s.Instrument);
        Assert.Equal(50, s.SongsPlayed);
        Assert.Equal(10, s.FullComboCount);
        Assert.Equal(5, s.GoldStarCount);
        Assert.Equal(95.5, s.AvgAccuracy, 0.01);
        Assert.Equal(3, s.BestRank);
        Assert.Equal("song_best", s.BestRankSongId);
        Assert.Equal(5_000_000, s.TotalScore);
        Assert.Equal("{\"1\":2,\"5\":10}", s.PercentileDist);
        Assert.Equal("Top 3%", s.AvgPercentile);
        Assert.Equal("Top 10%", s.OverallPercentile);
    }

    [Fact]
    public void UpsertPlayerStats_updates_existing_row()
    {
        Db.UpsertPlayerStats(new Persistence.PlayerStatsDto
        {
            AccountId = "acct_1",
            Instrument = "Solo_Guitar",
            SongsPlayed = 50,
            FullComboCount = 10,
        });
        Db.UpsertPlayerStats(new Persistence.PlayerStatsDto
        {
            AccountId = "acct_1",
            Instrument = "Solo_Guitar",
            SongsPlayed = 60,
            FullComboCount = 20,
        });

        var stats = Db.GetPlayerStats("acct_1");
        Assert.Single(stats);
        Assert.Equal(60, stats[0].SongsPlayed);
        Assert.Equal(20, stats[0].FullComboCount);
    }

    [Fact]
    public void GetPlayerStats_returns_multiple_instruments()
    {
        Db.UpsertPlayerStats(new Persistence.PlayerStatsDto
        {
            AccountId = "acct_1",
            Instrument = "Solo_Guitar",
            SongsPlayed = 50,
        });
        Db.UpsertPlayerStats(new Persistence.PlayerStatsDto
        {
            AccountId = "acct_1",
            Instrument = "Solo_Bass",
            SongsPlayed = 30,
        });

        var stats = Db.GetPlayerStats("acct_1");
        Assert.Equal(2, stats.Count);
    }

    [Fact]
    public void GetPlayerStats_returns_empty_for_unknown_account()
    {
        var stats = Db.GetPlayerStats("nobody");
        Assert.Empty(stats);
    }

    [Fact]
    public void UpsertPlayerStats_handles_null_optional_fields()
    {
        Db.UpsertPlayerStats(new Persistence.PlayerStatsDto
        {
            AccountId = "acct_1",
            Instrument = "Overall",
            SongsPlayed = 10,
            BestRankSongId = null,
            PercentileDist = null,
            AvgPercentile = null,
            OverallPercentile = null,
        });

        var stats = Db.GetPlayerStats("acct_1");
        Assert.Single(stats);
        Assert.Null(stats[0].BestRankSongId);
        Assert.Null(stats[0].PercentileDist);
    }

    // ═══ Checkpoint ═════════════════════════════════════════════

    [Fact]
    public void Checkpoint_succeeds_after_writes()
    {
        Db.StartScrapeRun();

        // Should not throw
        Db.Checkpoint();
    }

    [Fact]
    public void Checkpoint_succeeds_on_empty_database()
    {
        // Should not throw even when there's nothing to checkpoint
        Db.Checkpoint();
    }

    // ═══ GetCompositeRankingNeighborhood ═════════════════════

    private void SeedCompositeRankings(params (string AccountId, double Rating, int Rank)[] accounts)
    {
        Db.ReplaceCompositeRankings(accounts.Select(a => new CompositeRankingDto
        {
            AccountId = a.AccountId,
            InstrumentsPlayed = 2,
            TotalSongsPlayed = 50,
            CompositeRating = a.Rating,
            CompositeRank = a.Rank,
            ComputedAt = "2025-01-01T00:00:00Z",
        }).ToList());
    }

    [Fact]
    public void GetCompositeRankingNeighborhood_returns_above_self_below()
    {
        SeedCompositeRankings(
            ("a1", 0.1, 1), ("a2", 0.2, 2), ("a3", 0.3, 3),
            ("a4", 0.4, 4), ("a5", 0.5, 5));

        var (above, self, below) = Db.GetCompositeRankingNeighborhood("a3", radius: 2);

        Assert.NotNull(self);
        Assert.Equal("a3", self.AccountId);
        Assert.Equal(3, self.CompositeRank);
        Assert.Equal(2, above.Count);
        Assert.Equal("a1", above[0].AccountId);
        Assert.Equal("a2", above[1].AccountId);
        Assert.Equal(2, below.Count);
        Assert.Equal("a4", below[0].AccountId);
        Assert.Equal("a5", below[1].AccountId);
    }

    [Fact]
    public void GetCompositeRankingNeighborhood_rank1_has_no_above()
    {
        SeedCompositeRankings(
            ("a1", 0.1, 1), ("a2", 0.2, 2), ("a3", 0.3, 3));

        var (above, self, below) = Db.GetCompositeRankingNeighborhood("a1", radius: 2);

        Assert.NotNull(self);
        Assert.Equal("a1", self.AccountId);
        Assert.Empty(above);
        Assert.Equal(2, below.Count);
    }

    [Fact]
    public void GetCompositeRankingNeighborhood_last_rank_has_no_below()
    {
        SeedCompositeRankings(
            ("a1", 0.1, 1), ("a2", 0.2, 2), ("a3", 0.3, 3));

        var (above, self, below) = Db.GetCompositeRankingNeighborhood("a3", radius: 2);

        Assert.NotNull(self);
        Assert.Equal("a3", self.AccountId);
        Assert.Equal(2, above.Count);
        Assert.Empty(below);
    }

    [Fact]
    public void GetCompositeRankingNeighborhood_unknown_account_returns_nulls()
    {
        SeedCompositeRankings(("a1", 0.1, 1));

        var (above, self, below) = Db.GetCompositeRankingNeighborhood("unknown");

        Assert.Null(self);
        Assert.Empty(above);
        Assert.Empty(below);
    }

    [Fact]
    public void GetCompositeRankingNeighborhood_default_radius_is_5()
    {
        var accounts = Enumerable.Range(1, 11)
            .Select(i => ($"a{i}", (double)i * 0.1, i))
            .ToArray();
        SeedCompositeRankings(accounts);

        var (above, self, below) = Db.GetCompositeRankingNeighborhood("a6");

        Assert.NotNull(self);
        Assert.Equal(5, above.Count);
        Assert.Equal(5, below.Count);
    }

    // ═══ GetBestValidScores ═════════════════════════════════════

    [Fact]
    public void GetBestValidScores_returns_highest_valid_score()
    {
        // Insert multiple score history entries: 80k, 90k, and 110k (invalid)
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 80_000, null, 3,
            accuracy: 90, isFullCombo: false, stars: 5, scoreAchievedAt: "2025-01-01T00:00:00Z");
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", 80_000, 90_000, 3, 2,
            accuracy: 95, isFullCombo: true, stars: 6, scoreAchievedAt: "2025-02-01T00:00:00Z");
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", 90_000, 110_000, 2, 1,
            accuracy: 98, isFullCombo: true, stars: 6, scoreAchievedAt: "2025-03-01T00:00:00Z");

        var thresholds = new Dictionary<(string, string), int>
        {
            [("song_1", "Solo_Guitar")] = 100_000, // 110k is invalid
        };
        var result = Db.GetBestValidScores("acct_1", thresholds);

        Assert.Single(result);
        var fallback = result[("song_1", "Solo_Guitar")];
        Assert.Equal(90_000, fallback.Score);
        Assert.Equal(95, fallback.Accuracy);
        Assert.True(fallback.IsFullCombo);
        Assert.Equal(6, fallback.Stars);
    }

    [Fact]
    public void GetBestValidScores_returns_empty_when_no_valid_scores()
    {
        // Only one score, and it's invalid
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 110_000, null, 1,
            accuracy: 98, scoreAchievedAt: "2025-01-01T00:00:00Z");

        var thresholds = new Dictionary<(string, string), int>
        {
            [("song_1", "Solo_Guitar")] = 100_000,
        };
        var result = Db.GetBestValidScores("acct_1", thresholds);

        Assert.Empty(result);
    }

    [Fact]
    public void GetBestValidScores_returns_empty_for_empty_thresholds()
    {
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 90_000, null, 1,
            scoreAchievedAt: "2025-01-01T00:00:00Z");

        var result = Db.GetBestValidScores("acct_1", new Dictionary<(string, string), int>());
        Assert.Empty(result);
    }

    [Fact]
    public void GetBestValidScores_handles_multiple_instruments()
    {
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 90_000, null, 1,
            accuracy: 95, isFullCombo: true, stars: 6, scoreAchievedAt: "2025-01-01T00:00:00Z");
        Db.InsertScoreChange("song_1", "Solo_Bass", "acct_1", null, 85_000, null, 2,
            accuracy: 90, isFullCombo: false, stars: 5, scoreAchievedAt: "2025-01-01T00:00:00Z");

        var thresholds = new Dictionary<(string, string), int>
        {
            [("song_1", "Solo_Guitar")] = 100_000,
            [("song_1", "Solo_Bass")] = 100_000,
        };
        var result = Db.GetBestValidScores("acct_1", thresholds);

        Assert.Equal(2, result.Count);
        Assert.Equal(90_000, result[("song_1", "Solo_Guitar")].Score);
        Assert.Equal(85_000, result[("song_1", "Solo_Bass")].Score);
    }

    // ═══ GetBulkBestValidScores ═════════════════════════════════

    [Fact]
    public void GetBulkBestValidScores_ReturnsHighestValidPerEntry()
    {
        // acct_1 on song_1: 80k, 90k (valid), 110k (invalid)
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 80_000, null, 3,
            accuracy: 90, isFullCombo: false, stars: 5, scoreAchievedAt: "2025-01-01T00:00:00Z");
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", 80_000, 90_000, 3, 2,
            accuracy: 95, isFullCombo: true, stars: 6, scoreAchievedAt: "2025-02-01T00:00:00Z");
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", 90_000, 110_000, 2, 1,
            accuracy: 98, isFullCombo: true, stars: 6, scoreAchievedAt: "2025-03-01T00:00:00Z");

        // acct_2 on song_2: 50k (valid)
        Db.InsertScoreChange("song_2", "Solo_Guitar", "acct_2", null, 50_000, null, 5,
            accuracy: 85, isFullCombo: false, stars: 4, scoreAchievedAt: "2025-01-01T00:00:00Z");

        var entries = new Dictionary<(string, string), int>
        {
            [("acct_1", "song_1")] = 100_000, // 110k exceeds, 90k is best valid
            [("acct_2", "song_2")] = 100_000,
        };
        var result = Db.GetBulkBestValidScores("Solo_Guitar", entries);

        Assert.Equal(2, result.Count);
        Assert.Equal(90_000, result[("acct_1", "song_1")].Score);
        Assert.Equal(95, result[("acct_1", "song_1")].Accuracy);
        Assert.True(result[("acct_1", "song_1")].IsFullCombo);
        Assert.Equal(50_000, result[("acct_2", "song_2")].Score);
    }

    [Fact]
    public void GetBulkBestValidScores_SkipsEntriesAboveThreshold()
    {
        // Only score is above threshold
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 110_000, null, 1,
            accuracy: 98, scoreAchievedAt: "2025-01-01T00:00:00Z");

        var entries = new Dictionary<(string, string), int>
        {
            [("acct_1", "song_1")] = 100_000,
        };
        var result = Db.GetBulkBestValidScores("Solo_Guitar", entries);
        Assert.Empty(result);
    }

    [Fact]
    public void GetBulkBestValidScores_EmptyInput_ReturnsEmpty()
    {
        var result = Db.GetBulkBestValidScores("Solo_Guitar", new Dictionary<(string, string), int>());
        Assert.Empty(result);
    }

    [Fact]
    public void GetBulkBestValidScores_FiltersbyInstrument()
    {
        // Same account/song but different instruments
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 90_000, null, 1,
            accuracy: 95, scoreAchievedAt: "2025-01-01T00:00:00Z");
        Db.InsertScoreChange("song_1", "Solo_Bass", "acct_1", null, 70_000, null, 1,
            accuracy: 85, scoreAchievedAt: "2025-01-01T00:00:00Z");

        var entries = new Dictionary<(string, string), int>
        {
            [("acct_1", "song_1")] = 100_000,
        };

        var guitarResult = Db.GetBulkBestValidScores("Solo_Guitar", entries);
        Assert.Single(guitarResult);
        Assert.Equal(90_000, guitarResult[("acct_1", "song_1")].Score);

        var bassResult = Db.GetBulkBestValidScores("Solo_Bass", entries);
        Assert.Single(bassResult);
        Assert.Equal(70_000, bassResult[("acct_1", "song_1")].Score);
    }

    // ═══ Leaderboard Rivals ═════════════════════════════════════

    [Fact]
    public void ReplaceLeaderboardRivalsData_persists_and_reads_back()
    {
        var rivals = new List<Persistence.LeaderboardRivalRow>
        {
            new()
            {
                UserId = "u1", RivalAccountId = "r1", Instrument = "Solo_Guitar",
                RankMethod = "totalscore", Direction = "above",
                UserRank = 10, RivalRank = 8, SharedSongCount = 5,
                AheadCount = 3, BehindCount = 2, AvgSignedDelta = -1.5,
                ComputedAt = "2026-01-01T00:00:00Z",
            },
        };
        var samples = new List<Persistence.LeaderboardRivalSongSampleRow>
        {
            new()
            {
                UserId = "u1", RivalAccountId = "r1", Instrument = "Solo_Guitar",
                RankMethod = "totalscore", SongId = "s1",
                UserRank = 10, RivalRank = 8, RankDelta = -2,
                UserScore = 1000, RivalScore = 1100,
            },
        };

        Db.ReplaceLeaderboardRivalsData("u1", "Solo_Guitar", rivals, samples);

        var readRivals = Db.GetLeaderboardRivals("u1", "Solo_Guitar", "totalscore");
        Assert.Single(readRivals);
        Assert.Equal("r1", readRivals[0].RivalAccountId);
        Assert.Equal(5, readRivals[0].SharedSongCount);

        var readSamples = Db.GetLeaderboardRivalSongSamples("u1", "r1", "Solo_Guitar", "totalscore");
        Assert.Single(readSamples);
        Assert.Equal("s1", readSamples[0].SongId);
        Assert.Equal(-2, readSamples[0].RankDelta);
    }

    [Fact]
    public void ReplaceLeaderboardRivalsData_replaces_existing_data()
    {
        var initial = new List<Persistence.LeaderboardRivalRow>
        {
            new()
            {
                UserId = "u1", RivalAccountId = "old_rival", Instrument = "Solo_Guitar",
                RankMethod = "totalscore", Direction = "below",
                UserRank = 5, RivalRank = 7, SharedSongCount = 3,
                AheadCount = 2, BehindCount = 1, AvgSignedDelta = 2.0,
                ComputedAt = "2026-01-01T00:00:00Z",
            },
        };

        Db.ReplaceLeaderboardRivalsData("u1", "Solo_Guitar", initial, []);

        var updated = new List<Persistence.LeaderboardRivalRow>
        {
            new()
            {
                UserId = "u1", RivalAccountId = "new_rival", Instrument = "Solo_Guitar",
                RankMethod = "totalscore", Direction = "above",
                UserRank = 5, RivalRank = 3, SharedSongCount = 10,
                AheadCount = 4, BehindCount = 6, AvgSignedDelta = -3.0,
                ComputedAt = "2026-01-02T00:00:00Z",
            },
        };

        Db.ReplaceLeaderboardRivalsData("u1", "Solo_Guitar", updated, []);

        var readRivals = Db.GetLeaderboardRivals("u1", "Solo_Guitar");
        Assert.Single(readRivals);
        Assert.Equal("new_rival", readRivals[0].RivalAccountId);
    }
}
