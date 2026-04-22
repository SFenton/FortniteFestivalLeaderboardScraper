namespace FSTService;

/// <summary>
/// Feature flags controlling optional UI features and rollout-dependent capabilities.
/// Loaded from appsettings.json / environment variables.
/// </summary>
public sealed class FeatureOptions
{
    public const string Section = "Features";

    /// <summary>Leaderboards overview and full rankings pages.</summary>
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
    /// Compete page — derived from <see cref="Leaderboards"/>.
    /// Rivals are always available; Compete stays gated by the leaderboards rollout.
    /// </summary>
    public bool Compete => Leaderboards;
}
