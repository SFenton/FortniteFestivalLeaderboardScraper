using FortniteFestival.Core.Scraping;
using FortniteFestival.Core.Services;
using FSTService;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Persistence.Maintenance;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

/// <summary>
/// Orchestrates the post-scrape enrichment phases: parallel rank/firstSeen/nameRes,
/// refresh of registered users, derived-state publication, and deferred cleanup.
/// Extracted from <see cref="ScraperWorker"/> to reduce its dependency count and
/// make each phase independently testable.
/// </summary>
public sealed class PostScrapeOrchestrator
{
    private static readonly TimeSpan RegisteredRefreshOperationHeartbeatInterval =
        TimeSpan.FromSeconds(15);

    internal const string BandMaintenanceTimingPhase = "BandMaintenance";
    internal const string BandMaintenancePruneSubphase = "prune";
    internal const string BandMaintenanceSearchProjectionSubphase = "search_projection_refresh";
    internal const string BandMaintenanceCurrentProjectionSubphase = "current_projection_refresh";
    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> EmptyImpactedTeams =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);

    internal readonly record struct BandMaintenanceTimingMetrics(
        long? RowsRead = null,
        long? RowsWritten = null,
        long? RowsDeleted = null,
        long? ScopeCount = null)
    {
        public static BandMaintenanceTimingMetrics NoWork { get; } =
            new(RowsRead: 0, RowsWritten: 0, RowsDeleted: 0, ScopeCount: 0);

        public static BandMaintenanceTimingMetrics Unknown { get; } = new();
    }

    private sealed record PhaseCompletion(string Status, string? WarningMessage = null)
    {
        public static PhaseCompletion Completed { get; } = new("completed");
    }

    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly FirstSeenSeasonCalculator _firstSeenCalculator;
    private readonly AccountNameResolver _nameResolver;
    private readonly HistoryReconstructor _historyReconstructor;
    private readonly SharedDopPool _pool;
    private readonly CyclicalSongMachine _cyclicalMachine;
    private readonly RivalsOrchestrator _rivalsOrchestrator;
    private readonly RankingsCalculator _rankingsCalculator;
    private readonly LeaderboardRivalsCalculator _leaderboardRivalsCalculator;
    private readonly NotificationService _notifications;
    private readonly TokenManager _tokenManager;
    private readonly ScrapeProgressTracker _progress;
    private readonly UserSyncProgressTracker _syncTracker;
    private readonly IPathDataStore _pathDataStore;
    private readonly ScrapeTimePrecomputer _precomputer;
    private readonly PostScrapeBandExtractor _bandExtractor;
    private readonly BandScrapePhase _bandScrapePhase;
    private readonly BandLeaderboardPersistence _bandPersistence;
    private readonly RegisteredPlayerBandDiscoveryOrchestrator? _registeredPlayerBandDiscoveryOrchestrator;
    private readonly RegisteredBandProcessingOrchestrator? _registeredBandProcessingOrchestrator;
    private readonly BandSearchProjectionBuilder? _bandSearchProjectionBuilder;
    private readonly BandCurrentProjectionBuilder? _bandCurrentProjectionBuilder;
    private readonly ImprovementNotificationService? _improvementNotifications;
    private readonly ImprovementNotificationRecoveryService? _improvementNotificationRecovery;
    private readonly SoloCurrentProjectionBuilder? _soloCurrentProjectionBuilder;
    private readonly IOptions<ImprovementNotificationOptions> _improvementNotificationOptions;
    private readonly IOptions<BandRankHistoryOptions> _bandRankHistoryOptions;
    private readonly IOptions<DatabaseMaintenanceOptions> _databaseMaintenanceOptions;
    private readonly IDatabasePressureMonitor? _databasePressureMonitor;
    private readonly IDatabaseRetentionMaintenanceService? _retentionMaintenanceService;
    private readonly IOptions<ScraperOptions> _options;
    private readonly ILogger<PostScrapeOrchestrator> _log;
    private readonly IPostScrapePhaseFaultInjector? _phaseFaultInjector;
    private readonly WorkerStatusPublisher? _workerStatus;
    private readonly DurablePhaseProgressSink? _phaseProgress;
    private readonly RegistrationMutationCoordinator
        _registrationMutations;

    public PostScrapeOrchestrator(
        GlobalLeaderboardPersistence persistence,
        FirstSeenSeasonCalculator firstSeenCalculator,
        AccountNameResolver nameResolver,
        HistoryReconstructor historyReconstructor,
        SharedDopPool pool,
        CyclicalSongMachine cyclicalMachine,
        RivalsOrchestrator rivalsOrchestrator,
        RankingsCalculator rankingsCalculator,
        LeaderboardRivalsCalculator leaderboardRivalsCalculator,
        NotificationService notifications,
        TokenManager tokenManager,
        ScrapeProgressTracker progress,
        UserSyncProgressTracker syncTracker,
        IPathDataStore IPathDataStore,
        ScrapeTimePrecomputer precomputer,
        PostScrapeBandExtractor bandExtractor,
        BandScrapePhase bandScrapePhase,
        BandLeaderboardPersistence bandPersistence,
        IOptions<ScraperOptions> options,
        ILogger<PostScrapeOrchestrator> log,
        RegistrationMutationCoordinator
            registrationMutations,
        BandSearchProjectionBuilder? bandSearchProjectionBuilder,
        RegisteredBandProcessingOrchestrator? registeredBandProcessingOrchestrator = null,
        RegisteredPlayerBandDiscoveryOrchestrator? registeredPlayerBandDiscoveryOrchestrator = null,
        BandCurrentProjectionBuilder? bandCurrentProjectionBuilder = null,
        ImprovementNotificationService? improvementNotifications = null,
        SoloCurrentProjectionBuilder? soloCurrentProjectionBuilder = null,
        IOptions<ImprovementNotificationOptions>? improvementNotificationOptions = null,
        IOptions<BandRankHistoryOptions>? bandRankHistoryOptions = null,
        IOptions<DatabaseMaintenanceOptions>? databaseMaintenanceOptions = null,
        IDatabasePressureMonitor? databasePressureMonitor = null,
        IDatabaseRetentionMaintenanceService? retentionMaintenanceService = null,
        IPostScrapePhaseFaultInjector? phaseFaultInjector = null,
        WorkerStatusPublisher? workerStatus = null,
        ImprovementNotificationRecoveryService? improvementNotificationRecovery = null,
        DurablePhaseProgressSink? phaseProgress = null)
    {
        _persistence = persistence;
        _firstSeenCalculator = firstSeenCalculator;
        _nameResolver = nameResolver;
        _historyReconstructor = historyReconstructor;
        _pool = pool;
        _cyclicalMachine = cyclicalMachine;
        _rivalsOrchestrator = rivalsOrchestrator;
        _rankingsCalculator = rankingsCalculator;
        _leaderboardRivalsCalculator = leaderboardRivalsCalculator;
        _notifications = notifications;
        _tokenManager = tokenManager;
        _progress = progress;
        _syncTracker = syncTracker;
        _pathDataStore = IPathDataStore;
        _precomputer = precomputer;
        _bandExtractor = bandExtractor;
        _bandScrapePhase = bandScrapePhase;
        _bandPersistence = bandPersistence;
        _registeredPlayerBandDiscoveryOrchestrator = registeredPlayerBandDiscoveryOrchestrator;
        _registeredBandProcessingOrchestrator = registeredBandProcessingOrchestrator;
        _bandSearchProjectionBuilder = bandSearchProjectionBuilder;
        _bandCurrentProjectionBuilder = bandCurrentProjectionBuilder;
        _improvementNotifications = improvementNotifications;
        _improvementNotificationRecovery = improvementNotificationRecovery;
        _soloCurrentProjectionBuilder = soloCurrentProjectionBuilder;
        _improvementNotificationOptions = improvementNotificationOptions ?? Options.Create(new ImprovementNotificationOptions());
        _bandRankHistoryOptions = bandRankHistoryOptions ?? Options.Create(new BandRankHistoryOptions());
        _databaseMaintenanceOptions = databaseMaintenanceOptions ?? Options.Create(new DatabaseMaintenanceOptions());
        _databasePressureMonitor = databasePressureMonitor;
        _retentionMaintenanceService = retentionMaintenanceService;
        _options = options;
        _log = log;
        _phaseFaultInjector = phaseFaultInjector;
        _workerStatus = workerStatus;
        _phaseProgress = phaseProgress;
        _registrationMutations = registrationMutations;
    }

    /// <summary>
    /// Run post-scrape phases gated by <paramref name="resolvedPhases"/>.
    /// When all phases are enabled this behaves identically to the original pipeline.
    /// </summary>
    public async Task RunAsync(ScrapePassContext ctx, FestivalService service, ScrapePhase resolvedPhases, CancellationToken ct)
    {
        using var projectionReadPass =
            _persistence.BeginValidatedCurrentProjectionReadPass();

        // ── Solo enrichment ──
        if (resolvedPhases.HasFlag(ScrapePhase.SoloEnrichment))
            await RunEnrichmentAsync(ctx, service, ct);

        var registeredUserRefreshResult = new SongProcessingMachine.MachineResult();

        // ── Solo refresh registered users ──
        if (resolvedPhases.HasFlag(ScrapePhase.SoloRefreshUsers))
        {
            registeredUserRefreshResult = await RunPhaseAsync(
                ctx,
                "RefreshRegisteredUsers",
                () => RefreshRegisteredUsersAsync(ctx, ct),
                new SongProcessingMachine.MachineResult(),
                alwaysPropagateFailure: true);
            foreach (var scope in registeredUserRefreshResult.UpdatedScopes)
                ctx.AddNotificationProjectionScope(scope);
        }

        var expectedSnapshotPairs = BuildExpectedSnapshotPairs(ctx);
        if (ShouldActivateShadowSnapshotsBeforeDerived(ctx, resolvedPhases))
        {
            _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.PostScrapeEnrichment);
            _progress.SetSubOperation("activating_shadow_snapshots_early");
            _progress.BeginPhaseProgress(1);
            await RunPhaseAsync(ctx, "ActivateShadowSnapshotsEarly", () =>
            {
                var activated = _persistence.FinalizeShadowSnapshots(ctx.ScrapeId, expectedPairs: expectedSnapshotPairs);
                _log.LogInformation(
                    "Activated shadow snapshot {ScrapeId} before derived readers ({Pairs} pair(s), {ExpectedPairs} expected).",
                    ctx.ScrapeId,
                    activated,
                    expectedSnapshotPairs.Count);
                _progress.ReportPhaseItemComplete();
                return Task.CompletedTask;
            });
        }

        // ── Band data collection (fire-and-forget background) ──
        // Skip if band data was already fetched via BandPageFetcher during the scrape pass.
        // BandScrape (new) uses the shared DOP pool inside ScrapeOrchestrator;
        // BandScrapePhase (legacy) is the old per-song sequential fetcher.
        Task? bandScrapeTask = null;
        if (ShouldLaunchLegacyBandScrape(resolvedPhases))
        {
            var chartedSongs = service.Songs.Where(s => s.track?.su is not null).ToList();
            var bandAccessToken = await _tokenManager.GetAccessTokenAsync(ct);
            if (bandAccessToken is not null)
            {
                var bandCallerAccountId = _tokenManager.AccountId!;
                var bandAccessTokenProvider = new ScrapeAccessTokenProvider(_tokenManager, bandAccessToken, _log);
                bandScrapeTask = Task.Run(
                    () => _bandScrapePhase.ExecuteAsync(chartedSongs, bandAccessToken, bandCallerAccountId, ct, bandAccessTokenProvider),
                    ct);
                _log.LogInformation("Band scrape launched in background ({Songs} songs).", chartedSongs.Count);
            }
            else
            {
                _log.LogWarning("No access token for band scrape. Will retry next pass.");
            }
        }

        // ── Band extraction (SQL-only) ──
        var bandExtractionResult = BandExtractionResult.Empty;
        if (resolvedPhases.HasFlag(ScrapePhase.BandExtraction))
        {
            _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.BandScraping);
            _progress.SetSubOperation("extracting_band_context");
            bandExtractionResult = await RunPhaseAsync(
                ctx,
                "BandExtraction",
                () => _bandExtractor.RunAsync(ctx.ScrapeId > 0 ? ctx.ScrapeId : null, ct),
                BandExtractionResult.Empty);
        }

        if (bandScrapeTask is not null)
        {
            try
            {
                await RunPhaseAsync(ctx, "LegacyBandScrape", () => bandScrapeTask);
                _log.LogInformation("Background band scrape completed successfully.");
            }
            finally
            {
                bandScrapeTask = null;
            }
        }

        var registeredPlayerBandDiscoveryResult = RegisteredPlayerBandDiscoveryResult.Empty;
        if (ShouldRunRegisteredPlayerBandDiscovery(resolvedPhases))
        {
            var registeredPlayerBandDiscoveryOrchestrator = _registeredPlayerBandDiscoveryOrchestrator!;
            var bandAccessToken = await _tokenManager.GetAccessTokenAsync(ct);
            if (bandAccessToken is not null)
            {
                var chartedSongIds = service.Songs
                    .Select(static song => song.track?.su)
                    .Where(static songId => !string.IsNullOrWhiteSpace(songId))
                    .Select(static songId => songId!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var seasonWindows = _persistence.Meta.GetSeasonWindows();
                _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.SongMachine);
                _progress.SetSubOperation("registered_player_band_discovery");
                registeredPlayerBandDiscoveryResult = await RunPhaseAsync(
                    ctx,
                    "RegisteredPlayerBandDiscovery",
                    () => RunWithPostScrapeNetworkTimeoutAsync(
                        "registered-player band discovery",
                        _options.Value.RegisteredPlayerBandDiscoveryTimeout
                            ?? _options.Value.PostScrapeRefreshTimeout,
                        phaseCt => registeredPlayerBandDiscoveryOrchestrator.RunAsync(
                            chartedSongIds,
                            seasonWindows,
                            bandAccessToken,
                            _tokenManager.AccountId!,
                            _pool,
                            phaseCt),
                        ct),
                    RegisteredPlayerBandDiscoveryResult.Empty);
            }
            else
            {
                _log.LogWarning("No access token for registered-player band discovery. Will retry next pass.");
            }
        }

        var registeredBandProcessingResult = RegisteredBandProcessingResult.Empty;
        if (ShouldRunRegisteredBandProcessing(resolvedPhases))
        {
            var registeredBandProcessingOrchestrator = _registeredBandProcessingOrchestrator!;
            var bandAccessToken = await _tokenManager.GetAccessTokenAsync(ct);
            if (bandAccessToken is not null)
            {
                var chartedSongIds = service.Songs
                    .Select(static song => song.track?.su)
                    .Where(static songId => !string.IsNullOrWhiteSpace(songId))
                    .Select(static songId => songId!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var seasonWindows = _persistence.Meta.GetSeasonWindows();
                _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.SongMachine);
                _progress.SetSubOperation("registered_band_targeted_processing");
                registeredBandProcessingResult = await RunPhaseAsync(
                    ctx,
                    "RegisteredBandTargetedProcessing",
                    () => RunWithPostScrapeNetworkTimeoutAsync(
                        "registered-band targeted processing",
                        _options.Value.RegisteredBandTargetedProcessingTimeout
                            ?? _options.Value.PostScrapeRefreshTimeout,
                        phaseCt => registeredBandProcessingOrchestrator.RunAsync(
                            chartedSongIds,
                            seasonWindows,
                            bandAccessToken,
                            _tokenManager.AccountId!,
                            _pool,
                            phaseCt),
                        ct),
                    RegisteredBandProcessingResult.Empty);
            }
            else
            {
                _log.LogWarning("No access token for registered-band targeted processing. Will retry next pass.");
            }
        }

        var runFullBandMaintenance = ShouldRunBandMaintenance(resolvedPhases);
        if (runFullBandMaintenance && ShouldSkipFullBandMaintenanceForIncompleteScrape(ctx, resolvedPhases))
            runFullBandMaintenance = false;

        if (runFullBandMaintenance
            || registeredPlayerBandDiscoveryResult.ImpactedTeamsByBandType.Count > 0
            || registeredBandProcessingResult.ImpactedTeamsByBandType.Count > 0)
        {
            _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.BandScraping);
            _progress.SetSubOperation("maintaining_band_projection");
            var mergedExtractionResult = bandExtractionResult with
            {
                ImpactedTeamsByBandType = MergeImpactedTeams(
                    runFullBandMaintenance ? bandExtractionResult.ImpactedTeamsByBandType : EmptyImpactedTeams,
                    registeredPlayerBandDiscoveryResult.ImpactedTeamsByBandType,
                    registeredBandProcessingResult.ImpactedTeamsByBandType),
                ImpactedCurrentProjectionScopes = MergeCurrentProjectionScopes(
                    runFullBandMaintenance ? bandExtractionResult.ImpactedCurrentProjectionScopes : [],
                    registeredPlayerBandDiscoveryResult.ImpactedCurrentProjectionScopes,
                    registeredBandProcessingResult.ImpactedCurrentProjectionScopes),
            };
            await RunPhaseAsync(ctx, "BandMaintenance", () => RunBandMaintenanceAsync(ctx, mergedExtractionResult, runFullBandMaintenance, ct));
        }

        var skipDerivedSoloPhases = ShouldSkipDerivedSoloPhasesForIncompleteScrape(ctx, resolvedPhases);
        if (!skipDerivedSoloPhases)
        {
            // ── Solo rankings ──
            if (resolvedPhases.HasFlag(ScrapePhase.SoloRankings))
            {
                ctx.RankingsInputCutoffUtc = DateTime.UtcNow;
                ctx.RegisteredIds.UnionWith(_persistence.Meta.GetRegisteredAccountIds());
                foreach (var scope in _persistence.Meta.GetBackfillProjectionScopesCompletedBefore(
                             ctx.RegisteredIds,
                             ctx.RankingsInputCutoffUtc.Value))
                {
                    ctx.AddNotificationProjectionScope(scope);
                }

                ctx.RankingsComputedSuccessfully = await RunPhaseAsync(
                    ctx,
                    "ComputeRankings",
                    () => ComputeRankingsAsync(service, ctx.ScrapeId, ct),
                    defaultValue: false,
                    alwaysPropagateFailure: true);
            }

            if (ShouldPrepareSoloProjectionBeforeDerived(resolvedPhases))
            {
                await RunPhaseAsync(
                    ctx,
                    "PrepareSoloCurrentProjectionForDerived",
                    () => PrepareSoloCurrentProjectionForDerivedAsync(ctx, ct),
                    alwaysPropagateFailure: true);
            }

            // ── Solo rivals ──
            if (resolvedPhases.HasFlag(ScrapePhase.SoloRivals))
            {
                await RunPhaseAsync(ctx, "Rivals", () => ComputeRivalsAsync(ctx, ct));
                await RunLeaderboardRivalsPhaseAsync(ctx, ct);
            }

            // ── Solo player stats ──
            if (resolvedPhases.HasFlag(ScrapePhase.SoloPlayerStats))
            {
                await RunPhaseAsync(ctx, "PlayerStatsTiers", () => ComputePlayerStatsTiersAsync(ctx, ct));
            }

            // ── Solo finalize ──
            if (resolvedPhases.HasFlag(ScrapePhase.SoloFinalize))
            {
                _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.Finalizing);
                if (ctx.ScrapeId > 0)
                {
                    await RunPhaseAsync(ctx, "ActivateShadowSnapshots", () =>
                    {
                        _persistence.FinalizeShadowSnapshots(ctx.ScrapeId, wave: 2, expectedPairs: expectedSnapshotPairs);
                        return Task.CompletedTask;
                    });
                }
            }

        }

        if (_soloCurrentProjectionBuilder is not null
            && (resolvedPhases.HasFlag(ScrapePhase.SoloFinalize)
                || resolvedPhases.HasFlag(ScrapePhase.SoloPrecompute)))
        {
            await RunPhaseAsync(
                ctx,
                "SealSoloCurrentProjectionScopes",
                () => SealSoloCurrentProjectionScopesAsync(ctx, ct),
                alwaysPropagateFailure: true);
        }

    }

    internal static bool ShouldLaunchLegacyBandScrape(ScrapePhase resolvedPhases) =>
        resolvedPhases.HasFlag(ScrapePhase.BandScrapePhase)
        && !resolvedPhases.HasFlag(ScrapePhase.BandScrape);

    /// <summary>
    /// Run publication-critical cleanup after snapshots have been finalized but before
    /// response caches are unfrozen. This keeps persisted precomputed API payloads
    /// aligned with the current projections they are built from.
    /// </summary>
    public async Task RunPublicationCleanupAsync(ScrapePassContext ctx, ScrapePhase resolvedPhases, CancellationToken ct)
    {
        if (ShouldSkipPublicationCleanupForIncompleteScrape(ctx, resolvedPhases))
            return;

        var cleanupItems = 0;
        var refreshSoloCurrentProjection = ShouldRefreshSoloCurrentProjectionDuringCleanup(ctx, resolvedPhases);
        var precomputeApiResponses = ShouldPrecomputeDuringPublicationCleanup(resolvedPhases);

        if (precomputeApiResponses)
            ctx.RegisteredIds.UnionWith(_persistence.Meta.GetRegisteredAccountIds());

        if (refreshSoloCurrentProjection)
            cleanupItems++;
        if (precomputeApiResponses)
            cleanupItems++;

        if (cleanupItems == 0)
            return;

        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.Cleanup);
        _progress.SetSubOperation("publication_cleanup");
        _progress.BeginPhaseProgress(cleanupItems);

        if (refreshSoloCurrentProjection)
        {
            await RunPhaseAsync(
                ctx,
                "Cleanup.SoloCurrentProjection",
                () => RefreshSoloCurrentProjectionForCleanupAsync(ctx, ct),
                alwaysPropagateFailure: true);
            ctx.SoloCurrentProjectionRefreshedForPublication =
                ctx.PostScrapeOutcomes.Outcomes.Any(static outcome =>
                    outcome.Phase == "Cleanup.SoloCurrentProjection" && outcome.Success);
        }

        if (precomputeApiResponses)
        {
            await RunPhaseAsync(
                ctx,
                "Cleanup.PrecomputeAll",
                () => PrecomputeAllForCleanupAsync(
                    ctx,
                    ctx.EpicReportedOver100Pages,
                    ct),
                alwaysPropagateFailure: true);
        }
    }

    /// <summary>
    /// Detect improvement notifications only after the scrape has been published, so
    /// rank notifications never advertise a newer snapshot than public leaderboard pages.
    /// </summary>
    public async Task RunImprovementNotificationsAfterPublicationAsync(
        ScrapePassContext ctx,
        ScrapePhase resolvedPhases,
        CancellationToken ct)
    {
        var skipReason = GetImprovementNotificationSkipReason(
            ctx,
            resolvedPhases,
            requireRecoveryService: true);
        if (skipReason is not null)
        {
            RecordSkippedPhase(ctx, "ImprovementNotifications", skipReason);
            return;
        }

        await RunPhaseAsync(
            ctx,
            "ImprovementNotifications",
            async () =>
            {
                await _improvementNotificationRecovery!.RunPublishedScrapeAsync(
                    expectedPublishedScrapeId: ctx.ScrapeId,
                    execute: true,
                    baselineOnly: false,
                    refreshSoloProjection: false,
                    projectionScopes: null,
                    force: false,
                    source: "post-scrape",
                    ct);
                await _notifications.NotifyNotificationFeedChangedAsync();
            });
    }

    public bool ShouldQueueImprovementNotifications(
        ScrapePassContext ctx,
        ScrapePhase resolvedPhases) =>
        GetImprovementNotificationSkipReason(
            ctx,
            resolvedPhases,
            requireRecoveryService: false) is null;

    public Task EnsureBandContextReadyBeforeScrapeAsync(CancellationToken ct) =>
        _bandExtractor.EnsureBandContextReadyAsync(ct);

    public async Task<IReadOnlyCollection<SoloCurrentProjectionScopeKey>>
        PrepareImprovementNotificationProjectionScopesAsync(
            ScrapePassContext ctx,
            ScrapePhase resolvedPhases,
            CancellationToken ct)
    {
        if (!ShouldQueueImprovementNotifications(ctx, resolvedPhases))
            return [];

        return await BuildSoloProjectionScopesForNotificationsAsync(
            ctx,
            new SongProcessingMachine.MachineResult(),
            _improvementNotificationOptions.Value,
            ct);
    }

    public async Task RecoverPendingImprovementNotificationsOnStartupAsync(CancellationToken ct)
    {
        var options = _improvementNotificationOptions.Value;
        if (_improvementNotifications is null)
            return;

        var status = _improvementNotifications.GetPublicationStatus();
        if (!status.PublishedScrapeId.HasValue)
            return;

        var publishedScrapeId = status.PublishedScrapeId.Value;
        var markerMatchesPublished = status.MarkerScrapeId == publishedScrapeId;
        var scraperOptions = _options.Value;
        var explicitResumeContext =
            scraperOptions.RunOnce
            && scraperOptions.ResumeScrapeId > 0
            && scraperOptions.ResumeScrapeId != publishedScrapeId
            && scraperOptions.ResolvedPhases ==
                ScrapePhaseResolver.SoloLeaderboardsGroup;
        if (markerMatchesPublished
            && status.MarkerStatus == "completed"
            && status.IsCompleteForPublishedScrape(
                options.IncludePlayers,
                options.IncludeBands,
                options.IncludeSongEvents,
                options.IncludeRankings)
            && (!status.PublicReadsFrozen
                || explicitResumeContext))
        {
            return;
        }
        if (status.PublicReadsFrozen)
        {
            throw new InvalidOperationException(
                $"Improvement notification recovery for published scrape {publishedScrapeId} is blocked while public reads are frozen.");
        }
        if (status.MarkerScrapeId is null
            && (status.MarkerStatus is null or "disabled"))
        {
            return;
        }
        if (markerMatchesPublished && status.MarkerStatus == "disabled")
            return;
        if (status.MarkerScrapeId.HasValue && !markerMatchesPublished)
        {
            throw new InvalidOperationException(
                $"Improvement notification marker {status.MarkerScrapeId} does not match published scrape {publishedScrapeId}.");
        }
        if (status.IsCompleteForPublishedScrape(
                options.IncludePlayers,
                options.IncludeBands,
                options.IncludeSongEvents,
                options.IncludeRankings))
        {
            if (!markerMatchesPublished || status.MarkerStatus != "completed")
            {
                using var recoveryLock =
                    _improvementNotifications.AcquireRecoveryLock(publishedScrapeId);
                status = _improvementNotifications.GetPublicationStatus();
                markerMatchesPublished = status.MarkerScrapeId == publishedScrapeId;
                if (status.PublicReadsFrozen)
                {
                    throw new InvalidOperationException(
                        $"Improvement notification completion repair for published scrape {publishedScrapeId} " +
                        "is blocked while public reads are frozen.");
                }
                if (!markerMatchesPublished)
                {
                    throw new InvalidOperationException(
                        $"Improvement notification marker {status.MarkerScrapeId?.ToString() ?? "null"} " +
                        $"does not match published scrape {publishedScrapeId}.");
                }
                if (!status.IsCompleteForPublishedScrape(
                        options.IncludePlayers,
                        options.IncludeBands,
                        options.IncludeSongEvents,
                        options.IncludeRankings))
                {
                    throw new InvalidOperationException(
                        $"Improvement notification completion state changed while repairing published scrape {publishedScrapeId}.");
                }
                if (status.MarkerStatus != "completed")
                    _improvementNotifications.MarkPublicationCompleted(publishedScrapeId);
            }
            return;
        }

        if (_improvementNotificationRecovery is null)
        {
            throw new InvalidOperationException(
                "Improvement notification recovery is required before the next scrape, but the recovery service is unavailable.");
        }

        _log.LogWarning(
            "Published scrape {ScrapeId} has incomplete improvement notification detection; running startup recovery before the next scrape.",
            publishedScrapeId);

        if (_persistence.UseSnapshotOverlayWorkerReaders)
        {
            var builder = _soloCurrentProjectionBuilder
                ?? throw new InvalidOperationException(
                    "Snapshot/overlay notification recovery requires a configured solo projection builder.");
            await builder.EnsureSchemaAsync(ct);
            await builder.PruneOrphanedScopesAsync(
                new SoloCurrentProjectionRebuildOptions
                {
                    CommandTimeoutSeconds = Math.Max(
                        0,
                        _options.Value.SoloProjectionCleanupCommandTimeoutSeconds),
                    MaxDegreeOfParallelism = Math.Max(
                        1,
                        _options.Value.SoloProjectionCleanupMaxDegreeOfParallelism),
                },
                ct);
            var staleScopes = await builder.LoadStaleScopesAsync(ct);
            if (staleScopes.Count > 0)
            {
                var refresh = await builder.RefreshScopesAsync(
                    staleScopes,
                    new SoloCurrentProjectionRebuildOptions
                    {
                        CommandTimeoutSeconds = Math.Max(
                            0,
                            _options.Value.SoloProjectionCleanupCommandTimeoutSeconds),
                        MaxDegreeOfParallelism = Math.Max(
                            1,
                            _options.Value.SoloProjectionCleanupMaxDegreeOfParallelism),
                    },
                    ct);
                if (refresh.FailedScopeCount > 0)
                {
                    throw new InvalidOperationException(
                        $"Snapshot/overlay startup recovery failed to refresh {refresh.FailedScopeCount} solo projection scope(s).");
                }
            }
        }

        await _improvementNotificationRecovery.RunPublishedScrapeAsync(
            expectedPublishedScrapeId: publishedScrapeId,
            execute: true,
            baselineOnly: false,
            refreshSoloProjection: false,
            projectionScopes: null,
            force: false,
            source: "startup-recovery",
            ct);
        await _notifications.NotifyNotificationFeedChangedAsync();
    }

    /// <summary>
    /// Run best-effort database cleanup after derived state has been published.
    /// This phase must not include scrape writer resource cleanup; spool disposal
    /// stays with the writer lifecycle so disk is released as soon as possible.
    /// </summary>
    public async Task RunCleanupAsync(ScrapePassContext ctx, ScrapePhase resolvedPhases, CancellationToken ct)
    {
        if (ShouldSkipBestEffortCleanupForIncompleteScrape(ctx, resolvedPhases))
            return;

        var cleanupItems = 0;
        var cleanupSoloExcessEntries = resolvedPhases.HasFlag(ScrapePhase.SoloEnrichment);
        var cleanupRankHistoryRetention = resolvedPhases.HasFlag(ScrapePhase.SoloRankings);
        var cleanupBandRankHistoryRetention = resolvedPhases.HasFlag(ScrapePhase.SoloRankings);
        var cleanupServiceLevelRetention =
            resolvedPhases.HasFlag(ScrapePhase.SoloFinalize);

        if (cleanupSoloExcessEntries)
            cleanupItems++;
        if (cleanupRankHistoryRetention)
            cleanupItems += GlobalLeaderboardScraper.AllInstruments.Count + 1;
        if (cleanupBandRankHistoryRetention)
            cleanupItems += BandInstrumentMapping.AllBandTypes.Count;
        if (cleanupServiceLevelRetention)
            cleanupItems++;

        if (cleanupItems == 0)
            return;

        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.Cleanup);
        _progress.SetSubOperation("database_cleanup");
        _progress.BeginPhaseProgress(cleanupItems);

        if (cleanupSoloExcessEntries)
        {
            await RunPhaseAsync(ctx, "Cleanup.SoloExcessEntries", () => Task.Run(() =>
            {
                try
                {
                    _progress.SetSubOperation("cleanup_solo_excess_entries");
                    PruneExcessEntries(ctx);
                }
                finally
                {
                    _progress.ReportPhaseItemComplete();
                }
            }, ct));
        }

        if (cleanupRankHistoryRetention)
        {
            var skipReason = await GetMaintenanceCleanupSkipReasonAsync(
                "rank history retention",
                ct);
            if (skipReason is not null)
            {
                ReportSkippedCleanupItems(GlobalLeaderboardScraper.AllInstruments.Count + 1);
                RecordSkippedPhase(ctx, "Cleanup.RankHistoryRetention", skipReason);
            }
            else
                await RunPhaseAsync(ctx, "Cleanup.RankHistoryRetention", () => CleanupRankHistoryRetentionAsync(ct));
        }

        if (cleanupBandRankHistoryRetention)
        {
            var skipReason = await GetMaintenanceCleanupSkipReasonAsync(
                "band rank history retention",
                ct);
            if (skipReason is not null)
            {
                ReportSkippedCleanupItems(BandInstrumentMapping.AllBandTypes.Count);
                RecordSkippedPhase(ctx, "Cleanup.BandRankHistoryRetention", skipReason);
            }
            else
                await RunPhaseAsync(ctx, "Cleanup.BandRankHistoryRetention", () => CleanupBandRankHistoryRetentionAsync(ct));
        }

        if (cleanupServiceLevelRetention)
        {
            if (_retentionMaintenanceService is null)
            {
                ReportSkippedCleanupItems(1);
                RecordSkippedPhase(
                    ctx,
                    "Cleanup.ServiceLevelRetention",
                    "retention maintenance service is unavailable");
            }
            else if (!_databaseMaintenanceOptions.Value.ServiceLevelRetentionMaintenanceEnabled)
            {
                ReportSkippedCleanupItems(1);
                RecordSkippedPhase(
                    ctx,
                    "Cleanup.ServiceLevelRetention",
                    "service-level retention maintenance is disabled");
            }
            else
            {
                await RunPhaseAsync<DatabaseRetentionMaintenanceResult>(
                    ctx,
                    "Cleanup.ServiceLevelRetention",
                    () => RunServiceLevelRetentionMaintenanceAsync(ct),
                    completionSelector: static result =>
                        result.Skipped
                            ? new PhaseCompletion("skipped", result.Reason)
                            : PhaseCompletion.Completed);
            }
        }
    }

    private async Task<DatabaseRetentionMaintenanceResult> RunServiceLevelRetentionMaintenanceAsync(
        CancellationToken ct)
    {
        _progress.SetSubOperation("cleanup_service_level_retention");
        try
        {
            var result = await _retentionMaintenanceService!.RunAsync(ct);
            if (result.Skipped)
            {
                _log.LogInformation("Service-level retention maintenance skipped: {Reason}.", result.Reason);
                return result;
            }

            _log.LogInformation(
                "Service-level retention maintenance completed: {Reason}. Snapshot candidates={SnapshotCandidates:N0}, rewrites={RewriteCount:N0}, metadata rows deleted={MetadataDeleted:N0}.",
                result.Reason,
                result.SnapshotRetention.CandidateCount,
                result.SnapshotRetention.RewriteResults.Count,
                result.MetadataCleanup.TotalDeletedRows);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Service-level retention maintenance failed. Continuing without blocking fresh data publication.");
            throw;
        }
        finally
        {
            _progress.ReportPhaseItemComplete();
        }
    }

    private async Task<string?> GetMaintenanceCleanupSkipReasonAsync(
        string cleanupName,
        CancellationToken ct)
    {
        var options = _databaseMaintenanceOptions.Value;
        if (!options.SkipCleanupWhenPressureDetected || _databasePressureMonitor is null)
            return null;

        var snapshot = await _databasePressureMonitor.GetPressureSnapshotAsync(options, ct);
        if (!snapshot.IsUnderPressure)
            return null;

        var reason =
            $"database pressure is already high: {string.Join("; ", snapshot.Reasons)}";
        _log.LogWarning(
            "Skipping {CleanupName} cleanup because database pressure is already high: {Reasons}.",
            cleanupName,
            string.Join("; ", snapshot.Reasons));
        return reason;
    }

    private void ReportSkippedCleanupItems(int itemCount)
    {
        for (var i = 0; i < itemCount; i++)
            _progress.ReportPhaseItemComplete();
    }

    private static int PositiveOrDefault(int value, int fallback) => value > 0 ? value : fallback;

    private bool ShouldRefreshSoloCurrentProjectionDuringCleanup(ScrapePassContext ctx, ScrapePhase resolvedPhases)
    {
        if (_soloCurrentProjectionBuilder is null)
        {
            return false;
        }

        if (ctx.NotificationProjectionScopes.Count > 0)
            return true;

        if (!_options.Value.RefreshSoloProjectionDuringCleanup ||
            !resolvedPhases.HasFlag(ScrapePhase.SoloFinalize))
        {
            return false;
        }

        var minimumCoverage = _improvementNotificationOptions.Value.MinimumSoloLeaderboardCoverageRatio;
        if (HasSufficientSoloScrapeCoverage(ctx, resolvedPhases, minimumCoverage, out var actualSoloLeaderboards, out var expectedSoloLeaderboards, out var coverage))
            return true;

        _log.LogWarning(
            "Cleanup solo current projection refresh will run despite low solo scrape coverage because projection freshness is publication-critical: {Actual:N0}/{Expected:N0} leaderboards with data ({Coverage:P1}) below required {Required:P1}.",
            actualSoloLeaderboards,
            expectedSoloLeaderboards,
            coverage,
            minimumCoverage);

        return true;
    }

    private static bool ShouldPrecomputeDuringPublicationCleanup(ScrapePhase resolvedPhases) =>
        resolvedPhases.HasFlag(ScrapePhase.SoloPrecompute);

    private async Task PrecomputeAllForCleanupAsync(
        ScrapePassContext ctx,
        bool showLeaderboardEntryTotals,
        CancellationToken ct)
    {
        _progress.SetSubOperation("cleanup_api_precompute");
        try
        {
            await _precomputer.PrecomputeAllAsync(
                showLeaderboardEntryTotals,
                ct,
                publishImmediately: false,
                publicationCatalogSongs:
                    ctx.PublicationCatalogSongs);
        }
        finally
        {
            _progress.ReportPhaseItemComplete();
        }
    }

    private async Task SealSoloCurrentProjectionScopesAsync(
        ScrapePassContext ctx,
        CancellationToken ct)
    {
        var builder = _soloCurrentProjectionBuilder
            ?? throw new InvalidOperationException(
                "Solo current projection scope sealing requires a configured projection builder.");

        await builder.EnsureSchemaAsync(ct);
        foreach (var scope in await builder.LoadStaleScopesAsync(ct))
            ctx.AddNotificationProjectionScope(scope);

        ctx.SoloCurrentProjectionScopesSealedForPublication = true;
        _log.LogInformation(
            "Sealed {ScopeCount:N0} solo current projection scope(s) for publication before deferred registration writes.",
            ctx.NotificationProjectionScopes.Count);
    }

    private async Task RefreshSoloCurrentProjectionForCleanupAsync(
        ScrapePassContext ctx,
        CancellationToken ct)
    {
        _progress.SetSubOperation("cleanup_solo_current_projection");
        try
        {
            var builder = _soloCurrentProjectionBuilder;
            if (builder is null)
                return;

            await builder.EnsureSchemaAsync(ct);
            var staleScopes = ctx.SoloCurrentProjectionScopesSealedForPublication
                ? []
                : await builder.LoadStaleScopesAsync(ct);
            var scopes = staleScopes
                .Concat(ctx.NotificationProjectionScopes)
                .Distinct()
                .Where(scope => !ctx.RefreshedProjectionScopes.Contains(scope))
                .OrderBy(static scope => scope.Instrument, StringComparer.Ordinal)
                .ThenBy(static scope => scope.SongId, StringComparer.Ordinal)
                .ToArray();

            var options = _options.Value;
            var refreshOptions = new SoloCurrentProjectionRebuildOptions
            {
                CommandTimeoutSeconds = Math.Max(0, options.SoloProjectionCleanupCommandTimeoutSeconds),
                MaxDegreeOfParallelism = Math.Max(1, options.SoloProjectionCleanupMaxDegreeOfParallelism),
            };
            var orphanedRows = await builder.PruneOrphanedScopesAsync(refreshOptions, ct);
            if (orphanedRows > 0)
            {
                _log.LogInformation(
                    "Cleanup removed {OrphanedRows:N0} solo projection row(s) with no snapshot/overlay source.",
                    orphanedRows);
            }

            if (scopes.Length == 0)
            {
                _log.LogInformation("Cleanup solo current projection refresh skipped; no stale scopes found.");
                return;
            }

            _log.LogInformation(
                "Cleanup refreshing {ScopeCount:N0} stale or deferred-write solo current projection scope(s) with maxDegree={MaxDegree}.",
                scopes.Length,
                refreshOptions.MaxDegreeOfParallelism);

            var result = await builder.RefreshScopesAsync(scopes, refreshOptions, ct);
            if (result.FailedScopeCount > 0)
            {
                _log.LogError(
                    "Cleanup solo current projection refresh completed with failures: {Succeeded:N0}/{ScopeCount:N0} scope(s), {Failed:N0} failed, rows {Deleted:N0}->{Inserted:N0}, elapsed {ElapsedMs:N0}ms.",
                    result.SucceededScopeCount,
                    result.ScopeCount,
                    result.FailedScopeCount,
                    result.DeletedRows,
                    result.InsertedRows,
                    result.TotalElapsedMs);

                throw new InvalidOperationException($"Cleanup solo current projection refresh failed for {result.FailedScopeCount} scope(s).");
            }
            else
            {
                _log.LogInformation(
                    "Cleanup solo current projection refresh complete: {Succeeded:N0}/{ScopeCount:N0} scope(s), rows {Deleted:N0}->{Inserted:N0}, elapsed {ElapsedMs:N0}ms.",
                    result.SucceededScopeCount,
                    result.ScopeCount,
                    result.DeletedRows,
                    result.InsertedRows,
                    result.TotalElapsedMs);
            }

            var remainingStaleScopes = await builder.LoadStaleScopesAsync(ct);
            if (remainingStaleScopes.Count > 0)
                throw new InvalidOperationException($"Cleanup solo current projection refresh left {remainingStaleScopes.Count} stale scope(s).");
        }
        finally
        {
            _progress.ReportPhaseItemComplete();
        }
    }

    private bool ShouldPrepareSoloProjectionBeforeDerived(ScrapePhase resolvedPhases) =>
        _persistence.UseSnapshotOverlayWorkerReaders
        && _soloCurrentProjectionBuilder is not null
        && (
            resolvedPhases.HasFlag(ScrapePhase.SoloRivals)
            || resolvedPhases.HasFlag(ScrapePhase.SoloPlayerStats)
        );

    private async Task PrepareSoloCurrentProjectionForDerivedAsync(
        ScrapePassContext ctx,
        CancellationToken ct)
    {
        var builder = _soloCurrentProjectionBuilder
            ?? throw new InvalidOperationException(
                "Snapshot/overlay derived readers require a configured solo projection builder.");
        var options = _options.Value;
        var rebuildOptions = new SoloCurrentProjectionRebuildOptions
        {
            CommandTimeoutSeconds = Math.Max(
                0,
                options.SoloProjectionCleanupCommandTimeoutSeconds),
            MaxDegreeOfParallelism = Math.Max(
                1,
                options.SoloProjectionCleanupMaxDegreeOfParallelism),
        };

        await builder.EnsureSchemaAsync(ct);
        await builder.PruneOrphanedScopesAsync(rebuildOptions, ct);
        var scopes = (await builder.LoadStaleScopesAsync(ct))
            .Concat(ctx.NotificationProjectionScopes)
            .Distinct()
            .OrderBy(static scope => scope.Instrument, StringComparer.Ordinal)
            .ThenBy(static scope => scope.SongId, StringComparer.Ordinal)
            .ToArray();

        if (scopes.Length > 0)
        {
            _log.LogInformation(
                "Preparing {ScopeCount:N0} solo projection scope(s) before rivals/player stats.",
                scopes.Length);
            var result = await builder.RefreshScopesAsync(
                scopes,
                rebuildOptions,
                ct);
            if (result.FailedScopeCount > 0)
            {
                throw new InvalidOperationException(
                    $"Early solo projection refresh failed for {result.FailedScopeCount} scope(s).");
            }
            foreach (var scope in scopes)
                ctx.RefreshedProjectionScopes.Add(scope);
        }

        var remainingStaleScopes = await builder.LoadStaleScopesAsync(ct);
        if (remainingStaleScopes.Count > 0
            || await builder.HasOrphanedProjectionScopesAsync(ct))
        {
            throw new InvalidOperationException(
                $"Early solo projection validation is incomplete: stale={remainingStaleScopes.Count}.");
        }
        ctx.SoloCurrentProjectionRefreshedForPublication = true;
        _persistence.SetValidatedCurrentProjectionForWorkerReaders(true);
        _log.LogInformation(
            "Validated solo current projection for snapshot/overlay derived readers ({ScopeCount:N0} refreshed scope(s)).",
            scopes.Length);
    }

    private async Task CleanupRankHistoryRetentionAsync(CancellationToken ct)
    {
        var failures = new List<Exception>();
        var maintenanceOptions = _databaseMaintenanceOptions.Value;
        var batchSize = PositiveOrDefault(
            maintenanceOptions.RankHistoryCleanupBatchSize,
            DatabaseMaintenanceOptions.DefaultCleanupBatchSize);
        var maxBatches = PositiveOrDefault(
            maintenanceOptions.RankHistoryCleanupMaxBatches,
            DatabaseMaintenanceOptions.DefaultCleanupMaxBatches);
        var commandTimeoutSeconds = Math.Max(0, maintenanceOptions.CleanupCommandTimeoutSeconds);

        foreach (var instrument in GlobalLeaderboardScraper.AllInstruments)
        {
            ct.ThrowIfCancellationRequested();
            _progress.SetSubOperation($"cleanup_rank_history_{instrument}");
            try
            {
                var db = _persistence.GetOrCreateInstrumentDb(instrument);
                var deleted = await Task.Run(() => db.CleanupRankHistoryRetention(
                    batchSize: batchSize,
                    maxBatches: maxBatches), ct);
                if (deleted > 0)
                {
                    _log.LogInformation(
                        "Rank history retention cleanup for {Instrument} deleted {Deleted:N0} row(s).",
                        instrument,
                        deleted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex,
                    "Rank history retention cleanup failed for {Instrument}. Continuing without blocking fresh data publication.",
                    instrument);
                failures.Add(ex);
            }
            finally
            {
                _progress.ReportPhaseItemComplete();
            }
        }

        ct.ThrowIfCancellationRequested();
        _progress.SetSubOperation("cleanup_composite_rank_history");
        try
        {
            var deleted = await Task.Run(() => _persistence.Meta.CleanupCompositeRankHistoryRetention(
                batchSize: batchSize,
                maxBatches: maxBatches,
                commandTimeoutSeconds: commandTimeoutSeconds,
                ct: ct), ct);
            if (deleted > 0)
            {
                _log.LogInformation(
                    "Composite rank history retention cleanup deleted {Deleted:N0} row(s).",
                    deleted);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "Composite rank history retention cleanup failed. Continuing without blocking fresh data publication.");
            failures.Add(ex);
        }
        finally
        {
            _progress.ReportPhaseItemComplete();
        }

        if (failures.Count > 0)
            throw new AggregateException("One or more rank history retention cleanup operations failed.", failures);
    }

    private async Task CleanupBandRankHistoryRetentionAsync(CancellationToken ct)
    {
        var failures = new List<Exception>();
        var options = _bandRankHistoryOptions.Value;
        var maintenanceOptions = _databaseMaintenanceOptions.Value;
        var batchSize = PositiveOrDefault(
            maintenanceOptions.BandRankHistoryCleanupBatchSize,
            DatabaseMaintenanceOptions.DefaultCleanupBatchSize);
        var maxBatches = PositiveOrDefault(
            maintenanceOptions.BandRankHistoryCleanupMaxBatches,
            DatabaseMaintenanceOptions.DefaultCleanupMaxBatches);
        var commandTimeoutSeconds = options.CommandTimeoutSeconds > 0
            ? options.CommandTimeoutSeconds
            : Math.Max(0, maintenanceOptions.CleanupCommandTimeoutSeconds);
        foreach (var bandType in BandInstrumentMapping.AllBandTypes)
        {
            ct.ThrowIfCancellationRequested();
            _progress.SetSubOperation($"cleanup_band_rank_history_{bandType}");
            try
            {
                var deleted = await Task.Run(() => _persistence.Meta.CleanupBandRankHistoryRetention(
                    bandType,
                    options.RetentionDays,
                    commandTimeoutSeconds,
                    ct,
                    batchSize,
                    maxBatches), ct);
                if (deleted > 0)
                {
                    _log.LogInformation(
                        "Band rank history retention cleanup for {BandType} deleted {Deleted:N0} row(s).",
                        bandType,
                        deleted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex,
                    "Band rank history retention cleanup failed for {BandType}. Continuing without blocking fresh data publication.",
                    bandType);
                failures.Add(ex);
            }
            finally
            {
                _progress.ReportPhaseItemComplete();
            }
        }

        if (failures.Count > 0)
            throw new AggregateException("One or more band rank history retention cleanup operations failed.", failures);
    }

    private static bool ShouldRunBandMaintenance(ScrapePhase resolvedPhases) =>
        resolvedPhases.HasFlag(ScrapePhase.BandScrape) ||
        resolvedPhases.HasFlag(ScrapePhase.BandScrapePhase) ||
        resolvedPhases.HasFlag(ScrapePhase.BandExtraction) ||
        resolvedPhases.HasFlag(ScrapePhase.SoloEnrichment);

    private static bool HasLeaderboardScrapePhase(ScrapePhase resolvedPhases) =>
        resolvedPhases.HasFlag(ScrapePhase.SoloScrape) ||
        resolvedPhases.HasFlag(ScrapePhase.BandScrape) ||
        resolvedPhases.HasFlag(ScrapePhase.BandScrapePhase);

    private bool ShouldSkipIncompleteScrapeWork(
        ScrapePassContext ctx,
        ScrapePhase resolvedPhases,
        bool workRequested,
        string workDescription)
    {
        if (!workRequested || ctx.LeaderboardScrapeCompleted || !HasLeaderboardScrapePhase(resolvedPhases))
            return false;

        _log.LogWarning(
            "Skipping {WorkDescription} because the leaderboard scrape did not complete. Work will retry after a successful scrape.",
            workDescription);
        return true;
    }

    private bool ShouldSkipDerivedSoloPhasesForIncompleteScrape(ScrapePassContext ctx, ScrapePhase resolvedPhases) =>
        ShouldSkipIncompleteScrapeWork(
            ctx,
            resolvedPhases,
            resolvedPhases.HasFlag(ScrapePhase.SoloRankings) ||
            resolvedPhases.HasFlag(ScrapePhase.SoloRivals) ||
            resolvedPhases.HasFlag(ScrapePhase.SoloPlayerStats) ||
            resolvedPhases.HasFlag(ScrapePhase.SoloFinalize),
            "derived ranking/finalization phases");

    private bool ShouldSkipPublicationCleanupForIncompleteScrape(ScrapePassContext ctx, ScrapePhase resolvedPhases) =>
        ShouldSkipIncompleteScrapeWork(
            ctx,
            resolvedPhases,
            resolvedPhases.HasFlag(ScrapePhase.SoloFinalize) ||
            resolvedPhases.HasFlag(ScrapePhase.SoloPrecompute),
            "publication cleanup");

    private bool ShouldSkipBestEffortCleanupForIncompleteScrape(ScrapePassContext ctx, ScrapePhase resolvedPhases) =>
        ShouldSkipIncompleteScrapeWork(
            ctx,
            resolvedPhases,
            resolvedPhases.HasFlag(ScrapePhase.SoloEnrichment) ||
            resolvedPhases.HasFlag(ScrapePhase.SoloRankings) ||
            resolvedPhases.HasFlag(ScrapePhase.SoloFinalize),
            "post-scrape cleanup");

    private bool ShouldSkipFullBandMaintenanceForIncompleteScrape(ScrapePassContext ctx, ScrapePhase resolvedPhases)
    {
        if (ctx.LeaderboardScrapeCompleted)
            return false;

        if (!HasLeaderboardScrapePhase(resolvedPhases))
            return false;

        _log.LogWarning(
            "Skipping full band maintenance because the leaderboard scrape did not complete. " +
            "Targeted registered-band impacts may still be refreshed; full pruning/projection maintenance will retry after a successful scrape.");
        return true;
    }

    private bool ShouldRunRegisteredBandProcessing(ScrapePhase resolvedPhases) =>
        _registeredBandProcessingOrchestrator is not null &&
        _options.Value.EnableRegisteredBandTargetedProcessing &&
        (resolvedPhases.HasFlag(ScrapePhase.BandScrape) ||
         resolvedPhases.HasFlag(ScrapePhase.BandScrapePhase) ||
         resolvedPhases.HasFlag(ScrapePhase.BandExtraction) ||
         resolvedPhases.HasFlag(ScrapePhase.SoloRefreshUsers));

    private bool ShouldRunRegisteredPlayerBandDiscovery(ScrapePhase resolvedPhases) =>
        _registeredPlayerBandDiscoveryOrchestrator is not null &&
        _options.Value.EnableRegisteredPlayerBandDiscovery &&
        (resolvedPhases.HasFlag(ScrapePhase.BandScrape) ||
         resolvedPhases.HasFlag(ScrapePhase.BandScrapePhase) ||
         resolvedPhases.HasFlag(ScrapePhase.BandExtraction) ||
         resolvedPhases.HasFlag(ScrapePhase.SoloRefreshUsers));

    private string? GetImprovementNotificationSkipReason(
        ScrapePassContext ctx,
        ScrapePhase resolvedPhases,
        bool requireRecoveryService)
    {
        if (!resolvedPhases.HasFlag(ScrapePhase.SoloRankings))
            return "solo rankings were not selected";
        if (!ctx.RankingsComputedSuccessfully)
            return "rankings did not complete successfully";

        var options = _improvementNotificationOptions.Value;
        if (_improvementNotifications is null)
            return "notification persistence is unavailable";
        if (!options.Enabled)
            return "improvement notifications are disabled";

        var minimumCoverage = options.MinimumSoloLeaderboardCoverageRatio;
        if (!HasSufficientSoloScrapeCoverage(
                ctx,
                resolvedPhases,
                minimumCoverage,
                out var actualSoloLeaderboards,
                out var expectedSoloLeaderboards,
                out var coverage))
        {
            return
                $"solo scrape coverage was below threshold: " +
                $"{actualSoloLeaderboards:N0}/{expectedSoloLeaderboards:N0} " +
                $"leaderboards with data ({coverage:P1}) below required " +
                $"{minimumCoverage:P1}";
        }

        if (requireRecoveryService && _improvementNotificationRecovery is null)
            return "notification recovery service is unavailable";

        return null;
    }

    private static bool HasSufficientSoloScrapeCoverage(
        ScrapePassContext ctx,
        ScrapePhase resolvedPhases,
        double minimumCoverage,
        out int actualSoloLeaderboards,
        out int expectedSoloLeaderboards,
        out double coverage)
    {
        actualSoloLeaderboards = 0;
        expectedSoloLeaderboards = 0;
        coverage = 1d;

        if (!resolvedPhases.HasFlag(ScrapePhase.SoloScrape))
            return true;

        if (minimumCoverage <= 0)
            return true;

        expectedSoloLeaderboards = BuildExpectedSnapshotPairs(ctx).Count;
        if (expectedSoloLeaderboards == 0)
            return true;

        actualSoloLeaderboards = ctx.Aggregates.SoloLeaderboardsWithData;
        coverage = actualSoloLeaderboards / (double)expectedSoloLeaderboards;
        return coverage >= minimumCoverage;
    }

    private async Task RunBandMaintenanceAsync(
        ScrapePassContext ctx,
        BandExtractionResult extractionResult,
        bool runFullMaintenance,
        CancellationToken ct)
    {
        var pruneResult = await RunTimedBandMaintenanceSubphaseAsync(
            ctx,
            BandMaintenancePruneSubphase,
            () => Task.FromResult(
                runFullMaintenance
                    ? PruneBandEntries(ctx)
                    : BandPruneResult.Empty),
            GetBandPruneTimingMetrics);
        var impactedTeams = MergeImpactedTeams(
            extractionResult.ImpactedTeamsByBandType,
            pruneResult.AffectedTeamsByBandType);
        var impactedCurrentProjectionScopes = MergeCurrentProjectionScopes(
            extractionResult.ImpactedCurrentProjectionScopes,
            pruneResult.AffectedCurrentProjectionScopes);

        var searchRefreshResult = await RunTimedBandMaintenanceSubphaseAsync(
            ctx,
            BandMaintenanceSearchProjectionSubphase,
            async () =>
            {
                if (_bandSearchProjectionBuilder is null)
                {
                    return new BandSearchProjectionIncrementalResult(
                        false, 0, 0, 0, 0, 0, 0, 0, 0);
                }

                return await _bandSearchProjectionBuilder.RefreshIncrementalAsync(
                    impactedTeams,
                    ct);
            },
            GetBandSearchProjectionTimingMetrics);

        if (_bandSearchProjectionBuilder is not null)
        {
            if (!searchRefreshResult.ProjectionAvailable)
            {
                _log.LogDebug("Band search projection refresh skipped because no published projection state exists.");
            }
            else
            {
                _log.LogInformation(
                    "Band search projection maintenance complete: {ImpactedTeams:N0} impacted team(s), " +
                    "teams {DeletedTeams:N0}->{InsertedTeams:N0}, members {DeletedMembers:N0}->{InsertedMembers:N0}.",
                    searchRefreshResult.ImpactedTeams,
                    searchRefreshResult.DeletedTeamRows,
                    searchRefreshResult.InsertedTeamRows,
                    searchRefreshResult.DeletedMemberRows,
                    searchRefreshResult.InsertedMemberRows);
            }
        }

        await RunTimedBandMaintenanceSubphaseAsync(
            ctx,
            BandMaintenanceCurrentProjectionSubphase,
            () => _bandCurrentProjectionBuilder is null
                ? Task.FromResult(new BandMaintenanceTimingMetrics(
                    RowsRead: impactedCurrentProjectionScopes.Count,
                    RowsWritten: 0,
                    RowsDeleted: 0,
                    ScopeCount: 0))
                : RefreshBandCurrentProjectionScopesAsync(
                    impactedCurrentProjectionScopes,
                    ct),
            static metrics => metrics);
    }

    internal Task RunBandMaintenanceForTestAsync(
        ScrapePassContext ctx,
        BandExtractionResult extractionResult,
        bool runFullMaintenance,
        CancellationToken ct) =>
        RunBandMaintenanceAsync(
            ctx,
            extractionResult,
            runFullMaintenance,
            ct);

    internal async Task<T> RunTimedBandMaintenanceSubphaseAsync<T>(
        ScrapePassContext ctx,
        string subphase,
        Func<Task<T>> operation,
        Func<T, BandMaintenanceTimingMetrics> getMetrics)
    {
        var startedAt = DateTime.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await operation();
            stopwatch.Stop();
            RecordBandMaintenanceSubphaseTiming(
                ctx,
                subphase,
                startedAt,
                stopwatch.Elapsed,
                true,
                getMetrics(result),
                null);
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            RecordBandMaintenanceSubphaseTiming(
                ctx,
                subphase,
                startedAt,
                stopwatch.Elapsed,
                false,
                BandMaintenanceTimingMetrics.Unknown,
                ex.Message);
            throw;
        }
    }

    internal static BandMaintenanceTimingMetrics GetBandPruneTimingMetrics(
        BandPruneResult result) =>
        new(
            RowsDeleted:
                (long)result.DeletedEntries
                + result.DeletedMemberStats
                + result.DeletedMemberLookups,
            ScopeCount: result.AffectedCurrentProjectionScopes.Count);

    internal static BandMaintenanceTimingMetrics GetBandSearchProjectionTimingMetrics(
        BandSearchProjectionIncrementalResult result) =>
        new(
            RowsWritten: result.InsertedTeamRows + result.InsertedMemberRows,
            RowsDeleted: result.DeletedTeamRows + result.DeletedMemberRows,
            ScopeCount: result.ImpactedTeams);

    internal static BandMaintenanceTimingMetrics GetBandCurrentProjectionTimingMetrics(
        BandCurrentProjectionIncrementalRefreshResult result,
        int consideredScopeCount) =>
        new(
            RowsRead: consideredScopeCount,
            RowsWritten: result.InsertedRows,
            RowsDeleted: result.DeletedRows,
            ScopeCount: result.ScopeCount);

    private void RecordBandMaintenanceSubphaseTiming(
        ScrapePassContext ctx,
        string subphase,
        DateTime startedAt,
        TimeSpan duration,
        bool success,
        BandMaintenanceTimingMetrics metrics,
        string? errorMessage)
    {
        if (ctx.ScrapeId <= 0)
            return;

        try
        {
            _persistence.Meta.RecordScrapePhaseTiming(
                new ScrapePhaseTimingRecord(
                    ctx.ScrapeId,
                    BandMaintenanceTimingPhase,
                    subphase,
                    null,
                    startedAt,
                    startedAt + duration,
                    (long)duration.TotalMilliseconds,
                    metrics.RowsRead,
                    metrics.RowsWritten,
                    metrics.RowsDeleted,
                    metrics.ScopeCount,
                    success,
                    errorMessage));
        }
        catch (Exception ex)
        {
            _log.LogDebug(
                ex,
                "Failed to persist BandMaintenance timing for subphase {Subphase}.",
                subphase);
        }
    }

    private async Task<BandMaintenanceTimingMetrics> RefreshBandCurrentProjectionScopesAsync(
        IReadOnlyCollection<BandCurrentProjectionScopeKey> scopes,
        CancellationToken ct)
    {
        const int FallbackChunkSize = 128;

        if (scopes.Count == 0)
            return BandMaintenanceTimingMetrics.NoWork;

        var rebuildOptions =
            CreateBandCurrentProjectionRebuildOptions(_options.Value);
        _log.LogInformation(
            "Refreshing band current projection for {ScopeCount:N0} impacted scope(s); batchedMemberStatsAggregation={BatchedMemberStatsAggregation}.",
            scopes.Count,
            rebuildOptions.UseBatchedMemberStatsAggregation);
        BandCurrentProjectionIncrementalRefreshResult result;
        try
        {
            result = await _bandCurrentProjectionBuilder!.RefreshScopesAsync(
                scopes,
                rebuildOptions,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Band current projection maintenance hit a batch-level failure. Retrying in chunks of {ChunkSize:N0} scope(s).", FallbackChunkSize);
            return await RefreshBandCurrentProjectionScopesInChunksAsync(
                scopes,
                FallbackChunkSize,
                rebuildOptions,
                ct);
        }

        _log.LogInformation(
            "Band current projection maintenance complete in {ElapsedMs:N3} ms: {SuccessfulScopes:N0}/{ScopeCount:N0} scope(s), {DeletedRows:N0}->{InsertedRows:N0} rows, {FailedScopes:N0} failed.",
            result.TotalElapsedMs,
            result.SuccessfulScopes,
            result.ScopeCount,
            result.DeletedRows,
            result.InsertedRows,
            result.FailedScopes);
        if (result.FailedScopes > 0)
        {
            throw new InvalidOperationException(
                $"Band current projection failed for {result.FailedScopes}/{result.ScopeCount} scope(s).");
        }

        return GetBandCurrentProjectionTimingMetrics(result, scopes.Count);
    }

    internal static BandCurrentProjectionRebuildOptions
        CreateBandCurrentProjectionRebuildOptions(
            ScraperOptions options) =>
        new()
        {
            UseBatchedMemberStatsAggregation =
                options
                    .BandCurrentProjectionUseBatchedMemberStatsAggregation,
        };

    private async Task<BandMaintenanceTimingMetrics> RefreshBandCurrentProjectionScopesInChunksAsync(
        IReadOnlyCollection<BandCurrentProjectionScopeKey> scopes,
        int chunkSize,
        BandCurrentProjectionRebuildOptions rebuildOptions,
        CancellationToken ct)
    {
        var scopeChunks = scopes
            .GroupBy(static scope => scope.BandType, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group
                .OrderBy(static scope => scope.RankingScope, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static scope => scope.ScopeComboId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static scope => scope.SongId, StringComparer.OrdinalIgnoreCase)
                .Chunk(chunkSize))
            .ToArray();

        var successfulScopes = 0;
        var failedScopes = 0;
        var refreshedScopes = 0;
        long insertedRows = 0;
        long deletedRows = 0;
        long candidateRowsDeleted = 0;
        var elapsedMs = 0d;

        foreach (var chunk in scopeChunks)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await _bandCurrentProjectionBuilder!
                    .RefreshScopesAsync(
                        chunk,
                        rebuildOptions,
                        ct);
                refreshedScopes += result.ScopeCount;
                successfulScopes += result.SuccessfulScopes;
                failedScopes += result.FailedScopes;
                insertedRows += result.InsertedRows;
                deletedRows += result.DeletedRows;
                candidateRowsDeleted += result.CandidateRowsDeleted;
                elapsedMs += result.TotalElapsedMs;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedScopes += chunk.Length;
                _log.LogWarning(
                    ex,
                    "Band current projection fallback chunk failed for {BandType} ({ScopeCount:N0} scope(s)).",
                    chunk[0].BandType,
                    chunk.Length);
            }
        }

        _log.LogInformation(
            "Band current projection fallback maintenance complete in {ElapsedMs:N3} ms: {SuccessfulScopes:N0}/{ScopeCount:N0} scope(s), {DeletedRows:N0}->{InsertedRows:N0} rows, {CandidateRowsDeleted:N0} candidate row(s) deleted, {FailedScopes:N0} failed.",
            elapsedMs,
            successfulScopes,
            scopes.Count,
            deletedRows,
            insertedRows,
            candidateRowsDeleted,
            failedScopes);
        if (failedScopes > 0)
        {
            throw new InvalidOperationException(
                $"Band current projection fallback failed for {failedScopes}/{scopes.Count} scope(s).");
        }

        return new BandMaintenanceTimingMetrics(
            RowsRead: scopes.Count,
            RowsWritten: insertedRows,
            RowsDeleted: deletedRows,
            ScopeCount: refreshedScopes);
    }

    private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> MergeImpactedTeams(
        params IReadOnlyDictionary<string, IReadOnlyCollection<string>>[] sources)
    {
        var merged = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            foreach (var (bandType, teamKeys) in source)
            {
                if (!merged.TryGetValue(bandType, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    merged[bandType] = set;
                }

                foreach (var teamKey in teamKeys)
                    set.Add(teamKey);
            }
        }

        return merged.ToDictionary(
            static kvp => kvp.Key,
            static kvp => (IReadOnlyCollection<string>)kvp.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyCollection<BandCurrentProjectionScopeKey> MergeCurrentProjectionScopes(
        params IReadOnlyCollection<BandCurrentProjectionScopeKey>[] sources) =>
        BandCurrentProjectionScopeTracker.OrderedDistinct(sources.SelectMany(static source => source));

    internal static bool ShouldActivateShadowSnapshotsBeforeDerived(
        ScrapePassContext ctx,
        ScrapePhase resolvedPhases)
    {
        if (ctx.ScrapeId <= 0)
            return false;

        return resolvedPhases.HasFlag(ScrapePhase.SoloRankings)
            || resolvedPhases.HasFlag(ScrapePhase.SoloRivals)
            || resolvedPhases.HasFlag(ScrapePhase.SoloPlayerStats)
            || resolvedPhases.HasFlag(ScrapePhase.SoloPrecompute);
    }

    private static IReadOnlyList<(string SongId, string Instrument)> BuildExpectedSnapshotPairs(ScrapePassContext ctx)
        => ScrapeOrchestrator.BuildExpectedSoloLeaderboardPairs(ctx.ScrapeRequests);

    /// <summary>
    /// Run a post-scrape phase with timing and heap telemetry.
    /// Logs phase name, duration, and heap delta so the peak memory owner is identifiable.
    /// </summary>
    private async Task RunPhaseAsync(
        ScrapePassContext ctx,
        string phaseName,
        Func<Task> phase,
        bool alwaysPropagateFailure = false)
    {
        var criticality = PostScrapePhasePolicy.GetCriticality(phaseName);
        var heapBefore = GC.GetTotalMemory(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var startedAt = DateTime.UtcNow;
        StartDurablePhase(phaseName);
        UpdatePostProcessOperation(phaseName, $"Running {phaseName}");
        try
        {
            _phaseFaultInjector?.BeforePhase(phaseName);
            await phase();
        }
        catch (OperationCanceledException)
        {
            CompleteDurablePhase(phaseName, "cancelled");
            UpdatePostProcessOperation(phaseName, $"Cancelled {phaseName}");
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            CompleteDurablePhase(
                phaseName,
                "failed",
                criticality == PostScrapePhaseCriticality.BestEffort
                    ? ex.Message
                    : null,
                criticality == PostScrapePhaseCriticality.PublicationCritical
                    ? ex.Message
                    : null);
            UpdatePostProcessOperation(phaseName, $"Failed {phaseName}: {ex.Message}");
            RecordPhaseOutcome(ctx, phaseName, criticality, false, startedAt, sw.Elapsed, ex.Message);
            _log.LogWarning(
                ex,
                "PostScrape phase [{Phase}] failed ({Criticality}). Will retry next pass.",
                phaseName,
                criticality);
            if (criticality == PostScrapePhaseCriticality.PublicationCritical
                && (alwaysPropagateFailure || _persistence.EnforcePublicationCriticalPhases))
            {
                throw;
            }
            var heapAfterFailure = GC.GetTotalMemory(false);
            _log.LogInformation(
                "PostScrape phase [{Phase}] stopped after failure in {Elapsed}. Heap: {Before:N0} → {After:N0} ({Delta:+#,0;-#,0;0} bytes).",
                phaseName, sw.Elapsed, heapBefore, heapAfterFailure, heapAfterFailure - heapBefore);
            return;
        }
        sw.Stop();
        UpdatePostProcessOperation(phaseName, $"Completed {phaseName}");
        RecordPhaseOutcome(ctx, phaseName, criticality, true, startedAt, sw.Elapsed, null);
        CompleteDurablePhase(phaseName, "completed");
        var heapAfter = GC.GetTotalMemory(false);
        _log.LogInformation(
            "PostScrape phase [{Phase}] completed in {Elapsed}. Heap: {Before:N0} → {After:N0} ({Delta:+#,0;-#,0;0} bytes).",
            phaseName, sw.Elapsed, heapBefore, heapAfter, heapAfter - heapBefore);
    }

    /// <summary>
    /// Run a post-scrape phase that returns a result, with timing and heap telemetry.
    /// </summary>
    private async Task<T> RunPhaseAsync<T>(
        ScrapePassContext ctx,
        string phaseName,
        Func<Task<T>> phase,
        T defaultValue = default!,
        bool alwaysPropagateFailure = false,
        Func<T, PhaseCompletion>? completionSelector = null)
    {
        var criticality = PostScrapePhasePolicy.GetCriticality(phaseName);
        var heapBefore = GC.GetTotalMemory(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var startedAt = DateTime.UtcNow;
        T result = defaultValue;
        StartDurablePhase(phaseName);
        UpdatePostProcessOperation(phaseName, $"Running {phaseName}");
        try
        {
            _phaseFaultInjector?.BeforePhase(phaseName);
            result = await phase();
        }
        catch (OperationCanceledException)
        {
            CompleteDurablePhase(phaseName, "cancelled");
            UpdatePostProcessOperation(phaseName, $"Cancelled {phaseName}");
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            CompleteDurablePhase(
                phaseName,
                "failed",
                criticality == PostScrapePhaseCriticality.BestEffort
                    ? ex.Message
                    : null,
                criticality == PostScrapePhaseCriticality.PublicationCritical
                    ? ex.Message
                    : null);
            UpdatePostProcessOperation(phaseName, $"Failed {phaseName}: {ex.Message}");
            RecordPhaseOutcome(ctx, phaseName, criticality, false, startedAt, sw.Elapsed, ex.Message);
            _log.LogWarning(
                ex,
                "PostScrape phase [{Phase}] failed ({Criticality}). Will retry next pass.",
                phaseName,
                criticality);
            if (criticality == PostScrapePhaseCriticality.PublicationCritical
                && (alwaysPropagateFailure || _persistence.EnforcePublicationCriticalPhases))
            {
                throw;
            }
            var heapAfterFailure = GC.GetTotalMemory(false);
            _log.LogInformation(
                "PostScrape phase [{Phase}] stopped after failure in {Elapsed}. Heap: {Before:N0} → {After:N0} ({Delta:+#,0;-#,0;0} bytes).",
                phaseName, sw.Elapsed, heapBefore, heapAfterFailure, heapAfterFailure - heapBefore);
            return result;
        }
        sw.Stop();
        var completion = completionSelector?.Invoke(result) ?? PhaseCompletion.Completed;
        if (completion.Status == "skipped")
            EnsureSkipIsAllowed(phaseName, criticality);
        var detail = completion.Status == "skipped"
            ? $"Skipped {phaseName}: {completion.WarningMessage}"
            : $"Completed {phaseName}";
        UpdatePostProcessOperation(phaseName, detail);
        RecordPhaseOutcome(
            ctx,
            phaseName,
            criticality,
            true,
            startedAt,
            sw.Elapsed,
            null,
            completion.Status);
        CompleteDurablePhase(
            phaseName,
            completion.Status,
            completion.WarningMessage);
        var heapAfter = GC.GetTotalMemory(false);
        if (completion.Status == "skipped")
        {
            _log.LogInformation(
                "PostScrape phase [{Phase}] skipped in {Elapsed}: {Reason}. Heap: {Before:N0} → {After:N0} ({Delta:+#,0;-#,0;0} bytes).",
                phaseName,
                sw.Elapsed,
                completion.WarningMessage,
                heapBefore,
                heapAfter,
                heapAfter - heapBefore);
        }
        else
        {
            _log.LogInformation(
                "PostScrape phase [{Phase}] completed in {Elapsed}. Heap: {Before:N0} → {After:N0} ({Delta:+#,0;-#,0;0} bytes).",
                phaseName,
                sw.Elapsed,
                heapBefore,
                heapAfter,
                heapAfter - heapBefore);
        }
        return result;
    }

    private void StartDurablePhase(string phaseName)
    {
        var descriptor = PhaseProgressCatalog.FindPostScrape(phaseName);
        if (descriptor is null)
            return;
        var view = _phaseProgress?.StartPhase(descriptor);
        if (view is not null)
            _workerStatus?.ApplyDurableProgress(view);
    }

    private void CompleteDurablePhase(
        string phaseName,
        string status,
        string? warningMessage = null,
        string? errorMessage = null)
    {
        var descriptor = PhaseProgressCatalog.FindPostScrape(phaseName);
        if (descriptor is null)
            return;
        foreach (var progressView in _phaseProgress?.ObserveTracker(
                     _progress.GetProgressResponse().Current)
                 ?? [])
        {
            _workerStatus?.ApplyDurableProgress(progressView);
        }
        var view = _phaseProgress?.CompletePhase(
            descriptor.Id,
            status,
            warningMessage,
            errorMessage);
        if (view is not null)
            _workerStatus?.ApplyDurableProgress(view);
    }

    private void UpdatePostProcessOperation(
        string phaseName,
        string detail,
        double? progressPercent = null)
    {
        _workerStatus?.UpdateOperation(
            "scrape.post_process",
            phase: "PostScrapeEnrichment",
            subOperation: phaseName,
            detail: detail,
            progressPercent: progressPercent);
    }

    private void RecordSkippedPhase(
        ScrapePassContext ctx,
        string phaseName,
        string reason)
    {
        var criticality = PostScrapePhasePolicy.GetCriticality(phaseName);
        EnsureSkipIsAllowed(phaseName, criticality);
        var recordedAt = DateTime.UtcNow;
        StartDurablePhase(phaseName);
        UpdatePostProcessOperation(phaseName, $"Skipped {phaseName}: {reason}");
        RecordPhaseOutcome(
            ctx,
            phaseName,
            criticality,
            true,
            recordedAt,
            TimeSpan.Zero,
            null,
            "skipped");
        CompleteDurablePhase(phaseName, "skipped", reason);
        _log.LogInformation(
            "PostScrape phase [{Phase}] skipped: {Reason}.",
            phaseName,
            reason);
    }

    internal void RecordSkippedPhaseForTest(
        ScrapePassContext ctx,
        string phaseName,
        string reason) =>
        RecordSkippedPhase(ctx, phaseName, reason);

    internal Task RunClassifiedPhaseForTestAsync(
        ScrapePassContext ctx,
        string phaseName,
        Func<Task> phase,
        bool alwaysPropagateFailure = false) =>
        RunPhaseAsync(ctx, phaseName, phase, alwaysPropagateFailure);

    private void RecordPhaseOutcome(
        ScrapePassContext ctx,
        string phaseName,
        PostScrapePhaseCriticality criticality,
        bool success,
        DateTime startedAt,
        TimeSpan duration,
        string? errorMessage,
        string? status = null)
    {
        var outcomeStatus = status ?? (success ? "completed" : "failed");
        if (outcomeStatus == "skipped")
            EnsureSkipIsAllowed(phaseName, criticality);
        ctx.PostScrapeOutcomes.Record(new PostScrapePhaseOutcome(
            phaseName,
            criticality,
            success,
            errorMessage)
        {
            Status = outcomeStatus,
        });

        if (ctx.ScrapeId <= 0)
            return;

        try
        {
            _persistence.Meta.RecordScrapePhaseOutcome(new ScrapePhaseOutcomeRecord(
                ctx.ScrapeId,
                phaseName,
                criticality == PostScrapePhaseCriticality.PublicationCritical
                    ? "publication_critical"
                    : "best_effort",
                outcomeStatus,
                startedAt,
                startedAt + duration,
                (long)duration.TotalMilliseconds,
                errorMessage));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(
                ex,
                "Failed to persist post-scrape phase outcome for {Phase}.",
                phaseName);
            if (criticality == PostScrapePhaseCriticality.PublicationCritical
                && _persistence.EnforcePublicationCriticalPhases)
            {
                throw new InvalidOperationException(
                    $"Unable to persist publication-critical phase outcome for {phaseName}.",
                    ex);
            }
        }
    }

    private static void EnsureSkipIsAllowed(
        string phaseName,
        PostScrapePhaseCriticality criticality)
    {
        if (criticality != PostScrapePhaseCriticality.BestEffort)
        {
            throw new InvalidOperationException(
                $"Publication-critical phase '{phaseName}' cannot be recorded as skipped.");
        }
    }

    private async Task<T> RunWithPostScrapeNetworkTimeoutAsync<T>(
        string operationName,
        TimeSpan timeout,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        using var timeoutCts = timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeoutCts is not null)
            timeoutCts.CancelAfter(timeout);

        try
        {
            return await operation(timeoutCts?.Token ?? ct);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true && !ct.IsCancellationRequested)
        {
            _log.LogWarning(
                "Post-scrape {OperationName} timed out after {Timeout}. Continuing with downstream ranking and notification phases; work will retry next pass.",
                operationName,
                timeout);
            throw new TimeoutException(
                $"Post-scrape {operationName} timed out after {timeout}.");
        }
    }

    /// <summary>
    /// Four operations with partial parallelism: rank recomputation runs first,
    /// then pruning starts in parallel with FirstSeenSeason and account name resolution.
    /// Pruning only needs CHOpt max scores and registered IDs — it does not depend on
    /// FirstSeenSeason or account names.
    /// </summary>
    internal async Task RunEnrichmentAsync(ScrapePassContext ctx, FestivalService service, CancellationToken ct)
    {
        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.PostScrapeEnrichment);
        _progress.RegisterBranches(new[] { "rank_recompute", "first_seen", "name_resolution" });
        _progress.SetSubOperation("enriching_parallel_rank_recompute");

        Task rankTask;
        if (!_persistence.WriteLegacyLiveLeaderboardDuringScrape)
        {
            rankTask = RunPhaseAsync(
                ctx,
                "RankRecompute",
                () =>
                {
                    _progress.StartBranch("rank_recompute");
                    _progress.CompleteBranch(
                        "rank_recompute",
                        "skipped",
                        "legacy live leaderboard writes are disabled");
                    _log.LogInformation(
                        "Legacy rank recompute requires no work because legacy live leaderboard writes are disabled.");
                    return Task.CompletedTask;
                });
        }
        else
        {
            rankTask = RunPhaseAsync(ctx, "RankRecompute", () => Task.Run(() =>
            {
                _progress.StartBranch("rank_recompute");
                try
                {
                    var rankChangedSongs = ctx.Aggregates?.RankChangedSongIds;
                    if (rankChangedSongs is { Count: > 0 })
                    {
                        _progress.SetBranchTotal("rank_recompute", rankChangedSongs.Count);
                        _log.LogInformation("Recomputing ranks for {Count:N0} changed song(s) (of {Total:N0} total).",
                            rankChangedSongs.Count, ctx.ScrapeRequests.Count);
                        var rankUpdated = _persistence.RecomputeRanksForSongs(rankChangedSongs);
                        _progress.ReportBranchProgress("rank_recompute", rankChangedSongs.Count);
                        _log.LogInformation("Recomputed ranks across all instruments: {Count:N0} entries updated.", rankUpdated);
                        _progress.CompleteBranch("rank_recompute", "complete", $"{rankUpdated:N0} entries updated");
                    }
                    else
                    {
                        _log.LogInformation("No songs with rank-affecting changes. Skipping rank recomputation.");
                        _progress.CompleteBranch("rank_recompute", "skipped", "no rank-affecting changes");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Rank recomputation failed. Stored ranks may be stale.");
                    _progress.CompleteBranch("rank_recompute", "failed", ex.Message);
                    throw;
                }
            }, ct));
        }

        var firstSeenTask = RunPhaseAsync(ctx, "FirstSeenSeason", async () =>
        {
            _progress.StartBranch("first_seen");
            try
            {
                var firstSeenToken = await _tokenManager.GetAccessTokenAsync(ct);
                if (firstSeenToken is not null)
                {
                    IReadOnlyList<SeasonWindowInfo> firstSeenSeasonWindows;
                    try
                    {
                        firstSeenSeasonWindows = await _historyReconstructor.DiscoverSeasonWindowsAsync(
                            firstSeenToken,
                            _tokenManager.AccountId!,
                            ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogWarning(
                            ex,
                            "Season window discovery failed before FirstSeenSeason. Using stored windows.");
                        firstSeenSeasonWindows = _persistence.Meta.GetSeasonWindows();
                    }

                    if (firstSeenSeasonWindows.Count == 0)
                        firstSeenSeasonWindows = _persistence.Meta.GetSeasonWindows();

                    var firstSeenCount = await _firstSeenCalculator.CalculateAsync(
                        service, firstSeenToken, _tokenManager.AccountId!,
                        _pool,
                        ct,
                        firstSeenSeasonWindows,
                        authoritativeDiscoveryFresh: firstSeenSeasonWindows.Any(
                            static window =>
                                window.IsFreshAuthoritative
                                && window.SourceKind == "event_api"));
                    if (firstSeenCount > 0)
                        _log.LogInformation("Calculated FirstSeenSeason for {Count} song(s).", firstSeenCount);
                    _progress.CompleteBranch("first_seen", "complete",
                        firstSeenCount > 0 ? $"{firstSeenCount:N0} song(s) calculated" : "no songs needed calculation");
                }
                else
                {
                    _log.LogWarning("No access token for FirstSeenSeason calculation. Will retry next pass.");
                    _progress.CompleteBranch("first_seen", "skipped", "no access token");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "FirstSeenSeason calculation failed. Will retry next pass.");
                _progress.CompleteBranch("first_seen", "failed", ex.Message);
                throw;
            }
        });

        var nameResTask = RunPhaseAsync(ctx, "AccountNameResolution", async () =>
        {
            _progress.StartBranch("name_resolution");
            try
            {
                await _nameResolver.ResolveNewAccountsAsync(maxConcurrency: _options.Value.PageConcurrency, ct);
                _progress.CompleteBranch("name_resolution", "complete");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Account name resolution failed. Will retry next pass.");
                _progress.CompleteBranch("name_resolution", "failed", ex.Message);
                throw;
            }
        });

        Exception? rankFailure = null;
        try
        {
            await rankTask;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rankFailure = ex;
        }
        _progress.SetSubOperation("enriching_parallel_tail");
        await Task.WhenAll(firstSeenTask, nameResTask);
        if (rankFailure is not null)
            throw rankFailure;
    }

    /// <summary>
    /// Run account name resolution standalone (for --resolve-only mode).
    /// </summary>
    public Task<int> ResolveNamesAsync(int maxConcurrency, CancellationToken ct)
        => _nameResolver.ResolveNewAccountsAsync(maxConcurrency, ct);

    /// <summary>
    /// Compute per-instrument + composite + combo rankings and daily history snapshots.
    /// Runs after enrichment/pruning and registered-user refresh, before rivals.
    /// </summary>
    internal Task<bool> ComputeRankingsAsync(FestivalService service, CancellationToken ct)
        => ComputeRankingsAsync(service, 0, ct);

    internal async Task<bool> ComputeRankingsAsync(FestivalService service, long scrapeId, CancellationToken ct)
    {
        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.ComputingRankings);
        await _rankingsCalculator.ComputeAllAsync(service, ct, scrapeId);
        return true;
    }

    /// <summary>
    /// Refresh stale/missing entries for registered users using the song processing machine.
    /// Registration backfill and history reconstruction remain on their dedicated
    /// resumable workers so publication freshness cannot inherit historical backlog.
    /// All songs are processed in parallel, bounded by the shared DOP pool.
    /// </summary>
    internal async Task<SongProcessingMachine.MachineResult> RefreshRegisteredUsersAsync(ScrapePassContext ctx, CancellationToken ct)
    {
        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.SongMachine);
        var refreshTimeout = _options.Value.RegisteredUserRefreshTimeout
            ?? _options.Value.PostScrapeRefreshTimeout;
        using var refreshTimeoutCts = refreshTimeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (refreshTimeoutCts is not null)
            refreshTimeoutCts.CancelAfter(refreshTimeout);

        var refreshCt = refreshTimeoutCts?.Token ?? ct;

        try
        {
            var refreshToken = await _tokenManager.GetAccessTokenAsync(refreshCt);
            if (refreshToken is null)
            {
                _log.LogWarning("No access token for post-scrape refresh. Will retry next pass.");
                return new SongProcessingMachine.MachineResult();
            }

            var callerAccountId = _tokenManager.AccountId!;

            // Discover season windows. Runs every pass regardless of registered-user
            // count so the current-season signal (consumed by /api/songs via
            // MetaDatabase.GetCurrentSeason) stays fresh across season rollovers.
            _progress.SetSubOperation("discovering_season_windows");
            IReadOnlyList<Persistence.SeasonWindowInfo> seasonWindows;
            try
            {
                seasonWindows = await _historyReconstructor.DiscoverSeasonWindowsAsync(
                    refreshToken, callerAccountId, refreshCt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Season window discovery failed. Using empty season list.");
                seasonWindows = [];
            }

            if (seasonWindows.Count == 0)
                seasonWindows = _persistence.Meta.GetSeasonWindows();

            // Backstop: if the scraper has observed higher-numbered seasons in the
            // instrument DBs than the events API advertised (e.g. Epic renamed a
            // window and our regex missed the current season), persist a window
            // row for that season so GetCurrentSeason() reflects reality. event_id
            // and window_id are left blank; real values will be filled in when the
            // next events-API response matches.
            var instrumentMaxSeason = _persistence.GetMaxSeasonAcrossInstruments();
            if (instrumentMaxSeason is int floor)
            {
                var known = seasonWindows.Select(w => w.SeasonNumber).ToHashSet();
                for (int s = 1; s <= floor; s++)
                {
                    if (known.Contains(s)) continue;
                    _persistence.Meta.UpsertSeasonWindow(
                        s,
                        eventId: "",
                        windowId: "",
                        sourceKind: "synthetic");
                }
                if (floor > (seasonWindows.Count == 0 ? 0 : seasonWindows.Max(w => w.SeasonNumber)))
                {
                    _log.LogInformation(
                        "Season window floor raised from events-API max to instrument-DB max (season {Season}).",
                        floor);
                    seasonWindows = _persistence.Meta.GetSeasonWindows();
                }
            }

            if (ctx.RegisteredIds.Count == 0)
                return new SongProcessingMachine.MachineResult();

            await using var registrationLease =
                await _registrationMutations
                    .AcquireLeaseAsync(refreshCt);
            await registrationLease.VerifyHeldAsync(refreshCt);
            var chartedSongIds = ctx.ScrapeRequests
                .Select(static request => request.SongId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var orderedSongIds = _persistence.Meta.GetRegisteredUserRefreshSongOrder(
                chartedSongIds,
                GlobalLeaderboardScraper.AllInstruments);
            var currentSeason = Math.Max(
                seasonWindows.Count > 0
                    ? seasonWindows.Max(static window => window.SeasonNumber)
                    : 0,
                instrumentMaxSeason ?? 0);
            if (currentSeason <= 0)
                currentSeason = 1;
            var currentSeasonWindow = HistoryReconstructor.MergeSeasonWindows(
                    seasonWindows)
                .FirstOrDefault(window =>
                    window.SeasonNumber == currentSeason);
            var currentSeasonLookupId = currentSeasonWindow is null
                ? HistoryReconstructor.GetSeasonPrefix(currentSeason)
                : HistoryReconstructor.GetSeasonLookupId(currentSeasonWindow);
            RegisterKnownBandsForAccounts(ctx.RegisteredIds);
            LogRegisteredUserRefreshCoverage(
                "before",
                orderedSongIds,
                ctx.ScrapeId);

            // ── Build user list ──────────────────────────────────
            var users = new List<UserWorkItem>();

            // Post-scrape users
            foreach (var accountId in ctx.RegisteredIds)
            {
                var seasonsNeeded = new HashSet<int>();
                if (_options.Value.RefreshCurrentSeasonSessions)
                    seasonsNeeded.Add(currentSeason);

                users.Add(new UserWorkItem
                {
                    AccountId = accountId,
                    Purposes = WorkPurpose.PostScrape,
                    AllTimeNeeded = true,
                    SeasonsNeeded = seasonsNeeded,
                });
            }

            // ── Attach to the cyclical machine ──────────────────
            _progress.SetSubOperation("processing_songs");
            long nextOperationHeartbeatTicks = 0;
            var attachmentOptions = new CyclicalSongMachine.AttachmentOptions(
                PreserveSongOrder: true,
                OnScopesCompleted: scopes =>
                {
                    _persistence.Meta.UpsertRegisteredUserRefreshScopes(
                        ctx.ScrapeId,
                        scopes,
                        DateTime.UtcNow);
                    var nowTicks = DateTime.UtcNow.Ticks;
                    var scheduledTicks = Volatile.Read(
                        ref nextOperationHeartbeatTicks);
                    if (nowTicks >= scheduledTicks &&
                        Interlocked.CompareExchange(
                            ref nextOperationHeartbeatTicks,
                            nowTicks +
                                RegisteredRefreshOperationHeartbeatInterval.Ticks,
                            scheduledTicks) == scheduledTicks)
                    {
                        UpdatePostProcessOperation(
                            "RefreshRegisteredUsers",
                            "Refreshing registered users; completed scope batch persisted");
                    }
                    return ValueTask.CompletedTask;
                },
                CurrentSeason: currentSeason,
                CurrentSeasonLookupId: currentSeasonLookupId);

            SongProcessingMachine.MachineResult result;
            try
            {
                result = await _cyclicalMachine.AttachAsync(
                    users, orderedSongIds, seasonWindows,
                    SongMachineSource.PostScrape,
                    isHighPriority: true,
                    ct: refreshCt,
                    preserveProgressPhaseOnIdle: true,
                    attachmentOptions: attachmentOptions);
            }
            finally
            {
                LogRegisteredUserRefreshCoverage(
                    "after",
                    orderedSongIds,
                    ctx.ScrapeId);
            }

            if (result.EntriesUpdated > 0 || result.SessionsInserted > 0)
                _log.LogInformation("Song machine updated {Entries} entries, {Sessions} sessions for {Users} users.",
                    result.EntriesUpdated, result.SessionsInserted, result.UsersProcessed);

            return result;
        }
        catch (OperationCanceledException) when (refreshTimeoutCts?.IsCancellationRequested == true && !ct.IsCancellationRequested)
        {
            _log.LogWarning(
                "Post-scrape registered-user refresh timed out after {Timeout}. Continuing with downstream ranking and notification phases; refresh will retry next pass.",
                refreshTimeout);
            throw new TimeoutException(
                $"Post-scrape registered-user refresh timed out after {refreshTimeout}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Song processing machine failed. Will retry next pass.");
            throw;
        }
    }

    private void LogRegisteredUserRefreshCoverage(
        string stage,
        IReadOnlyCollection<string> songIds,
        long scrapeId)
    {
        try
        {
            var coverage = _persistence.Meta.GetRegisteredUserRefreshCoverage(
                songIds,
                GlobalLeaderboardScraper.AllInstruments,
                scrapeId,
                DateTime.UtcNow);
            _log.LogInformation(
                "Registered-user refresh coverage ({Stage}): expectedScopes={ExpectedScopes}, checkedScopes={CheckedScopes}, missingScopes={MissingScopes}, oldestCheckedAtUtc={OldestCheckedAtUtc}, oldestCheckedAge={OldestCheckedAge}, currentScrapeCompletions={CurrentScrapeCompletions}.",
                stage,
                coverage.ExpectedScopes,
                coverage.CheckedScopes,
                coverage.MissingScopes,
                coverage.OldestCheckedAtUtc,
                coverage.OldestCheckedAge,
                coverage.CurrentScrapeCompletions);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Unable to read bounded registered-user refresh coverage telemetry for stage {Stage}.",
                stage);
        }
    }

    private void RegisterKnownBandsForAccounts(IEnumerable<string> accountIds)
    {
        var registeredBands = _persistence.Meta.RegisterKnownBandsForAccountActivities(
            accountIds);

        if (registeredBands > 0)
            _log.LogDebug("Registered or refreshed {BandCount} known band(s) for tracked player history processing.", registeredBands);
    }

    private async Task<IReadOnlyCollection<SoloCurrentProjectionScopeKey>> BuildSoloProjectionScopesForNotificationsAsync(
        ScrapePassContext ctx,
        SongProcessingMachine.MachineResult registeredUserRefreshResult,
        ImprovementNotificationOptions options,
        CancellationToken ct)
    {
        var scopes = new HashSet<SoloCurrentProjectionScopeKey>();

        if (ctx.SoloCurrentProjectionRefreshedForPublication)
        {
            foreach (var scope in ctx.NotificationProjectionScopes)
                scopes.Add(scope);
            foreach (var scope in registeredUserRefreshResult.UpdatedScopes)
                scopes.Add(scope);
            return scopes.ToArray();
        }

        if (options.RefreshAllSoloScopesWhenNoImpactedScopes
            && _soloCurrentProjectionBuilder is not null)
        {
            return await _soloCurrentProjectionBuilder.LoadCurrentScopesAsync(ct);
        }

        throw new InvalidOperationException(
            "Improvement notification projection was not refreshed for publication, and unbounded scope fallback is disabled.");
    }

    /// <summary>
    /// Compute rivals for registered users whose scores (or rivals' scores) changed.
    /// </summary>
    internal async Task ComputeRivalsAsync(ScrapePassContext ctx, CancellationToken ct)
    {
        if (ctx.RegisteredIds.Count == 0)
            return;

        var dirtySongs = ctx.Aggregates.DirtyRivalSongs
            .Where(row => ctx.RegisteredIds.Contains(row.AccountId))
            .ToList();

        _log.LogInformation(
            "Song-rivals dirty summary: dirtySongs={DirtySongs}, dirtyAccounts={DirtyAccounts}, reasons={DirtyReasonCounts}.",
            dirtySongs.Count,
            dirtySongs.Select(row => row.AccountId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            FormatCountSummary(dirtySongs.GroupBy(row => row.DirtyReason, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase)));

        if (dirtySongs.Count > 0)
            _persistence.Meta.UpsertDirtyRivalSongs(dirtySongs);

        await _rivalsOrchestrator.ComputeAllAsync(ctx.RegisteredIds, null, ct);
    }

    private static string FormatCountSummary(IReadOnlyDictionary<string, int> counts)
    {
        if (counts.Count == 0)
            return "none";

        return string.Join(", ",
            counts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}"));
    }

    /// <summary>
    /// Compute leaderboard rivals for registered users. Per instrument per rank method,
    /// finds neighbors and compares shared songs.
    /// </summary>
    internal async Task ComputeLeaderboardRivalsAsync(ScrapePassContext ctx, CancellationToken ct)
    {
        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.ComputingRivals);
        if (ctx.RegisteredIds.Count == 0)
            return;

        var maxDegreeOfParallelism = Math.Max(
            1,
            _options.Value.LeaderboardRivalsMaxDegreeOfParallelism);
        _log.LogInformation(
            "Computing leaderboard rivals for {Count} registered user(s) with maxDegree={MaxDegree}.",
            ctx.RegisteredIds.Count,
            maxDegreeOfParallelism);
        _progress.BeginPhaseProgress(ctx.RegisteredIds.Count);

        await Parallel.ForEachAsync(
            ctx.RegisteredIds,
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
            },
            (accountId, _) =>
            {
                try
                {
                    var result = _leaderboardRivalsCalculator.ComputeForUser(
                        accountId,
                        rankingsAuthoritative:
                            ctx.RankingsComputedSuccessfully);
                    _log.LogDebug(
                        "Computed leaderboard rivals for {AccountId}: {Rivals} rival rows, {Samples} sample rows.",
                        accountId,
                        result.RivalCount,
                        result.SampleCount);
                    return ValueTask.CompletedTask;
                }
                finally
                {
                    _progress.ReportPhaseItemComplete();
                }
            });
    }

    internal Task RunLeaderboardRivalsPhaseAsync(
        ScrapePassContext ctx,
        CancellationToken ct) =>
        RunPhaseAsync(
            ctx,
            "LeaderboardRivals",
            () => ComputeLeaderboardRivalsAsync(ctx, ct),
            alwaysPropagateFailure: true);

    /// <summary>
    /// Prune excess entries from instrument DBs down to the configured max per song,
    /// preserving registered users. When CHOpt max scores are available, entries above
    /// the over-threshold boundary are exempt from pruning so that deep-scraped valid
    /// entries are not discarded along with exploited scores.
    /// Only depends on CHOpt max scores and registered IDs. It runs in the deferred
    /// cleanup phase after fresh derived state has been published.
    /// </summary>
    internal void PruneExcessEntries(ScrapePassContext ctx)
    {
        var maxPages = _options.Value.MaxPagesPerLeaderboard;
        if (maxPages <= 0) return; // unlimited — no pruning

        if (!_persistence.WriteLegacyLiveLeaderboardDuringScrape)
        {
            _log.LogInformation(
                "Skipping legacy live leaderboard excess prune because legacy live scrape writes are disabled; snapshot current-state replaces foreground solo prune.");
            return;
        }

        var maxEntries = maxPages * 100;
        try
        {
            // Build per-instrument, per-song threshold maps from CHOpt max scores.
            // Entries above CHOpt max × cutoff multiplier are kept unconditionally;
            // the maxEntries cap applies only to entries at or below the cutoff.
            var allMaxScores = _pathDataStore.GetAllMaxScores();
            var cutoffMultiplier = _options.Value.ValidCutoffMultiplier;
            Dictionary<string, IReadOnlyDictionary<string, int>>? thresholds = null;

            if (allMaxScores.Count > 0)
            {
                thresholds = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

                foreach (var instrument in _persistence.GetInstrumentKeys())
                {
                    var songMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (songId, maxScores) in allMaxScores)
                    {
                        var choptMax = maxScores.GetByInstrument(instrument);
                        if (choptMax.HasValue)
                            songMap[songId] = (int)(choptMax.Value * cutoffMultiplier);
                    }
                    if (songMap.Count > 0)
                        thresholds[instrument] = songMap;
                }

                if (thresholds.Count == 0)
                    thresholds = null;
            }

            var deleted = _persistence.PruneAllInstruments(maxEntries, ctx.RegisteredIds, thresholds);
            if (deleted > 0)
                _log.LogInformation("Pruned {Deleted:N0} excess entries (keeping top {Max:N0} valid per song + registered users).",
                    deleted, maxEntries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Entry pruning failed. Will retry next pass.");
            throw;
        }
    }

    /// <summary>
    /// Prune excess band entries. For each song × band type, keep all over-threshold
    /// entries at the top plus the next 10K valid entries plus any team containing a
    /// registered user. Cascades to band_member_stats and band_members.
    /// </summary>
    internal BandPruneResult PruneBandEntries(ScrapePassContext ctx)
    {
        try
        {
            var result = _bandPersistence.PruneBandEntriesDetailed(ctx.RegisteredIds);
            if (result.DeletedEntries > 0)
                _log.LogInformation("Band pruning complete: {Deleted:N0} entries removed.", result.DeletedEntries);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Band entry pruning failed. Will retry next pass.");
            return BandPruneResult.Empty;
        }
    }

    /// <summary>
    /// Compute leeway-tiered player stats for accounts whose scores changed in this scrape.
    /// Pass 2 of the two-pass incremental strategy — score-dependent aggregates only.
    /// (Pass 1 — rank refresh for all accounts — is future work.)
    /// </summary>
    internal async Task ComputePlayerStatsTiersAsync(
        ScrapePassContext ctx,
        CancellationToken ct)
    {
        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.Precomputing);
        _progress.SetSubOperation("population_tiers");
        var changedIds = ctx.Aggregates.ChangedAccountIds;
        // Also include registered users (their stats should always be fresh)
        var accountIds = new HashSet<string>(changedIds, StringComparer.OrdinalIgnoreCase);
        foreach (var id in ctx.RegisteredIds)
            accountIds.Add(id);
        var normalizedAccountIds =
            MaxScoreMaintenanceAccountIdPolicy.NormalizeSet(accountIds);

        _log.LogInformation("Computing player stats tiers for {Count:N0} accounts ({Changed:N0} changed + {Registered:N0} registered).",
            normalizedAccountIds.Length, changedIds.Count, ctx.RegisteredIds.Count);
        _progress.BeginPhaseProgress(normalizedAccountIds.Length);
        var reportedAccounts = 0;
        await PlayerStatsTierRebuilder.RebuildAsync(
            _persistence,
            _pathDataStore,
            normalizedAccountIds,
            _log,
            ct,
            onProgress: (completed, _) =>
            {
                var delta = completed - reportedAccounts;
                reportedAccounts = completed;
                _progress.ReportPhaseItemsComplete(delta);
            });
    }
}
