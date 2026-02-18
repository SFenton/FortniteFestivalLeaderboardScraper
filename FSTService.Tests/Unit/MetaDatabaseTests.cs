using FSTService.Tests.Helpers;

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
}
