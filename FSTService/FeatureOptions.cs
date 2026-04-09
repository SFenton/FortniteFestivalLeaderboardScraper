namespace FSTService;

/// <summary>
/// Feature flags controlling which UI features are enabled.
/// Loaded from appsettings.json / environment variables.
/// </summary>
public sealed class FeatureOptions
{
    public const string Section = "Features";

    /// <summary>Rivals pages and navigation links.</summary>
    public bool Rivals { get; set; }

    /// <summary>Leaderboards overview and full rankings pages.</summary>
    public bool Leaderboards { get; set; }

    /// <summary>First-run experience carousels on every page.</summary>
    public bool FirstRun { get; set; }

    /// <summary>Difficulty pill on leaderboard and score history rows.</summary>
    public bool Difficulty { get; set; }

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
    /// Compete page — derived from <see cref="Rivals"/> AND <see cref="Leaderboards"/>.
    /// CompetePage links to both rivals and rankings; if either is off, compete is off.
    /// </summary>
    public bool Compete => Rivals && Leaderboards;
}
