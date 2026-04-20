namespace FSTService;

public static class RivalsDirtyReason
{
    public const string SelfScoreChange = "self_score_change";
    public const string SelfRankChange = "self_rank_change";
    public const string NeighborWindowChange = "neighbor_window_change";
    public const string EligibilityChange = "eligibility_change";
    public const string AlgorithmVersionBump = "algorithm_version_bump";
}