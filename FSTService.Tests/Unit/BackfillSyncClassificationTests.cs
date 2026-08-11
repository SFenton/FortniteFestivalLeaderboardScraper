using FSTService.Persistence;

namespace FSTService.Tests.Unit;

public sealed class BackfillSyncClassificationTests
{
    [Fact]
    public void LegacyDeferredCatalogRefresh_IsBackground()
    {
        var result = BackfillSyncClassification.IsBackgroundRefresh(
            new BackfillStatusInfo
            {
                Status = "deferred",
                DeferredReason = BackfillDeferredReasons.WorkerQueue,
            },
            new HistoryReconStatusInfo
            {
                Status = "complete",
                CompletedAt = "2026-08-07T06:22:32Z",
            },
            new BackfillSongProgressInfo
            {
                SongsChecked = 699,
                TotalSongs = 702,
            });

        Assert.True(result);
    }

    [Fact]
    public void FirstTimeInProgressSync_IsNotBackground()
    {
        var result = BackfillSyncClassification.IsBackgroundRefresh(
            new BackfillStatusInfo
            {
                Status = "in_progress",
                StartedAt = "2026-08-11T15:00:00Z",
            },
            new HistoryReconStatusInfo
            {
                Status = "complete",
                CompletedAt = "2026-08-11T15:05:00Z",
            },
            new BackfillSongProgressInfo
            {
                SongsChecked = 1,
                TotalSongs = 702,
            });

        Assert.False(result);
    }
}
