using System.Diagnostics.CodeAnalysis;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FortniteFestival.Core.Persistence;
using FSTService.Api;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Persistence.Maintenance;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService;

internal sealed record ScrapeCatalogSelection(
    FestivalService Service,
    SongCatalogPersistenceToken Token,
    long PublicationId);

internal sealed class SongCatalogCaptureException : InvalidOperationException
{
    public SongCatalogSyncResult Capture { get; }

    public SongCatalogCaptureException(SongCatalogSyncResult capture)
        : base(
            "Exact provider song catalog refresh failed: "
            + (capture.FailureReason ?? "no exact persistence token"))
    {
        Capture = capture;
    }
}

internal sealed class PublicationCommitDeferredException
    : InvalidOperationException
{
    public PublicationCommitDeferredException(
        long scrapeId,
        Exception innerException)
        : base(
            $"Publication for scrape {scrapeId} remains ready but was deferred by contention.",
            innerException)
    {
        ScrapeId = scrapeId;
    }

    public long ScrapeId { get; }
}

internal sealed class PublicationCommitShutdownDeferredException
    : OperationCanceledException
{
    public PublicationCommitShutdownDeferredException(
        long scrapeId,
        OperationCanceledException innerException)
        : base(
            $"Publication for scrape {scrapeId} remains ready because worker shutdown interrupted contention retry.",
            innerException)
    {
        ScrapeId = scrapeId;
    }

    public long ScrapeId { get; }
}

internal sealed class PublicationCommitExecutionException
    : InvalidOperationException
{
    public PublicationCommitExecutionException(
        long scrapeId,
        Exception innerException,
        ScrapeLifecycleNotifier.PublicationCommitIntentLease
            commitIntent)
        : base(
            $"Publication for scrape {scrapeId} failed while its durable commit intent remained owned.",
            innerException)
    {
        ScrapeId = scrapeId;
        CommitIntent = commitIntent;
    }

    public long ScrapeId { get; }
    public ScrapeLifecycleNotifier.PublicationCommitIntentLease
        CommitIntent { get; }
}

internal sealed record DeferredPublicationResumeOutcome(
    bool Handled,
    bool Published,
    string? Detail);

internal sealed record PendingSnapshotGenerationRetentionSafePoint(
    long ScrapeId,
    long PublicationId);

/// <summary>
/// Background worker that continuously scrapes Fortnite Festival leaderboard scores.
///
/// Lifecycle:
///   1. Ensure authenticated (device auth → refresh → device code setup)
///   2. Initialize FestivalService (song catalog, images)
///   3. Scrape global leaderboards for all songs (V1 alltime)
///   4. Persist to sharded SQLite DBs, resolve names
///   5. Sleep for configured interval
///   6. Repeat
/// </summary>
public sealed class ScraperWorker : BackgroundService
{
    private readonly TokenManager _tokenManager;
    private readonly GlobalLeaderboardScraper _globalScraper;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly FestivalService _festivalService;
    private readonly StartupInitializer _dbInitializer;
    private readonly ScrapeOrchestrator _scrapeOrchestrator;
    private readonly PostScrapeOrchestrator _postScrapeOrchestrator;
    private readonly BackfillOrchestrator _backfillOrchestrator;
    private readonly CyclicalSongMachine _cyclicalMachine;
    private readonly PathGenerationCoordinator _pathGeneration;
    private readonly IPathDataStore _pathDataStore;
    private readonly SongsCacheService _songsCache;
    private readonly ResponseCacheService _playerCache;
    private readonly ResponseCacheService _leaderboardAllCache;
    private readonly ScrapeLifecycleNotifier _lifecycle;
    private readonly ScrapeTimePrecomputer _precomputer;
    private readonly ScrapeProgressTracker _progress;
    private readonly BackgroundWorkCoordinator _backgroundWork;
    private readonly UserSyncProgressTracker _syncTracker;
    private readonly NotificationService _notifications;
    private readonly DeferredRetentionMaintenanceRunner? _deferredRetentionMaintenance;
    private readonly ISnapshotGenerationRetentionPlanner?
        _snapshotGenerationRetentionPlanner;
    private readonly WorkerStatusPublisher? _workerStatus;
    private readonly RegistrationMutationCoordinator
        _registrationMutations;
    private readonly IOptions<ScraperOptions> _options;
    private readonly SnapshotGenerationRetentionSafePointQueue
        _retentionSafePoints = new();
    private readonly PublicationCommitOptions
        _publicationCommitOptions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ScraperWorker> _log;
    private readonly System.Text.Json.JsonSerializerOptions _jsonOpts;
    private DateTime _serviceStartedAtUtc = DateTime.UtcNow;
    private long? _lastCompletedScoresChangedBroadcastScrapeId;
    internal Func<long?, Task>?
        ScoresChangedNotificationTestHook { get; set; }

    private static readonly TimeSpan WebRegistrationStartupProtection = TimeSpan.FromHours(4);
    private static readonly TimeSpan BestEffortCleanupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeferredPublicationRetryDelay =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetentionSafePointRetryDelay =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan
        DefaultRetentionRegistrationDrainWaitBudget =
            TimeSpan.FromSeconds(30);
    private static readonly TimeSpan
        RetentionRegistrationDrainInitialPollDelay =
            TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan
        RetentionRegistrationDrainMaximumPollDelay =
            TimeSpan.FromSeconds(2);

    internal int PendingRetentionSafePointCount =>
        _retentionSafePoints.Count;
    internal IReadOnlyList<
        PendingSnapshotGenerationRetentionSafePoint>
        PendingRetentionSafePoints =>
        _retentionSafePoints.Snapshot();
    internal TimeSpan RetentionRegistrationDrainWaitBudget
        { get; set; } =
            DefaultRetentionRegistrationDrainWaitBudget;

    public ScraperWorker(
        TokenManager tokenManager,
        GlobalLeaderboardScraper globalScraper,
        GlobalLeaderboardPersistence persistence,
        FestivalService festivalService,
        StartupInitializer dbInitializer,
        ScrapeOrchestrator scrapeOrchestrator,
        PostScrapeOrchestrator postScrapeOrchestrator,
        BackfillOrchestrator backfillOrchestrator,
        CyclicalSongMachine cyclicalMachine,
        PathGenerationCoordinator pathGeneration,
        IPathDataStore IPathDataStore,
        SongsCacheService songsCache,
        [FromKeyedServices("PlayerCache")] ResponseCacheService playerCache,
        [FromKeyedServices("LeaderboardAllCache")] ResponseCacheService leaderboardAllCache,
        ScrapeLifecycleNotifier lifecycle,
        ScrapeTimePrecomputer precomputer,
        ScrapeProgressTracker progress,
        BackgroundWorkCoordinator backgroundWork,
        UserSyncProgressTracker syncTracker,
        NotificationService notifications,
        IOptions<ScraperOptions> options,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
        IHostApplicationLifetime lifetime,
        ILogger<ScraperWorker> log,
        RegistrationMutationCoordinator
            registrationMutations,
        DeferredRetentionMaintenanceRunner? deferredRetentionMaintenance = null,
        ISnapshotGenerationRetentionPlanner?
            snapshotGenerationRetentionPlanner = null,
        WorkerStatusPublisher? workerStatus = null,
        IOptions<PublicationCommitOptions>?
            publicationCommitOptions = null)
    {
        _tokenManager = tokenManager;
        _globalScraper = globalScraper;
        _persistence = persistence;
        _festivalService = festivalService;
        _dbInitializer = dbInitializer;
        _scrapeOrchestrator = scrapeOrchestrator;
        _postScrapeOrchestrator = postScrapeOrchestrator;
        _backfillOrchestrator = backfillOrchestrator;
        _cyclicalMachine = cyclicalMachine;
        _pathGeneration = pathGeneration;
        _pathDataStore = IPathDataStore;
        _songsCache = songsCache;
        _playerCache = playerCache;
        _leaderboardAllCache = leaderboardAllCache;
        _lifecycle = lifecycle;
        _precomputer = precomputer;
        _progress = progress;
        _backgroundWork = backgroundWork;
        _syncTracker = syncTracker;
        _notifications = notifications;
        _deferredRetentionMaintenance = deferredRetentionMaintenance;
        _snapshotGenerationRetentionPlanner =
            snapshotGenerationRetentionPlanner;
        _workerStatus = workerStatus;
        _registrationMutations = registrationMutations;
        _options = options;
        _publicationCommitOptions =
            publicationCommitOptions?.Value
            ?? new PublicationCommitOptions();
        _jsonOpts = jsonOptions.Value.SerializerOptions;
        _lifetime = lifetime;
        _log = log;
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "ScraperWorker failed with an unhandled exception.");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            await CleanupActiveScrapeResourcesAsync("scheduled shutdown", cancellationToken);
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;

        // Wait for DatabaseInitializer to finish (DBs + song catalog)
        await _dbInitializer.WaitForReadyAsync(stoppingToken);
        _log.LogInformation("Song catalog loaded. {SongCount} songs available for API.",
            _festivalService.Songs.Count);

        // Start the cyclical song machine so callers (post-scrape, backfill,
        // track endpoint) can attach at any time.
        _cyclicalMachine.Start(stoppingToken);

        // Precomputed API responses are now served from PostgreSQL.
        // No disk load needed — data persists across restarts in the api_response_cache table.
        {
            if (_precomputer.Count == 0)
                _log.LogInformation("No precomputed responses in RAM buffer (served from PostgreSQL).");
            PrimeSongsCache(); // Rebuild with population tiers
        }

        await ResumeDeferredPublicationBeforeGatesAsync(
            stoppingToken);

        // --api-only mode: skip scrape work. Song catalog freshness is owned by
        // SongCatalogRefreshWorker, which is registered directly by Program.cs.
        if (opts.ApiOnly)
        {
            _log.LogInformation("Running in --api-only mode. Scrape pipeline disabled.");
            if (_retentionSafePoints.Count > 0)
            {
                await EnsureImprovementNotificationsCompleteBeforeNextScrapeAsync(
                    stoppingToken);
                await DrainSnapshotGenerationRetentionSafePointQueueAsync(
                    stoppingToken);
            }
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { /* normal shutdown */ }
            return;
        }

        // --setup mode: only do device code auth, then exit
        if (opts.SetupOnly)
        {
            _log.LogInformation("Running in --setup mode (device code authentication only).");
            var ok = await _tokenManager.PerformDeviceCodeSetupAsync(stoppingToken);
            _log.LogInformation(ok ? "Setup complete! You can now run the service normally."
                                   : "Setup failed. Please try again.");
            return;
        }

        _log.LogInformation("ScraperWorker starting. Interval={Interval}, DOP={Dop}",
            opts.ScrapeInterval, opts.DegreeOfParallelism);
        _workerStatus?.PublishHeartbeat("running", "Worker ready");

        await _postScrapeOrchestrator.EnsureBandContextReadyBeforeScrapeAsync(
            stoppingToken);
        await EnsureImprovementNotificationsCompleteBeforeNextScrapeAsync(stoppingToken);

        // Ensure we have a valid auth session before entering the loop
        _workerStatus?.BeginOperation("worker.authentication", "Checking Epic authentication", phase: "Starting");
        if (!await EnsureAuthenticatedAsync(stoppingToken))
        {
            _workerStatus?.FailOperation("worker.authentication", detail: "Epic authentication unavailable");
            return;
        }
        _workerStatus?.CompleteOperation("worker.authentication");

        // --test mode: fetch one song and exit
        if (!string.IsNullOrEmpty(opts.TestSongQuery))
        {
            await RunSingleSongTestAsync(_festivalService, opts, stoppingToken);
            return;
        }

        // --resolve-only mode: skip scraping, just resolve unresolved account names
        if (opts.ResolveOnly)
        {
            await RunResolveOnlyAsync(stoppingToken);
            return;
        }

        // --backfill-only mode: skip scraping, just run backfill enrichment for registered users
        if (opts.BackfillOnly)
        {
            _log.LogInformation("Running in --backfill-only mode. Enriching existing entries with rank/percentile.");

            // ── DIAGNOSTIC: V2 lookup for #1 player to check if percentile is returned ──
            var diagToken = await _tokenManager.GetAccessTokenAsync(stoppingToken);
            var diagCaller = _tokenManager.AccountId!;
            if (diagToken is not null)
            {
                try
                {
                    // #1 Guitar player for song 092c2537 (popular song)
                    var diagEntry = await _globalScraper.LookupAccountAsync(
                        "092c2537-54ed-4963-9f91-873219ad5e74", "Solo_Guitar",
                        "e408c4613c8f4da5907090b390bda80c", diagToken, diagCaller, ct: stoppingToken);
                    if (diagEntry is not null)
                        _log.LogWarning("DIAG: #1 player V2 → Rank={Rank}, Percentile={Percentile}, Score={Score}",
                            diagEntry.Rank, diagEntry.Percentile, diagEntry.Score);
                    else
                        _log.LogWarning("DIAG: #1 player V2 → null (no entry)");
                }
                catch (Exception ex) { _log.LogWarning(ex, "DIAG: V2 lookup failed"); }
            }

            await _backfillOrchestrator.RunBackfillAsync(_festivalService, stoppingToken);
            _log.LogInformation("Backfill enrichment complete.");
            return;
        }

        EnqueueCurrentPublicationForRetentionObservation();
        if (opts.RunOnce
            && _retentionSafePoints.Count > 0
            && !await DrainRegistrationWorkAfterRunOnceAsync(
                opts,
                stoppingToken))
        {
            _log.LogWarning(
                "Startup retention safe-point observations remain queued because registration drain is incomplete.");
            return;
        }

        // Register next-rank-update provider so sync completion messages include
        // an estimated time for global rankings recalculation.
        DateTime? lastScrapeEndUtc = null;
        _syncTracker.SetNextRankUpdateProvider(() =>
        {
            if (lastScrapeEndUtc is null) return null;
            return lastScrapeEndUtc.Value + opts.ScrapeInterval;
        });

        // Main scrape loop
        while (!stoppingToken.IsCancellationRequested)
        {
            await DrainSnapshotGenerationRetentionSafePointQueueAsync(
                stoppingToken);

            var publicationBeforePass =
                _persistence.Meta.GetPublicationPointerState();
            await RunScrapePassAsync(_festivalService, opts, stoppingToken);
            await ResumeDeferredPublicationBeforeGatesAsync(
                stoppingToken);
            await EnsureImprovementNotificationsCompleteBeforeNextScrapeAsync(stoppingToken);
            lastScrapeEndUtc = DateTime.UtcNow;
            var publicationAfterPass =
                _persistence.Meta.GetPublicationPointerState();
            var publishedNewState =
                publicationAfterPass.PublishedScrapeId.HasValue
                && publicationAfterPass.CurrentPublicationId.HasValue
                && (
                    publicationAfterPass.PublishedScrapeId !=
                        publicationBeforePass.PublishedScrapeId
                    || publicationAfterPass.CurrentPublicationId !=
                        publicationBeforePass.CurrentPublicationId);
            if (publishedNewState
                && _snapshotGenerationRetentionPlanner?.IsEnabled ==
                    true)
            {
                EnqueueSnapshotGenerationRetentionSafePoint(
                    new PendingSnapshotGenerationRetentionSafePoint(
                        publicationAfterPass
                            .PublishedScrapeId!.Value,
                        publicationAfterPass
                            .CurrentPublicationId!.Value));
            }

            // Phase-selective flags only affect the first (launch) pass.
            // After it completes, revert to the full pipeline for subsequent cycles.
            if (opts.EnabledPhases != ScrapePhase.None)
            {
                _log.LogInformation("Launch phases complete ({Phases}). Reverting to full pipeline for subsequent cycles.",
                    ScrapePhaseResolver.Format(opts.ResolvedPhases));
                opts.EnabledPhases = ScrapePhase.None;
            }

            if (opts.RunOnce)
            {
                var registrationDrainComplete =
                    !publishedNewState;
                if (publishedNewState)
                {
                    registrationDrainComplete =
                        await DrainRegistrationWorkAfterRunOnceAsync(
                            opts,
                            stoppingToken);
                }
                else
                {
                    _log.LogWarning(
                        "Skipping post-run registration drain because the run-once pass did not publish a new scrape.");
                }
                if (registrationDrainComplete)
                {
                    await DrainSnapshotGenerationRetentionSafePointQueueAsync(
                        stoppingToken);
                }
                _log.LogInformation("--once: scrape + resolve pass complete. Exiting.");
                break;
            }

            _log.LogInformation("Next scrape in {Interval}. Sleeping...", opts.ScrapeInterval);
            try
            {
                await Task.Delay(opts.ScrapeInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log.LogInformation("ScraperWorker stopping.");
        _workerStatus?.MarkOffline("ScraperWorker stopping");

    }

    private async Task<bool> DrainRegistrationWorkAfterRunOnceAsync(
        ScraperOptions opts,
        CancellationToken ct)
    {
        _workerStatus?.BeginOperation(
            "registration.post_run_once",
            "Draining queued registration work after publication",
            phase: "PostPublication");
        try
        {
            var claimed = await RegistrationBackfillWorker
                .RunAvailableRegistrationWorkAsync(
                    opts.RegistrationBackfillBatchSize,
                    (batchSize, token) =>
                        _backfillOrchestrator
                            .RunQueuedRegistrationBackfillBatchAsync(
                                _festivalService,
                                batchSize,
                                token),
                    token => _backfillOrchestrator.RunHistoryReconAsync(
                        _festivalService,
                        token),
                    () => _persistence.Meta.GetPendingBackfills().Count > 0
                          || _persistence.Meta.GetDeferredBackfills().Count > 0,
                    claimedInBatch => _log.LogInformation(
                        "Claimed {Count} post-publication registration backfill account(s).",
                        claimedInBatch),
                    ct);

            var registrationDrain =
                await GetRegistrationDrainWaitSnapshotAsync(
                    ct);
            var remainingBackfills =
                registrationDrain.RunnableBackfills;
            var remainingHistory =
                registrationDrain.RepairableHistory;
            if (registrationDrain.HasTerminalBlocker)
            {
                _workerStatus?.CompleteOperation(
                    "registration.post_run_once",
                    detail:
                        "terminal registration state will be persisted by the retention observer");
                _log.LogWarning(
                    "Post-publication registration drain found terminal non-runnable state; the report-only retention observer will persist a blocked cycle.");
                return true;
            }
            if (remainingBackfills > 0 || remainingHistory > 0)
            {
                var detail =
                    $"deferred with {remainingBackfills} backfill and " +
                    $"{remainingHistory} history item(s) remaining";
                _workerStatus?.CompleteOperation(
                    "registration.post_run_once",
                    "deferred",
                    detail);
                _log.LogWarning(
                    "Post-publication registration drain remains incomplete: {Backfills} backfill and {History} history item(s).",
                    remainingBackfills,
                    remainingHistory);
                return false;
            }

            _workerStatus?.CompleteOperation(
                "registration.post_run_once",
                detail: claimed > 0
                    ? $"claimed {claimed} backfill account(s)"
                    : "no queued backfill accounts");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _workerStatus?.CompleteOperation(
                "registration.post_run_once",
                "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _workerStatus?.FailOperation(
                "registration.post_run_once",
                ex);
            _log.LogError(
                ex,
                "Post-publication registration work failed; durable queues remain for the next worker.");
            return false;
        }
    }

    private async Task<SnapshotGenerationRetentionPlanResult?>
        TryRunSnapshotGenerationRetentionSafePointAsync(
            PendingSnapshotGenerationRetentionSafePoint safePoint,
            CancellationToken ct)
    {
        var planner = _snapshotGenerationRetentionPlanner;
        if (planner?.IsEnabled != true)
            return null;

        _workerStatus?.BeginOperation(
            "retention.snapshot_generation_report",
            "Observing snapshot-generation retention safety",
            phase: "PostPublication");
        _backgroundWork.RequestPauseForScrape();
        try
        {
            await _backgroundWork
                .WaitForBackgroundQuiescenceAsync(ct);
            var request =
                new SnapshotGenerationRetentionPlanRequest(
                    safePoint.ScrapeId,
                    safePoint.PublicationId,
                    DateTime.UtcNow,
                    _lastCompletedScoresChangedBroadcastScrapeId,
                    BackgroundWorkQuiesced: true);
            var result = await planner.PlanAsync(
                request,
                ct);
            if (result.Disposition ==
                    SnapshotGenerationRetentionPlanDisposition
                        .Deferred
                && result.Reason.StartsWith(
                    "scores_changed_broadcast_incomplete:",
                    StringComparison.Ordinal)
                && _lastCompletedScoresChangedBroadcastScrapeId !=
                    safePoint.ScrapeId)
            {
                await NotifyScoresChangedAfterPublicationAsync(
                    safePoint.ScrapeId);
                result = await planner.PlanAsync(
                    request with
                    {
                        SafePointAtUtc = DateTime.UtcNow,
                        BroadcastCompletedScrapeId =
                            safePoint.ScrapeId,
                    },
                    ct);
            }
            _workerStatus?.CompleteOperation(
                "retention.snapshot_generation_report",
                result.Disposition.ToString().ToLowerInvariant(),
                result.Reason);
            return result;
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            _workerStatus?.CompleteOperation(
                "retention.snapshot_generation_report",
                "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _workerStatus?.FailOperation(
                "retention.snapshot_generation_report",
                ex);
            _log.LogError(
                ex,
                "Snapshot-generation retention safe-point observation failed after publication {PublicationId}; the accepted publication remains current.",
                safePoint.PublicationId);
            return new SnapshotGenerationRetentionPlanResult(
                SnapshotGenerationRetentionPlanDisposition.Failed,
                null,
                "retention safe-point invocation failed",
                null,
                null,
                0,
                0,
                0,
                0,
                OracleAgreement: false,
                Retryable: true);
        }
        finally
        {
            _backgroundWork.ResumeAfterScrape();
        }
    }

    private void EnqueueCurrentPublicationForRetentionObservation()
    {
        if (_snapshotGenerationRetentionPlanner?.IsEnabled !=
            true)
        {
            return;
        }

        var publication =
            _persistence.Meta.GetPublicationPointerState();
        if (publication.PublishedScrapeId.HasValue
            && publication.CurrentPublicationId.HasValue)
        {
            EnqueueSnapshotGenerationRetentionSafePoint(
                new PendingSnapshotGenerationRetentionSafePoint(
                    publication.PublishedScrapeId.Value,
                    publication.CurrentPublicationId.Value));
        }
    }

    private void EnqueueSnapshotGenerationRetentionSafePoint(
        PendingSnapshotGenerationRetentionSafePoint safePoint)
    {
        if (_snapshotGenerationRetentionPlanner?.IsEnabled !=
            true)
        {
            return;
        }

        if (_retentionSafePoints.Enqueue(safePoint))
        {
            _log.LogInformation(
                "Queued snapshot-generation retention safe point for scrape {ScrapeId}, publication {PublicationId}; pending={PendingCount}.",
                safePoint.ScrapeId,
                safePoint.PublicationId,
                _retentionSafePoints.Count);
        }
    }

    private async Task<bool>
        TryProcessNextSnapshotGenerationRetentionSafePointAsync(
            CancellationToken ct)
    {
        if (!_retentionSafePoints.TryPeek(
                out var safePoint)
            || safePoint is null)
        {
            return true;
        }

        var result =
            await TryRunSnapshotGenerationRetentionSafePointAsync(
                safePoint,
                ct);
        var terminalPersisted =
            result?.CycleId.HasValue == true
            && result.Retryable == false
            && result.Disposition is
                SnapshotGenerationRetentionPlanDisposition
                    .Existing
                or SnapshotGenerationRetentionPlanDisposition
                    .Observed
                or SnapshotGenerationRetentionPlanDisposition
                    .Blocked
                or SnapshotGenerationRetentionPlanDisposition
                    .OracleMismatch
                or SnapshotGenerationRetentionPlanDisposition
                    .Failed;
        if (!terminalPersisted)
        {
            _log.LogWarning(
                "Retaining snapshot-generation safe point for scrape {ScrapeId}, publication {PublicationId}; no terminal persisted cycle was returned.",
                safePoint.ScrapeId,
                safePoint.PublicationId);
            return false;
        }

        _retentionSafePoints.CompleteTerminal(safePoint);
        return true;
    }

    private async Task
        DrainSnapshotGenerationRetentionSafePointQueueAsync(
            CancellationToken ct)
    {
        while (_retentionSafePoints.Count > 0)
        {
            bool registrationDrained;
            try
            {
                registrationDrained =
                    await WaitForRegistrationDrainBeforeRetentionAsync(
                        ct);
            }
            catch (OperationCanceledException)
                when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "Snapshot-generation safe-point registration-drain observation failed; queued work is retained and no background pause was requested.");
                return;
            }

            if (!registrationDrained)
            {
                _log.LogWarning(
                    "Retaining {PendingCount} snapshot-generation safe point(s) after the bounded registration-drain wait; background registration remains runnable and the next scrape may proceed.",
                    _retentionSafePoints.Count);
                return;
            }

            if (await TryProcessNextSnapshotGenerationRetentionSafePointAsync(
                    ct))
            {
                continue;
            }

            await Task.Delay(
                RetentionSafePointRetryDelay,
                ct);
        }
    }

    private async Task<bool>
        WaitForRegistrationDrainBeforeRetentionAsync(
            CancellationToken ct)
    {
        var startedAt =
            System.Diagnostics.Stopwatch.GetTimestamp();
        var delay =
            RetentionRegistrationDrainInitialPollDelay;
        RegistrationDrainWaitSnapshot? lastLogged = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot =
                await GetRegistrationDrainWaitSnapshotAsync(
                    ct);
            if (snapshot.HasTerminalBlocker
                || snapshot.RunnableBackfills == 0
                && snapshot.RepairableHistory == 0)
            {
                return true;
            }

            if (snapshot != lastLogged)
            {
                _log.LogInformation(
                    "Waiting without pausing background work for registration drain before snapshot-generation retention observation: backfills={Backfills}, history={History}.",
                    snapshot.RunnableBackfills,
                    snapshot.RepairableHistory);
                lastLogged = snapshot;
            }
            _backgroundWork.RequestRegistrationDrain();

            var elapsed =
                System.Diagnostics.Stopwatch.GetElapsedTime(
                    startedAt);
            var remaining =
                RetentionRegistrationDrainWaitBudget
                - elapsed;
            if (remaining <= TimeSpan.Zero)
                return false;

            await Task.Delay(
                delay < remaining ? delay : remaining,
                ct);
            var nextDelayTicks = Math.Min(
                delay.Ticks * 2,
                RetentionRegistrationDrainMaximumPollDelay
                    .Ticks);
            delay = TimeSpan.FromTicks(
                nextDelayTicks);
        }
    }

    private async Task<RegistrationDrainWaitSnapshot>
        GetRegistrationDrainWaitSnapshotAsync(
            CancellationToken ct)
    {
        var state =
            await _persistence.Meta
                .GetRegistrationDrainStatusAsync(
                    ct: ct);
        return new RegistrationDrainWaitSnapshot(
            state.RunnableBackfills,
            state.RepairableHistory,
            state.HasTerminalBlocker);
    }

    private sealed record RegistrationDrainWaitSnapshot(
        int RunnableBackfills,
        int RepairableHistory,
        bool HasTerminalBlocker);

    private async Task EnsureImprovementNotificationsCompleteBeforeNextScrapeAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var freezeState =
                    _persistence.Meta.GetPublicReadFreezeState();
                if (freezeState.PublicationCommitDeferred)
                {
                    _log.LogWarning(
                        "Notification recovery gate yielded to deferred publication recovery for scrape {ScrapeId}.",
                        freezeState.ScrapeId);
                    return;
                }
                if (freezeState.PublicationFailureIsolationPending)
                {
                    _log.LogWarning(
                        "Notification recovery gate is waiting for pending publication isolation on scrape {ScrapeId}.",
                        freezeState.ScrapeId);
                    _ = _persistence.Meta
                        .ReconcileStalePublicationCommitIntent(
                            TimeSpan.FromSeconds(
                                Math.Max(
                                    1,
                                    _publicationCommitOptions
                                        .StaleCommitIntentSeconds)));
                    await Task.Delay(
                        DeferredPublicationRetryDelay,
                        stoppingToken);
                    continue;
                }

                _workerStatus?.BeginOperation(
                    "notifications.recovery",
                    "Recovering pending improvement notifications before the next scrape",
                    phase: "PreScrapeGate");
                await _postScrapeOrchestrator.RecoverPendingImprovementNotificationsOnStartupAsync(
                    stoppingToken);
                _workerStatus?.CompleteOperation("notifications.recovery");
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _workerStatus?.CompleteOperation("notifications.recovery", "deferred");
                throw;
            }
            catch (Exception ex)
            {
                _workerStatus?.FailOperation("notifications.recovery", ex);
                _log.LogError(
                    ex,
                    "Pending improvement notification recovery failed. The next scrape is held and recovery will retry in {RetryDelay}.",
                    TimeSpan.FromSeconds(
                        Math.Max(
                            1,
                            _publicationCommitOptions
                                .NotificationRecoveryRetrySeconds)));
                await Task.Delay(
                    TimeSpan.FromSeconds(
                        Math.Max(
                            1,
                            _publicationCommitOptions
                                .NotificationRecoveryRetrySeconds)),
                    stoppingToken);
            }
        }
    }

    internal static void ValidateResumeScrape(
        ScraperOptions options,
        ScrapePhase resolvedPhases,
        ScrapeResumeState? state)
    {
        var requiredPhases =
            ScrapePhase.SoloRankings
            | ScrapePhase.SoloRivals
            | ScrapePhase.SoloPlayerStats
            | ScrapePhase.SoloPrecompute
            | ScrapePhase.SoloFinalize;

        if (!options.RunOnce)
            throw new InvalidOperationException("Scrape recovery requires run-once mode.");
        if (resolvedPhases != requiredPhases)
        {
            throw new InvalidOperationException(
                $"Scrape recovery requires exactly the solo leaderboard phases; resolved {ScrapePhaseResolver.Format(resolvedPhases)}.");
        }
        if (state is null || state.ScrapeId != options.ResumeScrapeId)
            throw new InvalidOperationException($"Scrape {options.ResumeScrapeId} does not exist.");
        if (!state.CanResume)
        {
            throw new InvalidOperationException(
                $"Scrape {state.ScrapeId} cannot resume: status={state.Status}, " +
                $"manifests={state.CompleteManifestCount}/{state.ManifestCount}, " +
                $"writerFailures={state.WriterFailureCount}, criticalFailures={state.CriticalPhaseFailureCount}, " +
                $"published={state.PublishedScrapeId?.ToString() ?? "none"}.");
        }
        if (options.ResumeSongsScraped <= 0
            || options.ResumeTotalEntries <= 0
            || options.ResumeTotalRequests <= 0
            || options.ResumeTotalBytes <= 0)
        {
            throw new InvalidOperationException("Scrape recovery requires persisted positive scrape metrics.");
        }
    }

    internal ScrapeCatalogSelection LoadResumeSongCatalog(
        long scrapeId)
    {
        var catalog = _persistence.Meta
            .GetPublicationSongCatalogForScrape(scrapeId)
            ?? throw new InvalidOperationException(
                $"Scrape {scrapeId} has no exact ready publication song catalog.");
        var songs = SongCatalogSnapshotBuilder.DeserializeCatalog(
            catalog.CatalogJson);
        var token = new SongCatalogPersistenceToken(
            catalog.CatalogVersion,
            catalog.SchemaVersion,
            catalog.ContentHash,
            catalog.SongCount);
        SongCatalogSnapshotBuilder.ValidateToken(
            SongCatalogSnapshotBuilder.Create(songs),
            token);
        return new ScrapeCatalogSelection(
            FestivalService.CreateFromSongCatalogSnapshot(songs),
            token,
            catalog.PublicationId);
    }

    internal async Task<ScrapeCatalogSelection>
        ResolveSongCatalogForPassAsync(
            FestivalService service,
            ScrapeResumeState? resumeState)
    {
        if (resumeState is not null)
            return LoadResumeSongCatalog(resumeState.ScrapeId);

        var capture = await service.SyncSongsWithResultAsync();
        if (!capture.IsExact || capture.PersistenceToken is null)
            throw new SongCatalogCaptureException(capture);

        return new ScrapeCatalogSelection(
            service,
            capture.PersistenceToken,
            PublicationId: 0);
    }

    internal static IReadOnlyList<
        GlobalLeaderboardScraper.SongScrapeRequest>
        BuildCatalogScrapeRequests(
            FestivalService service,
            ScraperOptions options)
    {
        var instruments = ScrapeOrchestrator.GetEnabledInstruments(
            options);
        return service.Songs
            .Where(static song => song.track?.su is not null)
            .Select(song =>
                new GlobalLeaderboardScraper.SongScrapeRequest
                {
                    SongId = song.track.su,
                    Instruments = instruments,
                    Label = song.track.tt,
                })
            .ToList();
    }

    // ─── Auth helpers ───────────────────────────────────────────

    private async Task<bool> EnsureAuthenticatedAsync(CancellationToken ct)
    {
        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is not null)
            return true;

        _log.LogWarning("No stored credentials. Running interactive device code setup...");
        var ok = await _tokenManager.PerformDeviceCodeSetupAsync(ct);
        if (!ok)
        {
            _log.LogError("Device code setup failed. Cannot start scraping. Exiting.");
            return false;
        }
        return true;
    }

    // ─── Resolve-only mode ──────────────────────────────────────

    /// <summary>
    /// Skip scraping entirely.  Resolve display names for any account IDs
    /// already stored in the meta DB with LastResolved = NULL, then exit.
    /// </summary>
    private async Task RunResolveOnlyAsync(CancellationToken ct)
    {
        var unresolvedCount = _persistence.Meta.GetUnresolvedAccountCount();
        _log.LogInformation("--resolve-only: {Count} unresolved account(s) in meta DB.", unresolvedCount);

        if (unresolvedCount == 0)
        {
            _log.LogInformation("Nothing to resolve. Exiting.");
            return;
        }

        _progress.SetPhase(ScrapeProgressTracker.ScrapePhase.ResolvingNames);
        try
        {
            var resolved = await _postScrapeOrchestrator.ResolveNamesAsync(maxConcurrency: 8, ct);
            _log.LogInformation("--resolve-only complete. {Resolved} account(s) resolved.", resolved);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Account name resolution failed.");
        }
    }

    private static IReadOnlyList<string> GetEnabledInstruments(ScraperOptions opts)
        => ScrapeOrchestrator.GetEnabledInstruments(opts);

    internal static bool IsSuccessfulPhaseOutcomeStatus(
        string status,
        PostScrapePhaseCriticality criticality) =>
        PostScrapePhasePolicy.IsSuccessfulStatus(status, criticality);

    internal static PostScrapePhaseOutcome RehydratePhaseOutcome(
        ScrapePhaseOutcomeRecord outcome)
    {
        var criticality = outcome.Criticality switch
        {
            "publication_critical" =>
                PostScrapePhaseCriticality.PublicationCritical,
            "best_effort" =>
                PostScrapePhaseCriticality.BestEffort,
            _ => throw new InvalidOperationException(
                $"Persisted post-scrape phase '{outcome.Phase}' has unknown " +
                $"criticality '{outcome.Criticality}'."),
        };
        return new PostScrapePhaseOutcome(
            outcome.Phase,
            criticality,
            IsSuccessfulPhaseOutcomeStatus(
                outcome.Status,
                criticality),
            outcome.ErrorMessage)
        {
            Status = outcome.Status,
        };
    }

    // ─── Scrape pass (V1 alltime global) ────────────────────────

    /// <summary>
    /// Scrape all songs via V1 alltime global leaderboards.
    /// Delegates core scraping to <see cref="ScrapeOrchestrator"/>, then
    /// runs post-scrape enrichment and backfill via downstream orchestrators.
    /// </summary>
    private async Task RunScrapePassAsync(
        FestivalService service,
        ScraperOptions opts,
        CancellationToken ct)
    {
        var processMemMb = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
        _log.LogInformation("Starting scrape pass... (Process memory: {MemoryMB} MB)", processMemMb);
        _workerStatus?.BeginOperation("scrape.pass", "Running leaderboard update", phase: "Scraping", subOperation: "scrape_pass");
        var passStatus = "failed";
        string? passDetail = "Scrape pass exited before a publication decision.";
        var publicReadsFrozen = false;
        var publicReadsReleased = false;
        var durableFailureIsolationConfirmed = true;
        _backgroundWork.RequestPauseForScrape();
        try
        {
            await _backgroundWork.WaitForBackgroundQuiescenceAsync(ct);

            var deferredResume =
                await TryResumeDeferredPublicationAsync(ct);
            if (deferredResume.Handled)
            {
                passStatus = deferredResume.Published
                    ? "completed"
                    : "failed";
                passDetail = deferredResume.Detail;
                publicReadsFrozen = true;
                publicReadsReleased = true;
                return;
            }

            var resolvedPhases = opts.ResolvedPhases;
            if (resolvedPhases != ScrapePhase.All)
                _log.LogInformation("Phase-selective mode: {Phases}", ScrapePhaseResolver.Format(resolvedPhases));

            var resumeState = opts.ResumeScrapeId > 0
                ? _persistence.Meta.GetScrapeResumeState(opts.ResumeScrapeId)
                : null;
            if (opts.ResumeScrapeId > 0)
                ValidateResumeScrape(opts, resolvedPhases, resumeState);
            if (resumeState is not null)
                _workerStatus?.AttachScrape(resumeState.ScrapeId);
            bool anyScrapePhase =
                resolvedPhases.HasFlag(ScrapePhase.SoloScrape)
                || resolvedPhases.HasFlag(ScrapePhase.BandScrape);

            await PruneStaleWebRegistrationsIfEligibleAsync(
                opts,
                ct);

            // Stale precomputed data (from last scrape) is served during the scrape pass.
            // PrecomputeAllAsync at post-scrape overwrites entries atomically, so we don't
            // need to invalidate here. This avoids an 8+ second cold-start penalty for the
            // first API request when the service restarts mid-scrape.

            var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
            if (accessToken is null)
            {
                _log.LogError("Cannot obtain access token. Skipping this pass.");
                passDetail = "Access token unavailable";
                return;
            }

            ScrapeCatalogSelection catalogSelection;
            try
            {
                catalogSelection =
                    await ResolveSongCatalogForPassAsync(
                        service,
                        resumeState);
            }
            catch (SongCatalogCaptureException ex)
            {
                var capture = ex.Capture;
                passDetail = ex.Message;
                _log.LogError(
                    "Aborting scrape before allocation because the provider song catalog is not exact. " +
                    "RequestSucceeded={RequestSucceeded}, SafetyMerge={SafetyMerge}, " +
                    "ProviderSongs={ProviderSongs}, CatalogSongs={CatalogSongs}, " +
                    "DroppedObjects={DroppedObjects}, Reason={Reason}",
                    capture.ProviderRequestSucceeded,
                    capture.SafetyMergeApplied,
                    capture.ProviderSongCount,
                    capture.CatalogSongCount,
                    capture.DroppedProviderObjectCount,
                    capture.FailureReason);
                return;
            }

            var passService = catalogSelection.Service;
            var songCatalogToken = catalogSelection.Token;
            if (resumeState is not null)
            {
                _log.LogInformation(
                    "Resume scrape {ScrapeId} pinned to publication {PublicationId} song catalog version {CatalogVersion} ({SongCount} songs); provider refresh skipped.",
                    resumeState.ScrapeId,
                    catalogSelection.PublicationId,
                    catalogSelection.Token.CatalogVersion,
                    catalogSelection.Token.SongCount);
            }
            else
            {
                _persistence.InvalidateTotalSongCount();
            }

            // Keep this worker's local song list current for scrape requests. Public
            // /api/songs freshness, song-change pushes, and path generation are owned
            // by SongCatalogRefreshWorker in fstservice.
            PrimeSongsCache(passService);

            // ── Core scrape: delegate to ScrapeOrchestrator ──
            // Freeze all response caches so API consumers see consistent (stale) data
            // throughout the scrape + post-scrape enrichment + precomputation cycle.
            _lifecycle.ScrapeStarting();
            publicReadsFrozen = true;

            ScrapePassResult? result = null;
            var authFailureAborted = false;
            if (anyScrapePhase)
            {
                try
                {
                    _workerStatus?.BeginOperation("scrape.leaderboards", "Scraping leaderboard scores", phase: "Scraping", subOperation: "fetching_leaderboards");
                    result = await _scrapeOrchestrator.RunAsync(
                        accessToken,
                        _tokenManager.AccountId!,
                        passService,
                        songCatalogToken!,
                        ct,
                        _tokenManager);
                    _workerStatus?.CompleteOperation("scrape.leaderboards");
                }
                catch (ScrapeAuthenticationException ex)
                {
                    _workerStatus?.FailOperation("scrape.leaderboards", ex);
                    authFailureAborted = true;
                    _log.LogError(ex,
                        "Scrape pass aborted due to unrecoverable authorization failure. " +
                        "Partial scrape data from this pass will not be post-processed or published.");
                }
                catch (CdnBlockedException ex)
                {
                    _workerStatus?.FailOperation("scrape.leaderboards", ex, "CDN block detected");
                    _log.LogError(ex,
                        "CDN block escaped to scrape pass level (wire sends: {WireSends}, blocks: {Blocks}). " +
                        "Partial data from this pass was already persisted via pipelined writers. " +
                        "Full-data post-scrape derived work will be skipped unless the scrape returned a completed result.",
                        _globalScraper.Executor.TotalHttpSends, _globalScraper.Executor.CdnBlocksDetected);
                    // Do NOT return — fall through so cleanup can unfreeze public reads.
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _workerStatus?.FailOperation("scrape.leaderboards", detail: "Internal cancellation source triggered");
                    _log.LogWarning(
                        "Scrape pass was canceled by an internal cancellation source. " +
                        "Partial data from this pass was already persisted. " +
                        "Full-data post-scrape derived work will be skipped unless the scrape returned a completed result.");
                    // Do NOT return — fall through so cleanup can unfreeze public reads.
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _workerStatus?.FailOperation("scrape.leaderboards", ex);
                    _log.LogError(ex,
                        "Scrape pass failed with a non-CDN exception. Partial data may have been staged. " +
                        "Full-data post-scrape derived work will be skipped unless the scrape returned a completed result.");
                    // Do NOT return — fall through so cleanup can unfreeze public reads.
                }
            }
            else
            {
                _log.LogInformation("Scrape phases not requested. Skipping ScrapeOrchestrator.");
            }

            if (authFailureAborted)
            {
                _progress.EndPass();
                _lifecycle.ScrapeFailed();
                publicReadsReleased = true;
                passDetail = "Leaderboard scrape failed authentication.";
                return;
            }

            // Build a minimal context when scrape was skipped (post-scrape phases
            // still need registered IDs, scrape requests, etc.)
            var ctx = result?.Context ?? new ScrapePassContext
            {
                ScrapeId = resumeState?.ScrapeId ?? result?.ScrapeId ?? 0,
                AccessToken = accessToken,
                CallerAccountId = _tokenManager.AccountId!,
                RegisteredIds = _persistence.Meta.GetRegisteredAccountIds(),
                Aggregates = new Persistence.GlobalLeaderboardPersistence.PipelineAggregates(),
                ScrapeRequests = BuildCatalogScrapeRequests(
                    passService,
                    opts),
                PublicationCatalogSongs =
                    passService.Songs.ToArray(),
                DegreeOfParallelism = opts.DegreeOfParallelism,
                EpicReportedOver100Pages = resumeState is not null && opts.ResumeEpicReportedOver100Pages,
                LeaderboardScrapeCompleted = !anyScrapePhase,
            };

            if (resumeState is not null)
            {
                foreach (var outcome in resumeState.PhaseOutcomes)
                {
                    ctx.PostScrapeOutcomes.Record(
                        RehydratePhaseOutcome(outcome));
                }

                result = new ScrapePassResult
                {
                    Context = ctx,
                    ScrapeId = resumeState.ScrapeId,
                    SongsScraped = opts.ResumeSongsScraped,
                    TotalEntries = opts.ResumeTotalEntries,
                    TotalRequests = opts.ResumeTotalRequests,
                    TotalBytes = opts.ResumeTotalBytes,
                    EpicReportedOver100Pages = opts.ResumeEpicReportedOver100Pages,
                    ScrapeDuration = DateTime.UtcNow - resumeState.StartedAtUtc,
                };
                _log.LogWarning(
                    "Resuming scrape {ScrapeId} from durable network/writer state with phases {Phases}.",
                    resumeState.ScrapeId,
                    ScrapePhaseResolver.Format(resolvedPhases));
            }

            long? pathPublicationId = null;
            if (ctx.ScrapeId > 0)
            {
                pathPublicationId = _persistence.Meta
                    .GetPublicationGenerationForScrape(ctx.ScrapeId)?
                    .PublicationId
                    ?? throw new InvalidOperationException(
                        $"Scrape {ctx.ScrapeId} has no publication-bound path snapshot.");
            }
            using var pathPublicationScope =
                pathPublicationId.HasValue
                    ? _pathDataStore.BeginPublicationRead(
                        pathPublicationId.Value)
                    : null;

            var postScrapePhases = resolvedPhases;
            var skipPostScrapeForIncompleteScrape = anyScrapePhase && result is null;
            if (skipPostScrapeForIncompleteScrape)
            {
                _log.LogWarning(
                    "Skipping post-scrape derived phases because the leaderboard scrape did not complete. " +
                    "Public reads will stay on the last published scrape and the full pipeline will retry next pass.");
                postScrapePhases = ScrapePhase.None;
            }

            // ── Post-pass: enrichment, refresh, rankings, rivals, derived publication ──
            if (skipPostScrapeForIncompleteScrape)
            {
                _log.LogWarning(
                    "Skipping scrape publication preparation because no completed scrape result is available. " +
                    "Current published band rankings will remain unchanged.");
            }
            else
            {
                _lifecycle.ScrapePostProcessing();
            }

            var postProcessCompleted = skipPostScrapeForIncompleteScrape;
            var postProcessOperationActive = false;
            if (postProcessCompleted)
            {
                _log.LogWarning("Post-scrape orchestration skipped because no completed scrape result is available.");
                passDetail = "Leaderboard scrape did not complete; post-processing and publication were skipped.";
            }
            else
            {
                try
                {
                    _workerStatus?.BeginOperation("scrape.post_process", "Post-processing leaderboard update", phase: "PostScrapeEnrichment");
                    postProcessOperationActive = true;
                    await _postScrapeOrchestrator.RunAsync(
                        ctx,
                        passService,
                        postScrapePhases,
                        ct);
                    postProcessCompleted = true;
                }
                catch (OperationCanceledException ex)
                {
                    passDetail = $"Post-processing was cancelled: {ex.Message}";
                    RecordFailedCandidateIsolation(
                        ctx.ScrapeId,
                        "Post-processing cancellation",
                        ex.Message,
                        ref durableFailureIsolationConfirmed);
                    throw;
                }
                catch (Exception ex)
                {
                    _workerStatus?.FailOperation("scrape.post_process", ex);
                    postProcessOperationActive = false;
                    passDetail = $"Post-processing failed: {ex.Message}";
                    RecordFailedCandidateIsolation(
                        ctx.ScrapeId,
                        "Post-processing",
                        ex.Message,
                        ref durableFailureIsolationConfirmed);
                    _log.LogError(ex, "Post-scrape orchestration failed. Finalizing pass with stale data.");
                }
            }

            if (postProcessCompleted)
            {
                var publicationCleanupCompleted = true;
                try
                {
                    await _postScrapeOrchestrator.RunPublicationCleanupAsync(ctx, postScrapePhases, ct);
                }
                catch (OperationCanceledException ex)
                {
                    passDetail = $"Publication cleanup was cancelled: {ex.Message}";
                    RecordFailedCandidateIsolation(
                        ctx.ScrapeId,
                        "Publication cleanup cancellation",
                        ex.Message,
                        ref durableFailureIsolationConfirmed);
                    throw;
                }
                catch (Exception ex)
                {
                    publicationCleanupCompleted = false;
                    passDetail = $"Publication cleanup failed: {ex.Message}";
                    _log.LogError(ex, "Publication cleanup failed. Published scrape will remain unchanged to avoid live-read fallback.");
                    RecordFailedCandidateIsolation(
                        ctx.ScrapeId,
                        "Publication cleanup",
                        ex.Message,
                        ref durableFailureIsolationConfirmed);
                }

                postProcessCompleted = publicationCleanupCompleted;
            }
            else
            {
                _log.LogWarning("Skipping publication cleanup because post-process orchestration did not complete cleanly.");
            }

            // ── Cleanup: storage/query-health work that must not delay fresh data publication ──
            if (postProcessCompleted)
            {
                try
                {
                    await _postScrapeOrchestrator.RunCleanupAsync(ctx, postScrapePhases, ct);
                }
                catch (OperationCanceledException ex)
                {
                    passDetail = $"Post-scrape cleanup was cancelled: {ex.Message}";
                    RecordFailedCandidateIsolation(
                        ctx.ScrapeId,
                        "Post-scrape cleanup cancellation",
                        ex.Message,
                        ref durableFailureIsolationConfirmed);
                    throw;
                }
                catch (Exception ex)
                {
                    passDetail = $"Post-scrape cleanup failed: {ex.Message}";
                    _log.LogWarning(ex, "Post-scrape cleanup failed. Will retry next pass.");
                }
            }
            else
            {
                _log.LogWarning("Skipping post-scrape cleanup because post-process orchestration did not complete cleanly.");
            }

            if (postProcessOperationActive)
            {
                if (postProcessCompleted)
                    _workerStatus?.CompleteOperation("scrape.post_process");
                else
                    _workerStatus?.FailOperation("scrape.post_process", detail: passDetail);
                postProcessOperationActive = false;
            }

            var endMemMb = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
            _log.LogInformation("Scrape pass complete. (Process memory: {MemoryMB} MB)", endMemMb);

            _progress.EndPass();

            long? publishedScrapeId = null;
            var publishedNewState = false;
            var publicationDurablyCommitted = false;

            if (result is not null)
            {
                if (postProcessCompleted)
                {
                    _workerStatus?.BeginOperation(
                        "scrape.publication",
                        "Publishing leaderboard update",
                        phase: "Publishing",
                        subOperation: "preparing_publication");
                    try
                    {
                        _lifecycle.ScrapePublishing();
                        ScrapePublicationGuard.EnsureCanPublish(
                            result.ScrapeId,
                            ctx.PostScrapeOutcomes,
                            _persistence.EnforcePublicationCriticalPhases);

                        int? expectedPublishedScopeCount = null;
                        if (_persistence.WritePublishedScopeSources)
                        {
                            if (!resolvedPhases.HasFlag(ScrapePhase.SoloScrape)
                                && resumeState is null)
                            {
                                throw new InvalidOperationException(
                                    "Published scope-source promotion requires the solo scrape phase.");
                            }

                            _workerStatus?.UpdateOperation(
                                "scrape.publication",
                                subOperation: "building_published_scope_sources");
                            var expectedPairs =
                                ScrapeOrchestrator.BuildExpectedSoloLeaderboardPairs(result.Context.ScrapeRequests);
                            var sourceBuildStopwatch = System.Diagnostics.Stopwatch.StartNew();
                            var sourceBuild = _persistence.BuildPublishedScopeSourceCandidate(
                                result.ScrapeId,
                                expectedPairs);
                            sourceBuildStopwatch.Stop();
                            _log.LogInformation(
                                "Built published scope-source candidate for scrape {ScrapeId}: expected={Expected:N0}, validated={Validated:N0}, mapped={Mapped:N0}, missing={Missing:N0}, elapsed={Elapsed}.",
                                result.ScrapeId,
                                sourceBuild.ExpectedScopeCount,
                                sourceBuild.ValidatedScopeCount,
                                sourceBuild.MappedScopeCount,
                                sourceBuild.MissingScopeCount,
                                sourceBuildStopwatch.Elapsed);
                            if (!sourceBuild.IsComplete)
                            {
                                throw new InvalidOperationException(
                                    $"Scrape {result.ScrapeId} published scope-source candidate is incomplete: " +
                                    $"expected={sourceBuild.ExpectedScopeCount}, validated={sourceBuild.ValidatedScopeCount}, " +
                                    $"mapped={sourceBuild.MappedScopeCount}, missing={sourceBuild.MissingScopeCount}.");
                            }

                            expectedPublishedScopeCount = sourceBuild.ExpectedScopeCount;
                        }

                        _workerStatus?.UpdateOperation(
                            "scrape.publication",
                            subOperation: "committing_publication");
                        var queueImprovementNotifications =
                            _postScrapeOrchestrator.ShouldQueueImprovementNotifications(
                                ctx,
                                postScrapePhases);
                        IReadOnlyCollection<SoloCurrentProjectionScopeKey>?
                            improvementNotificationProjectionScopes = null;
                        if (queueImprovementNotifications)
                        {
                            _workerStatus?.UpdateOperation(
                                "scrape.publication",
                                subOperation: "preparing_notification_projection_plan");
                            improvementNotificationProjectionScopes =
                                await _postScrapeOrchestrator
                                    .PrepareImprovementNotificationProjectionScopesAsync(
                                        ctx,
                                        postScrapePhases,
                                        ct);
                        }

                        _persistence.Meta.CompleteScrapeRun(
                            result.ScrapeId,
                            result.SongsScraped,
                            result.TotalEntries,
                            result.TotalRequests,
                            result.TotalBytes,
                            result.EpicReportedOver100Pages);
                        _workerStatus?.UpdateOperation(
                            "scrape.publication",
                            subOperation: "preparing_publication_candidate");
                        var publicationPreparation =
                            _persistence.Meta.PrepareScrapePublication(
                            result.ScrapeId,
                            promoteCachedResponses:
                                postScrapePhases.HasFlag(ScrapePhase.SoloPrecompute),
                            expectedPublishedScopeCount: expectedPublishedScopeCount,
                            queueImprovementNotifications: queueImprovementNotifications,
                            improvementNotificationProjectionScopes:
                                improvementNotificationProjectionScopes,
                            rankingsInputCutoffUtc:
                                ctx.RankingsInputCutoffUtc);
                        _workerStatus?.UpdateOperation(
                            "scrape.publication",
                            subOperation: "draining_publication_readers");
                        var publicationCommit =
                            await CommitPreparedWithContentionRetriesAsync(
                                publicationPreparation,
                                ct);
                        publicationDurablyCommitted = true;
                        publishedScrapeId = result.ScrapeId;
                        publishedNewState = true;
                        passStatus = "completed";
                        passDetail = null;
                        _log.LogInformation(
                            "Publication {PublicationId} committed for scrape {ScrapeId}: prepare={PrepareElapsedMs:N3}ms, drain={DrainElapsedMs:N3}ms, exclusive={ExclusiveElapsedMs:N3}ms, lockRejections={LockRejections}, relationLockRetries={RelationLockRetries}.",
                            publicationCommit.PublicationId,
                            result.ScrapeId,
                            publicationPreparation.PrepareDuration
                                .TotalMilliseconds,
                            publicationCommit.DrainDuration
                                .TotalMilliseconds,
                            publicationCommit.ExclusiveLockDuration
                                .TotalMilliseconds,
                            publicationCommit.LockRejections,
                            publicationCommit.RelationLockRetries);
                        _workerStatus?.UpdateOperation(
                            "scrape.publication",
                            subOperation:
                                "cleaning_publication_artifacts");
                        try
                        {
                            _persistence.Meta
                                .CleanupPublishedScrapePublication(
                                    publicationPreparation,
                                    publicationCommit);
                        }
                        catch (Exception cleanupEx)
                        {
                            _log.LogWarning(
                                cleanupEx,
                                "Publication {PublicationId} succeeded, but post-commit artifact cleanup will need a later retry.",
                                publicationCommit.PublicationId);
                        }
                        _workerStatus?.CompleteOperation("scrape.publication");
                        _workerStatus?.UpdateOperation(
                            "scrape.pass",
                            phase: "Finalizing",
                            subOperation: "post_publication");
                    }
                    catch (OperationCanceledException ex)
                        when (publicationDurablyCommitted)
                    {
                        _log.LogWarning(
                            ex,
                            "Scrape {ScrapeId} committed successfully, but post-commit finalization was canceled; publication remains current.",
                            result.ScrapeId);
                    }
                    catch (Exception ex)
                        when (publicationDurablyCommitted)
                    {
                        _log.LogWarning(
                            ex,
                            "Scrape {ScrapeId} committed successfully, but post-commit status/cleanup work failed; publication remains current.",
                            result.ScrapeId);
                    }
                    catch (PublicationCommitShutdownDeferredException ex)
                    {
                        _workerStatus?.CompleteOperation(
                            "scrape.publication",
                            "deferred",
                            "worker shutdown");
                        passDetail = ex.Message;
                        durableFailureIsolationConfirmed = false;
                        return;
                    }
                    catch (PublicationCommitDeferredException ex)
                    {
                        _workerStatus?.FailOperation(
                            "scrape.publication",
                            ex,
                            "ready publication deferred");
                        passDetail = ex.Message;
                        durableFailureIsolationConfirmed = false;
                        return;
                    }
                    catch (PublicationCommitExecutionException ex)
                    {
                        passDetail =
                            $"Publication failed: {ex.InnerException?.Message ?? ex.Message}";
                        try
                        {
                            _persistence.Meta.FailScrapeRun(
                                ex.ScrapeId,
                                MetaDatabase
                                    .PublicationReadIsolationFailurePhase,
                                passDetail,
                                ex.CommitIntent.CommitIntent);
                            ex.CommitIntent.CompleteIsolation();
                            ex.CommitIntent.Dispose();
                        }
                        catch (Exception isolationEx)
                        {
                            durableFailureIsolationConfirmed = false;
                            ex.CommitIntent
                                .PreserveForIsolationPending();
                            ex.CommitIntent.Dispose();
                            _lifecycle.ScrapeFailureIsolationPending(
                                ex.ScrapeId);
                            throw new InvalidOperationException(
                                "Publication failed and durable read isolation remains pending.",
                                isolationEx);
                        }

                        throw new InvalidOperationException(
                            passDetail,
                            ex.InnerException ?? ex);
                    }
                    catch (OperationCanceledException)
                    {
                        _workerStatus?.CompleteOperation("scrape.publication", "cancelled");
                        passDetail = "Publication was cancelled.";
                        RecordFailedCandidateIsolation(
                            ctx.ScrapeId,
                            "Publication cancellation",
                            "publication",
                            passDetail,
                            ref durableFailureIsolationConfirmed);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _workerStatus?.FailOperation("scrape.publication", ex);
                        passDetail = $"Publication failed: {ex.Message}";
                        RecordFailedCandidateIsolation(
                            result.ScrapeId,
                            "Publication",
                            "publication",
                            ex.Message,
                            ref durableFailureIsolationConfirmed);
                        throw;
                    }
                }
                else
                {
                    passDetail ??= $"Scrape {result.ScrapeId} post-processing did not complete.";
                    _log.LogWarning(
                        "Scrape {ScrapeId} was not marked complete or published because post-process orchestration did not complete cleanly.",
                        result.ScrapeId);
                }
            }
            else if (!anyScrapePhase && postProcessCompleted)
            {
                passStatus = "completed";
                passDetail = null;
            }

            if (publishedNewState)
            {
                try
                {
                    if (ctx.RankingsComputedSuccessfully
                        && ctx.RankingsInputCutoffUtc.HasValue)
                    {
                        _persistence.Meta.ClearBackfillRankingsPending(
                            ctx.RegisteredIds,
                            ctx.RankingsInputCutoffUtc.Value);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        ex,
                        "Published scrape {ScrapeId} successfully, but clearing pending-rank sync state failed and will retry later.",
                        publishedScrapeId);
                }
            }

            if (publicReadsFrozen)
            {
                if (publishedNewState)
                    PrimeSongsCache(passService);

                if (passStatus == "completed")
                    _lifecycle.ScrapeCompleted();
                else
                    _lifecycle.ScrapeFailed(
                        durableFailureIsolationConfirmed);
                publicReadsReleased = true;
            }

            if (publishedNewState)
            {
                try
                {
                    await _postScrapeOrchestrator.RunImprovementNotificationsAfterPublicationAsync(ctx, postScrapePhases, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Post-publication improvement notification detection failed. Published scrape remains available; notifications will retry next pass.");
                }

                try
                {
                    // Notify connected clients only after the server can serve the published scrape.
                    await NotifyScoresChangedAfterPublicationAsync(
                        publishedScrapeId);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        ex,
                        "Published scrape {ScrapeId} remains current, but scores-changed broadcast failed; clients will recover through publication polling.",
                        publishedScrapeId);
                }

                _deferredRetentionMaintenance?.ScheduleAfterPublication(
                    $"scrape {publishedScrapeId} published",
                    ct);
            }
        }
        finally
        {
            if (publicReadsFrozen && !publicReadsReleased)
            {
                if (passStatus == "completed")
                    _lifecycle.ScrapeCompleted();
                else
                    _lifecycle.ScrapeFailed(
                        durableFailureIsolationConfirmed);
                publicReadsReleased = true;
            }

            await CleanupActiveScrapeResourcesAsync("scrape pass exit", CancellationToken.None);
            _backgroundWork.ResumeAfterScrape();
            _workerStatus?.CompleteOperation("scrape.pass", passStatus, passDetail);
        }
    }

    private async Task ResumeDeferredPublicationBeforeGatesAsync(
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var outcome =
                await TryResumeDeferredPublicationAsync(ct);
            if (outcome.Published)
            {
                EnqueueCurrentPublicationForRetentionObservation();
                return;
            }
            if (!outcome.Handled)
                return;

            _log.LogWarning(
                "Deferred publication remains unresolved; retrying in {Delay}.",
                DeferredPublicationRetryDelay);
            await Task.Delay(
                DeferredPublicationRetryDelay,
                ct);
        }
    }

    private async Task NotifyScoresChangedAfterPublicationAsync(
        long? scrapeId)
    {
        await (
            ScoresChangedNotificationTestHook?.Invoke(scrapeId)
            ?? _notifications.NotifyScoresChangedAsync(scrapeId));
        if (scrapeId.HasValue)
        {
            _lastCompletedScoresChangedBroadcastScrapeId =
                scrapeId.Value;
        }
    }

    private async Task<DeferredPublicationResumeOutcome>
        TryResumeDeferredPublicationAsync(CancellationToken ct)
    {
        PublicationPreparationResult? preparation;
        try
        {
            preparation =
                _persistence.Meta
                    .GetDeferredPublicationPreparation();
        }
        catch (DeferredPublicationMetadataException ex)
        {
            _log.LogError(
                ex,
                "Deferred publication metadata is invalid; isolating the working candidate.");
            var pointers =
                _persistence.Meta.GetPublicationPointerState();
            if (pointers.WorkingPublicationId.HasValue)
            {
                var generation =
                    _persistence.Meta.GetPublicationGeneration(
                        pointers.WorkingPublicationId.Value);
                if (generation?.ScrapeId is long scrapeId)
                {
                    var isolationConfirmed = true;
                    try
                    {
                        _persistence.Meta.FailScrapeRun(
                            scrapeId,
                            MetaDatabase.PublicationReadIsolationFailurePhase,
                            ex.Message);
                    }
                    catch
                    {
                        isolationConfirmed = false;
                        _lifecycle.ScrapeFailureIsolationPending(
                            scrapeId);
                    }
                    _lifecycle.ScrapeFailed(isolationConfirmed);
                }
            }
            return new DeferredPublicationResumeOutcome(
                Handled: true,
                Published: false,
                Detail: ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Deferred publication lookup failed transiently; preserving the ready candidate for retry.");
            return new DeferredPublicationResumeOutcome(
                Handled: true,
                Published: false,
                Detail:
                    "Deferred publication lookup failed transiently; retry scheduled.");
        }

        if (preparation is null)
        {
            return new DeferredPublicationResumeOutcome(
                Handled: false,
                Published: false,
                Detail: null);
        }

        _log.LogWarning(
            "Resuming deferred ready publication {PublicationId} for scrape {ScrapeId} before any new scrape allocation.",
            preparation.PublicationId,
            preparation.ScrapeId);
        _workerStatus?.AttachScrape(preparation.ScrapeId);
        _workerStatus?.BeginOperation(
            "scrape.publication",
            "Resuming deferred leaderboard publication",
            phase: "Publishing",
            subOperation: "resuming_deferred_publication");
        PublicationCommitResult commit;
        try
        {
            commit =
                await CommitPreparedWithContentionRetriesAsync(
                    preparation,
                    ct);
        }
        catch (PublicationCommitShutdownDeferredException ex)
        {
            _workerStatus?.CompleteOperation(
                "scrape.publication",
                "deferred",
                "worker shutdown");
            return new DeferredPublicationResumeOutcome(
                Handled: true,
                Published: false,
                Detail: ex.Message);
        }
        catch (PublicationCommitDeferredException ex)
        {
            _workerStatus?.FailOperation(
                "scrape.publication",
                ex,
                "deferred publication remains ready");
            return new DeferredPublicationResumeOutcome(
                Handled: true,
                Published: false,
                Detail: ex.Message);
        }
        catch (PublicationCommitExecutionException ex)
        {
            var isolationConfirmed = true;
            try
            {
                _persistence.Meta.FailScrapeRun(
                    ex.ScrapeId,
                    MetaDatabase.PublicationReadIsolationFailurePhase,
                    ex.InnerException?.Message ?? ex.Message,
                    ex.CommitIntent.CommitIntent);
                ex.CommitIntent.CompleteIsolation();
                ex.CommitIntent.Dispose();
            }
            catch (Exception isolationEx)
            {
                isolationConfirmed = false;
                ex.CommitIntent.PreserveForIsolationPending();
                ex.CommitIntent.Dispose();
                _lifecycle.ScrapeFailureIsolationPending(
                    ex.ScrapeId);
                _log.LogError(
                    isolationEx,
                    "Deferred publication for scrape {ScrapeId} failed and durable isolation remains pending.",
                    ex.ScrapeId);
            }
            _lifecycle.ScrapeFailed(isolationConfirmed);
            _workerStatus?.FailOperation(
                "scrape.publication",
                ex.InnerException ?? ex);
            return new DeferredPublicationResumeOutcome(
                Handled: true,
                Published: false,
                Detail: ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            var isolationConfirmed = true;
            try
            {
                _persistence.Meta.FailScrapeRun(
                    preparation.ScrapeId,
                    MetaDatabase.PublicationReadIsolationFailurePhase,
                    ex.Message);
            }
            catch (Exception isolationEx)
            {
                isolationConfirmed = false;
                _lifecycle.ScrapeFailureIsolationPending(
                    preparation.ScrapeId);
                _log.LogError(
                    isolationEx,
                    "Deferred publication {PublicationId} failed and durable isolation remains pending.",
                    preparation.PublicationId);
            }
            _lifecycle.ScrapeFailed(isolationConfirmed);
            _workerStatus?.FailOperation(
                "scrape.publication",
                ex);
            return new DeferredPublicationResumeOutcome(
                Handled: true,
                Published: false,
                Detail: ex.Message);
        }

        if (preparation.RankingsInputCutoffUtc.HasValue)
        {
            try
            {
                _persistence.Meta
                    .ClearBackfillRankingsPending(
                        _persistence.Meta
                            .GetRegisteredAccountIds(),
                        preparation
                            .RankingsInputCutoffUtc.Value);
            }
            catch (Exception pendingStateEx)
            {
                _log.LogWarning(
                    pendingStateEx,
                    "Deferred publication {PublicationId} committed, but clearing pending-rank sync state failed and will retry later.",
                    preparation.PublicationId);
            }
        }

        try
        {
            _persistence.Meta
                .CleanupPublishedScrapePublication(
                    preparation,
                    commit);
        }
        catch (Exception cleanupEx)
        {
            _log.LogWarning(
                cleanupEx,
                "Deferred publication {PublicationId} committed, but cleanup deferred.",
                preparation.PublicationId);
        }

        try
        {
            _lifecycle.ScrapeCompleted();
        }
        catch (Exception lifecycleEx)
        {
            _log.LogWarning(
                lifecycleEx,
                "Deferred publication {PublicationId} committed, but local lifecycle completion failed.",
                preparation.PublicationId);
        }

        try
        {
            _workerStatus?.CompleteOperation(
                "scrape.publication");
        }
        catch (Exception statusEx)
        {
            _log.LogWarning(
                statusEx,
                "Deferred publication {PublicationId} committed, but worker status completion failed.",
                preparation.PublicationId);
        }

        try
        {
            await NotifyScoresChangedAfterPublicationAsync(
                preparation.ScrapeId);
        }
        catch (Exception notificationEx)
        {
            _log.LogWarning(
                notificationEx,
                "Deferred publication {PublicationId} committed, but scores-changed broadcast failed.",
                preparation.PublicationId);
        }

        return new DeferredPublicationResumeOutcome(
            Handled: true,
            Published: true,
            Detail: null);
    }

    private async Task<PublicationCommitResult>
        CommitPreparedWithContentionRetriesAsync(
            PublicationPreparationResult preparation,
            CancellationToken ct)
    {
        Exception? lastContention = null;
        var attempts = Math.Max(
            1,
            _publicationCommitOptions
                .ContentionRetryAttempts);
        ScrapeLifecycleNotifier.PublicationCommitIntentLease?
            commitIntent = null;
        for (var attempt = 1;
             attempt <= attempts && commitIntent is null;
             attempt++)
        {
            try
            {
                commitIntent =
                    _lifecycle.PublicationCommitStarting(
                        preparation.ScrapeId);
            }
            catch (PublicationCommitBusyException ex)
            {
                lastContention = ex;
                if (attempt >= attempts)
                {
                    throw new PublicationCommitDeferredException(
                        preparation.ScrapeId,
                        ex);
                }

                _log.LogWarning(
                    ex,
                    "Publication {PublicationId} could not acquire commit-intent ownership on attempt {Attempt}/{Attempts}; retrying.",
                    preparation.PublicationId,
                    attempt,
                    attempts);
                try
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(
                            Math.Max(
                                1,
                                _publicationCommitOptions
                                    .ContentionRetryDelayMilliseconds)),
                        ct);
                }
                catch (OperationCanceledException cancellationEx)
                    when (ct.IsCancellationRequested)
                {
                    throw new PublicationCommitShutdownDeferredException(
                        preparation.ScrapeId,
                        cancellationEx);
                }
            }
        }

        if (commitIntent is null)
        {
            throw new PublicationCommitDeferredException(
                preparation.ScrapeId,
                lastContention
                ?? new InvalidOperationException(
                    "Publication commit-intent acquisition exhausted."));
        }

        var transferCommitIntent = false;
        try
        {
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    return _persistence.Meta
                        .CommitPreparedScrapePublication(
                            preparation,
                            commitIntent.CommitIntent);
                }
                catch (OperationCanceledException cancellationEx)
                    when (ct.IsCancellationRequested)
                {
                    commitIntent.Defer();
                    throw new PublicationCommitShutdownDeferredException(
                        preparation.ScrapeId,
                        cancellationEx);
                }
                catch (PublicationCommitDeadlineExceededException ex)
                {
                    lastContention = ex;
                    commitIntent.Defer();
                    throw new PublicationCommitDeferredException(
                        preparation.ScrapeId,
                        ex);
                }
                catch (PublicationCommitBusyException ex)
                {
                    lastContention = ex;
                    if (attempt >= attempts)
                    {
                        commitIntent.Defer();
                        throw new PublicationCommitDeferredException(
                            preparation.ScrapeId,
                            ex);
                    }

                    _log.LogWarning(
                        ex,
                        "Publication {PublicationId} contention attempt {Attempt}/{Attempts} failed while retaining one durable commit intent; retrying.",
                        preparation.PublicationId,
                        attempt,
                        attempts);
                    try
                    {
                        await Task.Delay(
                            TimeSpan.FromMilliseconds(
                                Math.Max(
                                    1,
                                    _publicationCommitOptions
                                        .ContentionRetryDelayMilliseconds)),
                            ct);
                    }
                    catch (OperationCanceledException cancellationEx)
                        when (ct.IsCancellationRequested)
                    {
                        commitIntent.Defer();
                        throw new PublicationCommitShutdownDeferredException(
                            preparation.ScrapeId,
                            cancellationEx);
                    }
                }
                catch (Exception ex)
                {
                    transferCommitIntent = true;
                    throw new PublicationCommitExecutionException(
                        preparation.ScrapeId,
                        ex,
                        commitIntent);
                }
            }
        }
        finally
        {
            if (!transferCommitIntent)
                commitIntent.Dispose();
        }

        throw new PublicationCommitDeferredException(
            preparation.ScrapeId,
            lastContention
            ?? new InvalidOperationException(
                "Publication contention retry policy exhausted."));
    }

    private void RecordFailedCandidateIsolation(
        long scrapeId,
        string operation,
        string failureMessage,
        ref bool durableFailureIsolationConfirmed) =>
        RecordFailedCandidateIsolation(
            scrapeId,
            operation,
            MetaDatabase.PostProcessReadIsolationFailurePhase,
            failureMessage,
            ref durableFailureIsolationConfirmed);

    private void RecordFailedCandidateIsolation(
        long scrapeId,
        string operation,
        string failurePhase,
        string failureMessage,
        ref bool durableFailureIsolationConfirmed)
    {
        if (scrapeId <= 0)
            return;

        try
        {
            _persistence.Meta.FailScrapeRun(
                scrapeId,
                failurePhase,
                failureMessage);
        }
        catch (Exception isolationEx)
        {
            durableFailureIsolationConfirmed = false;
            _lifecycle.ScrapeFailureIsolationPending(scrapeId);
            throw new InvalidOperationException(
                $"{operation} failed and durable read isolation could not be recorded.",
                isolationEx);
        }
    }

    private async Task CleanupActiveScrapeResourcesAsync(string reason, CancellationToken ct)
    {
        var cleanupTask = Task.Run(async () =>
        {
            await _scrapeOrchestrator.CleanupActiveBandSpoolAsync();
            await _persistence.CleanupActiveScrapeWritersAsync();
        });

        Task completedTask;
        try
        {
            completedTask = await Task.WhenAny(cleanupTask, Task.Delay(BestEffortCleanupTimeout, ct));
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Best-effort scrape resource cleanup skipped during {Reason}: shutdown cancellation was already requested.", reason);
            return;
        }

        if (!ReferenceEquals(completedTask, cleanupTask))
        {
            _log.LogWarning("Best-effort scrape resource cleanup timed out after {Timeout} during {Reason}.", BestEffortCleanupTimeout, reason);
            return;
        }

        try
        {
            await cleanupTask;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Best-effort scrape resource cleanup failed during {Reason}.", reason);
        }
    }

    private async Task PruneStaleWebRegistrationsIfEligibleAsync(
        ScraperOptions opts,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now - _serviceStartedAtUtc < WebRegistrationStartupProtection)
            return;

        var retentionWindow = TimeSpan.FromDays(Math.Max(1, opts.WebRegistrationRetentionDays));
        var staleBeforeUtc = now - retentionWindow;
        await using var registrationLease =
            await _registrationMutations
                .AcquireWriteLeaseAsync(ct);
        await registrationLease.VerifyHeldAsync(ct);
        var pruned = _persistence.Meta.PruneStaleWebRegistrations(staleBeforeUtc);
        if (pruned > 0)
        {
            _log.LogInformation(
                "Pruned {Count} stale web registration(s) before scrape start. StaleBeforeUtc={StaleBeforeUtc:o}",
                pruned,
                staleBeforeUtc);
        }
    }

    // ─── Path generation ──────────────────────────────────────

    /// <summary>
    /// Generates optimal paths and max attainable scores for new/changed songs.
    /// Downloads encrypted MIDI from Epic, decrypts, runs CHOpt, stores results.
    /// Safe to call as fire-and-forget — errors are logged but don't block scraping.
    /// </summary>
    [ExcludeFromCodeCoverage] // Coordinator error paths are covered independently; Coverlet misses this async catch state.
    internal async Task TryGeneratePathsAsync(FestivalService service, bool force, CancellationToken ct)
    {
        var opts = _options.Value;
        if (!opts.EnablePathGeneration ||
            !opts.EnableAutomaticPathGeneration)
            return;

        try
        {
            var songs = service.Songs.Where(s => s.track?.su is not null && !string.IsNullOrEmpty(s.track.mu)).ToList();
            if (songs.Count == 0) return;

            if (force)
                await _pathGeneration.GeneratePathsAsync(songs, force: true, ct);
            else
                await _pathGeneration.GenerateAutomaticPathsAsync(songs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Path generation failed. Scraping continues unaffected.");
        }
    }

    // ─── Songs cache priming ────────────────────────────────────

    private void PrimeSongsCache(FestivalService? service = null)
    {
        try
        {
            _songsCache.Prime(
                service ?? _festivalService,
                _pathDataStore,
                _persistence.Meta,
                _persistence,
                _precomputer,
                _jsonOpts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to prime songs cache; will rebuild on next request.");
            _songsCache.Invalidate();
        }
    }

    // ─── Song test ───────────────────────────────────────────────

    private async Task RunSingleSongTestAsync(
        FestivalService service,
        ScraperOptions opts,
        CancellationToken ct)
    {
        // Support comma-separated queries: --test "Song A,Song B"
        var queries = opts.TestSongQuery!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        _log.LogInformation("Test mode. Searching for {Count} song(s): {Queries}",
            queries.Length, string.Join(", ", queries.Select(q => $"\"{q}\"")));

        // Resolve each query to a Song
        var matched = new List<Song>();
        foreach (var query in queries)
        {
            var match = service.Songs.FirstOrDefault(s =>
                s.track?.tt != null &&
                s.track.tt.Contains(query, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                _log.LogError("No song matching \"{Query}\" found in catalog ({Total} songs).",
                    query, service.Songs.Count);
                continue;
            }

            _log.LogInformation("Found: \"{Title}\" by {Artist}  [id={SongId}]",
                match.track.tt, match.track.an, match.track.su);
            matched.Add(match);
        }

        if (matched.Count == 0)
        {
            _log.LogError("No songs matched. Exiting.");
            return;
        }

        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is null)
        {
            _log.LogError("Cannot obtain access token for test.");
            return;
        }

        var accountId = _tokenManager.AccountId!;

        // Build scrape requests — query all instruments for every song
        var scrapeRequests = matched.Select(song => new GlobalLeaderboardScraper.SongScrapeRequest
        {
            SongId = song.track.su,
            Instruments = GlobalLeaderboardScraper.AllInstruments,
            Label = song.track.tt,
        }).ToList();

        _log.LogInformation("Scraping {SongCount} song(s) across all instruments (DOP={Dop})...",
            scrapeRequests.Count, opts.DegreeOfParallelism);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var allResults = await _globalScraper.ScrapeManySongsAsync(
            scrapeRequests, accessToken, accountId, opts.DegreeOfParallelism, onSongComplete: null, ct,
            maxPages: opts.MaxPagesPerLeaderboard,
            sequential: opts.SequentialScrape,
            pageConcurrency: opts.PageConcurrency,
            songConcurrency: opts.SongConcurrency,
            validEntryTarget: opts.ValidEntryTarget);
        sw.Stop();

        // Grand summary
        int grandEntries = allResults.Values.SelectMany(r => r).Sum(r => r.Entries.Count);
        int grandRequests = allResults.Values.SelectMany(r => r).Sum(r => r.Requests);
        long grandBytes = allResults.Values.SelectMany(r => r).Sum(r => r.BytesReceived);

        _log.LogInformation(
            "All done. {Songs} songs, {Entries} total entries, {Requests} requests, {Bytes} bytes, {Elapsed:F1}s",
            allResults.Count, grandEntries, grandRequests, grandBytes, sw.Elapsed.TotalSeconds);

        // Per-song detail
        foreach (var song in matched)
        {
            if (!allResults.TryGetValue(song.track.su, out var results)) continue;

            _log.LogInformation("═══ {Title} by {Artist} ═══", song.track.tt, song.track.an);

            foreach (var result in results)
            {
                _log.LogInformation("── {Instrument}: {Count} entries, {Pages} pages ──",
                    result.Instrument, result.Entries.Count, result.TotalPages);

                foreach (var entry in result.Entries.Take(3))
                {
                    _log.LogInformation(
                        "    {AccountId}  Score={Score}  Accuracy={Accuracy}%  Stars={Stars}  FC={FC}",
                        entry.AccountId, entry.Score, entry.Accuracy,
                        entry.Stars, entry.IsFullCombo ? "YES" : "no");
                }

                if (result.Entries.Count > 3)
                    _log.LogInformation("    ... and {More} more entries", result.Entries.Count - 3);
            }
        }
    }
}
