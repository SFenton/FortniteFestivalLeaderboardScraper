namespace FSTService.Scraping;

public sealed record PhaseProgressDescriptor(
    string Id,
    string Label,
    string LegacyPhase,
    int Ordinal,
    string? TrackerOperation = null,
    string? BranchId = null,
    string? OperationKey = null,
    string? DefaultUnitsKind = null);

public static class PhaseProgressCatalog
{
    public const string PlanVersion = "fst.scrape-plan.v2";
    public const string OperationId = "scrape.update";

    private static readonly PhaseProgressDescriptor[] Descriptors =
    [
        new("scrape.leaderboards", "Scraping leaderboard scores", "Scraping", 100,
            TrackerOperation: "Scraping", OperationKey: "scrape.leaderboards", DefaultUnitsKind: "leaderboards"),
        new("post.rank_recompute", "Recomputing changed ranks", "RankRecompute", 200,
            TrackerOperation: "PostScrapeEnrichment", BranchId: "rank_recompute", DefaultUnitsKind: "songs"),
        new("post.first_seen_season", "Calculating first-seen seasons", "FirstSeenSeason", 210,
            TrackerOperation: "PostScrapeEnrichment", BranchId: "first_seen", DefaultUnitsKind: "songs"),
        new("post.account_name_resolution", "Resolving account names", "AccountNameResolution", 220,
            TrackerOperation: "PostScrapeEnrichment", BranchId: "name_resolution", DefaultUnitsKind: "batches"),
        new("post.refresh_registered_users", "Refreshing registered users", "RefreshRegisteredUsers", 230,
            TrackerOperation: "RefreshingRegisteredUsers", DefaultUnitsKind: "songs"),
        new("post.activate_shadow_snapshots_early", "Activating candidate snapshots for derived reads", "ActivateShadowSnapshotsEarly", 240,
            TrackerOperation: "PostScrapeEnrichment", DefaultUnitsKind: "steps"),
        new("post.band_extraction", "Extracting band context", "BandExtraction", 250,
            TrackerOperation: "BandScraping", DefaultUnitsKind: "songs"),
        new("post.legacy_band_scrape", "Completing legacy band scrape", "LegacyBandScrape", 260,
            TrackerOperation: "BandScraping"),
        new("post.registered_player_band_discovery", "Discovering registered-player bands", "RegisteredPlayerBandDiscovery", 270,
            TrackerOperation: "SongMachine", DefaultUnitsKind: "accounts"),
        new("post.registered_band_targeted_processing", "Processing registered bands", "RegisteredBandTargetedProcessing", 280,
            TrackerOperation: "SongMachine", DefaultUnitsKind: "bands"),
        new("post.deferred_registration_sync", "Synchronizing deferred registrations", "DeferredRegistrationSync", 290,
            TrackerOperation: "RefreshingRegisteredUsers", DefaultUnitsKind: "accounts"),
        new("post.band_maintenance", "Maintaining band projections", "BandMaintenance", 300,
            TrackerOperation: "BandScraping", DefaultUnitsKind: "scopes"),
        new("post.compute_rankings", "Computing rankings", "ComputeRankings", 310,
            TrackerOperation: "ComputingRankings", DefaultUnitsKind: "instruments"),
        new("post.prepare_solo_current_projection", "Preparing solo current projections", "PrepareSoloCurrentProjectionForDerived", 320,
            TrackerOperation: "PostScrapeEnrichment", DefaultUnitsKind: "scopes"),
        new("post.rivals", "Computing player rivals", "Rivals", 330,
            TrackerOperation: "ComputingRivals", DefaultUnitsKind: "accounts"),
        new("post.leaderboard_rivals", "Computing leaderboard rivals", "LeaderboardRivals", 340,
            TrackerOperation: "ComputingRivals", DefaultUnitsKind: "accounts"),
        new("post.player_stats_tiers", "Computing player statistics", "PlayerStatsTiers", 350,
            TrackerOperation: "Precomputing", DefaultUnitsKind: "accounts"),
        new("post.checkpoint", "Checkpointing candidate data", "Checkpoint", 360,
            TrackerOperation: "Finalizing", DefaultUnitsKind: "steps"),
        new("post.activate_shadow_snapshots", "Finalizing candidate snapshots", "ActivateShadowSnapshots", 370,
            TrackerOperation: "Finalizing", DefaultUnitsKind: "scopes"),
        new("post.seal_solo_current_projection", "Sealing solo projection scopes", "SealSoloCurrentProjectionScopes", 380,
            TrackerOperation: "Finalizing", DefaultUnitsKind: "scopes"),
        new("post.cleanup_solo_current_projection", "Refreshing solo current projections", "Cleanup.SoloCurrentProjection", 400,
            TrackerOperation: "Cleanup", DefaultUnitsKind: "scopes"),
        new("post.cleanup_precompute_all", "Precomputing published API responses", "Cleanup.PrecomputeAll", 410,
            TrackerOperation: "Cleanup", DefaultUnitsKind: "accounts"),
        new("post.cleanup_solo_excess_entries", "Cleaning excess solo entries", "Cleanup.SoloExcessEntries", 430,
            TrackerOperation: "Cleanup", DefaultUnitsKind: "steps"),
        new("post.cleanup_rank_history_retention", "Cleaning solo rank history", "Cleanup.RankHistoryRetention", 440,
            TrackerOperation: "Cleanup", DefaultUnitsKind: "instruments"),
        new("post.cleanup_band_rank_history_retention", "Cleaning band rank history", "Cleanup.BandRankHistoryRetention", 450,
            TrackerOperation: "Cleanup", DefaultUnitsKind: "band_types"),
        new("post.cleanup_service_level_retention", "Planning service-level retention", "Cleanup.ServiceLevelRetention", 460,
            TrackerOperation: "Cleanup", DefaultUnitsKind: "steps"),
        new("publication.commit", "Publishing leaderboard update", "Publishing", 900,
            TrackerOperation: "Publishing", OperationKey: "scrape.publication", DefaultUnitsKind: "steps"),
        new("post.improvement_notifications", "Detecting improvement notifications", "ImprovementNotifications", 920,
            TrackerOperation: "Cleanup", DefaultUnitsKind: "scopes"),
    ];

    private static readonly IReadOnlyDictionary<string, PhaseProgressDescriptor> ByPostScrapeName =
        Descriptors
            .Where(descriptor => descriptor.Id.StartsWith("post.", StringComparison.Ordinal))
            .ToDictionary(descriptor => descriptor.LegacyPhase, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, PhaseProgressDescriptor> ByOperationKey =
        Descriptors
            .Where(descriptor => descriptor.OperationKey is not null)
            .ToDictionary(descriptor => descriptor.OperationKey!, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, PhaseProgressDescriptor> ById =
        Descriptors.ToDictionary(descriptor => descriptor.Id, StringComparer.Ordinal);

    public static IReadOnlyList<PhaseProgressDescriptor> All { get; } =
        Array.AsReadOnly(Descriptors);

    public static PhaseProgressDescriptor? FindPostScrape(string phaseName) =>
        ByPostScrapeName.GetValueOrDefault(phaseName);

    public static PhaseProgressDescriptor? FindByOperationKey(string operationKey) =>
        ByOperationKey.GetValueOrDefault(operationKey);

    public static PhaseProgressDescriptor? FindById(string phaseId) =>
        ById.GetValueOrDefault(phaseId);
}
