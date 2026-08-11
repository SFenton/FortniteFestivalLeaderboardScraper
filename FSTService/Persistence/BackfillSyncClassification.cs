namespace FSTService.Persistence;

public static class BackfillDeferredReasons
{
    public const string WorkerQueue = "worker_backfill_queue";
    public const string CatalogRefreshQueue = "catalog_refresh_queue";

    public static bool IsCatalogRefresh(string? reason)
        => string.Equals(
            reason,
            CatalogRefreshQueue,
            StringComparison.OrdinalIgnoreCase);
}

public static class BackfillSyncClassification
{
    public static bool IsBackgroundRefresh(
        BackfillStatusInfo? backfill,
        HistoryReconStatusInfo? historyRecon,
        BackfillSongProgressInfo? displayProgress)
    {
        if (backfill is null)
            return false;

        if (BackfillDeferredReasons.IsCatalogRefresh(backfill.DeferredReason))
            return true;

        return string.Equals(
                backfill.Status,
                "deferred",
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                backfill.DeferredReason,
                BackfillDeferredReasons.WorkerQueue,
                StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(backfill.StartedAt)
            && string.Equals(
                historyRecon?.Status,
                "complete",
                StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(historyRecon?.CompletedAt)
            && displayProgress?.SongsChecked > 0;
    }
}
