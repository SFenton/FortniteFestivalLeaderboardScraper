using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class MetaDatabaseTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    private Persistence.MetaDatabase Db => _fixture.Db;

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
    public void RegisterOrUpdateUser_returns_true_on_first_insert()
    {
        var isNew = Db.RegisterOrUpdateUser("dev_1", "acct_1", "Player", "iOS");
        Assert.True(isNew);
    }

    [Fact]
    public void RegisterOrUpdateUser_returns_false_on_duplicate()
    {
        Db.RegisterOrUpdateUser("dev_1", "acct_1", "Player", "iOS");
        var isNew = Db.RegisterOrUpdateUser("dev_1", "acct_1", "Player", "iOS");
        Assert.False(isNew);
    }

    [Fact]
    public void GetRegisteredAccountIds_returns_distinct_accounts()
    {
        Db.RegisterOrUpdateUser("dev_1", "acct_1", "P1", null);
        Db.RegisterOrUpdateUser("dev_2", "acct_1", "P1", null);
        Db.RegisterOrUpdateUser("dev_3", "acct_2", "P2", null);

        var ids = Db.GetRegisteredAccountIds();
        Assert.Equal(2, ids.Count);
        Assert.Contains("acct_1", ids);
        Assert.Contains("acct_2", ids);
    }

    [Fact]
    public void GetRegistrationInfo_returns_details()
    {
        Db.RegisterOrUpdateUser("dev_1", "acct_1", "Player", "Android");
        var info = Db.GetRegistrationInfo("acct_1", "dev_1");
        Assert.NotNull(info);
        Assert.Equal("acct_1", info.AccountId);
        Assert.Equal("Player", info.DisplayName);
    }

    [Fact]
    public void IsDeviceRegistered_reflects_state()
    {
        Assert.False(Db.IsDeviceRegistered("dev_1"));
        Db.RegisterOrUpdateUser("dev_1", "acct_1", "P1", null);
        Assert.True(Db.IsDeviceRegistered("dev_1"));
    }

    [Fact]
    public void UnregisterAccount_removes_all_devices_and_returns_ids()
    {
        Db.RegisterOrUpdateUser("dev_1", "acct_1", "P1", null);
        Db.RegisterOrUpdateUser("dev_2", "acct_1", "P1", null);
        Db.RegisterOrUpdateUser("dev_3", "acct_2", "P2", null);

        var removed = Db.UnregisterAccount("acct_1");
        Assert.Equal(2, removed.Count);
        Assert.Contains("dev_1", removed);
        Assert.Contains("dev_2", removed);

        // acct_2 should be unaffected
        Assert.True(Db.IsDeviceRegistered("dev_3"));
        Assert.False(Db.IsDeviceRegistered("dev_1"));
    }

    [Fact]
    public void UnregisterAccount_returns_empty_for_unknown_account()
    {
        var removed = Db.UnregisterAccount("nonexistent");
        Assert.Empty(removed);
    }

    [Fact]
    public void GetOrphanedRegisteredAccounts_finds_accounts_with_expired_sessions()
    {
        // Register two accounts and give them sessions
        Db.RegisterOrUpdateUser("dev_1", "acct_1", "Player1", null);
        Db.RegisterOrUpdateUser("dev_2", "acct_2", "Player2", null);

        // acct_1's session is expired, acct_2's is active
        Db.InsertAccountNames([("acct_1", "Player1"), ("acct_2", "Player2")]);
        Db.InsertSession("Player1", "dev_1", "hash_1", null, DateTime.UtcNow.AddDays(-5)); // expired
        Db.InsertSession("Player2", "dev_2", "hash_2", null, DateTime.UtcNow.AddDays(30)); // active

        var orphaned = Db.GetOrphanedRegisteredAccounts();
        Assert.Single(orphaned);
        Assert.Equal("acct_1", orphaned[0]);
    }

    [Fact]
    public void GetOrphanedRegisteredAccounts_excludes_accounts_that_never_had_sessions()
    {
        // Register an account but never create a session for it
        Db.RegisterOrUpdateUser("dev_1", "acct_1", "Player1", null);
        Db.InsertAccountNames([("acct_1", "Player1")]);

        var orphaned = Db.GetOrphanedRegisteredAccounts();
        Assert.Empty(orphaned);
    }

    [Fact]
    public void GetOrphanedRegisteredAccounts_returns_empty_when_all_sessions_active()
    {
        Db.RegisterOrUpdateUser("dev_1", "acct_1", "Player1", null);
        Db.InsertAccountNames([("acct_1", "Player1")]);
        Db.InsertSession("Player1", "dev_1", "hash_1", null, DateTime.UtcNow.AddDays(30));

        var orphaned = Db.GetOrphanedRegisteredAccounts();
        Assert.Empty(orphaned);
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

    // ═══ UserSessions ═══════════════════════════════════════════

    [Fact]
    public void InsertSession_and_GetActiveSession_roundtrip()
    {
        var sessionId = Db.InsertSession("player1", "dev_1", "hash_abc", "iOS",
            DateTime.UtcNow.AddDays(30));
        Assert.True(sessionId > 0);

        var session = Db.GetActiveSession("hash_abc");
        Assert.NotNull(session);
        Assert.Equal("player1", session.Username);
        Assert.Equal("dev_1", session.DeviceId);
    }

    [Fact]
    public void RevokeSession_makes_it_inactive()
    {
        Db.InsertSession("player1", "dev_1", "hash_abc", null, DateTime.UtcNow.AddDays(30));
        Db.RevokeSession("hash_abc");

        var session = Db.GetActiveSession("hash_abc");
        Assert.Null(session);
    }

    [Fact]
    public void RevokeAllSessions_revokes_all_for_user()
    {
        Db.InsertSession("player1", "dev_1", "hash_1", null, DateTime.UtcNow.AddDays(30));
        Db.InsertSession("player1", "dev_2", "hash_2", null, DateTime.UtcNow.AddDays(30));
        Db.InsertSession("other", "dev_3", "hash_3", null, DateTime.UtcNow.AddDays(30));

        Db.RevokeAllSessions("player1");

        Assert.Null(Db.GetActiveSession("hash_1"));
        Assert.Null(Db.GetActiveSession("hash_2"));
        Assert.NotNull(Db.GetActiveSession("hash_3"));
    }

    [Fact]
    public void CleanupExpiredSessions_removes_old_sessions()
    {
        Db.InsertSession("player1", "dev_1", "hash_old", null, DateTime.UtcNow.AddDays(-10));
        Db.InsertSession("player1", "dev_1", "hash_new", null, DateTime.UtcNow.AddDays(30));

        var cleaned = Db.CleanupExpiredSessions(DateTime.UtcNow);
        Assert.Equal(1, cleaned);
        Assert.Null(Db.GetActiveSession("hash_old"));
        Assert.NotNull(Db.GetActiveSession("hash_new"));
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

    // ═══ DataCollectionVersion ═════════════════════════════════

    [Fact]
    public void EnsureSchema_sets_data_collection_version()
    {
        Assert.Equal(Persistence.MetaDatabase.DataCollectionVersion, Db.GetDataCollectionVersion());
    }

    [Fact]
    public void Version_upgrade_resets_completed_backfill_and_history_recon()
    {
        // Simulate two users: one completed, one in-progress
        Db.EnqueueBackfill("acct_done", 100);
        Db.StartBackfill("acct_done");
        Db.MarkBackfillSongChecked("acct_done", "song_1", "Solo_Guitar", true);
        Db.CompleteBackfill("acct_done");

        Db.EnqueueHistoryRecon("acct_done", 50);
        Db.StartHistoryRecon("acct_done");
        Db.MarkHistoryReconSongProcessed("acct_done", "song_1", "Solo_Guitar");
        Db.CompleteHistoryRecon("acct_done");

        Db.EnqueueBackfill("acct_wip", 100);
        Db.StartBackfill("acct_wip");

        // Downgrade the stored version to simulate a pre-upgrade DB
        using (var conn = new SqliteConnection($"Data Source={_fixture.DbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE DataVersion SET Version = 0 WHERE Key = 'DataCollection';";
            cmd.ExecuteNonQuery();
        }

        // Re-run schema (simulates service restart after code upgrade)
        Db.ResetInitialized();
        Db.EnsureSchema();

        // Completed user should be reset to pending with cleared progress
        var bf = Db.GetBackfillStatus("acct_done");
        Assert.Equal("pending", bf!.Status);
        Assert.Equal(0, bf.SongsChecked);
        Assert.Empty(Db.GetCheckedBackfillPairs("acct_done"));

        var hr = Db.GetHistoryReconStatus("acct_done");
        Assert.Equal("pending", hr!.Status);
        Assert.Equal(0, hr.SongsProcessed);
        Assert.Empty(Db.GetProcessedHistoryReconPairs("acct_done"));

        // In-progress user should be untouched (only 'complete' gets reset)
        var wip = Db.GetBackfillStatus("acct_wip");
        Assert.Equal("in_progress", wip!.Status);

        // Version should now match the constant
        Assert.Equal(Persistence.MetaDatabase.DataCollectionVersion, Db.GetDataCollectionVersion());
    }

    [Fact]
    public void Version_upgrade_is_idempotent_when_already_current()
    {
        // Complete a user
        Db.EnqueueBackfill("acct_1", 100);
        Db.StartBackfill("acct_1");
        Db.CompleteBackfill("acct_1");

        // Re-run schema — version is already current, so nothing should change
        Db.ResetInitialized();
        Db.EnsureSchema();

        var bf = Db.GetBackfillStatus("acct_1");
        Assert.Equal("complete", bf!.Status);
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

        var window = Db.GetSeasonWindow(1);
        Assert.NotNull(window);
        Assert.Equal("evt_1_updated", window.EventId);
        Assert.Equal("season_1_new", window.WindowId);
    }

    [Fact]
    public void GetSeasonWindow_returns_null_for_unknown()
    {
        var window = Db.GetSeasonWindow(99);
        Assert.Null(window);
    }

    // ═══ GetKnownAccountIds ═════════════════════════════════════

    [Fact]
    public void GetKnownAccountIds_returns_empty_when_no_accounts()
    {
        var ids = Db.GetKnownAccountIds();
        Assert.Empty(ids);
    }

    [Fact]
    public void GetKnownAccountIds_returns_all_account_ids()
    {
        Db.InsertAccountIds(["acct_1", "acct_2", "acct_3"]);
        // Resolve one of them to ensure both resolved and unresolved are returned
        Db.InsertAccountNames([("acct_1", "Player One")]);

        var ids = Db.GetKnownAccountIds();
        Assert.Equal(3, ids.Count);
        Assert.Contains("acct_1", ids);
        Assert.Contains("acct_2", ids);
        Assert.Contains("acct_3", ids);
    }

    // ═══ EpicUserTokens ════════════════════════════════════════

    [Fact]
    public void UpsertEpicUserToken_and_GetEpicUserToken_roundtrip()
    {
        var nonce = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var encAccess = new byte[] { 0xAA, 0xBB, 0xCC };
        var encRefresh = new byte[] { 0xDD, 0xEE, 0xFF };
        var tokenExp = DateTimeOffset.UtcNow.AddHours(2);
        var refreshExp = DateTimeOffset.UtcNow.AddDays(7);

        Db.UpsertEpicUserToken("acct_1", encAccess, encRefresh, tokenExp, refreshExp, nonce);

        var stored = Db.GetEpicUserToken("acct_1");
        Assert.NotNull(stored);
        Assert.Equal("acct_1", stored.AccountId);
        Assert.Equal(encAccess, stored.EncryptedAccessToken);
        Assert.Equal(encRefresh, stored.EncryptedRefreshToken);
        Assert.Equal(nonce, stored.Nonce);
        Assert.NotNull(stored.UpdatedAt);
    }

    [Fact]
    public void GetEpicUserToken_returns_null_for_unknown()
    {
        var stored = Db.GetEpicUserToken("nonexistent");
        Assert.Null(stored);
    }

    [Fact]
    public void UpsertEpicUserToken_updates_existing()
    {
        var nonce1 = new byte[12];
        var nonce2 = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var enc1 = new byte[] { 0x01 };
        var enc2 = new byte[] { 0x02 };
        var now = DateTimeOffset.UtcNow;

        Db.UpsertEpicUserToken("acct_1", enc1, enc1, now.AddHours(1), now.AddDays(1), nonce1);
        Db.UpsertEpicUserToken("acct_1", enc2, enc2, now.AddHours(2), now.AddDays(2), nonce2);

        var stored = Db.GetEpicUserToken("acct_1");
        Assert.NotNull(stored);
        Assert.Equal(enc2, stored.EncryptedAccessToken);
        Assert.Equal(nonce2, stored.Nonce);
    }

    [Fact]
    public void DeleteEpicUserToken_removes_stored_token()
    {
        var nonce = new byte[12];
        Db.UpsertEpicUserToken("acct_1", [0x01], [0x02],
            DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddDays(1), nonce);

        Db.DeleteEpicUserToken("acct_1");

        Assert.Null(Db.GetEpicUserToken("acct_1"));
    }

    [Fact]
    public void DeleteEpicUserToken_noop_for_nonexistent()
    {
        // Should not throw
        Db.DeleteEpicUserToken("nonexistent");
    }

    // ═══ SongFirstSeenSeason ════════════════════════════════════

    [Fact]
    public void UpsertFirstSeenSeason_and_GetFirstSeenSeason_roundtrip()
    {
        Db.UpsertFirstSeenSeason("song_1", 5, 4, 5, "found_at_season_5");
        var result = Db.GetFirstSeenSeason("song_1");
        Assert.Equal(5, result);
    }

    [Fact]
    public void GetFirstSeenSeason_returns_null_for_unknown()
    {
        var result = Db.GetFirstSeenSeason("unknown_song");
        Assert.Null(result);
    }

    [Fact]
    public void GetSongsWithFirstSeenSeason_returns_set()
    {
        Db.UpsertFirstSeenSeason("song_1", 5, 4, 5, null);
        Db.UpsertFirstSeenSeason("song_2", null, 3, 3, "not_found");

        var set = Db.GetSongsWithFirstSeenSeason();
        Assert.Equal(2, set.Count);
        Assert.Contains("song_1", set);
        Assert.Contains("song_2", set);
    }

    [Fact]
    public void GetAllFirstSeenSeasons_returns_dictionary()
    {
        Db.UpsertFirstSeenSeason("song_1", 5, 4, 5, null);
        Db.UpsertFirstSeenSeason("song_2", null, 3, 3, null);

        var dict = Db.GetAllFirstSeenSeasons();
        Assert.Equal(2, dict.Count);
        Assert.Equal(5, dict["song_1"].FirstSeenSeason);
        Assert.Equal(5, dict["song_1"].EstimatedSeason);
        Assert.Null(dict["song_2"].FirstSeenSeason);
        Assert.Equal(3, dict["song_2"].EstimatedSeason);
    }

    [Fact]
    public void UpsertFirstSeenSeason_updates_existing()
    {
        Db.UpsertFirstSeenSeason("song_1", 5, 4, 5, "initial");
        Db.UpsertFirstSeenSeason("song_1", 3, 2, 3, "updated");

        var result = Db.GetFirstSeenSeason("song_1");
        Assert.Equal(3, result);

        var dict = Db.GetAllFirstSeenSeasons();
        Assert.Equal(3, dict["song_1"].EstimatedSeason);
    }

    [Fact]
    public void UpsertFirstSeenSeason_nullable_firstSeen()
    {
        Db.UpsertFirstSeenSeason("song_1", null, 3, 3, null);
        var result = Db.GetFirstSeenSeason("song_1");
        Assert.Null(result);

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

    // ═══ GetDeviceAccountMappings ═══════════════════════════════

    [Fact]
    public void GetDeviceAccountMappings_returns_empty_initially()
    {
        Assert.Empty(Db.GetDeviceAccountMappings());
    }

    [Fact]
    public void GetDeviceAccountMappings_returns_registered_pairs()
    {
        Db.RegisterUser("dev1", "acct1");
        Db.RegisterUser("dev2", "acct2");
        var mappings = Db.GetDeviceAccountMappings();
        Assert.Equal(2, mappings.Count);
        Assert.Contains(mappings, m => m.DeviceId == "dev1" && m.AccountId == "acct1");
        Assert.Contains(mappings, m => m.DeviceId == "dev2" && m.AccountId == "acct2");
    }

    // ═══ GetAccountForDevice ════════════════════════════════════

    [Fact]
    public void GetAccountForDevice_returns_null_when_not_registered()
    {
        Assert.Null(Db.GetAccountForDevice("dev1"));
    }

    [Fact]
    public void GetAccountForDevice_returns_account()
    {
        Db.RegisterUser("dev1", "acct1");
        Assert.Equal("acct1", Db.GetAccountForDevice("dev1"));
    }

    // ═══ UpdateLastSync ═════════════════════════════════════════

    [Fact]
    public void UpdateLastSync_does_not_throw()
    {
        Db.RegisterUser("dev1", "acct1");
        Db.UpdateLastSync("dev1", "acct1");
        // No exception = success. The sync timestamp is updated.
    }

    // ═══ IsDeviceRegistered ═════════════════════════════════════

    [Fact]
    public void IsDeviceRegistered_false_when_empty()
    {
        Assert.False(Db.IsDeviceRegistered("dev1"));
    }

    [Fact]
    public void IsDeviceRegistered_true_after_register()
    {
        Db.RegisterUser("dev1", "acct1");
        Assert.True(Db.IsDeviceRegistered("dev1"));
    }

    // ═══ UnregisterAccount ══════════════════════════════════════

    [Fact]
    public void UnregisterAccount_removes_all_devices()
    {
        Db.RegisterUser("devA", "acct1");
        Db.RegisterUser("devB", "acct1");
        var removed = Db.UnregisterAccount("acct1");
        Assert.Equal(2, removed.Count);
        Assert.Contains("devA", removed);
        Assert.Contains("devB", removed);
        Assert.False(Db.IsDeviceRegistered("devA"));
        Assert.False(Db.IsDeviceRegistered("devB"));
    }

    [Fact]
    public void UnregisterAccount_returns_empty_for_unknown()
    {
        var removed = Db.UnregisterAccount("nobody");
        Assert.Empty(removed);
    }

    // ═══ GetOrphanedRegisteredAccounts ══════════════════════════

    [Fact]
    public void GetOrphanedRegisteredAccounts_returns_empty_when_no_sessions()
    {
        Db.RegisterUser("dev1", "acct1");
        // No sessions at all → not orphaned (safety guard requires at least 1 session)
        var orphans = Db.GetOrphanedRegisteredAccounts();
        Assert.Empty(orphans);
    }

    // ═══ Constructor / Directory Creation ═══════════════════════

    [Fact]
    public void Constructor_creates_directory_if_not_exists()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fst_test_dir_{Guid.NewGuid():N}", "nested");
        var dbPath = Path.Combine(dir, "test.db");
        try
        {
            var logger = Substitute.For<ILogger<Persistence.MetaDatabase>>();
            using var db = new Persistence.MetaDatabase(dbPath, logger);
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(dir)!, true); } catch { }
        }
    }

    // ═══ Migration: SongFirstSeenSeason NOT NULL → nullable ════

    [Fact]
    public void EnsureSchema_migrates_SongFirstSeenSeason_from_old_NOTNULL_schema()
    {
        // Create a temp DB with the OLD schema (NOT NULL on FirstSeenSeason)
        var dbPath = Path.Combine(Path.GetTempPath(), $"fst_migration_{Guid.NewGuid():N}.db");
        try
        {
            // Step 1: Create old-style SongFirstSeenSeason with NOT NULL
            var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE SongFirstSeenSeason (
                        SongId            TEXT    PRIMARY KEY,
                        FirstSeenSeason   INTEGER NOT NULL,
                        MinObservedSeason INTEGER NOT NULL,
                        ProbeResult       TEXT,
                        CalculatedAt      TEXT    NOT NULL
                    );
                    INSERT INTO SongFirstSeenSeason (SongId, FirstSeenSeason, MinObservedSeason, ProbeResult, CalculatedAt)
                    VALUES ('song1', 3, 2, 'found', '2024-01-01T00:00:00Z');
                    """;
                cmd.ExecuteNonQuery();
            }

            // Step 2: Create MetaDatabase on the same file → EnsureSchema should migrate
            var logger = Substitute.For<ILogger<Persistence.MetaDatabase>>();
            using var db = new Persistence.MetaDatabase(dbPath, logger);
            db.EnsureSchema();

            // Step 3: Verify migration: FirstSeenSeason should now be nullable (notnull=0)
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var check = conn.CreateCommand();
                check.CommandText = "SELECT \"notnull\" FROM pragma_table_info('SongFirstSeenSeason') WHERE name = 'FirstSeenSeason';";
                var notnull = (long)(check.ExecuteScalar() ?? 1);
                Assert.Equal(0, notnull); // Should now be nullable

                // Verify EstimatedSeason column was added
                using var estCheck = conn.CreateCommand();
                estCheck.CommandText = "SELECT COUNT(*) FROM pragma_table_info('SongFirstSeenSeason') WHERE name = 'EstimatedSeason';";
                var exists = (long)(estCheck.ExecuteScalar() ?? 0);
                Assert.Equal(1, exists);

                // Verify existing data was preserved
                using var dataCheck = conn.CreateCommand();
                dataCheck.CommandText = "SELECT FirstSeenSeason, EstimatedSeason FROM SongFirstSeenSeason WHERE SongId = 'song1';";
                using var reader = dataCheck.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(3, reader.GetInt32(0)); // FirstSeenSeason preserved
                Assert.Equal(3, reader.GetInt32(1)); // EstimatedSeason = COALESCE(EstimatedSeason, FirstSeenSeason)
            }
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { File.Delete(dbPath + "-wal"); } catch { }
            try { File.Delete(dbPath + "-shm"); } catch { }
        }
    }

    // ═══ GetAllFirstSeenSeasons ═════════════════════════════════

    [Fact]
    public void EnsureSchema_migrates_ScoreHistory_without_ScoreAchievedAt()
    {
        // Simulates the production crash: old DB has ScoreHistory without
        // ScoreAchievedAt, so the dedup index (which references that column)
        // must not run until after the migration adds it.
        var dbPath = Path.Combine(Path.GetTempPath(), $"fst_scorehist_mig_{Guid.NewGuid():N}.db");
        try
        {
            var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                // Old schema: ScoreHistory without ScoreAchievedAt, SeasonRank, AllTimeRank, Stars, Percentile, Season
                cmd.CommandText = """
                    CREATE TABLE ScoreHistory (
                        Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                        SongId      TEXT    NOT NULL,
                        Instrument  TEXT    NOT NULL,
                        AccountId   TEXT    NOT NULL,
                        OldScore    INTEGER,
                        NewScore    INTEGER,
                        OldRank     INTEGER,
                        NewRank     INTEGER,
                        ChangedAt   TEXT    NOT NULL
                    );
                    CREATE INDEX IX_ScoreHist_Account ON ScoreHistory (AccountId);
                    CREATE INDEX IX_ScoreHist_Song    ON ScoreHistory (SongId, Instrument);

                    INSERT INTO ScoreHistory (SongId, Instrument, AccountId, OldScore, NewScore, OldRank, NewRank, ChangedAt)
                    VALUES ('song1', 'Solo_Guitar', 'acct1', 0, 50000, 0, 100, '2025-01-01T00:00:00Z');
                    """;
                cmd.ExecuteNonQuery();
            }

            // EnsureSchema should NOT crash — it must add ScoreAchievedAt before
            // creating the dedup index that references it.
            var logger = Substitute.For<ILogger<Persistence.MetaDatabase>>();
            using var db = new Persistence.MetaDatabase(dbPath, logger);
            db.EnsureSchema();

            // Verify ScoreAchievedAt column was added successfully
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var check = conn.CreateCommand();
                check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('ScoreHistory') WHERE name = 'ScoreAchievedAt';";
                Assert.Equal(1L, (long)(check.ExecuteScalar() ?? 0));

                // Verify the unique dedup index exists
                using var idxCheck = conn.CreateCommand();
                idxCheck.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_ScoreHist_Dedup';";
                Assert.Equal(1L, (long)(idxCheck.ExecuteScalar() ?? 0));

                // Verify existing data was preserved
                using var data = conn.CreateCommand();
                data.CommandText = "SELECT NewScore FROM ScoreHistory WHERE AccountId = 'acct1';";
                Assert.Equal(50000L, (long)(data.ExecuteScalar() ?? 0));
            }
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { File.Delete(dbPath + "-wal"); } catch { }
            try { File.Delete(dbPath + "-shm"); } catch { }
        }
    }

    [Fact]
    public void GetAllFirstSeenSeasons_returns_all_entries()
    {
        Db.UpsertFirstSeenSeason("song_a", 3, 2, 3, "found");
        Db.UpsertFirstSeenSeason("song_b", null, null, 5, "estimated");

        var all = Db.GetAllFirstSeenSeasons();
        Assert.Equal(2, all.Count);
        Assert.Equal(3, all["song_a"].FirstSeenSeason);
        Assert.Null(all["song_b"].FirstSeenSeason);
        Assert.Equal(5, all["song_b"].EstimatedSeason);
    }

    // ═══ GetRegistrationInfo ════════════════════════════════════

    [Fact]
    public void GetRegistrationInfo_returns_info_for_registered_user()
    {
        Db.RegisterOrUpdateUser("dev_info", "acct_info", "TestPlayer", "iOS");
        var info = Db.GetRegistrationInfo("acct_info", "dev_info");
        Assert.NotNull(info);
        Assert.Equal("acct_info", info.AccountId);
        Assert.Equal("TestPlayer", info.DisplayName);
        Assert.NotNull(info.RegisteredAt);
        Assert.NotNull(info.LastLoginAt);
    }

    [Fact]
    public void GetRegistrationInfo_returns_null_for_unknown()
    {
        var info = Db.GetRegistrationInfo("nobody", "nodev");
        Assert.Null(info);
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
}
