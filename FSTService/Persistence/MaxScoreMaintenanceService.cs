using System.Collections.Frozen;
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
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly IMetaDatabase _metaDatabase;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ScraperOptions _options;
    private readonly MaxScoreMaintenanceNotificationService _notifications;
    private readonly MaxScoreMaintenanceDerivedStateService _derivedState;
    private readonly ScrapeTimePrecomputer _precomputer;
    private readonly ISongInstrumentSupportCache
        _instrumentSupportCache;
    private readonly ILogger<MaxScoreMaintenanceService> _log;
    internal Action<MaxScoreMaintenancePhase>?
        AfterPhaseCheckpointTestHook
    { get; set; }
    internal Action<Exception>? FailureTestHook
    { get; set; }
    internal Action<MaxScoreMaintenancePhase>?
        AfterRollbackPhaseCheckpointTestHook
    { get; set; }
    internal Action? BeforeRollbackPathRestoreTestHook
    { get; set; }
    internal Action? BeforeDerivedRebuildTestHook
    { get; set; }
    internal Action? BeforeRollbackCompletionTestHook
    { get; set; }
    internal Action<int>? AfterRollbackCompletionTestHook
    { get; set; }
    internal Func<Exception?>?
        TerminalMutationGateCleanupFailureTestHook
    { get; set; }
    internal Func<MaxScoreMaintenanceRollbackRuntimeState>?
        RollbackRuntimeStateTestHook
    { get; set; }
    internal Func<string, Exception?>?
        PlanEvidenceStageFailureTestHook
    { get; set; }
    internal Action<string, int>?
        EvidenceCommandTimeoutTestHook
    { get; set; }
    internal Func<
        string,
        IReadOnlyList<MaxScoreMaintenanceObservedScoreCheck>,
        IReadOnlyList<MaxScoreMaintenanceObservedScoreCheck>>?
        ObservedScoreChecksTestHook
    { get; set; }

    public MaxScoreMaintenanceService(
        PathGenerationCoordinator pathGeneration,
        IPathDataStore pathStore,
        GlobalLeaderboardPersistence persistence,
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
        _persistence = persistence;
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
        if (stageRequestPath is null || explicitSongIds.Count > 0)
        {
            throw new ArgumentException(
                "Max-score maintenance staging requires one strict request file and does not accept unscoped song IDs.");
        }
        var request =
            await MaxScoreMaintenanceFileStore.LoadStageRequestAsync(
                _options.DataDirectory,
                stageRequestPath,
                ct);
        var stageRequestDigest = request.ComputeDigest();
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
        var currentIdentityBySong =
            new Dictionary<string, MaxScoreMaintenancePathIdentity>(
                StringComparer.Ordinal);
        foreach (var requestSong in request.Songs)
        {
            var song = context.SongsById[requestSong.SongId];
            var state = RequireCurrentPathState(
                requestSong.SongId,
                requestSong.SongId,
                context.CatalogLastModifiedBySong[requestSong.SongId]);
            requestSong.ValidateOldMaxima(
                MaxScoreMaintenanceMaxima.From(state.MaxScores));
            if (request.ExpectedChangedInstruments.Any(
                    PathGenerationInstruments.IsPlasticDrumsInstrument)
                && PathGenerationProfiles.HasInvalidPlasticDrumsScores(
                    state.GenerationProfile))
            {
                throw new InvalidOperationException(
                    $"Current generation for {requestSong.SongId} uses known-invalid plastic-drums v3.");
            }
            currentIdentityBySong[requestSong.SongId] =
                MaxScoreMaintenanceArtifactValidator
                    .CaptureCurrentIdentity(
                        _options.DataDirectory,
                        state);

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
            if (!promotion.ExpectedInstruments.SequenceEqual(
                    request.ExpectedPathInstruments,
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Staged generation for {pathRequest.SongId} differs from the approved exact instrument scope.");
            }
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
            requestSong.ValidateNewMaxima(newMaxima);
            var changed = MaxScoreMaintenanceManifest.AllInstruments
                .Where(instrument =>
                    oldMaxima.GetByInstrument(instrument)
                    != newMaxima.GetByInstrument(instrument))
                .ToArray();
            if (!changed.SequenceEqual(
                    request.ExpectedChangedInstruments,
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Staged generation for {pathRequest.SongId} does not change exactly the approved instruments.");
            }

            var currentIdentity =
                currentIdentityBySong[pathRequest.SongId];
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
                PathGenerationPending: false,
                validated.ArtifactTreeSha256,
                validated.ArtifactFileCount);
            var plasticDrumsEvidence =
                changed.Any(
                    PathGenerationInstruments.IsPlasticDrumsInstrument)
                    ? MaxScoreMaintenanceArtifactValidator
                        .CapturePlasticDrumsEvidence(validated)
                    : null;
            manifestSongs.Add(
                new MaxScoreMaintenanceManifestSong(
                    pathRequest.SongId,
                    context.CatalogLastModifiedBySong[
                        pathRequest.SongId],
                    currentIdentity,
                    stagedIdentity,
                    changed,
                    plasticDrumsEvidence)
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
                    request.Purpose,
                    Promotable: false,
                    stageRequestDigest,
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
            new MaxScoreMaintenanceScope(
                request.Purpose,
                stageRequestDigest,
                request.ExpectedPathInstruments,
                request.ExpectedChangedInstruments),
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
        foreach (var song in manifest.Songs)
        {
            MaxScoreMaintenanceArtifactValidator
                .ValidateManifestSong(
                    _options.DataDirectory,
                    song);
        }
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
            request.Purpose,
            Promotable:
                request.Purpose
                == MaxScoreMaintenanceStagePurposes.Promotion,
            stageRequestDigest,
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
        manifest = manifest.RequirePromotionReady();
        var manifestDigest = manifest.ComputeDigest();
        ValidateManifestCliIdentity(
            manifest,
            manifestDigest,
            expectedPublishedScrapeId,
            expectedManifestDigest);

        MaxScoreMaintenancePlanReport report;
        var failureStage = "lease-acquisition";
        try
        {
            await using var lease =
                await _metaDatabase
                    .AcquireMaxScoreMaintenanceLeaseAsync(
                        manifest.ExpectedPublicationId,
                        ct);
            failureStage = "lease-validation";
            await lease.VerifyHeldAsync(
                requireSourceLocks: false,
                ct);
            failureStage = "plan-inspection";
            report = await lease.ExecuteTransactionAsync(
                "plan-inspection",
                requireSourceLocks: true,
                (connection, transaction, token) => BuildPlanAsync(
                    manifest,
                    manifestDigest,
                    connection,
                    transaction,
                    token,
                    stage => failureStage = stage),
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
                ScoreHistoryFingerprint: new string('0', 64),
                PopulationEvidence:
                    EmptyPopulationEvidence(),
                ScoreHistoryEvidence:
                    EmptyScoreHistoryEvidence(),
                AffectedInstruments:
                    manifest.Scope.ExpectedChangedInstruments,
                RoutineCandidateCount: -1,
                Checks:
                [
                    new MaxScoreMaintenancePlanCheck(
                        "plan",
                        Passed: false,
                        FormatPlanFailureDetail(
                            failureStage,
                            ex)),
                ],
                RoutineCandidates: [],
                ArtifactEvidence:
                    CreateArtifactEvidence(manifest),
                ObservedScoreChecks: []);
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
        manifest = manifest.RequirePromotionReady();
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
        IDisposable? authoritativeReadPass = null;
        try
        {
            activeLease = resume
                ? await _metaDatabase
                    .AcquireMaxScoreMaintenanceResumeLeaseAsync(
                        manifest.ExpectedPublicationId,
                        ct)
                : await _metaDatabase
                    .AcquireMaxScoreMaintenanceLeaseAsync(
                        manifest.ExpectedPublicationId,
                        ct);
            var lease = activeLease;
            await lease.VerifyHeldAsync(
                requireSourceLocks: false,
                ct);
            var run = await LoadRunAsync(manifestDigest, ct);
            if (run?.Phase
                >= MaxScoreMaintenancePhase.RollbackValidating)
            {
                throw new InvalidOperationException(
                    "The maintenance run has entered rollback reconciliation; use the rollback command.");
            }
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

                var resumingExistingRun = run is not null;
                var resumeStartedAtValidated =
                    run?.Phase == MaxScoreMaintenancePhase.Validated;
                var rollbackEvidenceValidated = false;
                if (run is null)
                {
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: false,
                        ct);
                    var plan =
                        await lease.ExecuteTransactionAsync(
                            "apply-plan-validation",
                            requireSourceLocks: true,
                            (connection, transaction, token) => BuildPlanAsync(
                                manifest,
                                manifestDigest,
                                connection,
                                transaction,
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
                    AfterPhaseCheckpointTestHook?.Invoke(
                        run.Phase);
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
                    if (run.Phase
                        >= MaxScoreMaintenancePhase.RollbackCaptured)
                    {
                        if (rollbackOutputPath is not null
                            && !PathsEquivalent(
                                ResolveDataPath(rollbackOutputPath),
                                run.RollbackSnapshotPath
                                ?? throw new InvalidOperationException(
                                    "Rollback checkpoint path is missing.")))
                        {
                            throw new InvalidOperationException(
                                "Resume rollback path does not match the persisted rollback evidence path.");
                        }
                        await ValidatePersistedRollbackEvidenceAsync(
                            lease,
                            manifest,
                            manifestDigest,
                            run,
                            ct);
                        rollbackEvidenceValidated = true;
                    }
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    RequireOwnedFreeze(
                        manifest,
                        manifestDigest);
                }

                authoritativeReadPass =
                    _persistence
                        .BeginMaxScoreMaintenancePublishedReadPass();
                var publishedContext = LoadPublishedContext(
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
                    publishedContext.Catalog);
                var readSnapshot =
                    await CaptureMaintenanceReadSnapshotAsync(
                        lease,
                        manifest,
                        publishedContext.CatalogSongs,
                        ct);
                if (run is null)
                {
                    throw new InvalidOperationException(
                        "Max-score maintenance run disappeared after freeze establishment.");
                }
                ValidateObservedScoreEvidence(
                    readSnapshot.ObservedScoreChecks);
                if (readSnapshot.PopulationEvidence
                        != run.PopulationEvidence
                    || readSnapshot.ScoreHistoryEvidence
                        != run.ScoreHistoryEvidence
                    || !string.Equals(
                        readSnapshot.ScoreHistoryEvidence
                            .Fingerprint,
                        run.ScoreHistoryFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Publication-bound population or consumed score-history evidence differs from the persisted plan.");
                }
                ValidateRecomputedPlanEvidence(
                    manifest,
                    manifestDigest,
                    publishedContext,
                    run,
                    readSnapshot);

                if (resumingExistingRun)
                {
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
                    await MarkRunRunningAsync(
                        lease,
                        manifestDigest,
                        ct);
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
                    AfterPhaseCheckpointTestHook?.Invoke(
                        run.Phase);
                }
                if (rollbackOutputPath is not null
                    && !PathsEquivalent(
                        ResolveDataPath(rollbackOutputPath),
                        run.RollbackSnapshotPath
                        ?? throw new InvalidOperationException(
                            "Rollback checkpoint path is missing.")))
                {
                    throw new InvalidOperationException(
                        "Resume rollback path does not match the persisted rollback evidence path.");
                }
                if (!rollbackEvidenceValidated)
                {
                    await ValidatePersistedRollbackEvidenceAsync(
                        lease,
                        manifest,
                        manifestDigest,
                        run,
                        ct);
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
                        cacheEvidence: null,
                        cachePublicationId: null,
                        ct: ct);
                    run = await LoadRequiredRunAsync(
                        manifestDigest,
                        ct);
                    AfterPhaseCheckpointTestHook?.Invoke(
                        run.Phase);
                }

                if (run.Phase
                    < MaxScoreMaintenancePhase.DerivedStateRebuilt)
                {
                    ValidateWorkerOffline();
                    BeforeDerivedRebuildTestHook?.Invoke();
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    var derived = await _derivedState.RebuildAsync(
                        manifest,
                        publishedContext.CatalogSongs,
                        readSnapshot.PostPromotionMaxScores,
                        readSnapshot.Population,
                        readSnapshot.AffectedPlayerStatsAccounts,
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
                        cacheEvidence: null,
                        cachePublicationId: null,
                        ct: ct);
                    run = await LoadRequiredRunAsync(
                        manifestDigest,
                        ct);
                    AfterPhaseCheckpointTestHook?.Invoke(
                        run.Phase);
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
                    AfterPhaseCheckpointTestHook?.Invoke(
                        run.Phase);
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
                                publishedContext.CatalogSongs,
                                readSnapshot.PostPromotionMaxScores,
                                readSnapshot.Population,
                                lease,
                                ct);
                    if (stagedCacheEntries <= 0)
                    {
                        throw new InvalidOperationException(
                            "Maintenance cache staging produced no entries.");
                    }
                    var cacheEvidence =
                        await BuildCacheEvidenceAsync(
                            manifest,
                            readSnapshot,
                            ct);
                    if (cacheEvidence.EntryCount
                        != stagedCacheEntries)
                    {
                        throw new InvalidOperationException(
                            "Maintenance cache staging count differs from its validated content evidence.");
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
                        cacheEvidence: cacheEvidence,
                        cachePublicationId:
                            manifest.ExpectedPublicationId,
                        ct: ct);
                    run = await LoadRequiredRunAsync(
                        manifestDigest,
                        ct);
                    AfterPhaseCheckpointTestHook?.Invoke(
                        run.Phase);
                }

                if (run.Phase < MaxScoreMaintenancePhase.Validated)
                {
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    await ValidateCompletedMaintenanceAsync(
                        lease,
                        manifest,
                        manifestDigest,
                        run,
                        readSnapshot,
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
                        cacheEvidence: null,
                        cachePublicationId: null,
                        ct: ct);
                    run = await LoadRequiredRunAsync(
                        manifestDigest,
                        ct);
                    AfterPhaseCheckpointTestHook?.Invoke(
                        run.Phase);
                }

                if (resumeStartedAtValidated)
                {
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    await ValidateCompletedMaintenanceAsync(
                        lease,
                        manifest,
                        manifestDigest,
                        run,
                        readSnapshot,
                        ct);
                }

                await ValidatePersistedRollbackEvidenceAsync(
                    lease,
                    manifest,
                    manifestDigest,
                    run,
                    ct);
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
            try
            {
                FailureTestHook?.Invoke(ex);
            }
            catch
            {
            }
            MaxScoreMaintenanceRunState? run = null;
            try
            {
                run = await LoadRunAsync(manifestDigest, ct);
                if (run is not null
                    && run.Phase
                    != MaxScoreMaintenancePhase.Completed
                    && run.Phase
                    < MaxScoreMaintenancePhase.RollbackValidating)
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
            var rollbackOwned =
                run is not null
                && run.Phase
                    >= MaxScoreMaintenancePhase
                        .RollbackValidating;
            var currentFreeze =
                _metaDatabase
                    .GetPublicReadFreezeState()
                    .IsFrozen;
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
                    CacheEvidence: null,
                    FailureStage: ownedFreeze
                        ? "checkpoint_unavailable"
                        : "pre_freeze",
                    Detail: BoundDetail(ex.Message))
                : rollbackOwned
                ? ToApplyRollbackOwnedRejection(
                    run!,
                    currentFreeze,
                    ex)
                : ToApplyReport(
                    run,
                    succeeded: false,
                    resumable: true,
                    publicReadsFrozen: true);
        }
        finally
        {
            authoritativeReadPass?.Dispose();
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

    public async Task<MaxScoreMaintenanceRollbackReport> RollbackAsync(
        long expectedPublishedScrapeId,
        string manifestPath,
        string expectedManifestDigest,
        string expectedPlanDigest,
        string rollbackFilePath,
        string expectedRollbackDigest,
        string reportOutputPath,
        bool dryRun,
        CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        await using var reportReservation =
            MaxScoreMaintenanceFileStore.ReserveNewReport(
                _options.DataDirectory,
                reportOutputPath);
        try
        {
            return await RollbackCoreAsync(
                expectedPublishedScrapeId,
                manifestPath,
                expectedManifestDigest,
                expectedPlanDigest,
                rollbackFilePath,
                expectedRollbackDigest,
                reportReservation,
                dryRun,
                startedAtUtc,
                ct);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException)
        {
            try
            {
                FailureTestHook?.Invoke(ex);
            }
            catch
            {
            }
            var manifest =
                await MaxScoreMaintenanceFileStore
                    .LoadManifestAsync(
                        _options.DataDirectory,
                        manifestPath,
                        CancellationToken.None);
            var report = CreateRollbackFailureReport(
                manifest,
                MaxScoreMaintenanceManifest.NormalizeSha256(
                    expectedManifestDigest,
                    nameof(expectedManifestDigest)),
                MaxScoreMaintenanceManifest.NormalizeSha256(
                    expectedPlanDigest,
                    nameof(expectedPlanDigest)),
                MaxScoreMaintenanceManifest.NormalizeSha256(
                    expectedRollbackDigest,
                    nameof(expectedRollbackDigest)),
                startedAtUtc,
                _metaDatabase
                    .GetPublicReadFreezeState()
                    .IsFrozen,
                dryRun,
                ex);
            await reportReservation.WriteAsync(
                report,
                CancellationToken.None);
            return report;
        }
    }

    private async Task<MaxScoreMaintenanceRollbackReport>
        RollbackCoreAsync(
        long expectedPublishedScrapeId,
        string manifestPath,
        string expectedManifestDigest,
        string expectedPlanDigest,
        string rollbackFilePath,
        string expectedRollbackDigest,
        MaxScoreMaintenanceFileStore.NewReportReservation
            reportReservation,
        bool dryRun,
        DateTime startedAtUtc,
        CancellationToken ct)
    {
        var manifest =
            (await MaxScoreMaintenanceFileStore.LoadManifestAsync(
                _options.DataDirectory,
                manifestPath,
                ct)).RequirePromotionReady();
        var manifestDigest = manifest.ComputeDigest();
        ValidateManifestCliIdentity(
            manifest,
            manifestDigest,
            expectedPublishedScrapeId,
            expectedManifestDigest);
        var planDigest =
            MaxScoreMaintenanceManifest.NormalizeSha256(
                expectedPlanDigest,
                nameof(expectedPlanDigest));
        var rollbackDigest =
            MaxScoreMaintenanceManifest.NormalizeSha256(
                expectedRollbackDigest,
                nameof(expectedRollbackDigest));
        var rollbackFile =
            await MaxScoreMaintenanceFileStore
                .LoadCanonicalRollbackSnapshotAsync(
                    _options.DataDirectory,
                    rollbackFilePath,
                    ct);
        if (!string.Equals(
                rollbackFile.Sha256,
                rollbackDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Rollback file SHA-256 does not match the CLI gate.");
        }
        var rollbackSnapshot =
            rollbackFile.Snapshot.ValidateAndNormalize();
        ValidateRollbackSnapshotIdentity(
            rollbackSnapshot,
            manifest,
            manifestDigest,
            planDigest);
        foreach (var song in manifest.Songs)
        {
            MaxScoreMaintenanceArtifactValidator
                .ValidateManifestSong(
                    _options.DataDirectory,
                    song);
        }

        MaxScoreMaintenanceRollbackReport report;
        IMaxScoreMaintenanceLease? activeLease = null;
        IDisposable? authoritativeReadPass = null;
        var terminalReconciliationEligible = false;
        try
        {
            ValidateWorkerOffline();
            await ValidateRollbackRuntimeQuiescenceAsync(ct);
            var run = await LoadRequiredRunAsync(
                manifestDigest,
                ct);
            ValidateRunIdentity(run, manifest, planDigest);
            await ValidateRollbackRunAndEvidenceAsync(
                manifest,
                manifestDigest,
                rollbackDigest,
                rollbackFile.FullPath,
                rollbackSnapshot,
                run,
                ct);

            if (run.Phase == MaxScoreMaintenancePhase.RolledBack)
            {
                if (dryRun)
                {
                    throw new InvalidOperationException(
                        "Rollback dry-run is read-only and is not applicable after the run is already rolled_back.");
                }
                ValidateRollbackPathPhase(
                    rollbackSnapshot,
                    throwOnMismatch: true);
                _ = LoadPublishedContext(
                    manifest.ExpectedPublishedScrapeId,
                    manifest.ExpectedPublicationId,
                    manifest.Songs
                        .Select(song => song.SongId)
                        .ToArray(),
                    requireUnfrozen: true);
                terminalReconciliationEligible = true;
                await EnsureTerminalMutationGateReleasedAsync(
                        manifest.ExpectedPublicationId,
                        ct);
                report = ToRollbackReport(
                    run,
                    rollbackDigest,
                    dryRun: false,
                    validated: true,
                    succeeded: true,
                    resumable: false,
                    publicReadsFrozen: false,
                    startedAtUtc,
                    completedAtUtc:
                        run.RolledBackAtUtc
                        ?? run.CompletedAtUtc
                        ?? run.UpdatedAtUtc);
            }
            else
            {
                ValidateRollbackEligiblePhase(run.Phase);
                RequireOwnedFreeze(manifest, manifestDigest);
                var currentPathsRestored =
                    run.Phase
                    >= MaxScoreMaintenancePhase
                        .RollbackPathsRestored;
                if (currentPathsRestored)
                {
                    ValidateRollbackPathPhase(
                        rollbackSnapshot,
                        throwOnMismatch: true);
                }
                else
                {
                    ValidatePathPhase(
                        manifest,
                        postPromotion: true,
                        throwOnMismatch: true);
                }
                var beforePathFingerprint =
                    ComputeManifestPathFingerprint(
                        manifest,
                        postPromotion: true);
                var afterPathFingerprint =
                    ComputeRollbackPathFingerprint(
                        rollbackSnapshot);

                if (dryRun)
                {
                    report =
                        CreateRollbackDryRunReport(
                            run,
                            rollbackDigest,
                            beforePathFingerprint,
                            afterPathFingerprint,
                            startedAtUtc);
                }
                else
                {
                    activeLease =
                        await _metaDatabase
                            .AcquireMaxScoreMaintenanceRollbackLeaseAsync(
                                manifest.ExpectedPublicationId,
                                ct);
                    var lease = activeLease;
                    await lease.VerifyHeldAsync(
                        requireSourceLocks: true,
                        ct);
                    run = await LoadRequiredRunAsync(
                        manifestDigest,
                        ct);
                    ValidateRunIdentity(
                        run,
                        manifest,
                        planDigest);
                    RequireOwnedFreeze(
                        manifest,
                        manifestDigest);
                    await ValidatePersistedRollbackEvidenceAsync(
                        lease,
                        manifest,
                        manifestDigest,
                        run,
                        ct);

                    authoritativeReadPass =
                        _persistence
                            .BeginMaxScoreMaintenancePublishedReadPass();
                    var publishedContext = LoadPublishedContext(
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
                        publishedContext.Catalog);
                    var readSnapshot =
                        await CaptureRollbackReadSnapshotAsync(
                            lease,
                            manifest,
                            rollbackSnapshot,
                            publishedContext.CatalogSongs,
                            ct);
                    if (readSnapshot.PopulationEvidence
                            != run.PopulationEvidence
                        || readSnapshot.ScoreHistoryEvidence
                            != run.ScoreHistoryEvidence)
                    {
                        throw new InvalidOperationException(
                            "Rollback publication population or approved score-history evidence differs from the accepted run.");
                    }

                    if (run.Phase
                        < MaxScoreMaintenancePhase
                            .RollbackValidating)
                    {
                        await BeginRollbackAsync(
                            lease,
                            manifestDigest,
                            planDigest,
                            rollbackDigest,
                            beforePathFingerprint,
                            ct);
                        run = await LoadRequiredRunAsync(
                            manifestDigest,
                            ct);
                        AfterRollbackPhaseCheckpointTestHook
                            ?.Invoke(run.Phase);
                    }
                    else
                    {
                        await MarkRollbackRunningAsync(
                            lease,
                            manifestDigest,
                            ct);
                    }

                    if (run.Phase
                        < MaxScoreMaintenancePhase
                            .RollbackPathsRestored)
                    {
                        await RestoreRollbackPathsAsync(
                            lease,
                            manifest,
                            manifestDigest,
                            planDigest,
                            rollbackSnapshot,
                            beforePathFingerprint,
                            afterPathFingerprint,
                            ct);
                        _pathStore.InvalidateCachedState();
                        _instrumentSupportCache
                            .RefreshSongInstrumentSupport();
                        run = await LoadRequiredRunAsync(
                            manifestDigest,
                            ct);
                        AfterRollbackPhaseCheckpointTestHook
                            ?.Invoke(run.Phase);
                    }
                    ValidateRollbackPathPhase(
                        rollbackSnapshot,
                        throwOnMismatch: true);

                    if (run.Phase
                        < MaxScoreMaintenancePhase
                            .RollbackDerivedStateRebuilt)
                    {
                        ValidateWorkerOffline();
                        BeforeDerivedRebuildTestHook?.Invoke();
                        var derived =
                            await _derivedState.RebuildAsync(
                                manifest,
                                publishedContext.CatalogSongs,
                                readSnapshot.PostPromotionMaxScores,
                                readSnapshot.Population,
                                readSnapshot
                                    .AffectedPlayerStatsAccounts,
                                lease,
                                ct);
                        await AdvanceRollbackPhaseAsync(
                            lease,
                            manifestDigest,
                            planDigest,
                            MaxScoreMaintenancePhase
                                .RollbackPathsRestored,
                            MaxScoreMaintenancePhase
                                .RollbackDerivedStateRebuilt,
                            restoredSongCount: null,
                            rebuiltInstrumentCount:
                                derived.RebuiltInstrumentCount,
                            notificationResult: null,
                            stagedCacheEntryCount: null,
                            cacheEvidence: null,
                            cachePublicationId: null,
                            ct);
                        run = await LoadRequiredRunAsync(
                            manifestDigest,
                            ct);
                        AfterRollbackPhaseCheckpointTestHook
                            ?.Invoke(run.Phase);
                    }

                    if (run.Phase
                        < MaxScoreMaintenancePhase
                            .RollbackNotificationsQuarantined)
                    {
                        await _notifications
                            .QuarantineAndAlignRollbackAsync(
                                manifest,
                                manifestDigest,
                                planDigest,
                                run
                                    .PublishedScoreSourceFingerprint,
                                lease,
                                ct);
                        run = await LoadRequiredRunAsync(
                            manifestDigest,
                            ct);
                        AfterRollbackPhaseCheckpointTestHook
                            ?.Invoke(run.Phase);
                    }

                    if (run.Phase
                        < MaxScoreMaintenancePhase
                            .RollbackCachesStaged)
                    {
                        var stagedCacheEntries =
                            await _precomputer
                                .StageCurrentPublicationCachesForMaintenanceAsync(
                                    manifest.ExpectedPublicationId,
                                    publishedContext.CatalogSongs,
                                    readSnapshot
                                        .PostPromotionMaxScores,
                                    readSnapshot.Population,
                                    lease,
                                    ct);
                        if (stagedCacheEntries <= 0)
                        {
                            throw new InvalidOperationException(
                                "Rollback cache staging produced no entries.");
                        }
                        var cacheEvidence =
                            await BuildCacheEvidenceAsync(
                                manifest,
                                readSnapshot,
                                ct);
                        await AdvanceRollbackPhaseAsync(
                            lease,
                            manifestDigest,
                            planDigest,
                            MaxScoreMaintenancePhase
                                .RollbackNotificationsQuarantined,
                            MaxScoreMaintenancePhase
                                .RollbackCachesStaged,
                            restoredSongCount: null,
                            rebuiltInstrumentCount: null,
                            notificationResult: null,
                            stagedCacheEntries,
                            cacheEvidence,
                            manifest.ExpectedPublicationId,
                            ct);
                        run = await LoadRequiredRunAsync(
                            manifestDigest,
                            ct);
                        AfterRollbackPhaseCheckpointTestHook
                            ?.Invoke(run.Phase);
                    }

                    if (run.Phase
                        < MaxScoreMaintenancePhase
                            .RollbackValidated)
                    {
                        await ValidateCompletedRollbackAsync(
                            lease,
                            manifest,
                            manifestDigest,
                            rollbackSnapshot,
                            run,
                            readSnapshot,
                            ct);
                        await AdvanceRollbackPhaseAsync(
                            lease,
                            manifestDigest,
                            planDigest,
                            MaxScoreMaintenancePhase
                                .RollbackCachesStaged,
                            MaxScoreMaintenancePhase
                                .RollbackValidated,
                            restoredSongCount: null,
                            rebuiltInstrumentCount: null,
                            notificationResult: null,
                            stagedCacheEntryCount: null,
                            cacheEvidence: null,
                            cachePublicationId: null,
                            ct);
                        run = await LoadRequiredRunAsync(
                            manifestDigest,
                            ct);
                        AfterRollbackPhaseCheckpointTestHook
                            ?.Invoke(run.Phase);
                    }

                    BeforeRollbackCompletionTestHook?.Invoke();
                    await ValidateCompletedRollbackAsync(
                        lease,
                        manifest,
                        manifestDigest,
                        rollbackSnapshot,
                        run,
                        readSnapshot,
                        ct);
                    await ValidatePersistedRollbackEvidenceAsync(
                        lease,
                        manifest,
                        manifestDigest,
                        run,
                        ct);
                    terminalReconciliationEligible = true;
                    await lease.CompleteRollbackAsync(
                        manifest.ExpectedPublishedScrapeId,
                        manifestDigest,
                        ct);
                    AfterRollbackCompletionTestHook?.Invoke(
                        lease.BackendProcessId);
                    _instrumentSupportCache
                        .InvalidateSongInstrumentSupport();
                    run = await LoadRequiredRunAsync(
                        manifestDigest,
                        ct);
                    await activeLease.DisposeAsync();
                    activeLease = null;
                    await EnsureTerminalMutationGateReleasedAsync(
                        manifest.ExpectedPublicationId,
                        ct);
                    report = ToRollbackReport(
                        run,
                        rollbackDigest,
                        dryRun: false,
                        validated: true,
                        succeeded: true,
                        resumable: false,
                        publicReadsFrozen: false,
                        startedAtUtc,
                        completedAtUtc:
                            run.RolledBackAtUtc
                            ?? run.UpdatedAtUtc);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                FailureTestHook?.Invoke(ex);
            }
            catch
            {
            }
            MaxScoreMaintenanceRunState? run = null;
            try
            {
                run = await LoadRunAsync(
                    manifestDigest,
                    CancellationToken.None);
                if (activeLease is not null
                    && run is not null
                    && run.Phase
                        >= MaxScoreMaintenancePhase
                            .RollbackValidating
                    && run.Phase
                        < MaxScoreMaintenancePhase.RolledBack)
                {
                    await RecordRollbackFailureAsync(
                        activeLease,
                        manifestDigest,
                        run.Phase,
                        ex,
                        CancellationToken.None);
                    run = await LoadRunAsync(
                        manifestDigest,
                        CancellationToken.None);
                }
            }
            catch (Exception checkpointError)
            {
                _log.LogError(
                    checkpointError,
                    "Failed to persist max-score rollback failure state.");
            }
            if (dryRun
                && run?.Phase
                    == MaxScoreMaintenancePhase.RolledBack)
            {
                throw;
            }
            var freezeState =
                _metaDatabase.GetPublicReadFreezeState();
            var terminalCommitted =
                terminalReconciliationEligible
                &&
                run is
                {
                    Phase:
                        MaxScoreMaintenancePhase.RolledBack,
                    Status: "rolled_back",
                }
                && !freezeState.IsFrozen;
            Exception? terminalCleanupFailure = null;
            if (terminalCommitted)
            {
                try
                {
                    if (activeLease is not null)
                    {
                        await activeLease.DisposeAsync();
                        activeLease = null;
                    }
                    await EnsureTerminalMutationGateReleasedAsync(
                        manifest.ExpectedPublicationId,
                        CancellationToken.None);
                }
                catch (Exception cleanupError)
                {
                    terminalCleanupFailure =
                        new InvalidOperationException(
                        "Rollback committed, but the durable max-score mutation gate could not be reconciled.",
                        new AggregateException(
                            ex,
                            cleanupError));
                }
            }
            report = terminalCommitted
                     && terminalCleanupFailure is null
                ? ToRollbackReport(
                    run!,
                    rollbackDigest,
                    dryRun: false,
                    validated: true,
                    succeeded: true,
                    resumable: false,
                    publicReadsFrozen: false,
                    startedAtUtc,
                    completedAtUtc:
                        run!.RolledBackAtUtc
                        ?? run.UpdatedAtUtc)
                : terminalCommitted
                ? ToRollbackReport(
                    run!,
                    rollbackDigest,
                    dryRun: false,
                    validated: true,
                    succeeded: false,
                    resumable: true,
                    publicReadsFrozen: false,
                    startedAtUtc,
                    completedAtUtc:
                        run!.RolledBackAtUtc
                        ?? run.UpdatedAtUtc,
                    cleanupPending: true,
                    failureStage:
                        "mutation_gate_cleanup",
                    detail:
                        BoundDetail(
                            terminalCleanupFailure!
                                .Message))
                : run?.Phase
                    == MaxScoreMaintenancePhase.RolledBack
                ? CreateRollbackFailureReport(
                    manifest,
                    manifestDigest,
                    planDigest,
                    rollbackDigest,
                    startedAtUtc,
                    freezeState.IsFrozen,
                    dryRun,
                    ex)
                : run is null
                ? CreateRollbackFailureReport(
                    manifest,
                    manifestDigest,
                    planDigest,
                    rollbackDigest,
                    startedAtUtc,
                    freezeState.IsFrozen,
                    dryRun,
                    ex)
                : ToRollbackReport(
                    run,
                    rollbackDigest,
                    dryRun,
                    validated:
                        run.Phase
                        >= MaxScoreMaintenancePhase
                            .RollbackValidated,
                    succeeded: false,
                    resumable:
                        run.Phase
                        >= MaxScoreMaintenancePhase
                            .RollbackValidating,
                    publicReadsFrozen:
                        freezeState.IsFrozen,
                    startedAtUtc,
                    completedAtUtc: DateTime.UtcNow,
                    failureStage:
                        run.RollbackFailureStage
                        ?? FormatPhase(run.Phase),
                    detail:
                        run.RollbackFailureDetail
                        ?? BoundDetail(ex.Message));
        }
        finally
        {
            authoritativeReadPass?.Dispose();
            if (activeLease is not null)
                await activeLease.DisposeAsync();
        }

        await reportReservation.WriteAsync(
            report,
            ct);
        return report;
    }

    private async Task<MaxScoreMaintenancePlanReport> BuildPlanAsync(
        MaxScoreMaintenanceManifest manifest,
        string manifestDigest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct,
        Action<string>? stageStarted = null)
    {
        BeginPlanEvidenceStage(
            "manifest-promotion-readiness",
            stageStarted);
        manifest = manifest.RequirePromotionReady();
        if (_options.EnableAutomaticPathGeneration)
        {
            throw new InvalidOperationException(
                "Max-score maintenance requires automatic path generation to remain disabled.");
        }

        BeginPlanEvidenceStage(
            "published-context",
            stageStarted);
        var context = LoadPublishedContext(
            manifest.ExpectedPublishedScrapeId,
            manifest.ExpectedPublicationId,
            manifest.Songs.Select(song => song.SongId).ToArray(),
            requireUnfrozen: true);
        BeginPlanEvidenceStage(
            "catalog-identity",
            stageStarted);
        ValidateManifestCatalogIdentity(manifest, context.Catalog);
        BeginPlanEvidenceStage(
            "worker-state",
            stageStarted);
        ValidateWorkerOffline();
        BeginPlanEvidenceStage(
            "path-evidence",
            stageStarted);
        ValidatePathPhase(
            manifest,
            postPromotion: false,
            throwOnMismatch: true);
        BeginPlanEvidenceStage(
            "artifact-evidence",
            stageStarted);
        var artifactEvidence = CreateArtifactEvidence(manifest);
        BeginPlanEvidenceStage(
            "observed-score-evidence",
            stageStarted);
        var observedScoreChecks =
            ApplyObservedScoreChecksTestHook(
                "plan",
                await LoadObservedScoreChecksAsync(
                    manifest,
                    connection,
                    transaction,
                    ct));
        var observedScoresClear =
            observedScoreChecks.All(check => check.Passed);
        BeginPlanEvidenceStage(
            "notification-evidence",
            stageStarted);
        var notificationInspection =
            await _notifications.InspectRoutineStateAsync(
                manifest,
                manifestDigest,
                requireOwnedFreeze: false,
                ct);
        var routineCandidatesClear =
            notificationInspection.CandidateCount == 0;

        BeginPlanEvidenceStage(
            "rank-history-evidence",
            stageStarted);
        var rankHistoryFingerprint =
            await ComputeRankHistoryFingerprintAsync(ct);
        BeginPlanEvidenceStage(
            "publication-population-evidence",
            stageStarted);
        var populationSnapshot =
            await LoadPublishedPopulationSnapshotAsync(
                manifest,
                connection,
                transaction,
                ct);
        ValidatePublishedScopeOwnership(
            context.CatalogSongs,
            populationSnapshot.Population.Keys);
        ValidateManifestPopulationCoverage(
            manifest,
            populationSnapshot.Population);
        BeginPlanEvidenceStage(
            "post-promotion-maxima",
            stageStarted);
        var postPromotionMaxScores =
            BuildPostPromotionMaxScores(
                manifest,
                context.CatalogSongs,
                populationSnapshot.Population.Keys);
        BeginPlanEvidenceStage(
            "complete-score-history-evidence",
            stageStarted);
        var scoreHistoryEvidence =
            await ComputeScoreHistoryEvidenceAsync(
                manifest,
                postPromotionMaxScores,
                connection,
                transaction,
                ct,
                _options
                    .MaxScoreMaintenanceCommandTimeoutSeconds,
                EvidenceCommandTimeoutTestHook);
        var affectedInstruments =
            manifest.Scope.ExpectedChangedInstruments.ToArray();
        BeginPlanEvidenceStage(
            "plan-digest",
            stageStarted);
        var planDigest = ComputePlanDigest(
            manifestDigest,
            context,
            notificationInspection,
            rankHistoryFingerprint,
            populationSnapshot.Evidence,
            scoreHistoryEvidence,
            affectedInstruments,
            artifactEvidence,
            observedScoreChecks);
        return new MaxScoreMaintenancePlanReport(
            MaxScoreMaintenancePlanReport.CurrentReportVersion,
            CanApply:
                routineCandidatesClear
                && observedScoresClear,
            manifestDigest,
            planDigest,
            manifest.ExpectedPublishedScrapeId,
            manifest.ExpectedPublicationId,
            manifest.CatalogContentHash,
            notificationInspection.PublishedScoreSourceFingerprint,
            notificationInspection.NotificationStateFingerprint,
            rankHistoryFingerprint,
            scoreHistoryEvidence.Fingerprint,
            populationSnapshot.Evidence,
            scoreHistoryEvidence,
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
                    $"{manifest.Songs.Count} current rollback and staged immutable artifact trees/hashes validated"),
                new(
                    "observed-scores",
                    observedScoresClear,
                    FormatObservedScoreSummary(
                        observedScoreChecks,
                        observedScoresClear)),
                new(
                    "notifications",
                    routineCandidatesClear,
                    routineCandidatesClear
                        ? "completed marker and visible routine lanes current; zero candidates"
                        : $"completed marker is current but {notificationInspection.CandidateCount:N0} routine candidate(s) remain"),
                new(
                    "population",
                    true,
                    $"published scopes={populationSnapshot.Evidence.ScopeCount:N0}, min={populationSnapshot.Evidence.MinimumTotalEntries:N0}, max={populationSnapshot.Evidence.MaximumTotalEntries:N0}, fingerprint={populationSnapshot.Evidence.Fingerprint}"),
                new(
                    "rank-history",
                    true,
                    $"baseline fingerprint {rankHistoryFingerprint}"),
                new(
                    "score-history",
                    true,
                    $"consumed rows={scoreHistoryEvidence.RowCount:N0}, id-range={FormatNullableRange(scoreHistoryEvidence.MinimumId, scoreHistoryEvidence.MaximumId)}, changed-range={FormatNullableRange(scoreHistoryEvidence.MinimumChangedAtUtc, scoreHistoryEvidence.MaximumChangedAtUtc)}, fingerprint={scoreHistoryEvidence.Fingerprint}"),
            ],
            RoutineCandidates: notificationInspection.Candidates,
            ArtifactEvidence: artifactEvidence,
            ObservedScoreChecks: observedScoreChecks);
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

                MaxScoreMaintenanceArtifactValidator
                    .ValidateManifestSong(
                        _options.DataDirectory,
                        song);
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

    private static void ValidateRollbackSnapshotIdentity(
        MaxScoreMaintenanceRollbackSnapshot snapshot,
        MaxScoreMaintenanceManifest manifest,
        string manifestDigest,
        string planDigest)
    {
        if (!string.Equals(
                snapshot.ManifestSha256,
                manifestDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                snapshot.PlanDigest,
                planDigest,
                StringComparison.Ordinal)
            || snapshot.ExpectedPublishedScrapeId
                != manifest.ExpectedPublishedScrapeId
            || snapshot.ExpectedPublicationId
                != manifest.ExpectedPublicationId
            || snapshot.CatalogVersion
                != manifest.CatalogVersion
            || snapshot.CatalogSchemaVersion
                != manifest.CatalogSchemaVersion
            || snapshot.CatalogSongCount
                != manifest.CatalogSongCount
            || !string.Equals(
                snapshot.CatalogContentHash,
                manifest.CatalogContentHash,
                StringComparison.Ordinal)
            || snapshot.CatalogSourceCapturedAtUtc
                != manifest.CatalogSourceCapturedAtUtc
            || snapshot.Songs.Count != manifest.Songs.Count)
        {
            throw new InvalidOperationException(
                "Rollback snapshot identity differs from the manifest, plan, publication, or catalog.");
        }
        for (var index = 0;
             index < manifest.Songs.Count;
             index++)
        {
            var rollbackSong = snapshot.Songs[index];
            var manifestSong = manifest.Songs[index];
            if (!string.Equals(
                    rollbackSong.SongId,
                    manifestSong.SongId,
                    StringComparison.Ordinal)
                || !ProviderTimestampIdentity.Equivalent(
                    rollbackSong.ExpectedCatalogLastModified,
                    manifestSong.ExpectedCatalogLastModified)
                || !PathIdentityCanonicalEquals(
                    rollbackSong.Path,
                    manifestSong.CurrentPath))
            {
                throw new InvalidOperationException(
                    $"Rollback snapshot does not restore the exact pre-apply path identity for {manifestSong.SongId}.");
            }
        }
    }

    private static void ValidateRollbackEligiblePhase(
        MaxScoreMaintenancePhase phase)
    {
        if (phase is MaxScoreMaintenancePhase.RollbackCaptured
            or >= MaxScoreMaintenancePhase.PathsPromoted
            and <= MaxScoreMaintenancePhase.Validated
            or >= MaxScoreMaintenancePhase.RollbackValidating
            and < MaxScoreMaintenancePhase.RolledBack)
        {
            return;
        }
        throw new InvalidOperationException(
            $"Max-score rollback requires a post-promotion incomplete or resumable rollback phase; current phase is {phase}.");
    }

    private void ValidateRollbackPathPhase(
        MaxScoreMaintenanceRollbackSnapshot snapshot,
        bool throwOnMismatch)
    {
        try
        {
            foreach (var song in snapshot.Songs)
            {
                var state = RequireCurrentPathState(
                    song.SongId,
                    song.SongId,
                    song.ExpectedCatalogLastModified);
                if (!PathIdentityMatches(state, song.Path))
                {
                    throw new InvalidOperationException(
                        $"Rolled-back path identity mismatch for {song.SongId}.");
                }
            }
        }
        catch when (!throwOnMismatch)
        {
        }
    }

    private static bool PathIdentityCanonicalEquals(
        MaxScoreMaintenancePathIdentity first,
        MaxScoreMaintenancePathIdentity second)
        => JsonSerializer.SerializeToUtf8Bytes(
                first.ValidateAndNormalize(
                    nameof(first),
                    true),
                MaxScoreMaintenanceJson.Canonical)
            .AsSpan()
            .SequenceEqual(
                JsonSerializer.SerializeToUtf8Bytes(
                    second.ValidateAndNormalize(
                        nameof(second),
                        true),
                    MaxScoreMaintenanceJson.Canonical));

    private static string ComputeManifestPathFingerprint(
        MaxScoreMaintenanceManifest manifest,
        bool postPromotion)
        => ComputePathFingerprint(
            manifest.Songs.Select(song => new
            {
                song.SongId,
                Path = postPromotion
                    ? song.StagedPath with
                    {
                        Revision = checked(
                            song.CurrentPath.Revision + 1),
                    }
                    : song.CurrentPath,
            }));

    private static string ComputeRollbackPathFingerprint(
        MaxScoreMaintenanceRollbackSnapshot snapshot)
        => ComputePathFingerprint(
            snapshot.Songs.Select(song => new
            {
                song.SongId,
                song.Path,
            }));

    private static string ComputePathFingerprint<T>(
        IEnumerable<T> rows)
        => Convert.ToHexStringLower(
            SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(
                    rows,
                    MaxScoreMaintenanceJson.Canonical)));

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

    private async Task ValidateRollbackRuntimeQuiescenceAsync(
        CancellationToken ct)
    {
        MaxScoreMaintenanceRollbackRuntimeState state;
        if (RollbackRuntimeStateTestHook is not null)
        {
            state = RollbackRuntimeStateTestHook();
        }
        else
        {
            await using var connection =
                await _dataSource.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 5;
            command.CommandText = """
                SELECT
                    COUNT(*) FILTER (
                        WHERE application_name IN (
                            'fst-max-score-maintenance',
                            'fst-max-score-resume',
                            'fst-max-score-rollback')
                    )::INTEGER,
                    COUNT(*) FILTER (
                        WHERE application_name IN (
                            'fstworker-scraper',
                            'fstworker-registration',
                            'FST Scraper Worker')
                    )::INTEGER,
                    (
                        SELECT COUNT(*)::INTEGER
                        FROM pg_locks lock
                        JOIN pg_stat_activity waiting
                          ON waiting.pid = lock.pid
                        WHERE NOT lock.granted
                          AND waiting.datname =
                              current_database()
                    )
                FROM pg_stat_activity
                WHERE pid <> pg_backend_pid()
                  AND datname = current_database()
                """;
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new InvalidOperationException(
                    "Rollback runtime quiescence evidence was unavailable.");
            }
            state = new MaxScoreMaintenanceRollbackRuntimeState(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2));
        }
        if (state.MaintenanceBackendCount != 0
            || state.WorkerBackendCount != 0
            || state.WaitingLockCount != 0)
        {
            throw new InvalidOperationException(
                "Rollback requires zero maintenance/worker backends and zero waiting locks: "
                + $"maintenance={state.MaintenanceBackendCount}, "
                + $"worker={state.WorkerBackendCount}, "
                + $"waitingLocks={state.WaitingLockCount}.");
        }
    }

    private async Task EnsureTerminalMutationGateReleasedAsync(
        long publicationId,
        CancellationToken ct)
    {
        if (await IsTerminalMutationGateReleasedAsync(
                publicationId,
                ct))
        {
            return;
        }
        var injectedFailure =
            TerminalMutationGateCleanupFailureTestHook
                ?.Invoke();
        if (injectedFailure is not null)
            throw injectedFailure;

        var cleanupLease =
            await _metaDatabase
                .AcquireMaxScoreMaintenanceRollbackLeaseAsync(
                    publicationId,
                    ct);
        try
        {
            await cleanupLease.VerifyHeldAsync(
                requireSourceLocks: false,
                ct);
        }
        finally
        {
            await cleanupLease.DisposeAsync();
        }

        if (!await IsTerminalMutationGateReleasedAsync(
                publicationId,
                ct))
        {
            throw new InvalidOperationException(
                "The durable max-score mutation gate remains asserted after terminal rollback cleanup.");
        }
    }

    private async Task<bool> IsTerminalMutationGateReleasedAsync(
        long publicationId,
        CancellationToken ct)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = """
            SELECT
                current_publication_id = @publicationId
                AND max_score_mutation_gate_token IS NULL
                AND max_score_mutation_gate_publication_id
                    IS NULL
                AND max_score_mutation_gate_backend_pid IS NULL
                AND max_score_mutation_gate_backend_start
                    IS NULL
                AND max_score_mutation_gate_acquired_at IS NULL
            FROM scrape_publication_state
            WHERE id = TRUE
            """;
        command.Parameters.AddWithValue(
            "publicationId",
            publicationId);
        return await command.ExecuteScalarAsync(ct)
            is true;
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
                    score_history_fingerprint,
                    population_evidence,
                    score_history_evidence,
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
                    @scoreHistoryFingerprint,
                    @populationEvidence,
                    @scoreHistoryEvidence,
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
                    insert.Parameters.AddWithValue(
                        "scoreHistoryFingerprint",
                        plan.ScoreHistoryFingerprint);
                    insert.Parameters.Add(
                        "populationEvidence",
                        NpgsqlDbType.Jsonb).Value =
                        JsonSerializer.Serialize(
                            plan.PopulationEvidence,
                            MaxScoreMaintenanceJson.Canonical);
                    insert.Parameters.Add(
                        "scoreHistoryEvidence",
                        NpgsqlDbType.Jsonb).Value =
                        JsonSerializer.Serialize(
                            plan.ScoreHistoryEvidence,
                            MaxScoreMaintenanceJson.Canonical);
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
                    path_artifact_tree_sha256,
                    path_artifact_file_count,
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
                    @artifactTreeSha256,
                    @artifactFileCount,
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
            manifest.CatalogVersion,
            manifest.CatalogSchemaVersion,
            manifest.CatalogContentHash,
            manifest.CatalogSongCount,
            manifest.CatalogSourceCapturedAtUtc,
            manifest.Songs
                .Select(song =>
                    new MaxScoreMaintenanceRollbackSong(
                        song.SongId,
                        song.ExpectedCatalogLastModified,
                        song.CurrentPath))
                .ToArray())
            .ValidateAndNormalize();

    private async Task ValidatePersistedRollbackEvidenceAsync(
        IMaxScoreMaintenanceLease lease,
        MaxScoreMaintenanceManifest manifest,
        string manifestDigest,
        MaxScoreMaintenanceRunState run,
        CancellationToken ct)
    {
        if (run.Phase
            < MaxScoreMaintenancePhase.RollbackCaptured)
        {
            throw new InvalidOperationException(
                "Persisted rollback evidence is required after rollback capture.");
        }
        var rollbackPath = run.RollbackSnapshotPath
            ?? throw new InvalidOperationException(
                "Rollback checkpoint path is missing.");
        var rollbackSha256 = run.RollbackSnapshotSha256
            ?? throw new InvalidOperationException(
                "Rollback checkpoint SHA-256 is missing.");
        var resolvedPath = ResolveDataPath(rollbackPath);
        if (!PathsEquivalent(resolvedPath, rollbackPath))
        {
            throw new InvalidOperationException(
                "Rollback checkpoint path no longer resolves to its persisted identity.");
        }

        var rollbackSongs =
            await lease.ExecuteTransactionAsync(
                "rollback-evidence-validation",
                requireSourceLocks: true,
                (connection, transaction, token) =>
                    LoadRollbackSongsAsync(
                        connection,
                        transaction,
                        manifestDigest,
                        token),
                IsolationLevel.RepeatableRead,
                ct);
        if (rollbackSongs.Count != manifest.Songs.Count
            || !rollbackSongs
                .Select(song => song.SongId)
                .SequenceEqual(
                    manifest.Songs.Select(song => song.SongId),
                    StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Rollback database evidence does not cover the exact manifest song identity.");
        }

        var expectedSnapshot =
            new MaxScoreMaintenanceRollbackSnapshot(
                MaxScoreMaintenanceRollbackSnapshot
                    .CurrentSnapshotVersion,
                NormalizeDatabaseTimestamp(run.CreatedAtUtc),
                manifestDigest,
                run.PlanDigest,
                run.ExpectedPublishedScrapeId,
                run.ExpectedPublicationId,
                manifest.CatalogVersion,
                manifest.CatalogSchemaVersion,
                run.ExpectedCatalogHash,
                run.ExpectedCatalogSongCount,
                manifest.CatalogSourceCapturedAtUtc,
                rollbackSongs)
            .ValidateAndNormalize();
        await MaxScoreMaintenanceFileStore
            .ValidateCanonicalRollbackSnapshotAsync(
                _options.DataDirectory,
                resolvedPath,
                rollbackSha256,
                expectedSnapshot,
                ct);
    }

    private async Task ValidateRollbackRunAndEvidenceAsync(
        MaxScoreMaintenanceManifest manifest,
        string manifestDigest,
        string rollbackDigest,
        string rollbackFullPath,
        MaxScoreMaintenanceRollbackSnapshot rollbackSnapshot,
        MaxScoreMaintenanceRunState run,
        CancellationToken ct)
    {
        if (run.Phase == MaxScoreMaintenancePhase.Completed
                || run.Status is "completed")
        {
            throw new InvalidOperationException(
                "A successfully completed max-score run cannot be rolled back by the incomplete-run recovery command.");
        }
        if (run.Phase == MaxScoreMaintenancePhase.RolledBack)
        {
            if (!string.Equals(
                    run.Status,
                    "rolled_back",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Rolled-back phase has an invalid terminal status.");
            }
        }
        else
        {
            ValidateRollbackEligiblePhase(run.Phase);
            if (run.Status is not ("running" or "failed"))
            {
                throw new InvalidOperationException(
                    "Rollback requires a running or failed incomplete maintenance run.");
            }
            RequireOwnedFreeze(manifest, manifestDigest);
            _ = LoadPublishedContext(
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
        }
        if (run.RollbackSnapshotPath is null
                || run.RollbackSnapshotSha256 is null
                || !PathsEquivalent(
                    rollbackFullPath,
                    run.RollbackSnapshotPath)
                || !string.Equals(
                    rollbackDigest,
                    run.RollbackSnapshotSha256,
                    StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Rollback file path or digest differs from the durable checkpoint.");
        }

        await using var connection =
                await _dataSource.OpenConnectionAsync(ct);
        await using var transaction =
                await connection.BeginTransactionAsync(
                    IsolationLevel.RepeatableRead,
                    ct);
        var rollbackSongs =
                await LoadRollbackSongsAsync(
                    connection,
                    transaction,
                    manifestDigest,
                    ct);
        var expectedSnapshot =
                new MaxScoreMaintenanceRollbackSnapshot(
                    MaxScoreMaintenanceRollbackSnapshot
                        .CurrentSnapshotVersion,
                    NormalizeDatabaseTimestamp(run.CreatedAtUtc),
                    manifestDigest,
                    run.PlanDigest,
                    run.ExpectedPublishedScrapeId,
                    run.ExpectedPublicationId,
                    manifest.CatalogVersion,
                    manifest.CatalogSchemaVersion,
                    run.ExpectedCatalogHash,
                    run.ExpectedCatalogSongCount,
                    manifest.CatalogSourceCapturedAtUtc,
                    rollbackSongs)
                .ValidateAndNormalize();
        if (!rollbackSnapshot.SerializeCanonical()
                    .AsSpan()
                    .SequenceEqual(
                        expectedSnapshot.SerializeCanonical()))
        {
            throw new InvalidOperationException(
                "Rollback file, database rollback rows, and manifest scope are not exactly equal.");
        }
        await transaction.CommitAsync(ct);
    }

    private static async Task<
        IReadOnlyList<MaxScoreMaintenanceRollbackSong>>
        LoadRollbackSongsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string manifestDigest,
            CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT song_id,
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
                   path_generation_pending,
                   path_artifact_tree_sha256,
                   path_artifact_file_count
            FROM max_score_maintenance_rollback_songs
            WHERE manifest_sha256 = @manifestSha256
            ORDER BY song_id
            """;
        command.Parameters.AddWithValue(
            "manifestSha256",
            manifestDigest);
        var songs =
            new List<MaxScoreMaintenanceRollbackSong>();
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            songs.Add(
                new MaxScoreMaintenanceRollbackSong(
                    reader.GetString(0),
                    reader.GetString(1),
                    new MaxScoreMaintenancePathIdentity(
                        reader.GetInt64(2),
                        ReadNullableString(reader, 3),
                        ReadNullableString(reader, 4),
                        ReadNullableDateTime(reader, 5),
                        ReadNullableString(reader, 6),
                        ReadNullableString(reader, 7),
                        ReadNullableString(reader, 8),
                        ReadNullableString(reader, 9),
                        reader.GetFieldValue<string[]>(10),
                        new MaxScoreMaintenanceMaxima(
                            ReadNullableInt32(reader, 11),
                            ReadNullableInt32(reader, 12),
                            ReadNullableInt32(reader, 13),
                            ReadNullableInt32(reader, 14),
                            ReadNullableInt32(reader, 15),
                            ReadNullableInt32(reader, 16),
                            ReadNullableInt32(reader, 17),
                            ReadNullableInt32(reader, 18)),
                        reader.GetBoolean(19),
                        ReadNullableString(reader, 20),
                        ReadNullableInt32(reader, 21))));
        }
        return songs;
    }

    private async Task ValidateCompletedMaintenanceAsync(
        IMaxScoreMaintenanceLease lease,
        MaxScoreMaintenanceManifest manifest,
        string manifestDigest,
        MaxScoreMaintenanceRunState run,
        MaxScoreMaintenanceReadSnapshot readSnapshot,
        CancellationToken ct)
        => await ValidateCompletedMaintenanceCoreAsync(
            lease,
            manifest,
            manifestDigest,
            rollbackSnapshot: null,
            run,
            readSnapshot,
            ct);

    private async Task ValidateCompletedRollbackAsync(
        IMaxScoreMaintenanceLease lease,
        MaxScoreMaintenanceManifest manifest,
        string manifestDigest,
        MaxScoreMaintenanceRollbackSnapshot rollbackSnapshot,
        MaxScoreMaintenanceRunState run,
        MaxScoreMaintenanceReadSnapshot readSnapshot,
        CancellationToken ct)
        => await ValidateCompletedMaintenanceCoreAsync(
            lease,
            manifest,
            manifestDigest,
            rollbackSnapshot,
            run,
            readSnapshot,
            ct);

    private async Task ValidateCompletedMaintenanceCoreAsync(
        IMaxScoreMaintenanceLease lease,
        MaxScoreMaintenanceManifest manifest,
        string manifestDigest,
        MaxScoreMaintenanceRollbackSnapshot? rollbackSnapshot,
        MaxScoreMaintenanceRunState run,
        MaxScoreMaintenanceReadSnapshot readSnapshot,
        CancellationToken ct)
    {
        var rollback = rollbackSnapshot is not null;
        if (!_persistence
                .IsMaxScoreMaintenancePublishedReadPassActive)
        {
            throw new InvalidOperationException(
                "Final max-score validation requires the strict published-source read context.");
        }
        RequireOwnedFreeze(manifest, manifestDigest);
        var changedSongCount = rollback
            ? run.RollbackRestoredSongCount
            : run.PromotedSongCount;
        var rebuiltInstrumentCount = rollback
            ? run.RollbackRebuiltInstrumentCount
            : run.RebuiltInstrumentCount;
        var visibleDeliveryCount = rollback
            ? run.RollbackVisibleDeliveryCount
            : run.VisibleDeliveryCount;
        var cacheEvidence = rollback
            ? run.RollbackCacheEvidence
            : run.CacheEvidence;
        var stagedCacheEntryCount = rollback
            ? run.RollbackStagedCacheEntryCount
            : run.StagedCacheEntryCount;
        var notificationMaintenanceRunId = rollback
            ? run.RollbackNotificationMaintenanceRunId
            : run.NotificationMaintenanceRunId;
        if (changedSongCount != manifest.Songs.Count
            || rebuiltInstrumentCount
                != manifest.Scope.ExpectedChangedInstruments.Count
            || visibleDeliveryCount != 0)
        {
            throw new InvalidOperationException(
                "Maintenance checkpoint does not cover the exact song/instrument scope or zero-visible notification contract.");
        }
        if (rollback)
        {
            ValidateRollbackPathPhase(
                rollbackSnapshot!,
                throwOnMismatch: true);
        }
        else
        {
            ValidatePathPhase(
                manifest,
                postPromotion: true,
                throwOnMismatch: true);
        }
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
        var currentCacheEvidence =
            await BuildCacheEvidenceAsync(
                manifest,
                readSnapshot,
                ct);
        if (cacheEvidence is null
            || cacheEvidence != currentCacheEvidence)
        {
            throw new InvalidOperationException(
                "Staged cache content evidence changed or no longer matches the authoritative target scopes/accounts.");
        }

        await lease.ExecuteTransactionAsync(
            "final-state-validation",
            requireSourceLocks: true,
            async (conn, transaction, token) =>
            {
                await using (var cacheLocks = conn.CreateCommand())
                {
                    cacheLocks.Transaction = transaction;
                    cacheLocks.CommandText = """
                LOCK TABLE api_response_cache_staging
                    IN SHARE MODE;
                LOCK TABLE publication_api_response_cache_staging
                    IN SHARE MODE;
                """;
                    await cacheLocks.ExecuteNonQueryAsync(token);
                }
                if (rollback)
                {
                    await MaxScoreMaintenanceCacheEntryEvidenceStore
                        .ValidateRollbackAsync(
                            manifestDigest,
                            manifest.ExpectedPublicationId,
                            stagedCacheEntryCount,
                            conn,
                            transaction,
                            token,
                            _options
                                .MaxScoreMaintenanceCommandTimeoutSeconds);
                }
                else
                {
                    await MaxScoreMaintenanceCacheEntryEvidenceStore
                        .ValidateAsync(
                            manifestDigest,
                            manifest.ExpectedPublicationId,
                            stagedCacheEntryCount,
                            conn,
                            transaction,
                            token,
                            _options
                                .MaxScoreMaintenanceCommandTimeoutSeconds);
                }
                var scoreHistorySnapshot =
                    await MaxScoreMaintenanceScoreHistoryEvidenceCalculator
                        .ComputeAsync(
                        manifest,
                        readSnapshot.ScoreHistoryEvidenceMaxScores,
                        conn,
                        transaction,
                        token,
                        _options
                            .MaxScoreMaintenanceCommandTimeoutSeconds,
                        EvidenceCommandTimeoutTestHook);
                var scoreHistoryEvidence =
                    scoreHistorySnapshot.Evidence;
                if (scoreHistoryEvidence
                        != readSnapshot.ScoreHistoryEvidence
                    || scoreHistoryEvidence
                        != run.ScoreHistoryEvidence)
                {
                    throw new InvalidOperationException(
                        "Score history changed during max-score maintenance.");
                }
                if (!scoreHistorySnapshot
                        .AffectedPlayerStatsAccounts
                        .SequenceEqual(
                            readSnapshot
                                .AffectedPlayerStatsAccounts,
                            StringComparer.Ordinal)
                    || !scoreHistorySnapshot
                        .AffectedRegisteredAccounts
                        .SequenceEqual(
                            readSnapshot
                                .AffectedRegisteredAccounts,
                            StringComparer.Ordinal)
                    || !scoreHistorySnapshot
                        .OverlayOnlyRegisteredAccounts
                        .SequenceEqual(
                            readSnapshot
                                .OverlayOnlyRegisteredAccounts,
                            StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Affected score-history account selectors changed during max-score maintenance.");
                }
                if (rollback)
                {
                    var rollbackScoreHistorySnapshot =
                        HasPositiveScoreHistoryMaximum(
                            readSnapshot
                                .RollbackScoreHistoryEvidenceMaxScores,
                            readSnapshot.PublishedScopes)
                            ? await MaxScoreMaintenanceScoreHistoryEvidenceCalculator
                                .ComputeAsync(
                                    manifest,
                                    readSnapshot
                                        .RollbackScoreHistoryEvidenceMaxScores,
                                    conn,
                                    transaction,
                                    token,
                                    _options
                                        .MaxScoreMaintenanceCommandTimeoutSeconds,
                                    EvidenceCommandTimeoutTestHook,
                                    requirePositiveChangedMaxima: false,
                                    seededAffectedAccounts:
                                        scoreHistorySnapshot
                                            .AffectedPlayerStatsAccounts)
                            : new MaxScoreMaintenanceScoreHistorySnapshot(
                                EmptyScoreHistoryEvidence(),
                                scoreHistorySnapshot
                                    .AffectedPlayerStatsAccounts,
                                scoreHistorySnapshot
                                    .AffectedRegisteredAccounts,
                                scoreHistorySnapshot
                                    .OverlayOnlyRegisteredAccounts);
                    if (rollbackScoreHistorySnapshot.Evidence
                        != readSnapshot
                            .RollbackScoreHistoryEvidence)
                    {
                        throw new InvalidOperationException(
                            "Rollback score history changed during max-score maintenance.");
                    }
                }

                var publishedInstruments = readSnapshot.PublishedScopes
                    .Select(scope => scope.Instrument)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(
                        instrument => instrument,
                        StringComparer.Ordinal)
                    .ToArray();
                var invalidPlayerStatsAccountCount =
                    await MaxScoreMaintenanceDerivedStateService
                        .CountInvalidPlayerStatsAccountsAsync(
                            readSnapshot.AffectedPlayerStatsAccounts,
                            publishedInstruments,
                            NormalizeDatabaseTimestamp(
                                run.CreatedAtUtc),
                            conn,
                            transaction,
                            token);
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                ConfigureEvidenceCommandTimeout(
                    cmd,
                    "final-state-evidence");
                cmd.CommandText = $"""
            WITH {PublishedSoloScopeSql.CurrentResolvedAllEntriesCte},
            expected(
                song_id,
                instrument,
                max_score,
                total_entries) AS (
                SELECT *
                FROM unnest(
                    @songIds::TEXT[],
                    @instruments::TEXT[],
                    @maxScores::INTEGER[],
                    @totalEntries::BIGINT[])
            ), current_stats AS MATERIALIZED (
                SELECT song_id,
                       instrument,
                       entry_count,
                       max_score
                FROM song_stats
                WHERE instrument =
                    ANY(@affectedInstruments)
            ), stats AS (
                SELECT
                    (SELECT COUNT(*)::INTEGER
                     FROM expected) AS expected_count,
                    (SELECT COUNT(*)::INTEGER
                     FROM current_stats) AS actual_count,
                    (
                        SELECT COUNT(*)::INTEGER
                        FROM expected
                        FULL JOIN current_stats current
                          USING (song_id, instrument)
                        WHERE expected.song_id IS NULL
                           OR current.song_id IS NULL
                           OR current.max_score
                                IS DISTINCT FROM
                                expected.max_score
                           OR current.entry_count
                                IS DISTINCT FROM
                                expected.total_entries
                    ) AS mismatch_count
            ), expected_scope_counts AS (
                SELECT instrument,
                       COUNT(*)::INTEGER AS scope_count
                FROM expected
                GROUP BY instrument
            ), published_rank_accounts AS (
                SELECT DISTINCT
                       resolved.instrument,
                       resolved.account_id
                FROM resolved_rows resolved
                JOIN expected
                  ON expected.song_id =
                        resolved.song_id
                 AND expected.instrument =
                        resolved.instrument
            ), rankings AS (
                SELECT
                    COUNT(*)::BIGINT AS row_count,
                    COUNT(*) FILTER (
                        WHERE scope.scope_count IS NULL
                           OR ranking.total_charted_songs
                                IS DISTINCT FROM
                                scope.scope_count
                           OR published.account_id IS NULL
                    )::BIGINT AS invalid_count
                FROM account_rankings ranking
                LEFT JOIN expected_scope_counts scope
                  ON scope.instrument =
                        ranking.instrument
                LEFT JOIN published_rank_accounts published
                  ON published.instrument =
                        ranking.instrument
                 AND published.account_id =
                        ranking.account_id
                WHERE ranking.instrument =
                    ANY(@affectedInstruments)
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
            ), missing_player_stats AS (
                SELECT @invalidPlayerStatsAccountCount::BIGINT
                    AS row_count
            ), missing_leaderboard_rivals AS (
                SELECT COUNT(*)::BIGINT AS row_count
                FROM (
                    SELECT DISTINCT account_id
                    FROM registered_users
                    WHERE btrim(account_id) <> ''
                ) registered
                CROSS JOIN unnest(@affectedInstruments::TEXT[])
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
            SELECT stats.expected_count,
                   stats.actual_count,
                   stats.mismatch_count,
                   rankings.row_count,
                   rankings.invalid_count,
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
                var affectedInstruments = manifest.Scope
                    .ExpectedChangedInstruments
                    .ToHashSet(StringComparer.Ordinal);
                var expectedScopes = readSnapshot.Population
                    .Where(pair =>
                        affectedInstruments.Contains(
                            pair.Key.Instrument))
                    .OrderBy(
                        pair => pair.Key.SongId,
                        StringComparer.Ordinal)
                    .ThenBy(
                        pair => pair.Key.Instrument,
                        StringComparer.Ordinal)
                    .Select(pair =>
                    {
                        readSnapshot.PostPromotionMaxScores.TryGetValue(
                            pair.Key.SongId,
                            out var songMaxScores);
                        return (
                            pair.Key.SongId,
                            pair.Key.Instrument,
                            MaxScore: songMaxScores?.GetByInstrument(
                                pair.Key.Instrument),
                            TotalEntries: pair.Value);
                    })
                    .ToArray();
                if (expectedScopes.Length == 0)
                {
                    throw new InvalidOperationException(
                        "Final validation has no frozen published scopes for the affected instruments.");
                }
                cmd.Parameters.Add(
                    "songIds",
                    NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                    expectedScopes.Select(pair => pair.SongId).ToArray();
                cmd.Parameters.Add(
                    "instruments",
                    NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                    expectedScopes.Select(pair => pair.Instrument).ToArray();
                cmd.Parameters.Add(
                    "maxScores",
                    NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                    expectedScopes.Select(pair => pair.MaxScore).ToArray();
                cmd.Parameters.Add(
                    "totalEntries",
                    NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value =
                    expectedScopes.Select(pair =>
                            pair.TotalEntries)
                        .ToArray();
                cmd.Parameters.Add(
                    "affectedInstruments",
                    NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                    affectedInstruments
                        .OrderBy(
                            instrument => instrument,
                            StringComparer.Ordinal)
                        .ToArray();
                cmd.Parameters.AddWithValue(
                    "manifestSha256",
                    manifestDigest);
                cmd.Parameters.AddWithValue(
                    "notificationMaintenanceRunId",
                    notificationMaintenanceRunId
                    ?? throw new InvalidOperationException(
                        "Notification maintenance audit run is missing."));
                cmd.Parameters.AddWithValue(
                    "purpose",
                    MaxScoreMaintenanceSchema.Purpose);
                cmd.Parameters.AddWithValue(
                    "publicationId",
                    manifest.ExpectedPublicationId);
                cmd.Parameters.Add(
                    "rankMethods",
                    NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                    LeaderboardRivalsCalculator.RankMethods;
                cmd.Parameters.AddWithValue(
                    "invalidPlayerStatsAccountCount",
                    invalidPlayerStatsAccountCount);
                await using var reader =
                    await cmd.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    throw new InvalidOperationException(
                        "Post-maintenance validation returned no evidence row.");
                }
                var validation = new
                {
                    ExpectedStats = reader.GetInt32(0),
                    ActualStats = reader.GetInt32(1),
                    InvalidStats = reader.GetInt32(2),
                    RankingRows = reader.GetInt64(3),
                    InvalidRankings = reader.GetInt64(4),
                    RollbackRows = reader.GetInt32(5),
                    NotificationRows = reader.GetInt32(6),
                    VisibleNotifications = reader.GetInt32(7),
                    StagedCacheRows = reader.GetInt64(8),
                    CompositeRows = reader.GetInt64(9),
                    FamilyRows = reader.GetInt64(10),
                    ComboRows = reader.GetInt64(11),
                    MissingPlayerStats = reader.GetInt64(12),
                    MissingLeaderboardRivals = reader.GetInt64(13),
                    InvalidBandRankings = reader.GetInt64(14),
                };
                if (validation.ExpectedStats
                        != expectedScopes.Length
                    || validation.ActualStats
                        != expectedScopes.Length
                    || validation.InvalidStats != 0
                    || validation.InvalidRankings != 0
                    || validation.RollbackRows != manifest.Songs.Count
                    || validation.NotificationRows != 1
                    || validation.VisibleNotifications != 0
                    || validation.StagedCacheRows
                        != stagedCacheEntryCount
                    || validation.MissingPlayerStats != 0
                    || validation.MissingLeaderboardRivals != 0
                    || validation.InvalidBandRankings != 0)
                {
                    throw new InvalidOperationException(
                        $"Post-maintenance validation failed: stats={validation.ActualStats}/{validation.ExpectedStats} (mismatches={validation.InvalidStats}), rankings={validation.RankingRows} (invalid={validation.InvalidRankings}), rollback={validation.RollbackRows}/{manifest.Songs.Count}, notificationRows={validation.NotificationRows}, visible={validation.VisibleNotifications}, cache={validation.StagedCacheRows}/{stagedCacheEntryCount}, composite={validation.CompositeRows}, family={validation.FamilyRows}, combo={validation.ComboRows}, invalidPlayerStats={validation.MissingPlayerStats}, missingRivals={validation.MissingLeaderboardRivals}, invalidBandRankings={validation.InvalidBandRankings}.");
                }

            },
            IsolationLevel.RepeatableRead,
            ct);
    }

    private async Task<MaxScoreMaintenanceCacheEvidence>
        BuildCacheEvidenceAsync(
            MaxScoreMaintenanceManifest manifest,
            MaxScoreMaintenanceReadSnapshot readSnapshot,
            CancellationToken ct)
    {
        if (!_persistence
                .IsMaxScoreMaintenancePublishedReadPassActive)
        {
            throw new InvalidOperationException(
                "Cache evidence requires the strict published-source read context.");
        }

        var targetPairs = manifest.Songs
            .SelectMany(song => song.ChangedInstruments.Select(
                instrument => (
                    song.SongId,
                    Instrument: instrument)))
            .OrderBy(pair => pair.SongId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Instrument, StringComparer.Ordinal)
            .ToArray();
        var targetKeys = targetPairs
            .Select(pair => pair.SongId)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                songId => songId,
                songId => LeaderboardCacheKeys.LeaderboardAll(
                    songId,
                    LeaderboardCacheKeys.SongDetailPreviewTop,
                    leeway: null),
                StringComparer.Ordinal);
        var accountKeys =
            readSnapshot.AffectedRegisteredAccounts
                .ToDictionary(
                    accountId => accountId,
                    accountId => $"player:{accountId}:::",
                    StringComparer.OrdinalIgnoreCase);
        var requestedKeys = targetKeys.Values
            .Concat(accountKeys.Values)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        long entryCount;
        string contentFingerprint;
        var payloads =
            new Dictionary<string, byte[]>(
                StringComparer.Ordinal);
        var actualPublishedScopeCacheKeys =
            new HashSet<string>(StringComparer.Ordinal);
        await using (var connection =
                     await _dataSource.OpenConnectionAsync(ct))
        {
            await using (var aggregate =
                         connection.CreateCommand())
            {
                ConfigureEvidenceCommandTimeout(
                    aggregate,
                    "publication-cache-aggregate-evidence");
                aggregate.CommandText = """
                    WITH rows AS MATERIALIZED (
                        SELECT cache_key,
                               etag,
                               encode(
                                   digest(
                                       json_data,
                                       'sha256'),
                                   'hex') AS json_sha256
                        FROM publication_api_response_cache_staging
                        WHERE publication_id = @publicationId
                    ), evidence AS (
                        SELECT COUNT(*)::BIGINT AS row_count,
                               COALESCE(
                                   MIN(cache_key),
                                   '') AS minimum_key,
                               COALESCE(
                                   MAX(cache_key),
                                   '') AS maximum_key,
                               COALESCE(
                                   SUM(
                                       hashtextextended(
                                           concat_ws(
                                               ':',
                                               cache_key,
                                               etag,
                                               json_sha256),
                                           0)::NUMERIC),
                                   0)::TEXT AS hash_sum,
                               COALESCE(
                                   bit_xor(
                                       hashtextextended(
                                           concat_ws(
                                               ':',
                                               cache_key,
                                               etag,
                                               json_sha256),
                                           1)),
                                   0)::TEXT AS hash_xor
                        FROM rows
                    )
                    SELECT row_count,
                           encode(
                               digest(
                                   convert_to(
                                       concat_ws(
                                           ':',
                                           row_count,
                                           minimum_key,
                                           maximum_key,
                                           hash_sum,
                                           hash_xor),
                                       'UTF8'),
                                   'sha256'),
                               'hex')
                    FROM evidence
                    """;
                aggregate.Parameters.AddWithValue(
                    "publicationId",
                    manifest.ExpectedPublicationId);
                await using var reader =
                    await aggregate.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    throw new InvalidOperationException(
                        "Staged cache aggregate evidence was unavailable.");
                }
                entryCount = reader.GetInt64(0);
                contentFingerprint = reader.GetString(1);
            }

            await using (var keys = connection.CreateCommand())
            {
                ConfigureEvidenceCommandTimeout(
                    keys,
                    "publication-cache-key-evidence");
                keys.CommandText = """
                    SELECT cache_key
                    FROM publication_api_response_cache_staging
                    WHERE publication_id = @publicationId
                      AND (
                          cache_key LIKE 'lb:%'
                          OR cache_key LIKE
                              'lb-rank-offsets:%'
                          OR cache_key LIKE
                              'lb-rank-offsets-v1:%'
                      )
                    """;
                keys.Parameters.AddWithValue(
                    "publicationId",
                    manifest.ExpectedPublicationId);
                await using var reader =
                    await keys.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    actualPublishedScopeCacheKeys.Add(
                        reader.GetString(0));
                }
            }

            if (requestedKeys.Length > 0)
            {
                await using var payload = connection.CreateCommand();
                ConfigureEvidenceCommandTimeout(
                    payload,
                    "publication-cache-payload-evidence");
                payload.CommandText = """
                    SELECT cache_key, json_data
                    FROM publication_api_response_cache_staging
                    WHERE publication_id = @publicationId
                      AND cache_key = ANY(@cacheKeys)
                    """;
                payload.Parameters.AddWithValue(
                    "publicationId",
                    manifest.ExpectedPublicationId);
                payload.Parameters.Add(
                    "cacheKeys",
                    NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                    requestedKeys;
                await using var reader =
                    await payload.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    payloads[reader.GetString(0)] =
                        reader.GetFieldValue<byte[]>(1);
                }
            }
        }

        if (entryCount <= 0)
        {
            throw new InvalidOperationException(
                "Maintenance cache staging produced no aggregate evidence.");
        }

        var expectedPublishedScopeCacheKeys =
            BuildExpectedPublishedScopeCacheKeys(
                readSnapshot);
        if (!actualPublishedScopeCacheKeys.SetEquals(
                expectedPublishedScopeCacheKeys))
        {
            var missing = expectedPublishedScopeCacheKeys
                .Except(
                    actualPublishedScopeCacheKeys,
                    StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .Take(5);
            var unexpected = actualPublishedScopeCacheKeys
                .Except(
                    expectedPublishedScopeCacheKeys,
                    StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .Take(5);
            throw new InvalidOperationException(
                "Staged publication-scope cache key inventory differs from the frozen catalog and population snapshot: "
                + $"missing=[{string.Join(",", missing)}], "
                + $"unexpected=[{string.Join(",", unexpected)}].");
        }
        var expectedPublishedScopeKeyFingerprint =
            ComputeEvidenceFingerprint(
                expectedPublishedScopeCacheKeys
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray());

        var expectedScopes = new List<CacheScopeFingerprintRow>();
        var actualScopes = new List<CacheScopeFingerprintRow>();
        foreach (var pair in targetPairs)
        {
            var result =
                _persistence
                    .GetCurrentStateLeaderboardWithCount(
                        pair.SongId,
                        pair.Instrument,
                        LeaderboardCacheKeys.SongDetailPreviewTop)
                ?? throw new InvalidOperationException(
                    $"Authoritative target scope {pair.SongId}/{pair.Instrument} is unavailable.");
            if (!readSnapshot.Population.TryGetValue(
                    (pair.SongId, pair.Instrument),
                    out var publishedPopulation))
            {
                throw new InvalidOperationException(
                    $"Publication population is missing for cache scope {pair.SongId}/{pair.Instrument}.");
            }
            expectedScopes.Add(new CacheScopeFingerprintRow(
                pair.SongId,
                pair.Instrument,
                Math.Max(
                    publishedPopulation,
                    result.TotalCount),
                result.TotalCount,
                result.Entries
                    .Select(entry =>
                        new CacheEntryFingerprintRow(
                            entry.AccountId,
                            entry.Score,
                            entry.Rank))
                    .ToArray()));

            var targetKey = targetKeys[pair.SongId];
            if (!payloads.TryGetValue(
                    targetKey,
                    out var payload))
            {
                throw new InvalidOperationException(
                    $"Staged cache is missing target song key {targetKey}.");
            }
            using var document = JsonDocument.Parse(payload);
            var instrumentPayload = document.RootElement
                .GetProperty("instruments")
                .EnumerateArray()
                .SingleOrDefault(item =>
                    string.Equals(
                        item.GetProperty("instrument")
                            .GetString(),
                        pair.Instrument,
                        StringComparison.Ordinal));
            if (instrumentPayload.ValueKind
                == JsonValueKind.Undefined)
            {
                throw new InvalidOperationException(
                    $"Staged cache is missing target scope {pair.SongId}/{pair.Instrument}.");
            }
            actualScopes.Add(new CacheScopeFingerprintRow(
                pair.SongId,
                pair.Instrument,
                instrumentPayload
                    .GetProperty("totalEntries")
                    .GetInt64(),
                instrumentPayload
                    .GetProperty("localEntries")
                    .GetInt32(),
                instrumentPayload
                    .GetProperty("entries")
                    .EnumerateArray()
                    .Select(entry =>
                        new CacheEntryFingerprintRow(
                            entry.GetProperty("accountId")
                                .GetString()
                            ?? throw new InvalidOperationException(
                                "Staged target cache entry has no account ID."),
                            entry.GetProperty("score")
                                .GetInt32(),
                            entry.GetProperty("rank")
                                .GetInt32()))
                    .ToArray()));
        }

        var expectedTargetFingerprint =
            ComputeEvidenceFingerprint(expectedScopes);
        var actualTargetFingerprint =
            ComputeEvidenceFingerprint(actualScopes);
        if (!string.Equals(
                expectedTargetFingerprint,
                actualTargetFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Staged target leaderboard cache content differs from the exact published source plus overlays.");
        }

        var expectedAccounts =
            BuildExpectedAccountCacheRows(
                readSnapshot.AffectedRegisteredAccounts,
                readSnapshot.Population);
        var actualAccounts =
            BuildActualAccountCacheRows(
                readSnapshot.AffectedRegisteredAccounts,
                accountKeys,
                payloads);
        var expectedAccountFingerprint =
            ComputeEvidenceFingerprint(expectedAccounts);
        var actualAccountFingerprint =
            ComputeEvidenceFingerprint(actualAccounts);
        if (!string.Equals(
                expectedAccountFingerprint,
                actualAccountFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Staged affected-account cache content differs from the exact published source plus overlays.");
        }

        var overlayAccountSet =
            readSnapshot.OverlayOnlyRegisteredAccounts
                .ToHashSet(
                    StringComparer.OrdinalIgnoreCase);
        var expectedOverlayAccounts = expectedAccounts
            .Where(account =>
                overlayAccountSet.Contains(account.AccountId))
            .ToArray();
        var actualOverlayAccounts = actualAccounts
            .Where(account =>
                overlayAccountSet.Contains(account.AccountId))
            .ToArray();
        var expectedOverlayFingerprint =
            ComputeEvidenceFingerprint(
                expectedOverlayAccounts);
        var actualOverlayFingerprint =
            ComputeEvidenceFingerprint(
                actualOverlayAccounts);
        if (!string.Equals(
                expectedOverlayFingerprint,
                actualOverlayFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Staged overlay-only account cache content differs from the supplemental overlay.");
        }

        return new MaxScoreMaintenanceCacheEvidence(
            entryCount,
            contentFingerprint,
            expectedPublishedScopeCacheKeys.Count,
            expectedPublishedScopeKeyFingerprint,
            expectedScopes.Count,
            expectedTargetFingerprint,
            expectedAccounts.Count,
            expectedAccountFingerprint,
            expectedOverlayAccounts.Length,
            expectedOverlayFingerprint);
    }

    private static HashSet<string>
        BuildExpectedPublishedScopeCacheKeys(
            MaxScoreMaintenanceReadSnapshot readSnapshot)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var songId in readSnapshot.PublishedScopes
                     .Select(scope => scope.SongId)
                     .Distinct(StringComparer.Ordinal))
        {
            keys.Add(LeaderboardCacheKeys.LeaderboardAll(
                songId,
                LeaderboardCacheKeys.SongDetailPreviewTop,
                leeway: null));
            keys.Add(LeaderboardCacheKeys.LeaderboardAll(
                songId,
                LeaderboardCacheKeys.SongDetailPreviewTop,
                leeway: 1.0));
        }
        foreach (var scope in readSnapshot.PublishedScopes)
        {
            if (readSnapshot.PostPromotionMaxScores.TryGetValue(
                    scope.SongId,
                    out var maxScores)
                && maxScores.GetByInstrument(
                    scope.Instrument) is > 0)
            {
                keys.Add(
                    LeaderboardCacheKeys
                        .LeaderboardRankOffsets(
                            scope.SongId,
                            scope.Instrument));
            }
        }
        return keys;
    }

    private List<CacheAccountFingerprintRow>
        BuildExpectedAccountCacheRows(
            IReadOnlyCollection<string> accountIds,
            IReadOnlyDictionary<
                (string SongId, string Instrument),
                long> population)
    {
        var rows = new List<CacheAccountFingerprintRow>();
        foreach (var accountId in accountIds
                     .OrderBy(
                         value => value,
                         StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(BuildExpectedAccountCacheRow(
                accountId,
                _persistence
                    .GetCurrentStatePlayerProfile(accountId),
                population));
        }
        return rows;
    }

    internal static CacheAccountFingerprintRow
        BuildExpectedAccountCacheRow(
            string accountId,
            IReadOnlyCollection<PlayerScoreDto> profile,
            IReadOnlyDictionary<
                (string SongId, string Instrument),
                long> population)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(population);
        var scores = profile
                .Select(score =>
                {
                    if (!population.TryGetValue(
                            (score.SongId, score.Instrument),
                            out var totalEntries))
                    {
                        throw new InvalidOperationException(
                            $"Publication population is missing for affected account score {score.SongId}/{score.Instrument}.");
                    }
                    return new CachePlayerScoreFingerprintRow(
                        score.SongId,
                        ComboIds.FromInstruments(
                            [score.Instrument]),
                        score.Score,
                        score.ApiRank > 0
                            ? score.ApiRank
                            : score.Rank,
                        totalEntries);
                })
                .OrderBy(
                    score => score.SongId,
                    StringComparer.Ordinal)
                .ThenBy(
                    score => score.Instrument,
                    StringComparer.Ordinal)
                .ToArray();
        return new CacheAccountFingerprintRow(
            accountId,
            scores);
    }

    private static List<CacheAccountFingerprintRow>
        BuildActualAccountCacheRows(
            IReadOnlyCollection<string> accountIds,
            IReadOnlyDictionary<string, string> accountKeys,
            IReadOnlyDictionary<string, byte[]> payloads)
    {
        var rows = new List<CacheAccountFingerprintRow>();
        foreach (var accountId in accountIds
                     .OrderBy(
                         value => value,
                         StringComparer.OrdinalIgnoreCase))
        {
            var cacheKey = accountKeys[accountId];
            if (!payloads.TryGetValue(cacheKey, out var payload))
            {
                throw new InvalidOperationException(
                    "Staged cache is missing an affected account key: account="
                    + MaxScoreMaintenanceAccountIdPolicy
                        .FormatEvidenceId(accountId)
                    + ".");
            }
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!string.Equals(
                    root.GetProperty("accountId").GetString(),
                    accountId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Staged account cache identity differs: account="
                    + MaxScoreMaintenanceAccountIdPolicy
                        .FormatEvidenceId(accountId)
                    + ".");
            }
            var scores = root.GetProperty("scores")
                .EnumerateArray()
                .Select(score =>
                    new CachePlayerScoreFingerprintRow(
                        score.GetProperty("si")
                            .GetString()
                        ?? throw new InvalidOperationException(
                            "Staged player score has no song ID."),
                        score.GetProperty("ins")
                            .GetString()
                        ?? throw new InvalidOperationException(
                            "Staged player score has no instrument."),
                        score.GetProperty("sc")
                            .GetInt32(),
                        score.GetProperty("rk")
                            .GetInt32(),
                        score.GetProperty("te")
                            .GetInt64()))
                .OrderBy(score => score.SongId, StringComparer.Ordinal)
                .ThenBy(
                    score => score.Instrument,
                    StringComparer.Ordinal)
                .ToArray();
            rows.Add(new CacheAccountFingerprintRow(
                accountId,
                scores));
        }
        return rows;
    }

    private static string ComputeEvidenceFingerprint<T>(
        IReadOnlyCollection<T> rows)
        => Convert.ToHexStringLower(
            SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(
                    rows,
                    MaxScoreMaintenanceJson.Canonical)));

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
              AND (
                  stats.n_tup_ins <> 0
                  OR stats.n_tup_upd <> 0
                  OR stats.n_tup_del <> 0
              )
            """;
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct))
            ?? throw new InvalidOperationException(
                "Rank-history fingerprint was unavailable.");
    }

    private static IReadOnlyList<MaxScoreMaintenanceArtifactEvidence>
        CreateArtifactEvidence(
            MaxScoreMaintenanceManifest manifest)
        => manifest.Songs
            .Select(song =>
                new MaxScoreMaintenanceArtifactEvidence(
                    song.SongId,
                    song.CurrentPath.ArtifactGenerationId!,
                    song.CurrentPath.ArtifactTreeSha256!,
                    song.CurrentPath.ArtifactFileCount!.Value,
                    song.StagedPath.ArtifactGenerationId!,
                    song.StagedPath.ArtifactTreeSha256!,
                    song.StagedPath.ArtifactFileCount!.Value,
                    song.PlasticDrumsEvidence))
            .ToArray();

    internal static async Task<
        IReadOnlyList<MaxScoreMaintenanceObservedScoreCheck>>
        LoadObservedScoreChecksAsync(
            MaxScoreMaintenanceManifest manifest,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
    {
        var changedPairs = manifest.Songs
            .SelectMany(song => song.ChangedInstruments.Select(
                instrument => (
                    song.SongId,
                    Instrument: instrument,
                    NewMaximum: song.StagedPath.Maxima
                        .GetByInstrument(instrument)!.Value)))
            .Select(pair => (
                pair.SongId,
                pair.Instrument,
                pair.NewMaximum,
                ValidCutoff:
                    RankingsCalculator.ComputeMaxScoreThreshold(
                        pair.NewMaximum)))
            .OrderBy(pair => pair.SongId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Instrument, StringComparer.Ordinal)
            .ToArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 60;
        command.CommandText = $"""
            WITH expected(
                song_id,
                instrument,
                new_maximum,
                valid_cutoff) AS (
                SELECT *
                FROM unnest(
                    @songIds::TEXT[],
                    @instruments::TEXT[],
                    @newMaximums::INTEGER[],
                    @validCutoffs::INTEGER[])
            ), affected AS (
                SELECT song_id, instrument
                FROM expected
            ),
            {PublishedSoloScopeSql.CurrentResolvedAffectedEntriesCte}
            SELECT expected.song_id,
                   expected.instrument,
                   expected.new_maximum,
                   expected.valid_cutoff,
                   EXISTS (
                       SELECT 1
                       FROM selected_sources selected
                       WHERE selected.song_id =
                           expected.song_id
                         AND selected.instrument =
                           expected.instrument),
                   MAX(resolved.score)::INTEGER,
                   MAX(resolved.score) FILTER (
                       WHERE resolved.score
                           <= expected.valid_cutoff)::INTEGER,
                   COUNT(resolved.score) FILTER (
                       WHERE resolved.score
                           > expected.valid_cutoff)
            FROM expected
            LEFT JOIN resolved_rows resolved
              ON resolved.song_id = expected.song_id
             AND resolved.instrument = expected.instrument
            GROUP BY expected.song_id,
                     expected.instrument,
                     expected.new_maximum,
                     expected.valid_cutoff
            ORDER BY expected.song_id,
                     expected.instrument
            """;
        command.Parameters.Add(
            "songIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            changedPairs.Select(pair => pair.SongId).ToArray();
        command.Parameters.Add(
            "instruments",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            changedPairs.Select(pair => pair.Instrument).ToArray();
        command.Parameters.Add(
            "newMaximums",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            changedPairs.Select(pair => pair.NewMaximum).ToArray();
        command.Parameters.Add(
            "validCutoffs",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            changedPairs.Select(pair => pair.ValidCutoff).ToArray();

        var checks =
            new List<MaxScoreMaintenanceObservedScoreCheck>(
                changedPairs.Length);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var newMaximum = reader.GetInt32(2);
            var validCutoff = reader.GetInt32(3);
            var sourceMapped = reader.GetBoolean(4);
            var highestObservedScore = reader.IsDBNull(5)
                ? (int?)null
                : reader.GetInt32(5);
            var highestEligibleObservedScore = reader.IsDBNull(6)
                ? (int?)null
                : reader.GetInt32(6);
            checks.Add(MaxScoreMaintenanceObservedScoreCheck.Create(
                reader.GetString(0),
                reader.GetString(1),
                newMaximum,
                validCutoff,
                sourceMapped,
                highestObservedScore,
                highestEligibleObservedScore,
                reader.GetInt64(7)));
        }
        if (checks.Count != changedPairs.Length)
        {
            throw new InvalidOperationException(
                "Observed-score validation did not cover every changed song/instrument pair.");
        }

        return checks;
    }

    internal static bool IsObservedScoreCompatible(
        int newMaximum,
        int validCutoff,
        bool sourceMapped,
        int? highestEligibleObservedScore)
        => MaxScoreMaintenanceObservedScoreCheck
            .IsCompatible(
                newMaximum,
                validCutoff,
                sourceMapped,
                highestEligibleObservedScore);

    private static string FormatObservedScoreFailure(
        MaxScoreMaintenanceObservedScoreCheck check)
        => check.SourceMapped
            ? "eligible observed-score evidence is incompatible; "
              + FormatObservedScoreEvidence(check)
            : "authoritative published source is missing; "
              + FormatObservedScoreEvidence(check);

    private static string FormatObservedScoreSummary(
        IReadOnlyList<MaxScoreMaintenanceObservedScoreCheck> checks,
        bool passed)
    {
        var evidence = string.Join(
            "; ",
            checks.Select(check =>
                check.Passed
                    ? FormatObservedScoreEvidence(check)
                    : FormatObservedScoreFailure(check)));
        return passed
            ? $"{checks.Count} changed song/instrument maxima have mapped authoritative sources and eligible scores within the exact 105% ranking cutoff; ranking-invalid rows above cutoff={checks.Sum(check => check.AboveValidCutoffCount):N0}; {evidence}"
            : $"{checks.Count(check => !check.Passed)} observed-score checks failed compatibility; {evidence}";
    }

    private static string FormatObservedScoreEvidence(
        MaxScoreMaintenanceObservedScoreCheck check)
        => $"{check.SongId}/{check.Instrument}: sourceMapped={check.SourceMapped.ToString().ToLowerInvariant()}, rawHighest={check.HighestObservedScore?.ToString() ?? "none"}, eligibleHighest={check.HighestEligibleObservedScore?.ToString() ?? "none"}, aboveValidCutoffCount={check.AboveValidCutoffCount}, newMaximum={check.NewMaximum}, validCutoff={check.ValidCutoff}";

    internal static async Task<MaxScoreMaintenanceScoreHistoryEvidence>
        ComputeScoreHistoryEvidenceAsync(
            MaxScoreMaintenanceManifest manifest,
            IReadOnlyDictionary<string, SongMaxScores>
                postPromotionMaxScores,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct,
            int commandTimeoutSeconds =
                ScraperOptions
                    .DefaultMaxScoreMaintenanceCommandTimeoutSeconds,
            Action<string, int>? commandTimeoutConfigured = null)
        => (
            await MaxScoreMaintenanceScoreHistoryEvidenceCalculator
                .ComputeAsync(
                    manifest,
                    postPromotionMaxScores,
                    connection,
                    transaction,
                    ct,
                    commandTimeoutSeconds,
                    commandTimeoutConfigured))
            .Evidence;

    // Differential-test oracle for the pre-optimization selector semantics.
    internal static async Task<MaxScoreMaintenanceScoreHistoryEvidence>
        ComputeScoreHistoryEvidenceReferenceAsync(
            MaxScoreMaintenanceManifest manifest,
            IReadOnlyDictionary<string, SongMaxScores>
                postPromotionMaxScores,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct,
            int commandTimeoutSeconds =
                ScraperOptions
                    .DefaultMaxScoreMaintenanceCommandTimeoutSeconds,
            Action<string, int>? commandTimeoutConfigured = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(postPromotionMaxScores);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(
                transaction.Connection,
                connection))
        {
            throw new ArgumentException(
                "The score-history evidence transaction must belong to the supplied connection.",
                nameof(transaction));
        }

        var maxima = postPromotionMaxScores
            .SelectMany(song => GlobalLeaderboardScraper
                .AllInstruments.Select(instrument => (
                    SongId: song.Key,
                    Instrument: instrument,
                    Maximum: song.Value
                        .GetByInstrument(instrument))))
            .Where(item => item.Maximum is > 0)
            .Select(item => (
                item.SongId,
                item.Instrument,
                Maximum: item.Maximum!.Value,
                Threshold:
                    RankingsCalculator.ComputeMaxScoreThreshold(
                        item.Maximum.Value)))
            .OrderBy(item => item.SongId, StringComparer.Ordinal)
            .ThenBy(item => item.Instrument, StringComparer.Ordinal)
            .ToArray();
        if (maxima.Length == 0)
        {
            throw new InvalidOperationException(
                "Score-history evidence requires post-promotion maxima.");
        }
        var changedPairs = manifest.Songs
            .SelectMany(song => song.ChangedInstruments.Select(
                instrument => (
                    song.SongId,
                    Instrument: instrument)))
            .OrderBy(pair => pair.SongId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Instrument, StringComparer.Ordinal)
            .ToArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        MaxScoreMaintenanceCommandTimeout.Configure(
            command,
            commandTimeoutSeconds,
            "complete-score-history-evidence",
            commandTimeoutConfigured);
        command.CommandText = $"""
            WITH maxima(
                song_id,
                instrument,
                max_score,
                max_threshold) AS (
                SELECT *
                FROM unnest(
                    @songIds::TEXT[],
                    @instruments::TEXT[],
                    @maxScores::INTEGER[],
                    @maxThresholds::INTEGER[])
            ), changed_pairs(song_id, instrument) AS (
                SELECT *
                FROM unnest(
                    @changedSongIds::TEXT[],
                    @changedInstruments::TEXT[])
            ), affected_instruments(instrument) AS (
                SELECT DISTINCT value
                FROM unnest(@affectedInstruments::TEXT[])
                    value
            ),
            {PublishedSoloScopeSql.CurrentResolvedAllEntriesCte},
            affected_player_accounts AS MATERIALIZED (
                SELECT DISTINCT resolved.account_id
                FROM resolved_rows resolved
                JOIN changed_pairs changed
                  ON changed.song_id = resolved.song_id
                 AND changed.instrument =
                     resolved.instrument
                WHERE btrim(resolved.account_id) <> ''
            ), registered_accounts AS MATERIALIZED (
                SELECT account_id
                FROM registered_users
                WHERE btrim(account_id) <> ''
            ), player_stats_fallback_scopes AS MATERIALIZED (
                SELECT resolved.song_id,
                       resolved.instrument,
                       resolved.account_id,
                       maxima.max_threshold
                FROM resolved_rows resolved
                JOIN affected_player_accounts affected
                  ON affected.account_id =
                     resolved.account_id
                JOIN maxima
                  ON maxima.song_id = resolved.song_id
                 AND maxima.instrument =
                     resolved.instrument
                WHERE resolved.score > maxima.max_score
            ), ranking_fallback_scopes AS MATERIALIZED (
                SELECT resolved.song_id,
                       resolved.instrument,
                       resolved.account_id,
                       maxima.max_threshold
                FROM resolved_rows resolved
                JOIN affected_instruments affected
                  ON affected.instrument =
                     resolved.instrument
                JOIN maxima
                  ON maxima.song_id = resolved.song_id
                 AND maxima.instrument =
                     resolved.instrument
                WHERE resolved.score >
                      maxima.max_threshold
            ), relevant_history AS MATERIALIZED (
                SELECT history.*
                FROM score_history history
                JOIN registered_accounts registered
                  ON registered.account_id =
                     history.account_id
                UNION ALL
                SELECT history.*
                FROM score_history history
                JOIN player_stats_fallback_scopes fallback
                  ON fallback.song_id = history.song_id
                 AND fallback.instrument =
                     history.instrument
                 AND fallback.account_id =
                     history.account_id
                WHERE history.new_score <=
                      fallback.max_threshold
                  AND NOT EXISTS (
                      SELECT 1
                      FROM registered_accounts registered
                      WHERE registered.account_id =
                          history.account_id)
                UNION ALL
                SELECT history.*
                FROM score_history history
                JOIN ranking_fallback_scopes fallback
                  ON fallback.song_id = history.song_id
                 AND fallback.instrument =
                     history.instrument
                 AND fallback.account_id =
                     history.account_id
                WHERE history.new_score <=
                      fallback.max_threshold
                  AND NOT EXISTS (
                      SELECT 1
                      FROM registered_accounts registered
                      WHERE registered.account_id =
                          history.account_id)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM affected_player_accounts affected
                      WHERE affected.account_id =
                          history.account_id)
            ), canonical_rows AS MATERIALIZED (
                SELECT id,
                       changed_at,
                       jsonb_build_array(
                           id,
                           song_id,
                           instrument,
                           account_id,
                           old_score,
                           new_score,
                           old_rank,
                           new_rank,
                           accuracy,
                           is_full_combo,
                           stars,
                           percentile,
                           season,
                           CASE
                               WHEN score_achieved_at IS NULL
                                   THEN NULL
                               ELSE (
                                   EXTRACT(
                                       EPOCH FROM
                                           score_achieved_at)
                                   * 1000000)::BIGINT
                           END,
                           season_rank,
                           all_time_rank,
                           difficulty,
                           (
                               EXTRACT(
                                   EPOCH FROM changed_at)
                               * 1000000)::BIGINT)::TEXT
                           AS row_identity
                FROM relevant_history
            ), aggregate_evidence AS (
                SELECT COUNT(*)::BIGINT AS row_count,
                       MIN(id)::BIGINT AS minimum_id,
                       MAX(id)::BIGINT AS maximum_id,
                       MIN(changed_at) AS minimum_changed_at,
                       MAX(changed_at) AS maximum_changed_at,
                       COALESCE(
                           SUM(
                               hashtextextended(
                                   row_identity,
                                   0)::NUMERIC),
                           0)::TEXT AS hash_sum,
                       COALESCE(
                           bit_xor(
                               hashtextextended(
                                   row_identity,
                                   1)),
                           0)::TEXT AS hash_xor
                FROM canonical_rows
            )
            SELECT row_count,
                   minimum_id,
                   maximum_id,
                   minimum_changed_at,
                   maximum_changed_at,
                   encode(
                       digest(
                           convert_to(
                               concat_ws(
                                   ':',
                                   row_count,
                                   COALESCE(
                                       minimum_id::TEXT,
                                       ''),
                                   COALESCE(
                                       maximum_id::TEXT,
                                       ''),
                                   COALESCE(
                                       (
                                           EXTRACT(
                                               EPOCH FROM
                                                   minimum_changed_at)
                                           * 1000000)::BIGINT::TEXT,
                                       ''),
                                   COALESCE(
                                       (
                                           EXTRACT(
                                               EPOCH FROM
                                                   maximum_changed_at)
                                           * 1000000)::BIGINT::TEXT,
                                       ''),
                                   hash_sum,
                                   hash_xor),
                               'UTF8'),
                           'sha256'),
                       'hex')
            FROM aggregate_evidence
            """;
        command.Parameters.Add(
            "songIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            maxima.Select(item => item.SongId).ToArray();
        command.Parameters.Add(
            "instruments",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            maxima.Select(item => item.Instrument).ToArray();
        command.Parameters.Add(
            "maxScores",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            maxima.Select(item => item.Maximum).ToArray();
        command.Parameters.Add(
            "maxThresholds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            maxima.Select(item => item.Threshold).ToArray();
        command.Parameters.Add(
            "changedSongIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            changedPairs.Select(pair => pair.SongId).ToArray();
        command.Parameters.Add(
            "changedInstruments",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            changedPairs.Select(pair => pair.Instrument).ToArray();
        command.Parameters.Add(
            "affectedInstruments",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            manifest.Scope.ExpectedChangedInstruments.ToArray();
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                "Score-history evidence was unavailable.");
        }
        return new MaxScoreMaintenanceScoreHistoryEvidence(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3)
                ? null
                : reader.GetDateTime(3).ToUniversalTime(),
            reader.IsDBNull(4)
                ? null
                : reader.GetDateTime(4).ToUniversalTime(),
            reader.GetString(5));
    }

    private async Task<MaxScoreMaintenancePopulationSnapshot>
        LoadPublishedPopulationSnapshotAsync(
            MaxScoreMaintenanceManifest manifest,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(
                transaction.Connection,
                connection))
        {
            throw new ArgumentException(
                "The population snapshot transaction must belong to the supplied connection.",
                nameof(transaction));
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        ConfigureEvidenceCommandTimeout(
            command,
            "publication-population-evidence");
        command.CommandText = $"""
            WITH publication_guard AS MATERIALIZED (
                SELECT 1
                FROM scrape_publication_state state
                JOIN publication_generations generation
                  ON generation.publication_id =
                     state.current_publication_id
                 AND generation.scrape_id =
                     state.published_scrape_id
                 AND generation.status = 'current'
                WHERE state.id = TRUE
                  AND state.current_publication_id =
                      @publicationId
                  AND state.published_scrape_id =
                      @publishedScrapeId
                  AND state.working_publication_id IS NULL
            ),
            {PublishedSoloScopeSql.CurrentResolvedAllEntriesCte},
            scope_population AS (
                SELECT source.song_id,
                       source.instrument,
                       source.reported_total_entries,
                       COUNT(resolved.account_id)::BIGINT
                           AS resolved_row_count
                FROM published_sources source
                CROSS JOIN publication_guard
                LEFT JOIN resolved_rows resolved
                  ON resolved.song_id = source.song_id
                 AND resolved.instrument =
                     source.instrument
                GROUP BY source.song_id,
                         source.instrument,
                         source.reported_total_entries
            )
            SELECT song_id,
                   instrument,
                   reported_total_entries,
                   resolved_row_count
            FROM scope_population
            ORDER BY song_id, instrument
            """;
        command.Parameters.AddWithValue(
            "publicationId",
            manifest.ExpectedPublicationId);
        command.Parameters.AddWithValue(
            "publishedScrapeId",
            manifest.ExpectedPublishedScrapeId);

        var rows = new List<PopulationScopeEvidenceRow>();
        var population =
            new Dictionary<
                (string SongId, string Instrument),
                long>();
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(2))
            {
                throw new InvalidOperationException(
                    $"Published population is missing for {reader.GetString(0)}/{reader.GetString(1)}.");
            }
            var reported = reader.GetInt64(2);
            var resolved = reader.GetInt64(3);
            if (reported < 0)
            {
                throw new InvalidOperationException(
                    $"Published population is negative for {reader.GetString(0)}/{reader.GetString(1)}.");
            }
            var effective = Math.Max(reported, resolved);
            var key = (
                SongId: reader.GetString(0),
                Instrument: reader.GetString(1));
            population[key] = effective;
            rows.Add(new PopulationScopeEvidenceRow(
                key.SongId,
                key.Instrument,
                reported,
                resolved,
                effective));
        }
        if (rows.Count == 0)
        {
            throw new InvalidOperationException(
                "The exact current publication has no population-bound source scopes.");
        }

        var canonical = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                manifest.ExpectedPublishedScrapeId,
                manifest.ExpectedPublicationId,
                Scopes = rows,
            },
            MaxScoreMaintenanceJson.Canonical);
        var evidence = new MaxScoreMaintenancePopulationEvidence(
            rows.Count,
            rows.Min(row => row.EffectiveTotalEntries),
            rows.Max(row => row.EffectiveTotalEntries),
            Convert.ToHexStringLower(
                SHA256.HashData(canonical)));
        return new MaxScoreMaintenancePopulationSnapshot(
            population.ToFrozenDictionary(),
            evidence);
    }

    private Dictionary<string, SongMaxScores>
        BuildPostPromotionMaxScores(
            MaxScoreMaintenanceManifest manifest,
            IReadOnlyCollection<Song> publishedCatalogSongs,
            IEnumerable<(string SongId, string Instrument)>
                publishedScopes)
    {
        var catalogSongIds = publishedCatalogSongs
            .Select(song => song.track?.su)
            .Where(static songId =>
                !string.IsNullOrWhiteSpace(songId))
            .Select(static songId => songId!)
            .ToHashSet(StringComparer.Ordinal);
        var scopedSongIds = publishedScopes
            .Select(scope => scope.SongId)
            .ToHashSet(StringComparer.Ordinal);
        var result = _pathStore.GetAllMaxScores()
            .Where(pair =>
                catalogSongIds.Contains(pair.Key)
                && scopedSongIds.Contains(pair.Key))
            .ToDictionary(
                pair => pair.Key,
                pair => MaxScoreMaintenanceMaxima
                    .From(pair.Value)
                    .ToSongMaxScores(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var song in manifest.Songs)
        {
            result[song.SongId] =
                song.StagedPath.Maxima.ToSongMaxScores();
        }
        return result;
    }

    private async Task<MaxScoreMaintenanceReadSnapshot>
        CaptureMaintenanceReadSnapshotAsync(
            IMaxScoreMaintenanceLease lease,
            MaxScoreMaintenanceManifest manifest,
            IReadOnlyCollection<Song> publishedCatalogSongs,
            CancellationToken ct)
    {
        if (!_persistence
                .IsMaxScoreMaintenancePublishedReadPassActive)
        {
            throw new InvalidOperationException(
                "Max-score input capture requires the strict published-source read context.");
        }

        return await lease.ExecuteTransactionAsync(
            "capture-publication-bound-inputs",
            requireSourceLocks: true,
            async (connection, transaction, token) =>
            {
                var population =
                    await LoadPublishedPopulationSnapshotAsync(
                        manifest,
                        connection,
                        transaction,
                        token);
                var publishedScopes = population.Population.Keys
                    .OrderBy(
                        scope => scope.SongId,
                        StringComparer.Ordinal)
                    .ThenBy(
                        scope => scope.Instrument,
                        StringComparer.Ordinal)
                    .ToArray();
                ValidatePublishedScopeOwnership(
                    publishedCatalogSongs,
                    publishedScopes);
                ValidateManifestPopulationCoverage(
                    manifest,
                    population.Population);
                var postPromotionMaxScores =
                    BuildPostPromotionMaxScores(
                        manifest,
                        publishedCatalogSongs,
                        publishedScopes);
                var observedScoreChecks =
                    ApplyObservedScoreChecksTestHook(
                        "apply-resume",
                        await LoadObservedScoreChecksAsync(
                            manifest,
                            connection,
                            transaction,
                            token));
                var scoreHistory =
                    await MaxScoreMaintenanceScoreHistoryEvidenceCalculator
                        .ComputeAsync(
                        manifest,
                        postPromotionMaxScores,
                        connection,
                        transaction,
                        token,
                        _options
                            .MaxScoreMaintenanceCommandTimeoutSeconds,
                        EvidenceCommandTimeoutTestHook);
                return new MaxScoreMaintenanceReadSnapshot(
                    postPromotionMaxScores.ToFrozenDictionary(
                        StringComparer.OrdinalIgnoreCase),
                    postPromotionMaxScores.ToFrozenDictionary(
                        StringComparer.OrdinalIgnoreCase),
                    postPromotionMaxScores.ToFrozenDictionary(
                        StringComparer.OrdinalIgnoreCase),
                    population.Population,
                    population.Evidence,
                    scoreHistory.Evidence,
                    scoreHistory.Evidence,
                    observedScoreChecks,
                    publishedScopes,
                    MaxScoreMaintenanceAccountIdPolicy
                        .NormalizeSet(
                            scoreHistory
                                .AffectedPlayerStatsAccounts),
                    MaxScoreMaintenanceAccountIdPolicy
                        .NormalizeSet(
                            scoreHistory
                                .AffectedRegisteredAccounts),
                    MaxScoreMaintenanceAccountIdPolicy
                        .NormalizeSet(
                            scoreHistory
                                .OverlayOnlyRegisteredAccounts));
            },
            IsolationLevel.RepeatableRead,
            ct);
    }

    private async Task<MaxScoreMaintenanceReadSnapshot>
        CaptureRollbackReadSnapshotAsync(
            IMaxScoreMaintenanceLease lease,
            MaxScoreMaintenanceManifest manifest,
            MaxScoreMaintenanceRollbackSnapshot rollbackSnapshot,
            IReadOnlyCollection<Song> publishedCatalogSongs,
            CancellationToken ct)
    {
        if (!_persistence
                .IsMaxScoreMaintenancePublishedReadPassActive)
        {
            throw new InvalidOperationException(
                "Max-score rollback input capture requires the strict published-source read context.");
        }

        return await lease.ExecuteTransactionAsync(
            "capture-rollback-publication-bound-inputs",
            requireSourceLocks: true,
            async (connection, transaction, token) =>
            {
                var population =
                    await LoadPublishedPopulationSnapshotAsync(
                        manifest,
                        connection,
                        transaction,
                        token);
                var publishedScopes = population.Population.Keys
                    .OrderBy(
                        scope => scope.SongId,
                        StringComparer.Ordinal)
                    .ThenBy(
                        scope => scope.Instrument,
                        StringComparer.Ordinal)
                    .ToArray();
                ValidatePublishedScopeOwnership(
                    publishedCatalogSongs,
                    publishedScopes);
                ValidateManifestPopulationCoverage(
                    manifest,
                    population.Population);
                var rollbackMaxScores =
                    BuildRollbackMaxScores(
                        rollbackSnapshot,
                        publishedCatalogSongs,
                        publishedScopes);
                var evidenceMaxScores =
                    BuildPostPromotionMaxScores(
                        manifest,
                        publishedCatalogSongs,
                        publishedScopes);
                var approvedScoreHistory =
                    await MaxScoreMaintenanceScoreHistoryEvidenceCalculator
                        .ComputeAsync(
                            manifest,
                            evidenceMaxScores,
                            connection,
                            transaction,
                            token,
                            _options
                                .MaxScoreMaintenanceCommandTimeoutSeconds,
                            EvidenceCommandTimeoutTestHook);
                var rollbackScoreHistory =
                    HasPositiveScoreHistoryMaximum(
                        rollbackMaxScores,
                        publishedScopes)
                        ? await MaxScoreMaintenanceScoreHistoryEvidenceCalculator
                            .ComputeAsync(
                                manifest,
                                rollbackMaxScores,
                                connection,
                                transaction,
                                token,
                                _options
                                    .MaxScoreMaintenanceCommandTimeoutSeconds,
                                EvidenceCommandTimeoutTestHook,
                                requirePositiveChangedMaxima: false,
                                seededAffectedAccounts:
                                    approvedScoreHistory
                                        .AffectedPlayerStatsAccounts)
                        : new MaxScoreMaintenanceScoreHistorySnapshot(
                            EmptyScoreHistoryEvidence(),
                            approvedScoreHistory
                                .AffectedPlayerStatsAccounts,
                            approvedScoreHistory
                                .AffectedRegisteredAccounts,
                            approvedScoreHistory
                                .OverlayOnlyRegisteredAccounts);
                var affectedPlayerStatsAccounts =
                    approvedScoreHistory
                        .AffectedPlayerStatsAccounts
                        .Concat(
                            rollbackScoreHistory
                                .AffectedPlayerStatsAccounts)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(
                            accountId => accountId,
                            StringComparer.Ordinal)
                        .ToArray();
                var affectedRegisteredAccounts =
                    approvedScoreHistory
                        .AffectedRegisteredAccounts
                        .Concat(
                            rollbackScoreHistory
                                .AffectedRegisteredAccounts)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(
                            accountId => accountId,
                            StringComparer.Ordinal)
                        .ToArray();
                var overlayOnlyRegisteredAccounts =
                    approvedScoreHistory
                        .OverlayOnlyRegisteredAccounts
                        .Concat(
                            rollbackScoreHistory
                                .OverlayOnlyRegisteredAccounts)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(
                            accountId => accountId,
                            StringComparer.Ordinal)
                        .ToArray();
                return new MaxScoreMaintenanceReadSnapshot(
                    rollbackMaxScores.ToFrozenDictionary(
                        StringComparer.OrdinalIgnoreCase),
                    evidenceMaxScores.ToFrozenDictionary(
                        StringComparer.OrdinalIgnoreCase),
                    rollbackMaxScores.ToFrozenDictionary(
                        StringComparer.OrdinalIgnoreCase),
                    population.Population,
                    population.Evidence,
                    approvedScoreHistory.Evidence,
                    rollbackScoreHistory.Evidence,
                    [],
                    publishedScopes,
                    affectedPlayerStatsAccounts,
                    affectedRegisteredAccounts,
                    overlayOnlyRegisteredAccounts);
            },
            IsolationLevel.RepeatableRead,
            ct);
    }

    private Dictionary<string, SongMaxScores>
        BuildRollbackMaxScores(
            MaxScoreMaintenanceRollbackSnapshot rollbackSnapshot,
            IReadOnlyCollection<Song> publishedCatalogSongs,
            IEnumerable<(string SongId, string Instrument)>
                publishedScopes)
    {
        var catalogSongIds = publishedCatalogSongs
            .Select(song => song.track?.su)
            .Where(static songId =>
                !string.IsNullOrWhiteSpace(songId))
            .Select(static songId => songId!)
            .ToHashSet(StringComparer.Ordinal);
        var scopedSongIds = publishedScopes
            .Select(scope => scope.SongId)
            .ToHashSet(StringComparer.Ordinal);
        var result = _pathStore.GetAllMaxScores()
            .Where(pair =>
                catalogSongIds.Contains(pair.Key)
                && scopedSongIds.Contains(pair.Key))
            .ToDictionary(
                pair => pair.Key,
                pair => MaxScoreMaintenanceMaxima
                    .From(pair.Value)
                    .ToSongMaxScores(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var song in rollbackSnapshot.Songs)
        {
            result[song.SongId] =
                song.Path.Maxima.ToSongMaxScores();
        }
        return result;
    }

    private static bool HasPositiveScoreHistoryMaximum(
        IReadOnlyDictionary<string, SongMaxScores> maxScores,
        IEnumerable<(string SongId, string Instrument)> scopes)
        => scopes.Any(scope =>
            maxScores.TryGetValue(
                scope.SongId,
                out var songMaxScores)
            && songMaxScores.GetByInstrument(
                scope.Instrument) is > 0);

    private IReadOnlyList<MaxScoreMaintenanceObservedScoreCheck>
        ApplyObservedScoreChecksTestHook(
            string stage,
            IReadOnlyList<MaxScoreMaintenanceObservedScoreCheck>
                checks)
        => ObservedScoreChecksTestHook?.Invoke(stage, checks)
           ?? checks;

    private static void ValidatePublishedScopeOwnership(
        IReadOnlyCollection<Song> publishedCatalogSongs,
        IEnumerable<(string SongId, string Instrument)>
            publishedScopes)
    {
        var catalogSongIds = publishedCatalogSongs
            .Select(song => song.track?.su)
            .Where(static songId =>
                !string.IsNullOrWhiteSpace(songId))
            .Select(static songId => songId!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var scope in publishedScopes)
        {
            if (!catalogSongIds.Contains(scope.SongId)
                || !GlobalLeaderboardScraper.AllInstruments
                    .Contains(
                        scope.Instrument,
                        StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Published scope {scope.SongId}/{scope.Instrument} is not owned by the frozen catalog.");
            }
        }
    }

    private static void ValidateManifestPopulationCoverage(
        MaxScoreMaintenanceManifest manifest,
        IReadOnlyDictionary<
            (string SongId, string Instrument),
            long> population)
    {
        foreach (var song in manifest.Songs)
        {
            foreach (var instrument in song.ChangedInstruments)
            {
                if (!population.ContainsKey(
                        (song.SongId, instrument)))
                {
                    throw new InvalidOperationException(
                        $"Published population is missing for maintenance scope {song.SongId}/{instrument}.");
                }
            }
        }
    }

    private static string ComputePlanDigest(
        string manifestDigest,
        PublishedContext context,
        MaxScoreMaintenanceNotificationInspection notifications,
        string rankHistoryFingerprint,
        MaxScoreMaintenancePopulationEvidence populationEvidence,
        MaxScoreMaintenanceScoreHistoryEvidence scoreHistoryEvidence,
        IReadOnlyList<string> affectedInstruments,
        IReadOnlyList<MaxScoreMaintenanceArtifactEvidence> artifactEvidence,
        IReadOnlyList<MaxScoreMaintenanceObservedScoreCheck>
            observedScoreChecks)
    {
        var canonical = JsonSerializer.Serialize(
            new
            {
                contractVersion =
                    MaxScoreMaintenancePlanReport
                        .CurrentPlanDigestContractVersion,
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
                populationEvidence,
                scoreHistoryEvidence,
                affectedInstruments,
                artifactEvidence,
                observedScoreChecks,
                routineCandidates = notifications.Candidates,
            },
            MaxScoreMaintenanceJson.Canonical);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void ValidateRecomputedPlanEvidence(
        MaxScoreMaintenanceManifest manifest,
        string manifestDigest,
        PublishedContext context,
        MaxScoreMaintenanceRunState run,
        MaxScoreMaintenanceReadSnapshot readSnapshot)
    {
        var recomputedDigest = ComputePlanDigest(
            manifestDigest,
            context,
            new MaxScoreMaintenanceNotificationInspection(
                run.PublishedScoreSourceFingerprint,
                run.NotificationStateFingerprint,
                []),
            run.RankHistoryFingerprint,
            readSnapshot.PopulationEvidence,
            readSnapshot.ScoreHistoryEvidence,
            manifest.Scope.ExpectedChangedInstruments.ToArray(),
            CreateArtifactEvidence(manifest),
            readSnapshot.ObservedScoreChecks);
        if (!string.Equals(
                recomputedDigest,
                run.PlanDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Recomputed max-score plan evidence digest {recomputedDigest} differs from the approved digest {run.PlanDigest}.");
        }
    }

    private static void ValidateObservedScoreEvidence(
        IReadOnlyList<MaxScoreMaintenanceObservedScoreCheck>
            observedScoreChecks)
    {
        var failedObservedScore =
            observedScoreChecks
                .FirstOrDefault(check => !check.Passed);
        if (failedObservedScore is not null)
        {
            throw new InvalidOperationException(
                "Observed-score evidence no longer satisfies the approved eligibility contract: "
                + FormatObservedScoreFailure(
                    failedObservedScore));
        }
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
                       score_history_fingerprint,
                       population_evidence::TEXT,
                       score_history_evidence::TEXT,
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
                       staged_cache_evidence::TEXT,
                       failure_stage,
                       failure_detail,
                       created_at,
                       rollback_started_at,
                       rollback_paths_restored_at,
                       rollback_derived_state_rebuilt_at,
                       rollback_notifications_quarantined_at,
                       rollback_caches_staged_at,
                       rollback_validated_at,
                       rolled_back_at,
                       rollback_before_path_fingerprint,
                       rollback_after_path_fingerprint,
                       rollback_restored_song_count,
                       rollback_rebuilt_instrument_count,
                       rollback_notification_maintenance_run_id,
                       rollback_quarantined_candidate_count,
                       rollback_visible_delivery_count,
                       rollback_staged_cache_entry_count,
                       rollback_cache_evidence::TEXT,
                       rollback_failure_stage,
                       rollback_failure_detail,
                       updated_at,
                       completed_at
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
            DeserializeEvidence<
                MaxScoreMaintenancePopulationEvidence>(
                    reader.GetString(10)),
            DeserializeEvidence<
                MaxScoreMaintenanceScoreHistoryEvidence>(
                    reader.GetString(11)),
            reader.GetString(12),
            ParsePhase(reader.GetString(13)),
            reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetInt64(17),
            reader.GetInt32(18),
            reader.GetInt32(19),
            reader.GetInt64(20),
            reader.GetInt32(21),
            reader.GetInt64(22),
            reader.IsDBNull(23)
                ? null
                : DeserializeEvidence<
                    MaxScoreMaintenanceCacheEvidence>(
                        reader.GetString(23)),
            reader.IsDBNull(24) ? null : reader.GetString(24),
            reader.IsDBNull(25) ? null : reader.GetString(25),
            reader.GetDateTime(26),
            ReadNullableDateTime(reader, 27),
            ReadNullableDateTime(reader, 28),
            ReadNullableDateTime(reader, 29),
            ReadNullableDateTime(reader, 30),
            ReadNullableDateTime(reader, 31),
            ReadNullableDateTime(reader, 32),
            ReadNullableDateTime(reader, 33),
            ReadNullableString(reader, 34),
            ReadNullableString(reader, 35),
            reader.GetInt32(36),
            reader.GetInt32(37),
            ReadNullableInt64(reader, 38),
            reader.GetInt64(39),
            reader.GetInt32(40),
            reader.GetInt64(41),
            reader.IsDBNull(42)
                ? null
                : DeserializeEvidence<
                    MaxScoreMaintenanceCacheEvidence>(
                        reader.GetString(42)),
            ReadNullableString(reader, 43),
            ReadNullableString(reader, 44),
            reader.GetDateTime(45),
            ReadNullableDateTime(reader, 46));

    private async Task AdvancePhaseAsync(
        IMaxScoreMaintenanceLease lease,
        string manifestDigest,
        string planDigest,
        MaxScoreMaintenancePhase expectedPhase,
        MaxScoreMaintenancePhase nextPhase,
        int? promotedSongCount,
        int? rebuiltInstrumentCount,
        long? stagedCacheEntryCount,
        MaxScoreMaintenanceCacheEvidence? cacheEvidence,
        long? cachePublicationId,
        CancellationToken ct)
    {
        await lease.ExecuteTransactionAsync(
            $"phase-checkpoint:{FormatPhase(nextPhase)}",
            requireSourceLocks: true,
            async (conn, tx, token) =>
            {
                if (nextPhase == MaxScoreMaintenancePhase.CachesStaged)
                {
                    if (!cachePublicationId.HasValue
                        || stagedCacheEntryCount is not > 0
                        || cacheEvidence is null
                        || cacheEvidence.EntryCount
                            != stagedCacheEntryCount.Value)
                    {
                        throw new InvalidOperationException(
                            "The cache-staging checkpoint requires exact publication, count, and aggregate evidence.");
                    }
                    await MaxScoreMaintenanceCacheEntryEvidenceStore
                        .CaptureAsync(
                            manifestDigest,
                            cachePublicationId.Value,
                            stagedCacheEntryCount.Value,
                            conn,
                            tx,
                            token,
                            _options
                                .MaxScoreMaintenanceCommandTimeoutSeconds);
                }
                else if (cachePublicationId.HasValue)
                {
                    throw new InvalidOperationException(
                        "Cache publication identity is only valid for the cache-staging checkpoint.");
                }

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
                staged_cache_evidence = COALESCE(
                    @cacheEvidence,
                    staged_cache_evidence),
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
                cmd.Parameters.Add(
                    "cacheEvidence",
                    NpgsqlDbType.Jsonb).Value =
                    cacheEvidence is null
                        ? DBNull.Value
                        : JsonSerializer.Serialize(
                            cacheEvidence,
                            MaxScoreMaintenanceJson.Canonical);
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

    private async Task BeginRollbackAsync(
        IMaxScoreMaintenanceLease lease,
        string manifestDigest,
        string planDigest,
        string rollbackDigest,
        string beforePathFingerprint,
        CancellationToken ct)
    {
        await lease.ExecuteTransactionAsync(
            "rollback-begin",
            requireSourceLocks: true,
            async (connection, transaction, token) =>
            {
                await ConfigureShortTransactionAsync(
                    connection,
                    transaction,
                    token);
                await using var command =
                    connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE max_score_maintenance_runs
                    SET phase = 'rollback_validating',
                        status = 'running',
                        rollback_started_at =
                            COALESCE(
                                rollback_started_at,
                                now()),
                        rollback_before_path_fingerprint =
                            @beforePathFingerprint,
                        rollback_failure_stage = NULL,
                        rollback_failure_detail = NULL,
                        updated_at = now()
                    WHERE manifest_sha256 = @manifestSha256
                      AND plan_digest = @planDigest
                      AND rollback_snapshot_sha256 =
                            @rollbackSha256
                      AND phase IN (
                          'rollback_captured',
                          'paths_promoted',
                          'derived_state_rebuilt',
                          'notifications_quarantined',
                          'caches_staged',
                          'validated')
                      AND status IN ('running', 'failed')
                    """;
                command.Parameters.AddWithValue(
                    "beforePathFingerprint",
                    beforePathFingerprint);
                command.Parameters.AddWithValue(
                    "manifestSha256",
                    manifestDigest);
                command.Parameters.AddWithValue(
                    "planDigest",
                    planDigest);
                command.Parameters.AddWithValue(
                    "rollbackSha256",
                    rollbackDigest);
                if (await command.ExecuteNonQueryAsync(token)
                    != 1)
                {
                    throw new InvalidOperationException(
                        "Max-score rollback phase could not be started from the exact incomplete run.");
                }
            },
            IsolationLevel.Serializable,
            ct);
    }

    private async Task MarkRollbackRunningAsync(
        IMaxScoreMaintenanceLease lease,
        string manifestDigest,
        CancellationToken ct)
    {
        await lease.ExecuteTransactionAsync(
            "rollback-resume-running",
            requireSourceLocks: true,
            async (connection, transaction, token) =>
            {
                await using var command =
                    connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE max_score_maintenance_runs
                    SET status = 'running',
                        rollback_failure_stage = NULL,
                        rollback_failure_detail = NULL,
                        updated_at = now()
                    WHERE manifest_sha256 = @manifestSha256
                      AND phase IN (
                          'rollback_validating',
                          'rollback_paths_restored',
                          'rollback_derived_state_rebuilt',
                          'rollback_notifications_quarantined',
                          'rollback_caches_staged',
                          'rollback_validated')
                      AND status IN ('running', 'failed')
                    """;
                command.Parameters.AddWithValue(
                    "manifestSha256",
                    manifestDigest);
                if (await command.ExecuteNonQueryAsync(token)
                    != 1)
                {
                    throw new InvalidOperationException(
                        "Resumable max-score rollback run was not found.");
                }
            },
            ct: ct);
    }

    private async Task RestoreRollbackPathsAsync(
        IMaxScoreMaintenanceLease lease,
        MaxScoreMaintenanceManifest manifest,
        string manifestDigest,
        string planDigest,
        MaxScoreMaintenanceRollbackSnapshot snapshot,
        string beforePathFingerprint,
        string afterPathFingerprint,
        CancellationToken ct)
    {
        BeforeRollbackPathRestoreTestHook?.Invoke();
        await lease.ExecuteTransactionAsync(
            "rollback-path-restoration",
            requireSourceLocks: true,
            async (connection, transaction, token) =>
            {
                await ConfigureShortTransactionAsync(
                    connection,
                    transaction,
                    token);
                await LockOwnedFreezeRowAsync(
                    connection,
                    transaction,
                    manifest,
                    manifestDigest,
                    token);
                var current =
                    await LoadPathStatesForUpdateAsync(
                        connection,
                        transaction,
                        manifest.Songs
                            .Select(song => song.SongId)
                            .ToArray(),
                        token);
                if (current.Count != manifest.Songs.Count)
                {
                    throw new InvalidOperationException(
                        "Rollback path restoration did not lock the exact manifest song set.");
                }
                foreach (var song in manifest.Songs)
                {
                    var expected =
                        song.StagedPath with
                        {
                            Revision = checked(
                                song.CurrentPath.Revision
                                + 1),
                        };
                    if (!current.TryGetValue(
                            song.SongId,
                            out var state)
                        || !PathIdentityMatches(
                            state,
                            expected))
                    {
                        throw new InvalidOperationException(
                            $"Current promoted path changed before rollback for {song.SongId}.");
                    }
                }

                foreach (var rollbackSong in snapshot.Songs)
                {
                    await using var update =
                        connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE songs
                        SET max_lead_score = @lead,
                            max_bass_score = @bass,
                            max_drums_score = @drums,
                            max_vocals_score = @vocals,
                            max_pro_lead_score = @proLead,
                            max_pro_bass_score = @proBass,
                            max_pro_cymbals_score = @proCymbals,
                            max_pro_drums_score = @proDrums,
                            dat_file_hash = @datHash,
                            song_last_modified =
                                @songLastModified,
                            paths_generated_at = @generatedAt,
                            chopt_version = @choptVersion,
                            chopt_binary_sha256 = @binaryHash,
                            path_generation_profile = @profile,
                            path_artifact_generation_id =
                                @generationId,
                            path_expected_instruments =
                                @expectedInstruments,
                            path_generation_revision =
                                @restoredRevision,
                            path_generation_pending = @pending
                        WHERE song_id = @songId
                          AND path_generation_revision =
                                @expectedCurrentRevision
                        """;
                    AddRollbackPathRestoreParameters(
                        update,
                        rollbackSong,
                        checked(
                            manifest.Songs.Single(
                                    song => string.Equals(
                                        song.SongId,
                                        rollbackSong.SongId,
                                        StringComparison.Ordinal))
                                .CurrentPath.Revision
                            + 1));
                    if (await update.ExecuteNonQueryAsync(token)
                        != 1)
                    {
                        throw new InvalidOperationException(
                            $"Rollback path update lost the locked row for {rollbackSong.SongId}.");
                    }
                }

                await using var checkpoint =
                    connection.CreateCommand();
                checkpoint.Transaction = transaction;
                checkpoint.CommandText = """
                    UPDATE max_score_maintenance_runs
                    SET phase = 'rollback_paths_restored',
                        status = 'running',
                        rollback_paths_restored_at = now(),
                        rollback_before_path_fingerprint =
                            @beforePathFingerprint,
                        rollback_after_path_fingerprint =
                            @afterPathFingerprint,
                        rollback_restored_song_count =
                            @restoredSongCount,
                        rollback_failure_stage = NULL,
                        rollback_failure_detail = NULL,
                        updated_at = now()
                    WHERE manifest_sha256 = @manifestSha256
                      AND plan_digest = @planDigest
                      AND phase = 'rollback_validating'
                      AND status IN ('running', 'failed')
                    """;
                checkpoint.Parameters.AddWithValue(
                    "beforePathFingerprint",
                    beforePathFingerprint);
                checkpoint.Parameters.AddWithValue(
                    "afterPathFingerprint",
                    afterPathFingerprint);
                checkpoint.Parameters.AddWithValue(
                    "restoredSongCount",
                    snapshot.Songs.Count);
                checkpoint.Parameters.AddWithValue(
                    "manifestSha256",
                    manifestDigest);
                checkpoint.Parameters.AddWithValue(
                    "planDigest",
                    planDigest);
                if (await checkpoint.ExecuteNonQueryAsync(token)
                    != 1)
                {
                    throw new InvalidOperationException(
                        "Rollback path checkpoint phase changed before commit.");
                }
            },
            IsolationLevel.Serializable,
            ct);
    }

    private async Task AdvanceRollbackPhaseAsync(
        IMaxScoreMaintenanceLease lease,
        string manifestDigest,
        string planDigest,
        MaxScoreMaintenancePhase expectedPhase,
        MaxScoreMaintenancePhase nextPhase,
        int? restoredSongCount,
        int? rebuiltInstrumentCount,
        MaxScoreMaintenanceNotificationQuarantineResult?
            notificationResult,
        long? stagedCacheEntryCount,
        MaxScoreMaintenanceCacheEvidence? cacheEvidence,
        long? cachePublicationId,
        CancellationToken ct)
    {
        await lease.ExecuteTransactionAsync(
            $"rollback-phase-checkpoint:{FormatPhase(nextPhase)}",
            requireSourceLocks: true,
            async (connection, transaction, token) =>
            {
                if (nextPhase
                    == MaxScoreMaintenancePhase
                        .RollbackCachesStaged)
                {
                    if (!cachePublicationId.HasValue
                        || stagedCacheEntryCount is not > 0
                        || cacheEvidence is null
                        || cacheEvidence.EntryCount
                            != stagedCacheEntryCount.Value)
                    {
                        throw new InvalidOperationException(
                            "Rollback cache checkpoint requires exact publication, count, and evidence.");
                    }
                    await MaxScoreMaintenanceCacheEntryEvidenceStore
                        .CaptureRollbackAsync(
                            manifestDigest,
                            cachePublicationId.Value,
                            stagedCacheEntryCount.Value,
                            connection,
                            transaction,
                            token,
                            _options
                                .MaxScoreMaintenanceCommandTimeoutSeconds);
                }
                else if (cachePublicationId.HasValue)
                {
                    throw new InvalidOperationException(
                        "Rollback cache publication identity is only valid for cache staging.");
                }

                await using var command =
                    connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE max_score_maintenance_runs
                    SET phase = @nextPhase,
                        status = 'running',
                        rollback_restored_song_count =
                            COALESCE(
                                @restoredSongCount,
                                rollback_restored_song_count),
                        rollback_rebuilt_instrument_count =
                            COALESCE(
                                @rebuiltInstrumentCount,
                                rollback_rebuilt_instrument_count),
                        rollback_notification_maintenance_run_id =
                            COALESCE(
                                @notificationMaintenanceRunId,
                                rollback_notification_maintenance_run_id),
                        rollback_quarantined_candidate_count =
                            COALESCE(
                                @quarantinedCandidateCount,
                                rollback_quarantined_candidate_count),
                        rollback_visible_delivery_count =
                            COALESCE(
                                @visibleDeliveryCount,
                                rollback_visible_delivery_count),
                        rollback_staged_cache_entry_count =
                            COALESCE(
                                @stagedCacheEntryCount,
                                rollback_staged_cache_entry_count),
                        rollback_cache_evidence =
                            COALESCE(
                                @cacheEvidence,
                                rollback_cache_evidence),
                        rollback_derived_state_rebuilt_at =
                            CASE
                                WHEN @nextPhase =
                                    'rollback_derived_state_rebuilt'
                                    THEN now()
                                ELSE rollback_derived_state_rebuilt_at
                            END,
                        rollback_notifications_quarantined_at =
                            CASE
                                WHEN @nextPhase =
                                    'rollback_notifications_quarantined'
                                    THEN now()
                                ELSE rollback_notifications_quarantined_at
                            END,
                        rollback_caches_staged_at =
                            CASE
                                WHEN @nextPhase =
                                    'rollback_caches_staged'
                                    THEN now()
                                ELSE rollback_caches_staged_at
                            END,
                        rollback_validated_at =
                            CASE
                                WHEN @nextPhase =
                                    'rollback_validated'
                                    THEN now()
                                ELSE rollback_validated_at
                            END,
                        rollback_failure_stage = NULL,
                        rollback_failure_detail = NULL,
                        updated_at = now()
                    WHERE manifest_sha256 = @manifestSha256
                      AND plan_digest = @planDigest
                      AND phase = @expectedPhase
                      AND status IN ('running', 'failed')
                    """;
                command.Parameters.AddWithValue(
                    "nextPhase",
                    FormatPhase(nextPhase));
                command.Parameters.AddWithValue(
                    "expectedPhase",
                    FormatPhase(expectedPhase));
                command.Parameters.Add(
                    "restoredSongCount",
                    NpgsqlDbType.Integer).Value =
                    (object?)restoredSongCount
                    ?? DBNull.Value;
                command.Parameters.Add(
                    "rebuiltInstrumentCount",
                    NpgsqlDbType.Integer).Value =
                    (object?)rebuiltInstrumentCount
                    ?? DBNull.Value;
                command.Parameters.Add(
                    "notificationMaintenanceRunId",
                    NpgsqlDbType.Bigint).Value =
                    (object?)notificationResult
                        ?.MaintenanceRunId
                    ?? DBNull.Value;
                command.Parameters.Add(
                    "quarantinedCandidateCount",
                    NpgsqlDbType.Bigint).Value =
                    (object?)notificationResult
                        ?.CandidateCount
                    ?? DBNull.Value;
                command.Parameters.Add(
                    "visibleDeliveryCount",
                    NpgsqlDbType.Integer).Value =
                    (object?)notificationResult
                        ?.VisibleDeliveryCount
                    ?? DBNull.Value;
                command.Parameters.Add(
                    "stagedCacheEntryCount",
                    NpgsqlDbType.Bigint).Value =
                    (object?)stagedCacheEntryCount
                    ?? DBNull.Value;
                command.Parameters.Add(
                    "cacheEvidence",
                    NpgsqlDbType.Jsonb).Value =
                    cacheEvidence is null
                        ? DBNull.Value
                        : JsonSerializer.Serialize(
                            cacheEvidence,
                            MaxScoreMaintenanceJson.Canonical);
                command.Parameters.AddWithValue(
                    "manifestSha256",
                    manifestDigest);
                command.Parameters.AddWithValue(
                    "planDigest",
                    planDigest);
                if (await command.ExecuteNonQueryAsync(token)
                    != 1)
                {
                    throw new InvalidOperationException(
                        $"Max-score rollback phase changed before {nextPhase} checkpoint.");
                }
            },
            ct: ct);
    }

    private async Task RecordRollbackFailureAsync(
        IMaxScoreMaintenanceLease lease,
        string manifestDigest,
        MaxScoreMaintenancePhase phase,
        Exception error,
        CancellationToken ct)
    {
        await lease.ExecuteTransactionAsync(
            "rollback-failure-checkpoint",
            requireSourceLocks: true,
            async (connection, transaction, token) =>
            {
                await using var command =
                    connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE max_score_maintenance_runs
                    SET status = 'failed',
                        rollback_failure_stage =
                            @failureStage,
                        rollback_failure_detail =
                            @failureDetail,
                        updated_at = now()
                    WHERE manifest_sha256 = @manifestSha256
                      AND phase IN (
                          'rollback_validating',
                          'rollback_paths_restored',
                          'rollback_derived_state_rebuilt',
                          'rollback_notifications_quarantined',
                          'rollback_caches_staged',
                          'rollback_validated')
                    """;
                command.Parameters.AddWithValue(
                    "failureStage",
                    FormatPhase(phase));
                command.Parameters.AddWithValue(
                    "failureDetail",
                    BoundDetail(error.Message));
                command.Parameters.AddWithValue(
                    "manifestSha256",
                    manifestDigest);
                await command.ExecuteNonQueryAsync(token);
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
        command.Parameters.AddWithValue(
            "artifactTreeSha256",
            (object?)song.Path.ArtifactTreeSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "artifactFileCount",
            (object?)song.Path.ArtifactFileCount ?? DBNull.Value);
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

    private static async Task<IReadOnlyDictionary<
            string,
            PathGenerationState>>
        LoadPathStatesForUpdateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyCollection<string> songIds,
            CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT song_id,
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
                   NULLIF(last_modified, ''),
                   path_generation_pending
            FROM songs
            WHERE song_id = ANY(@songIds)
            ORDER BY song_id
            FOR UPDATE
            """;
        command.Parameters.Add(
            "songIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            songIds.ToArray();
        var result = new Dictionary<
            string,
            PathGenerationState>(
            StringComparer.Ordinal);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var state = new PathGenerationState(
                reader.GetString(0),
                reader.GetInt64(1),
                ReadNullableString(reader, 2),
                ReadNullableString(reader, 3),
                ReadNullableDateTime(reader, 4),
                ReadNullableString(reader, 5),
                ReadNullableString(reader, 6),
                ReadNullableString(reader, 7),
                ReadNullableString(reader, 8),
                reader.GetFieldValue<string[]>(9),
                new SongMaxScores
                {
                    MaxLeadScore =
                        ReadNullableInt32(reader, 10),
                    MaxBassScore =
                        ReadNullableInt32(reader, 11),
                    MaxDrumsScore =
                        ReadNullableInt32(reader, 12),
                    MaxVocalsScore =
                        ReadNullableInt32(reader, 13),
                    MaxProLeadScore =
                        ReadNullableInt32(reader, 14),
                    MaxProBassScore =
                        ReadNullableInt32(reader, 15),
                    MaxProCymbalsScore =
                        ReadNullableInt32(reader, 16),
                    MaxProDrumsScore =
                        ReadNullableInt32(reader, 17),
                },
                ReadNullableString(reader, 18),
                reader.GetBoolean(19));
            result.Add(state.SongId, state);
        }
        return result;
    }

    private static void AddRollbackPathRestoreParameters(
        NpgsqlCommand command,
        MaxScoreMaintenanceRollbackSong song,
        long expectedCurrentRevision)
    {
        command.Parameters.AddWithValue(
            "songId",
            song.SongId);
        command.Parameters.AddWithValue(
            "expectedCurrentRevision",
            expectedCurrentRevision);
        command.Parameters.AddWithValue(
            "restoredRevision",
            song.Path.Revision);
        command.Parameters.AddWithValue(
            "datHash",
            (object?)song.Path.DatFileHash
            ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "songLastModified",
            (object?)song.Path.SongLastModified
            ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "generatedAt",
            (object?)song.Path.GeneratedAtUtc
            ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "choptVersion",
            (object?)song.Path.ChoptVersion
            ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "binaryHash",
            (object?)song.Path.ChoptBinarySha256
            ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "profile",
            (object?)song.Path.GenerationProfile
            ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "generationId",
            (object?)song.Path.ArtifactGenerationId
            ?? DBNull.Value);
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
            (object?)song.Path.Maxima.ProLead
            ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "proBass",
            (object?)song.Path.Maxima.ProBass
            ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "proCymbals",
            (object?)song.Path.Maxima.ProCymbals
            ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "proDrums",
            (object?)song.Path.Maxima.ProDrums
            ?? DBNull.Value);
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
                       path_artifact_tree_sha256,
                       path_artifact_file_count,
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
                || ReadNullableString(reader, 9)
                    != path.ArtifactTreeSha256
                || ReadNullableInt32(reader, 10)
                    != path.ArtifactFileCount
                || !reader.GetFieldValue<string[]>(11)
                    .SequenceEqual(
                        path.ExpectedInstruments,
                        StringComparer.Ordinal)
                || ReadNullableInt32(reader, 12)
                    != path.Maxima.Lead
                || ReadNullableInt32(reader, 13)
                    != path.Maxima.Bass
                || ReadNullableInt32(reader, 14)
                    != path.Maxima.Drums
                || ReadNullableInt32(reader, 15)
                    != path.Maxima.Vocals
                || ReadNullableInt32(reader, 16)
                    != path.Maxima.ProLead
                || ReadNullableInt32(reader, 17)
                    != path.Maxima.ProBass
                || ReadNullableInt32(reader, 18)
                    != path.Maxima.ProCymbals
                || ReadNullableInt32(reader, 19)
                    != path.Maxima.ProDrums
                || reader.GetBoolean(20)
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

    private static long? ReadNullableInt64(
        NpgsqlDataReader reader,
        int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : reader.GetInt64(ordinal);

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
            .ThenBy(pair => GetInstrumentOrder(pair.Instrument))
            .ToArray();
    }

    private static int GetInstrumentOrder(string instrument)
        => PathGenerationInstruments.Definitions
            .Select((definition, index) => (
                definition.Instrument,
                Index: index))
            .Single(item => string.Equals(
                item.Instrument,
                instrument,
                StringComparison.Ordinal))
            .Index;

    internal static bool PathIdentityMatches(
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
        if (!string.Equals(
                request.ExpectedChoptVersion,
                runtime.Version,
                StringComparison.Ordinal)
            || !string.Equals(
                request.ExpectedChoptBinarySha256,
                runtime.BinarySha256,
                StringComparison.Ordinal)
            || !string.Equals(
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
            run.CacheEvidence,
            run.FailureStage,
            run.FailureDetail);

    private static MaxScoreMaintenanceApplyReport
        ToApplyRollbackOwnedRejection(
            MaxScoreMaintenanceRunState run,
            bool publicReadsFrozen,
            Exception error)
        => new(
            MaxScoreMaintenanceApplyReport.CurrentReportVersion,
            Succeeded: false,
            Resumable: false,
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
            run.CacheEvidence,
            FailureStage: "rollback_owned",
            Detail: BoundDetail(error.Message));

    private static MaxScoreMaintenanceRollbackReport
        CreateRollbackDryRunReport(
            MaxScoreMaintenanceRunState run,
            string rollbackDigest,
            string beforePathFingerprint,
            string afterPathFingerprint,
            DateTime startedAtUtc)
        => new MaxScoreMaintenanceRollbackReport(
            MaxScoreMaintenanceRollbackReport
                .CurrentReportVersion,
            Validated: true,
            Succeeded: false,
            DryRun: true,
            Resumable: true,
            PublicReadsFrozen: true,
            CleanupPending: false,
            run.ManifestSha256,
            run.PlanDigest,
            rollbackDigest,
            MaxScoreMaintenancePhase.RollbackValidating,
            run.ExpectedPublishedScrapeId,
            run.ExpectedPublicationId,
            beforePathFingerprint,
            afterPathFingerprint,
            RestoredSongCount: 0,
            RebuiltInstrumentCount: 0,
            QuarantinedCandidateCount: 0,
            VisibleDeliveryCount: 0,
            StagedCacheEntryCount: 0,
            CacheEvidence: null,
            startedAtUtc,
            CompletedAtUtc: DateTime.UtcNow,
            Stages:
            [
                new MaxScoreMaintenanceRollbackStageReport(
                    MaxScoreMaintenancePhase
                        .RollbackValidating,
                    MaxScoreMaintenanceRollbackStageStatus
                        .Completed,
                    startedAtUtc,
                    DateTime.UtcNow,
                    beforePathFingerprint,
                    afterPathFingerprint,
                    RowCount: 0,
                    Detail:
                        $"Dry-run validated exact rollback identity from durable {FormatPhase(run.Phase)}/{run.Status} without mutation."),
            ],
            FailureStage: null,
            Detail:
                $"Rollback dry-run validation completed from durable {FormatPhase(run.Phase)}/{run.Status}; public reads remain frozen.");

    private static MaxScoreMaintenanceRollbackReport
        CreateRollbackFailureReport(
            MaxScoreMaintenanceManifest manifest,
            string manifestDigest,
            string planDigest,
            string rollbackDigest,
            DateTime startedAtUtc,
            bool publicReadsFrozen,
            bool dryRun,
            Exception error)
        => new MaxScoreMaintenanceRollbackReport(
            MaxScoreMaintenanceRollbackReport
                .CurrentReportVersion,
            Validated: false,
            Succeeded: false,
            DryRun: dryRun,
            Resumable: false,
            PublicReadsFrozen: publicReadsFrozen,
            CleanupPending: false,
            manifestDigest,
            planDigest,
            rollbackDigest,
            MaxScoreMaintenancePhase.None,
            manifest.ExpectedPublishedScrapeId,
            manifest.ExpectedPublicationId,
            BeforePathFingerprint: null,
            AfterPathFingerprint: null,
            RestoredSongCount: 0,
            RebuiltInstrumentCount: 0,
            QuarantinedCandidateCount: 0,
            VisibleDeliveryCount: 0,
            StagedCacheEntryCount: 0,
            CacheEvidence: null,
            startedAtUtc,
            CompletedAtUtc: DateTime.UtcNow,
            Stages: [],
            FailureStage: "preflight",
            Detail: BoundDetail(error.Message));

    private static MaxScoreMaintenanceRollbackReport
        ToRollbackReport(
            MaxScoreMaintenanceRunState run,
            string rollbackDigest,
            bool dryRun,
            bool validated,
            bool succeeded,
            bool resumable,
            bool publicReadsFrozen,
            DateTime startedAtUtc,
            DateTime? completedAtUtc,
            bool cleanupPending = false,
            string? failureStage = null,
            string? detail = null)
        => new MaxScoreMaintenanceRollbackReport(
            MaxScoreMaintenanceRollbackReport
                .CurrentReportVersion,
            validated,
            succeeded,
            dryRun,
            resumable,
            publicReadsFrozen,
            cleanupPending,
            run.ManifestSha256,
            run.PlanDigest,
            rollbackDigest,
            run.Phase,
            run.ExpectedPublishedScrapeId,
            run.ExpectedPublicationId,
            run.RollbackBeforePathFingerprint,
            run.RollbackAfterPathFingerprint,
            run.RollbackRestoredSongCount,
            run.RollbackRebuiltInstrumentCount,
            run.RollbackQuarantinedCandidateCount,
            run.RollbackVisibleDeliveryCount,
            run.RollbackStagedCacheEntryCount,
            run.RollbackCacheEvidence,
            startedAtUtc,
            completedAtUtc,
            BuildRollbackStageReports(run),
            failureStage,
            detail);

    private static IReadOnlyList<
            MaxScoreMaintenanceRollbackStageReport>
        BuildRollbackStageReports(
            MaxScoreMaintenanceRunState run)
    {
        var stages = new[]
        {
            (
                Phase:
                    MaxScoreMaintenancePhase
                        .RollbackValidating,
                Started: run.RollbackStartedAtUtc,
                Completed:
                    run.RollbackPathsRestoredAtUtc,
                Before:
                    run.RollbackBeforePathFingerprint,
                After:
                    run.RollbackAfterPathFingerprint,
                Rows: (long?)null),
            (
                Phase:
                    MaxScoreMaintenancePhase
                        .RollbackPathsRestored,
                Started: run.RollbackStartedAtUtc,
                Completed:
                    run.RollbackPathsRestoredAtUtc,
                Before:
                    run.RollbackBeforePathFingerprint,
                After:
                    run.RollbackAfterPathFingerprint,
                Rows:
                    (long?)run
                        .RollbackRestoredSongCount),
            (
                Phase:
                    MaxScoreMaintenancePhase
                        .RollbackDerivedStateRebuilt,
                Started:
                    run.RollbackPathsRestoredAtUtc,
                Completed:
                    run
                        .RollbackDerivedStateRebuiltAtUtc,
                Before: (string?)null,
                After: (string?)null,
                Rows:
                    (long?)run
                        .RollbackRebuiltInstrumentCount),
            (
                Phase:
                    MaxScoreMaintenancePhase
                        .RollbackNotificationsQuarantined,
                Started:
                    run
                        .RollbackDerivedStateRebuiltAtUtc,
                Completed:
                    run
                        .RollbackNotificationsQuarantinedAtUtc,
                Before: (string?)null,
                After: (string?)null,
                Rows:
                    (long?)run
                        .RollbackQuarantinedCandidateCount),
            (
                Phase:
                    MaxScoreMaintenancePhase
                        .RollbackCachesStaged,
                Started:
                    run
                        .RollbackNotificationsQuarantinedAtUtc,
                Completed:
                    run.RollbackCachesStagedAtUtc,
                Before: (string?)null,
                After:
                    run.RollbackCacheEvidence
                        ?.ContentFingerprint,
                Rows:
                    (long?)run
                        .RollbackStagedCacheEntryCount),
            (
                Phase:
                    MaxScoreMaintenancePhase
                        .RollbackValidated,
                Started:
                    run.RollbackCachesStagedAtUtc,
                Completed:
                    run.RollbackValidatedAtUtc,
                Before: (string?)null,
                After:
                    run.RollbackCacheEvidence
                        ?.ContentFingerprint,
                Rows: (long?)null),
            (
                Phase:
                    MaxScoreMaintenancePhase.RolledBack,
                Started:
                    run.RollbackValidatedAtUtc,
                Completed:
                    run.RolledBackAtUtc,
                Before: (string?)null,
                After:
                    run.RollbackAfterPathFingerprint,
                Rows: (long?)null),
        };
        return stages
            .Select(stage =>
            {
                var status =
                    stage.Completed.HasValue
                        ? MaxScoreMaintenanceRollbackStageStatus
                            .Completed
                        : run.Phase == stage.Phase
                          && string.Equals(
                              run.Status,
                              "failed",
                              StringComparison.Ordinal)
                            ? MaxScoreMaintenanceRollbackStageStatus
                                .Failed
                            : run.Phase >= stage.Phase
                                ? MaxScoreMaintenanceRollbackStageStatus
                                    .Running
                                : MaxScoreMaintenanceRollbackStageStatus
                                    .Pending;
                return new MaxScoreMaintenanceRollbackStageReport(
                    stage.Phase,
                    status,
                    stage.Started,
                    stage.Completed,
                    stage.Before,
                    stage.After,
                    stage.Rows,
                    run.Phase == stage.Phase
                        ? run.RollbackFailureDetail
                        : null);
            })
            .ToArray();
    }

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

    private void BeginPlanEvidenceStage(
        string stage,
        Action<string>? stageStarted)
    {
        stageStarted?.Invoke(stage);
        var injectedFailure =
            PlanEvidenceStageFailureTestHook?.Invoke(stage);
        if (injectedFailure is not null)
            throw injectedFailure;
    }

    private void ConfigureEvidenceCommandTimeout(
        NpgsqlCommand command,
        string evidenceStage)
        => MaxScoreMaintenanceCommandTimeout.Configure(
            command,
            _options.MaxScoreMaintenanceCommandTimeoutSeconds,
            evidenceStage,
            EvidenceCommandTimeoutTestHook);

    private static string FormatPlanFailureDetail(
        string evidenceStage,
        Exception error)
        => BoundDetail(
            $"stage={evidenceStage}; "
            + error.GetBaseException().Message);

    private static string BoundDetail(string detail)
    {
        const int maximumLength = 2048;
        var sanitized = detail.Replace('\0', ' ');
        return sanitized.Length <= maximumLength
            ? sanitized
            : sanitized[..maximumLength];
    }

    private static T DeserializeEvidence<T>(string json)
        where T : class
        => JsonSerializer.Deserialize<T>(
               json,
               MaxScoreMaintenanceJson.Strict)
           ?? throw new InvalidOperationException(
               $"Max-score maintenance {typeof(T).Name} is invalid.");

    private static MaxScoreMaintenancePopulationEvidence
        EmptyPopulationEvidence()
        => new(
            0,
            0,
            0,
            new string('0', 64));

    private static MaxScoreMaintenanceScoreHistoryEvidence
        EmptyScoreHistoryEvidence()
        => new(
            0,
            null,
            null,
            null,
            null,
            new string('0', 64));

    private static string FormatNullableRange(
        long? minimum,
        long? maximum)
        => minimum.HasValue && maximum.HasValue
            ? $"{minimum.Value}..{maximum.Value}"
            : "empty";

    private static string FormatNullableRange(
        DateTime? minimum,
        DateTime? maximum)
        => minimum.HasValue && maximum.HasValue
            ? $"{minimum.Value:O}..{maximum.Value:O}"
            : "empty";

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
            "rollback_validating" =>
                MaxScoreMaintenancePhase.RollbackValidating,
            "rollback_paths_restored" =>
                MaxScoreMaintenancePhase.RollbackPathsRestored,
            "rollback_derived_state_rebuilt" =>
                MaxScoreMaintenancePhase
                    .RollbackDerivedStateRebuilt,
            "rollback_notifications_quarantined" =>
                MaxScoreMaintenancePhase
                    .RollbackNotificationsQuarantined,
            "rollback_caches_staged" =>
                MaxScoreMaintenancePhase.RollbackCachesStaged,
            "rollback_validated" =>
                MaxScoreMaintenancePhase.RollbackValidated,
            "rolled_back" =>
                MaxScoreMaintenancePhase.RolledBack,
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
            MaxScoreMaintenancePhase.RollbackValidating =>
                "rollback_validating",
            MaxScoreMaintenancePhase.RollbackPathsRestored =>
                "rollback_paths_restored",
            MaxScoreMaintenancePhase
                .RollbackDerivedStateRebuilt =>
                "rollback_derived_state_rebuilt",
            MaxScoreMaintenancePhase
                .RollbackNotificationsQuarantined =>
                "rollback_notifications_quarantined",
            MaxScoreMaintenancePhase.RollbackCachesStaged =>
                "rollback_caches_staged",
            MaxScoreMaintenancePhase.RollbackValidated =>
                "rollback_validated",
            MaxScoreMaintenancePhase.RolledBack =>
                "rolled_back",
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

    private sealed record PopulationScopeEvidenceRow(
        string SongId,
        string Instrument,
        long ReportedTotalEntries,
        long ResolvedRowCount,
        long EffectiveTotalEntries);

    private sealed record MaxScoreMaintenancePopulationSnapshot(
        IReadOnlyDictionary<
            (string SongId, string Instrument),
            long> Population,
        MaxScoreMaintenancePopulationEvidence Evidence);

    private sealed record MaxScoreMaintenanceReadSnapshot(
        IReadOnlyDictionary<string, SongMaxScores>
            PostPromotionMaxScores,
        IReadOnlyDictionary<string, SongMaxScores>
            ScoreHistoryEvidenceMaxScores,
        IReadOnlyDictionary<string, SongMaxScores>
            RollbackScoreHistoryEvidenceMaxScores,
        IReadOnlyDictionary<
            (string SongId, string Instrument),
            long> Population,
        MaxScoreMaintenancePopulationEvidence
            PopulationEvidence,
        MaxScoreMaintenanceScoreHistoryEvidence
            ScoreHistoryEvidence,
        MaxScoreMaintenanceScoreHistoryEvidence
            RollbackScoreHistoryEvidence,
        IReadOnlyList<MaxScoreMaintenanceObservedScoreCheck>
            ObservedScoreChecks,
        IReadOnlyList<(
            string SongId,
            string Instrument)> PublishedScopes,
        IReadOnlyList<string> AffectedPlayerStatsAccounts,
        IReadOnlyList<string> AffectedRegisteredAccounts,
        IReadOnlyList<string> OverlayOnlyRegisteredAccounts);

    private sealed record CacheEntryFingerprintRow(
        string AccountId,
        int Score,
        int Rank);

    private sealed record CacheScopeFingerprintRow(
        string SongId,
        string Instrument,
        long TotalEntries,
        int LocalEntries,
        IReadOnlyList<CacheEntryFingerprintRow> Entries);

    internal sealed record CachePlayerScoreFingerprintRow(
        string SongId,
        string Instrument,
        int Score,
        int Rank,
        long TotalEntries);

    internal sealed record CacheAccountFingerprintRow(
        string AccountId,
        IReadOnlyList<CachePlayerScoreFingerprintRow> Scores);

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
        string ScoreHistoryFingerprint,
        MaxScoreMaintenancePopulationEvidence
            PopulationEvidence,
        MaxScoreMaintenanceScoreHistoryEvidence
            ScoreHistoryEvidence,
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
        MaxScoreMaintenanceCacheEvidence? CacheEvidence,
        string? FailureStage,
        string? FailureDetail,
        DateTime CreatedAtUtc,
        DateTime? RollbackStartedAtUtc,
        DateTime? RollbackPathsRestoredAtUtc,
        DateTime? RollbackDerivedStateRebuiltAtUtc,
        DateTime? RollbackNotificationsQuarantinedAtUtc,
        DateTime? RollbackCachesStagedAtUtc,
        DateTime? RollbackValidatedAtUtc,
        DateTime? RolledBackAtUtc,
        string? RollbackBeforePathFingerprint,
        string? RollbackAfterPathFingerprint,
        int RollbackRestoredSongCount,
        int RollbackRebuiltInstrumentCount,
        long? RollbackNotificationMaintenanceRunId,
        long RollbackQuarantinedCandidateCount,
        int RollbackVisibleDeliveryCount,
        long RollbackStagedCacheEntryCount,
        MaxScoreMaintenanceCacheEvidence? RollbackCacheEvidence,
        string? RollbackFailureStage,
        string? RollbackFailureDetail,
        DateTime UpdatedAtUtc,
        DateTime? CompletedAtUtc);
}

internal sealed record MaxScoreMaintenanceRollbackRuntimeState(
    int MaintenanceBackendCount,
    int WorkerBackendCount,
    int WaitingLockCount);
