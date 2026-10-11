namespace FSTService;

public sealed class DatabaseMaintenanceOptions
{
    public const string Section = "DatabaseMaintenance";
    public const int DefaultCleanupBatchSize = 5_000;
    public const int DefaultCleanupMaxBatches = 1;
    public const long DefaultSnapshotRetentionMinimumEstimatedPurgeBytes = 1L * 1024 * 1024 * 1024;

    public const int DefaultRankHistoryRetentionDays = 30;

    public bool SkipCleanupWhenPressureDetected { get; set; } = true;

    /// <summary>
    /// Days of solo and composite rank history kept. Each account also keeps its
    /// newest row at or before the cutoff, so a chart can start at the cutoff.
    /// </summary>
    public int RankHistoryRetentionDays { get; set; } = DefaultRankHistoryRetentionDays;

    /// <summary>
    /// Per-table statement timeout for the daily rank-history retention delete;
    /// 0 waits indefinitely (the phase is still cancellable).
    /// </summary>
    public int RankHistoryRetentionCommandTimeoutSeconds { get; set; } = 900;
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
    public int ServiceMaintenanceLockWaitMilliseconds { get; set; } = 500;
    public bool SnapshotGenerationRetentionReportOnlyEnabled { get; set; } = false;
    public int SnapshotGenerationRetentionCommandTimeoutSeconds { get; set; } = 30;
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
        "composite_rank_history",
        "rank_history",
    ];
}