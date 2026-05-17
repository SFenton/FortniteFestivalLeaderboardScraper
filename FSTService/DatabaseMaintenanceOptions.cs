namespace FSTService;

public sealed class DatabaseMaintenanceOptions
{
    public const string Section = "DatabaseMaintenance";
    public const int DefaultCleanupBatchSize = 5_000;
    public const int DefaultCleanupMaxBatches = 1;
    public const long DefaultSnapshotRetentionMinimumEstimatedPurgeBytes = 1L * 1024 * 1024 * 1024;

    public bool SkipCleanupWhenPressureDetected { get; set; } = true;
    public int RankHistoryCleanupBatchSize { get; set; } = DefaultCleanupBatchSize;
    public int RankHistoryCleanupMaxBatches { get; set; } = DefaultCleanupMaxBatches;
    public int BandRankHistoryCleanupBatchSize { get; set; } = DefaultCleanupBatchSize;
    public int BandRankHistoryCleanupMaxBatches { get; set; } = DefaultCleanupMaxBatches;
    public int CleanupCommandTimeoutSeconds { get; set; } = 120;
    public bool ServiceLevelRetentionMaintenanceEnabled { get; set; } = true;
    public bool SnapshotRetentionRewriteEnabled { get; set; } = false;
    public bool SnapshotRetentionReportOnlyWhenDisabled { get; set; } = true;
    public int SnapshotRetentionRollbackCompletedSnapshotsToKeep { get; set; } = 1;
    public int SnapshotRetentionMaxPartitionsPerRun { get; set; } = 1;
    public long SnapshotRetentionMinimumEstimatedPurgeBytes { get; set; } = DefaultSnapshotRetentionMinimumEstimatedPurgeBytes;
    public long SnapshotRetentionMaximumEstimatedRetainedBytes { get; set; } = 0;
    public string? SnapshotRetentionFreeSpacePath { get; set; }
    public long SnapshotRetentionMinimumFreeBytes { get; set; } = 0;
    public bool MetadataTtlCleanupEnabled { get; set; } = true;
    public int MetadataRetentionDays { get; set; } = 180;
    public int MetadataCleanupBatchSize { get; set; } = DefaultCleanupBatchSize;
    public int MetadataCleanupMaxBatches { get; set; } = DefaultCleanupMaxBatches;
    public int CompletedScrapeLogRowsToKeep { get; set; } = 100;
    public bool DeferredServiceLevelRetentionEnabled { get; set; } = true;
    public int DeferredServiceLevelRetentionInitialDelaySeconds { get; set; } = 60;
    public int DeferredServiceLevelRetentionPollSeconds { get; set; } = 60;
    public int DeferredServiceLevelRetentionMaxAttempts { get; set; } = 30;
    public int DeferredServiceLevelRetentionMaxRuntimeMinutes { get; set; } = 90;
    public int LongRunningMaintenanceSeconds { get; set; } = 30;
    public long WatchedTableDeadTupleThreshold { get; set; } = 10_000_000;
    public string[] WatchedTables { get; set; } =
    [
        "leaderboard_entries_snapshot",
        "band_team_rank_history",
        "band_team_rank_history_points",
        "band_team_ranking_stats_history",
        "band_song_team_rankings",
        "composite_rank_history",
        "rank_history",
    ];
}