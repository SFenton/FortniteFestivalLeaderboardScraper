namespace FSTService.Persistence;

public sealed class BandRankHistorySnapshotOptions
{
    public BandRankHistoryWriteMode WriteMode { get; init; } = BandRankHistoryWriteMode.Legacy;
    public bool UseLatestState { get; init; } = true;
    public bool UseNarrowHistory { get; init; } = true;
    public bool UseWideHistoryCompatibilityWrite { get; init; } = true;
    public bool RangeChunkingEnabled { get; init; } = true;
    public int ChunkSize { get; init; } = 250_000;
    public bool SynchronousCommitOff { get; init; }
    public int CommandTimeoutSeconds { get; init; }
    public int RetentionDays { get; init; } = 365;
    public bool CleanupRetention { get; init; } = true;
}

public sealed class BandRankHistorySnapshotResult
{
    public long RowsScanned { get; init; }
    public long RowsInserted { get; init; }
    public long RowsSkipped { get; init; }
    public int ChunksCompleted { get; init; }
    public int ChunksTotal { get; init; }
}

public sealed class BandRankHistoryV2BackfillOptions
{
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public string? RankingScope { get; init; }
    public string? ComboId { get; init; }
    public bool Execute { get; init; }
    public bool SynchronousCommitOff { get; init; }
    public int CommandTimeoutSeconds { get; init; }
}

public sealed class BandRankHistoryV2BackfillResult
{
    public string BandType { get; init; } = "";
    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
    public bool Execute { get; init; }
    public long LegacyRows { get; init; }
    public long ExistingV2Rows { get; init; }
    public long MissingV2Rows { get; init; }
    public long SnapshotRowsUpserted { get; init; }
    public long PointRowsInserted { get; init; }
    public long LatestRowsUpserted { get; init; }
    public int SlicesTotal { get; init; }
    public int SlicesBackfilled { get; init; }
    public IReadOnlyList<BandRankHistoryV2BackfillSlice> Slices { get; init; } = [];
}

public sealed class BandRankHistoryV2BackfillSlice
{
    public string BandType { get; init; } = "";
    public string SnapshotDate { get; init; } = "";
    public string RankingScope { get; init; } = "";
    public string ComboId { get; init; } = "";
    public long LegacyRows { get; init; }
    public long ExistingV2Rows { get; init; }
    public long MissingV2Rows { get; init; }
    public long CompleteSnapshots { get; init; }
    public long SnapshotRowsUpserted { get; init; }
    public long PointRowsInserted { get; init; }
    public long LatestRowsUpserted { get; init; }
}

public sealed class BandRankHistoryV2ParitySummary
{
    public string BandType { get; init; } = "";
    public string? RankingScope { get; init; }
    public string? ComboId { get; init; }
    public string SnapshotDate { get; init; } = "";
    public long LegacyRows { get; init; }
    public long V2Rows { get; init; }
    public long MatchingRows { get; init; }
    public long MissingFromV2 { get; init; }
    public long MissingFromLegacy { get; init; }
    public long ValueMismatches { get; init; }
    public long CompleteSnapshots { get; init; }
    public long IncompleteSnapshots { get; init; }
    public long V2SnapshotSourceRows { get; init; }
    public long LegacyStatsRows { get; init; }
    public IReadOnlyList<BandRankHistoryParityMismatchSample> Samples { get; init; } = [];
}

public sealed class BandRankHistoryV2LatestParitySummary
{
    public string BandType { get; init; } = "";
    public string? RankingScope { get; init; }
    public string? ComboId { get; init; }
    public string SnapshotDate { get; init; } = "";
    public long V2PointRows { get; init; }
    public long LatestRowsForSnapshot { get; init; }
    public long MatchingLatestRows { get; init; }
    public long MissingFromLatest { get; init; }
    public long LatestMismatches { get; init; }
    public long ExtraLatestRowsForSnapshot { get; init; }
    public IReadOnlyList<BandRankHistoryParityMismatchSample> Samples { get; init; } = [];
}

public sealed class BandRankHistoryV2ReadPreview
{
    public string BandType { get; init; } = "";
    public string RankingScope { get; init; } = "";
    public string ComboId { get; init; } = "";
    public string TeamKey { get; init; } = "";
    public int Days { get; init; }
    public int LegacyRows { get; init; }
    public int V2OnlyRows { get; init; }
    public int CurrentV2FallbackRows { get; init; }
    public int MergedRows { get; init; }
    public bool CurrentV2FallbackWouldHideLegacyDates { get; init; }
    public IReadOnlyList<string> LegacyDates { get; init; } = [];
    public IReadOnlyList<string> V2Dates { get; init; } = [];
    public IReadOnlyList<string> CurrentV2FallbackDates { get; init; } = [];
    public IReadOnlyList<string> MergedDates { get; init; } = [];
    public IReadOnlyList<string> LegacyDatesHiddenByCurrentV2Fallback { get; init; } = [];
    public IReadOnlyList<string> LegacyDatesMissingFromV2 { get; init; } = [];
}

public sealed class BandRankHistoryWideNarrowParitySummary
{
    public string BandType { get; init; } = "";
    public string? RankingScope { get; init; }
    public string? ComboId { get; init; }
    public string SnapshotDate { get; init; } = "";
    public long WideRows { get; init; }
    public long NarrowRows { get; init; }
    public long MatchingRows { get; init; }
    public long MissingFromNarrow { get; init; }
    public long MissingFromWide { get; init; }
    public long ValueMismatches { get; init; }
    public IReadOnlyList<BandRankHistoryParityMismatchSample> Samples { get; init; } = [];
}

public sealed class BandRankHistoryParityMismatchSample
{
    public string BandType { get; init; } = "";
    public string RankingScope { get; init; } = "";
    public string ComboId { get; init; } = "";
    public string TeamKey { get; init; } = "";
    public string SnapshotDate { get; init; } = "";
    public string MismatchKind { get; init; } = "";
    public IReadOnlyList<string> MismatchedColumns { get; init; } = [];
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
    public int ChunkOrdinal { get; init; }
    public string? TeamKeyStart { get; init; }
    public string? TeamKeyEnd { get; init; }
    public long EstimatedRows { get; init; }
    public long SourceGeneration { get; init; }
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
