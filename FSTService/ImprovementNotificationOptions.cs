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

    /// <summary>
    /// Alert when a required detection lane has not completed for this many
    /// newly completed/published scrapes.
    /// </summary>
    public int StaleAfterPublishedScrapes { get; set; } = 1;

    /// <summary>Alert when the oldest required detection lane is this many hours old.</summary>
    public int StaleAfterHours { get; set; } = 24;

    /// <summary>How often the API service checks and logs notification staleness.</summary>
    public TimeSpan StalenessCheckInterval { get; set; } = TimeSpan.FromMinutes(15);
}

public static class ImprovementNotificationSafetyContract
{
    public const string RoutineScoreObservationPurpose = "routine_score_observation_v1";
    public const string RoutineScoreObservationCause = "score_observation";
    public const string RoutineItemShopPurpose = "routine_item_shop_observation_v1";
    public const string RoutineItemShopCause = "item_shop_observation";
    public const string ProLeadMaxScoreRepairPurpose = "maintenance_pro_lead_max_score_repair_v1";
    public const string MaxScoreRecomputeCause = "max_score_recompute";
    public const string VisibleDeliveryState = "visible";
    public const string QuarantinedDeliveryState = "quarantined";

    // This is intentionally a compile-time contract, not configuration.
    public const int ProLeadMaxScoreRepairVisibleDeliveryCap = 0;
}