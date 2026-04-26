namespace FSTService;

/// <summary>
/// Feature flags controlling optional UI features and rollout-dependent capabilities.
/// Loaded from appsettings.json / environment variables.
/// </summary>
public sealed class FeatureOptions
{
    public const string Section = "Features";

    /// <summary>
    /// Rank history charts on the leaderboards overview page and player details pages.
    /// The /leaderboards and /leaderboards/all pages themselves are always available;
    /// this flag only gates the rank-history chart rendering and associated queries.
    /// </summary>
    public bool Leaderboards { get; set; }

    /// <summary>Difficulty pill on leaderboard and score history rows.</summary>
    public bool Difficulty { get; set; }

    /// <summary>Player details page bands section.</summary>
    public bool PlayerBands { get; set; }

    /// <summary>Experimental leaderboard ranking metrics and related UI.</summary>
    public bool ExperimentalRanks { get; set; }

    /// <summary>
    /// When true, the scrape pipeline computes per-bucket ranking deltas for
    /// leeway-aware global rankings. When false, global rankings always use
    /// the base 1.05× CHOpt threshold with no per-leeway adjustments.
    /// All delta code is preserved; this flag only gates computation.
    /// </summary>
    public bool ComputeRankingDeltas { get; set; }

    /// <summary>
    /// When true, leeway-aware ranking reads use interval-tier resolution
    /// instead of exact-bucket dense delta lookups. Dense path is retained as fallback.
    /// Only meaningful when <see cref="ComputeRankingDeltas"/> is also true.
    /// </summary>
    public bool UseRankingDeltaTiers { get; set; } = true;

    /// <summary>
    /// When true, scrape spool flushes continue to maintain the legacy mutable
    /// leaderboard_entries table. When false, scrape flushes write snapshot
    /// current-state rows only and leave legacy live rows unchanged for rollback.
    /// </summary>
    public bool WriteLegacyLiveLeaderboardDuringScrape { get; set; } = true;

    /// <summary>
    /// Compete page. Always enabled; the flag derivation is retained only for API
    /// shape compatibility and is expected to be removed alongside this property.
    /// </summary>
    public bool Compete => true;
}
