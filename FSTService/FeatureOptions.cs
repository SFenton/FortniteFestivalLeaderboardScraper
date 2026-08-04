namespace FSTService;

/// <summary>
/// Feature flags controlling persistence and publication rollout capabilities.
/// Loaded from appsettings.json / environment variables.
/// </summary>
public sealed class FeatureOptions
{
    public const string Section = "Features";

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

}
