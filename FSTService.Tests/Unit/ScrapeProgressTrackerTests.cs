using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public class ScrapeProgressTrackerTests
{
    private readonly ScrapeProgressTracker _tracker = new();

    // ─── BeginPass ──────────────────────────────────────

    [Fact]
    public void BeginPass_SetsPhaseToScraping()
    {
        _tracker.BeginPass(10, 5, 100);
        Assert.Equal(ScrapeProgressTracker.ScrapePhase.Scraping, _tracker.Phase);
    }

    [Fact]
    public void SetPhase_ComputingRankings()
    {
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.ComputingRankings);
        Assert.Equal(ScrapeProgressTracker.ScrapePhase.ComputingRankings, _tracker.Phase);
    }

    [Fact]
    public void BeginPass_ResetsCounters()
    {
        _tracker.BeginPass(10, 5, 100);
        _tracker.ReportPageFetched(1000);

        // Begin a new pass — should reset
        _tracker.BeginPass(20, 10, 200);

        var progress = _tracker.GetProgressResponse();
        Assert.NotNull(progress.Current);
        Assert.Equal("Scraping", progress.Current!.Operation);
        Assert.Equal(0, progress.Current.Pages?.Fetched);
        Assert.Empty(progress.CompletedOperations);
    }

    // ─── Scraping counters ──────────────────────────────

    [Fact]
    public void ReportPage0_UpdatesEstimatedPages()
    {
        _tracker.BeginPass(2, 1, 0);
        _tracker.ReportPage0(10);
        _tracker.ReportPage0(5);

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(15, progress.Current?.Pages?.DiscoveredTotal);
    }

    [Fact]
    public void ReportPageFetched_IncrementsCounters()
    {
        _tracker.BeginPass(1, 1, 0);
        _tracker.ReportPageFetched(500);
        _tracker.ReportPageFetched(600);

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(2, progress.Current?.Pages?.Fetched);
        Assert.Equal(1100L, progress.Current?.BytesReceived);
        Assert.Equal(2, progress.Current?.Requests);
    }

    [Fact]
    public void ReportRetry_IncrementsRetryAndRequestCounter()
    {
        _tracker.BeginPass(1, 1, 0);
        _tracker.ReportRetry();
        _tracker.ReportRetry();

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(2, progress.Current?.Retries);
        Assert.Equal(2, progress.Current?.Requests);
    }

    [Fact]
    public void ReportLeaderboardComplete_IncrementsPerInstrument()
    {
        _tracker.BeginPass(4, 2, 0);
        var totals = new Dictionary<string, int>
        {
            ["Solo_Guitar"] = 2,
            ["Solo_Bass"] = 2,
        };
        _tracker.SetInstrumentTotals(totals);

        _tracker.ReportLeaderboardComplete("Solo_Guitar");
        _tracker.ReportLeaderboardComplete("Solo_Guitar");
        _tracker.ReportLeaderboardComplete("Solo_Bass");

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(3, progress.Current?.Leaderboards?.Completed);
        Assert.Equal(4, progress.Current?.Leaderboards?.Total);

        var breakdown = progress.Current?.LeaderboardsByInstrument;
        Assert.NotNull(breakdown);
        Assert.Equal(2, breakdown!["Solo_Guitar"].Completed);
        Assert.Equal(2, breakdown["Solo_Guitar"].Total);
        Assert.Equal(1, breakdown["Solo_Bass"].Completed);
    }

    [Fact]
    public void ReportLeaderboardComplete_TransitionsToPersistingScores_WhenAllLeaderboardsDone()
    {
        _tracker.BeginPass(2, 1, 0);
        _tracker.SetSubOperation("fetching_leaderboards");

        _tracker.ReportLeaderboardComplete("Solo_Guitar");
        // Only 1 of 2 done — should still be fetching
        Assert.Equal("fetching_leaderboards", _tracker.GetProgressResponse().Current?.SubOperation);

        _tracker.ReportLeaderboardComplete("Solo_Bass");
        // All done — should auto-transition
        Assert.Equal("persisting_scores", _tracker.GetProgressResponse().Current?.SubOperation);
    }

    [Fact]
    public void ReportLeaderboardComplete_DoesNotClobberOtherSubOperations()
    {
        _tracker.BeginPass(1, 1, 0);
        _tracker.SetSubOperation("deep_scraping");

        _tracker.ReportLeaderboardComplete("Solo_Guitar");
        // Sub-operation was not "fetching_leaderboards", so must not change
        Assert.Equal("deep_scraping", _tracker.GetProgressResponse().Current?.SubOperation);
    }

    [Fact]
    public void SetSubOperation_CanOverridePersistingScores()
    {
        _tracker.BeginPass(1, 1, 0);
        _tracker.SetSubOperation("fetching_leaderboards");
        _tracker.ReportLeaderboardComplete("Solo_Guitar");
        Assert.Equal("persisting_scores", _tracker.GetProgressResponse().Current?.SubOperation);

        // Deep scrape or orchestrator can override
        _tracker.SetSubOperation("deep_scraping");
        Assert.Equal("deep_scraping", _tracker.GetProgressResponse().Current?.SubOperation);
    }

    [Fact]
    public void ReportSongComplete_IncrementsSongCounter()
    {
        _tracker.BeginPass(2, 2, 0);
        _tracker.ReportSongComplete();

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(1, progress.Current?.Songs?.Completed);
        Assert.Equal(2, progress.Current?.Songs?.Total);
    }

    // ─── Scraping snapshot: progress estimation ─────────

    [Fact]
    public void ScrapingSnapshot_UsesDiscoveredTotalWhenAllKnown()
    {
        _tracker.BeginPass(2, 1, 0);
        _tracker.ReportPage0(10);
        _tracker.ReportPage0(5);
        // All 2 leaderboards discovered → total = 15

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(15, progress.Current?.Pages?.EstimatedTotal);
        Assert.True(progress.Current?.Pages?.DiscoveryComplete);
    }

    [Fact]
    public void ScrapingSnapshot_ExtrapolatesWhenNotAllDiscovered()
    {
        _tracker.BeginPass(4, 2, 0);
        _tracker.ReportPage0(10); // 1 out of 4 discovered

        var progress = _tracker.GetProgressResponse();
        // Extrapolation: 10 / 1 * 4 = 40
        Assert.Equal(40, progress.Current?.Pages?.EstimatedTotal);
        Assert.False(progress.Current?.Pages?.DiscoveryComplete);
    }

    [Fact]
    public void ScrapingSnapshot_UsesCachedEstimateWhenNoDiscovery()
    {
        _tracker.BeginPass(4, 2, 100);

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(100, progress.Current?.Pages?.EstimatedTotal);
    }

    [Fact]
    public void ScrapingSnapshot_FallbackToTotalLeaderboards()
    {
        _tracker.BeginPass(4, 2, 0); // No cached, no discovered

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(4, progress.Current?.Pages?.EstimatedTotal);
    }

    [Fact]
    public void ScrapingSnapshot_ProgressPercent_Calculated()
    {
        _tracker.BeginPass(10, 5, 0);
        _tracker.ReportLeaderboardComplete("Solo_Guitar");
        _tracker.ReportLeaderboardComplete("Solo_Guitar");

        var progress = _tracker.GetProgressResponse();
        // 2/10 = 20%
        Assert.Equal(20.0, progress.Current?.ProgressPercent);
    }

    [Fact]
    public void ScrapingSnapshot_ProgressPercent_100_WhenAllLeaderboardsComplete()
    {
        _tracker.BeginPass(2, 1, 0);
        _tracker.ReportLeaderboardComplete("Solo_Guitar");
        _tracker.ReportLeaderboardComplete("Solo_Bass");

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(100.0, progress.Current?.ProgressPercent);
    }

    [Fact]
    public void ScrapingSnapshot_ProgressPercent_NotInflatedByExtraPages()
    {
        // Deep scrape can fetch more pages than estimated — progress should
        // reflect leaderboard completion, not page count.
        _tracker.BeginPass(2, 1, 0);
        _tracker.ReportPage0(5);
        _tracker.ReportPageFetched(100);
        _tracker.ReportPageFetched(100);
        _tracker.ReportPageFetched(100);
        _tracker.ReportPageFetched(100);
        _tracker.ReportPageFetched(100);
        _tracker.ReportPageFetched(100); // 6 fetched > 5 estimated
        _tracker.ReportLeaderboardComplete("Solo_Guitar"); // only 1 of 2 done

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(50.0, progress.Current?.ProgressPercent);
    }

    // ─── Name resolution ────────────────────────────────

    [Fact]
    public void NameResolution_TracksProgress()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.ResolvingNames);
        _tracker.BeginNameResolution(10, 1000);

        _tracker.ReportNameBatchComplete(100, success: true);
        _tracker.ReportNameBatchComplete(0, success: false);

        var progress = _tracker.GetProgressResponse();
        Assert.Equal("ResolvingNames", progress.Current?.Operation);
        Assert.Equal(2, progress.Current?.Batches?.Completed);
        Assert.Equal(10, progress.Current?.Batches?.Total);
        Assert.Equal(100, progress.Current?.AccountsResolved);
        Assert.Equal(1, progress.Current?.FailedBatches);
    }

    [Fact]
    public void NameResolutionSnapshot_ProgressPercent()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.ResolvingNames);
        _tracker.BeginNameResolution(4, 400);

        _tracker.ReportNameBatchComplete(50, true);
        _tracker.ReportNameBatchComplete(50, true);

        var progress = _tracker.GetProgressResponse();
        // 2/4 = 50%
        Assert.Equal(50.0, progress.Current?.ProgressPercent);
    }

    // ─── Phase transitions ──────────────────────────────

    [Fact]
    public void SetPhase_SnapshotsPreviousOperation()
    {
        _tracker.BeginPass(2, 1, 0);
        _tracker.ReportPageFetched(100);
        _tracker.ReportLeaderboardComplete("Solo_Guitar");

        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.ResolvingNames);

        var progress = _tracker.GetProgressResponse();
        Assert.Single(progress.CompletedOperations);
        Assert.Equal("Scraping", progress.CompletedOperations[0].Operation);
        Assert.Equal("ResolvingNames", progress.Current?.Operation);
    }

    [Fact]
    public void EndPass_SnapshotsFinalOperationAndSetsIdle()
    {
        _tracker.BeginPass(1, 1, 0);
        _tracker.ReportPageFetched(100);
        _tracker.EndPass();

        Assert.Equal(ScrapeProgressTracker.ScrapePhase.Idle, _tracker.Phase);

        var progress = _tracker.GetProgressResponse();
        Assert.Null(progress.Current);
        Assert.Single(progress.CompletedOperations);
    }

    [Fact]
    public void MultiplePhaseTransitions_AccumulateHistory()
    {
        _tracker.BeginPass(1, 1, 0);

        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.ResolvingNames);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.RebuildingPersonalDbs);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.BackfillingScores);

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(3, progress.CompletedOperations.Count);
        Assert.Equal("Scraping", progress.CompletedOperations[0].Operation);
        Assert.Equal("ResolvingNames", progress.CompletedOperations[1].Operation);
        Assert.Equal("RebuildingPersonalDbs", progress.CompletedOperations[2].Operation);
    }

    // ─── Idle state ─────────────────────────────────────

    [Fact]
    public void GetProgressResponse_WhenIdle_ReturnsNull()
    {
        var progress = _tracker.GetProgressResponse();
        Assert.Null(progress.Current);
        Assert.Empty(progress.CompletedOperations);
    }

    // ─── OperationSnapshot for non-Scraping phases ──────

    [Fact]
    public void Initializing_Phase_ReturnsSnapshot()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.Initializing);

        var progress = _tracker.GetProgressResponse();
        Assert.Equal("Initializing", progress.Current?.Operation);
    }

    [Fact]
    public void RebuildingPersonalDbs_Phase_ReturnsSnapshot()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.RebuildingPersonalDbs);

        var progress = _tracker.GetProgressResponse();
        Assert.Equal("RebuildingPersonalDbs", progress.Current?.Operation);
    }

    // ─── Adaptive concurrency integration ───────────────

    [Fact]
    public void SetAdaptiveLimiter_ReflectsInSnapshot()
    {
        _tracker.BeginPass(1, 1, 0);

        using var limiter = new AdaptiveConcurrencyLimiter(16, 4, 64,
            NSubstitute.Substitute.For<Microsoft.Extensions.Logging.ILogger>());
        _tracker.SetAdaptiveLimiter(limiter);

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(16, progress.Current?.CurrentDop);

        _tracker.SetAdaptiveLimiter(null);
        progress = _tracker.GetProgressResponse();
        Assert.Null(progress.Current?.CurrentDop);
    }

    // ─── PassElapsedSeconds ─────────────────────────────

    [Fact]
    public void PassElapsedSeconds_IsTracked()
    {
        _tracker.BeginPass(1, 1, 0);
        var progress = _tracker.GetProgressResponse();
        Assert.True(progress.PassElapsedSeconds >= 0);
    }

    // ─── DTO defaults ───────────────────────────────────

    [Fact]
    public void ProgressResponse_DefaultValues()
    {
        var response = new ProgressResponse();
        Assert.Null(response.Current);
        Assert.Empty(response.CompletedOperations);
        Assert.Equal(0, response.PassElapsedSeconds);
    }

    [Fact]
    public void OperationSnapshot_DefaultValues()
    {
        var snapshot = new OperationSnapshot();
        Assert.Equal("", snapshot.Operation);
        Assert.Null(snapshot.StartedAtUtc);
        Assert.Equal(0, snapshot.ElapsedSeconds);
        Assert.Null(snapshot.EstimatedRemainingSeconds);
        Assert.Null(snapshot.ProgressPercent);
        Assert.Null(snapshot.Songs);
        Assert.Null(snapshot.Leaderboards);
        Assert.Null(snapshot.Pages);
        Assert.Null(snapshot.Requests);
        Assert.Null(snapshot.Retries);
        Assert.Null(snapshot.BytesReceived);
        Assert.Null(snapshot.CurrentDop);
        Assert.Null(snapshot.Batches);
        Assert.Null(snapshot.AccountsResolved);
        Assert.Null(snapshot.FailedBatches);
    }

    [Fact]
    public void ProgressCounter_DefaultValues()
    {
        var counter = new ProgressCounter();
        Assert.Equal(0, counter.Completed);
        Assert.Equal(0, counter.Total);
    }

    [Fact]
    public void PageProgress_DefaultValues()
    {
        var p = new PageProgress();
        Assert.Equal(0, p.Fetched);
        Assert.Equal(0, p.EstimatedTotal);
        Assert.Equal(0, p.DiscoveredTotal);
        Assert.False(p.DiscoveryComplete);
        Assert.Equal(0, p.LeaderboardsDiscovered);
    }

    // ─── CalculatingFirstSeen ───────────────────────────

    [Fact]
    public void CalculatingFirstSeen_Phase_ReturnsSnapshot()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.CalculatingFirstSeen);

        var progress = _tracker.GetProgressResponse();
        Assert.Equal("CalculatingFirstSeen", progress.Current?.Operation);
    }

    // ─── RefreshingRegisteredUsers ──────────────────────

    [Fact]
    public void RefreshingRegisteredUsers_Phase_ReturnsSnapshot()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.RefreshingRegisteredUsers);

        var progress = _tracker.GetProgressResponse();
        Assert.Equal("RefreshingRegisteredUsers", progress.Current?.Operation);
    }

    // ─── ReconstructingHistory ──────────────────────────

    [Fact]
    public void ReconstructingHistory_Phase_ReturnsSnapshot()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.ReconstructingHistory);

        var progress = _tracker.GetProgressResponse();
        Assert.Equal("ReconstructingHistory", progress.Current?.Operation);
    }

    // ─── Default / unknown phase ────────────────────────

    [Fact]
    public void UnknownPhase_ReturnsNullSnapshot()
    {
        _tracker.BeginPass(0, 0, 0);
        // Force an undefined enum value to exercise the default switch arm
        _tracker.SetPhase((ScrapeProgressTracker.ScrapePhase)99);

        var progress = _tracker.GetProgressResponse();
        Assert.Null(progress.Current);
    }

    // ─── Generic phase progress ─────────────────────────

    [Fact]
    public void BeginPhaseProgress_SetsCountersVisibleInSnapshot()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.RefreshingRegisteredUsers);
        _tracker.BeginPhaseProgress(totalItems: 100, totalAccounts: 3);

        var progress = _tracker.GetProgressResponse();
        Assert.Equal("RefreshingRegisteredUsers", progress.Current?.Operation);
        Assert.Equal(100, progress.Current?.WorkItems?.Total);
        Assert.Equal(0, progress.Current?.WorkItems?.Completed);
        Assert.Equal(3, progress.Current?.Accounts?.Total);
        Assert.Equal(0, progress.Current?.Accounts?.Completed);
        Assert.Equal(0.0, progress.Current?.ProgressPercent);
    }

    [Fact]
    public void ReportPhaseItemComplete_IncrementsAndUpdatesProgress()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.BackfillingScores);
        _tracker.BeginPhaseProgress(totalItems: 10);

        _tracker.ReportPhaseItemComplete();
        _tracker.ReportPhaseItemComplete();

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(2, progress.Current?.WorkItems?.Completed);
        Assert.Equal(10, progress.Current?.WorkItems?.Total);
        Assert.Equal(20.0, progress.Current?.ProgressPercent);
    }

    [Fact]
    public void AddPhaseItems_IncreasesTotalAndAffectsProgress()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.RefreshingRegisteredUsers);
        _tracker.BeginPhaseProgress(totalItems: 0, totalAccounts: 2);

        // First account's work discovered
        _tracker.AddPhaseItems(50);
        _tracker.ReportPhaseItemComplete();

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(50, progress.Current?.WorkItems?.Total);
        Assert.Equal(1, progress.Current?.WorkItems?.Completed);
        Assert.Equal(2.0, progress.Current?.ProgressPercent);
    }

    [Fact]
    public void ReportPhaseAccountComplete_Increments()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.RebuildingPersonalDbs);
        _tracker.BeginPhaseProgress(totalItems: 0, totalAccounts: 5);

        _tracker.ReportPhaseAccountComplete();
        _tracker.ReportPhaseAccountComplete();

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(2, progress.Current?.Accounts?.Completed);
        Assert.Equal(5, progress.Current?.Accounts?.Total);
        // Progress based on accounts when no work items
        Assert.Equal(40.0, progress.Current?.ProgressPercent);
    }

    [Fact]
    public void ReportPhaseRequest_And_ReportPhaseRetry_Increment()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.BackfillingScores);
        _tracker.BeginPhaseProgress(totalItems: 10);

        _tracker.ReportPhaseRequest();
        _tracker.ReportPhaseRequest();
        _tracker.ReportPhaseRetry();

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(2, progress.Current?.Requests);
        Assert.Equal(1, progress.Current?.Retries);
    }

    [Fact]
    public void ReportPhaseEntryUpdated_Accumulates()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.RefreshingRegisteredUsers);
        _tracker.BeginPhaseProgress(totalItems: 10);

        _tracker.ReportPhaseEntryUpdated();
        _tracker.ReportPhaseEntryUpdated(5);

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(6, progress.Current?.EntriesUpdated);
    }

    [Fact]
    public void GenericPhaseSnapshot_EstimatesRemainingTime()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.BackfillingScores);
        _tracker.BeginPhaseProgress(totalItems: 100);

        // Simulate 50% complete
        for (int i = 0; i < 50; i++)
            _tracker.ReportPhaseItemComplete();

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(50.0, progress.Current?.ProgressPercent);
        // Estimated remaining should be non-null at 50%
        Assert.NotNull(progress.Current?.EstimatedRemainingSeconds);
    }

    [Fact]
    public void GenericPhaseSnapshot_NullFieldsWhenZero()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.ComputingRankings);
        // No BeginPhaseProgress called — all counters at 0

        var progress = _tracker.GetProgressResponse();
        Assert.Equal("ComputingRankings", progress.Current?.Operation);
        Assert.Null(progress.Current?.WorkItems);
        Assert.Null(progress.Current?.Accounts);
        Assert.Null(progress.Current?.Requests);
        Assert.Null(progress.Current?.Retries);
        Assert.Null(progress.Current?.EntriesUpdated);
        Assert.Null(progress.Current?.ProgressPercent);
    }

    [Fact]
    public void SetPhase_ResetsGenericCounters()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.BackfillingScores);
        _tracker.BeginPhaseProgress(totalItems: 100, totalAccounts: 2);
        _tracker.ReportPhaseItemComplete();
        _tracker.ReportPhaseRequest();
        _tracker.ReportPhaseEntryUpdated(5);

        // Transition to a new phase — counters should reset
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.ReconstructingHistory);

        var progress = _tracker.GetProgressResponse();
        Assert.Equal("ReconstructingHistory", progress.Current?.Operation);
        Assert.Null(progress.Current?.WorkItems);
        Assert.Null(progress.Current?.Accounts);
        Assert.Null(progress.Current?.Requests);
        Assert.Null(progress.Current?.EntriesUpdated);
    }

    [Fact]
    public void SetPhase_SnapshotsGenericCountersBeforeReset()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.BackfillingScores);
        _tracker.BeginPhaseProgress(totalItems: 10, totalAccounts: 1);

        for (int i = 0; i < 10; i++)
            _tracker.ReportPhaseItemComplete();
        _tracker.ReportPhaseAccountComplete();
        _tracker.ReportPhaseEntryUpdated(3);
        _tracker.ReportPhaseRequest();

        // Transition — previous phase should be snapshotted
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.ReconstructingHistory);

        var progress = _tracker.GetProgressResponse();
        var backfillOp = progress.CompletedOperations
            .FirstOrDefault(o => o.Operation == "BackfillingScores");
        Assert.NotNull(backfillOp);
        Assert.Equal(10, backfillOp!.WorkItems?.Completed);
        Assert.Equal(10, backfillOp.WorkItems?.Total);
        Assert.Equal(1, backfillOp.Accounts?.Completed);
        Assert.Equal(1, backfillOp.Accounts?.Total);
        Assert.Equal(3, backfillOp.EntriesUpdated);
        Assert.Equal(100.0, backfillOp.ProgressPercent);
    }

    [Fact]
    public void GenericPhaseSnapshot_ProgressPercent_CappedAt100()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.ComputingRivals);
        _tracker.BeginPhaseProgress(totalItems: 0, totalAccounts: 1);

        _tracker.ReportPhaseAccountComplete();
        _tracker.ReportPhaseAccountComplete(); // More than total

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(100.0, progress.Current?.ProgressPercent);
    }

    [Fact]
    public void RebuildingPersonalDbs_UsesGenericPhaseSnapshot()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.RebuildingPersonalDbs);
        _tracker.BeginPhaseProgress(totalItems: 0, totalAccounts: 3);
        _tracker.ReportPhaseAccountComplete();

        var progress = _tracker.GetProgressResponse();
        Assert.Equal("RebuildingPersonalDbs", progress.Current?.Operation);
        Assert.Equal(3, progress.Current?.Accounts?.Total);
        Assert.Equal(1, progress.Current?.Accounts?.Completed);
    }

    [Fact]
    public void OperationSnapshot_NewFieldDefaults()
    {
        var snapshot = new OperationSnapshot();
        Assert.Null(snapshot.Accounts);
        Assert.Null(snapshot.WorkItems);
        Assert.Null(snapshot.EntriesUpdated);
    }

    [Fact]
    public void GenericPhaseSnapshot_AdaptiveLimiterDop()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.RefreshingRegisteredUsers);
        _tracker.BeginPhaseProgress(totalItems: 10);

        using var limiter = new AdaptiveConcurrencyLimiter(32, 4, 128,
            NSubstitute.Substitute.For<Microsoft.Extensions.Logging.ILogger>());
        _tracker.SetAdaptiveLimiter(limiter);

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(32, progress.Current?.CurrentDop);
    }

    // ─── SubOperation tracking ──────────────────────────

    [Fact]
    public void SubOperation_DefaultIsNull()
    {
        var snapshot = new OperationSnapshot();
        Assert.Null(snapshot.SubOperation);
    }

    [Fact]
    public void SetSubOperation_AppearsInScrapingSnapshot()
    {
        _tracker.BeginPass(1, 1, 0);
        _tracker.SetSubOperation("fetching_leaderboards");

        var progress = _tracker.GetProgressResponse();
        Assert.Equal("fetching_leaderboards", progress.Current?.SubOperation);
    }

    [Fact]
    public void SetSubOperation_AppearsInGenericPhaseSnapshot()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.ComputingRankings);
        _tracker.SetSubOperation("per_instrument_rankings");

        var progress = _tracker.GetProgressResponse();
        Assert.Equal("per_instrument_rankings", progress.Current?.SubOperation);
    }

    [Fact]
    public void SetSubOperation_AppearsInNameResolutionSnapshot()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.ResolvingNames);
        _tracker.BeginNameResolution(5, 500);
        _tracker.SetSubOperation("resolving_batch");

        var progress = _tracker.GetProgressResponse();
        Assert.Equal("resolving_batch", progress.Current?.SubOperation);
    }

    [Fact]
    public void SetSubOperation_AppearsInPostScrapeEnrichmentSnapshot()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.PostScrapeEnrichment);
        _tracker.SetSubOperation("enriching_parallel");

        var progress = _tracker.GetProgressResponse();
        Assert.Equal("enriching_parallel", progress.Current?.SubOperation);
    }

    [Fact]
    public void SetPhase_ClearsSubOperation()
    {
        _tracker.BeginPass(1, 1, 0);
        _tracker.SetSubOperation("fetching_leaderboards");

        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.ComputingRankings);

        var progress = _tracker.GetProgressResponse();
        Assert.Null(progress.Current?.SubOperation);
    }

    [Fact]
    public void EndPass_ClearsSubOperation()
    {
        _tracker.BeginPass(1, 1, 0);
        _tracker.SetSubOperation("fetching_leaderboards");

        _tracker.EndPass();

        // After EndPass the phase is Idle, so Current is null — verify the
        // completed snapshot captured the sub-operation and that current is clean
        var progress = _tracker.GetProgressResponse();
        Assert.Null(progress.Current);
        Assert.Single(progress.CompletedOperations);
        Assert.Equal("fetching_leaderboards", progress.CompletedOperations[0].SubOperation);
    }

    [Fact]
    public void SetSubOperation_Null_ClearsValue()
    {
        _tracker.BeginPass(1, 1, 0);
        _tracker.SetSubOperation("fetching_leaderboards");
        _tracker.SetSubOperation(null);

        var progress = _tracker.GetProgressResponse();
        Assert.Null(progress.Current?.SubOperation);
    }

    // ─── New phases: Precomputing + Finalizing ──────────

    [Fact]
    public void Precomputing_Phase_ReturnsSnapshot()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.Precomputing);
        _tracker.SetSubOperation("population_tiers");

        var progress = _tracker.GetProgressResponse();
        Assert.Equal("Precomputing", progress.Current?.Operation);
        Assert.Equal("population_tiers", progress.Current?.SubOperation);
    }

    [Fact]
    public void Finalizing_Phase_ReturnsSnapshot()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.Finalizing);
        _tracker.SetSubOperation("cleaning_up_sessions");

        var progress = _tracker.GetProgressResponse();
        Assert.Equal("Finalizing", progress.Current?.Operation);
        Assert.Equal("cleaning_up_sessions", progress.Current?.SubOperation);
    }

    [Fact]
    public void SubOperation_PreservedInCompletedOperationSnapshot()
    {
        _tracker.BeginPass(0, 0, 0);
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.SongMachine);
        _tracker.SetSubOperation("processing_songs");

        // Transition to next phase — snapshot should preserve the sub-operation
        _tracker.SetPhase(ScrapeProgressTracker.ScrapePhase.ComputingRankings);

        var progress = _tracker.GetProgressResponse();
        var songMachineOp = progress.CompletedOperations
            .FirstOrDefault(o => o.Operation == "SongMachine");
        Assert.NotNull(songMachineOp);
        Assert.Equal("processing_songs", songMachineOp!.SubOperation);
    }
}
