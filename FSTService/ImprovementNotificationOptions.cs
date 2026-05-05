namespace FSTService;

/// <summary>
/// Controls scrape-triggered improvement notification detection.
/// </summary>
public sealed class ImprovementNotificationOptions
{
    public const string Section = "ImprovementNotifications";

    /// <summary>Enable notification detection during post-scrape.</summary>
    public bool Enabled { get; set; }

    /// <summary>Detection scope. Start with registered; all-user mode is a later rollout.</summary>
    public string Scope { get; set; } = "registered";

    public bool IncludePlayers { get; set; } = true;
    public bool IncludeBands { get; set; } = true;
    public bool IncludeSongEvents { get; set; } = true;
    public bool IncludeRankings { get; set; } = true;
    public bool PruneExpired { get; set; } = true;

    /// <summary>Refresh solo current projection before player song event detection.</summary>
    public bool RefreshSoloProjection { get; set; } = true;

    /// <summary>
    /// Rebuild all solo current projection scopes when no impacted scopes can be derived.
    /// Disabled by default because first rollout should avoid surprise full-table work.
    /// </summary>
    public bool RefreshAllSoloScopesWhenNoImpactedScopes { get; set; }

    /// <summary>Command timeout for notification SQL. 0 means unlimited.</summary>
    public int CommandTimeoutSeconds { get; set; }

    /// <summary>Optional timeout for solo projection refresh SQL. 0 means unlimited.</summary>
    public int SoloProjectionCommandTimeoutSeconds { get; set; }

    /// <summary>
    /// Minimum fraction of expected solo leaderboard scopes that must return data before
    /// post-scrape detection runs. Set to 0 to disable the coverage guard.
    /// </summary>
    public double MinimumSoloLeaderboardCoverageRatio { get; set; } = 0.95;

    /// <summary>When true, notification failures fail the post-scrape pass.</summary>
    public bool FailScrapeOnError { get; set; }
}