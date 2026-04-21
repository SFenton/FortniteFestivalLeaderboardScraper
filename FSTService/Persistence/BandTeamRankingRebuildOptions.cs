namespace FSTService.Persistence;

public enum BandTeamRankingWriteMode
{
    ComboBatched,
    Monolithic,
}

public sealed class BandTeamRankingRebuildOptions
{
    public static BandTeamRankingRebuildOptions Default { get; } = new();

    public BandTeamRankingWriteMode WriteMode { get; init; } = BandTeamRankingWriteMode.ComboBatched;
    public int CommandTimeoutSeconds { get; init; } = 600;
    public bool AnalyzeStagingTable { get; init; } = true;
    public bool DisableSynchronousCommit { get; init; } = true;
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