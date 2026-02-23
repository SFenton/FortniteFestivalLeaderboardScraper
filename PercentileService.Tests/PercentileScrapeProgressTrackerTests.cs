using Microsoft.Extensions.Logging;
using NSubstitute;

namespace PercentileService.Tests;

public sealed class PercentileScrapeProgressTrackerTests
{
    [Fact]
    public void Initially_not_running()
    {
        var tracker = new PercentileScrapeProgressTracker();
        Assert.False(tracker.IsRunning);

        var snap = tracker.GetProgressResponse();
        Assert.False(snap.IsRunning);
        Assert.Null(snap.StartedAtUtc);
        Assert.Equal(0, snap.Entries!.Total);
        Assert.Equal(0, snap.Entries.Completed);
    }

    [Fact]
    public void BeginScrape_sets_running_and_total()
    {
        var tracker = new PercentileScrapeProgressTracker();
        tracker.BeginScrape(100);

        Assert.True(tracker.IsRunning);
        var snap = tracker.GetProgressResponse();
        Assert.True(snap.IsRunning);
        Assert.NotNull(snap.StartedAtUtc);
        Assert.Equal(100, snap.Entries!.Total);
        Assert.Equal(0, snap.Entries.Completed);
    }

    [Fact]
    public void ReportSuccess_increments_succeeded_and_completed()
    {
        var tracker = new PercentileScrapeProgressTracker();
        tracker.BeginScrape(10);

        tracker.ReportSuccess();
        tracker.ReportSuccess();

        var snap = tracker.GetProgressResponse();
        Assert.Equal(2, snap.Entries!.Succeeded);
        Assert.Equal(2, snap.Entries.Completed);
        Assert.Equal(0, snap.Entries.Failed);
        Assert.Equal(0, snap.Entries.Skipped);
    }

    [Fact]
    public void ReportFailed_increments_failed_and_completed()
    {
        var tracker = new PercentileScrapeProgressTracker();
        tracker.BeginScrape(10);

        tracker.ReportFailed();

        var snap = tracker.GetProgressResponse();
        Assert.Equal(1, snap.Entries!.Failed);
        Assert.Equal(1, snap.Entries.Completed);
        Assert.Equal(0, snap.Entries.Succeeded);
    }

    [Fact]
    public void ReportSkipped_increments_skipped_and_completed()
    {
        var tracker = new PercentileScrapeProgressTracker();
        tracker.BeginScrape(10);

        tracker.ReportSkipped();

        var snap = tracker.GetProgressResponse();
        Assert.Equal(1, snap.Entries!.Skipped);
        Assert.Equal(1, snap.Entries.Completed);
        Assert.Equal(0, snap.Entries.Succeeded);
    }

    [Fact]
    public void EndScrape_sets_not_running()
    {
        var tracker = new PercentileScrapeProgressTracker();
        tracker.BeginScrape(5);
        for (int i = 0; i < 5; i++) tracker.ReportSuccess();
        tracker.EndScrape();

        Assert.False(tracker.IsRunning);
        var snap = tracker.GetProgressResponse();
        Assert.False(snap.IsRunning);
        Assert.Null(snap.StartedAtUtc); // null when not running
    }

    [Fact]
    public void ProgressPercent_calculated_correctly()
    {
        var tracker = new PercentileScrapeProgressTracker();
        tracker.BeginScrape(4);

        tracker.ReportSuccess();
        var snap1 = tracker.GetProgressResponse();
        Assert.Equal(25.0, snap1.ProgressPercent);

        tracker.ReportSuccess();
        tracker.ReportFailed();
        var snap2 = tracker.GetProgressResponse();
        Assert.Equal(75.0, snap2.ProgressPercent);

        tracker.ReportSkipped();
        var snap3 = tracker.GetProgressResponse();
        Assert.Equal(100.0, snap3.ProgressPercent);
    }

    [Fact]
    public void ProgressPercent_zero_when_no_entries()
    {
        var tracker = new PercentileScrapeProgressTracker();
        tracker.BeginScrape(0);

        var snap = tracker.GetProgressResponse();
        Assert.Equal(0.0, snap.ProgressPercent);
    }

    [Fact]
    public void EstimatedRemaining_calculated_when_in_progress()
    {
        var tracker = new PercentileScrapeProgressTracker();
        tracker.BeginScrape(100);

        // Simulate 50% done
        for (int i = 0; i < 50; i++) tracker.ReportSuccess();

        var snap = tracker.GetProgressResponse();
        Assert.NotNull(snap.EstimatedRemainingSeconds);
        Assert.True(snap.EstimatedRemainingSeconds >= 0);
    }

    [Fact]
    public void EstimatedRemaining_null_when_nothing_completed()
    {
        var tracker = new PercentileScrapeProgressTracker();
        tracker.BeginScrape(100);

        var snap = tracker.GetProgressResponse();
        Assert.Null(snap.EstimatedRemainingSeconds);
    }

    [Fact]
    public void EstimatedRemaining_null_when_100_percent()
    {
        var tracker = new PercentileScrapeProgressTracker();
        tracker.BeginScrape(2);
        tracker.ReportSuccess();
        tracker.ReportSuccess();

        var snap = tracker.GetProgressResponse();
        // At 100%, no remaining time estimate
        Assert.Null(snap.EstimatedRemainingSeconds);
    }

    [Fact]
    public void SetAdaptiveLimiter_exposes_currentDop()
    {
        var tracker = new PercentileScrapeProgressTracker();
        tracker.BeginScrape(10);

        using var limiter = new AdaptiveConcurrencyLimiter(32, 4, 64,
            Substitute.For<ILogger>());
        tracker.SetAdaptiveLimiter(limiter);

        var snap = tracker.GetProgressResponse();
        Assert.Equal(32, snap.CurrentDop);
    }

    [Fact]
    public void CurrentDop_null_when_no_limiter()
    {
        var tracker = new PercentileScrapeProgressTracker();
        tracker.BeginScrape(10);

        var snap = tracker.GetProgressResponse();
        Assert.Null(snap.CurrentDop);
    }

    [Fact]
    public void ElapsedSeconds_advances_after_begin()
    {
        var tracker = new PercentileScrapeProgressTracker();
        tracker.BeginScrape(1);

        // Just verify it's non-negative (may be 0 if very fast)
        var snap = tracker.GetProgressResponse();
        Assert.True(snap.ElapsedSeconds >= 0);
    }

    [Fact]
    public void BeginScrape_resets_previous_state()
    {
        var tracker = new PercentileScrapeProgressTracker();

        // First scrape
        tracker.BeginScrape(5);
        tracker.ReportSuccess();
        tracker.ReportFailed();
        tracker.EndScrape();

        // Second scrape — counters should be reset
        tracker.BeginScrape(10);
        var snap = tracker.GetProgressResponse();
        Assert.True(snap.IsRunning);
        Assert.Equal(10, snap.Entries!.Total);
        Assert.Equal(0, snap.Entries.Completed);
        Assert.Equal(0, snap.Entries.Succeeded);
        Assert.Equal(0, snap.Entries.Failed);
        Assert.Equal(0, snap.Entries.Skipped);
        Assert.Null(snap.CurrentDop); // limiter reset
    }

    [Fact]
    public void Mixed_results_tracked_correctly()
    {
        var tracker = new PercentileScrapeProgressTracker();
        tracker.BeginScrape(6);

        tracker.ReportSuccess();
        tracker.ReportSuccess();
        tracker.ReportFailed();
        tracker.ReportSkipped();
        tracker.ReportSuccess();
        tracker.ReportFailed();

        var snap = tracker.GetProgressResponse();
        Assert.Equal(6, snap.Entries!.Completed);
        Assert.Equal(3, snap.Entries.Succeeded);
        Assert.Equal(2, snap.Entries.Failed);
        Assert.Equal(1, snap.Entries.Skipped);
        Assert.Equal(100.0, snap.ProgressPercent);
    }
}
