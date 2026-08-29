using System.Data;
using FSTService.Scraping.Replay;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService.Persistence.Maintenance;

public interface ISnapshotGenerationRetentionPlanner
{
    bool IsEnabled { get; }

    Task<SnapshotGenerationRetentionPlanResult> PlanAsync(
        SnapshotGenerationRetentionPlanRequest request,
        CancellationToken ct = default);
}

public sealed partial class SnapshotGenerationRetentionPlanner
    : ISnapshotGenerationRetentionPlanner
{
    private const int MinimumCommandTimeoutSeconds = 5;
    private const int MaximumCommandTimeoutSeconds = 120;
    private const int MaximumLockWaitMilliseconds = 5_000;

    private readonly NpgsqlDataSource _dataSource;
    private readonly SnapshotGenerationRetentionRepository _repository;
    private readonly ISnapshotGenerationRetentionOracle _oracle;
    private readonly ServiceMaintenanceLock _serviceMaintenanceLock;
    private readonly IOptions<DatabaseMaintenanceOptions> _options;
    private readonly IOptions<ScraperOptions> _scraperOptions;
    private readonly ILogger<SnapshotGenerationRetentionPlanner> _log;

    public SnapshotGenerationRetentionPlanner(
        NpgsqlDataSource dataSource,
        SnapshotGenerationRetentionRepository repository,
        ISnapshotGenerationRetentionOracle oracle,
        ServiceMaintenanceLock serviceMaintenanceLock,
        IOptions<DatabaseMaintenanceOptions> options,
        IOptions<ScraperOptions> scraperOptions,
        ILogger<SnapshotGenerationRetentionPlanner> log)
    {
        _dataSource = dataSource;
        _repository = repository;
        _oracle = oracle;
        _serviceMaintenanceLock = serviceMaintenanceLock;
        _options = options;
        _scraperOptions = scraperOptions;
        _log = log;
    }

    public bool IsEnabled =>
        _options.Value
            .SnapshotGenerationRetentionReportOnlyEnabled;

    public async Task<SnapshotGenerationRetentionPlanResult> PlanAsync(
        SnapshotGenerationRetentionPlanRequest request,
        CancellationToken ct = default)
    {
        if (!IsEnabled)
            return SnapshotGenerationRetentionPlanResult.Disabled();

        ValidateRequest(request);
        try
        {
            var existing =
                await _repository.GetCycleForSafePointAsync(
                    request.TriggerScrapeId,
                    request.TriggerPublicationId,
                    request.SafePointKind,
                    ct);
            if (existing is not null)
            {
                return BuildResult(
                    existing,
                    SnapshotGenerationRetentionPlanDisposition
                        .Existing,
                    "the terminal safe point already has an immutable report-only retention cycle",
                    Retryable: false);
            }

            if (!request.BackgroundWorkQuiesced)
            {
                return await DeferAsync(
                    request,
                    "background_work_not_quiesced",
                    "The worker did not establish background-operation quiescence before the retention observation.",
                    retryable: true,
                    new
                    {
                        request.BackgroundWorkQuiesced,
                        request.BroadcastCompletedScrapeId,
                    },
                    ct);
            }

            if (request.BroadcastCompletedScrapeId !=
                request.TriggerScrapeId)
            {
                return await DeferAsync(
                    request,
                    "scores_changed_broadcast_incomplete",
                    $"The post-publication scores-changed broadcast was not confirmed for scrape {request.TriggerScrapeId}.",
                    retryable: true,
                    new
                    {
                        request.TriggerScrapeId,
                        request.BroadcastCompletedScrapeId,
                    },
                    ct);
            }

            return await PlanCoreAsync(request, ct);
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
                "Snapshot-generation retention report-only planning failed for scrape {ScrapeId}, publication {PublicationId}.",
                request.TriggerScrapeId,
                request.TriggerPublicationId);
            try
            {
                return await PersistFailureAsync(
                    request,
                    ex,
                    CancellationToken.None);
            }
            catch (Exception persistenceException)
            {
                throw new AggregateException(
                    "Snapshot-generation retention planning failed and its durable failure evidence could not be persisted.",
                    ex,
                    persistenceException);
            }
        }
    }

    private async Task<SnapshotGenerationRetentionPlanResult>
        PlanCoreAsync(
            SnapshotGenerationRetentionPlanRequest request,
            CancellationToken ct)
    {
        var configured = _options.Value;
        var commandTimeoutSeconds = Math.Clamp(
            configured
                .SnapshotGenerationRetentionCommandTimeoutSeconds,
            MinimumCommandTimeoutSeconds,
            MaximumCommandTimeoutSeconds);
        var lockWait = TimeSpan.FromMilliseconds(
            Math.Clamp(
                configured
                    .ServiceMaintenanceLockWaitMilliseconds,
                0,
                MaximumLockWaitMilliseconds));

        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);

        var registrationLock =
            await PostgresSessionAdvisoryLock.TryAcquireAsync(
                connection,
                RegistrationMutationGate.AdvisoryLockKey,
                shared: false,
                lockWait,
                ct);
        if (registrationLock is null)
        {
            return await DeferAsync(
                request,
                "registration_mutation_lock_busy",
                "Registration mutation work currently owns the first advisory lock in the maintenance order.",
                retryable: true,
                new
                {
                    LockKey =
                        RegistrationMutationGate.AdvisoryLockKey,
                    WaitMilliseconds =
                        (int)lockWait.TotalMilliseconds,
                },
                ct);
        }
        await using var registrationLease = registrationLock;

        var maintenanceLock =
            await _serviceMaintenanceLock.TryAcquireAsync(
                connection,
                lockWait,
                ct);
        if (maintenanceLock is null)
        {
            await registrationLease.DisposeAsync();
            return await DeferAsync(
                request,
                "service_maintenance_lock_busy",
                "Database TTL or another service-maintenance observer currently owns the centralized maintenance lock.",
                retryable: true,
                new
                {
                    LockKey =
                        ServiceMaintenanceLock.AdvisoryLockKey,
                    WaitMilliseconds =
                        (int)lockWait.TotalMilliseconds,
                },
                ct);
        }
        await using var maintenanceLease = maintenanceLock;

        var publicationLock =
            await PostgresSessionAdvisoryLock.TryAcquireAsync(
                connection,
                PublicationGenerationSchema.AdvisoryLockKey,
                shared: true,
                lockWait,
                ct);
        if (publicationLock is null)
        {
            await maintenanceLease.DisposeAsync();
            await registrationLease.DisposeAsync();
            return await DeferAsync(
                request,
                "publication_lock_busy",
                "Publication allocation or commit currently owns the publication advisory lock.",
                retryable: true,
                new
                {
                    LockKey =
                        PublicationGenerationSchema.AdvisoryLockKey,
                    Shared = true,
                    WaitMilliseconds =
                        (int)lockWait.TotalMilliseconds,
                },
                ct);
        }
        await using var publicationLease = publicationLock;

        var plannerLock =
            await PostgresSessionAdvisoryLock.TryAcquireAsync(
                connection,
                SnapshotGenerationRetentionContract
                    .PlannerAdvisoryLockKey,
                shared: false,
                lockWait,
                ct);
        if (plannerLock is null)
        {
            await publicationLease.DisposeAsync();
            await maintenanceLease.DisposeAsync();
            await registrationLease.DisposeAsync();
            return await DeferAsync(
                request,
                "retention_planner_lock_busy",
                "Another report-only snapshot-generation retention planner owns the final planner lock.",
                retryable: true,
                new
                {
                    LockKey =
                        SnapshotGenerationRetentionContract
                            .PlannerAdvisoryLockKey,
                    WaitMilliseconds =
                        (int)lockWait.TotalMilliseconds,
                },
                ct);
        }
        await using var plannerLease = plannerLock;

        ReadObservation observation;
        await using (var transaction =
                     await connection.BeginTransactionAsync(
                         IsolationLevel.RepeatableRead,
                         ct))
        {
            await ConfigureReadOnlyTransactionAsync(
                connection,
                transaction,
                commandTimeoutSeconds,
                ct);

            var configuredResumeScrapeId = Math.Max(
                0,
                _scraperOptions.Value.ResumeScrapeId);
            var safePoint = await LoadSafePointStateAsync(
                connection,
                transaction,
                request,
                configuredResumeScrapeId,
                commandTimeoutSeconds,
                ct);
            if (safePoint.DeferralBlockers.Count > 0)
            {
                await transaction.CommitAsync(ct);
                var blocker = safePoint.DeferralBlockers[0];
                return await DeferAsync(
                    request,
                    blocker.Code,
                    blocker.Detail,
                    retryable: true,
                    new
                    {
                        Blockers =
                            safePoint.DeferralBlockers,
                        GlobalBlockers =
                            safePoint.GlobalBlockers,
                        Anomalies =
                            safePoint.Anomalies,
                        safePoint.PublishedScrapeId,
                        safePoint.CurrentPublicationId,
                        safePoint.PreviousPublicationId,
                        safePoint.WorkingPublicationId,
                    },
                    ct);
            }

            var topology = await LoadTopologyAsync(
                connection,
                transaction,
                commandTimeoutSeconds,
                ct);
            var references = await LoadPrimaryReferencesAsync(
                connection,
                transaction,
                topology.Children,
                safePoint.NamedPublications,
                configuredResumeScrapeId,
                commandTimeoutSeconds,
                ct);
            var oracle = await _oracle.LoadAsync(
                connection,
                transaction,
                configuredResumeScrapeId,
                commandTimeoutSeconds,
                ct);
            await transaction.CommitAsync(ct);
            observation = new ReadObservation(
                safePoint,
                topology,
                references,
                oracle);
        }

        var persistRequest =
            BuildPersistRequest(request, observation);
        var persisted = await _repository.PersistAsync(
            connection,
            persistRequest,
            commandTimeoutSeconds,
            ct);
        var cycle = persisted.Cycle;
        if (!persisted.Inserted)
        {
            return BuildResult(
                cycle,
                SnapshotGenerationRetentionPlanDisposition
                    .Existing,
                "the terminal safe point already has an immutable report-only retention cycle",
                Retryable: false);
        }

        var disposition = cycle.Status switch
        {
            SnapshotGenerationRetentionCycleStatus.Observed =>
                SnapshotGenerationRetentionPlanDisposition.Observed,
            SnapshotGenerationRetentionCycleStatus.Blocked =>
                SnapshotGenerationRetentionPlanDisposition.Blocked,
            SnapshotGenerationRetentionCycleStatus.OracleMismatch =>
                SnapshotGenerationRetentionPlanDisposition
                    .OracleMismatch,
            _ =>
                SnapshotGenerationRetentionPlanDisposition.Failed,
        };
        var reason = disposition switch
        {
            SnapshotGenerationRetentionPlanDisposition.Observed =>
                $"report-only observation persisted {cycle.CandidateCount} candidate, {cycle.ProtectedCount} protected, and {cycle.BlockedCount} blocked child relation(s), with {persistRequest.Anomalies.Count} nonblocking anomaly warning(s)",
            SnapshotGenerationRetentionPlanDisposition
                .OracleMismatch =>
                "independent SQL liveness oracle disagreed with the primary child sets; the cycle failed closed with zero candidates",
            SnapshotGenerationRetentionPlanDisposition.Blocked =>
                $"report-only observation persisted {cycle.CandidateCount} candidate, {cycle.ProtectedCount} protected, and {cycle.BlockedCount} blocked child relation(s), but one or more safety blockers prevent a clean cycle",
            _ =>
                "report-only retention planning failed",
        };

        _log.LogInformation(
            "Snapshot-generation retention report-only cycle {CycleId} status={Status}, oracleAgreement={OracleAgreement}, candidates={CandidateCount}, protected={ProtectedCount}, blocked={BlockedCount}, anomalies={AnomalyCount}, candidateBytes={CandidateBytes}.",
            cycle.CycleId,
            cycle.Status,
            cycle.OracleAgreement,
            cycle.CandidateCount,
            cycle.ProtectedCount,
            cycle.BlockedCount,
            persistRequest.Anomalies.Count,
            cycle.CandidateBytes);
        return BuildResult(
            cycle,
            disposition,
            reason,
            Retryable: false);
    }

    private SnapshotGenerationRetentionPersistRequest
        BuildPersistRequest(
            SnapshotGenerationRetentionPlanRequest request,
            ReadObservation observation)
    {
        var plannerChildKeys = observation.Topology.Children
            .Select(static child => child.PhysicalKey)
            .OrderBy(
                static key => key,
                StringComparer.Ordinal)
            .ToArray();
        var plannerLiveKeys = observation.References.RootReasons
            .Keys
            .OrderBy(
                static key => key,
                StringComparer.Ordinal)
            .ToArray();
        var plannerCandidateKeys = plannerChildKeys
            .Where(key =>
                !observation.References.RootReasons
                    .ContainsKey(key))
            .ToArray();
        var oracleChildKeys = observation.Oracle.ChildKeys
            .OrderBy(
                static key => key,
                StringComparer.Ordinal)
            .ToArray();
        var oracleLiveKeys = observation.Oracle.LiveKeys
            .OrderBy(
                static key => key,
                StringComparer.Ordinal)
            .ToArray();
        var oracleCandidateKeys =
            observation.Oracle.CandidateKeys
                .OrderBy(
                    static key => key,
                    StringComparer.Ordinal)
                .ToArray();
        var plannerPublicationSourceValidations =
            observation.References
                .PublicationSourceValidations
                .Select(static validation =>
                    validation.ComparisonKey)
                .OrderBy(
                    static validation => validation,
                    StringComparer.Ordinal)
                .ToArray();
        var oraclePublicationSourceValidations =
            observation.Oracle
                .EffectivePublicationSourceValidations
                .Select(static validation =>
                    validation.ComparisonKey)
                .OrderBy(
                    static validation => validation,
                    StringComparer.Ordinal)
                .ToArray();
        var publicationSourceValidationAgrees =
            plannerPublicationSourceValidations.SequenceEqual(
                oraclePublicationSourceValidations,
                StringComparer.Ordinal);
        var plannerIndexTopologyValidations =
            observation.Topology.IndexTopologyValidations
                .Select(static validation =>
                    validation.ComparisonKey)
                .OrderBy(
                    static validation => validation,
                    StringComparer.Ordinal)
                .ToArray();
        var oracleIndexTopologyValidations =
            observation.Oracle
                .EffectiveIndexTopologyValidations
                .Select(static validation =>
                    validation.ComparisonKey)
                .OrderBy(
                    static validation => validation,
                    StringComparer.Ordinal)
                .ToArray();
        var indexTopologyValidationAgrees =
            plannerIndexTopologyValidations.SequenceEqual(
                oracleIndexTopologyValidations,
                StringComparer.Ordinal);
        var comparison = CompareSets(
            plannerChildKeys,
            plannerLiveKeys,
            plannerCandidateKeys,
            oracleChildKeys,
            oracleLiveKeys,
            oracleCandidateKeys,
            publicationSourceValidationAgrees,
            indexTopologyValidationAgrees);

        var globalBlockers = observation.SafePoint
            .GlobalBlockers
            .Concat(observation.Topology.GlobalBlockers)
            .Concat(observation.References.GlobalBlockers)
            .Concat(
                observation.Oracle
                    .EffectivePublicationSourceValidations
                    .Where(static validation =>
                        !validation.IsValid)
                    .Select(static validation =>
                        new SnapshotGenerationRetentionBlocker(
                            "oracle_named_publication_source_set_invalid",
                            $"Independent SQL validation rejected named {validation.Slot} publication {validation.PublicationId}/{validation.ScrapeId}: {validation.ComparisonKey}.")))
            .Concat(
                observation.Oracle
                    .EffectiveIndexTopologyValidations
                    .Where(static validation =>
                        !validation.IsValid)
                    .Select(static validation =>
                        new SnapshotGenerationRetentionBlocker(
                            "oracle_index_topology_invalid",
                            $"Independent catalog traversal rejected the {validation.Instrument} top/root/default/numeric-child index hierarchy: {validation.ComparisonKey}.")))
            .DistinctBy(static blocker =>
                (blocker.Code, blocker.Detail))
            .OrderBy(
                static blocker => blocker.Code,
                StringComparer.Ordinal)
            .ThenBy(
                static blocker => blocker.Detail,
                StringComparer.Ordinal)
            .ToList();
        if (!comparison.Agrees)
        {
            globalBlockers.Add(
                new SnapshotGenerationRetentionBlocker(
                    "liveness_oracle_mismatch",
                    publicationSourceValidationAgrees
                    && indexTopologyValidationAgrees
                        ? TierZeroCanonicalJson.SerializeToString(
                            comparison)
                        : "The independent SQL oracle disagreed with a primary source-binding or index-topology validation: "
                          + TierZeroCanonicalJson.SerializeToString(
                              new
                              {
                                  Planner =
                                      plannerPublicationSourceValidations,
                                  Oracle =
                                      oraclePublicationSourceValidations,
                                  PlannerIndexTopology =
                                      plannerIndexTopologyValidations,
                                  OracleIndexTopology =
                                      oracleIndexTopologyValidations,
                              })));
        }

        var evaluations =
            new List<SnapshotGenerationRetentionEvaluation>(
                observation.Topology.Children.Count);
        foreach (var child in observation.Topology.Children
                     .OrderBy(
                         static item =>
                             item.InstrumentDefinition
                                 .CanonicalOrder)
                     .ThenBy(
                         static item => item.SnapshotId)
                     .ThenBy(
                         static item =>
                             item.ChildRelation,
                         StringComparer.Ordinal))
        {
            var plannerLive =
                observation.References.RootReasons
                    .TryGetValue(
                        child.PhysicalKey,
                        out var reasons);
            var oracleLive =
                observation.Oracle.LiveKeys.Contains(
                    child.PhysicalKey);
            var blockers = child.TopologyBlockers
                .Concat(
                    observation.References
                        .ChildBlockers
                        .GetValueOrDefault(
                            child.PhysicalKey)
                        ?? [])
                .ToList();
            string classification;
            if (!comparison.Agrees)
            {
                blockers.Add(
                    new SnapshotGenerationRetentionBlocker(
                        "liveness_oracle_mismatch",
                        "The primary planner and independent SQL oracle did not return identical physical child/live/candidate sets."));
                classification =
                    SnapshotGenerationRetentionClassification
                        .OracleMismatch;
            }
            else if (plannerLive)
            {
                classification =
                    SnapshotGenerationRetentionClassification
                        .Protected;
            }
            else if (globalBlockers.Count > 0
                     || blockers.Count > 0)
            {
                classification =
                    SnapshotGenerationRetentionClassification
                        .Blocked;
            }
            else
            {
                classification =
                    SnapshotGenerationRetentionClassification
                        .Candidate;
            }

            evaluations.Add(
                new SnapshotGenerationRetentionEvaluation(
                    child,
                    plannerLive,
                    oracleLive,
                    reasons ?? [],
                    blockers
                        .Concat(
                            classification ==
                                SnapshotGenerationRetentionClassification
                                    .Blocked
                                ? globalBlockers
                                : [])
                        .DistinctBy(static blocker =>
                            (blocker.Code, blocker.Detail))
                        .OrderBy(
                            static blocker => blocker.Code,
                            StringComparer.Ordinal)
                        .ThenBy(
                            static blocker => blocker.Detail,
                            StringComparer.Ordinal)
                        .ToArray(),
                    classification));
        }

        var candidateIdentityHash =
            ComputeCandidateIdentityHash(evaluations);
        var observationHash = ComputeObservationHash(
            request,
            evaluations,
            globalBlockers,
            comparison,
            observation.SafePoint.Anomalies);
        var status = !comparison.Agrees
            ? SnapshotGenerationRetentionCycleStatus
                .OracleMismatch
            : evaluations.Any(static evaluation =>
                evaluation.Blockers.Count > 0)
              || globalBlockers.Count > 0
                ? SnapshotGenerationRetentionCycleStatus.Blocked
                : SnapshotGenerationRetentionCycleStatus.Observed;

        return new SnapshotGenerationRetentionPersistRequest(
            request,
            status,
            comparison.Agrees,
            candidateIdentityHash,
            observationHash,
            plannerChildKeys,
            plannerLiveKeys,
            plannerCandidateKeys,
            oracleChildKeys,
            oracleLiveKeys,
            oracleCandidateKeys,
            observation.References
                .PublicationSourceValidations,
            observation.Oracle
                .EffectivePublicationSourceValidations,
            observation.Topology
                .IndexTopologyValidations,
            observation.Oracle
                .EffectiveIndexTopologyValidations,
            evaluations,
            globalBlockers,
            observation.SafePoint.Anomalies,
            ErrorMessage: null);
    }

    internal static SnapshotGenerationRetentionSetComparison
        CompareSets(
            IEnumerable<string> plannerChildren,
            IEnumerable<string> plannerLive,
            IEnumerable<string> plannerCandidates,
            IEnumerable<string> oracleChildren,
            IEnumerable<string> oracleLive,
            IEnumerable<string> oracleCandidates,
            bool publicationSourceValidationAgrees = true,
            bool indexTopologyValidationAgrees = true)
    {
        var plannerChildSet = plannerChildren.ToHashSet(
            StringComparer.Ordinal);
        var plannerLiveSet = plannerLive.ToHashSet(
            StringComparer.Ordinal);
        var plannerCandidateSet = plannerCandidates.ToHashSet(
            StringComparer.Ordinal);
        var oracleChildSet = oracleChildren.ToHashSet(
            StringComparer.Ordinal);
        var oracleLiveSet = oracleLive.ToHashSet(
            StringComparer.Ordinal);
        var oracleCandidateSet = oracleCandidates.ToHashSet(
            StringComparer.Ordinal);

        static string[] Except(
            IReadOnlySet<string> left,
            IReadOnlySet<string> right) =>
            left.Where(key => !right.Contains(key))
                .OrderBy(
                    static key => key,
                    StringComparer.Ordinal)
                .ToArray();

        var plannerOnlyChildren =
            Except(plannerChildSet, oracleChildSet);
        var oracleOnlyChildren =
            Except(oracleChildSet, plannerChildSet);
        var plannerOnlyLive =
            Except(plannerLiveSet, oracleLiveSet);
        var oracleOnlyLive =
            Except(oracleLiveSet, plannerLiveSet);
        var plannerOnlyCandidates =
            Except(plannerCandidateSet, oracleCandidateSet);
        var oracleOnlyCandidates =
            Except(oracleCandidateSet, plannerCandidateSet);
        return new SnapshotGenerationRetentionSetComparison(
            publicationSourceValidationAgrees
            && indexTopologyValidationAgrees
            && plannerOnlyChildren.Length == 0
            && oracleOnlyChildren.Length == 0
            && plannerOnlyLive.Length == 0
            && oracleOnlyLive.Length == 0
            && plannerOnlyCandidates.Length == 0
            && oracleOnlyCandidates.Length == 0,
            publicationSourceValidationAgrees,
            indexTopologyValidationAgrees,
            plannerOnlyChildren,
            oracleOnlyChildren,
            plannerOnlyLive,
            oracleOnlyLive,
            plannerOnlyCandidates,
            oracleOnlyCandidates);
    }

    internal static string ComputeCandidateIdentityHash(
        IEnumerable<SnapshotGenerationRetentionEvaluation>
            evaluations) =>
        TierZeroCanonicalJson.Sha256Hex(
            TierZeroCanonicalJson.Serialize(
                evaluations
                    .Where(static evaluation =>
                        evaluation.Classification ==
                        SnapshotGenerationRetentionClassification
                            .Candidate)
                    .OrderBy(
                        static evaluation =>
                            evaluation.Child.PhysicalKey,
                        StringComparer.Ordinal)
                    .Select(static evaluation => new
                    {
                        evaluation.Child.PhysicalKey,
                        evaluation.Child
                            .StableChildIdentityHash,
                        evaluation.Child
                            .StableConfigSchemaHash,
                    })));

    internal static string ComputeObservationHash(
        SnapshotGenerationRetentionPlanRequest request,
        IEnumerable<SnapshotGenerationRetentionEvaluation>
            evaluations,
        IEnumerable<SnapshotGenerationRetentionBlocker>
            globalBlockers,
        SnapshotGenerationRetentionSetComparison comparison,
        IEnumerable<SnapshotGenerationRetentionAnomaly>?
            anomalies = null) =>
        TierZeroCanonicalJson.Sha256Hex(
            TierZeroCanonicalJson.Serialize(new
            {
                SnapshotGenerationRetentionContract
                    .PlannerVersion,
                SnapshotGenerationRetentionContract
                    .ConfigVersion,
                request.TriggerScrapeId,
                request.TriggerPublicationId,
                request.SafePointKind,
                Evaluations = evaluations
                    .OrderBy(
                        static evaluation =>
                            evaluation.Child.PhysicalKey,
                        StringComparer.Ordinal)
                    .Select(static evaluation => new
                    {
                        evaluation.Child.PhysicalKey,
                        evaluation.Child
                            .StableChildIdentityHash,
                        evaluation.Child
                            .StableConfigSchemaHash,
                        evaluation.Child.RowEstimate,
                        evaluation.Child.TotalBytes,
                        evaluation.Child
                            .ObservationMetricsHash,
                        evaluation.PlannerLive,
                        evaluation.OracleLive,
                        evaluation.Classification,
                        evaluation.RootReasons,
                        evaluation.Blockers,
                    }),
                GlobalBlockers = globalBlockers
                    .OrderBy(
                        static blocker => blocker.Code,
                        StringComparer.Ordinal)
                    .ThenBy(
                        static blocker => blocker.Detail,
                        StringComparer.Ordinal),
                Anomalies = (
                        anomalies
                        ?? Array.Empty<
                            SnapshotGenerationRetentionAnomaly>())
                    .OrderBy(
                        static anomaly => anomaly.Code,
                        StringComparer.Ordinal)
                    .ThenBy(
                        static anomaly => anomaly.PublicationId)
                    .ThenBy(
                        static anomaly => anomaly.ScrapeId)
                    .ThenBy(
                        static anomaly =>
                            anomaly.PublicationStatus,
                        StringComparer.Ordinal)
                    .ThenBy(
                        static anomaly => anomaly.Detail,
                        StringComparer.Ordinal),
                Comparison = comparison,
            }));

    private async Task<SnapshotGenerationRetentionPlanResult>
        DeferAsync(
            SnapshotGenerationRetentionPlanRequest request,
            string code,
            string detail,
            bool retryable,
            object evidence,
            CancellationToken ct)
    {
        await _repository.RecordDeferralAsync(
            request,
            code,
            detail,
            retryable,
            evidence,
            ct);
        _log.LogWarning(
            "Snapshot-generation retention report-only safe point deferred: {Code}: {Detail}",
            code,
            detail);
        return new SnapshotGenerationRetentionPlanResult(
            SnapshotGenerationRetentionPlanDisposition.Deferred,
            null,
            $"{code}: {detail}",
            null,
            null,
            0,
            0,
            0,
            0,
            OracleAgreement: false,
            Retryable: retryable);
    }

    private async Task<SnapshotGenerationRetentionPlanResult>
        PersistFailureAsync(
            SnapshotGenerationRetentionPlanRequest request,
            Exception exception,
            CancellationToken ct)
    {
        var baseException = exception.GetBaseException();
        var error = baseException.Message.Length <= 4_000
            ? baseException.Message
            : baseException.Message[..4_000];
        var emptyHash = TierZeroCanonicalJson.Sha256Hex(
            TierZeroCanonicalJson.Serialize(Array.Empty<string>()));
        var observationHash =
            TierZeroCanonicalJson.Sha256Hex(
                TierZeroCanonicalJson.Serialize(new
                {
                    SnapshotGenerationRetentionContract
                        .PlannerVersion,
                    SnapshotGenerationRetentionContract
                        .ConfigVersion,
                    request.TriggerScrapeId,
                    request.TriggerPublicationId,
                    ErrorType =
                        baseException.GetType().FullName,
                    Error = error,
                }));
        var persistRequest =
            new SnapshotGenerationRetentionPersistRequest(
                request,
                SnapshotGenerationRetentionCycleStatus.Failed,
                OracleAgreement: false,
                emptyHash,
                observationHash,
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [
                    new SnapshotGenerationRetentionBlocker(
                        "planner_exception",
                        error),
                ],
                [],
                error);

        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        var lockWait = TimeSpan.FromMilliseconds(
            Math.Clamp(
                _options.Value
                    .ServiceMaintenanceLockWaitMilliseconds,
                0,
                MaximumLockWaitMilliseconds));
        await using var registrationLease =
            await PostgresSessionAdvisoryLock.TryAcquireAsync(
                connection,
                RegistrationMutationGate.AdvisoryLockKey,
                shared: false,
                lockWait,
                ct)
            ?? throw new InvalidOperationException(
                "Could not reacquire the registration mutation lock to persist retention failure evidence.");
        await using var maintenanceLease =
            await _serviceMaintenanceLock.TryAcquireAsync(
                connection,
                lockWait,
                ct)
            ?? throw new InvalidOperationException(
                "Could not reacquire the centralized service-maintenance lock to persist retention failure evidence.");
        await using var publicationLease =
            await PostgresSessionAdvisoryLock.TryAcquireAsync(
                connection,
                PublicationGenerationSchema.AdvisoryLockKey,
                shared: true,
                lockWait,
                ct)
            ?? throw new InvalidOperationException(
                "Could not reacquire the shared publication lock to persist retention failure evidence.");
        await using var plannerLease =
            await PostgresSessionAdvisoryLock.TryAcquireAsync(
                connection,
                SnapshotGenerationRetentionContract
                    .PlannerAdvisoryLockKey,
                shared: false,
                lockWait,
                ct)
            ?? throw new InvalidOperationException(
                "Could not reacquire the retention planner lock to persist failure evidence.");
        var persisted = await _repository.PersistAsync(
            connection,
            persistRequest,
            Math.Clamp(
                _options.Value
                    .SnapshotGenerationRetentionCommandTimeoutSeconds,
                MinimumCommandTimeoutSeconds,
                MaximumCommandTimeoutSeconds),
            ct);
        return BuildResult(
            persisted.Cycle,
            persisted.Inserted
                ? SnapshotGenerationRetentionPlanDisposition.Failed
                : SnapshotGenerationRetentionPlanDisposition.Existing,
            persisted.Inserted
                ? "snapshot-generation retention planning failed; immutable failure evidence was persisted"
                : "the terminal safe point already has an immutable retention cycle",
            Retryable: false);
    }

    private static SnapshotGenerationRetentionPlanResult
        BuildResult(
            SnapshotGenerationRetentionCycle cycle,
            SnapshotGenerationRetentionPlanDisposition disposition,
            string reason,
            bool Retryable) =>
        new(
            disposition,
            cycle.CycleId,
            reason,
            cycle.CandidateIdentityHash,
            cycle.ObservationHash,
            cycle.CandidateCount,
            cycle.ProtectedCount,
            cycle.BlockedCount,
            cycle.CandidateBytes,
            cycle.OracleAgreement,
            Retryable);

    private static async Task ConfigureReadOnlyTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = """
            SET TRANSACTION READ ONLY;
            SELECT set_config('lock_timeout', '500ms', true);
            SELECT set_config('statement_timeout', @statementTimeout, true);
            SELECT set_config(
                'idle_in_transaction_session_timeout',
                @idleTimeout,
                true);
            """;
        command.Parameters.AddWithValue(
            "statementTimeout",
            $"{commandTimeoutSeconds}s");
        command.Parameters.AddWithValue(
            "idleTimeout",
            $"{commandTimeoutSeconds + 5}s");
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void ValidateRequest(
        SnapshotGenerationRetentionPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TriggerScrapeId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "A terminal retention safe point requires a positive scrape ID.");
        }
        if (request.TriggerPublicationId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "A terminal retention safe point requires a positive publication ID.");
        }
        if (request.SafePointAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "The terminal retention safe-point timestamp must be UTC.",
                nameof(request));
        }
        if (!string.Equals(
                request.SafePointKind,
                SnapshotGenerationRetentionContract
                    .TerminalWorkerSafePoint,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Unsupported snapshot-generation retention safe point.",
                nameof(request));
        }
    }

    private sealed record ReadObservation(
        SafePointState SafePoint,
        TopologyState Topology,
        PrimaryReferenceState References,
        SnapshotGenerationRetentionOracleResult Oracle);
}
