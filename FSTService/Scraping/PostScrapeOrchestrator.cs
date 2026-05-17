using FortniteFestival.Core.Scraping;
using FortniteFestival.Core.Services;
using FSTService;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Persistence.Maintenance;
using Microsoft.Extensions.DependencyInjection;
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
    private const int PlayerStatsTierAccountChunkSize = 512;

    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly FirstSeenSeasonCalculator _firstSeenCalculator;
    private readonly AccountNameResolver _nameResolver;
    private readonly PostScrapeRefresher _refresher;
    private readonly IServiceProvider _serviceProvider;
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
    private readonly SoloCurrentProjectionBuilder? _soloCurrentProjectionBuilder;
    private readonly IOptions<ImprovementNotificationOptions> _improvementNotificationOptions;
    private readonly IOptions<BandRankHistoryOptions> _bandRankHistoryOptions;
    private readonly IOptions<DatabaseMaintenanceOptions> _databaseMaintenanceOptions;
    private readonly IDatabasePressureMonitor? _databasePressureMonitor;
    private readonly IDatabaseRetentionMaintenanceService? _retentionMaintenanceService;
    private readonly IOptions<ScraperOptions> _options;
    private readonly ILogger<PostScrapeOrchestrator> _log;

    public PostScrapeOrchestrator(
        GlobalLeaderboardPersistence persistence,
        FirstSeenSeasonCalculator firstSeenCalculator,
        AccountNameResolver nameResolver,
        PostScrapeRefresher refresher,
        IServiceProvider serviceProvider,
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
        IDatabaseRetentionMaintenanceService? retentionMaintenanceService = null)
    {
        _persistence = persistence;
        _firstSeenCalculator = firstSeenCalculator;
        _nameResolver = nameResolver;
        _refresher = refresher;
        _serviceProvider = serviceProvider;
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
        _soloCurrentProjectionBuilder = soloCurrentProjectionBuilder;
        _improvementNotificationOptions = improvementNotificationOptions ?? Options.Create(new ImprovementNotificationOptions());
        _bandRankHistoryOptions = bandRankHistoryOptions ?? Options.Create(new BandRankHistoryOptions());
        _databaseMaintenanceOptions = databaseMaintenanceOptions ?? Options.Create(new DatabaseMaintenanceOptions());
        _databasePressureMonitor = databasePressureMonitor;
        _retentionMaintenanceService = retentionMaintenanceService;
        _options = options;
        _log = log;
    }

    /// <summary>
    /// Run post-scrape phases gated by <paramref name="resolvedPhases"/>.
    /// When all phases are enabled this behaves identically to the original pipeline.
    /// </summary>
    public async Task RunAsync(ScrapePassContext ctx, FestivalService service, ScrapePhase resolvedPhases, CancellationToken ct)
    {
        // ── Solo enrichment ──
        if (resolvedPhases.HasFlag(ScrapePhase.SoloEnrichment))
            await RunPhaseAsync("Enrichment", () => RunEnrichmentAsync(ctx, service, ct));

        // ── Solo refresh registered users ──
        var registeredUserRefreshResult = new SongProcessingMachine.MachineResult();
        if (resolvedPhases.HasFlag(ScrapePhase.SoloRefreshUsers))
            registeredUserRefreshResult = await RunPhaseAsync(
                "RefreshRegisteredUsers",
                () => RefreshRegisteredUsersAsync(ctx, ct),
                new SongProcessingMachine.MachineResult());

        var expectedSnapshotPairs = BuildExpectedSnapshotPairs(ctx);
        if (ShouldActivateShadowSnapshotsBeforeDerived(ctx, resolvedPhases))
        {
            _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.PostScrapeEnrichment);
            _progress.SetSubOperation("activating_shadow_snapshots_early");
            _progress.BeginPhaseProgress(1);
            await RunPhaseAsync("ActivateShadowSnapshotsEarly", () =>
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
        bool bandAlreadyFetched = resolvedPhases.HasFlag(ScrapePhase.BandScrape);
        if (resolvedPhases.HasFlag(ScrapePhase.BandScrapePhase) && !bandAlreadyFetched)
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
            bandExtractionResult = await RunPhaseAsync("BandExtraction", () => _bandExtractor.RunAsync(ct), BandExtractionResult.Empty);
        }

        if (bandScrapeTask is not null)
        {
            try
            {
                await bandScrapeTask;
                _log.LogInformation("Background band scrape completed successfully.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Background band scrape failed. Band data may be incomplete this cycle.");
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
                    "RegisteredPlayerBandDiscovery",
                    () => registeredPlayerBandDiscoveryOrchestrator.RunAsync(
                        chartedSongIds,
                        seasonWindows,
                        bandAccessToken,
                        _tokenManager.AccountId!,
                        _pool,
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
                    "RegisteredBandTargetedProcessing",
                    () => registeredBandProcessingOrchestrator.RunAsync(
                        chartedSongIds,
                        seasonWindows,
                        bandAccessToken,
                        _tokenManager.AccountId!,
                        _pool,
                        ct),
                    RegisteredBandProcessingResult.Empty);
            }
            else
            {
                _log.LogWarning("No access token for registered-band targeted processing. Will retry next pass.");
            }
        }

        if (ShouldRunBandMaintenance(resolvedPhases)
            || registeredPlayerBandDiscoveryResult.ImpactedTeamsByBandType.Count > 0
            || registeredBandProcessingResult.ImpactedTeamsByBandType.Count > 0)
        {
            _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.BandScraping);
            _progress.SetSubOperation("maintaining_band_projection");
            var mergedExtractionResult = bandExtractionResult with
            {
                ImpactedTeamsByBandType = MergeImpactedTeams(
                    bandExtractionResult.ImpactedTeamsByBandType,
                    registeredPlayerBandDiscoveryResult.ImpactedTeamsByBandType,
                    registeredBandProcessingResult.ImpactedTeamsByBandType),
                ImpactedCurrentProjectionScopes = MergeCurrentProjectionScopes(
                    bandExtractionResult.ImpactedCurrentProjectionScopes,
                    registeredPlayerBandDiscoveryResult.ImpactedCurrentProjectionScopes,
                    registeredBandProcessingResult.ImpactedCurrentProjectionScopes),
            };
            await RunPhaseAsync("BandMaintenance", () => RunBandMaintenanceAsync(ctx, mergedExtractionResult, ct));
        }

        // ── Solo rankings ──
        var rankingsSucceeded = false;
        if (resolvedPhases.HasFlag(ScrapePhase.SoloRankings))
            rankingsSucceeded = await RunPhaseAsync("ComputeRankings", () => ComputeRankingsAsync(service, ctx.ScrapeId, ct));

        // ── Solo rivals ──
        if (resolvedPhases.HasFlag(ScrapePhase.SoloRivals))
        {
            await RunPhaseAsync("Rivals", () => ComputeRivalsAsync(ctx, ct));
        }

        // ── Solo player stats ──
        if (resolvedPhases.HasFlag(ScrapePhase.SoloPlayerStats))
        {
            _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.Precomputing);
            await RunPhaseAsync("PlayerStatsTiers", () => ComputePlayerStatsTiersAsync(ctx, ct));
        }

        // ── Solo finalize ──
        if (resolvedPhases.HasFlag(ScrapePhase.SoloFinalize))
        {
            _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.Finalizing);
            _progress.RegisterBranches(new[] { "final_checkpoint", "pre_warming_cache" });
            await RunPhaseAsync("Checkpoint", () => Task.Run(() =>
            {
                _progress.StartBranch("final_checkpoint");
                _progress.SetSubOperation("final_checkpoint");
                try
                {
                    _persistence.CheckpointAll();
                    _progress.CompleteBranch("final_checkpoint", "complete");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _progress.CompleteBranch("final_checkpoint", "failed", ex.Message);
                    throw;
                }
            }, ct));

            StartBestEffortCacheWarm(ctx.RegisteredIds);

            if (ctx.ScrapeId > 0)
            {
                await RunPhaseAsync("ActivateShadowSnapshots", () =>
                {
                    _persistence.FinalizeShadowSnapshots(ctx.ScrapeId, wave: 2, expectedPairs: expectedSnapshotPairs);
                    return Task.CompletedTask;
                });
            }
        }

        if (rankingsSucceeded && ShouldRunImprovementNotifications(ctx, resolvedPhases))
        {
            await RunPhaseAsync(
                "ImprovementNotifications",
                () => RunImprovementNotificationDetectionAsync(ctx, registeredUserRefreshResult, ct),
                rethrowOnFailure: _improvementNotificationOptions.Value.FailScrapeOnError);
        }

        // ── Await background band scrape for exception observation ──
        if (bandScrapeTask is not null)
        {
            try
            {
                await bandScrapeTask;
                _log.LogInformation("Background band scrape completed successfully.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Background band scrape failed. Band data may be incomplete this cycle.");
            }
        }
    }

    /// <summary>
    /// Run publication-critical cleanup after snapshots have been finalized but before
    /// response caches are unfrozen. This keeps persisted precomputed API payloads
    /// aligned with the current projections they are built from.
    /// </summary>
    public async Task RunPublicationCleanupAsync(ScrapePassContext ctx, ScrapePhase resolvedPhases, CancellationToken ct)
    {
        var cleanupItems = 0;
        var refreshSoloCurrentProjection = ShouldRefreshSoloCurrentProjectionDuringCleanup(ctx, resolvedPhases);
        var precomputeApiResponses = ShouldPrecomputeDuringPublicationCleanup(resolvedPhases);

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
            await RunPhaseAsync("Cleanup.SoloCurrentProjection", () => RefreshSoloCurrentProjectionForCleanupAsync(ct));
        }

        if (precomputeApiResponses)
        {
            _persistence.Meta.ClearBackfillRankingsPending(ctx.RegisteredIds);
            await RunPhaseAsync("Cleanup.PrecomputeAll", () => PrecomputeAllForCleanupAsync(ctx.EpicReportedOver100Pages, ct));
        }
    }

    /// <summary>
    /// Process users who registered during an active scrape/update after the current
    /// cycle's ranking and precompute publication has already run. Their raw scores
    /// and song-rivals become visible, while global rank-derived outputs remain
    /// flagged as pending until the next ranking pass includes them in ctx.RegisteredIds.
    /// </summary>
    public async Task RunDeferredRegistrationSyncAsync(ScrapePassContext ctx, FestivalService service, ScrapePhase resolvedPhases, CancellationToken ct)
    {
        if (!resolvedPhases.HasFlag(ScrapePhase.SoloRefreshUsers))
            return;

        var deferredBackfills = _persistence.Meta.GetDeferredBackfills();
        if (deferredBackfills.Count == 0)
            return;

        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is null)
        {
            _log.LogWarning("No access token for deferred registration sync. Will retry next pass.");
            return;
        }

        if (service.Songs.Count == 0)
            await service.InitializeAsync();

        var chartedSongIds = service.Songs
            .Select(static song => song.track?.su)
            .Where(static songId => !string.IsNullOrWhiteSpace(songId))
            .Select(static songId => songId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (chartedSongIds.Count == 0)
        {
            _log.LogWarning("Deferred registration sync skipped because no charted songs are loaded.");
            return;
        }

        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.RefreshingRegisteredUsers);
        _progress.SetSubOperation("deferred_registration_sync");

        IReadOnlyList<Persistence.SeasonWindowInfo> seasonWindows;
        try
        {
            seasonWindows = await _historyReconstructor.DiscoverSeasonWindowsAsync(
                accessToken, _tokenManager.AccountId!, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Season window discovery failed during deferred registration sync. Using stored season windows.");
            seasonWindows = _persistence.Meta.GetSeasonWindows();
        }

        var instrumentMaxSeason = _persistence.GetMaxSeasonAcrossInstruments();
        if (instrumentMaxSeason is int floor)
        {
            var known = seasonWindows.Select(w => w.SeasonNumber).ToHashSet();
            for (int s = 1; s <= floor; s++)
            {
                if (known.Contains(s)) continue;
                _persistence.Meta.UpsertSeasonWindow(s, eventId: "", windowId: "");
            }

            if (floor > (seasonWindows.Count == 0 ? 0 : seasonWindows.Max(w => w.SeasonNumber)))
                seasonWindows = _persistence.Meta.GetSeasonWindows();
        }

        var currentSeason = instrumentMaxSeason
            ?? (seasonWindows.Count == 0 ? 1 : seasonWindows.Max(w => w.SeasonNumber));
        var allSeasons = seasonWindows.Select(w => w.SeasonNumber).ToHashSet();
        if (allSeasons.Count == 0)
            allSeasons.Add(currentSeason);

        var users = new List<UserWorkItem>(deferredBackfills.Count);
        foreach (var backfill in deferredBackfills)
        {
            var totalPairs = backfill.TotalSongsToCheck > 0
                ? backfill.TotalSongsToCheck
                : chartedSongIds.Count * GlobalLeaderboardScraper.AllInstruments.Count;

            _persistence.Meta.StartBackfill(backfill.AccountId);
            _syncTracker.BeginBackfill(backfill.AccountId, totalPairs);

            users.Add(new UserWorkItem
            {
                AccountId = backfill.AccountId,
                Purposes = WorkPurpose.Backfill | WorkPurpose.HistoryRecon,
                AllTimeNeeded = true,
                SeasonsNeeded = new HashSet<int>(allSeasons),
                AlreadyChecked = _persistence.Meta.GetCheckedBackfillPairs(backfill.AccountId),
            });
        }

        _log.LogInformation(
            "Running deferred registration sync for {Count} user(s) after current-cycle derived publication.",
            users.Count);

        var result = await _cyclicalMachine.AttachAsync(
            users,
            chartedSongIds,
            seasonWindows,
            SongMachineSource.PostScrape,
            isHighPriority: true,
            ct: ct,
            preserveProgressPhaseOnIdle: true);

        if (result.EntriesUpdated > 0 || result.SessionsInserted > 0)
            _log.LogInformation("Deferred registration sync updated {Entries} entries, {Sessions} sessions for {Users} users.",
                result.EntriesUpdated, result.SessionsInserted, result.UsersProcessed);

        foreach (var user in users)
        {
            try
            {
                _persistence.Meta.CompleteBackfill(user.AccountId, rankingsPending: true);
                _rivalsOrchestrator.ComputeForUser(user.AccountId, forceRecompute: true);
                _ = _notifications.NotifyBackfillCompleteAsync(user.AccountId);

                var reconStatus = _persistence.Meta.GetHistoryReconStatus(user.AccountId);
                if (reconStatus is null)
                    _persistence.Meta.EnqueueHistoryRecon(user.AccountId, 0);

                _persistence.Meta.CompleteHistoryRecon(user.AccountId);
                _ = _notifications.NotifyHistoryReconCompleteAsync(user.AccountId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Deferred registration post-sync actions failed for {AccountId}.", user.AccountId);
            }
        }
    }

    /// <summary>
    /// Run best-effort database cleanup after derived state has been published.
    /// This phase must not include scrape writer resource cleanup; spool disposal
    /// stays with the writer lifecycle so disk is released as soon as possible.
    /// </summary>
    public async Task RunCleanupAsync(ScrapePassContext ctx, ScrapePhase resolvedPhases, CancellationToken ct)
    {
        var cleanupItems = 0;
        var cleanupSoloExcessEntries = resolvedPhases.HasFlag(ScrapePhase.SoloEnrichment);
        var cleanupRankHistoryRetention = resolvedPhases.HasFlag(ScrapePhase.SoloRankings);
        var cleanupBandRankHistoryRetention = resolvedPhases.HasFlag(ScrapePhase.SoloRankings);
        var cleanupServiceLevelRetention = ShouldRunServiceLevelRetentionMaintenance(resolvedPhases);

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
            await RunPhaseAsync("Cleanup.SoloExcessEntries", () => Task.Run(() =>
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
            if (await ShouldSkipMaintenanceCleanupAsync("rank history retention", ct))
                ReportSkippedCleanupItems(GlobalLeaderboardScraper.AllInstruments.Count + 1);
            else
                await RunPhaseAsync("Cleanup.RankHistoryRetention", () => CleanupRankHistoryRetentionAsync(ct));
        }

        if (cleanupBandRankHistoryRetention)
        {
            if (await ShouldSkipMaintenanceCleanupAsync("band rank history retention", ct))
                ReportSkippedCleanupItems(BandInstrumentMapping.AllBandTypes.Count);
            else
                await RunPhaseAsync("Cleanup.BandRankHistoryRetention", () => CleanupBandRankHistoryRetentionAsync(ct));
        }

        if (cleanupServiceLevelRetention)
            await RunPhaseAsync("Cleanup.ServiceLevelRetention", () => RunServiceLevelRetentionMaintenanceAsync(ct));
    }

    private bool ShouldRunServiceLevelRetentionMaintenance(ScrapePhase resolvedPhases) =>
        _retentionMaintenanceService is not null &&
        _databaseMaintenanceOptions.Value.ServiceLevelRetentionMaintenanceEnabled &&
        resolvedPhases.HasFlag(ScrapePhase.SoloFinalize);

    private async Task RunServiceLevelRetentionMaintenanceAsync(CancellationToken ct)
    {
        _progress.SetSubOperation("cleanup_service_level_retention");
        try
        {
            var result = await _retentionMaintenanceService!.RunAsync(ct);
            if (result.Skipped)
            {
                _log.LogInformation("Service-level retention maintenance skipped: {Reason}.", result.Reason);
                return;
            }

            _log.LogInformation(
                "Service-level retention maintenance completed: {Reason}. Snapshot candidates={SnapshotCandidates:N0}, rewrites={RewriteCount:N0}, metadata rows deleted={MetadataDeleted:N0}.",
                result.Reason,
                result.SnapshotRetention.CandidateCount,
                result.SnapshotRetention.RewriteResults.Count,
                result.MetadataCleanup.TotalDeletedRows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Service-level retention maintenance failed. Continuing without blocking fresh data publication.");
        }
        finally
        {
            _progress.ReportPhaseItemComplete();
        }
    }

    private async Task<bool> ShouldSkipMaintenanceCleanupAsync(string cleanupName, CancellationToken ct)
    {
        var options = _databaseMaintenanceOptions.Value;
        if (!options.SkipCleanupWhenPressureDetected || _databasePressureMonitor is null)
            return false;

        var snapshot = await _databasePressureMonitor.GetPressureSnapshotAsync(options, ct);
        if (!snapshot.IsUnderPressure)
            return false;

        _log.LogWarning(
            "Skipping {CleanupName} cleanup because database pressure is already high: {Reasons}.",
            cleanupName,
            string.Join("; ", snapshot.Reasons));
        return true;
    }

    private void ReportSkippedCleanupItems(int itemCount)
    {
        for (var i = 0; i < itemCount; i++)
            _progress.ReportPhaseItemComplete();
    }

    private static int PositiveOrDefault(int value, int fallback) => value > 0 ? value : fallback;

    private bool ShouldRefreshSoloCurrentProjectionDuringCleanup(ScrapePassContext ctx, ScrapePhase resolvedPhases)
    {
        if (_soloCurrentProjectionBuilder is null ||
            !_options.Value.RefreshSoloProjectionDuringCleanup ||
            !resolvedPhases.HasFlag(ScrapePhase.SoloFinalize))
        {
            return false;
        }

        var minimumCoverage = _improvementNotificationOptions.Value.MinimumSoloLeaderboardCoverageRatio;
        if (HasSufficientSoloScrapeCoverage(ctx, resolvedPhases, minimumCoverage, out var actualSoloLeaderboards, out var expectedSoloLeaderboards, out var coverage))
            return true;

        _log.LogWarning(
            "Cleanup solo current projection refresh skipped because solo scrape coverage was below threshold: {Actual:N0}/{Expected:N0} leaderboards with data ({Coverage:P1}) below required {Required:P1}. Preserving previous current projection.",
            actualSoloLeaderboards,
            expectedSoloLeaderboards,
            coverage,
            minimumCoverage);

        return false;
    }

    private static bool ShouldPrecomputeDuringPublicationCleanup(ScrapePhase resolvedPhases) =>
        resolvedPhases.HasFlag(ScrapePhase.SoloPrecompute);

    private async Task PrecomputeAllForCleanupAsync(bool showLeaderboardEntryTotals, CancellationToken ct)
    {
        _progress.SetSubOperation("cleanup_api_precompute");
        try
        {
            await _precomputer.PrecomputeAllAsync(showLeaderboardEntryTotals, ct, publishImmediately: false);
        }
        finally
        {
            _progress.ReportPhaseItemComplete();
        }
    }

    private async Task RefreshSoloCurrentProjectionForCleanupAsync(CancellationToken ct)
    {
        _progress.SetSubOperation("cleanup_solo_current_projection");
        try
        {
            var builder = _soloCurrentProjectionBuilder;
            if (builder is null)
                return;

            var staleScopes = await builder.LoadStaleScopesAsync(ct);
            if (staleScopes.Count == 0)
            {
                _log.LogInformation("Cleanup solo current projection refresh skipped; no stale scopes found.");
                return;
            }

            var options = _options.Value;
            var refreshOptions = new SoloCurrentProjectionRebuildOptions
            {
                CommandTimeoutSeconds = Math.Max(0, options.SoloProjectionCleanupCommandTimeoutSeconds),
                MaxDegreeOfParallelism = Math.Max(1, options.SoloProjectionCleanupMaxDegreeOfParallelism),
            };

            _log.LogInformation(
                "Cleanup refreshing {ScopeCount:N0} stale solo current projection scope(s) with maxDegree={MaxDegree}.",
                staleScopes.Count,
                refreshOptions.MaxDegreeOfParallelism);

            var result = await builder.RefreshScopesAsync(staleScopes, refreshOptions, ct);
            if (result.FailedScopeCount > 0)
            {
                _log.LogWarning(
                    "Cleanup solo current projection refresh completed with failures: {Succeeded:N0}/{ScopeCount:N0} scope(s), {Failed:N0} failed, rows {Deleted:N0}->{Inserted:N0}, elapsed {ElapsedMs:N0}ms.",
                    result.SucceededScopeCount,
                    result.ScopeCount,
                    result.FailedScopeCount,
                    result.DeletedRows,
                    result.InsertedRows,
                    result.TotalElapsedMs);
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
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Cleanup solo current projection refresh failed. Stale scopes will fall back to snapshot reads and retry next cleanup.");
        }
        finally
        {
            _progress.ReportPhaseItemComplete();
        }
    }

    private async Task CleanupRankHistoryRetentionAsync(CancellationToken ct)
    {
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
        }
        finally
        {
            _progress.ReportPhaseItemComplete();
        }
    }

    private async Task CleanupBandRankHistoryRetentionAsync(CancellationToken ct)
    {
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
            }
            finally
            {
                _progress.ReportPhaseItemComplete();
            }
        }
    }

    private static bool ShouldRunBandMaintenance(ScrapePhase resolvedPhases) =>
        resolvedPhases.HasFlag(ScrapePhase.BandScrape) ||
        resolvedPhases.HasFlag(ScrapePhase.BandScrapePhase) ||
        resolvedPhases.HasFlag(ScrapePhase.BandExtraction) ||
        resolvedPhases.HasFlag(ScrapePhase.SoloEnrichment);

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

    private bool ShouldRunImprovementNotifications(ScrapePassContext ctx, ScrapePhase resolvedPhases)
    {
        var options = _improvementNotificationOptions.Value;
        if (_improvementNotifications is null || !options.Enabled)
            return false;

        return HasSufficientSoloScrapeCoverageForNotifications(ctx, resolvedPhases, options);
    }

    internal bool HasSufficientSoloScrapeCoverageForNotifications(
        ScrapePassContext ctx,
        ScrapePhase resolvedPhases,
        ImprovementNotificationOptions options)
    {
        var minimumCoverage = options.MinimumSoloLeaderboardCoverageRatio;
        if (HasSufficientSoloScrapeCoverage(ctx, resolvedPhases, minimumCoverage, out var actualSoloLeaderboards, out var expectedSoloLeaderboards, out var coverage))
            return true;

        _log.LogWarning(
            "Improvement notifications skipped because solo scrape coverage was below threshold: {Actual:N0}/{Expected:N0} leaderboards with data ({Coverage:P1}) below required {Required:P1}.",
            actualSoloLeaderboards,
            expectedSoloLeaderboards,
            coverage,
            minimumCoverage);
        return false;
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

    private async Task RunBandMaintenanceAsync(ScrapePassContext ctx, BandExtractionResult extractionResult, CancellationToken ct)
    {
        var pruneResult = PruneBandEntries(ctx);
        var impactedTeams = MergeImpactedTeams(
            extractionResult.ImpactedTeamsByBandType,
            pruneResult.AffectedTeamsByBandType);
        var impactedCurrentProjectionScopes = MergeCurrentProjectionScopes(
            extractionResult.ImpactedCurrentProjectionScopes,
            pruneResult.AffectedCurrentProjectionScopes);

        if (_bandSearchProjectionBuilder is not null)
        {
            var refreshResult = await _bandSearchProjectionBuilder.RefreshIncrementalAsync(impactedTeams, ct);
            if (!refreshResult.ProjectionAvailable)
            {
                _log.LogDebug("Band search projection refresh skipped because no published projection state exists.");
            }
            else
            {
                _log.LogInformation(
                    "Band search projection maintenance complete: {ImpactedTeams:N0} impacted team(s), " +
                    "teams {DeletedTeams:N0}->{InsertedTeams:N0}, members {DeletedMembers:N0}->{InsertedMembers:N0}.",
                    refreshResult.ImpactedTeams,
                    refreshResult.DeletedTeamRows,
                    refreshResult.InsertedTeamRows,
                    refreshResult.DeletedMemberRows,
                    refreshResult.InsertedMemberRows);
            }
        }

        if (_bandCurrentProjectionBuilder is not null)
            await RefreshBandCurrentProjectionScopesAsync(impactedCurrentProjectionScopes, ct);
    }

    private async Task RefreshBandCurrentProjectionScopesAsync(
        IReadOnlyCollection<BandCurrentProjectionScopeKey> scopes,
        CancellationToken ct)
    {
        const int FallbackChunkSize = 128;

        if (scopes.Count == 0)
            return;

        _log.LogInformation("Refreshing band current projection for {ScopeCount:N0} impacted scope(s).", scopes.Count);

        BandCurrentProjectionIncrementalRefreshResult result;
        try
        {
            result = await _bandCurrentProjectionBuilder!.RefreshScopesAsync(scopes, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Band current projection maintenance hit a batch-level failure. Retrying in chunks of {ChunkSize:N0} scope(s).", FallbackChunkSize);
            await RefreshBandCurrentProjectionScopesInChunksAsync(scopes, FallbackChunkSize, ct);
            return;
        }

        _log.LogInformation(
            "Band current projection maintenance complete in {ElapsedMs:N3} ms: {SuccessfulScopes:N0}/{ScopeCount:N0} scope(s), {DeletedRows:N0}->{InsertedRows:N0} rows, {FailedScopes:N0} failed.",
            result.TotalElapsedMs,
            result.SuccessfulScopes,
            result.ScopeCount,
            result.DeletedRows,
            result.InsertedRows,
            result.FailedScopes);
    }

    private async Task RefreshBandCurrentProjectionScopesInChunksAsync(
        IReadOnlyCollection<BandCurrentProjectionScopeKey> scopes,
        int chunkSize,
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
        long insertedRows = 0;
        long deletedRows = 0;
        long candidateRowsDeleted = 0;
        var elapsedMs = 0d;

        foreach (var chunk in scopeChunks)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await _bandCurrentProjectionBuilder!.RefreshScopesAsync(chunk, ct: ct);
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

    private void StartBestEffortCacheWarm(IReadOnlyCollection<string> registeredIds)
    {
        _progress.StartBranch("pre_warming_cache");
        _progress.CompleteBranch("pre_warming_cache", "queued", "running after scrape completion");

        _ = Task.Run(() =>
        {
            try
            {
                _persistence.PreWarmRankingsCache(registeredIds);
                _log.LogInformation("Best-effort rankings cache warm completed for {UserCount:N0} registered user(s).", registeredIds.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Best-effort rankings cache warm failed. Cache entries will populate on demand.");
            }
        });
    }

    private static bool ShouldActivateShadowSnapshotsBeforeDerived(ScrapePassContext ctx, ScrapePhase resolvedPhases)
    {
        if (ctx.ScrapeId <= 0)
            return false;

        return resolvedPhases.HasFlag(ScrapePhase.SoloRankings)
            || resolvedPhases.HasFlag(ScrapePhase.SoloRivals)
            || resolvedPhases.HasFlag(ScrapePhase.SoloPlayerStats)
            || resolvedPhases.HasFlag(ScrapePhase.SoloPrecompute);
    }

    private static IReadOnlyList<(string SongId, string Instrument)> BuildExpectedSnapshotPairs(ScrapePassContext ctx)
    {
        var pairs = new HashSet<(string SongId, string Instrument)>();

        foreach (var request in ctx.ScrapeRequests)
        {
            if (string.IsNullOrWhiteSpace(request.SongId))
                continue;

            foreach (var instrument in request.Instruments)
            {
                if (string.IsNullOrWhiteSpace(instrument) || ScrapeOrchestrator.IsBandInstrument(instrument))
                    continue;

                pairs.Add((request.SongId, instrument));
            }
        }

        return pairs.ToArray();
    }

    /// <summary>
    /// Run a post-scrape phase with timing and heap telemetry.
    /// Logs phase name, duration, and heap delta so the peak memory owner is identifiable.
    /// </summary>
    private async Task RunPhaseAsync(string phaseName, Func<Task> phase, bool rethrowOnFailure = false)
    {
        var heapBefore = GC.GetTotalMemory(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await phase();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PostScrape phase [{Phase}] failed. Will retry next pass.", phaseName);
            if (rethrowOnFailure)
                throw;
        }
        sw.Stop();
        var heapAfter = GC.GetTotalMemory(false);
        _log.LogInformation(
            "PostScrape phase [{Phase}] completed in {Elapsed}. Heap: {Before:N0} → {After:N0} ({Delta:+#,0;-#,0;0} bytes).",
            phaseName, sw.Elapsed, heapBefore, heapAfter, heapAfter - heapBefore);
    }

    /// <summary>
    /// Run a post-scrape phase that returns a result, with timing and heap telemetry.
    /// </summary>
    private async Task<T> RunPhaseAsync<T>(string phaseName, Func<Task<T>> phase, T defaultValue = default!, bool rethrowOnFailure = false)
    {
        var heapBefore = GC.GetTotalMemory(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        T result = defaultValue;
        try
        {
            result = await phase();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PostScrape phase [{Phase}] failed. Will retry next pass.", phaseName);
            if (rethrowOnFailure)
                throw;
        }
        sw.Stop();
        var heapAfter = GC.GetTotalMemory(false);
        _log.LogInformation(
            "PostScrape phase [{Phase}] completed in {Elapsed}. Heap: {Before:N0} → {After:N0} ({Delta:+#,0;-#,0;0} bytes).",
            phaseName, sw.Elapsed, heapBefore, heapAfter, heapAfter - heapBefore);
        return result;
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

        var rankTask = Task.Run(() =>
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
            }
        }, ct);

        var firstSeenTask = Task.Run(async () =>
        {
            _progress.StartBranch("first_seen");
            try
            {
                var firstSeenToken = await _tokenManager.GetAccessTokenAsync(ct);
                if (firstSeenToken is not null)
                {
                    var firstSeenCount = await _firstSeenCalculator.CalculateAsync(
                        service, firstSeenToken, _tokenManager.AccountId!,
                        _pool, ct);
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
            }
        }, ct);

        var nameResTask = Task.Run(async () =>
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
            }
        }, ct);

        await rankTask;
        _progress.SetSubOperation("enriching_parallel_tail");
        await Task.WhenAll(firstSeenTask, nameResTask);
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
        try
        {
            _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.ComputingRankings);
            await _rankingsCalculator.ComputeAllAsync(service, ct, scrapeId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Rankings computation failed. Will retry next pass.");
            return false;
        }
    }

    /// <summary>
    /// Refresh stale/missing entries for registered users using the song processing machine.
    /// Also processes pending backfill and history recon users in the same run.
    /// All songs are processed in parallel, bounded by the shared DOP pool.
    /// </summary>
    internal async Task<SongProcessingMachine.MachineResult> RefreshRegisteredUsersAsync(ScrapePassContext ctx, CancellationToken ct)
    {
        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.SongMachine);

        try
        {
            var refreshToken = await _tokenManager.GetAccessTokenAsync(ct);
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
                    refreshToken, callerAccountId, ct);
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
                    _persistence.Meta.UpsertSeasonWindow(s, eventId: "", windowId: "");
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

            var chartedSongIds = ctx.ScrapeRequests.Select(r => r.SongId).ToList();
            var currentSeason = instrumentMaxSeason ?? 1;
            var allSeasons = seasonWindows.Select(w => w.SeasonNumber).ToHashSet();
            var canRunCompleteHistoryRecon = allSeasons.Count > 0;
            var pendingBackfills = _persistence.Meta.GetPendingBackfills();
            RegisterKnownBandsForAccounts(ctx.RegisteredIds.Concat(pendingBackfills.Select(static bf => bf.AccountId)));

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

            // Pending backfill users
            foreach (var bf in pendingBackfills)
            {
                var alreadyChecked = _persistence.Meta.GetCheckedBackfillPairs(bf.AccountId);
                users.Add(new UserWorkItem
                {
                    AccountId = bf.AccountId,
                    Purposes = canRunCompleteHistoryRecon
                        ? WorkPurpose.Backfill | WorkPurpose.HistoryRecon
                        : WorkPurpose.Backfill,
                    AllTimeNeeded = true,
                    SeasonsNeeded = canRunCompleteHistoryRecon ? new HashSet<int>(allSeasons) : [],
                    AlreadyChecked = alreadyChecked,
                });
            }

            // Pending history recon users
            foreach (var accountId in ctx.RegisteredIds)
            {
                var backfillStatus = _persistence.Meta.GetBackfillStatus(accountId);
                if (backfillStatus?.Status != "complete") continue;

                var reconStatus = _persistence.Meta.GetHistoryReconStatus(accountId);
                if (reconStatus?.Status == "complete") continue;

                if (!canRunCompleteHistoryRecon)
                {
                    _persistence.Meta.EnqueueHistoryRecon(accountId, chartedSongIds.Count);
                    continue;
                }

                if (pendingBackfills.Any(b => b.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var alreadyProcessed = _persistence.Meta.GetProcessedHistoryReconPairs(accountId);
                users.Add(new UserWorkItem
                {
                    AccountId = accountId,
                    Purposes = WorkPurpose.HistoryRecon,
                    AllTimeNeeded = false,
                    SeasonsNeeded = new HashSet<int>(allSeasons),
                    AlreadyChecked = alreadyProcessed,
                });
            }

            // ── Attach to the cyclical machine ──────────────────
            _progress.SetSubOperation("processing_songs");
            var result = await _cyclicalMachine.AttachAsync(
                users, chartedSongIds, seasonWindows,
                SongMachineSource.PostScrape,
                isHighPriority: true,
                ct: ct,
                preserveProgressPhaseOnIdle: true);

            if (result.EntriesUpdated > 0 || result.SessionsInserted > 0)
                _log.LogInformation("Song machine updated {Entries} entries, {Sessions} sessions for {Users} users.",
                    result.EntriesUpdated, result.SessionsInserted, result.UsersProcessed);

            // ── Handle per-user completion inline ────────────────
            _progress.SetSubOperation("completing_user_actions");
            foreach (var user in users.Where(u => u.Purposes.HasFlag(WorkPurpose.Backfill)))
            {
                try
                {
                    _persistence.Meta.CompleteBackfill(user.AccountId);
                    _rivalsOrchestrator.ComputeForUser(user.AccountId);
                    _ = _notifications.NotifyBackfillCompleteAsync(user.AccountId);

                    if (!user.Purposes.HasFlag(WorkPurpose.HistoryRecon))
                        EnsureHistoryReconPending(user.AccountId, chartedSongIds.Count);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Post-backfill actions failed for {AccountId}.", user.AccountId);
                }
            }

            foreach (var user in users.Where(u => u.Purposes.HasFlag(WorkPurpose.HistoryRecon)))
            {
                try
                {
                    var reconStatus = _persistence.Meta.GetHistoryReconStatus(user.AccountId);
                    if (reconStatus?.Status == "complete") continue;

                    if (reconStatus is null)
                        _persistence.Meta.EnqueueHistoryRecon(user.AccountId, 0);

                    _persistence.Meta.CompleteHistoryRecon(user.AccountId);
                    _ = _notifications.NotifyHistoryReconCompleteAsync(user.AccountId);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Post-history-recon actions failed for {AccountId}.", user.AccountId);
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Song processing machine failed. Will retry next pass.");
            return new SongProcessingMachine.MachineResult();
        }
    }

    private void RegisterKnownBandsForAccounts(IEnumerable<string> accountIds)
    {
        var registeredBands = 0;
        foreach (var accountId in accountIds.Distinct(StringComparer.OrdinalIgnoreCase))
            registeredBands += _persistence.Meta.RegisterKnownBandsForAccountActivity(accountId);

        if (registeredBands > 0)
            _log.LogDebug("Registered or refreshed {BandCount} known band(s) for tracked player history processing.", registeredBands);
    }

    private void EnsureHistoryReconPending(string accountId, int totalSongsToProcess)
    {
        var reconStatus = _persistence.Meta.GetHistoryReconStatus(accountId);
        if (reconStatus?.Status != "complete")
            _persistence.Meta.EnqueueHistoryRecon(accountId, totalSongsToProcess);
    }

    private async Task RunImprovementNotificationDetectionAsync(
        ScrapePassContext ctx,
        SongProcessingMachine.MachineResult registeredUserRefreshResult,
        CancellationToken ct)
    {
        var service = _improvementNotifications;
        if (service is null)
            return;

        var options = _improvementNotificationOptions.Value;
        if (options.IncludePlayers && options.IncludeSongEvents && options.RefreshSoloProjection)
        {
            if (_soloCurrentProjectionBuilder is null)
            {
                _log.LogWarning("Improvement notifications skipped because solo current projection builder is unavailable.");
                return;
            }

            var scopes = await BuildSoloProjectionScopesForNotificationsAsync(ctx, registeredUserRefreshResult, options, ct);
            if (scopes.Count > 0)
            {
                var refreshResult = await _soloCurrentProjectionBuilder.RefreshScopesAsync(
                    scopes,
                    new SoloCurrentProjectionRebuildOptions
                    {
                        CommandTimeoutSeconds = options.SoloProjectionCommandTimeoutSeconds,
                    },
                    ct);

                _log.LogInformation(
                    "Solo current projection refreshed for notifications: {Scopes:N0} scope(s), {Succeeded:N0} succeeded, {Failed:N0} failed, rows {Deleted:N0}->{Inserted:N0}, elapsed {ElapsedMs:N0}ms.",
                    refreshResult.ScopeCount,
                    refreshResult.SucceededScopeCount,
                    refreshResult.FailedScopeCount,
                    refreshResult.DeletedRows,
                    refreshResult.InsertedRows,
                    refreshResult.TotalElapsedMs);

                if (refreshResult.FailedScopeCount > 0)
                    throw new InvalidOperationException($"Solo current projection refresh failed for {refreshResult.FailedScopeCount} notification scope(s).");
            }
            else
            {
                _log.LogInformation("Solo current projection refresh for notifications skipped because no impacted scopes were found.");
            }
        }

        ct.ThrowIfCancellationRequested();
        var report = await Task.Run(() => service.Precompute(new ImprovementNotificationPrecomputeOptions(
            Scope: options.Scope,
            Execute: true,
            BaselineOnly: false,
            IncludePlayers: options.IncludePlayers,
            IncludeBands: options.IncludeBands,
            IncludeSongEvents: options.IncludeSongEvents,
            IncludeRankings: options.IncludeRankings,
            PruneExpired: options.PruneExpired,
            CommandTimeoutSeconds: options.CommandTimeoutSeconds,
            Source: "post-scrape")), ct);

        _log.LogInformation(
            "Improvement notification detection complete: run={RunId}, scope={Scope}, player events song={PlayerSongEvents:N0}/rank={PlayerRankEvents:N0}, band events song={BandSongEvents:N0}/rank={BandRankEvents:N0}, expired pruned player={ExpiredPlayer:N0}/band={ExpiredBand:N0}.",
            report.RunId,
            report.Scope,
            report.PlayerSongEventsInserted,
            report.PlayerRankEventsInserted,
            report.BandSongEventsInserted,
            report.BandRankEventsInserted,
            report.ExpiredPlayerEventsDeleted,
            report.ExpiredBandEventsDeleted);
    }

    private async Task<IReadOnlyCollection<SoloCurrentProjectionScopeKey>> BuildSoloProjectionScopesForNotificationsAsync(
        ScrapePassContext ctx,
        SongProcessingMachine.MachineResult registeredUserRefreshResult,
        ImprovementNotificationOptions options,
        CancellationToken ct)
    {
        var scopes = new HashSet<SoloCurrentProjectionScopeKey>();

        foreach (var request in ctx.ScrapeRequests)
        {
            if (string.IsNullOrWhiteSpace(request.SongId))
                continue;

            foreach (var instrument in request.Instruments)
            {
                if (string.IsNullOrWhiteSpace(instrument) || ScrapeOrchestrator.IsBandInstrument(instrument))
                    continue;

                scopes.Add(new SoloCurrentProjectionScopeKey(request.SongId, instrument));
            }
        }

        foreach (var entry in ctx.Aggregates.SeenRegisteredEntries)
        {
            if (!string.IsNullOrWhiteSpace(entry.SongId) && !string.IsNullOrWhiteSpace(entry.Instrument))
                scopes.Add(new SoloCurrentProjectionScopeKey(entry.SongId, entry.Instrument));
        }

        foreach (var scope in registeredUserRefreshResult.UpdatedScopes)
            scopes.Add(scope);

        if (scopes.Count == 0 && options.RefreshAllSoloScopesWhenNoImpactedScopes && _soloCurrentProjectionBuilder is not null)
            return await _soloCurrentProjectionBuilder.LoadCurrentScopesAsync(ct);

        return scopes.ToArray();
    }

    /// <summary>
    /// Compute rivals for registered users whose scores (or rivals' scores) changed.
    /// </summary>
    internal async Task ComputeRivalsAsync(ScrapePassContext ctx, CancellationToken ct)
    {
        if (ctx.RegisteredIds.Count == 0)
            return;

        try
        {
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Rivals computation failed. Will retry next pass.");
        }
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
        if (ctx.RegisteredIds.Count == 0)
            return;

        try
        {
            _log.LogInformation("Computing leaderboard rivals for {Count} registered user(s).", ctx.RegisteredIds.Count);

            var tasks = ctx.RegisteredIds.Select(accountId => Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var result = _leaderboardRivalsCalculator.ComputeForUser(accountId);
                    _log.LogDebug(
                        "Computed leaderboard rivals for {AccountId}: {Rivals} rival rows, {Samples} sample rows.",
                        accountId, result.RivalCount, result.SampleCount);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Leaderboard rivals computation failed for {AccountId}.", accountId);
                }
            }, ct)).ToList();

            await Task.WhenAll(tasks);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Leaderboard rivals computation failed. Will retry next pass.");
        }
    }

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
    internal Task ComputePlayerStatsTiersAsync(ScrapePassContext ctx, CancellationToken ct)
    {
        var changedIds = ctx.Aggregates.ChangedAccountIds;
        // Also include registered users (their stats should always be fresh)
        var accountIds = new HashSet<string>(changedIds, StringComparer.OrdinalIgnoreCase);
        foreach (var id in ctx.RegisteredIds)
            accountIds.Add(id);

        if (accountIds.Count == 0) return Task.CompletedTask;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogInformation("Computing player stats tiers for {Count:N0} accounts ({Changed:N0} changed + {Registered:N0} registered).",
            accountIds.Count, changedIds.Count, ctx.RegisteredIds.Count);

        var allMaxScores = _pathDataStore.GetAllMaxScores();
        var metaDb = _persistence.Meta;
        var instrumentKeys = _persistence.GetInstrumentKeys();
        int totalSongs = _persistence.GetTotalSongCount();
        var population = metaDb.GetAllLeaderboardPopulation();
        int computed = 0;

        foreach (var accountChunk in accountIds.Chunk(PlayerStatsTierAccountChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            Dictionary<string, List<PlayerScoreDto>> profilesByAccount;
            try
            {
                profilesByAccount = _persistence.GetCurrentStatePlayerProfiles(accountChunk);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Stats tier bulk score load failed for {Count:N0} account(s).", accountChunk.Length);
                continue;
            }

            var rows = new List<PlayerStatsTiersRow>();
            foreach (var accountId in accountChunk)
            {
                ct.ThrowIfCancellationRequested();
                if (!profilesByAccount.TryGetValue(accountId, out var allScores) || allScores.Count == 0)
                    continue;

                try
                {
                    var accountRows = BuildPlayerStatsTierRows(accountId, allScores, allMaxScores, instrumentKeys, totalSongs, population, metaDb);
                    if (accountRows.Count == 0)
                        continue;

                    rows.AddRange(accountRows);
                    computed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Stats tier computation failed for {AccountId}.", accountId);
                }
            }

            if (rows.Count > 0)
            {
                try
                {
                    metaDb.UpsertPlayerStatsTiersBatch(rows);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Stats tier batch write failed for {RowCount:N0} row(s).", rows.Count);
                }
            }
        }

        sw.Stop();
        _log.LogInformation("Computed player stats tiers for {Computed:N0}/{Total:N0} accounts in {Elapsed:F1}s.",
            computed, accountIds.Count, sw.Elapsed.TotalSeconds);
        return Task.CompletedTask;
    }

    private static List<PlayerStatsTiersRow> BuildPlayerStatsTierRows(
        string accountId,
        IReadOnlyList<PlayerScoreDto> allScores,
        Dictionary<string, SongMaxScores> allMaxScores,
        IReadOnlyList<string> instrumentKeys,
        int totalSongs,
        Dictionary<(string SongId, string Instrument), long> population,
        IMetaDatabase metaDb)
    {
        if (allScores.Count == 0)
            return [];

        Dictionary<(string SongId, string Instrument), List<ValidScoreFallback>>? fallbacks = null;
        var maxThresholds = PlayerStatsTierRowBuilder.BuildAboveMaxThresholds(allScores, allMaxScores);
        if (maxThresholds.Count > 0)
            fallbacks = metaDb.GetAllValidScoreTiers(accountId, maxThresholds);

        return PlayerStatsTierRowBuilder.BuildRows(
            accountId,
            allScores,
            instrumentKeys,
            totalSongs,
            allMaxScores,
            population,
            fallbacks);
    }
}
