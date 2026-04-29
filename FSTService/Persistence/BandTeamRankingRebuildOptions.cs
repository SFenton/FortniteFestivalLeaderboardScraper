namespace FSTService.Persistence;

public enum BandTeamRankingWriteMode
{
    ComboBatched,
    Monolithic,
    Phased,
}

public sealed class BandTeamRankingRebuildOptions
{
    public const string Section = "BandTeamRankings";

    public static BandTeamRankingRebuildOptions Default { get; } = new();

    public BandTeamRankingWriteMode WriteMode { get; set; } = BandTeamRankingWriteMode.Monolithic;
    public int CommandTimeoutSeconds { get; set; } = 0;
    public bool AnalyzeStagingTable { get; set; } = false;
    public bool DisableSynchronousCommit { get; set; } = true;

    /// <summary>
    /// Run standard rank-history snapshots concurrently with band ranking rebuilds, then await both
    /// before the rankings pass completes. This preserves scrape completion gating while reducing
    /// wall clock when PostgreSQL can absorb the overlap.
    /// </summary>
    public bool OverlapRankHistorySnapshotsWithBandRankings { get; set; } = false;
}

public sealed record BandTeamRankingRebuildMetrics(
    string BandType,
    BandTeamRankingWriteMode WriteMode,
    int ResultRowCount,
    int StatsRowCount,
    int DistinctComboCount,
    double MaterializeResultsMs,
    double AnalyzeResultsMs,
    double DeleteExistingMs,
    double InsertRankingsMs,
    double InsertStatsMs,
    double TotalElapsedMs);