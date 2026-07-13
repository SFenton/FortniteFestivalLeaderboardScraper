using FSTService.Persistence;

namespace FSTService.Tests.Unit;

public sealed class ScrapeCorrectnessRecordsTests
{
    [Fact]
    public void ScopeManifestPersistenceResult_RequiresExactCompleteCoverage()
    {
        var complete = new ScopeManifestPersistenceResult(42, 2, 2, 2, 0, 0);
        var incomplete = complete with { IncompleteScopeCount = 1 };

        Assert.True(complete.IsComplete);
        Assert.False(incomplete.IsComplete);
    }

    [Fact]
    public void ScrapeFailureSummary_RoundTripsDurableStatus()
    {
        var failedAt = DateTime.UtcNow;
        var summary = new ScrapeFailureSummary(
            42,
            "failed",
            failedAt,
            "writer",
            "three rows retained",
            1,
            ["Checkpoint"]);

        Assert.Equal(42, summary.ScrapeId);
        Assert.Equal("failed", summary.Status);
        Assert.Equal(failedAt, summary.FailedAtUtc);
        Assert.Equal("writer", summary.FailurePhase);
        Assert.Equal("three rows retained", summary.FailureMessage);
        Assert.Equal(1, summary.BestEffortFailureCount);
        Assert.Equal(["Checkpoint"], summary.BestEffortFailedPhases);
    }
}
