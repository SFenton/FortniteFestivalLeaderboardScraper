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
        _tracker.BeginPass(1, 1, 0);
        _tracker.ReportPage0(10);
        _tracker.ReportPageFetched(100); // 1 fetched
        _tracker.ReportPageFetched(100); // 2 fetched

        var progress = _tracker.GetProgressResponse();
        // 2/10 = 20%
        Assert.Equal(20.0, progress.Current?.ProgressPercent);
    }

    [Fact]
    public void ScrapingSnapshot_ProgressPercent_CappedAt100()
    {
        _tracker.BeginPass(1, 1, 0);
        _tracker.ReportPage0(1);
        _tracker.ReportPageFetched(100); // 1 fetched
        _tracker.ReportPageFetched(100); // 2 fetched (more than total)

        var progress = _tracker.GetProgressResponse();
        Assert.Equal(100.0, progress.Current?.ProgressPercent);
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
}
