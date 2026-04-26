namespace FSTService.Persistence;

public sealed class BandRankHistorySnapshotOptions
{
    public bool UseLatestState { get; init; } = true;
    public bool UseNarrowHistory { get; init; } = true;
    public bool UseWideHistoryCompatibilityWrite { get; init; } = true;
    public bool SynchronousCommitOff { get; init; }
    public int CommandTimeoutSeconds { get; init; }
    public int RetentionDays { get; init; } = 365;
}

public sealed class BandRankHistorySnapshotResult
{
    public long RowsScanned { get; init; }
    public long RowsInserted { get; init; }
    public long RowsSkipped { get; init; }
    public int ChunksCompleted { get; init; }
    public int ChunksTotal { get; init; }
}

public sealed class BandRankHistoryJobInfo
{
    public long JobId { get; init; }
    public long ScrapeId { get; init; }
    public string SnapshotDate { get; init; } = "";
    public string BandType { get; init; } = "";
    public string Mode { get; init; } = "";
    public string Status { get; init; } = "";
    public string? StartedAt { get; init; }
    public string? CompletedAt { get; init; }
    public string? FailedAt { get; init; }
    public string? PausedAt { get; init; }
    public string? SupersededAt { get; init; }
    public string? LastError { get; init; }
    public int Attempts { get; init; }
    public int ChunksTotal { get; init; }
    public int ChunksCompleted { get; init; }
    public long RowsScanned { get; init; }
    public long RowsInserted { get; init; }
    public long RowsSkipped { get; init; }
    public string? CurrentRankingScope { get; init; }
    public string? CurrentComboId { get; init; }
    public string UpdatedAt { get; init; } = "";
}

public sealed class BandRankHistoryChunkInfo
{
    public long JobId { get; init; }
    public string BandType { get; init; } = "";
    public string RankingScope { get; init; } = "";
    public string ComboId { get; init; } = "";
    public string Status { get; init; } = "";
}

public sealed class BandRankHistoryStatusDto
{
    public string HistoryStatus { get; init; } = "current";
    public string? CurrentRankingsComputedAt { get; init; }
    public string? HistoryComputedThrough { get; init; }
    public string? HistoryJobUpdatedAt { get; init; }
    public string? HistoryMessage { get; init; }
}
