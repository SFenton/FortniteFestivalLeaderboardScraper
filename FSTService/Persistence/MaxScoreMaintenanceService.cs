using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FortniteFestival.Core;
using FSTService.Scraping;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

public sealed class MaxScoreMaintenanceService
{
    private static readonly TimeSpan WorkerHeartbeatStaleAfter =
        TimeSpan.FromSeconds(90);
    private readonly PathGenerationCoordinator _pathGeneration;
    private readonly IPathDataStore _pathStore;
    private readonly IMetaDatabase _metaDatabase;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ScraperOptions _options;
    private readonly MaxScoreMaintenanceNotificationService _notifications;
    private readonly MaxScoreMaintenanceDerivedStateService _derivedState;
    private readonly ScrapeTimePrecomputer _precomputer;
    private readonly ISongInstrumentSupportCache
        _instrumentSupportCache;
    private readonly ILogger<MaxScoreMaintenanceService> _log;

    public MaxScoreMaintenanceService(
        PathGenerationCoordinator pathGeneration,
        IPathDataStore pathStore,
        IMetaDatabase metaDatabase,
        NpgsqlDataSource dataSource,
        IOptions<ScraperOptions> options,
        MaxScoreMaintenanceNotificationService notifications,
        MaxScoreMaintenanceDerivedStateService derivedState,
        ScrapeTimePrecomputer precomputer,
        ISongInstrumentSupportCache instrumentSupportCache,
        ILogger<MaxScoreMaintenanceService> log)
    {
        _pathGeneration = pathGeneration;
        _pathStore = pathStore;
        _metaDatabase = metaDatabase;
        _dataSource = dataSource;
        _options = options.Value;
        _notifications = notifications;
        _derivedState = derivedState;
        _precomputer = precomputer;
        _instrumentSupportCache = instrumentSupportCache;
        _log = log;
    }

    public async Task<MaxScoreMaintenanceStageReport> StageAsync(
        long expectedPublishedScrapeId,
        string? stageRequestPath,
        IReadOnlyList<string> explicitSongIds,
        string manifestOutputPath,
        string reportOutputPath,
        CancellationToken ct)
    {
        RequirePathGenerationConfiguration();
        MaxScoreMaintenanceStageRequest request;
        if (stageRequestPath is not null)
        {
            request =
                await MaxScoreMaintenanceFileStore.LoadStageRequestAsync(
                    _options.DataDirectory,
                    stageRequestPath,
                    ct);
            if (explicitSongIds.Count > 0)
            {
                throw new ArgumentException(
                    "Stage request file and explicit song IDs are mutually exclusive.");
            }
        }
        else
        {
            request = new MaxScoreMaintenanceStageRequest(
                MaxScoreMaintenanceStageRequest.CurrentRequestVersion,
                expectedPublishedScrapeId,
                explicitSongIds
                    .OrderBy(songId => songId, StringComparer.Ordinal)
                    .Select(songId =>
                        new MaxScoreMaintenanceStageRequestSong(songId))
                    .ToArray())
                .ValidateAndNormalize();
        }
        if (request.ExpectedPublishedScrapeId
            != expectedPublishedScrapeId)
        {
            throw new InvalidOperationException(
                "Stage request published scrape ID does not match the CLI gate.");
        }

        var context = LoadPublishedContext(
            expectedPublishedScrapeId,
            expectedPublicationId: null,
            request.Songs.Select(song => song.SongId).ToArray(),
            requireUnfrozen: true);
        var requests = new List<(
            SongPathRequest Request,
            PathGenerationState State)>(request.Songs.Count);
        var requestBySong = request.Songs.ToDictionary(
            song => song.SongId,
            StringComparer.Ordinal);
        foreach (var requestSong in request.Songs)
        {
            var song = context.SongsById[requestSong.SongId];
            var state = RequireCurrentPathState(
                requestSong.SongId,
                requestSong.SongId,
                context.CatalogLastModifiedBySong[requestSong.SongId]);
            if (requestSong.ExpectedOldMaxima is not null
                && requestSong.ExpectedOldMaxima
                    != MaxScoreMaintenanceMaxima.From(state.MaxScores))
            {
                throw new InvalidOperationException(
                    $"Expected old maxima do not match current state for {requestSong.SongId}.");
            }

            var pathRequest = SongPathRequest.FromSong(song)
                ?? throw new InvalidOperationException(
                    $"Song {requestSong.SongId} has no usable encrypted chart identity.");
            pathRequest = pathRequest with
            {
                LastModified =
                    context.CatalogLastModifiedBySong[
                        requestSong.SongId],
            };
            requests.Add((pathRequest, state));
        }

        var attempts = await _pathGeneration.StagePathsSerialAsync(
            requests,
            ct);
        var reports = new List<MaxScoreMaintenanceStageSongReport>(
            request.Songs.Count);
        var manifestSongs =
            new List<MaxScoreMaintenanceManifestSong>(
                request.Songs.Count);
        PathGenerationRuntimeIdentity? runtime = null;
        for (var index = 0; index < requests.Count; index++)
        {
            var (pathRequest, state) = requests[index];
            var requestSong = requestBySong[pathRequest.SongId];
            if (index >= attempts.Count)
            {
                reports.Add(new MaxScoreMaintenanceStageSongReport(
                    pathRequest.SongId,
                    "not_attempted",
                    state.Revision,
                    MaxScoreMaintenanceMaxima.From(state.MaxScores),
                    null,
                    [],
                    null,
                    null,
                    "Serial staging stopped after an earlier failure."));
                continue;
            }

            var attempt = attempts[index];
            var promotion = attempt.StagedPromotion;
            if (attempt.Outcome != PathGenerationAttemptOutcome.Staged
                || promotion is null)
            {
                reports.Add(new MaxScoreMaintenanceStageSongReport(
                    pathRequest.SongId,
                    "failed",
                    state.Revision,
                    MaxScoreMaintenanceMaxima.From(state.MaxScores),
                    null,
                    [],
                    promotion?.ArtifactGenerationId,
                    attempt.FailureStage,
                    attempt.Detail));
                continue;
            }

            var validated =
                PathArtifactResolver.ValidateImmutableGeneration(
                    _options.DataDirectory,
                    promotion.SongId,
                    promotion.ArtifactGenerationId);
            ValidateStagedPromotion(promotion, validated);
            runtime ??= promotion.Runtime;
            if (runtime != promotion.Runtime)
            {
                throw new InvalidOperationException(
                    "Serial staging changed CHOpt runtime identity.");
            }

            var oldMaxima =
                MaxScoreMaintenanceMaxima.From(state.MaxScores);
            var newMaxima =
                MaxScoreMaintenanceMaxima.From(validated.MaxScores);
            if (requestSong.ExpectedNewMaxima is not null
                && requestSong.ExpectedNewMaxima != newMaxima)
            {
                throw new InvalidOperationException(
                    $"Expected new maxima do not match staged generation for {pathRequest.SongId}.");
            }
            var changed = MaxScoreMaintenanceManifest.AllInstruments
                .Where(instrument =>
                    oldMaxima.GetByInstrument(instrument)
                    != newMaxima.GetByInstrument(instrument))
                .ToArray();
            if (changed.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Staged generation for {pathRequest.SongId} does not change any maximum.");
            }

            var currentIdentity =
                MaxScoreMaintenancePathIdentity.From(state);
            var stagedIdentity = new MaxScoreMaintenancePathIdentity(
                state.Revision,
                promotion.DatFileHash,
                promotion.SongLastModified,
                promotion.GeneratedAtUtc,
                promotion.Runtime.Version,
                promotion.Runtime.BinarySha256,
                promotion.Runtime.Profile,
                promotion.ArtifactGenerationId,
                promotion.ExpectedInstruments.ToArray(),
                newMaxima,
                PathGenerationPending: false);
            manifestSongs.Add(
                new MaxScoreMaintenanceManifestSong(
                    pathRequest.SongId,
                    context.CatalogLastModifiedBySong[
                        pathRequest.SongId],
                    currentIdentity,
                    stagedIdentity,
                    changed)
                .ValidateAndNormalize());
            reports.Add(new MaxScoreMaintenanceStageSongReport(
                pathRequest.SongId,
                "staged",
                state.Revision,
                oldMaxima,
                newMaxima,
                changed,
                promotion.ArtifactGenerationId,
                null,
                null));
        }

        if (manifestSongs.Count != request.Songs.Count
            || runtime is null)
        {
            var failedReport =
                new MaxScoreMaintenanceStageReport(
                    MaxScoreMaintenanceStageReport.CurrentReportVersion,
                    Succeeded: false,
                    expectedPublishedScrapeId,
                    context.Catalog.PublicationId,
                    MaxScoreMaintenanceFileStore.ResolveNewJsonOutputPath(
                        _options.DataDirectory,
                        manifestOutputPath),
                    ManifestSha256: null,
                    reports);
            await MaxScoreMaintenanceFileStore.WriteNewReportAsync(
                _options.DataDirectory,
                reportOutputPath,
                failedReport,
                ct);
            return failedReport;
        }

        ValidateExpectedRuntime(request, runtime);
        var now = NormalizeDatabaseTimestamp(DateTime.UtcNow);
        var manifest = new MaxScoreMaintenanceManifest(
            MaxScoreMaintenanceManifest.CurrentManifestVersion,
            expectedPublishedScrapeId,
            context.Catalog.PublicationId,
            context.Catalog.CatalogVersion,
            context.Catalog.SchemaVersion,
            context.Catalog.ContentHash,
            context.Catalog.SongCount,
            context.Catalog.SourceCapturedAtUtc,
            now,
            runtime,
            manifestSongs
                .OrderBy(song => song.SongId, StringComparer.Ordinal)
                .ToArray())
            .ValidateAndNormalize();
        var finalContext = LoadPublishedContext(
            expectedPublishedScrapeId,
            context.Catalog.PublicationId,
            request.Songs.Select(song => song.SongId).ToArray(),
            requireUnfrozen: true);
        if (finalContext.Catalog.CatalogVersion
                != context.Catalog.CatalogVersion
            || finalContext.Catalog.SongCount
                != context.Catalog.SongCount
            || !string.Equals(
                finalContext.Catalog.ContentHash,
                context.Catalog.ContentHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Published catalog changed during serial staging; no manifest was written.");
        }
        EnsureStageSourceUnchanged(manifest);
        var writtenManifest =
            await MaxScoreMaintenanceFileStore
                .WriteCanonicalManifestAsync(
                    _options.DataDirectory,
                    manifestOutputPath,
                    manifest,
                    ct);
        if (!string.Equals(
                writtenManifest.Sha256,
                manifest.ComputeDigest(),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Canonical manifest file digest does not match the normalized manifest digest.");
        }

        var report = new MaxScoreMaintenanceStageReport(
            MaxScoreMaintenanceStageReport.CurrentReportVersion,
            Succeeded: true,
            expectedPublishedScrapeId,
            context.Catalog.PublicationId,
            writtenManifest.FullPath,
            writtenManifest.Sha256,
            reports);
        await MaxScoreMaintenanceFileStore.WriteNewReportAsync(
            _options.DataDirectory,
            reportOutputPath,
            report,
            ct);
        return report;
    }

    public async Task<MaxScoreMaintenancePlanReport> PlanAsync(
        long expectedPublishedScrapeId,
        string manifestPath,
        string expectedManifestDigest,
        string reportOutputPath,
        CancellationToken ct)
    {
        var manifest =
            await MaxScoreMaintenanceFileStore.LoadManifestAsync(
                _options.DataDirectory,
                manifestPath,
                ct);
        var manifestDigest = manifest.ComputeDigest();
        ValidateManifestCliIdentity(
            manifest,
            manifestDigest,
            expectedPublishedScrapeId,
            expectedManifestDigest);

        MaxScoreMaintenancePlanReport report;
        try
        {
            await using var lease =
                await _metaDatabase
                    .AcquireMaxScoreMaintenanceLeaseAsync(
                        manifest.ExpectedPublicationId,
                        ct);
            await lease.VerifyHeldAsync(
                requireSourceLocks: false,
                ct);
            report = await lease.ExecuteTransactionAsync(
                "plan-inspection",
                requireSourceLocks: true,
                (_, _, token) => BuildPlanAsync(
                    manifest,
                    manifestDigest,
                    token),
                IsolationLevel.RepeatableRead,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            report = new MaxScoreMaintenancePlanReport(
                MaxScoreMaintenancePlanReport.CurrentReportVersion,
                CanApply: false,
                manifestDigest,
                PlanDigest: new string('0', 64),
                manifest.ExpectedPublishedScrapeId,
                manifest.ExpectedPublicationId,
                manifest.CatalogContentHash,
                PublishedScoreSourceFingerprint: new string('0', 64),
                NotificationStateFingerprint: new string('0', 64),
                RankHistoryFingerprint: new string('0', 64),
                AffectedInstruments: manifest.Songs
                    .SelectMany(song => song.ChangedInstruments)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(instrument => instrument, StringComparer.Ordinal)
                    .ToArray(),
                RoutineCandidateCount: -1,
                Checks:
                [
                    new MaxScoreMaintenancePlanCheck(
                        "plan",
                        Passed: false,
                        BoundDetail(ex.Message)),
                ],
                RoutineCandidates: []);
        }

        await MaxScoreMaintenanceFileStore.WriteNewReportAsync(
            _options.DataDirectory,
            reportOutputPath,
            report,
            ct);
        return report;
    }

    public async Task<MaxScoreMaintenanceApplyReport> ApplyOrResumeAsync(
        bool resume,
        long expectedPublishedScrapeId,
        string manifestPath,
        string expectedManifestDigest,
        string expectedPlanDigest,
        string? rollbackOutputPath,
        string reportOutputPath,
        CancellationToken ct)
    {
        var manifest =
            await MaxScoreMaintenanceFileStore.LoadManifestAsync(
                _options.DataDirectory,
                manifestPath,
                ct);
        var manifestDigest = manifest.ComputeDigest();
        ValidateManifestCliIdentity(
            manifest,
            manifestDigest,
            expectedPublishedScrapeId,
            expectedManifestDigest);
        var normalizedPlanDigest =
            MaxScoreMaintenanceManifest.NormalizeSha256(
                expectedPlanDigest,
                nameof(expectedPlanDigest));

        MaxScoreMaintenanceApplyReport report;
        IMaxScoreMaintenanceLease? activeLease = null;
        try
        {
            activeLease =
                await _metaDatabase
                    .AcquireMaxScoreMaintenanceLeaseAsync(
                        manifest.ExpectedPublicationId,
                        ct);
            var lease = activeLease;
            await lease.VerifyHeldAsync(
                requireSourceLocks: false,
                ct);
            var run = await LoadRunAsync(manifestDigest, ct);
            if (run?.Phase == MaxScoreMaintenancePhase.Completed)
            {
                report = ToApplyReport(
                    run,
                    succeeded: true,
                    resumable: false,
                    publicReadsFrozen: false);
            }
            else
            {
                if (resume && run is null)
                {
                    throw new InvalidOperationException(
                        "Resume requires an existing digest-owned maintenance run.");
                }
                if (!resume && run is not null)
                {
                    throw new InvalidOperationException(
                        "An incomplete maintenance run already exists; use the resume command.");
                }

                if (run is null)
                {
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: false,
                        ct);
                    var plan =
                        await lease.ExecuteTransactionAsync(
                            "apply-plan-validation",
                            requireSourceLocks: true,
                            (_, _, token) => BuildPlanAsync(
                                manifest,
                                manifestDigest,
                                token),
                            IsolationLevel.RepeatableRead,
                            ct);
                    if (!plan.CanApply
                        || !string.Equals(
                            plan.PlanDigest,
                            normalizedPlanDigest,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Plan digest changed from expected {normalizedPlanDigest} to {plan.PlanDigest}; no freeze was created.");
                    }
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: false,
                        ct);
                    await EstablishFreezeAsync(
                        lease,
                        manifest,
                        manifestDigest,
                        plan,
                        ct);
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    RequireOwnedFreeze(
                        manifest,
                        manifestDigest);
                    run = await LoadRequiredRunAsync(
                        manifestDigest,
                        ct);
                }
                else
                {
                    ValidateRunIdentity(
                        run,
                        manifest,
                        normalizedPlanDigest);
                    RequireOwnedFreeze(
                        manifest,
                        manifestDigest);
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    RequireOwnedFreeze(
                        manifest,
                        manifestDigest);
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    await MarkRunRunningAsync(
                        lease,
                        manifestDigest,
                        ct);
                    var resumeInspection =
                        await _notifications.InspectRoutineStateAsync(
                            manifest,
                            manifestDigest,
                            requireOwnedFreeze: true,
                            ct);
                    if (!string.Equals(
                            resumeInspection
                                .PublishedScoreSourceFingerprint,
                            run.PublishedScoreSourceFingerprint,
                            StringComparison.Ordinal)
                        || run.Phase
                           < MaxScoreMaintenancePhase
                               .NotificationsQuarantined
                        && !string.Equals(
                            resumeInspection
                                .NotificationStateFingerprint,
                            run.NotificationStateFingerprint,
                            StringComparison.Ordinal)
                        || run.Phase
                           < MaxScoreMaintenancePhase.PathsPromoted
                        && resumeInspection.CandidateCount != 0
                        || !string.Equals(
                            await ComputeRankHistoryFingerprintAsync(ct),
                            run.RankHistoryFingerprint,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Resume source, notification, candidate, or rank-history identity differs from the persisted checkpoint.");
                    }
                }

                if (run.Phase < MaxScoreMaintenancePhase.RollbackCaptured)
                {
                    var resolvedRollbackPath =
                        rollbackOutputPath ?? run.RollbackSnapshotPath
                        ?? throw new InvalidOperationException(
                            "Resume before rollback capture requires the original rollback output path.");
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    await CaptureRollbackAsync(
                        lease,
                        manifest,
                        manifestDigest,
                        normalizedPlanDigest,
                        resolvedRollbackPath,
                        run.CreatedAtUtc,
                        ct);
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    run = await LoadRequiredRunAsync(
                        manifestDigest,
                        ct);
                }
                else if (rollbackOutputPath is not null
                         && !PathsEquivalent(
                             ResolveDataPath(rollbackOutputPath),
                             run.RollbackSnapshotPath!))
                {
                    throw new InvalidOperationException(
                        "Resume rollback path does not match the persisted rollback evidence path.");
                }

                if (run.Phase < MaxScoreMaintenancePhase.PathsPromoted)
                {
                    if (!ValidatePathPhase(
                            manifest,
                            postPromotion: true,
                            throwOnMismatch: false))
                    {
                        ValidatePathPhase(
                            manifest,
                            postPromotion: false,
                            throwOnMismatch: true);
                        await lease.VerifyHeldAsync(
                            requireSourceLocks: true,
                            ct);
                        var promotionResult =
                            await _pathStore
                                .TryPromoteGenerationsAtomicallyAsync(
                                    manifest.Songs
                                        .Select(CreatePromotion)
                                        .ToArray(),
                                    new PathGenerationBatchPromotionGate(
                                        manifest.ExpectedPublicationId,
                                        manifest.ExpectedPublishedScrapeId,
                                        PublicReadFreezeState
                                            .MaxScoreMaintenanceReasonPrefix
                                        + manifestDigest),
                                    lease,
                                    ct);
                        if (promotionResult.Outcome
                            != PathGenerationPromotionOutcome.Promoted
                            || promotionResult.PromotedCount
                            != manifest.Songs.Count)
                        {
                            throw new InvalidOperationException(
                                $"Atomic path promotion failed at {promotionResult.FailedSongId ?? "unknown"} with {promotionResult.Outcome}.");
                        }
                        _pathStore.InvalidateCachedState();
                        ValidatePathPhase(
                            manifest,
                            postPromotion: true,
                            throwOnMismatch: true);
                    }
                    ValidateWorkerOffline();
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    _instrumentSupportCache
                        .RefreshSongInstrumentSupport();
                    var admittedPairs =
                        GetNewlyAdmittedPathPairs(manifest);
                    var registrationReset =
                        await lease.ExecuteTransactionAsync(
                            "path-admission-reset",
                            requireSourceLocks: true,
                            (connection, transaction, _) =>
                                Task.FromResult(
                                    _metaDatabase
                                        .ResetRegistrationProgressForAdmittedPairs(
                                            admittedPairs,
                                            PublicReadFreezeState
                                                .MaxScoreMaintenanceReasonPrefix
                                            + manifestDigest,
                                            connection,
                                            transaction)),
                            ct: ct);
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    ValidateWorkerOffline();
                    _log.LogInformation(
                        "Refreshed promoted path admission: pairs={PairCount}, negativeBackfillChecksReset={BackfillResetCount}, backfillAccountsRequeued={BackfillAccountCount}, historyChecksReset={HistoryResetCount}, historyAccountsRequeued={HistoryAccountCount}.",
                        admittedPairs.Count,
                        registrationReset
                            .RemovedNegativeBackfillPairChecks,
                        registrationReset
                            .RequeuedBackfillAccountCount,
                        registrationReset.RemovedHistoryPairChecks,
                        registrationReset.RequeuedHistoryAccountCount);
                    await AdvancePhaseAsync(
                        lease,
                        manifestDigest,
                        normalizedPlanDigest,
                        MaxScoreMaintenancePhase.RollbackCaptured,
                        MaxScoreMaintenancePhase.PathsPromoted,
                        promotedSongCount: manifest.Songs.Count,
                        rebuiltInstrumentCount: null,
                        stagedCacheEntryCount: null,
                        ct);
                    run = await LoadRequiredRunAsync(
                        manifestDigest,
                        ct);
                }

                if (run.Phase
                    < MaxScoreMaintenancePhase.DerivedStateRebuilt)
                {
                    ValidateWorkerOffline();
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    var context = LoadPublishedContext(
                        manifest.ExpectedPublishedScrapeId,
                        manifest.ExpectedPublicationId,
                        manifest.Songs
                            .Select(song => song.SongId)
                            .ToArray(),
                        requireUnfrozen: false,
                        requiredFreezeReason:
                            PublicReadFreezeState
                                .MaxScoreMaintenanceReasonPrefix
                            + manifestDigest);
                    ValidateManifestCatalogIdentity(
                        manifest,
                        context.Catalog);
                    var derived = await _derivedState.RebuildAsync(
                        manifest,
                        context.CatalogSongs,
                        lease,
                        ct);
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    ValidateWorkerOffline();
                    await AdvancePhaseAsync(
                        lease,
                        manifestDigest,
                        normalizedPlanDigest,
                        MaxScoreMaintenancePhase.PathsPromoted,
                        MaxScoreMaintenancePhase.DerivedStateRebuilt,
                        promotedSongCount: null,
                        rebuiltInstrumentCount:
                            derived.RebuiltInstrumentCount,
                        stagedCacheEntryCount: null,
                        ct);
                    run = await LoadRequiredRunAsync(
                        manifestDigest,
                        ct);
                }

                if (run.Phase
                    < MaxScoreMaintenancePhase.NotificationsQuarantined)
                {
                    ValidateWorkerOffline();
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    await _notifications.QuarantineAndAlignAsync(
                        manifest,
                        manifestDigest,
                        normalizedPlanDigest,
                        run.PublishedScoreSourceFingerprint,
                        lease,
                        ct);
                    run = await LoadRequiredRunAsync(
                        manifestDigest,
                        ct);
                }

                if (run.Phase < MaxScoreMaintenancePhase.CachesStaged)
                {
                    ValidateWorkerOffline();
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    var stagedCacheEntries =
                        await _precomputer
                            .StageCurrentPublicationCachesForMaintenanceAsync(
                                manifest.ExpectedPublicationId,
                                lease,
                                ct);
                    if (stagedCacheEntries <= 0)
                    {
                        throw new InvalidOperationException(
                            "Maintenance cache staging produced no entries.");
                    }
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    await AdvancePhaseAsync(
                        lease,
                        manifestDigest,
                        normalizedPlanDigest,
                        MaxScoreMaintenancePhase
                            .NotificationsQuarantined,
                        MaxScoreMaintenancePhase.CachesStaged,
                        promotedSongCount: null,
                        rebuiltInstrumentCount: null,
                        stagedCacheEntryCount: stagedCacheEntries,
                        ct);
                    run = await LoadRequiredRunAsync(
                        manifestDigest,
                        ct);
                }

                if (run.Phase < MaxScoreMaintenancePhase.Validated)
                {
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    await ValidateCompletedMaintenanceAsync(
                        manifest,
                        manifestDigest,
                        run,
                        ct);
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    await AdvancePhaseAsync(
                        lease,
                        manifestDigest,
                        normalizedPlanDigest,
                        MaxScoreMaintenancePhase.CachesStaged,
                        MaxScoreMaintenancePhase.Validated,
                        promotedSongCount: null,
                        rebuiltInstrumentCount: null,
                        stagedCacheEntryCount: null,
                        ct);
                    run = await LoadRequiredRunAsync(
                        manifestDigest,
                        ct);
                }

                await lease.CompleteAsync(
                    manifest.ExpectedPublishedScrapeId,
                    manifestDigest,
                    ct);
                _instrumentSupportCache
                    .InvalidateSongInstrumentSupport();
                run = await LoadRequiredRunAsync(
                    manifestDigest,
                    ct);
                report = ToApplyReport(
                    run,
                    succeeded: true,
                    resumable: false,
                    publicReadsFrozen: false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MaxScoreMaintenanceRunState? run = null;
            try
            {
                run = await LoadRunAsync(manifestDigest, ct);
                if (run is not null
                    && run.Phase
                    != MaxScoreMaintenancePhase.Completed)
                {
                    if (activeLease is not null)
                    {
                        await RecordFailureAsync(
                            activeLease,
                            manifestDigest,
                            run.Phase,
                            ex,
                            CancellationToken.None);
                    }
                    run = await LoadRunAsync(
                        manifestDigest,
                        CancellationToken.None);
                }
            }
            catch (Exception checkpointError)
            {
                _log.LogError(
                    checkpointError,
                    "Failed to read or update the max-score resumable checkpoint after {FailureType}.",
                    ex.GetType().Name);
            }

            var ownedFreeze = false;
            try
            {
                RequireOwnedFreeze(manifest, manifestDigest);
                ownedFreeze = true;
            }
            catch
            {
            }
            report = run?.Phase
                == MaxScoreMaintenancePhase.Completed
                ? ToApplyReport(
                    run,
                    succeeded: true,
                    resumable: false,
                    publicReadsFrozen: false)
                : run is null
                ? new MaxScoreMaintenanceApplyReport(
                    MaxScoreMaintenanceApplyReport.CurrentReportVersion,
                    Succeeded: false,
                    Resumable: ownedFreeze,
                    PublicReadsFrozen: ownedFreeze,
                    manifestDigest,
                    normalizedPlanDigest,
                    MaxScoreMaintenancePhase.None,
                    manifest.ExpectedPublishedScrapeId,
                    manifest.ExpectedPublicationId,
                    RollbackSnapshotPath: null,
                    RollbackSnapshotSha256: null,
                    PromotedSongCount: 0,
                    RebuiltInstrumentCount: 0,
                    QuarantinedCandidateCount: 0,
                    VisibleDeliveryCount: 0,
                    StagedCacheEntryCount: 0,
                    FailureStage: ownedFreeze
                        ? "checkpoint_unavailable"
                        : "pre_freeze",
                    Detail: BoundDetail(ex.Message))
                : ToApplyReport(
                    run,
                    succeeded: false,
                    resumable: true,
                    publicReadsFrozen: true);
        }
        finally
        {
            if (activeLease is not null)
                await activeLease.DisposeAsync();
        }

        await MaxScoreMaintenanceFileStore.WriteNewReportAsync(
            _options.DataDirectory,
            reportOutputPath,
            report,
            ct);
        return report;
    }

    private async Task<MaxScoreMaintenancePlanReport> BuildPlanAsync(
        MaxScoreMaintenanceManifest manifest,
        string manifestDigest,
        CancellationToken ct)
    {
        if (_options.EnableAutomaticPathGeneration)
        {
            throw new InvalidOperationException(
                "Max-score maintenance requires automatic path generation to remain disabled.");
        }

        var context = LoadPublishedContext(
            manifest.ExpectedPublishedScrapeId,
            manifest.ExpectedPublicationId,
            manifest.Songs.Select(song => song.SongId).ToArray(),
            requireUnfrozen: true);
        ValidateManifestCatalogIdentity(manifest, context.Catalog);
        ValidateWorkerOffline();
        ValidatePathPhase(
            manifest,
            postPromotion: false,
            throwOnMismatch: true);
        var notificationInspection =
            await _notifications.InspectRoutineStateAsync(
                manifest,
                manifestDigest,
                requireOwnedFreeze: false,
                ct);
        var routineCandidatesClear =
            notificationInspection.CandidateCount == 0;

        var rankHistoryFingerprint =
            await ComputeRankHistoryFingerprintAsync(ct);
        var affectedInstruments = manifest.Songs
            .SelectMany(song => song.ChangedInstruments)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(instrument => instrument, StringComparer.Ordinal)
            .ToArray();
        var planDigest = ComputePlanDigest(
            manifestDigest,
            context,
            notificationInspection,
            rankHistoryFingerprint,
            affectedInstruments);
        return new MaxScoreMaintenancePlanReport(
            MaxScoreMaintenancePlanReport.CurrentReportVersion,
            CanApply: routineCandidatesClear,
            manifestDigest,
            planDigest,
            manifest.ExpectedPublishedScrapeId,
            manifest.ExpectedPublicationId,
            manifest.CatalogContentHash,
            notificationInspection.PublishedScoreSourceFingerprint,
            notificationInspection.NotificationStateFingerprint,
            rankHistoryFingerprint,
            affectedInstruments,
            RoutineCandidateCount:
                notificationInspection.CandidateCount,
            Checks:
            [
                new(
                    "publication",
                    true,
                    $"scrape={manifest.ExpectedPublishedScrapeId}, publication={manifest.ExpectedPublicationId}, no working publication"),
                new(
                    "catalog",
                    true,
                    $"version={manifest.CatalogVersion}, songs={manifest.CatalogSongCount}, hash={manifest.CatalogContentHash}"),
                new(
                    "worker",
                    true,
                    "worker is idle/offline and no scrape is running"),
                new(
                    "path-generation",
                    true,
                    "automatic generation disabled and distributed lease acquired"),
                new(
                    "paths",
                    true,
                    $"{manifest.Songs.Count} current/staged immutable path identities validated"),
                new(
                    "notifications",
                    routineCandidatesClear,
                    routineCandidatesClear
                        ? "completed marker and visible routine lanes current; zero candidates"
                        : $"completed marker is current but {notificationInspection.CandidateCount:N0} routine candidate(s) remain"),
                new(
                    "rank-history",
                    true,
                    $"baseline fingerprint {rankHistoryFingerprint}"),
            ],
            RoutineCandidates: notificationInspection.Candidates);
    }

    private PublishedContext LoadPublishedContext(
        long expectedPublishedScrapeId,
        long? expectedPublicationId,
        IReadOnlyCollection<string> requiredSongIds,
        bool requireUnfrozen,
        string? requiredFreezeReason = null)
    {
        var pointers = _metaDatabase.GetPublicationPointerState();
        if (pointers.PublishedScrapeId
                != expectedPublishedScrapeId
            || pointers.CurrentPublicationId is null
            || expectedPublicationId.HasValue
               && pointers.CurrentPublicationId
                   != expectedPublicationId.Value
            || pointers.WorkingPublicationId.HasValue)
        {
            throw new InvalidOperationException(
                "Max-score maintenance requires the exact current publication and no working publication.");
        }
        var generation = _metaDatabase.GetPublicationGeneration(
            pointers.CurrentPublicationId.Value);
        if (generation is null
            || generation.ScrapeId != expectedPublishedScrapeId
            || !string.Equals(
                generation.Status,
                PublicationGenerationStatus.Current,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Current publication generation identity is invalid.");
        }
        var publishedRun = _metaDatabase.GetPublishedScrapeRun();
        if (publishedRun is null
            || publishedRun.Id != expectedPublishedScrapeId
            || publishedRun.CompletedAt is null
            || !string.Equals(
                publishedRun.Status,
                "completed",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Expected published scrape is not completed.");
        }
        var freeze = _metaDatabase.GetPublicReadFreezeState();
        if (requireUnfrozen && freeze.IsFrozen)
        {
            throw new InvalidOperationException(
                "Max-score maintenance planning/staging requires unfrozen reads.");
        }
        if (requiredFreezeReason is not null
            && (!freeze.IsFrozen
                || freeze.ScrapeId != expectedPublishedScrapeId
                || !string.Equals(
                    freeze.Reason,
                    requiredFreezeReason,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Max-score maintenance freeze ownership changed.");
        }

        var catalog =
            _metaDatabase.GetPublicationSongCatalogForScrape(
                expectedPublishedScrapeId)
            ?? throw new InvalidOperationException(
                "Current publication has no exact bound catalog.");
        if (catalog.PublicationId
            != pointers.CurrentPublicationId.Value
            || catalog.SchemaVersion
                != SongCatalogSnapshotBuilder.SchemaVersion)
        {
            throw new InvalidOperationException(
                "Current publication catalog binding is invalid.");
        }
        var songs = SongCatalogSnapshotBuilder.DeserializeCatalog(
                catalog.CatalogJson)
            .OrderBy(song => song.track!.su, StringComparer.Ordinal)
            .ToArray();
        var rebuilt = SongCatalogSnapshotBuilder.Create(songs);
        if (songs.Length != catalog.SongCount
            || rebuilt.SongCount != catalog.SongCount
            || !string.Equals(
                rebuilt.ContentHash,
                catalog.ContentHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Current publication catalog content hash/count is invalid.");
        }

        var songsById = songs.ToDictionary(
            song => song.track!.su,
            StringComparer.Ordinal);
        var lastModifiedBySong = new Dictionary<string, string>(
            StringComparer.Ordinal);
        foreach (var songId in requiredSongIds)
        {
            if (!songsById.TryGetValue(songId, out var song)
                || !TryGetExactProviderLastModified(
                    song,
                    out var lastModified))
            {
                throw new InvalidOperationException(
                    $"Exact published catalog is missing song {songId} or its provider timestamp.");
            }
            lastModifiedBySong[songId] =
                ProviderTimestampIdentity.NormalizeRequired(
                    lastModified!,
                    nameof(lastModified));
        }

        return new PublishedContext(
            pointers,
            catalog,
            songs,
            songsById,
            lastModifiedBySong);
    }

    private PathGenerationState RequireCurrentPathState(
        string songId,
        string expectedSongId,
        string expectedCatalogLastModified)
    {
        var state = _pathStore.GetPathGenerationState(songId)
            ?? throw new InvalidOperationException(
                $"Path state is missing for {songId}.");
        if (!string.Equals(
                state.SongId,
                expectedSongId,
                StringComparison.Ordinal)
            || !ProviderTimestampIdentity.Equivalent(
                state.CatalogLastModified,
                expectedCatalogLastModified))
        {
            throw new InvalidOperationException(
                $"Path/catalog identity mismatch for {songId}.");
        }
        return state;
    }

    private bool ValidatePathPhase(
        MaxScoreMaintenanceManifest manifest,
        bool postPromotion,
        bool throwOnMismatch)
    {
        try
        {
            foreach (var song in manifest.Songs)
            {
                var state = RequireCurrentPathState(
                    song.SongId,
                    song.SongId,
                    song.ExpectedCatalogLastModified);
                var expected = postPromotion
                    ? song.StagedPath with
                    {
                        Revision = checked(
                            song.CurrentPath.Revision + 1),
                    }
                    : song.CurrentPath;
                if (!PathIdentityMatches(state, expected))
                {
                    throw new InvalidOperationException(
                        $"{(postPromotion ? "Post-promotion" : "Current")} path identity mismatch for {song.SongId}.");
                }

                var validated =
                    PathArtifactResolver.ValidateImmutableGeneration(
                        _options.DataDirectory,
                        song.SongId,
                        song.StagedPath.ArtifactGenerationId!);
                ValidateManifestStagedGeneration(song, validated);
                if (!postPromotion
                    && string.Equals(
                        state.ArtifactGenerationId,
                        song.StagedPath.ArtifactGenerationId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Staged generation for {song.SongId} is already active.");
                }
            }
            return true;
        }
        catch when (!throwOnMismatch)
        {
            return false;
        }
    }

    private void ValidateWorkerOffline()
    {
        var runtime = _metaDatabase.GetServiceRuntimeState(
            WorkerStatusPublisher.ScraperWorkerKey,
            commandTimeoutSeconds: 30);
        if (runtime.LatestScrape is
            {
                Status: "running",
            })
        {
            throw new InvalidOperationException(
                "Max-score maintenance is blocked by a running scrape.");
        }
        if (runtime.CurrentPhaseAttempt is not null)
        {
            throw new InvalidOperationException(
                "Max-score maintenance is blocked by an active worker phase attempt.");
        }
        var worker = runtime.WorkerStatus;
        var offline = worker is null
            || worker.Status.Equals(
                "offline",
                StringComparison.OrdinalIgnoreCase)
            || worker.Status.Equals(
                "stale",
                StringComparison.OrdinalIgnoreCase)
            || worker.LastHeartbeatAtUtc is { } heartbeat
               && DateTime.UtcNow - heartbeat
                   > WorkerHeartbeatStaleAfter;
        if (!offline)
        {
            throw new InvalidOperationException(
                $"Max-score maintenance requires the worker offline; status={worker!.Status}.");
        }
    }

    private async Task EstablishFreezeAsync(
        IMaxScoreMaintenanceLease lease,
        MaxScoreMaintenanceManifest manifest,
        string manifestDigest,
        MaxScoreMaintenancePlanReport plan,
        CancellationToken ct)
    {
        var freezeReason =
            PublicReadFreezeState.MaxScoreMaintenanceReasonPrefix
            + manifestDigest;
        await lease.ExecuteTransactionAsync(
            "freeze-establishment",
            requireSourceLocks: false,
            async (conn, tx, token) =>
            {
        await ConfigureShortTransactionAsync(conn, tx, token);
        await using (var state = conn.CreateCommand())
        {
            state.Transaction = tx;
            state.CommandText = """
                SELECT current_publication_id,
                       working_publication_id,
                       published_scrape_id,
                       public_reads_frozen,
                       improvement_notifications_scrape_id,
                       improvement_notifications_status,
                       EXISTS (
                           SELECT 1
                           FROM scrape_log scrape
                           WHERE scrape.status = 'running'
                       )
                FROM scrape_publication_state
                WHERE id = TRUE
                FOR UPDATE
                """;
            await using var reader = await state.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)
                || reader.IsDBNull(0)
                || reader.GetInt64(0)
                    != manifest.ExpectedPublicationId
                || !reader.IsDBNull(1)
                || reader.IsDBNull(2)
                || Convert.ToInt64(reader.GetValue(2))
                    != manifest.ExpectedPublishedScrapeId
                || reader.GetBoolean(3)
                || reader.IsDBNull(4)
                || Convert.ToInt64(reader.GetValue(4))
                    != manifest.ExpectedPublishedScrapeId
                || reader.IsDBNull(5)
                || !string.Equals(
                    reader.GetString(5),
                    "completed",
                    StringComparison.Ordinal)
                || reader.GetBoolean(6))
            {
                throw new InvalidOperationException(
                    "Publication state changed before maintenance freeze establishment.");
            }
        }

        await using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO max_score_maintenance_runs (
                    manifest_sha256,
                    manifest_version,
                    plan_digest,
                    expected_published_scrape_id,
                    expected_publication_id,
                    expected_catalog_hash,
                    expected_catalog_song_count,
                    published_score_source_fingerprint,
                    notification_state_fingerprint,
                    rank_history_fingerprint,
                    manifest_json,
                    freeze_reason,
                    phase,
                    status,
                    visible_delivery_count)
                VALUES (
                    @manifestSha256,
                    @manifestVersion,
                    @planDigest,
                    @publishedScrapeId,
                    @publicationId,
                    @catalogHash,
                    @catalogSongCount,
                    @scoreFingerprint,
                    @notificationFingerprint,
                    @rankHistoryFingerprint,
                    @manifest,
                    @freezeReason,
                    'freeze_established',
                    'running',
                    0)
                """;
            insert.Parameters.AddWithValue(
                "manifestSha256",
                manifestDigest);
            insert.Parameters.AddWithValue(
                "manifestVersion",
                manifest.ManifestVersion);
            insert.Parameters.AddWithValue(
                "planDigest",
                plan.PlanDigest);
            insert.Parameters.AddWithValue(
                "publishedScrapeId",
                manifest.ExpectedPublishedScrapeId);
            insert.Parameters.AddWithValue(
                "publicationId",
                manifest.ExpectedPublicationId);
            insert.Parameters.AddWithValue(
                "catalogHash",
                manifest.CatalogContentHash);
            insert.Parameters.AddWithValue(
                "catalogSongCount",
                manifest.CatalogSongCount);
            insert.Parameters.AddWithValue(
                "scoreFingerprint",
                plan.PublishedScoreSourceFingerprint);
            insert.Parameters.AddWithValue(
                "notificationFingerprint",
                plan.NotificationStateFingerprint);
            insert.Parameters.AddWithValue(
                "rankHistoryFingerprint",
                plan.RankHistoryFingerprint);
            insert.Parameters.Add("manifest", NpgsqlDbType.Jsonb).Value =
                Encoding.UTF8.GetString(manifest.SerializeCanonical());
            insert.Parameters.AddWithValue(
                "freezeReason",
                freezeReason);
            await insert.ExecuteNonQueryAsync(token);
        }

        await using (var freeze = conn.CreateCommand())
        {
            freeze.Transaction = tx;
            freeze.CommandText = """
                UPDATE scrape_publication_state
                SET public_reads_frozen = TRUE,
                    public_reads_frozen_at = now(),
                    public_reads_frozen_scrape_id =
                        @publishedScrapeId,
                    public_reads_frozen_reason = @freezeReason,
                    updated_at = now()
                WHERE id = TRUE
                  AND current_publication_id = @publicationId
                  AND working_publication_id IS NULL
                  AND published_scrape_id = @publishedScrapeId
                  AND NOT public_reads_frozen
                """;
            freeze.Parameters.AddWithValue(
                "publishedScrapeId",
                manifest.ExpectedPublishedScrapeId);
            freeze.Parameters.AddWithValue(
                "publicationId",
                manifest.ExpectedPublicationId);
            freeze.Parameters.AddWithValue(
                "freezeReason",
                freezeReason);
            if (await freeze.ExecuteNonQueryAsync(token) != 1)
            {
                throw new InvalidOperationException(
                    "Digest-owned maintenance freeze was not established.");
            }
        }
            },
            IsolationLevel.Serializable,
            ct);
    }

    private async Task CaptureRollbackAsync(
        IMaxScoreMaintenanceLease lease,
        MaxScoreMaintenanceManifest manifest,
        string manifestDigest,
        string planDigest,
        string rollbackOutputPath,
        DateTime runCreatedAtUtc,
        CancellationToken ct)
    {
        await lease.VerifyHeldAsync(
            requireSourceLocks: true,
            ct);
        ValidatePathPhase(
            manifest,
            postPromotion: false,
            throwOnMismatch: true);
        var snapshot = CreateRollbackSnapshot(
            manifest,
            manifestDigest,
            planDigest,
            runCreatedAtUtc);
        var written = await MaxScoreMaintenanceFileStore
            .WriteCanonicalRollbackSnapshotAsync(
                _options.DataDirectory,
                rollbackOutputPath,
                snapshot,
                ct);

        await lease.ExecuteTransactionAsync(
            "rollback-checkpoint",
            requireSourceLocks: true,
            async (conn, tx, token) =>
            {
        await ConfigureShortTransactionAsync(conn, tx, token);
        await LockOwnedFreezeRowAsync(
            conn,
            tx,
            manifest,
            manifestDigest,
            token);
        foreach (var song in snapshot.Songs)
        {
            await using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO max_score_maintenance_rollback_songs (
                    manifest_sha256,
                    song_id,
                    expected_catalog_last_modified,
                    path_generation_revision,
                    dat_file_hash,
                    song_last_modified,
                    paths_generated_at,
                    chopt_version,
                    chopt_binary_sha256,
                    path_generation_profile,
                    path_artifact_generation_id,
                    path_expected_instruments,
                    max_lead_score,
                    max_bass_score,
                    max_drums_score,
                    max_vocals_score,
                    max_pro_lead_score,
                    max_pro_bass_score,
                    max_pro_cymbals_score,
                    max_pro_drums_score,
                    path_generation_pending)
                VALUES (
                    @manifestSha256,
                    @songId,
                    @catalogLastModified,
                    @revision,
                    @datHash,
                    @songLastModified,
                    @generatedAt,
                    @choptVersion,
                    @binaryHash,
                    @profile,
                    @generationId,
                    @expectedInstruments,
                    @lead,
                    @bass,
                    @drums,
                    @vocals,
                    @proLead,
                    @proBass,
                    @proCymbals,
                    @proDrums,
                    @pending)
                ON CONFLICT (manifest_sha256, song_id) DO NOTHING
                """;
            AddRollbackParameters(
                insert,
                manifestDigest,
                song);
            await insert.ExecuteNonQueryAsync(token);
        }

        await using (var advance = conn.CreateCommand())
        {
            advance.Transaction = tx;
            advance.CommandText = """
                UPDATE max_score_maintenance_runs
                SET rollback_snapshot_path = @rollbackPath,
                    rollback_snapshot_sha256 = @rollbackSha256,
                    phase = 'rollback_captured',
                    status = 'running',
                    failure_stage = NULL,
                    failure_detail = NULL,
                    updated_at = now()
                WHERE manifest_sha256 = @manifestSha256
                  AND plan_digest = @planDigest
                  AND phase IN (
                      'freeze_established',
                      'rollback_captured')
                """;
            advance.Parameters.AddWithValue(
                "rollbackPath",
                written.FullPath);
            advance.Parameters.AddWithValue(
                "rollbackSha256",
                written.Sha256);
            advance.Parameters.AddWithValue(
                "manifestSha256",
                manifestDigest);
            advance.Parameters.AddWithValue(
                "planDigest",
                planDigest);
            if (await advance.ExecuteNonQueryAsync(token) != 1)
            {
                throw new InvalidOperationException(
                    "Rollback capture phase identity changed.");
            }
        }
        await ValidateRollbackRowsAsync(
            conn,
            tx,
            manifestDigest,
            snapshot.Songs,
            token);
            },
            IsolationLevel.Serializable,
            ct);
    }

    internal static MaxScoreMaintenanceRollbackSnapshot
        CreateRollbackSnapshot(
            MaxScoreMaintenanceManifest manifest,
            string manifestDigest,
            string planDigest,
            DateTime runCreatedAtUtc)
        => new MaxScoreMaintenanceRollbackSnapshot(
            MaxScoreMaintenanceRollbackSnapshot.CurrentSnapshotVersion,
            NormalizeDatabaseTimestamp(runCreatedAtUtc),
            manifestDigest,
            planDigest,
            manifest.ExpectedPublishedScrapeId,
            manifest.ExpectedPublicationId,
            manifest.CatalogContentHash,
            manifest.Songs
                .Select(song =>
                    new MaxScoreMaintenanceRollbackSong(
                        song.SongId,
                        song.ExpectedCatalogLastModified,
                        song.CurrentPath))
                .ToArray())
            .ValidateAndNormalize();

    private async Task ValidateCompletedMaintenanceAsync(
        MaxScoreMaintenanceManifest manifest,
        string manifestDigest,
        MaxScoreMaintenanceRunState run,
        CancellationToken ct)
    {
        RequireOwnedFreeze(manifest, manifestDigest);
        ValidatePathPhase(
            manifest,
            postPromotion: true,
            throwOnMismatch: true);
        var rankHistoryFingerprint =
            await ComputeRankHistoryFingerprintAsync(ct);
        if (!string.Equals(
                rankHistoryFingerprint,
                run.RankHistoryFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Rank history changed during max-score maintenance.");
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 600;
        cmd.CommandText = """
            WITH expected(song_id, instrument, max_score) AS (
                SELECT *
                FROM unnest(
                    @songIds::TEXT[],
                    @instruments::TEXT[],
                    @maxScores::INTEGER[])
            ), stats AS (
                SELECT COUNT(*)::INTEGER AS matched
                FROM expected
                JOIN song_stats current
                  ON current.song_id = expected.song_id
                 AND current.instrument = expected.instrument
                 AND current.max_score = expected.max_score
            ), rankings AS (
                SELECT COUNT(*)::BIGINT AS row_count
                FROM account_rankings
                WHERE instrument = ANY(@affectedInstruments)
            ), rollback AS (
                SELECT COUNT(*)::INTEGER AS row_count
                FROM max_score_maintenance_rollback_songs
                WHERE manifest_sha256 = @manifestSha256
            ), notifications AS (
                SELECT COUNT(*)::INTEGER AS row_count,
                       COALESCE(SUM(visible_delivery_count), 0)::INTEGER
                           AS visible_count
                FROM improvement_notification_maintenance_runs
                WHERE maintenance_run_id =
                    @notificationMaintenanceRunId
                  AND notification_purpose = @purpose
                  AND delivery_state = 'quarantined'
            ), staged_cache AS (
                SELECT COUNT(*)::BIGINT AS row_count
                FROM publication_api_response_cache_staging
                WHERE publication_id = @publicationId
            ), aggregate_rankings AS (
                SELECT
                    (SELECT COUNT(*) FROM composite_rankings)
                        AS composite_rows,
                    (SELECT COUNT(*) FROM solo_family_rankings)
                        AS family_rows,
                    (SELECT COUNT(*) FROM combo_leaderboard)
                        AS combo_rows
            ), affected_accounts AS (
                SELECT DISTINCT current.account_id
                FROM current_leaderboard_entries current
                JOIN expected
                  ON expected.song_id = current.song_id
                 AND expected.instrument = current.instrument
            ), missing_player_stats AS (
                SELECT COUNT(*)::BIGINT AS row_count
                FROM affected_accounts affected
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM player_stats_tiers stats
                    WHERE stats.account_id = affected.account_id
                )
            ), missing_leaderboard_rivals AS (
                SELECT COUNT(*)::BIGINT AS row_count
                FROM (
                    SELECT DISTINCT account_id
                    FROM registered_users
                ) registered
                CROSS JOIN unnest(@allInstruments::TEXT[])
                    instrument(value)
                CROSS JOIN unnest(@rankMethods::TEXT[])
                    method(value)
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM leaderboard_rivals_state state
                    WHERE state.user_id = registered.account_id
                      AND state.instrument = instrument.value
                      AND state.rank_method = method.value
                )
            ), invalid_band_rankings AS (
                SELECT COUNT(*)::BIGINT AS row_count
                FROM (
                    SELECT
                        EXISTS (
                            SELECT 1
                            FROM band_entries
                            WHERE band_type = 'Band_Duets'
                              AND NOT is_over_threshold
                        ) AS has_source,
                        EXISTS (
                            SELECT 1
                            FROM band_team_rankings_current_band_duets
                        ) AS has_rankings
                    UNION ALL
                    SELECT
                        EXISTS (
                            SELECT 1
                            FROM band_entries
                            WHERE band_type = 'Band_Trios'
                              AND NOT is_over_threshold
                        ),
                        EXISTS (
                            SELECT 1
                            FROM band_team_rankings_current_band_trios
                        )
                    UNION ALL
                    SELECT
                        EXISTS (
                            SELECT 1
                            FROM band_entries
                            WHERE band_type = 'Band_Quad'
                              AND NOT is_over_threshold
                        ),
                        EXISTS (
                            SELECT 1
                            FROM band_team_rankings_current_band_quad
                        )
                ) band
                WHERE band.has_source
                  AND NOT band.has_rankings
            )
            SELECT stats.matched,
                   rankings.row_count,
                   rollback.row_count,
                   notifications.row_count,
                   notifications.visible_count,
                   staged_cache.row_count,
                   aggregate_rankings.composite_rows,
                   aggregate_rankings.family_rows,
                   aggregate_rankings.combo_rows,
                   missing_player_stats.row_count,
                   missing_leaderboard_rivals.row_count,
                   invalid_band_rankings.row_count
            FROM stats,
                 rankings,
                 rollback,
                 notifications,
                 staged_cache,
                 aggregate_rankings,
                 missing_player_stats,
                 missing_leaderboard_rivals,
                 invalid_band_rankings
            """;
        var changedPairs = manifest.Songs
            .SelectMany(song => song.ChangedInstruments.Select(
                instrument => (
                    song.SongId,
                    Instrument: instrument,
                    MaxScore: song.StagedPath.Maxima
                        .GetByInstrument(instrument)!.Value)))
            .ToArray();
        cmd.Parameters.Add(
            "songIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            changedPairs.Select(pair => pair.SongId).ToArray();
        cmd.Parameters.Add(
            "instruments",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            changedPairs.Select(pair => pair.Instrument).ToArray();
        cmd.Parameters.Add(
            "maxScores",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            changedPairs.Select(pair => pair.MaxScore).ToArray();
        cmd.Parameters.Add(
            "affectedInstruments",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            changedPairs.Select(pair => pair.Instrument)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        cmd.Parameters.AddWithValue(
            "manifestSha256",
            manifestDigest);
        cmd.Parameters.AddWithValue(
            "notificationMaintenanceRunId",
            run.NotificationMaintenanceRunId
            ?? throw new InvalidOperationException(
                "Notification maintenance audit run is missing."));
        cmd.Parameters.AddWithValue(
            "purpose",
            MaxScoreMaintenanceSchema.Purpose);
        cmd.Parameters.AddWithValue(
            "publicationId",
            manifest.ExpectedPublicationId);
        cmd.Parameters.Add(
            "allInstruments",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            GlobalLeaderboardScraper.AllInstruments.ToArray();
        cmd.Parameters.Add(
            "rankMethods",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            LeaderboardRivalsCalculator.RankMethods;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)
            || reader.GetInt32(0) != changedPairs.Length
            || reader.GetInt64(1) <= 0
            || reader.GetInt32(2) != manifest.Songs.Count
            || reader.GetInt32(3) != 1
            || reader.GetInt32(4) != 0
            || reader.GetInt64(5) != run.StagedCacheEntryCount
            || reader.GetInt64(6) <= 0
            || reader.GetInt64(7) <= 0
            || reader.GetInt64(8) <= 0
            || reader.GetInt64(9) != 0
            || reader.GetInt64(10) != 0
            || reader.GetInt64(11) != 0)
        {
            throw new InvalidOperationException(
                "Post-maintenance paths, song stats, rankings, rollback, notification, or cache validation failed.");
        }
    }

    private async Task<string> ComputeRankHistoryFingerprintAsync(
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 60;
        cmd.CommandText = """
            WITH cleared AS MATERIALIZED (
                SELECT pg_stat_clear_snapshot()
            )
            SELECT encode(
                digest(
                    COALESCE(
                        string_agg(
                            concat_ws(
                                ':',
                                stats.relname,
                                stats.n_tup_ins,
                                stats.n_tup_upd,
                                stats.n_tup_del,
                                database.stats_reset),
                            '|' ORDER BY stats.relname),
                        ''),
                    'sha256'),
                'hex')
            FROM pg_stat_all_tables stats
            JOIN pg_stat_database database
              ON database.datname = current_database()
            CROSS JOIN cleared
            WHERE stats.schemaname = 'public'
              AND (
                  stats.relname = 'rank_history'
                  OR stats.relname LIKE 'rank_history_%'
                  OR stats.relname = 'composite_rank_history'
                  OR stats.relname LIKE 'band_team_rank_history%'
                  OR stats.relname LIKE 'band_rank_history_%'
              )
            """;
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct))
            ?? throw new InvalidOperationException(
                "Rank-history fingerprint was unavailable.");
    }

    private static string ComputePlanDigest(
        string manifestDigest,
        PublishedContext context,
        MaxScoreMaintenanceNotificationInspection notifications,
        string rankHistoryFingerprint,
        IReadOnlyList<string> affectedInstruments)
    {
        var canonical = JsonSerializer.Serialize(
            new
            {
                contractVersion = 1,
                manifestDigest,
                publishedScrapeId =
                    context.Pointers.PublishedScrapeId,
                publicationId =
                    context.Pointers.CurrentPublicationId,
                catalogVersion = context.Catalog.CatalogVersion,
                catalogSchemaVersion = context.Catalog.SchemaVersion,
                catalogContentHash = context.Catalog.ContentHash,
                catalogSongCount = context.Catalog.SongCount,
                notifications.PublishedScoreSourceFingerprint,
                notifications.NotificationStateFingerprint,
                rankHistoryFingerprint,
                affectedInstruments,
                routineCandidates = notifications.Candidates,
            },
            MaxScoreMaintenanceJson.Canonical);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task<MaxScoreMaintenanceRunState?> LoadRunAsync(
        string manifestDigest,
        CancellationToken ct)
    {
        try
        {
            await using var conn =
                await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT manifest_sha256,
                       plan_digest,
                       expected_published_scrape_id,
                       expected_publication_id,
                       expected_catalog_hash,
                       expected_catalog_song_count,
                       published_score_source_fingerprint,
                       notification_state_fingerprint,
                       rank_history_fingerprint,
                       freeze_reason,
                       phase,
                       status,
                       rollback_snapshot_path,
                       rollback_snapshot_sha256,
                       notification_maintenance_run_id,
                       promoted_song_count,
                       rebuilt_instrument_count,
                       quarantined_candidate_count,
                       visible_delivery_count,
                       staged_cache_entry_count,
                       failure_stage,
                       failure_detail,
                       created_at
                FROM max_score_maintenance_runs
                WHERE manifest_sha256 = @manifestSha256
                """;
            cmd.Parameters.AddWithValue(
                "manifestSha256",
                manifestDigest);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;
            return ReadRun(reader);
        }
        catch (PostgresException ex) when (ex.SqlState
            is PostgresErrorCodes.UndefinedTable
            or PostgresErrorCodes.UndefinedColumn)
        {
            throw new InvalidOperationException(
                "Max-score maintenance requires current release schema. Run --initialize-schema-only first.",
                ex);
        }
    }

    private async Task<MaxScoreMaintenanceRunState>
        LoadRequiredRunAsync(
            string manifestDigest,
            CancellationToken ct)
        => await LoadRunAsync(manifestDigest, ct)
           ?? throw new InvalidOperationException(
               "Digest-owned max-score maintenance run is missing.");

    private static MaxScoreMaintenanceRunState ReadRun(
        NpgsqlDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            ParsePhase(reader.GetString(10)),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetInt64(14),
            reader.GetInt32(15),
            reader.GetInt32(16),
            reader.GetInt64(17),
            reader.GetInt32(18),
            reader.GetInt64(19),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.IsDBNull(21) ? null : reader.GetString(21),
            reader.GetDateTime(22));

    private async Task AdvancePhaseAsync(
        IMaxScoreMaintenanceLease lease,
        string manifestDigest,
        string planDigest,
        MaxScoreMaintenancePhase expectedPhase,
        MaxScoreMaintenancePhase nextPhase,
        int? promotedSongCount,
        int? rebuiltInstrumentCount,
        long? stagedCacheEntryCount,
        CancellationToken ct)
    {
        await lease.ExecuteTransactionAsync(
            $"phase-checkpoint:{FormatPhase(nextPhase)}",
            requireSourceLocks: true,
            async (conn, tx, token) =>
            {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE max_score_maintenance_runs
            SET phase = @nextPhase,
                status = 'running',
                promoted_song_count = COALESCE(
                    @promotedSongCount,
                    promoted_song_count),
                rebuilt_instrument_count = COALESCE(
                    @rebuiltInstrumentCount,
                    rebuilt_instrument_count),
                staged_cache_entry_count = COALESCE(
                    @stagedCacheEntryCount,
                    staged_cache_entry_count),
                failure_stage = NULL,
                failure_detail = NULL,
                updated_at = now()
            WHERE manifest_sha256 = @manifestSha256
              AND plan_digest = @planDigest
              AND phase = @expectedPhase
              AND status IN ('running', 'failed')
            """;
        cmd.Parameters.AddWithValue(
            "nextPhase",
            FormatPhase(nextPhase));
        cmd.Parameters.Add(
            "promotedSongCount",
            NpgsqlDbType.Integer).Value =
            (object?)promotedSongCount ?? DBNull.Value;
        cmd.Parameters.Add(
            "rebuiltInstrumentCount",
            NpgsqlDbType.Integer).Value =
            (object?)rebuiltInstrumentCount ?? DBNull.Value;
        cmd.Parameters.Add(
            "stagedCacheEntryCount",
            NpgsqlDbType.Bigint).Value =
            (object?)stagedCacheEntryCount ?? DBNull.Value;
        cmd.Parameters.AddWithValue(
            "manifestSha256",
            manifestDigest);
        cmd.Parameters.AddWithValue("planDigest", planDigest);
        cmd.Parameters.AddWithValue(
            "expectedPhase",
            FormatPhase(expectedPhase));
        if (await cmd.ExecuteNonQueryAsync(token) != 1)
        {
            throw new InvalidOperationException(
                $"Max-score phase changed before {nextPhase} checkpoint.");
        }
            },
            ct: ct);
    }

    private async Task MarkRunRunningAsync(
        IMaxScoreMaintenanceLease lease,
        string manifestDigest,
        CancellationToken ct)
    {
        await lease.ExecuteTransactionAsync(
            "resume-running-checkpoint",
            requireSourceLocks: true,
            async (conn, tx, token) =>
            {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE max_score_maintenance_runs
            SET status = 'running',
                failure_stage = NULL,
                failure_detail = NULL,
                updated_at = now()
            WHERE manifest_sha256 = @manifestSha256
              AND phase <> 'completed'
            """;
        cmd.Parameters.AddWithValue(
            "manifestSha256",
            manifestDigest);
        if (await cmd.ExecuteNonQueryAsync(token) != 1)
        {
            throw new InvalidOperationException(
                "Resumable max-score maintenance run was not found.");
        }
            },
            ct: ct);
    }

    private async Task RecordFailureAsync(
        IMaxScoreMaintenanceLease lease,
        string manifestDigest,
        MaxScoreMaintenancePhase phase,
        Exception error,
        CancellationToken ct)
    {
        await lease.ExecuteTransactionAsync(
            "failure-checkpoint",
            requireSourceLocks: true,
            async (conn, tx, token) =>
            {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE max_score_maintenance_runs
            SET status = 'failed',
                failure_stage = @failureStage,
                failure_detail = @failureDetail,
                updated_at = now()
            WHERE manifest_sha256 = @manifestSha256
              AND phase <> 'completed'
            """;
        cmd.Parameters.AddWithValue(
            "failureStage",
            FormatPhase(phase));
        cmd.Parameters.AddWithValue(
            "failureDetail",
            BoundDetail(error.Message));
        cmd.Parameters.AddWithValue(
            "manifestSha256",
            manifestDigest);
        await cmd.ExecuteNonQueryAsync(token);
            },
            ct: ct);
    }

    private void RequireOwnedFreeze(
        MaxScoreMaintenanceManifest manifest,
        string manifestDigest)
    {
        var freeze = _metaDatabase.GetPublicReadFreezeState();
        var expectedReason =
            PublicReadFreezeState.MaxScoreMaintenanceReasonPrefix
            + manifestDigest;
        if (!freeze.IsFrozen
            || freeze.ScrapeId
                != manifest.ExpectedPublishedScrapeId
            || !string.Equals(
                freeze.Reason,
                expectedReason,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Digest-owned maintenance freeze is missing or owned by another operation.");
        }
    }

    private async Task LockOwnedFreezeRowAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        MaxScoreMaintenanceManifest manifest,
        string manifestDigest,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT current_publication_id,
                   working_publication_id,
                   published_scrape_id,
                   public_reads_frozen,
                   public_reads_frozen_scrape_id,
                   public_reads_frozen_reason
            FROM scrape_publication_state
            WHERE id = TRUE
            FOR UPDATE
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)
            || reader.IsDBNull(0)
            || reader.GetInt64(0)
                != manifest.ExpectedPublicationId
            || !reader.IsDBNull(1)
            || reader.IsDBNull(2)
            || Convert.ToInt64(reader.GetValue(2))
                != manifest.ExpectedPublishedScrapeId
            || !reader.GetBoolean(3)
            || reader.IsDBNull(4)
            || Convert.ToInt64(reader.GetValue(4))
                != manifest.ExpectedPublishedScrapeId
            || reader.IsDBNull(5)
            || !string.Equals(
                reader.GetString(5),
                PublicReadFreezeState
                    .MaxScoreMaintenanceReasonPrefix
                + manifestDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Digest-owned maintenance freeze identity changed.");
        }
    }

    private static async Task ConfigureShortTransactionAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '30s';
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddRollbackParameters(
        NpgsqlCommand command,
        string manifestDigest,
        MaxScoreMaintenanceRollbackSong song)
    {
        command.Parameters.AddWithValue(
            "manifestSha256",
            manifestDigest);
        command.Parameters.AddWithValue("songId", song.SongId);
        command.Parameters.AddWithValue(
            "catalogLastModified",
            song.ExpectedCatalogLastModified);
        command.Parameters.AddWithValue(
            "revision",
            song.Path.Revision);
        command.Parameters.AddWithValue(
            "datHash",
            (object?)song.Path.DatFileHash ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "songLastModified",
            (object?)song.Path.SongLastModified ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "generatedAt",
            (object?)song.Path.GeneratedAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "choptVersion",
            (object?)song.Path.ChoptVersion ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "binaryHash",
            (object?)song.Path.ChoptBinarySha256 ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "profile",
            (object?)song.Path.GenerationProfile ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "generationId",
            (object?)song.Path.ArtifactGenerationId ?? DBNull.Value);
        command.Parameters.Add(
            "expectedInstruments",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            song.Path.ExpectedInstruments.ToArray();
        command.Parameters.AddWithValue(
            "lead",
            (object?)song.Path.Maxima.Lead ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "bass",
            (object?)song.Path.Maxima.Bass ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "drums",
            (object?)song.Path.Maxima.Drums ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "vocals",
            (object?)song.Path.Maxima.Vocals ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "proLead",
            (object?)song.Path.Maxima.ProLead ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "proBass",
            (object?)song.Path.Maxima.ProBass ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "proCymbals",
            (object?)song.Path.Maxima.ProCymbals ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "proDrums",
            (object?)song.Path.Maxima.ProDrums ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "pending",
            song.Path.PathGenerationPending);
    }

    private static async Task ValidateRollbackRowsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string manifestDigest,
        IReadOnlyList<MaxScoreMaintenanceRollbackSong> expectedSongs,
        CancellationToken ct)
    {
        foreach (var expected in expectedSongs)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT expected_catalog_last_modified,
                       path_generation_revision,
                       dat_file_hash,
                       song_last_modified,
                       paths_generated_at,
                       chopt_version,
                       chopt_binary_sha256,
                       path_generation_profile,
                       path_artifact_generation_id,
                       path_expected_instruments,
                       max_lead_score,
                       max_bass_score,
                       max_drums_score,
                       max_vocals_score,
                       max_pro_lead_score,
                       max_pro_bass_score,
                       max_pro_cymbals_score,
                       max_pro_drums_score,
                       path_generation_pending
                FROM max_score_maintenance_rollback_songs
                WHERE manifest_sha256 = @manifestSha256
                  AND song_id = @songId
                """;
            cmd.Parameters.AddWithValue(
                "manifestSha256",
                manifestDigest);
            cmd.Parameters.AddWithValue(
                "songId",
                expected.SongId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var path = expected.Path;
            if (!await reader.ReadAsync(ct)
                || !ProviderTimestampIdentity.Equivalent(
                    reader.GetString(0),
                    expected.ExpectedCatalogLastModified)
                || reader.GetInt64(1) != path.Revision
                || ReadNullableString(reader, 2) != path.DatFileHash
                || !ProviderTimestampIdentity.Equivalent(
                    ReadNullableString(reader, 3),
                    path.SongLastModified)
                || ReadNullableDateTime(reader, 4)
                    != path.GeneratedAtUtc
                || ReadNullableString(reader, 5)
                    != path.ChoptVersion
                || ReadNullableString(reader, 6)
                    != path.ChoptBinarySha256
                || ReadNullableString(reader, 7)
                    != path.GenerationProfile
                || ReadNullableString(reader, 8)
                    != path.ArtifactGenerationId
                || !reader.GetFieldValue<string[]>(9)
                    .SequenceEqual(
                        path.ExpectedInstruments,
                        StringComparer.Ordinal)
                || ReadNullableInt32(reader, 10)
                    != path.Maxima.Lead
                || ReadNullableInt32(reader, 11)
                    != path.Maxima.Bass
                || ReadNullableInt32(reader, 12)
                    != path.Maxima.Drums
                || ReadNullableInt32(reader, 13)
                    != path.Maxima.Vocals
                || ReadNullableInt32(reader, 14)
                    != path.Maxima.ProLead
                || ReadNullableInt32(reader, 15)
                    != path.Maxima.ProBass
                || ReadNullableInt32(reader, 16)
                    != path.Maxima.ProCymbals
                || ReadNullableInt32(reader, 17)
                    != path.Maxima.ProDrums
                || reader.GetBoolean(18)
                    != path.PathGenerationPending)
            {
                throw new InvalidOperationException(
                    $"Rollback evidence identity mismatch for {expected.SongId}.");
            }
        }
    }

    private static string? ReadNullableString(
        NpgsqlDataReader reader,
        int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : reader.GetString(ordinal);

    private static DateTime? ReadNullableDateTime(
        NpgsqlDataReader reader,
        int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : reader.GetDateTime(ordinal);

    private static int? ReadNullableInt32(
        NpgsqlDataReader reader,
        int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : reader.GetInt32(ordinal);

    private static PathGenerationPromotion CreatePromotion(
        MaxScoreMaintenanceManifestSong song)
        => new(
            song.StagedPath.ArtifactGenerationId!,
            song.SongId,
            song.CurrentPath.Revision,
            song.StagedPath.ArtifactGenerationId!,
            song.StagedPath.DatFileHash!,
            song.ExpectedCatalogLastModified,
            song.StagedPath.GeneratedAtUtc!.Value,
            new PathGenerationRuntimeIdentity(
                song.StagedPath.ChoptVersion!,
                song.StagedPath.ChoptBinarySha256!,
                song.StagedPath.GenerationProfile!),
            song.StagedPath.ExpectedInstruments,
            song.StagedPath.Maxima.ToSongMaxScores());

    internal static IReadOnlyList<SoloCurrentProjectionScopeKey>
        GetNewlyAdmittedPathPairs(
            MaxScoreMaintenanceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Songs
            .SelectMany(song =>
                song.StagedPath.ExpectedInstruments
                    .Where(instrument =>
                        song.StagedPath.Maxima
                            .GetByInstrument(instrument) is > 0
                        && (!song.CurrentPath.ExpectedInstruments
                                .Contains(
                                    instrument,
                                    StringComparer.Ordinal)
                            || song.CurrentPath.Maxima
                                .GetByInstrument(instrument) is not > 0))
                    .Select(instrument =>
                        new SoloCurrentProjectionScopeKey(
                            song.SongId,
                            instrument)))
            .Distinct()
            .OrderBy(pair => pair.SongId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Instrument, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool PathIdentityMatches(
        PathGenerationState actual,
        MaxScoreMaintenancePathIdentity expected)
        => actual.Revision == expected.Revision
           && string.Equals(
               actual.DatFileHash,
               expected.DatFileHash,
               StringComparison.Ordinal)
           && ProviderTimestampIdentity.Equivalent(
               actual.SongLastModified,
               expected.SongLastModified)
           && actual.GeneratedAtUtc == expected.GeneratedAtUtc
           && string.Equals(
               actual.ChoptVersion,
               expected.ChoptVersion,
               StringComparison.Ordinal)
           && string.Equals(
               actual.ChoptBinarySha256,
               expected.ChoptBinarySha256,
               StringComparison.Ordinal)
           && string.Equals(
               actual.GenerationProfile,
               expected.GenerationProfile,
               StringComparison.Ordinal)
           && string.Equals(
               actual.ArtifactGenerationId,
               expected.ArtifactGenerationId,
               StringComparison.Ordinal)
           && PathGenerationInstruments.NormalizeExpected(
                   actual.ExpectedInstruments)
               .SequenceEqual(
                   expected.ExpectedInstruments,
                   StringComparer.Ordinal)
           && MaxScoreMaintenanceMaxima.From(actual.MaxScores)
               == expected.Maxima
           && actual.PathGenerationPending
               == expected.PathGenerationPending;

    private static void ValidateStagedPromotion(
        PathGenerationPromotion promotion,
        ValidatedPathGeneration validated)
    {
        if (!string.Equals(
                validated.Manifest.GenerationId,
                promotion.ArtifactGenerationId,
                StringComparison.Ordinal)
            || !string.Equals(
                validated.Manifest.SongId,
                promotion.SongId,
                StringComparison.Ordinal)
            || !string.Equals(
                validated.Manifest.DatFileHash,
                promotion.DatFileHash,
                StringComparison.Ordinal)
            || !ProviderTimestampIdentity.Equivalent(
                validated.Manifest.SongLastModified,
                promotion.SongLastModified)
            || !string.Equals(
                validated.Manifest.ChoptVersion,
                promotion.Runtime.Version,
                StringComparison.Ordinal)
            || !string.Equals(
                validated.Manifest.ChoptBinarySha256,
                promotion.Runtime.BinarySha256,
                StringComparison.Ordinal)
            || !string.Equals(
                validated.Manifest.GenerationProfile,
                promotion.Runtime.Profile,
                StringComparison.Ordinal)
            || !validated.Manifest.ExpectedInstruments.SequenceEqual(
                promotion.ExpectedInstruments,
                StringComparer.Ordinal)
            || MaxScoreMaintenanceMaxima.From(validated.MaxScores)
                != MaxScoreMaintenanceMaxima.From(
                    promotion.MaxScores))
        {
            throw new InvalidOperationException(
                $"Staged immutable generation identity mismatch for {promotion.SongId}.");
        }
    }

    private static void ValidateManifestStagedGeneration(
        MaxScoreMaintenanceManifestSong song,
        ValidatedPathGeneration validated)
    {
        var staged = song.StagedPath;
        if (!string.Equals(
                validated.Manifest.GenerationId,
                staged.ArtifactGenerationId,
                StringComparison.Ordinal)
            || !string.Equals(
                validated.Manifest.SongId,
                song.SongId,
                StringComparison.Ordinal)
            || !string.Equals(
                validated.Manifest.DatFileHash,
                staged.DatFileHash,
                StringComparison.Ordinal)
            || !ProviderTimestampIdentity.Equivalent(
                validated.Manifest.SongLastModified,
                song.ExpectedCatalogLastModified)
            || validated.Manifest.GeneratedAtUtc
                != staged.GeneratedAtUtc
            || !string.Equals(
                validated.Manifest.ChoptVersion,
                staged.ChoptVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                validated.Manifest.ChoptBinarySha256,
                staged.ChoptBinarySha256,
                StringComparison.Ordinal)
            || !string.Equals(
                validated.Manifest.GenerationProfile,
                staged.GenerationProfile,
                StringComparison.Ordinal)
            || !validated.Manifest.ExpectedInstruments.SequenceEqual(
                staged.ExpectedInstruments,
                StringComparer.Ordinal)
            || MaxScoreMaintenanceMaxima.From(validated.MaxScores)
                != staged.Maxima)
        {
            throw new InvalidOperationException(
                $"Immutable staged generation failed manifest identity for {song.SongId}.");
        }
    }

    private void EnsureStageSourceUnchanged(
        MaxScoreMaintenanceManifest manifest)
    {
        foreach (var song in manifest.Songs)
        {
            var state = RequireCurrentPathState(
                song.SongId,
                song.SongId,
                song.ExpectedCatalogLastModified);
            if (!PathIdentityMatches(state, song.CurrentPath))
            {
                throw new InvalidOperationException(
                    $"Path source identity changed during staging for {song.SongId}; manifest was not accepted.");
            }
        }
    }

    private static void ValidateExpectedRuntime(
        MaxScoreMaintenanceStageRequest request,
        PathGenerationRuntimeIdentity runtime)
    {
        if (request.ExpectedChoptVersion is not null
            && !string.Equals(
                request.ExpectedChoptVersion,
                runtime.Version,
                StringComparison.Ordinal)
            || request.ExpectedChoptBinarySha256 is not null
            && !string.Equals(
                request.ExpectedChoptBinarySha256,
                runtime.BinarySha256,
                StringComparison.Ordinal)
            || request.ExpectedGenerationProfile is not null
            && !string.Equals(
                request.ExpectedGenerationProfile,
                runtime.Profile,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Staged CHOpt runtime identity does not match the approved stage request.");
        }
    }

    private void RequirePathGenerationConfiguration()
    {
        if (!_options.EnablePathGeneration)
        {
            throw new InvalidOperationException(
                "Max-score maintenance staging requires path generation enabled.");
        }
        if (_options.EnableAutomaticPathGeneration)
        {
            throw new InvalidOperationException(
                "Max-score maintenance staging requires automatic path generation disabled.");
        }
    }

    private static void ValidateManifestCliIdentity(
        MaxScoreMaintenanceManifest manifest,
        string manifestDigest,
        long expectedPublishedScrapeId,
        string expectedManifestDigest)
    {
        var normalizedExpectedDigest =
            MaxScoreMaintenanceManifest.NormalizeSha256(
                expectedManifestDigest,
                nameof(expectedManifestDigest));
        if (manifest.ExpectedPublishedScrapeId
                != expectedPublishedScrapeId
            || !string.Equals(
                manifestDigest,
                normalizedExpectedDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Manifest scrape identity or digest does not match the CLI gate.");
        }
    }

    private static void ValidateManifestCatalogIdentity(
        MaxScoreMaintenanceManifest manifest,
        PublicationSongCatalogInfo catalog)
    {
        if (catalog.PublicationId != manifest.ExpectedPublicationId
            || catalog.ScrapeId
                != manifest.ExpectedPublishedScrapeId
            || catalog.CatalogVersion != manifest.CatalogVersion
            || catalog.SchemaVersion
                != manifest.CatalogSchemaVersion
            || catalog.SongCount != manifest.CatalogSongCount
            || !string.Equals(
                catalog.ContentHash,
                manifest.CatalogContentHash,
                StringComparison.Ordinal)
            || catalog.SourceCapturedAtUtc
                != manifest.CatalogSourceCapturedAtUtc)
        {
            throw new InvalidOperationException(
                "Current publication catalog identity differs from the manifest.");
        }
    }

    private static void ValidateRunIdentity(
        MaxScoreMaintenanceRunState run,
        MaxScoreMaintenanceManifest manifest,
        string planDigest)
    {
        if (!string.Equals(
                run.PlanDigest,
                planDigest,
                StringComparison.Ordinal)
            || run.ExpectedPublishedScrapeId
                != manifest.ExpectedPublishedScrapeId
            || run.ExpectedPublicationId
                != manifest.ExpectedPublicationId
            || !string.Equals(
                run.ExpectedCatalogHash,
                manifest.CatalogContentHash,
                StringComparison.Ordinal)
            || run.ExpectedCatalogSongCount
                != manifest.CatalogSongCount)
        {
            throw new InvalidOperationException(
                "Resume manifest, plan, publication, or catalog identity differs from the persisted run.");
        }
    }

    private static MaxScoreMaintenanceApplyReport ToApplyReport(
        MaxScoreMaintenanceRunState run,
        bool succeeded,
        bool resumable,
        bool publicReadsFrozen)
        => new(
            MaxScoreMaintenanceApplyReport.CurrentReportVersion,
            succeeded,
            resumable,
            publicReadsFrozen,
            run.ManifestSha256,
            run.PlanDigest,
            run.Phase,
            run.ExpectedPublishedScrapeId,
            run.ExpectedPublicationId,
            run.RollbackSnapshotPath,
            run.RollbackSnapshotSha256,
            run.PromotedSongCount,
            run.RebuiltInstrumentCount,
            run.QuarantinedCandidateCount,
            run.VisibleDeliveryCount,
            run.StagedCacheEntryCount,
            run.FailureStage,
            run.FailureDetail);

    private string ResolveDataPath(string requestedPath)
        => MaxScoreMaintenanceFileStore.ResolveExistingJsonInputPath(
            _options.DataDirectory,
            requestedPath,
            MaxScoreMaintenanceManifest.MaximumManifestBytes);

    private static bool PathsEquivalent(string first, string second)
        => string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static DateTime NormalizeDatabaseTimestamp(DateTime value)
    {
        var utc = value.ToUniversalTime();
        return new DateTime(
            utc.Ticks - utc.Ticks % 10,
            DateTimeKind.Utc);
    }

    private static bool TryGetExactProviderLastModified(
        Song song,
        out string? lastModified)
    {
        lastModified = null;
        return song.providerJson is JsonElement
        {
            ValueKind: JsonValueKind.Object,
        } provider
            && provider.TryGetProperty(
                "lastModified",
                out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(
                lastModified = property.GetString());
    }

    private static string BoundDetail(string detail)
    {
        const int maximumLength = 2048;
        var sanitized = detail.Replace('\0', ' ');
        return sanitized.Length <= maximumLength
            ? sanitized
            : sanitized[..maximumLength];
    }

    private static MaxScoreMaintenancePhase ParsePhase(string phase)
        => phase switch
        {
            "freeze_established" =>
                MaxScoreMaintenancePhase.FreezeEstablished,
            "rollback_captured" =>
                MaxScoreMaintenancePhase.RollbackCaptured,
            "paths_promoted" =>
                MaxScoreMaintenancePhase.PathsPromoted,
            "derived_state_rebuilt" =>
                MaxScoreMaintenancePhase.DerivedStateRebuilt,
            "notifications_quarantined" =>
                MaxScoreMaintenancePhase.NotificationsQuarantined,
            "caches_staged" =>
                MaxScoreMaintenancePhase.CachesStaged,
            "validated" => MaxScoreMaintenancePhase.Validated,
            "completed" => MaxScoreMaintenancePhase.Completed,
            _ => throw new InvalidOperationException(
                $"Unknown max-score maintenance phase '{phase}'."),
        };

    private static string FormatPhase(MaxScoreMaintenancePhase phase)
        => phase switch
        {
            MaxScoreMaintenancePhase.FreezeEstablished =>
                "freeze_established",
            MaxScoreMaintenancePhase.RollbackCaptured =>
                "rollback_captured",
            MaxScoreMaintenancePhase.PathsPromoted =>
                "paths_promoted",
            MaxScoreMaintenancePhase.DerivedStateRebuilt =>
                "derived_state_rebuilt",
            MaxScoreMaintenancePhase.NotificationsQuarantined =>
                "notifications_quarantined",
            MaxScoreMaintenancePhase.CachesStaged =>
                "caches_staged",
            MaxScoreMaintenancePhase.Validated => "validated",
            MaxScoreMaintenancePhase.Completed => "completed",
            _ => throw new ArgumentOutOfRangeException(
                nameof(phase),
                phase,
                "Unsupported persisted max-score phase."),
        };

    private sealed record PublishedContext(
        PublicationPointerState Pointers,
        PublicationSongCatalogInfo Catalog,
        IReadOnlyList<Song> CatalogSongs,
        IReadOnlyDictionary<string, Song> SongsById,
        IReadOnlyDictionary<string, string>
            CatalogLastModifiedBySong);

    private sealed record MaxScoreMaintenanceRunState(
        string ManifestSha256,
        string PlanDigest,
        long ExpectedPublishedScrapeId,
        long ExpectedPublicationId,
        string ExpectedCatalogHash,
        int ExpectedCatalogSongCount,
        string PublishedScoreSourceFingerprint,
        string NotificationStateFingerprint,
        string RankHistoryFingerprint,
        string FreezeReason,
        MaxScoreMaintenancePhase Phase,
        string Status,
        string? RollbackSnapshotPath,
        string? RollbackSnapshotSha256,
        long? NotificationMaintenanceRunId,
        int PromotedSongCount,
        int RebuiltInstrumentCount,
        long QuarantinedCandidateCount,
        int VisibleDeliveryCount,
        long StagedCacheEntryCount,
        string? FailureStage,
        string? FailureDetail,
        DateTime CreatedAtUtc);
}
