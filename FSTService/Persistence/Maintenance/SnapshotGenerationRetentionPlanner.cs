using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
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

public sealed class SnapshotGenerationRetentionPlanner
    : ISnapshotGenerationRetentionPlanner
{
    private const int CommandTimeoutSeconds = 30;
    private const int MaximumPolicyCount = 100;
    private const string SnapshotParent =
        "leaderboard_entries_snapshot";
    private static readonly Regex GenerationLeafPattern = new(
        "^(?<root>leaderboard_entries_snapshot_[a-z0-9_]+)_s(?<snapshot>[1-9][0-9]*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly NpgsqlDataSource _dataSource;
    private readonly SnapshotGenerationRetentionRepository _repository;
    private readonly IOptions<DatabaseMaintenanceOptions> _options;
    private readonly IOptions<ScraperOptions> _scraperOptions;
    private readonly ILogger<SnapshotGenerationRetentionPlanner> _log;

    public SnapshotGenerationRetentionPlanner(
        NpgsqlDataSource dataSource,
        SnapshotGenerationRetentionRepository repository,
        IOptions<DatabaseMaintenanceOptions> options,
        IOptions<ScraperOptions> scraperOptions,
        ILogger<SnapshotGenerationRetentionPlanner> log)
    {
        _dataSource = dataSource;
        _repository = repository;
        _options = options;
        _scraperOptions = scraperOptions;
        _log = log;
    }

    public bool IsEnabled =>
        _options.Value
            .SnapshotGenerationRetentionPlannerEnabled;

    public async Task<SnapshotGenerationRetentionPlanResult> PlanAsync(
        SnapshotGenerationRetentionPlanRequest request,
        CancellationToken ct = default)
    {
        var configured = _options.Value;
        if (!configured.SnapshotGenerationRetentionPlannerEnabled)
            return SnapshotGenerationRetentionPlanResult.Disabled();

        if (request.TriggerScrapeId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "A retention safe point requires a positive trigger scrape ID.");
        }

        if (request.TriggerPublicationId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "A retention safe point requires a positive trigger publication ID.");
        }

        if (!string.Equals(
                request.SafePointKind,
                SnapshotGenerationRetentionContract
                    .PostPublicationSafePoint,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Unsupported snapshot-generation retention safe point.",
                nameof(request));
        }

        var policy = BuildEffectivePolicy(configured);
        try
        {
            return await PlanCoreAsync(request, policy, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Snapshot-generation retention planning failed for publication {PublicationId} and scrape {ScrapeId}. The accepted publication remains current.",
                request.TriggerPublicationId,
                request.TriggerScrapeId);

            var failure = await TryRecordFailureAsync(
                request,
                policy,
                ex,
                CancellationToken.None);
            return failure ?? new SnapshotGenerationRetentionPlanResult(
                SnapshotGenerationRetentionPlanDisposition.Failed,
                null,
                "snapshot-generation retention planning failed and durable failure recording was unavailable",
                null,
                0,
                0,
                0,
                0,
                0);
        }
    }

    private async Task<SnapshotGenerationRetentionPlanResult>
        PlanCoreAsync(
            SnapshotGenerationRetentionPlanRequest request,
            SnapshotGenerationRetentionPolicy policy,
            CancellationToken ct)
    {
                await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
                if (!await TryAcquireSessionLockAsync(
                        connection,
                        RegistrationMutationGate.AdvisoryLockKey,
                        shared: false,
                        ct))
                {
                    return new SnapshotGenerationRetentionPlanResult(
                        SnapshotGenerationRetentionPlanDisposition.Busy,
                        null,
                        "registration mutation work currently holds the shared advisory session lock",
                        null,
                        0,
                        0,
                        0,
                        0,
                        0);
                }
                await using var registrationLock =
                    new SessionAdvisoryLockLease(
                        connection,
                        RegistrationMutationGate.AdvisoryLockKey,
                        shared: false);

                if (!await TryAcquireSessionLockAsync(
                        connection,
                        PublicationGenerationSchema.AdvisoryLockKey,
                        shared: true,
                        ct))
                {
                    return new SnapshotGenerationRetentionPlanResult(
                        SnapshotGenerationRetentionPlanDisposition.Busy,
                        null,
                        "publication allocation or commit currently holds the advisory session lock",
                        null,
                        0,
                        0,
                0,
                0,
                0);
        }
        await using var publicationLock =
            new SessionAdvisoryLockLease(
                connection,
                PublicationGenerationSchema.AdvisoryLockKey,
                shared: true);

        if (!await TryAcquireSessionLockAsync(
                connection,
                SnapshotGenerationRetentionContract
                    .PlannerAdvisoryLockKey,
                shared: false,
                ct))
        {
            return new SnapshotGenerationRetentionPlanResult(
                SnapshotGenerationRetentionPlanDisposition.Busy,
                null,
                "another snapshot-generation retention planner holds the advisory session lock",
                null,
                0,
                0,
                0,
                0,
                0);
        }
        await using var plannerLock =
            new SessionAdvisoryLockLease(
                connection,
                SnapshotGenerationRetentionContract
                    .PlannerAdvisoryLockKey,
                shared: false);

        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                ct);
        await ApplyTransactionTimeoutsAsync(
            connection,
            transaction,
            ct);

        var existing =
            await SnapshotGenerationRetentionRepository
                .GetCycleForSafePointAsync(
                    connection,
                    transaction,
                    request.SafePointKind,
                    request.TriggerPublicationId,
                    ct);
        if (existing is not null)
        {
            var existingResult =
                await BuildExistingResultAsync(
                    connection,
                    transaction,
                    existing,
                    ct);
            await transaction.CommitAsync(ct);
            return existingResult;
        }

        var publication =
            await LoadPublicationStateAsync(
                connection,
                transaction,
                request,
                ct);
        var retryableBlockers = publication.Blockers
            .Where(static blocker =>
                IsRetryableSafePointBlocker(
                    blocker.Code))
            .ToArray();
        if (retryableBlockers.Length > 0)
        {
            await transaction.RollbackAsync(ct);
            return new SnapshotGenerationRetentionPlanResult(
                SnapshotGenerationRetentionPlanDisposition.Deferred,
                null,
                "snapshot-generation retention safe point is not terminal yet: "
                + string.Join(
                    "; ",
                    retryableBlockers.Select(
                        static blocker =>
                            $"{blocker.Code}: {blocker.Detail}")),
                null,
                0,
                0,
                0,
                0,
                0);
        }

        var cycleId =
            await SnapshotGenerationRetentionRepository
                .InsertPlanningCycleAsync(
                    connection,
                    transaction,
                    request,
                    policy,
                    ct);
        var evidence = new EvidenceChain(
            connection,
            transaction,
            cycleId);

        await evidence.AppendAsync(
            jobId: null,
            phase: "planning",
            kind: "configuration",
            new
            {
                SnapshotGenerationRetentionContract.PlannerVersion,
                SnapshotGenerationRetentionContract.ConfigVersion,
                request.TriggerScrapeId,
                request.TriggerPublicationId,
                request.SafePointKind,
                request.SafePointAtUtc,
                Policy = policy,
                ConfiguredResumeScrapeId =
                    Math.Max(0, _scraperOptions.Value.ResumeScrapeId),
            },
            ct);

        var catalog =
            await LoadCatalogAsync(
                connection,
                transaction,
                ct);
        var defaultCounts =
            await LoadDefaultCountsAsync(
                connection,
                transaction,
                catalog,
                ct);
        var references =
            await LoadReferenceStateAsync(
                connection,
                transaction,
                publication,
                catalog.Leaves,
                ct);

        await evidence.AppendAsync(
            jobId: null,
            phase: "planning",
            kind: "safe_point",
            new
            {
                publication.PublishedScrapeId,
                publication.CurrentPublicationId,
                publication.PreviousPublicationId,
                publication.WorkingPublicationId,
                publication.PublicReadsFrozen,
                publication.CommitIntentStartedAtUtc,
                publication.CommitIntentHeartbeatAtUtc,
                publication.CommitIntentOwner,
                publication.ImprovementNotificationScrapeId,
                publication.ImprovementNotificationStatus,
                publication.ImprovementNotificationCompletedAtUtc,
                publication.ImprovementNotificationProjectionReady,
                publication.ImprovementNotificationProjectionScrapeId,
                publication.RegistrationDrain,
                publication.RunningScrapeIds,
                publication.Slots,
                Blockers = publication.Blockers,
            },
            ct);
        await evidence.AppendAsync(
            jobId: null,
            phase: "catalog",
            kind: "discovery",
            new
            {
                catalog.TopPartitionKey,
                Roots = catalog.Roots,
                Leaves = catalog.Leaves,
                DefaultCounts = defaultCounts
                    .OrderBy(static item => item.Key, StringComparer.Ordinal)
                    .Select(static item => new
                    {
                        RootRelation = item.Key,
                        RowCount = item.Value,
                    }),
                GlobalBlockers = catalog.GlobalBlockers,
                InstrumentBlockers = catalog.InstrumentBlockers
                    .OrderBy(static item => item.Key, StringComparer.Ordinal)
                    .Select(static item => new
                    {
                        Instrument = item.Key,
                        Blockers = item.Value,
                    }),
            },
            ct);
        await evidence.AppendAsync(
            jobId: null,
            phase: "protection",
            kind: "references",
            new
            {
                Protected = references.Protected
                    .OrderBy(static item => item.Key, StringComparer.Ordinal)
                    .Select(static item => new
                    {
                        Instrument = item.Key,
                        SnapshotIds = item.Value
                            .OrderBy(static protectedItem => protectedItem.Key)
                            .Select(static protectedItem => new
                            {
                                SnapshotId = protectedItem.Key,
                                Reasons = protectedItem.Value
                                    .OrderBy(
                                        static reason => reason,
                                        StringComparer.Ordinal),
                            }),
                    }),
                references.RunningScrapeIds,
                references.SuccessfulPublicationScrapeIds,
                references.UnreplayedWriterFailureScrapeIds,
                references.PublicationSourceMaps,
                references.ActiveStateRowCount,
                references.ActiveStateFingerprint,
                references.ProjectionRowCount,
                references.ProjectionFingerprint,
                references.CurrentFingerprintRowCount,
                references.CurrentFingerprint,
                GlobalBlockers = references.GlobalBlockers,
                Blockers = references.InstrumentBlockers
                    .OrderBy(static item => item.Key, StringComparer.Ordinal)
                    .Select(static item => new
                    {
                        Instrument = item.Key,
                        Blockers = item.Value,
                    }),
            },
            ct);

        var activePlaceholder =
            await LoadActivePlaceholderAsync(
                connection,
                transaction,
                ct);
        var outstandingIdentities =
            await LoadOutstandingJobIdentitiesAsync(
                connection,
                transaction,
                ct);
        var evaluations = EvaluateLeaves(
            request,
            policy,
            catalog,
            defaultCounts,
            publication,
            references);
        var eligible = evaluations
            .Where(static evaluation =>
                evaluation.Blockers.Count == 0)
            .OrderBy(static evaluation =>
                evaluation.Leaf.SnapshotId)
            .ThenBy(evaluation =>
                RotateFairness(
                    evaluation.Leaf.Instrument.FairnessOrder,
                    request.TriggerPublicationId))
            .ThenBy(static evaluation =>
                evaluation.Leaf.ChildRelation,
                StringComparer.Ordinal)
            .ToArray();

        var selected = activePlaceholder is null
            ? eligible
                .Where(evaluation =>
                    !outstandingIdentities.Contains(
                        evaluation.Leaf.Identity))
                .Take(policy.MaxPlannedChildrenPerCycle)
                .Select(static evaluation =>
                    evaluation.Leaf.Identity)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var drafts = new List<SnapshotGenerationRetentionJobDraft>(
            evaluations.Count);
        foreach (var evaluation in evaluations
                     .OrderBy(static item => item.Leaf.SnapshotId)
                     .ThenBy(item => RotateFairness(
                         item.Leaf.Instrument.FairnessOrder,
                         request.TriggerPublicationId))
                     .ThenBy(static item =>
                         item.Leaf.ChildRelation,
                         StringComparer.Ordinal))
        {
            var blockers = evaluation.Blockers.ToList();
            string status;
            if (blockers.Count > 0)
            {
                status = SnapshotGenerationRetentionJobStatus.Blocked;
            }
            else if (policy.ReportOnly)
            {
                status = SnapshotGenerationRetentionJobStatus.Observed;
            }
            else if (outstandingIdentities.Contains(
                         evaluation.Leaf.Identity))
            {
                status = SnapshotGenerationRetentionJobStatus.Deferred;
                blockers.Add(new RetentionBlocker(
                    "existing_job_intent",
                    "An earlier nonterminal retention job already owns this exact physical child identity."));
            }
            else if (selected.Contains(evaluation.Leaf.Identity))
            {
                status = SnapshotGenerationRetentionJobStatus.Planned;
            }
            else
            {
                status = SnapshotGenerationRetentionJobStatus.Deferred;
                blockers.Add(new RetentionBlocker(
                    activePlaceholder is null
                        ? "cycle_plan_limit"
                    : activePlaceholder.Status ==
                        SnapshotGenerationRetentionJobStatus
                            .SafetyFailed
                        ? "global_safety_failure"
                        : "global_job_placeholder",
                activePlaceholder is null
                    ? "The child is otherwise eligible but exceeds the bounded per-cycle plan limit."
                    : activePlaceholder.Status ==
                        SnapshotGenerationRetentionJobStatus
                            .SafetyFailed
                        ? activePlaceholder.JobId.HasValue
                            ? $"Job {activePlaceholder.JobId} records a hard retention safety failure."
                            : $"Cycle {activePlaceholder.CycleId} records a hard retention safety failure."
                        : $"Job {activePlaceholder.JobId} already owns the global active destructive child placeholder."));
            }

            var protectedReasons = references.Protected
                .GetValueOrDefault(evaluation.Leaf.Instrument.Instrument)?
                .GetValueOrDefault(evaluation.Leaf.SnapshotId)
                ?? [];
            drafts.Add(new SnapshotGenerationRetentionJobDraft(
                policy.ReportOnly,
                SnapshotGenerationRetentionOperationKind.DropWholeChild,
                evaluation.Leaf.Instrument.Instrument,
                evaluation.Leaf.Instrument.RootRelation,
                evaluation.Leaf.ChildRelation,
                evaluation.Leaf.SnapshotId,
                evaluation.Leaf.ChildOid,
                evaluation.Leaf.ChildRelfilenode,
                evaluation.Leaf.PartitionBound,
                evaluation.Leaf.TablespaceName,
                evaluation.Leaf.RowEstimate,
                evaluation.Leaf.TotalBytes,
                TierZeroCanonicalJson.SerializeToString(new
                {
                    SnapshotId = evaluation.Leaf.SnapshotId,
                    Reasons = protectedReasons
                        .OrderBy(
                            static reason => reason,
                            StringComparer.Ordinal),
                }),
                TierZeroCanonicalJson.SerializeToString(new
                {
                    publication.CurrentPublicationId,
                    publication.PreviousPublicationId,
                    publication.WorkingPublicationId,
                    CurrentPublicationScrapeId =
                        publication.GetScrapeId("current"),
                    PreviousPublicationScrapeId =
                        publication.GetScrapeId("previous"),
                    WorkingPublicationScrapeId =
                        publication.GetScrapeId("working"),
                    PublicationSourceMaps =
                        references.PublicationSourceMaps,
                    references.ActiveStateRowCount,
                    references.ActiveStateFingerprint,
                    references.ProjectionRowCount,
                    references.ProjectionFingerprint,
                    references.CurrentFingerprintRowCount,
                    references.CurrentFingerprint,
                    LaterSuccessfulPublications =
                        evaluation.LaterSuccessfulPublications,
                    DefaultChildRows =
                        defaultCounts.GetValueOrDefault(
                            evaluation.Leaf.Instrument.RootRelation),
                }),
                blockers
                    .Select(static blocker => blocker.Code)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(
                        static code => code,
                        StringComparer.Ordinal)
                    .ToArray(),
                TierZeroCanonicalJson.SerializeToString(
                    blockers
                        .OrderBy(
                            static blocker => blocker.Code,
                            StringComparer.Ordinal)
                        .ThenBy(
                            static blocker => blocker.Detail,
                            StringComparer.Ordinal)
                        .ToArray()),
                status));
        }

        var planDigest = TierZeroCanonicalJson.Sha256Hex(
            TierZeroCanonicalJson.Serialize(new
            {
                SnapshotGenerationRetentionContract.PlannerVersion,
                SnapshotGenerationRetentionContract.ConfigVersion,
                request.TriggerScrapeId,
                request.TriggerPublicationId,
                request.SafePointKind,
                Policy = policy,
                Catalog = new
                {
                    catalog.TopPartitionKey,
                    GlobalBlockers = catalog.GlobalBlockers,
                    RootBlockers = catalog.Roots
                        .OrderBy(
                            static root => root.Instrument,
                            StringComparer.Ordinal)
                        .Select(static root => new
                        {
                            root.Instrument,
                            root.RootRelation,
                            root.RootPartitionKey,
                            root.Blockers,
                        }),
                    DefaultCounts = defaultCounts
                        .OrderBy(
                            static item => item.Key,
                            StringComparer.Ordinal),
                },
                Publication = new
                {
                    publication.PublishedScrapeId,
                    publication.CurrentPublicationId,
                    publication.PreviousPublicationId,
                    publication.WorkingPublicationId,
                    publication.PublicReadsFrozen,
                    publication.CommitIntentStartedAtUtc,
                    publication.CommitIntentHeartbeatAtUtc,
                    publication.CommitIntentOwner,
                    publication.ImprovementNotificationScrapeId,
                    publication.ImprovementNotificationStatus,
                    publication.ImprovementNotificationCompletedAtUtc,
                    publication.ImprovementNotificationProjectionReady,
                    publication.ImprovementNotificationProjectionScrapeId,
                    publication.RegistrationDrain,
                    publication.RunningScrapeIds,
                    publication.Blockers,
                },
                ReferenceGlobalBlockers =
                    references.GlobalBlockers,
                ReferenceFingerprints = new
                {
                    references.PublicationSourceMaps,
                    references.ActiveStateRowCount,
                    references.ActiveStateFingerprint,
                    references.ProjectionRowCount,
                    references.ProjectionFingerprint,
                    references.CurrentFingerprintRowCount,
                    references.CurrentFingerprint,
                },
                Jobs = drafts.Select(static draft => new
                {
                    draft.ReportOnly,
                    draft.OperationKind,
                    draft.Instrument,
                    draft.RootRelation,
                    draft.ChildRelation,
                    draft.SnapshotId,
                    draft.ChildOid,
                    draft.ChildRelfilenode,
                    draft.PartitionBound,
                    draft.TablespaceName,
                    draft.RowEstimate,
                    draft.TotalBytes,
                    draft.ProtectedEvidenceJson,
                    draft.ReferenceEvidenceJson,
                    draft.BlockerCodes,
                    draft.BlockerDetailsJson,
                    draft.Status,
                }),
            }));

        foreach (var draft in drafts)
        {
            var jobId =
                await SnapshotGenerationRetentionRepository
                    .InsertJobAsync(
                        connection,
                        transaction,
                        cycleId,
                        draft,
                        ct);
            await evidence.AppendAsync(
                jobId,
                "policy",
                "job",
                new
                {
                    draft.ReportOnly,
                    draft.OperationKind,
                    draft.Instrument,
                    draft.RootRelation,
                    draft.ChildRelation,
                    draft.SnapshotId,
                    draft.ChildOid,
                    draft.ChildRelfilenode,
                    draft.PartitionBound,
                    draft.TablespaceName,
                    draft.RowEstimate,
                    draft.TotalBytes,
                    draft.BlockerCodes,
                    BlockerDetails =
                        JsonSerializer.Deserialize<JsonElement>(
                            draft.BlockerDetailsJson),
                    draft.Status,
                },
                ct);
        }

        var candidateCount = eligible.Length;
        var blockedCount = evaluations.Count - candidateCount;
        var candidateBytes = eligible.Sum(
            static evaluation => evaluation.Leaf.TotalBytes);
        var blockedBytes = evaluations
            .Where(static evaluation =>
                evaluation.Blockers.Count > 0)
            .Sum(static evaluation => evaluation.Leaf.TotalBytes);
        var plannedCount = drafts.Count(static draft =>
            draft.Status ==
            SnapshotGenerationRetentionJobStatus.Planned);
        var cycleStatus = policy.ReportOnly
            && candidateCount > 0
            ? SnapshotGenerationRetentionCycleStatus.Observed
            : plannedCount > 0
                ? SnapshotGenerationRetentionCycleStatus.Planned
            : candidateCount > 0
                ? SnapshotGenerationRetentionCycleStatus.Deferred
                : SnapshotGenerationRetentionCycleStatus.Blocked;

        await evidence.AppendAsync(
            jobId: null,
            phase: "plan",
            kind: "summary",
            new
            {
                PlanDigest = planDigest,
                Status = cycleStatus,
                CandidateCount = candidateCount,
                PlannedCount = plannedCount,
                BlockedCount = blockedCount,
                CandidateBytes = candidateBytes,
                BlockedBytes = blockedBytes,
                ActivePlaceholder = activePlaceholder,
                ReportOnly = policy.ReportOnly,
            },
            ct);
        await SnapshotGenerationRetentionRepository.CompleteCycleAsync(
            connection,
            transaction,
            cycleId,
            cycleStatus,
            planDigest,
            candidateCount,
            blockedCount,
            candidateBytes,
            blockedBytes,
            errorMessage: null,
            ct);
        await transaction.CommitAsync(ct);

        var disposition = cycleStatus switch
        {
            SnapshotGenerationRetentionCycleStatus.Observed =>
                SnapshotGenerationRetentionPlanDisposition.Observed,
            SnapshotGenerationRetentionCycleStatus.Planned =>
                SnapshotGenerationRetentionPlanDisposition.Planned,
            SnapshotGenerationRetentionCycleStatus.Deferred =>
                SnapshotGenerationRetentionPlanDisposition.Deferred,
            _ => SnapshotGenerationRetentionPlanDisposition.Blocked,
        };
        return new SnapshotGenerationRetentionPlanResult(
            disposition,
            cycleId,
            BuildResultReason(
                disposition,
                candidateCount,
                plannedCount,
                blockedCount),
            planDigest,
            candidateCount,
            plannedCount,
            blockedCount,
            candidateBytes,
            blockedBytes);
    }

    private static SnapshotGenerationRetentionPolicy BuildEffectivePolicy(
        DatabaseMaintenanceOptions options) =>
        new(
            options.SnapshotGenerationRetentionReportOnly,
            Math.Clamp(
                options
                    .SnapshotGenerationRetentionNewestGenerationsToKeep,
                0,
                MaximumPolicyCount),
            Math.Clamp(
                options
                    .SnapshotGenerationRetentionMinimumLaterSuccessfulPublications,
                0,
                MaximumPolicyCount),
            Math.Clamp(
                options
                    .SnapshotGenerationRetentionMaxPlannedChildrenPerCycle,
                1,
                MaximumPolicyCount),
            options
                .SnapshotGenerationRetentionBlockUnreplayedWriterFailures);

    private static async Task ApplyTransactionTimeoutsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT set_config('lock_timeout', '500ms', true);
            SELECT set_config('statement_timeout', '30s', true);
            SELECT set_config(
                'idle_in_transaction_session_timeout',
                '35s',
                true);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> TryAcquireSessionLockAsync(
        NpgsqlConnection connection,
        long lockKey,
        bool shared,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = shared
            ? "SELECT pg_try_advisory_lock_shared(@lockKey)"
            : "SELECT pg_try_advisory_lock(@lockKey)";
        command.Parameters.AddWithValue("lockKey", lockKey);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private sealed class SessionAdvisoryLockLease
        : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly long _lockKey;
        private readonly bool _shared;
        private int _released;

        public SessionAdvisoryLockLease(
            NpgsqlConnection connection,
            long lockKey,
            bool shared)
        {
            _connection = connection;
            _lockKey = lockKey;
            _shared = shared;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(
                    ref _released,
                    1) != 0
                || _connection.State
                    != ConnectionState.Open)
            {
                return;
            }

            await using var command =
                _connection.CreateCommand();
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText = _shared
                ? "SELECT pg_advisory_unlock_shared(@lockKey)"
                : "SELECT pg_advisory_unlock(@lockKey)";
            command.Parameters.AddWithValue(
                "lockKey",
                _lockKey);
            await command.ExecuteScalarAsync(
                CancellationToken.None);
        }
    }

    private static async Task<PublicationState> LoadPublicationStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SnapshotGenerationRetentionPlanRequest request,
        CancellationToken ct)
    {
        long? publishedScrapeId = null;
        long? currentPublicationId = null;
        long? previousPublicationId = null;
        long? workingPublicationId = null;
        var publicReadsFrozen = false;
        DateTime? commitIntentStartedAtUtc = null;
        DateTime? commitIntentHeartbeatAtUtc = null;
        string? commitIntentOwner = null;
        long? notificationScrapeId = null;
        string? notificationStatus = null;
        DateTime? notificationCompletedAtUtc = null;
        var notificationProjectionReady = false;
        long? notificationProjectionScrapeId = null;
        var blockers = new List<RetentionBlocker>();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText = """
                SELECT
                    published_scrape_id::BIGINT,
                    current_publication_id,
                    previous_publication_id,
                    working_publication_id,
                    public_reads_frozen,
                    publication_commit_intent_started_at,
                    publication_commit_intent_heartbeat_at,
                    publication_commit_intent_owner,
                    improvement_notifications_scrape_id::BIGINT,
                    improvement_notifications_status,
                    improvement_notifications_completed_at,
                    improvement_notifications_projection_ready,
                    improvement_notifications_projection_scrape_id::BIGINT
                FROM scrape_publication_state
                WHERE id = TRUE
                """;
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                blockers.Add(new RetentionBlocker(
                    "publication_state_missing",
                    "The scrape publication singleton is missing."));
            }
            else
            {
                publishedScrapeId =
                    reader.IsDBNull(0) ? null : reader.GetInt64(0);
                currentPublicationId =
                    reader.IsDBNull(1) ? null : reader.GetInt64(1);
                previousPublicationId =
                    reader.IsDBNull(2) ? null : reader.GetInt64(2);
                workingPublicationId =
                    reader.IsDBNull(3) ? null : reader.GetInt64(3);
                publicReadsFrozen = reader.GetBoolean(4);
                commitIntentStartedAtUtc =
                    reader.IsDBNull(5) ? null : reader.GetDateTime(5);
                commitIntentHeartbeatAtUtc =
                    reader.IsDBNull(6) ? null : reader.GetDateTime(6);
                commitIntentOwner =
                    reader.IsDBNull(7) ? null : reader.GetString(7);
                notificationScrapeId =
                    reader.IsDBNull(8) ? null : reader.GetInt64(8);
                notificationStatus =
                    reader.IsDBNull(9) ? null : reader.GetString(9);
                notificationCompletedAtUtc =
                    reader.IsDBNull(10) ? null : reader.GetDateTime(10);
                notificationProjectionReady = reader.GetBoolean(11);
                notificationProjectionScrapeId =
                    reader.IsDBNull(12) ? null : reader.GetInt64(12);
            }
        }

        if (publishedScrapeId != request.TriggerScrapeId
            || currentPublicationId != request.TriggerPublicationId)
        {
            blockers.Add(new RetentionBlocker(
                "safe_point_mismatch",
                $"The requested safe point scrape/publication {request.TriggerScrapeId}/{request.TriggerPublicationId} does not match the current {publishedScrapeId?.ToString() ?? "null"}/{currentPublicationId?.ToString() ?? "null"}."));
        }
        if (publicReadsFrozen)
        {
            blockers.Add(new RetentionBlocker(
                "public_reads_frozen",
                "Public reads are frozen at the requested retention safe point."));
        }
        if (workingPublicationId.HasValue)
        {
            blockers.Add(new RetentionBlocker(
                "working_publication_present",
                $"Working publication {workingPublicationId.Value} must be resolved before retention planning."));
        }
        if (commitIntentStartedAtUtc.HasValue
            || commitIntentHeartbeatAtUtc.HasValue
            || !string.IsNullOrWhiteSpace(commitIntentOwner))
        {
            blockers.Add(new RetentionBlocker(
                "publication_commit_intent_present",
                "A publication commit intent remains active or unreconciled."));
        }

        var notificationsCompleted =
            string.Equals(
                notificationStatus,
                "completed",
                StringComparison.Ordinal)
            && notificationScrapeId == publishedScrapeId
            && notificationCompletedAtUtc.HasValue
            && notificationProjectionReady
            && notificationProjectionScrapeId == publishedScrapeId;
        var notificationsDisabled =
            string.Equals(
                notificationStatus,
                "disabled",
                StringComparison.Ordinal)
            && !notificationScrapeId.HasValue
            && !notificationCompletedAtUtc.HasValue
            && !notificationProjectionReady
            && !notificationProjectionScrapeId.HasValue;
        if (!notificationsCompleted && !notificationsDisabled)
        {
            blockers.Add(new RetentionBlocker(
                "improvement_notifications_incomplete",
                $"Improvement notification state is not completed for published scrape {publishedScrapeId?.ToString() ?? "null"} or correctly disabled."));
        }

        var registrationDrain =
            await RegistrationDrainStateReader.LoadAsync(
                connection,
                transaction,
                CommandTimeoutSeconds,
                ct);
        if (!registrationDrain.IsComplete)
        {
            blockers.Add(new RetentionBlocker(
                "registration_drain_incomplete",
                $"Registration drain has {registrationDrain.RemainingBackfills} backfill and {registrationDrain.RemainingHistory} history account(s) remaining."));
        }

        var runningScrapeIds = new List<long>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText = """
                SELECT id::BIGINT
                FROM scrape_log
                WHERE status = 'running'
                ORDER BY id
                """;
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                runningScrapeIds.Add(reader.GetInt64(0));
        }
        if (runningScrapeIds.Count > 0)
        {
            blockers.Add(new RetentionBlocker(
                "running_scrape",
                $"Running scrape(s) {string.Join(", ", runningScrapeIds)} prevent terminal retention planning."));
        }

        var slotIds = new[]
            {
                currentPublicationId,
                previousPublicationId,
                workingPublicationId,
            }
            .Where(static id => id.HasValue)
            .Select(static id => id!.Value)
            .Distinct()
            .ToArray();
        var generationById =
            new Dictionary<long, PublicationSlotGeneration>();
        if (slotIds.Length > 0)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText = """
                SELECT publication_id,
                       scrape_id,
                       status,
                       previous_publication_id
                FROM publication_generations
                WHERE publication_id = ANY(@publicationIds)
                """;
            command.Parameters.AddWithValue(
                "publicationIds",
                slotIds);
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var publicationId = reader.GetInt64(0);
                generationById[publicationId] =
                    new PublicationSlotGeneration(
                        publicationId,
                        reader.IsDBNull(1)
                            ? null
                            : reader.GetInt64(1),
                        reader.GetString(2),
                        reader.IsDBNull(3)
                            ? null
                            : reader.GetInt64(3));
            }
        }

        var distinctPointerCount = new[]
            {
                currentPublicationId,
                previousPublicationId,
                workingPublicationId,
            }
            .Where(static id => id.HasValue)
            .Select(static id => id!.Value)
            .Distinct()
            .Count();
        var nonNullPointerCount = new[]
            {
                currentPublicationId,
                previousPublicationId,
                workingPublicationId,
            }
            .Count(static id => id.HasValue);
        if (distinctPointerCount != nonNullPointerCount)
        {
            blockers.Add(new RetentionBlocker(
                "publication_pointer_duplicate",
                "Current, previous, and working publication pointers must be distinct."));
        }

        var slots = new List<PublicationSlot>();
        AddSlot(
            "current",
            currentPublicationId,
            [PublicationGenerationStatus.Current]);
        AddSlot(
            "previous",
            previousPublicationId,
            [PublicationGenerationStatus.Retained]);
        AddSlot(
            "working",
            workingPublicationId,
            [
                PublicationGenerationStatus.Building,
                PublicationGenerationStatus.Ready,
            ]);

        var currentSlot = slots.Single(static slot =>
            slot.Name == "current");
        if (publishedScrapeId.HasValue
            && currentSlot.Resolved
            && currentSlot.ScrapeId != publishedScrapeId)
        {
            blockers.Add(new RetentionBlocker(
                "current_generation_scrape_mismatch",
                $"Current publication {currentPublicationId} belongs to scrape {currentSlot.ScrapeId}, not published scrape {publishedScrapeId}."));
        }
        if (currentSlot.Resolved
            && currentPublicationId.HasValue
            && generationById.TryGetValue(
                currentPublicationId.Value,
                out var currentGeneration)
            && currentGeneration.PreviousPublicationId
                != previousPublicationId)
        {
            blockers.Add(new RetentionBlocker(
                "publication_predecessor_mismatch",
                $"Current publication {currentPublicationId} records previous publication {currentGeneration.PreviousPublicationId?.ToString() ?? "null"}, not pointer {previousPublicationId?.ToString() ?? "null"}."));
        }

        return new PublicationState(
            publishedScrapeId,
            currentPublicationId,
            previousPublicationId,
            workingPublicationId,
            publicReadsFrozen,
            commitIntentStartedAtUtc,
            commitIntentHeartbeatAtUtc,
            commitIntentOwner,
            notificationScrapeId,
            notificationStatus,
            notificationCompletedAtUtc,
            notificationProjectionReady,
            notificationProjectionScrapeId,
            registrationDrain,
            runningScrapeIds,
            slots,
            blockers);

        void AddSlot(
            string name,
            long? publicationId,
            IReadOnlyCollection<string> allowedStatuses)
        {
            if (!publicationId.HasValue)
            {
                slots.Add(new PublicationSlot(
                    name,
                    null,
                    null,
                    null,
                    Required: name == "current",
                    Resolved: name != "current"));
                if (name == "current")
                {
                    blockers.Add(new RetentionBlocker(
                        "publication_unresolved",
                        "The current publication pointer is null."));
                }
                return;
            }

            if (!generationById.TryGetValue(
                    publicationId.Value,
                    out var generation)
                || !generation.ScrapeId.HasValue
                || generation.ScrapeId.Value <= 0
                || !allowedStatuses.Contains(
                    generation.Status,
                    StringComparer.Ordinal))
            {
                slots.Add(new PublicationSlot(
                    name,
                    publicationId,
                    generation?.ScrapeId,
                    generation?.Status,
                    Required: true,
                    Resolved: false));
                blockers.Add(new RetentionBlocker(
                    "publication_unresolved",
                    $"The {name} publication {publicationId} is missing a positive scrape identity or has an unexpected status."));
                return;
            }

            slots.Add(new PublicationSlot(
                name,
                publicationId,
                generation.ScrapeId,
                generation.Status,
                Required: true,
                Resolved: true));
        }
    }

    private static async Task<CatalogState> LoadCatalogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        var rootNames =
            SnapshotGenerationRetentionContract.Instruments
                .Select(static instrument => instrument.RootRelation)
                .ToArray();
        var relations = new List<CatalogRelation>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText = """
                SELECT
                    relation.oid::BIGINT,
                    relation.relname,
                    relation.relkind::TEXT,
                    COALESCE(
                        pg_get_partkeydef(
                            partitioned.partrelid),
                        ''),
                    relation.relfilenode::BIGINT,
                    COALESCE(
                        pg_get_expr(
                            relation.relpartbound,
                            relation.oid,
                            TRUE),
                        ''),
                    COALESCE(
                        tablespace.spcname,
                        database_tablespace.spcname),
                    parent.oid::BIGINT,
                    parent.relname,
                    GREATEST(
                        COALESCE(relation.reltuples, 0),
                        0)::DOUBLE PRECISION,
                    CASE
                        WHEN relation.relkind IN ('r', 'm')
                            THEN pg_total_relation_size(
                                relation.oid)::BIGINT
                        ELSE 0::BIGINT
                    END
                FROM pg_class relation
                JOIN pg_namespace namespace
                  ON namespace.oid = relation.relnamespace
                LEFT JOIN pg_inherits inheritance
                  ON inheritance.inhrelid = relation.oid
                LEFT JOIN pg_class parent
                  ON parent.oid = inheritance.inhparent
                LEFT JOIN pg_partitioned_table partitioned
                  ON partitioned.partrelid = relation.oid
                LEFT JOIN pg_tablespace tablespace
                  ON tablespace.oid = relation.reltablespace
                CROSS JOIN LATERAL (
                    SELECT default_tablespace.spcname
                    FROM pg_database database
                    JOIN pg_tablespace default_tablespace
                      ON default_tablespace.oid =
                            database.dattablespace
                    WHERE database.datname = current_database()
                ) database_tablespace
                WHERE namespace.nspname = 'public'
                  AND (
                      relation.relname =
                            'leaderboard_entries_snapshot'
                      OR relation.relname = ANY(@rootNames)
                      OR parent.relname =
                            'leaderboard_entries_snapshot'
                      OR parent.relname = ANY(@rootNames)
                  )
                ORDER BY relation.relname
                """;
            command.Parameters.AddWithValue("rootNames", rootNames);
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                relations.Add(new CatalogRelation(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.IsDBNull(7)
                        ? null
                        : reader.GetInt64(7),
                    reader.IsDBNull(8)
                        ? null
                        : reader.GetString(8),
                    Math.Max(
                        0,
                        checked((long)Math.Ceiling(
                            reader.GetDouble(9)))),
                    reader.GetInt64(10)));
            }
        }

        var relationOids = relations
            .Select(static relation => relation.Oid)
            .Distinct()
            .ToArray();
        var indexes = new List<CatalogIndex>();
        if (relationOids.Length > 0)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText = """
                SELECT
                    table_relation.oid::BIGINT,
                    table_relation.relname,
                    index_relation.oid::BIGINT,
                    index_relation.relname,
                    index_relation.relkind::TEXT,
                    index_row.indisvalid,
                    index_row.indisready,
                    COALESCE(
                        tablespace.spcname,
                        database_tablespace.spcname),
                    parent_index.oid::BIGINT,
                    parent_index.relname,
                    index_row.indisprimary,
                    index_row.indisunique,
                    access_method.amname,
                    index_row.indexprs IS NOT NULL,
                    index_row.indpred IS NOT NULL,
                    index_row.indnatts =
                        index_row.indnkeyatts,
                    (
                        SELECT string_agg(
                            pg_get_indexdef(
                                index_row.indexrelid,
                                key_position,
                                TRUE),
                            ', ' ORDER BY key_position)
                        FROM generate_series(
                            1,
                            index_row.indnkeyatts)
                            key_position
                    ),
                    index_row.indoption::TEXT,
                    index_row.indclass::TEXT,
                    index_row.indcollation::TEXT,
                    pg_get_indexdef(index_row.indexrelid)
                FROM pg_index index_row
                JOIN pg_class table_relation
                  ON table_relation.oid =
                        index_row.indrelid
                JOIN pg_class index_relation
                  ON index_relation.oid =
                        index_row.indexrelid
                JOIN pg_am access_method
                  ON access_method.oid =
                        index_relation.relam
                LEFT JOIN pg_inherits inheritance
                  ON inheritance.inhrelid =
                        index_relation.oid
                LEFT JOIN pg_class parent_index
                  ON parent_index.oid =
                        inheritance.inhparent
                LEFT JOIN pg_tablespace tablespace
                  ON tablespace.oid =
                        index_relation.reltablespace
                CROSS JOIN LATERAL (
                    SELECT default_tablespace.spcname
                    FROM pg_database database
                    JOIN pg_tablespace default_tablespace
                      ON default_tablespace.oid =
                            database.dattablespace
                    WHERE database.datname = current_database()
                ) database_tablespace
                WHERE table_relation.oid::BIGINT =
                        ANY(@relationOids)
                ORDER BY table_relation.relname,
                         index_relation.relname
                """;
            command.Parameters.AddWithValue(
                "relationOids",
                relationOids);
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                indexes.Add(new CatalogIndex(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetBoolean(5),
                    reader.GetBoolean(6),
                    reader.GetString(7),
                    reader.IsDBNull(8)
                        ? null
                        : reader.GetInt64(8),
                    reader.IsDBNull(9)
                        ? null
                        : reader.GetString(9),
                    reader.GetBoolean(10),
                    reader.GetBoolean(11),
                    reader.GetString(12),
                    reader.GetBoolean(13),
                    reader.GetBoolean(14),
                    reader.GetBoolean(15),
                    reader.GetString(16),
                    reader.GetString(17),
                    reader.GetString(18),
                    reader.GetString(19),
                    reader.GetString(20)));
            }
        }

        return BuildCatalogState(relations, indexes);
    }

    internal static CatalogState BuildCatalogState(
        IReadOnlyList<CatalogRelation> relations,
        IReadOnlyList<CatalogIndex> indexes)
    {
        var globalBlockers = new List<RetentionBlocker>();
        var instrumentBlockers =
            SnapshotGenerationRetentionContract.Instruments
                .ToDictionary(
                    static instrument => instrument.Instrument,
                    static _ => new List<RetentionBlocker>(),
                    StringComparer.Ordinal);
        var roots = new List<RetentionRootEvidence>();
        var leaves = new List<GenerationLeaf>();
        var byName = relations.ToDictionary(
            static relation => relation.RelationName,
            StringComparer.Ordinal);
        var top = byName.GetValueOrDefault(SnapshotParent);
        if (top is null
            || top.Relkind != "p"
            || top.ParentOid.HasValue)
        {
            globalBlockers.Add(new RetentionBlocker(
                "snapshot_parent_shape_invalid",
                "The top snapshot relation is missing or is not an unattached partitioned table."));
        }
        else if (!string.Equals(
                     top.PartitionKeyDefinition,
                     "LIST (instrument)",
                     StringComparison.Ordinal))
        {
            globalBlockers.Add(new RetentionBlocker(
                "snapshot_parent_partition_key_invalid",
                $"The top snapshot relation uses partition key {top.PartitionKeyDefinition}, not exact LIST (instrument)."));
        }
        else if (!IsPgDefault(top.TablespaceName))
        {
            globalBlockers.Add(new RetentionBlocker(
                "non_pg_default",
                "The top snapshot relation is not in pg_default."));
        }

        var directTopChildren = relations
            .Where(static relation =>
                relation.ParentRelationName == SnapshotParent)
            .ToArray();
        var expectedRootNames =
            SnapshotGenerationRetentionContract.Instruments
                .Select(static instrument => instrument.RootRelation)
                .ToHashSet(StringComparer.Ordinal);
        foreach (var unexpected in directTopChildren.Where(
                     child => !expectedRootNames.Contains(
                         child.RelationName)))
        {
            globalBlockers.Add(new RetentionBlocker(
                "unexpected_instrument_root",
                $"Unexpected direct snapshot root {unexpected.RelationName}."));
        }

        var topIndexes = top is null
            ? []
            : indexes.Where(index =>
                    index.TableOid == top.Oid)
                .ToArray();
        if (!HasExpectedTopIndexes(topIndexes))
        {
            globalBlockers.Add(new RetentionBlocker(
                "snapshot_parent_index_shape_invalid",
                "The top snapshot relation does not own exactly the expected valid pg_default partitioned indexes."));
        }

        foreach (var instrument in
                 SnapshotGenerationRetentionContract.Instruments)
        {
            var blockers = instrumentBlockers[instrument.Instrument];
            var root = byName.GetValueOrDefault(
                instrument.RootRelation);
            var expectedRootBound =
                $"FOR VALUES IN ('{instrument.Instrument}')";
            if (root is null
                || root.ParentRelationName != SnapshotParent
                || root.Relkind != "p"
                || root.PartitionBound != expectedRootBound)
            {
                blockers.Add(new RetentionBlocker(
                    "root_shape_invalid",
                    $"Root {instrument.RootRelation} is missing, detached, not partitioned, or has an unexpected bound."));
            }
            else if (!string.Equals(
                         root.PartitionKeyDefinition,
                         "LIST (snapshot_id)",
                         StringComparison.Ordinal))
            {
                blockers.Add(new RetentionBlocker(
                    "root_partition_key_invalid",
                    $"Root {instrument.RootRelation} uses partition key {root.PartitionKeyDefinition}, not exact LIST (snapshot_id)."));
            }
            else if (!IsPgDefault(root.TablespaceName))
            {
                blockers.Add(new RetentionBlocker(
                    "non_pg_default",
                    $"Root {instrument.RootRelation} is not in pg_default."));
            }

            var children = relations
                .Where(relation =>
                    relation.ParentRelationName ==
                    instrument.RootRelation)
                .OrderBy(
                    static relation => relation.RelationName,
                    StringComparer.Ordinal)
                .ToArray();
            var defaultChild = children.SingleOrDefault(
                static child =>
                    child.PartitionBound == "DEFAULT");
            if (defaultChild is null
                || defaultChild.RelationName !=
                    instrument.DefaultRelation
                || defaultChild.Relkind != "r")
            {
                blockers.Add(new RetentionBlocker(
                    "default_shape_invalid",
                    $"Root {instrument.RootRelation} does not own exactly the expected regular default child."));
            }
            else if (!IsPgDefault(defaultChild.TablespaceName))
            {
                blockers.Add(new RetentionBlocker(
                    "non_pg_default",
                    $"Default child {defaultChild.RelationName} is not in pg_default."));
            }

            var nonDefaultChildren = children
                .Where(static child =>
                    child.PartitionBound != "DEFAULT")
                .ToArray();
            foreach (var child in nonDefaultChildren)
            {
                var match = GenerationLeafPattern.Match(
                    child.RelationName);
                if (!match.Success
                    || match.Groups["root"].Value !=
                        instrument.RootRelation
                    || !long.TryParse(
                        match.Groups["snapshot"].Value,
                        out var snapshotId)
                    || snapshotId <= 0)
                {
                    blockers.Add(new RetentionBlocker(
                        "malformed_child_name",
                        $"Attached child {child.RelationName} is not an exact numeric generation leaf."));
                    continue;
                }

                var leafBlockers =
                    new List<RetentionBlocker>();
                if (child.Relkind != "r"
                    || child.Relfilenode <= 0)
                {
                    blockers.Add(new RetentionBlocker(
                        "child_shape_invalid",
                        $"Child {child.RelationName} has an unexpected relation kind or nonphysical relfilenode and cannot be represented as a retention job."));
                    continue;
                }

                var expectedBound =
                    $"FOR VALUES IN ('{snapshotId}')";
                if (child.PartitionBound != expectedBound)
                {
                    leafBlockers.Add(new RetentionBlocker(
                        "child_shape_invalid",
                        $"Child {child.RelationName} has an unexpected partition bound."));
                }
                if (!IsPgDefault(child.TablespaceName))
                {
                    leafBlockers.Add(new RetentionBlocker(
                        "non_pg_default",
                        $"Child {child.RelationName} is not in pg_default."));
                }

                leaves.Add(new GenerationLeaf(
                    instrument,
                    child.RelationName,
                    snapshotId,
                    child.Oid,
                    child.Relfilenode,
                    child.PartitionBound,
                    child.TablespaceName,
                    child.RowEstimate,
                    child.TotalBytes,
                    leafBlockers));
            }

            var rootIndexes = root is null
                ? []
                : indexes.Where(index =>
                        index.TableOid == root.Oid)
                    .ToArray();
            if (!HasExactAttachedIndexes(
                    rootIndexes,
                    topIndexes,
                    expectedChildKind: "I"))
            {
                blockers.Add(new RetentionBlocker(
                    "root_index_shape_invalid",
                    $"Root {instrument.RootRelation} does not own the exact two valid attached pg_default partitioned indexes."));
            }

            foreach (var child in children)
            {
                var childIndexes = indexes
                    .Where(index =>
                        index.TableOid == child.Oid)
                    .ToArray();
                if (!HasExactAttachedIndexes(
                        childIndexes,
                        rootIndexes,
                        expectedChildKind: "i"))
                {
                    var blocker = new RetentionBlocker(
                        "child_index_shape_invalid",
                        $"Child {child.RelationName} does not own the exact valid attached pg_default indexes.");
                    if (child.PartitionBound == "DEFAULT")
                    {
                        blockers.Add(blocker);
                    }
                    else
                    {
                        var leaf = leaves.SingleOrDefault(
                            candidate =>
                                candidate.ChildOid == child.Oid);
                        leaf?.CatalogBlockers.Add(blocker);
                    }
                }
            }

            roots.Add(new RetentionRootEvidence(
                instrument.Instrument,
                instrument.RootRelation,
                root?.Oid,
                root?.Relkind,
                root?.PartitionKeyDefinition,
                root?.PartitionBound,
                root?.TablespaceName,
                defaultChild?.RelationName,
                defaultChild?.Oid,
                blockers.ToArray()));
        }

        return new CatalogState(
            top?.PartitionKeyDefinition,
            roots,
            leaves,
            globalBlockers,
            instrumentBlockers);
    }

    private static bool HasExactAttachedIndexes(
        IReadOnlyCollection<CatalogIndex> childIndexes,
        IReadOnlyCollection<CatalogIndex> parentIndexes,
        string expectedChildKind)
    {
        if (parentIndexes.Count != 2
            || childIndexes.Count != parentIndexes.Count)
        {
            return false;
        }

        var parentById = parentIndexes.ToDictionary(
            static index => index.IndexOid);
        return childIndexes.All(index =>
        {
            if (index.Relkind != expectedChildKind
                || !index.IsValid
                || !index.IsReady
                || !IsPgDefault(index.TablespaceName)
                || index.ParentIndexOid is not long parentId
                || !parentById.TryGetValue(
                    parentId,
                    out var parent))
            {
                return false;
            }

            return HasSameIndexDefinition(
                index,
                parent);
        });
    }

    private static bool HasExpectedTopIndexes(
        IReadOnlyCollection<CatalogIndex> indexes)
    {
        if (indexes.Count != 2)
            return false;

        var primary = indexes.SingleOrDefault(
            static index =>
                index.IndexName ==
                "leaderboard_entries_snapshot_pkey");
        var score = indexes.SingleOrDefault(
            static index =>
                index.IndexName ==
                "ix_les_snapshot_song_score");
        return primary is not null
            && score is not null
            && HasBaseIndexShape(primary, expectedPrimary: true)
            && HasBaseIndexShape(score, expectedPrimary: false)
            && primary.IndexDefinition ==
                "CREATE UNIQUE INDEX leaderboard_entries_snapshot_pkey ON ONLY public.leaderboard_entries_snapshot USING btree (snapshot_id, song_id, instrument, account_id)"
            && score.IndexDefinition ==
                "CREATE INDEX ix_les_snapshot_song_score ON ONLY public.leaderboard_entries_snapshot USING btree (snapshot_id, song_id, instrument, score DESC)";
    }

    private static bool HasBaseIndexShape(
        CatalogIndex index,
        bool expectedPrimary) =>
        index.Relkind == "I"
        && index.IsValid
        && index.IsReady
        && IsPgDefault(index.TablespaceName)
        && index.IsPrimary == expectedPrimary
        && index.IsUnique == expectedPrimary
        && index.AccessMethod == "btree"
        && !index.HasExpressions
        && !index.HasPredicate
        && index.HasNoIncludedColumns;

    private static bool HasSameIndexDefinition(
        CatalogIndex child,
        CatalogIndex parent) =>
        child.IsPrimary == parent.IsPrimary
        && child.IsUnique == parent.IsUnique
        && child.AccessMethod == parent.AccessMethod
        && child.HasExpressions == parent.HasExpressions
        && child.HasPredicate == parent.HasPredicate
        && child.HasNoIncludedColumns
            == parent.HasNoIncludedColumns
        && child.KeyDefinition == parent.KeyDefinition
        && child.IndexOptions == parent.IndexOptions
        && child.OperatorClasses == parent.OperatorClasses
        && child.Collations == parent.Collations;

    private static async Task<IReadOnlyDictionary<string, long>>
        LoadDefaultCountsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CatalogState catalog,
            CancellationToken ct)
    {
        var validDefaultRoots = catalog.Roots
            .Where(static root =>
                root.DefaultRelation is not null
                && root.DefaultOid.HasValue
                && root.Blockers.All(static blocker =>
                    blocker.Code != "default_shape_invalid"))
            .Select(static root => root.RootRelation)
            .ToHashSet(StringComparer.Ordinal);
        if (validDefaultRoots.Count !=
            SnapshotGenerationRetentionContract.Instruments.Count)
        {
            return new Dictionary<string, long>(
                StringComparer.Ordinal);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT root_relation, row_count
            FROM (
                SELECT
                    'leaderboard_entries_snapshot_solo_guitar'::TEXT
                        AS root_relation,
                    COUNT(*)::BIGINT AS row_count
                FROM ONLY
                    leaderboard_entries_snapshot_solo_guitar_default
                UNION ALL
                SELECT
                    'leaderboard_entries_snapshot_solo_bass',
                    COUNT(*)::BIGINT
                FROM ONLY
                    leaderboard_entries_snapshot_solo_bass_default
                UNION ALL
                SELECT
                    'leaderboard_entries_snapshot_solo_vocals',
                    COUNT(*)::BIGINT
                FROM ONLY
                    leaderboard_entries_snapshot_solo_vocals_default
                UNION ALL
                SELECT
                    'leaderboard_entries_snapshot_solo_drums',
                    COUNT(*)::BIGINT
                FROM ONLY
                    leaderboard_entries_snapshot_solo_drums_default
                UNION ALL
                SELECT
                    'leaderboard_entries_snapshot_pro_guitar',
                    COUNT(*)::BIGINT
                FROM ONLY
                    leaderboard_entries_snapshot_pro_guitar_default
                UNION ALL
                SELECT
                    'leaderboard_entries_snapshot_pro_bass',
                    COUNT(*)::BIGINT
                FROM ONLY
                    leaderboard_entries_snapshot_pro_bass_default
                UNION ALL
                SELECT
                    'leaderboard_entries_snapshot_pro_vocals',
                    COUNT(*)::BIGINT
                FROM ONLY
                    leaderboard_entries_snapshot_pro_vocals_default
                UNION ALL
                SELECT
                    'leaderboard_entries_snapshot_pro_cymbals',
                    COUNT(*)::BIGINT
                FROM ONLY
                    leaderboard_entries_snapshot_pro_cymbals_default
                UNION ALL
                SELECT
                    'leaderboard_entries_snapshot_pro_drums',
                    COUNT(*)::BIGINT
                FROM ONLY
                    leaderboard_entries_snapshot_pro_drums_default
            ) counts
            ORDER BY root_relation
            """;

        var counts = new Dictionary<string, long>(
            StringComparer.Ordinal);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            counts[reader.GetString(0)] = reader.GetInt64(1);
        return counts;
    }

    private static async Task<ReferenceState> LoadReferenceStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicationState publication,
        IReadOnlyList<GenerationLeaf> leaves,
        CancellationToken ct)
    {
        var protectedByInstrument =
            SnapshotGenerationRetentionContract.Instruments
                .ToDictionary(
                    static instrument => instrument.Instrument,
                    static _ =>
                        new Dictionary<long, HashSet<string>>(),
                    StringComparer.Ordinal);
        var blockersByInstrument =
            SnapshotGenerationRetentionContract.Instruments
                .ToDictionary(
                    static instrument => instrument.Instrument,
                    static _ => new List<RetentionBlocker>(),
                    StringComparer.Ordinal);
        var globalBlockers = new List<RetentionBlocker>();
        var instruments =
            SnapshotGenerationRetentionContract.Instruments
                .Select(static instrument => instrument.Instrument)
                .ToHashSet(StringComparer.Ordinal);
        var leafKeys = leaves
            .Select(static leaf =>
                (leaf.Instrument.Instrument, leaf.SnapshotId))
            .ToHashSet();

        var activeRows =
            await LoadActiveSnapshotRowsAsync(
                connection,
                transaction,
                ct);
        var projectionRows =
            await LoadProjectionRowsAsync(
                connection,
                transaction,
                ct);
        var currentFingerprintRows =
            await LoadCurrentFingerprintRowsAsync(
                connection,
                transaction,
                ct);
        var activeByKey = activeRows.ToDictionary(
            static row => new PublicationScopeKey(
                row.SongId,
                row.Instrument,
                "alltime"));
        var projectionByKey = projectionRows.ToDictionary(
            static row => new PublicationScopeKey(
                row.SongId,
                row.Instrument,
                "alltime"));
        var currentFingerprintByKey = currentFingerprintRows
            .ToDictionary(static row => new PublicationScopeKey(
                row.SongId,
                row.Instrument,
                row.ScopeKind));

        foreach (var row in activeRows)
        {
            if (!instruments.Contains(row.Instrument))
            {
                globalBlockers.Add(new RetentionBlocker(
                    "unexpected_reference_instrument",
                    $"Active snapshot state contains unsupported instrument {row.Instrument}."));
                continue;
            }
            if (row.ActiveSnapshotId is > 0)
            {
                AddLifecycleProtected(
                    row.Instrument,
                    row.ActiveSnapshotId.Value,
                    "active_snapshot");
            }
            else if (row.ActiveSnapshotId.HasValue)
            {
                AddInstrumentBlocker(
                    row.Instrument,
                    "invalid_protected_snapshot_id",
                    $"Active snapshot state contains invalid snapshot ID {row.ActiveSnapshotId.Value}.");
            }
        }

        foreach (var row in projectionRows)
        {
            if (!instruments.Contains(row.Instrument))
            {
                globalBlockers.Add(new RetentionBlocker(
                    "unexpected_reference_instrument",
                    $"Projection state contains unsupported instrument {row.Instrument}."));
                continue;
            }
            if (string.Equals(
                    row.SourceKind,
                    "snapshot",
                    StringComparison.Ordinal)
                && !row.SourceSnapshotId.HasValue)
            {
                AddInstrumentBlocker(
                    row.Instrument,
                    "projection_source_invalid",
                    $"Projection {row.SongId}/{row.Instrument} declares a snapshot source without a snapshot ID.");
                continue;
            }
            if (row.SourceSnapshotId is > 0)
            {
                if (row.RowCount > 0)
                {
                    AddPhysicalProtected(
                        row.Instrument,
                        row.SourceSnapshotId.Value,
                        "projection_source");
                }
                else
                {
                    AddLifecycleProtected(
                        row.Instrument,
                        row.SourceSnapshotId.Value,
                        "projection_source");
                }
            }
        }

        var activeStateFingerprint = FingerprintRows(
            activeRows
                .OrderBy(static row => row.Instrument, StringComparer.Ordinal)
                .ThenBy(static row => row.SongId, StringComparer.Ordinal));
        var projectionFingerprint = FingerprintRows(
            projectionRows
                .OrderBy(static row => row.Instrument, StringComparer.Ordinal)
                .ThenBy(static row => row.SongId, StringComparer.Ordinal));
        var currentScopeFingerprint = FingerprintRows(
            currentFingerprintRows
                .OrderBy(static row => row.Instrument, StringComparer.Ordinal)
                .ThenBy(static row => row.SongId, StringComparer.Ordinal)
                .ThenBy(static row => row.ScopeKind, StringComparer.Ordinal));

        var namedSlots = publication.Slots
            .Where(static slot =>
                slot.PublicationId.HasValue
                && slot.ScrapeId.HasValue
                && slot.Resolved)
            .ToArray();
        var publicationCatalogs =
            await LoadPublicationCatalogsAsync(
                connection,
                transaction,
                namedSlots,
                ct);
        var sourceBindings =
            await LoadSourceBindingsAsync(
                connection,
                transaction,
                namedSlots,
                ct);
        var publicationSources =
            await LoadPublicationSourcesAsync(
                connection,
                transaction,
                namedSlots,
                ct);
        var sourceMaps =
            new List<PublicationSourceMapEvidence>(
                namedSlots.Length);

        foreach (var slot in namedSlots)
        {
            var slotPublicationId = slot.PublicationId!.Value;
            var slotScrapeId = slot.ScrapeId!.Value;
            var slotBlockers = new List<RetentionBlocker>();
            var catalogValidation = ValidatePublicationCatalog(
                slot,
                publicationCatalogs.GetValueOrDefault(
                    slotPublicationId));
            slotBlockers.AddRange(catalogValidation.Blockers);

            var slotSources = publicationSources
                .Where(source =>
                    source.PublishedScrapeId ==
                    slotScrapeId)
                .OrderBy(static source =>
                    source.Instrument,
                    StringComparer.Ordinal)
                .ThenBy(static source =>
                    source.SongId,
                    StringComparer.Ordinal)
                .ThenBy(static source =>
                    source.ScopeKind,
                    StringComparer.Ordinal)
                .ToArray();
            var sourceMapFingerprint =
                FingerprintRows(slotSources);
            var expectedKeys = catalogValidation.SongIds
                .SelectMany(songId =>
                    SnapshotGenerationRetentionContract
                        .Instruments
                        .Select(instrument =>
                            new PublicationScopeKey(
                                songId,
                                instrument.Instrument,
                                "alltime")))
                .ToHashSet();
            var expectedCount = expectedKeys.Count;
            var actualGroups = slotSources
                .GroupBy(static source =>
                    new PublicationScopeKey(
                        source.SongId,
                        source.Instrument,
                        source.ScopeKind))
                .ToArray();

            foreach (var duplicateGroup in actualGroups.Where(
                         static group => group.Count() != 1))
            {
                AddKeyBlocker(
                    duplicateGroup.Key,
                    "publication_source_duplicate",
                    $"The {slot.Name} publication source map contains {duplicateGroup.Count()} rows for {FormatKey(duplicateGroup.Key)}.");
            }

            var actualKeys = actualGroups
                .Select(static group => group.Key)
                .ToHashSet();
            foreach (var instrument in
                     SnapshotGenerationRetentionContract.Instruments)
            {
                var missing = expectedKeys
                    .Where(key =>
                        key.Instrument == instrument.Instrument
                        && !actualKeys.Contains(key))
                    .OrderBy(static key => key.SongId, StringComparer.Ordinal)
                    .ToArray();
                if (missing.Length > 0)
                {
                    AddInstrumentBlocker(
                        instrument.Instrument,
                        "publication_source_key_missing",
                        $"The {slot.Name} publication source map is missing {missing.Length} expected alltime key(s) for {instrument.Instrument}: {FormatKeySample(missing)}.");
                }
            }

            foreach (var extra in actualKeys
                         .Where(key => !expectedKeys.Contains(key))
                         .OrderBy(static key => key.Instrument, StringComparer.Ordinal)
                         .ThenBy(static key => key.SongId, StringComparer.Ordinal)
                         .ThenBy(static key => key.ScopeKind, StringComparer.Ordinal))
            {
                AddKeyBlocker(
                    extra,
                    string.Equals(
                        extra.ScopeKind,
                        "alltime",
                        StringComparison.Ordinal)
                        ? "publication_source_key_extra"
                        : "publication_source_scope_invalid",
                    $"The {slot.Name} publication source map contains unexpected key {FormatKey(extra)}.");
            }

            foreach (var source in slotSources)
            {
                if (!instruments.Contains(source.Instrument))
                {
                    globalBlockers.Add(new RetentionBlocker(
                        "unexpected_reference_instrument",
                        $"The {slot.Name} publication source map contains unsupported instrument {source.Instrument}."));
                    continue;
                }

                var sourceError = ValidatePublicationSource(source);
                if (sourceError is not null)
                {
                    AddInstrumentBlocker(
                        source.Instrument,
                        "publication_source_invalid",
                        $"The {slot.Name} publication source {source.SongId}/{source.Instrument}/{source.ScopeKind} is invalid: {sourceError}");
                    continue;
                }

                if (source.SourceSnapshotId.HasValue)
                {
                    AddPhysicalProtected(
                        source.Instrument,
                        source.SourceSnapshotId.Value,
                        $"{slot.Name}_publication_source");
                }

                if (string.Equals(
                        slot.Name,
                        "current",
                        StringComparison.Ordinal))
                {
                    ValidateCurrentSource(
                        source,
                        currentFingerprintByKey,
                        activeByKey,
                        projectionByKey);
                }
            }

            var sourceBinding = sourceBindings.GetValueOrDefault(
                slotPublicationId);
            var bindingError = ValidateSourceBinding(
                slot,
                sourceBinding,
                expectedCount,
                slotSources.Length);
            if (bindingError is not null)
            {
                slotBlockers.Add(new RetentionBlocker(
                    "publication_source_binding_invalid",
                    bindingError));
            }

            foreach (var blocker in slotBlockers)
                globalBlockers.Add(blocker);
            sourceMaps.Add(new PublicationSourceMapEvidence(
                slot.Name,
                slotPublicationId,
                slotScrapeId,
                catalogValidation.CatalogVersion,
                catalogValidation.CatalogHash,
                catalogValidation.SongIds.Count,
                expectedCount,
                slotSources.Length,
                sourceMapFingerprint,
                sourceBinding?.RowCount,
                sourceBinding?.Status,
                sourceBinding?.BindingKind,
                sourceBinding?.BindingJson,
                slotBlockers));
        }

        foreach (var slot in publication.Slots.Where(
                     static slot => slot.ScrapeId.HasValue))
        {
            foreach (var instrument in
                     SnapshotGenerationRetentionContract.Instruments)
            {
                AddLifecycleProtected(
                    instrument.Instrument,
                    slot.ScrapeId!.Value,
                    $"{slot.Name}_publication_generation");
            }
        }

        var leafSnapshotIds = leaves
            .Select(static leaf => leaf.SnapshotId)
            .Distinct()
            .ToArray();
        var scrapes = new Dictionary<long, ScrapeIdentity>();
        if (leafSnapshotIds.Length > 0)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText = """
                SELECT
                    id::BIGINT,
                    status,
                    completed_at,
                    failed_at
                FROM scrape_log
                WHERE id = ANY(@scrapeIds)
                """;
            command.Parameters.AddWithValue(
                "scrapeIds",
                leafSnapshotIds);
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                scrapes[reader.GetInt64(0)] =
                    new ScrapeIdentity(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.IsDBNull(2)
                            ? null
                            : reader.GetDateTime(2),
                        reader.IsDBNull(3)
                            ? null
                            : reader.GetDateTime(3));
            }
        }

        var successfulPublicationScrapeIds = new List<long>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText = """
                SELECT generation.scrape_id
                FROM publication_generations generation
                JOIN scrape_log scrape
                  ON scrape.id = generation.scrape_id
                WHERE generation.scrape_id IS NOT NULL
                  AND generation.status IN ('current', 'retained')
                  AND generation.published_at IS NOT NULL
                  AND scrape.status = 'completed'
                  AND scrape.completed_at IS NOT NULL
                ORDER BY generation.scrape_id
                """;
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                successfulPublicationScrapeIds.Add(
                    reader.GetInt64(0));
            }
        }

        var unreplayedWriterFailureScrapeIds =
            new List<long>();
        if (leafSnapshotIds.Length > 0)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText = """
                SELECT DISTINCT scrape_id
                FROM scrape_writer_failures
                WHERE scrape_id = ANY(@scrapeIds)
                  AND replayed_at IS NULL
                ORDER BY scrape_id
                """;
            command.Parameters.AddWithValue(
                "scrapeIds",
                leafSnapshotIds);
            await using var reader =
                await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                unreplayedWriterFailureScrapeIds.Add(
                    reader.GetInt64(0));
            }
        }

        return new ReferenceState(
            protectedByInstrument,
            blockersByInstrument,
            globalBlockers,
            scrapes,
            publication.RunningScrapeIds,
            successfulPublicationScrapeIds,
            unreplayedWriterFailureScrapeIds,
            sourceMaps,
            activeRows.Count,
            activeStateFingerprint,
            projectionRows.Count,
            projectionFingerprint,
            currentFingerprintRows.Count,
            currentScopeFingerprint);

        void AddProtected(
            string instrument,
            long snapshotId,
            string reason)
        {
            if (!instruments.Contains(instrument))
                return;
            if (snapshotId <= 0)
            {
                AddInstrumentBlocker(
                    instrument,
                    "invalid_protected_snapshot_id",
                    $"Protected source {reason} has invalid snapshot ID {snapshotId}.");
                return;
            }

            var bySnapshot = protectedByInstrument[instrument];
            if (!bySnapshot.TryGetValue(
                    snapshotId,
                    out var reasons))
            {
                reasons = new HashSet<string>(
                    StringComparer.Ordinal);
                bySnapshot[snapshotId] = reasons;
            }
            reasons.Add(reason);
        }

        void AddLifecycleProtected(
            string instrument,
            long snapshotId,
            string reason)
        {
            if (leafKeys.Contains((instrument, snapshotId)))
                AddProtected(instrument, snapshotId, reason);
        }

        void AddPhysicalProtected(
            string instrument,
            long snapshotId,
            string reason)
        {
            AddProtected(instrument, snapshotId, reason);
            if (!leafKeys.Contains((instrument, snapshotId)))
            {
                AddInstrumentBlocker(
                    instrument,
                    "protected_leaf_missing",
                    $"Physical snapshot {snapshotId} required by {reason} has no direct numeric generation leaf for {instrument}.");
            }
        }

        void AddInstrumentBlocker(
            string instrument,
            string code,
            string detail)
        {
            if (blockersByInstrument.TryGetValue(
                    instrument,
                    out var blockers))
            {
                blockers.Add(new RetentionBlocker(
                    code,
                    detail));
            }
        }

        void AddKeyBlocker(
            PublicationScopeKey key,
            string code,
            string detail)
        {
            if (instruments.Contains(key.Instrument))
                AddInstrumentBlocker(key.Instrument, code, detail);
            else
                globalBlockers.Add(new RetentionBlocker(code, detail));
        }

        void ValidateCurrentSource(
            PublicationSource source,
            IReadOnlyDictionary<
                PublicationScopeKey,
                CurrentScopeFingerprintRow> fingerprintByKey,
            IReadOnlyDictionary<
                PublicationScopeKey,
                ActiveSnapshotRow> activeByScope,
            IReadOnlyDictionary<
                PublicationScopeKey,
                ProjectionRow> projectionByScope)
        {
            var key = new PublicationScopeKey(
                source.SongId,
                source.Instrument,
                source.ScopeKind);
            if (!fingerprintByKey.TryGetValue(
                    key,
                    out var fingerprint)
                || fingerprint.FingerprintVersion < 2
                || !fingerprint.IsComplete
                || fingerprint.LastSeenScrapeId
                    != source.PublishedScrapeId
                || fingerprint.SourceScrapeId
                    != source.PublishedScrapeId
                || fingerprint.PublishedScrapeId
                    != source.PublishedScrapeId
                || fingerprint.EntryCount != source.RowCount
                || !string.Equals(
                    fingerprint.ContentFingerprint,
                    source.ContentFingerprint,
                    StringComparison.Ordinal)
                || !string.Equals(
                    fingerprint.CoverageFingerprint,
                    source.CoverageFingerprint,
                    StringComparison.Ordinal)
                || !fingerprint.ReportedTotalEntries.HasValue
                || source.ReportedTotalEntries
                    < fingerprint.ReportedTotalEntries.Value
                || fingerprint.ReportedTotalPages
                    != source.ReportedTotalPages)
            {
                AddInstrumentBlocker(
                    source.Instrument,
                    "current_fingerprint_mismatch",
                    $"Current publication source {FormatKey(key)} does not match a complete published current fingerprint.");
            }

            if (!activeByScope.TryGetValue(
                    key,
                    out var active)
                || !active.IsFinalized
                || active.ActiveSnapshotId is null
                || active.ActiveSnapshotId <= 0
                || active.ScrapeId != active.ActiveSnapshotId)
            {
                AddInstrumentBlocker(
                    source.Instrument,
                    "current_active_state_invalid",
                    $"Current publication source {FormatKey(key)} has no finalized active snapshot identity.");
                return;
            }

            if (!projectionByScope.TryGetValue(
                    key,
                    out var projection)
                || !string.Equals(
                    projection.Status,
                    "ready",
                    StringComparison.Ordinal)
                || !string.Equals(
                    projection.SourceKind,
                    "snapshot",
                    StringComparison.Ordinal)
                || projection.SourceSnapshotId
                    != active.ActiveSnapshotId)
            {
                AddInstrumentBlocker(
                    source.Instrument,
                    "current_projection_mismatch",
                    $"Current publication source {FormatKey(key)} has no ready snapshot projection matching active snapshot {active.ActiveSnapshotId}.");
                return;
            }

            if (string.Equals(
                    source.SourceKind,
                    "empty",
                    StringComparison.Ordinal))
            {
                if (projection.RowCount != 0)
                {
                    AddInstrumentBlocker(
                        source.Instrument,
                        "authoritative_empty_projection_invalid",
                        $"Authoritative empty source {FormatKey(key)} has projection row count {projection.RowCount}, not zero.");
                }
            }
            else if (source.SourceSnapshotId
                     != active.ActiveSnapshotId)
            {
                AddInstrumentBlocker(
                    source.Instrument,
                    "current_active_state_mismatch",
                    $"Current snapshot source {FormatKey(key)} references snapshot {source.SourceSnapshotId}, but active state references {active.ActiveSnapshotId}.");
            }
            else if (projection.RowCount < source.RowCount)
            {
                AddInstrumentBlocker(
                    source.Instrument,
                    "current_projection_mismatch",
                    $"Current snapshot projection {FormatKey(key)} has {projection.RowCount} row(s), below source evidence {source.RowCount}.");
            }
        }
    }

    private static async Task<IReadOnlyList<ActiveSnapshotRow>>
        LoadActiveSnapshotRowsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                song_id,
                instrument,
                active_snapshot_id,
                scrape_id,
                is_finalized
            FROM leaderboard_snapshot_state
            ORDER BY instrument, song_id
            """;
        var rows = new List<ActiveSnapshotRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ActiveSnapshotRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.GetBoolean(4)));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<ProjectionRow>>
        LoadProjectionRowsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                song_id,
                instrument,
                projection_generation,
                row_count,
                source_snapshot_id,
                source_kind,
                status,
                error_message
            FROM solo_current_projection_scope
            ORDER BY instrument, song_id
            """;
        var rows = new List<ProjectionRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ProjectionRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<CurrentScopeFingerprintRow>>
        LoadCurrentFingerprintRowsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                song_id,
                instrument,
                scope_kind,
                fingerprint_version,
                content_fingerprint,
                coverage_fingerprint,
                entry_count::BIGINT,
                reported_total_entries,
                reported_total_pages,
                is_complete,
                source_scrape_id,
                published_scrape_id,
                last_seen_scrape_id
            FROM leaderboard_scope_fingerprints
            ORDER BY instrument, song_id, scope_kind
            """;
        var rows = new List<CurrentScopeFingerprintRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new CurrentScopeFingerprintRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.GetBoolean(9),
                reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetInt64(11),
                reader.GetInt64(12)));
        }
        return rows;
    }

    private static async Task<IReadOnlyDictionary<
        long,
        PublicationCatalogRow>> LoadPublicationCatalogsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyCollection<PublicationSlot> slots,
            CancellationToken ct)
    {
        var publicationIds = slots
            .Select(static slot => slot.PublicationId!.Value)
            .Distinct()
            .ToArray();
        if (publicationIds.Length == 0)
        {
            return new Dictionary<long, PublicationCatalogRow>();
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                generation.publication_id,
                generation.scrape_id,
                catalog.catalog_version,
                catalog.schema_version,
                catalog.catalog_json::TEXT,
                catalog.content_hash,
                catalog.song_count,
                catalog.source_kind,
                catalog.is_exact,
                catalog.source_captured_at,
                binding.binding_kind,
                binding.row_count,
                binding.content_hash,
                binding.status
            FROM publication_generations generation
            LEFT JOIN publication_song_catalog catalog
              ON catalog.publication_id = generation.publication_id
            LEFT JOIN publication_surface_bindings binding
              ON binding.publication_id = generation.publication_id
             AND binding.surface_name = 'song_catalog'
            WHERE generation.publication_id = ANY(@publicationIds)
            ORDER BY generation.publication_id
            """;
        command.Parameters.AddWithValue(
            "publicationIds",
            publicationIds);
        var rows = new Dictionary<long, PublicationCatalogRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var publicationId = reader.GetInt64(0);
            rows[publicationId] = new PublicationCatalogRow(
                publicationId,
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetBoolean(8),
                reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetInt64(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13));
        }
        return rows;
    }

    private static async Task<IReadOnlyDictionary<
        long,
        PublicationSourceBinding>> LoadSourceBindingsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyCollection<PublicationSlot> slots,
            CancellationToken ct)
    {
        var publicationIds = slots
            .Select(static slot => slot.PublicationId!.Value)
            .Distinct()
            .ToArray();
        if (publicationIds.Length == 0)
        {
            return new Dictionary<long, PublicationSourceBinding>();
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                publication_id,
                binding_kind,
                binding_json::TEXT,
                row_count,
                content_hash,
                status
            FROM publication_surface_bindings
            WHERE publication_id = ANY(@publicationIds)
              AND surface_name = 'solo_scope_sources'
            ORDER BY publication_id
            """;
        command.Parameters.AddWithValue(
            "publicationIds",
            publicationIds);
        var rows =
            new Dictionary<long, PublicationSourceBinding>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var publicationId = reader.GetInt64(0);
            rows[publicationId] = new PublicationSourceBinding(
                publicationId,
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<PublicationSource>>
        LoadPublicationSourcesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlyCollection<PublicationSlot> slots,
            CancellationToken ct)
    {
        var scrapeIds = slots
            .Select(static slot => slot.ScrapeId!.Value)
            .Distinct()
            .ToArray();
        if (scrapeIds.Length == 0)
            return [];

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                published_scrape_id,
                song_id,
                instrument,
                scope_kind,
                source_kind,
                source_snapshot_id,
                source_scrape_id,
                row_count,
                content_fingerprint,
                coverage_fingerprint,
                reported_total_entries,
                reported_total_pages,
                is_complete
            FROM leaderboard_published_scope_source
            WHERE published_scrape_id = ANY(@scrapeIds)
            ORDER BY published_scrape_id,
                     instrument,
                     song_id,
                     scope_kind
            """;
        command.Parameters.AddWithValue("scrapeIds", scrapeIds);
        var rows = new List<PublicationSource>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PublicationSource(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetInt64(10),
                reader.GetInt32(11),
                reader.GetBoolean(12)));
        }
        return rows;
    }

    private static PublicationCatalogValidation
        ValidatePublicationCatalog(
            PublicationSlot slot,
            PublicationCatalogRow? catalog)
    {
        var blockers = new List<RetentionBlocker>();
        var songIds = Array.Empty<string>();
        if (catalog is null
            || catalog.ScrapeId != slot.ScrapeId
            || !catalog.CatalogVersion.HasValue
            || !catalog.SchemaVersion.HasValue
            || string.IsNullOrWhiteSpace(catalog.CatalogJson)
            || string.IsNullOrWhiteSpace(catalog.ContentHash)
            || !catalog.SongCount.HasValue
            || !catalog.SourceCapturedAtUtc.HasValue)
        {
            blockers.Add(new RetentionBlocker(
                "publication_catalog_missing",
                $"The {slot.Name} publication {slot.PublicationId} has no complete publication song catalog row."));
        }
        else
        {
            try
            {
                var songs =
                    SongCatalogSnapshotBuilder.DeserializeCatalog(
                        catalog.CatalogJson);
                var rebuilt =
                    SongCatalogSnapshotBuilder.Create(songs);
                songIds = songs
                    .Select(static song => song.track?.su)
                    .Where(static songId =>
                        !string.IsNullOrWhiteSpace(songId))
                    .Select(static songId => songId!)
                    .OrderBy(static songId => songId, StringComparer.Ordinal)
                    .ToArray();
                if (songIds.Length
                        != songIds.Distinct(StringComparer.Ordinal).Count()
                    || songIds.Length != catalog.SongCount
                    || rebuilt.SongCount != catalog.SongCount
                    || catalog.CatalogVersion <= 0
                    || catalog.SchemaVersion
                        != SongCatalogSnapshotBuilder.SchemaVersion
                    || catalog.IsExact is not true
                    || !string.Equals(
                        catalog.SourceKind,
                        "provider_exact",
                        StringComparison.Ordinal)
                    || !string.Equals(
                        rebuilt.ContentHash,
                        catalog.ContentHash,
                        StringComparison.Ordinal))
                {
                    blockers.Add(new RetentionBlocker(
                        "publication_catalog_invalid",
                        $"The {slot.Name} publication {slot.PublicationId} catalog identity, count, or canonical content hash is invalid."));
                }
            }
            catch (Exception ex)
            {
                blockers.Add(new RetentionBlocker(
                    "publication_catalog_invalid",
                    $"The {slot.Name} publication {slot.PublicationId} catalog cannot be decoded: {Truncate(ex.Message, 500)}"));
            }
        }

        if (catalog is null
            || !string.Equals(
                catalog.BindingKind,
                "generation_catalog_snapshot",
                StringComparison.Ordinal)
            || !string.Equals(
                catalog.BindingStatus,
                "ready",
                StringComparison.Ordinal)
            || catalog.BindingRowCount != catalog.SongCount
            || !string.Equals(
                catalog.BindingContentHash,
                catalog.ContentHash,
                StringComparison.Ordinal))
        {
            blockers.Add(new RetentionBlocker(
                "publication_catalog_binding_invalid",
                $"The {slot.Name} publication {slot.PublicationId} catalog binding is missing or does not match the exact catalog row."));
        }

        return new PublicationCatalogValidation(
            catalog?.CatalogVersion,
            catalog?.ContentHash,
            songIds,
            blockers);
    }

    private static string? ValidateSourceBinding(
        PublicationSlot slot,
        PublicationSourceBinding? binding,
        int expectedCount,
        int actualCount)
    {
        if (binding is null)
        {
            return $"The {slot.Name} publication {slot.PublicationId} has no solo_scope_sources binding.";
        }
        if (!string.Equals(
                binding.BindingKind,
                "scrape_id",
                StringComparison.Ordinal)
            || !string.Equals(
                binding.Status,
                "ready",
                StringComparison.Ordinal)
            || binding.RowCount != expectedCount
            || binding.RowCount != actualCount)
        {
            return $"The {slot.Name} publication {slot.PublicationId} solo_scope_sources binding kind, status, or count is invalid (expected={expectedCount}, actual={actualCount}, bound={binding.RowCount?.ToString() ?? "null"}).";
        }

        try
        {
            using var document =
                JsonDocument.Parse(binding.BindingJson);
            var root = document.RootElement;
            var properties = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().ToArray()
                : [];
            if (properties.Length != 3
                || !root.TryGetProperty(
                    "publicationId",
                    out var publicationId)
                || !publicationId.TryGetInt64(
                    out var boundPublicationId)
                || boundPublicationId != slot.PublicationId
                || !root.TryGetProperty(
                    "publishedScrapeId",
                    out var scrapeId)
                || !scrapeId.TryGetInt64(
                    out var boundScrapeId)
                || boundScrapeId != slot.ScrapeId
                || !root.TryGetProperty("table", out var table)
                || !string.Equals(
                    table.GetString(),
                    "leaderboard_published_scope_source",
                    StringComparison.Ordinal))
            {
                return $"The {slot.Name} publication {slot.PublicationId} solo_scope_sources binding JSON does not exactly identify its publication and published scrape.";
            }
        }
        catch (Exception ex) when (
            ex is JsonException
            or InvalidOperationException)
        {
            return $"The {slot.Name} publication {slot.PublicationId} solo_scope_sources binding JSON is malformed.";
        }

        return null;
    }

    private static string FingerprintRows<T>(
        IEnumerable<T> rows) =>
        TierZeroCanonicalJson.Sha256Hex(
            TierZeroCanonicalJson.Serialize(rows));

    private static string FormatKey(
        PublicationScopeKey key) =>
        $"{key.SongId}/{key.Instrument}/{key.ScopeKind}";

    private static string FormatKeySample(
        IReadOnlyList<PublicationScopeKey> keys) =>
        string.Join(
            ", ",
            keys.Take(5).Select(FormatKey))
        + (keys.Count > 5 ? ", ..." : string.Empty);

    internal static string? ValidatePublicationSource(
        PublicationSource source)
    {
        if (string.IsNullOrWhiteSpace(source.SongId))
            return "The publication source row has no song identity.";
        if (!string.Equals(
                source.ScopeKind,
                "alltime",
                StringComparison.Ordinal))
        {
            return "The publication source row is not an alltime scope.";
        }
        if (!source.IsComplete)
            return "The publication source row is not complete.";
        if (string.IsNullOrWhiteSpace(source.ContentFingerprint)
            || string.IsNullOrWhiteSpace(source.CoverageFingerprint))
        {
            return "The publication source row has missing content or coverage evidence.";
        }
        if (source.SourceScrapeId <= 0
            || source.SourceScrapeId >
                source.PublishedScrapeId)
        {
            return "The publication source row has an invalid source scrape identity.";
        }

        return source.SourceKind switch
        {
            "snapshot"
                when source.SourceSnapshotId.HasValue
                     && source.SourceSnapshotId.Value > 0
                     && source.SourceSnapshotId.Value ==
                        source.SourceScrapeId
                     && source.RowCount > 0
                     && source.ReportedTotalEntries >=
                        source.RowCount
                     && source.ReportedTotalPages > 0 =>
                null,
            "empty"
                when !source.SourceSnapshotId.HasValue
                     && source.RowCount == 0
                     && source.ReportedTotalEntries == 0
                     && source.ReportedTotalPages == 0 =>
                null,
            "snapshot" =>
                "The snapshot publication source row has invalid snapshot, row-count, or page evidence.",
            "empty" =>
                "The authoritative empty publication source row is malformed.",
            _ =>
                $"The publication source kind {source.SourceKind} is unsupported.",
        };
    }

    private List<LeafEvaluation> EvaluateLeaves(
        SnapshotGenerationRetentionPlanRequest request,
        SnapshotGenerationRetentionPolicy policy,
        CatalogState catalog,
        IReadOnlyDictionary<string, long> defaultCounts,
        PublicationState publication,
        ReferenceState references)
    {
        var configuredResumeScrapeId =
            Math.Max(0, _scraperOptions.Value.ResumeScrapeId);
        var running = references.RunningScrapeIds.ToHashSet();
        var writerFailures = references
            .UnreplayedWriterFailureScrapeIds
            .ToHashSet();
        var namedGenerationIds = publication.Slots
            .Where(static slot => slot.ScrapeId.HasValue)
            .GroupBy(static slot => slot.ScrapeId!.Value)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(static slot => slot.Name)
                    .OrderBy(static name => name, StringComparer.Ordinal)
                    .ToArray());
        var newestByInstrument = catalog.Leaves
            .GroupBy(
                static leaf => leaf.Instrument.Instrument,
                StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                group => group
                    .OrderByDescending(
                        static leaf => leaf.SnapshotId)
                    .Take(policy.NewestGenerationsToKeep)
                    .Select(static leaf => leaf.SnapshotId)
                    .ToHashSet(),
                StringComparer.Ordinal);

        var evaluations = new List<LeafEvaluation>(
            catalog.Leaves.Count);
        foreach (var leaf in catalog.Leaves)
        {
            var blockers = new List<RetentionBlocker>();
            blockers.AddRange(catalog.GlobalBlockers);
            blockers.AddRange(publication.Blockers);
            blockers.AddRange(references.GlobalBlockers);
            blockers.AddRange(
                catalog.InstrumentBlockers[
                    leaf.Instrument.Instrument]);
            blockers.AddRange(leaf.CatalogBlockers);
            blockers.AddRange(
                references.InstrumentBlockers[
                    leaf.Instrument.Instrument]);

            if (!defaultCounts.TryGetValue(
                    leaf.Instrument.RootRelation,
                    out var defaultRows))
            {
                blockers.Add(new RetentionBlocker(
                    "default_count_unresolved",
                    $"The default child count for {leaf.Instrument.RootRelation} was not resolved."));
            }
            else if (defaultRows != 0)
            {
                blockers.Add(new RetentionBlocker(
                    "default_not_empty",
                    $"The default child for {leaf.Instrument.RootRelation} contains {defaultRows} row(s)."));
            }

            if (references.Protected[
                    leaf.Instrument.Instrument]
                .TryGetValue(
                    leaf.SnapshotId,
                    out var protectedReasons))
            {
                foreach (var reason in protectedReasons)
                {
                    blockers.Add(new RetentionBlocker(
                        reason,
                        $"Snapshot {leaf.SnapshotId} is protected by {reason}."));
                }
            }

            if (namedGenerationIds.TryGetValue(
                    leaf.SnapshotId,
                    out var slotNames))
            {
                foreach (var slotName in slotNames)
                {
                    blockers.Add(new RetentionBlocker(
                        $"{slotName}_publication_generation",
                        $"Snapshot {leaf.SnapshotId} is the {slotName} publication generation."));
                }
            }
            if (running.Contains(leaf.SnapshotId))
            {
                blockers.Add(new RetentionBlocker(
                    "running_scrape",
                    $"Snapshot {leaf.SnapshotId} belongs to a running scrape."));
            }
            if (configuredResumeScrapeId > 0
                && leaf.SnapshotId ==
                    configuredResumeScrapeId)
            {
                blockers.Add(new RetentionBlocker(
                    "configured_resume_scrape",
                    $"Snapshot {leaf.SnapshotId} is the configured resume scrape."));
            }
            if (newestByInstrument[
                    leaf.Instrument.Instrument]
                .Contains(leaf.SnapshotId))
            {
                blockers.Add(new RetentionBlocker(
                    "newest_generation",
                    $"Snapshot {leaf.SnapshotId} is inside the newest {policy.NewestGenerationsToKeep} generations for {leaf.Instrument.Instrument}."));
            }

            var laterSuccessfulPublications = references
                .SuccessfulPublicationScrapeIds
                .Count(id => id > leaf.SnapshotId);
            if (laterSuccessfulPublications <
                policy.MinimumLaterSuccessfulPublications)
            {
                blockers.Add(new RetentionBlocker(
                    "insufficient_later_publications",
                    $"Snapshot {leaf.SnapshotId} has {laterSuccessfulPublications} later successful publication(s); {policy.MinimumLaterSuccessfulPublications} are required."));
            }

            if (!references.Scrapes.TryGetValue(
                    leaf.SnapshotId,
                    out var scrape))
            {
                blockers.Add(new RetentionBlocker(
                    "scrape_identity_missing",
                    $"Snapshot {leaf.SnapshotId} has no scrape_log row."));
            }
            else if (!scrape.IsTerminal)
            {
                blockers.Add(new RetentionBlocker(
                    "scrape_not_terminal",
                    $"Snapshot {leaf.SnapshotId} scrape status {scrape.Status} is not a complete terminal identity."));
            }

            if (policy.BlockUnreplayedWriterFailures
                && writerFailures.Contains(leaf.SnapshotId))
            {
                blockers.Add(new RetentionBlocker(
                    "unreplayed_writer_failure",
                    $"Snapshot {leaf.SnapshotId} has unreplayed writer failure artifacts."));
            }

            evaluations.Add(new LeafEvaluation(
                leaf,
                laterSuccessfulPublications,
                blockers
                    .DistinctBy(static blocker =>
                        (blocker.Code, blocker.Detail))
                    .OrderBy(
                        static blocker => blocker.Code,
                        StringComparer.Ordinal)
                    .ThenBy(
                        static blocker => blocker.Detail,
                        StringComparer.Ordinal)
                    .ToArray()));
        }

        return evaluations;
    }

    private static async Task<ActivePlaceholder?> LoadActivePlaceholderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                active.job_id,
                active.cycle_id,
                active.status,
                active.instrument,
                active.snapshot_id
            FROM (
                SELECT
                    job_id,
                    cycle_id,
                    status,
                    instrument,
                    snapshot_id
                FROM snapshot_generation_retention_jobs
                WHERE NOT report_only
                  AND status IN (
                      'leased',
                      'executing',
                      'safety_failed')
                UNION ALL
                SELECT
                    NULL::BIGINT,
                    cycle_id,
                    status,
                    NULL::TEXT,
                    NULL::BIGINT
                FROM snapshot_generation_retention_cycles
                WHERE NOT report_only
                  AND status = 'safety_failed'
            ) active
            ORDER BY active.cycle_id, active.job_id NULLS FIRST
            LIMIT 1
            """;
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ActivePlaceholder(
                reader.IsDBNull(0) ? null : reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4))
            : null;
    }

    private static async Task<HashSet<string>>
        LoadOutstandingJobIdentitiesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT instrument,
                   child_oid,
                   child_relfilenode
            FROM snapshot_generation_retention_jobs
            WHERE NOT report_only
              AND status IN (
                  'planned',
                  'leased',
                  'executing',
                  'safety_failed')
            """;
        var identities =
            new HashSet<string>(StringComparer.Ordinal);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            identities.Add(
                $"{reader.GetString(0)}:"
                + $"{reader.GetInt64(1)}:"
                + $"{reader.GetInt64(2)}");
        }

        return identities;
    }

    private async Task<SnapshotGenerationRetentionPlanResult?>
        TryRecordFailureAsync(
            SnapshotGenerationRetentionPlanRequest request,
            SnapshotGenerationRetentionPolicy policy,
            Exception exception,
            CancellationToken ct)
    {
        try
        {
            await using var connection =
                await _dataSource.OpenConnectionAsync(ct);
            if (!await TryAcquireSessionLockAsync(
                    connection,
                    PublicationGenerationSchema.AdvisoryLockKey,
                    shared: true,
                    ct))
            {
                return null;
            }
            await using var publicationLock =
                new SessionAdvisoryLockLease(
                    connection,
                    PublicationGenerationSchema
                        .AdvisoryLockKey,
                    shared: true);
            if (!await TryAcquireSessionLockAsync(
                    connection,
                    SnapshotGenerationRetentionContract
                        .PlannerAdvisoryLockKey,
                    shared: false,
                    ct))
            {
                return null;
            }
            await using var plannerLock =
                new SessionAdvisoryLockLease(
                    connection,
                    SnapshotGenerationRetentionContract
                        .PlannerAdvisoryLockKey,
                    shared: false);
            await using var transaction =
                await connection.BeginTransactionAsync(ct);
            await ApplyTransactionTimeoutsAsync(
                connection,
                transaction,
                ct);
            var existing =
                await SnapshotGenerationRetentionRepository
                    .GetCycleForSafePointAsync(
                        connection,
                        transaction,
                        request.SafePointKind,
                        request.TriggerPublicationId,
                        ct);
            if (existing is not null)
            {
                var result = await BuildExistingResultAsync(
                    connection,
                    transaction,
                    existing,
                    ct);
                await transaction.CommitAsync(ct);
                return result;
            }

            var cycleId =
                await SnapshotGenerationRetentionRepository
                    .InsertPlanningCycleAsync(
                        connection,
                        transaction,
                        request,
                        policy,
                        ct);
            var error = Truncate(
                exception.GetBaseException().Message,
                4_000);
            var payload = new
            {
                SnapshotGenerationRetentionContract.PlannerVersion,
                SnapshotGenerationRetentionContract.ConfigVersion,
                request.TriggerScrapeId,
                request.TriggerPublicationId,
                request.SafePointKind,
                Policy = policy,
                ErrorType = exception.GetType().FullName,
                Error = error,
            };
            var digest = TierZeroCanonicalJson.Sha256Hex(
                TierZeroCanonicalJson.Serialize(payload));
            var evidence = new EvidenceChain(
                connection,
                transaction,
                cycleId);
            await evidence.AppendAsync(
                null,
                "failure",
                "planner_exception",
                payload,
                ct);
            await SnapshotGenerationRetentionRepository.CompleteCycleAsync(
                connection,
                transaction,
                cycleId,
                SnapshotGenerationRetentionCycleStatus.Failed,
                digest,
                0,
                0,
                0,
                0,
                error,
                ct);
            await transaction.CommitAsync(ct);
            return new SnapshotGenerationRetentionPlanResult(
                SnapshotGenerationRetentionPlanDisposition.Failed,
                cycleId,
                "snapshot-generation retention planning failed; durable failure evidence was recorded",
                digest,
                0,
                0,
                0,
                0,
                0);
        }
        catch (Exception recordException)
        {
            _log.LogError(
                recordException,
                "Failed to persist snapshot-generation retention planner failure evidence for publication {PublicationId}.",
                request.TriggerPublicationId);
            return null;
        }
    }

    private static async Task<SnapshotGenerationRetentionPlanResult>
        BuildExistingResultAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            SnapshotGenerationRetentionCycle cycle,
            CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT COUNT(*)::INTEGER
            FROM snapshot_generation_retention_jobs
            WHERE cycle_id = @cycleId
              AND status = 'planned'
            """;
        command.Parameters.AddWithValue(
            "cycleId",
            cycle.CycleId);
        var plannedCount =
            Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        var disposition = cycle.Status is
            SnapshotGenerationRetentionCycleStatus.Failed
            or SnapshotGenerationRetentionCycleStatus.SafetyFailed
                ? SnapshotGenerationRetentionPlanDisposition.Failed
                : SnapshotGenerationRetentionPlanDisposition.Existing;
        return new SnapshotGenerationRetentionPlanResult(
            disposition,
            cycle.CycleId,
            $"the safe point already has a durable snapshot-generation retention cycle with status {cycle.Status}",
            cycle.PlanDigest,
            cycle.CandidateCount,
            plannedCount,
            cycle.BlockedCount,
            cycle.CandidateBytes,
            cycle.BlockedBytes);
    }

    private static int RotateFairness(
        int instrumentOrder,
        long triggerPublicationId)
    {
        var count =
            SnapshotGenerationRetentionContract.Instruments.Count;
        var offset = (int)(triggerPublicationId % count);
        return (instrumentOrder - offset + count) % count;
    }

    private static string BuildResultReason(
        SnapshotGenerationRetentionPlanDisposition disposition,
        int candidateCount,
        int plannedCount,
        int blockedCount) =>
        disposition switch
        {
            SnapshotGenerationRetentionPlanDisposition.Observed =>
                $"report-only cycle observed {candidateCount} eligible child(ren), recorded no executable jobs, and persisted {blockedCount} blocked child(ren)",
            SnapshotGenerationRetentionPlanDisposition.Planned =>
                $"execution-enabled cycle persisted {plannedCount} planned child placeholder(s), {candidateCount} eligible child(ren), and {blockedCount} blocked child(ren)",
            SnapshotGenerationRetentionPlanDisposition.Deferred =>
                $"cycle found {candidateCount} eligible child(ren), but the global planned/active placeholder or cycle limit deferred all of them",
            _ =>
                $"cycle found no eligible children and persisted {blockedCount} blocked child(ren)",
        };

    private static bool IsPgDefault(string tablespaceName) =>
        string.Equals(
            tablespaceName,
            "pg_default",
            StringComparison.Ordinal);

    private static bool IsRetryableSafePointBlocker(
        string code) =>
        code is
            "public_reads_frozen"
            or "working_publication_present"
            or "publication_commit_intent_present"
            or "improvement_notifications_incomplete"
            or "registration_drain_incomplete"
            or "running_scrape";

    private static string Truncate(
        string value,
        int maxLength) =>
        value.Length <= maxLength
            ? value
            : value[..maxLength];

    internal sealed record CatalogRelation(
        long Oid,
        string RelationName,
        string Relkind,
        string PartitionKeyDefinition,
        long Relfilenode,
        string PartitionBound,
        string TablespaceName,
        long? ParentOid,
        string? ParentRelationName,
        long RowEstimate,
        long TotalBytes);

    internal sealed record CatalogIndex(
        long TableOid,
        string TableName,
        long IndexOid,
        string IndexName,
        string Relkind,
        bool IsValid,
        bool IsReady,
        string TablespaceName,
        long? ParentIndexOid,
        string? ParentIndexName,
        bool IsPrimary,
        bool IsUnique,
        string AccessMethod,
        bool HasExpressions,
        bool HasPredicate,
        bool HasNoIncludedColumns,
        string KeyDefinition,
        string IndexOptions,
        string OperatorClasses,
        string Collations,
        string IndexDefinition);

    internal sealed record PublicationSource(
        long PublishedScrapeId,
        string SongId,
        string Instrument,
        string ScopeKind,
        string SourceKind,
        long? SourceSnapshotId,
        long SourceScrapeId,
        long RowCount,
        string ContentFingerprint,
        string CoverageFingerprint,
        long ReportedTotalEntries,
        int ReportedTotalPages,
        bool IsComplete);

    private readonly record struct PublicationScopeKey(
        string SongId,
        string Instrument,
        string ScopeKind);

    private sealed record ActiveSnapshotRow(
        string SongId,
        string Instrument,
        long? ActiveSnapshotId,
        long? ScrapeId,
        bool IsFinalized);

    private sealed record ProjectionRow(
        string SongId,
        string Instrument,
        long ProjectionGeneration,
        long RowCount,
        long? SourceSnapshotId,
        string SourceKind,
        string Status,
        string? ErrorMessage);

    private sealed record CurrentScopeFingerprintRow(
        string SongId,
        string Instrument,
        string ScopeKind,
        int FingerprintVersion,
        string ContentFingerprint,
        string CoverageFingerprint,
        long EntryCount,
        long? ReportedTotalEntries,
        int? ReportedTotalPages,
        bool IsComplete,
        long SourceScrapeId,
        long? PublishedScrapeId,
        long LastSeenScrapeId);

    private sealed record PublicationCatalogRow(
        long PublicationId,
        long? ScrapeId,
        long? CatalogVersion,
        int? SchemaVersion,
        string? CatalogJson,
        string? ContentHash,
        int? SongCount,
        string? SourceKind,
        bool? IsExact,
        DateTime? SourceCapturedAtUtc,
        string? BindingKind,
        long? BindingRowCount,
        string? BindingContentHash,
        string? BindingStatus);

    private sealed record PublicationCatalogValidation(
        long? CatalogVersion,
        string? CatalogHash,
        IReadOnlyList<string> SongIds,
        IReadOnlyList<RetentionBlocker> Blockers);

    private sealed record PublicationSourceBinding(
        long PublicationId,
        string BindingKind,
        string BindingJson,
        long? RowCount,
        string? ContentHash,
        string Status);

    private sealed record PublicationSourceMapEvidence(
        string SlotName,
        long PublicationId,
        long PublishedScrapeId,
        long? CatalogVersion,
        string? CatalogHash,
        int CatalogSongCount,
        int ExpectedKeyCount,
        int ActualRowCount,
        string SourceMapFingerprint,
        long? BindingRowCount,
        string? BindingStatus,
        string? BindingKind,
        string? BindingJson,
        IReadOnlyList<RetentionBlocker> Blockers);

    internal sealed record CatalogState(
        string? TopPartitionKey,
        IReadOnlyList<RetentionRootEvidence> Roots,
        IReadOnlyList<GenerationLeaf> Leaves,
        IReadOnlyList<RetentionBlocker> GlobalBlockers,
        IReadOnlyDictionary<string, List<RetentionBlocker>>
            InstrumentBlockers);

    internal sealed record RetentionRootEvidence(
        string Instrument,
        string RootRelation,
        long? RootOid,
        string? RootRelkind,
        string? RootPartitionKey,
        string? RootBound,
        string? RootTablespace,
        string? DefaultRelation,
        long? DefaultOid,
        IReadOnlyList<RetentionBlocker> Blockers);

    internal sealed record GenerationLeaf(
        SnapshotGenerationRetentionInstrument Instrument,
        string ChildRelation,
        long SnapshotId,
        long ChildOid,
        long ChildRelfilenode,
        string PartitionBound,
        string TablespaceName,
        long RowEstimate,
        long TotalBytes,
        List<RetentionBlocker> CatalogBlockers)
    {
        public string Identity =>
            $"{Instrument.Instrument}:{ChildOid}:{ChildRelfilenode}";
    }

    internal sealed record RetentionBlocker(
        string Code,
        string Detail);

    private sealed record PublicationSlotGeneration(
        long PublicationId,
        long? ScrapeId,
        string Status,
        long? PreviousPublicationId);

    private sealed record PublicationSlot(
        string Name,
        long? PublicationId,
        long? ScrapeId,
        string? Status,
        bool Required,
        bool Resolved);

    private sealed record PublicationState(
        long? PublishedScrapeId,
        long? CurrentPublicationId,
        long? PreviousPublicationId,
        long? WorkingPublicationId,
        bool PublicReadsFrozen,
        DateTime? CommitIntentStartedAtUtc,
        DateTime? CommitIntentHeartbeatAtUtc,
        string? CommitIntentOwner,
        long? ImprovementNotificationScrapeId,
        string? ImprovementNotificationStatus,
        DateTime? ImprovementNotificationCompletedAtUtc,
        bool ImprovementNotificationProjectionReady,
        long? ImprovementNotificationProjectionScrapeId,
        RegistrationDrainState RegistrationDrain,
        IReadOnlyList<long> RunningScrapeIds,
        IReadOnlyList<PublicationSlot> Slots,
        IReadOnlyList<RetentionBlocker> Blockers)
    {
        public long? GetScrapeId(string slotName) =>
            Slots.Single(slot =>
                    string.Equals(
                        slot.Name,
                        slotName,
                        StringComparison.Ordinal))
                .ScrapeId;
    }

    private sealed record ScrapeIdentity(
        long ScrapeId,
        string Status,
        DateTime? CompletedAtUtc,
        DateTime? FailedAtUtc)
    {
        public bool IsTerminal =>
            Status == "completed"
                ? CompletedAtUtc.HasValue
                : Status == "failed"
                  && FailedAtUtc.HasValue;
    }

    private sealed record ReferenceState(
        IReadOnlyDictionary<
            string,
            Dictionary<long, HashSet<string>>> Protected,
        IReadOnlyDictionary<string, List<RetentionBlocker>>
            InstrumentBlockers,
        IReadOnlyList<RetentionBlocker> GlobalBlockers,
        IReadOnlyDictionary<long, ScrapeIdentity> Scrapes,
        IReadOnlyList<long> RunningScrapeIds,
        IReadOnlyList<long> SuccessfulPublicationScrapeIds,
        IReadOnlyList<long> UnreplayedWriterFailureScrapeIds,
        IReadOnlyList<PublicationSourceMapEvidence> PublicationSourceMaps,
        int ActiveStateRowCount,
        string ActiveStateFingerprint,
        int ProjectionRowCount,
        string ProjectionFingerprint,
        int CurrentFingerprintRowCount,
        string CurrentFingerprint);

    private sealed record LeafEvaluation(
        GenerationLeaf Leaf,
        int LaterSuccessfulPublications,
        IReadOnlyList<RetentionBlocker> Blockers);

    private sealed record ActivePlaceholder(
        long? JobId,
        long CycleId,
        string Status,
        string? Instrument,
        long? SnapshotId);

    private sealed class EvidenceChain
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;
        private readonly long _cycleId;
        private int _sequence;
        private string? _previousHash;

        public EvidenceChain(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long cycleId)
        {
            _connection = connection;
            _transaction = transaction;
            _cycleId = cycleId;
        }

        public async Task AppendAsync<T>(
            long? jobId,
            string phase,
            string kind,
            T payload,
            CancellationToken ct)
        {
            _sequence++;
            var payloadJson =
                TierZeroCanonicalJson.SerializeToString(payload);
            using var payloadDocument =
                JsonDocument.Parse(payloadJson);
            var currentHash = TierZeroCanonicalJson.Sha256Hex(
                TierZeroCanonicalJson.Serialize(new
                {
                    CycleId = _cycleId,
                    JobId = jobId,
                    Sequence = _sequence,
                    Phase = phase,
                    Kind = kind,
                    Payload =
                        payloadDocument.RootElement,
                    PreviousHash = _previousHash,
                }));
            await SnapshotGenerationRetentionRepository
                .AppendEvidenceAsync(
                    _connection,
                    _transaction,
                    _cycleId,
                    jobId,
                    _sequence,
                    phase,
                    kind,
                    payloadJson,
                    _previousHash,
                    currentHash,
                    ct);
            _previousHash = currentHash;
        }
    }
}
