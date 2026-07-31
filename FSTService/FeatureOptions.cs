using Microsoft.Extensions.Options;

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

    /// <summary>App Manual page and navigation entry points.</summary>
    public bool AppManual { get; set; }

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
    /// When true, backfill, refresh, and neighbor writes continue to dual-write
    /// the legacy mutable leaderboard_entries table in addition to the
    /// authoritative overlay. Keep enabled until legacy-only worker readers,
    /// including band extraction, are migrated and pass a full scrape A/B.
    /// </summary>
    public bool WriteLegacyLiveLeaderboardSupplementalRows { get; set; } = true;

    /// <summary>
    /// When true, durable score_history writes also maintain the non-authoritative
    /// solo-history rows in player_score_observations.
    /// </summary>
    public bool WriteSoloScoreObservations { get; set; }

    /// <summary>
    /// When true, band member fact writes also maintain the non-authoritative
    /// band-member rows in player_score_observations.
    /// </summary>
    public bool WriteBandMemberScoreObservations { get; set; }

    /// <summary>
    /// When true, scrape flushes compute observe-only per-song/instrument content
    /// fingerprints so future work can skip unchanged physical snapshot writes.
    /// Existing snapshot/current-state behavior remains authoritative.
    /// </summary>
    public bool UseLeaderboardScopeFingerprints { get; set; } = true;

    /// <summary>
    /// When true, solo snapshot flushes skip physically writing scopes whose
    /// complete content and coverage fingerprints match the current published
    /// scope source. Snapshot state remains pinned to that validated published
    /// physical source. Published-source writes and strict manifests are required.
    /// </summary>
    public bool SkipUnchangedPhysicalLeaderboardSnapshots { get; set; }

    /// <summary>
    /// When true, the worker records, validates, and atomically promotes a
    /// per-scope physical source for each published solo leaderboard.
    /// </summary>
    public bool WritePublishedScopeSources { get; set; }

    /// <summary>
    /// When true, every expected solo scope must have a complete page manifest
    /// before its published-source candidate can be promoted.
    /// </summary>
    public bool EnforceScopeCompletenessManifests { get; set; }

    /// <summary>
    /// When true, any solo, band, or bounded-online writer failure rejects the
    /// candidate scrape after retaining replay artifacts.
    /// </summary>
    public bool RequireSuccessfulScrapeWriters { get; set; }

    /// <summary>
    /// When true, failures in explicitly publication-critical post-scrape phases
    /// reject the candidate while best-effort failures remain visible warnings.
    /// </summary>
    public bool EnforcePublicationCriticalPhases { get; set; }

    /// <summary>
    /// Enables request publication pinning only after every publication-bound
    /// surface has a generation-addressable binding.
    /// </summary>
    public bool EnablePublicationReadContext { get; set; }

    /// <summary>
    /// When true, service-side current leaderboard reads and published exports
    /// resolve through the per-scope published source map. Keep disabled on the
    /// worker so post-process calculations continue to use the active candidate.
    /// </summary>
    public bool UsePublishedScopeSources { get; set; }

    /// <summary>
    /// When true, filtered solo projection reads preserve the already-published
    /// projection order by re-ranking on stored rank instead of re-sorting the
    /// same rows by score and tie-break fields.
    /// </summary>
    public bool UseStoredSoloProjectionRanksForFilteredReads { get; set; }

    /// <summary>
    /// Retired logical current/version shadow writer. This must remain false
    /// until a future versioned migration and live-scrape promotion explicitly
    /// restore an owner for the shadow tables.
    /// </summary>
    public bool WriteLogicalLeaderboardVersions { get; set; }

    /// <summary>
    /// Compete page. Always enabled; the flag derivation is retained only for API
    /// shape compatibility and is expected to be removed alongside this property.
    /// </summary>
    public bool Compete => true;
}

public sealed class FeatureOptionsValidator : IValidateOptions<FeatureOptions>
{
    public const string RetiredLogicalLeaderboardShadowMessage =
        "Features:WriteLogicalLeaderboardVersions is retired. Re-enabling it requires a versioned migration, rebuild/restore validation, and a new live-scrape promotion.";

    public ValidateOptionsResult Validate(string? name, FeatureOptions options)
    {
        return options.WriteLogicalLeaderboardVersions
            ? ValidateOptionsResult.Fail(RetiredLogicalLeaderboardShadowMessage)
            : ValidateOptionsResult.Success;
    }
}
