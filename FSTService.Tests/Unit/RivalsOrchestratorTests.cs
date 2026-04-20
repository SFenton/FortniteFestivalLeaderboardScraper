using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using NSubstitute;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for <see cref="RivalsOrchestrator"/> lifecycle, status management,
/// and parallel computation.
/// </summary>
public sealed class RivalsOrchestratorTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaFixture = new();
    private readonly string _dataDir;

    public RivalsOrchestratorTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"fst_rivals_orch_{Guid.NewGuid():N}");
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
            _metaFixture.Db,
            loggerFactory,
            NullLogger<GlobalLeaderboardPersistence>.Instance,
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions()));
        glp.Initialize();
        return glp;
    }

    private (RivalsOrchestrator Orch, ScrapeProgressTracker Progress) CreateOrchestrator(
        GlobalLeaderboardPersistence persistence,
        ILogger<RivalsOrchestrator>? log = null)
    {
        var calculator = new RivalsCalculator(persistence, NullLogger<RivalsCalculator>.Instance);
        var progress = new ScrapeProgressTracker();
        var orch = new RivalsOrchestrator(calculator, persistence, new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), progress, new UserSyncProgressTracker(new Api.NotificationService(Substitute.For<ILogger<Api.NotificationService>>()), Substitute.For<ILogger<UserSyncProgressTracker>>()), new Api.ResponseCacheService(TimeSpan.FromMinutes(5)), log ?? NullLogger<RivalsOrchestrator>.Instance);
        return (orch, progress);
    }

    [Fact]
    public async Task ComputeAllAsync_creates_status_rows_for_registered_users()
    {
        var persistence = CreatePersistence();
        var (orch, _) = CreateOrchestrator(persistence);

        var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_1", "acct_2" };
        await orch.ComputeAllAsync(registeredIds, null, CancellationToken.None);

        // Both accounts should have RivalsStatus rows
        Assert.NotNull(_metaFixture.Db.GetRivalsStatus("acct_1"));
        Assert.NotNull(_metaFixture.Db.GetRivalsStatus("acct_2"));
    }

    [Fact]
    public async Task ComputeAllAsync_skips_when_no_registered_users()
    {
        var persistence = CreatePersistence();
        var (orch, progress) = CreateOrchestrator(persistence);

        await orch.ComputeAllAsync(
            new HashSet<string>(), null, CancellationToken.None);

        // Should not have set phase
        Assert.Empty(_metaFixture.Db.GetPendingRivalsAccounts());
    }

    [Fact]
    public async Task ComputeAllAsync_skips_when_all_users_already_complete_with_rivals()
    {
        var persistence = CreatePersistence();
        var (orch, progress) = CreateOrchestrator(persistence);

        // Register user with actual rivals found — should NOT be requeued
        _metaFixture.Db.EnsureRivalsStatus("acct-complete");
        _metaFixture.Db.StartRivals("acct-complete");
        _metaFixture.Db.CompleteRivals("acct-complete", 3, 10);

        var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct-complete" };
        await orch.ComputeAllAsync(registeredIds, null, CancellationToken.None);
        // toCompute should be empty → early return (user has rivals, not stale)

        var status = _metaFixture.Db.GetRivalsStatus("acct-complete");
        Assert.Equal("complete", status!.Status);
        Assert.Equal(10, status.RivalsFound);
        Assert.Equal(RivalsAlgorithmVersion.SongRivals, status.AlgorithmVersion);
    }

    [Fact]
    public async Task ComputeAllAsync_processes_pending_accounts()
    {
        var persistence = CreatePersistence();
        var (orch, _) = CreateOrchestrator(persistence);

        // Pre-create pending status
        _metaFixture.Db.EnsureRivalsStatus("acct_1");

        var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_1" };
        await orch.ComputeAllAsync(registeredIds, null, CancellationToken.None);

        // Should be complete (even with no data, just 0 rivals)
        var status = _metaFixture.Db.GetRivalsStatus("acct_1");
        Assert.Equal("complete", status!.Status);
    }

    [Fact]
    public async Task ComputeAllAsync_includes_dirty_users()
    {
        var persistence = CreatePersistence();
        var (orch, _) = CreateOrchestrator(persistence);

        // acct_1 is complete, acct_2 is not pending but has dirty instruments
        _metaFixture.Db.EnsureRivalsStatus("acct_1");
        _metaFixture.Db.StartRivals("acct_1");
        _metaFixture.Db.CompleteRivals("acct_1", 0, 0);

        var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_1", "acct_2" };
        var dirtyMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["acct_2"] = new(StringComparer.OrdinalIgnoreCase) { "Solo_Guitar" },
        };

        await orch.ComputeAllAsync(registeredIds, dirtyMap, CancellationToken.None);

        // acct_2 should have been computed via dirty path
        var status2 = _metaFixture.Db.GetRivalsStatus("acct_2");
        Assert.NotNull(status2);
        Assert.Equal("complete", status2.Status);
    }

    [Fact]
    public async Task ComputeAllAsync_clears_dirty_songs_without_recompute_when_fingerprints_match()
    {
        var persistence = CreatePersistence();
        var log = new TestLogger<RivalsOrchestrator>();
        var (orch, _) = CreateOrchestrator(persistence, log);
        var calculator = new RivalsCalculator(persistence, NullLogger<RivalsCalculator>.Instance);

        persistence.PersistResult(new GlobalLeaderboardResult
        {
            SongId = "song-1",
            Instrument = "Solo_Guitar",
            Entries =
            [
                new LeaderboardEntry { AccountId = "acct_1", Score = 100_000, Rank = 1 },
            ],
        }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_1" });

        _metaFixture.Db.EnsureRivalsStatus("acct_1");
        _metaFixture.Db.StartRivals("acct_1");
        _metaFixture.Db.CompleteRivals("acct_1", 1, 1);

        var selectionState = calculator.ComputeSelectionState("acct_1");
        _metaFixture.Db.ReplaceRivalSelectionState("acct_1", selectionState.Fingerprints, selectionState.InstrumentStates);
        _metaFixture.Db.UpsertDirtyRivalSongs(
        [
            new RivalDirtySongRow
            {
                AccountId = "acct_1",
                Instrument = "Solo_Guitar",
                SongId = "song-1",
                DirtyReason = RivalsDirtyReason.NeighborWindowChange,
                DetectedAt = "2026-01-01T00:00:00Z",
            },
        ]);

        await orch.ComputeAllAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_1" }, null, CancellationToken.None);

        Assert.Empty(_metaFixture.Db.GetDirtyRivalSongs("acct_1"));
        var status = _metaFixture.Db.GetRivalsStatus("acct_1");
        Assert.NotNull(status);
        Assert.Equal(1, status!.RivalsFound);
        Assert.Contains(log.Entries, entry => entry.Message.Contains("skip_clean_after_compare", StringComparison.Ordinal));
        Assert.Contains(log.Entries, entry => entry.Message.Contains("skip_clean_after_compare=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ComputeAllAsync_recomputes_dirty_songs_when_selection_baseline_is_missing()
    {
        var persistence = CreatePersistence();
        var log = new TestLogger<RivalsOrchestrator>();
        var (orch, _) = CreateOrchestrator(persistence, log);

        persistence.PersistResult(new GlobalLeaderboardResult
        {
            SongId = "song-1",
            Instrument = "Solo_Guitar",
            Entries =
            [
                new LeaderboardEntry { AccountId = "acct_1", Score = 100_000, Rank = 1 },
            ],
        }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_1" });

        _metaFixture.Db.EnsureRivalsStatus("acct_1");
        _metaFixture.Db.StartRivals("acct_1");
        _metaFixture.Db.CompleteRivals("acct_1", 1, 1);
        _metaFixture.Db.UpsertDirtyRivalSongs(
        [
            new RivalDirtySongRow
            {
                AccountId = "acct_1",
                Instrument = "Solo_Guitar",
                SongId = "song-1",
                DirtyReason = RivalsDirtyReason.SelfScoreChange,
                DetectedAt = "2026-01-01T00:00:00Z",
            },
        ]);

        await orch.ComputeAllAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_1" }, null, CancellationToken.None);

        Assert.Empty(_metaFixture.Db.GetDirtyRivalSongs("acct_1"));
        Assert.NotEmpty(_metaFixture.Db.GetRivalInstrumentStates("acct_1"));
        var status = _metaFixture.Db.GetRivalsStatus("acct_1");
        Assert.NotNull(status);
        Assert.Equal(0, status!.RivalsFound);
        Assert.Contains(log.Entries, entry => entry.Message.Contains("recompute_missing_baseline", StringComparison.Ordinal));
        Assert.Contains(log.Entries, entry => entry.Message.Contains("recompute_missing_baseline=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ComputeAllAsync_logs_recompute_eligibility_changed_outcome()
    {
        var persistence = CreatePersistence();
        var log = new TestLogger<RivalsOrchestrator>();
        var (orch, _) = CreateOrchestrator(persistence, log);

        for (var index = 0; index < RivalsCalculator.MinUserSongsPerInstrument; index++)
        {
            persistence.PersistResult(new GlobalLeaderboardResult
            {
                SongId = $"song-{index}",
                Instrument = "Solo_Guitar",
                Entries =
                [
                    new LeaderboardEntry { AccountId = "acct_eligibility", Score = 100_000 - index, Rank = 1 },
                ],
            }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_eligibility" });
        }

        _metaFixture.Db.EnsureRivalsStatus("acct_eligibility");
        _metaFixture.Db.StartRivals("acct_eligibility");
        _metaFixture.Db.CompleteRivals("acct_eligibility", 1, 1);
        _metaFixture.Db.ReplaceRivalSelectionState(
            "acct_eligibility",
            [],
            [
                new RivalInstrumentStateRow
                {
                    AccountId = "acct_eligibility",
                    Instrument = "Solo_Guitar",
                    SongCount = RivalsCalculator.MinUserSongsPerInstrument - 1,
                    IsEligible = false,
                    ComputedAt = "2026-01-01T00:00:00Z",
                },
            ]);
        _metaFixture.Db.UpsertDirtyRivalSongs(
        [
            new RivalDirtySongRow
            {
                AccountId = "acct_eligibility",
                Instrument = "Solo_Guitar",
                SongId = "song-0",
                DirtyReason = RivalsDirtyReason.EligibilityChange,
                DetectedAt = "2026-01-01T00:00:00Z",
            },
        ]);

        await orch.ComputeAllAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct_eligibility" }, null, CancellationToken.None);

        Assert.Contains(log.Entries, entry => entry.Message.Contains("recompute_eligibility_changed", StringComparison.Ordinal));
        Assert.Contains(log.Entries, entry => entry.Message.Contains("recompute_eligibility_changed=1", StringComparison.Ordinal));
    }

    [Fact]
    public void ComputeForUser_completes_with_zero_rivals_when_no_data()
    {
        // User has a RivalsStatus row but no scores → should complete with 0 combos/rivals
        var persistence = CreatePersistence();
        var (orch, _) = CreateOrchestrator(persistence);

        _metaFixture.Db.EnsureRivalsStatus("empty_user");

        orch.ComputeForUser("empty_user");

        var status = _metaFixture.Db.GetRivalsStatus("empty_user");
        Assert.Equal("complete", status!.Status);
        Assert.Equal(0, status.CombosComputed);
        Assert.Equal(0, status.RivalsFound);
    }

    [Fact]
    public void ComputeForUser_creates_status_row_when_none_exists()
    {
        // Reproduces Bug 1: BackfillOrchestrator calls ComputeForUser without
        // a pre-existing rivals_status row. Before the fix, StartRivals/CompleteRivals
        // (UPDATEs) silently affected 0 rows and results were lost.
        var persistence = CreatePersistence();
        var (orch, _) = CreateOrchestrator(persistence);

        // No EnsureRivalsStatus call — simulates BackfillOrchestrator path
        Assert.Null(_metaFixture.Db.GetRivalsStatus("backfill_user"));

        orch.ComputeForUser("backfill_user");

        var status = _metaFixture.Db.GetRivalsStatus("backfill_user");
        Assert.NotNull(status);
        Assert.Equal("complete", status.Status);
    }

    [Fact]
    public async Task ComputeAllAsync_resets_stale_zero_rivals_to_pending()
    {
        // Reproduces Bug 2: User completed with 0 rivals (data wasn't available yet)
        // and is stuck forever. The stale-zero reset should requeue them.
        var persistence = CreatePersistence();
        var (orch, _) = CreateOrchestrator(persistence);

        // Simulate a user stuck at complete with 0 rivals
        _metaFixture.Db.EnsureRivalsStatus("stale_user");
        _metaFixture.Db.StartRivals("stale_user");
        _metaFixture.Db.CompleteRivals("stale_user", 0, 0);

        var statusBefore = _metaFixture.Db.GetRivalsStatus("stale_user");
        Assert.Equal("complete", statusBefore!.Status);
        Assert.Equal(0, statusBefore.RivalsFound);

        var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "stale_user" };
        await orch.ComputeAllAsync(registeredIds, null, CancellationToken.None);

        // Should have been recomputed (status reset to pending, then computed again)
        var statusAfter = _metaFixture.Db.GetRivalsStatus("stale_user");
        Assert.Equal("complete", statusAfter!.Status);
        // Still 0 rivals because no leaderboard data, but the point is it was re-attempted
    }

    [Fact]
    public async Task ComputeAllAsync_does_not_reset_users_with_actual_rivals()
    {
        // Ensure users who legitimately have rivals are not disrupted
        var persistence = CreatePersistence();
        var (orch, _) = CreateOrchestrator(persistence);

        // Simulate a user with real rivals
        _metaFixture.Db.EnsureRivalsStatus("rich_user");
        _metaFixture.Db.StartRivals("rich_user");
        _metaFixture.Db.CompleteRivals("rich_user", 5, 20);

        var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rich_user" };
        await orch.ComputeAllAsync(registeredIds, null, CancellationToken.None);

        // User should NOT have been requeued — they already have rivals
        // GetPendingRivalsAccounts should not have included them
        var status = _metaFixture.Db.GetRivalsStatus("rich_user");
        Assert.Equal("complete", status!.Status);
        Assert.Equal(20, status.RivalsFound);
    }

    [Fact]
    public async Task ComputeAllAsync_recomputes_users_with_old_algorithm_version()
    {
        var persistence = CreatePersistence();
        var (orch, _) = CreateOrchestrator(persistence);

        using (var conn = _metaFixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                INSERT INTO rivals_status (account_id, status, combos_computed, total_combos_to_compute, rivals_found, algorithm_version)
                VALUES ('old_algo_user', 'complete', 2, 2, 8, 1)";
            cmd.ExecuteNonQuery();
        }

        var registeredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "old_algo_user" };
        await orch.ComputeAllAsync(registeredIds, null, CancellationToken.None);

        var status = _metaFixture.Db.GetRivalsStatus("old_algo_user");
        Assert.NotNull(status);
        Assert.Equal("complete", status!.Status);
        Assert.Equal(RivalsAlgorithmVersion.SongRivals, status.AlgorithmVersion);
    }
}
