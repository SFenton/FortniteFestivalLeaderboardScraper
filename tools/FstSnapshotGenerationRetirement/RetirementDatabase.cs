using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace FstSnapshotGenerationRetirement;

public interface IRetirementDatabase
{
    Task<RetirementStatus> ReadStatusAsync(
        RetirementCodeIdentity codeIdentity,
        CancellationToken ct = default);

    Task<SnapshotGenerationRetirementPolicy>
        AuthorizePolicyEpochAsync(
            RetirementAuthorizationRequest request,
            RetirementCodeIdentity codeIdentity,
            CancellationToken ct = default);

    Task<RetirementReconcileResult> ReconcileAsync(
        CancellationToken ct = default);

    Task<RetirementReconcileResult>
        DeactivatePolicyEpochAsync(
            CancellationToken ct = default);

    Task<SnapshotGenerationRetirementJob> PlanCycleAsync(
        RetirementCodeIdentity codeIdentity,
        CancellationToken ct = default);
}

public sealed class RetirementDatabase
    : IRetirementDatabase, IAsyncDisposable
{
    private const int CommandTimeoutSeconds = 20;
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    private RetirementDatabase(
        NpgsqlDataSource dataSource,
        bool ownsDataSource)
    {
        _dataSource = dataSource;
        _ownsDataSource = ownsDataSource;
    }

    public static RetirementDatabase FromEnvironment()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(
                SnapshotGenerationRetirementContract
                    .ConnectionEnvironment);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{SnapshotGenerationRetirementContract.ConnectionEnvironment} is required.");
        }
        return FromConnectionString(connectionString);
    }

    public static RetirementDatabase FromConnectionString(
        string connectionString)
    {
        var builder =
            new NpgsqlConnectionStringBuilder(
                connectionString)
            {
                ApplicationName =
                    "fst-snapshot-retirement-plan",
                CommandTimeout =
                    CommandTimeoutSeconds,
                Timeout = 10,
                SearchPath = "pg_catalog,public",
                IncludeErrorDetail = false,
                Options =
                    "-c idle_in_transaction_session_timeout=20s -c transaction_timeout=30s",
            };
        var dataSource =
            NpgsqlDataSource.Create(
                builder.ConnectionString);
        return new(dataSource, ownsDataSource: true);
    }

    public static RetirementDatabase FromDataSource(
        NpgsqlDataSource dataSource) =>
        new(dataSource, ownsDataSource: false);

    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
            await _dataSource.DisposeAsync();
    }

    public async Task<bool> IsSchemaInitializedAsync(
        CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var command =
            connection.CreateCommand();
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                pg_catalog.to_regclass(
                    'public.snapshot_generation_retirement_control')
                    IS NOT NULL
                AND pg_catalog.to_regclass(
                    'public.snapshot_generation_retirement_policy_epochs')
                    IS NOT NULL
                AND pg_catalog.to_regclass(
                    'public.snapshot_generation_retirement_jobs')
                    IS NOT NULL
                AND pg_catalog.to_regclass(
                    'public.snapshot_generation_retirement_events')
                    IS NOT NULL
            """;
        return (bool)(
            await command.ExecuteScalarAsync(ct)
            ?? false);
    }

    public async Task<RetirementDatabaseIdentity>
        ReadDatabaseIdentityAsync(
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        return await ReadDatabaseIdentityAsync(
            connection,
            transaction: null,
            ct);
    }

    public async Task<RetirementStatus> ReadStatusAsync(
        RetirementCodeIdentity codeIdentity,
        CancellationToken ct = default)
    {
        codeIdentity.Validate();
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                ct);
        await ConfigureTransactionAsync(
            connection,
            transaction,
            readOnly: true,
            ct);
        var databaseIdentity =
            await ReadDatabaseIdentityAsync(
                connection,
                transaction,
                ct);
        var schemaInitialized =
            await IsSchemaInitializedAsync(
                connection,
                transaction,
                ct);
        var controlSchemaSha256 =
            schemaInitialized
                ? await ReadControlSchemaFingerprintAsync(
                    connection,
                    transaction,
                    ct)
                : new string('0', 64);
        var runtimeIdentity =
            BuildRuntimeIdentity(
                codeIdentity,
                databaseIdentity,
                controlSchemaSha256);
        if (!schemaInitialized)
        {
            await transaction.CommitAsync(ct);
            return new(
                false,
                SnapshotGenerationRetirementContract
                    .HonestClaim,
                runtimeIdentity,
                null,
                null,
                null,
                null,
                null);
        }

        var control = await ReadControlAsync(
            connection,
            transaction,
            forUpdate: false,
            ct);
        SnapshotGenerationRetirementPolicy?
            activePolicy = null;
        if (control.ActivePolicyEpochId is { } policyId)
        {
            activePolicy = await ReadPolicyAsync(
                connection,
                transaction,
                policyId,
                forUpdate: false,
                ct);
        }
        var latestPolicy = await ReadLatestPolicyAsync(
            connection,
            transaction,
            ct);
        var activeJob = await ReadPlannedJobAsync(
            connection,
            transaction,
            forUpdate: false,
            ct);
        var latestJob = await ReadLatestJobAsync(
            connection,
            transaction,
            ct);
        await transaction.CommitAsync(ct);
        return new(
            true,
            SnapshotGenerationRetirementContract
                .HonestClaim,
            runtimeIdentity,
            control,
            activePolicy,
            latestPolicy,
            activeJob,
            latestJob);
    }

    public async Task<SnapshotGenerationRetirementPolicy>
        AuthorizePolicyEpochAsync(
            RetirementAuthorizationRequest request,
            RetirementCodeIdentity codeIdentity,
            CancellationToken ct = default)
    {
        request = request.Normalize();
        request.Validate();
        codeIdentity.Validate();
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                ct);
        await ConfigureTransactionAsync(
            connection,
            transaction,
            readOnly: false,
            ct);
        await RequireSchemaInitializedAsync(
            connection,
            transaction,
            ct);
        var databaseIdentity =
            await ReadDatabaseIdentityAsync(
                connection,
                transaction,
                ct);
        var runtimeIdentity =
            BuildRuntimeIdentity(
                codeIdentity,
                databaseIdentity,
                await ReadControlSchemaFingerprintAsync(
                    connection,
                    transaction,
                    ct));
        request.RequireExactIdentity(
            runtimeIdentity);

        var control = await ReadControlAsync(
            connection,
            transaction,
            forUpdate: true,
            ct);
        var now = await ReadNowAsync(
            connection,
            transaction,
            ct);
        if (request.ExpiresAt <= now)
        {
            throw new InvalidOperationException(
                "The requested retirement policy already expired.");
        }
        if (control.Enabled
            && control.ActivePolicyEpochId is
                { } activePolicyId)
        {
            var activePolicy =
                await ReadPolicyAsync(
                    connection,
                    transaction,
                    activePolicyId,
                    forUpdate: true,
                    ct)
                ?? throw new InvalidOperationException(
                    "The active retirement policy is missing.");
            var activeJob =
                await ReadPlannedJobAsync(
                    connection,
                    transaction,
                    forUpdate: true,
                    ct);
            if (activeJob is not null
                && activeJob.PolicyEpochId !=
                    activePolicy.PolicyEpochId)
            {
                throw new InvalidOperationException(
                    "The active retirement job is bound to a different policy.");
            }
            var usage = await ReadPolicyUsageAsync(
                connection,
                transaction,
                activePolicy.PolicyEpochId,
                ct);
            if (now < activePolicy.ExpiresAt
                && (activeJob is not null
                    || usage.JobCount <
                        activePolicy.MaxJobs))
            {
                throw new InvalidOperationException(
                    "A retirement policy is already active.");
            }
            if (activeJob is not null)
            {
                await TransitionJobAsync(
                    connection,
                    transaction,
                    activeJob,
                    "expired",
                    "policy_expired",
                    now,
                    ct);
            }
            await DeactivatePolicyAsync(
                connection,
                transaction,
                activePolicy,
                now >= activePolicy.ExpiresAt
                    ? "policy_expired"
                    : "policy_job_budget_exhausted",
                now,
                ct);
        }

        var policyId = Guid.NewGuid();
        var digest = RetirementJson.Sha256(
            new RetirementPolicyDigestInput(
                SnapshotGenerationRetirementContract
                    .SchemaVersion,
                SnapshotGenerationRetirementContract
                    .ToolId,
                SnapshotGenerationRetirementContract
                    .StagePlan,
                request,
                runtimeIdentity,
                now));
        await InsertPolicyAsync(
            connection,
            transaction,
            policyId,
            request,
            runtimeIdentity,
            now,
            digest,
            ct);
        var policy = new SnapshotGenerationRetirementPolicy(
            policyId,
            request.NotBefore,
            request.ExpiresAt,
            request.MaxJobs,
            request.MaxTotalBytes,
            runtimeIdentity,
            request.ApprovedBy.Trim(),
            request.ReviewedBy.Trim(),
            request.ApprovalReference.Trim(),
            now,
            digest);
        await AppendEventAsync(
            connection,
            transaction,
            policyId,
            jobId: null,
            "policy_authorized",
            new
            {
                policyId,
                stageCeiling =
                    SnapshotGenerationRetirementContract
                        .StagePlan,
                request.NotBefore,
                request.ExpiresAt,
                request.MaxJobs,
                request.MaxTotalBytes,
                policy.PolicyDigest,
                sourceIdentitySha256 =
                    runtimeIdentity
                        .SourceIdentitySha256,
            },
            ct);
        await UpdateControlAsync(
            connection,
            transaction,
            enabled: true,
            policyId,
            "authorize-policy-epoch",
            now,
            ct);
        await transaction.CommitAsync(ct);
        return policy;
    }

    public async Task<RetirementReconcileResult>
        ReconcileAsync(
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                ct);
        await ConfigureTransactionAsync(
            connection,
            transaction,
            readOnly: false,
            ct);
        await RequireSchemaInitializedAsync(
            connection,
            transaction,
            ct);
        await AcquirePlanningSnapshotFenceAsync(
            connection,
            transaction,
            ct);
        var control = await ReadControlAsync(
            connection,
            transaction,
            forUpdate: true,
            ct);
        if (!control.Enabled
            || control.ActivePolicyEpochId is null)
        {
            await transaction.CommitAsync(ct);
            return new(
                "disabled",
                SnapshotGenerationRetirementContract
                    .HonestClaim,
                null,
                null);
        }

        var policy = await ReadPolicyAsync(
            connection,
            transaction,
            control.ActivePolicyEpochId.Value,
            forUpdate: true,
            ct)
            ?? throw new InvalidOperationException(
                "The active retirement policy is missing.");
        var job = await ReadPlannedJobAsync(
            connection,
            transaction,
            forUpdate: true,
            ct);
        if (job is not null
            && job.PolicyEpochId !=
                policy.PolicyEpochId)
        {
            throw new InvalidOperationException(
                "The active retirement job is bound to a different policy.");
        }
        var now = await ReadNowAsync(
            connection,
            transaction,
            ct);

        if (now >= policy.ExpiresAt)
        {
            if (job is not null)
            {
                job = await TransitionJobAsync(
                    connection,
                    transaction,
                    job,
                    "expired",
                    "policy_expired",
                    now,
                    ct);
            }
            await DeactivatePolicyAsync(
                connection,
                transaction,
                policy,
                "policy_expired",
                now,
                ct);
            await transaction.CommitAsync(ct);
            return new(
                "policy_expired",
                SnapshotGenerationRetirementContract
                    .HonestClaim,
                policy,
                job);
        }

        if (job is not null)
        {
            var staleReason =
                await ReadJobStaleReasonAsync(
                    connection,
                    transaction,
                    job,
                    ct);
            if (staleReason is not null)
            {
                var transitionAt =
                    await ReadNowAsync(
                        connection,
                        transaction,
                        ct);
                if (transitionAt >= policy.ExpiresAt)
                {
                    job = await TransitionJobAsync(
                        connection,
                        transaction,
                        job,
                        "expired",
                        "policy_expired",
                        transitionAt,
                        ct);
                    await DeactivatePolicyAsync(
                        connection,
                        transaction,
                        policy,
                        "policy_expired",
                        transitionAt,
                        ct);
                    await transaction.CommitAsync(ct);
                    return new(
                        "policy_expired",
                        SnapshotGenerationRetirementContract
                            .HonestClaim,
                        policy,
                        job);
                }
                job = await TransitionJobAsync(
                    connection,
                    transaction,
                    job,
                    "superseded",
                    staleReason,
                    transitionAt,
                    ct);
                await transaction.CommitAsync(ct);
                return new(
                    "job_superseded",
                    SnapshotGenerationRetirementContract
                        .HonestClaim,
                    policy,
                    job);
            }
        }

        var jobCount = await ReadPolicyJobCountAsync(
            connection,
            transaction,
            policy.PolicyEpochId,
            ct);
        var finalNow = await ReadNowAsync(
            connection,
            transaction,
            ct);
        if (finalNow >= policy.ExpiresAt)
        {
            if (job is not null)
            {
                job = await TransitionJobAsync(
                    connection,
                    transaction,
                    job,
                    "expired",
                    "policy_expired",
                    finalNow,
                    ct);
            }
            await DeactivatePolicyAsync(
                connection,
                transaction,
                policy,
                "policy_expired",
                finalNow,
                ct);
            await transaction.CommitAsync(ct);
            return new(
                "policy_expired",
                SnapshotGenerationRetirementContract
                    .HonestClaim,
                policy,
                job);
        }
        if (job is null
            && jobCount >= policy.MaxJobs)
        {
            await DeactivatePolicyAsync(
                connection,
                transaction,
                policy,
                "policy_job_budget_exhausted",
                finalNow,
                ct);
            await transaction.CommitAsync(ct);
            return new(
                "policy_job_budget_exhausted",
                SnapshotGenerationRetirementContract
                    .HonestClaim,
                policy,
                null);
        }

        await transaction.CommitAsync(ct);
        return new(
            "no_change",
            SnapshotGenerationRetirementContract
                .HonestClaim,
            policy,
            job);
    }

    public async Task<RetirementReconcileResult>
        DeactivatePolicyEpochAsync(
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                ct);
        await ConfigureTransactionAsync(
            connection,
            transaction,
            readOnly: false,
            ct);
        await RequireSchemaInitializedAsync(
            connection,
            transaction,
            ct);
        var control = await ReadControlAsync(
            connection,
            transaction,
            forUpdate: true,
            ct);
        if (!control.Enabled
            || control.ActivePolicyEpochId is null)
        {
            await transaction.CommitAsync(ct);
            return new(
                "disabled",
                SnapshotGenerationRetirementContract
                    .HonestClaim,
                null,
                null);
        }

        var policy = await ReadPolicyAsync(
            connection,
            transaction,
            control.ActivePolicyEpochId.Value,
            forUpdate: true,
            ct)
            ?? throw new InvalidOperationException(
                "The active retirement policy is missing.");
        var job = await ReadPlannedJobAsync(
            connection,
            transaction,
            forUpdate: true,
            ct);
        if (job is not null)
        {
            if (job.PolicyEpochId !=
                policy.PolicyEpochId)
            {
                throw new InvalidOperationException(
                    "The active retirement job is bound to a different policy.");
            }
            var now = await ReadNowAsync(
                connection,
                transaction,
                ct);
            job = await TransitionJobAsync(
                connection,
                transaction,
                job,
                "superseded",
                "operator_deactivated",
                now,
                ct);
        }
        var deactivatedAt = await ReadNowAsync(
            connection,
            transaction,
            ct);
        await DeactivatePolicyAsync(
            connection,
            transaction,
            policy,
            "operator_deactivated",
            deactivatedAt,
            ct);
        await transaction.CommitAsync(ct);
        return new(
            "operator_deactivated",
            SnapshotGenerationRetirementContract
                .HonestClaim,
            policy,
            job);
    }

    public async Task<SnapshotGenerationRetirementJob>
        PlanCycleAsync(
            RetirementCodeIdentity codeIdentity,
            CancellationToken ct = default)
    {
        codeIdentity.Validate();
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                ct);
        await ConfigureTransactionAsync(
            connection,
            transaction,
            readOnly: false,
            ct);
        await RequireSchemaInitializedAsync(
            connection,
            transaction,
            ct);
        await AcquirePlanningSnapshotFenceAsync(
            connection,
            transaction,
            ct);
        var databaseIdentity =
            await ReadDatabaseIdentityAsync(
                connection,
                transaction,
                ct);
        var runtimeIdentity =
            BuildRuntimeIdentity(
                codeIdentity,
                databaseIdentity,
                await ReadControlSchemaFingerprintAsync(
                    connection,
                    transaction,
                    ct));
        var control = await ReadControlAsync(
            connection,
            transaction,
            forUpdate: true,
            ct);
        if (!control.Enabled
            || control.ActivePolicyEpochId is null)
        {
            throw new InvalidOperationException(
                "Snapshot-generation retirement planning is disabled.");
        }
        var policy = await ReadPolicyAsync(
            connection,
            transaction,
            control.ActivePolicyEpochId.Value,
            forUpdate: true,
            ct)
            ?? throw new InvalidOperationException(
                "The active retirement policy is missing.");
        var activeJob = await ReadPlannedJobAsync(
            connection,
            transaction,
            forUpdate: true,
            ct);
        if (activeJob is not null
            && activeJob.PolicyEpochId !=
                policy.PolicyEpochId)
        {
            throw new InvalidOperationException(
                "The active retirement job is bound to a different policy.");
        }
        var now = await ReadNowAsync(
            connection,
            transaction,
            ct);
        if (now >= policy.ExpiresAt)
        {
            if (activeJob is not null)
            {
                await TransitionJobAsync(
                    connection,
                    transaction,
                    activeJob,
                    "expired",
                    "policy_expired",
                    now,
                    ct);
            }
            await DeactivatePolicyAsync(
                connection,
                transaction,
                policy,
                "policy_expired",
                now,
                ct);
            await transaction.CommitAsync(ct);
            throw new InvalidOperationException(
                "The retirement policy expired.");
        }
        RequirePolicyRuntime(
            policy,
            runtimeIdentity,
            now);
        if (activeJob is not null)
        {
            var staleReason =
                await ReadJobStaleReasonAsync(
                    connection,
                    transaction,
                    activeJob,
                    ct);
            if (staleReason is null)
            {
                var confirmedAt = await ReadNowAsync(
                    connection,
                    transaction,
                    ct);
                if (confirmedAt >= policy.ExpiresAt)
                {
                    var expiredJob =
                        await TransitionJobAsync(
                            connection,
                            transaction,
                            activeJob,
                            "expired",
                            "policy_expired",
                            confirmedAt,
                            ct);
                    await DeactivatePolicyAsync(
                        connection,
                        transaction,
                        policy,
                        "policy_expired",
                        confirmedAt,
                        ct);
                    await transaction.CommitAsync(ct);
                    throw new InvalidOperationException(
                        $"The retirement policy expired; job {expiredJob.JobId} was terminalized.");
                }
                await transaction.CommitAsync(ct);
                return activeJob;
            }
            var transitionAt =
                await ReadNowAsync(
                    connection,
                    transaction,
                    ct);
            if (transitionAt >= policy.ExpiresAt)
            {
                await TransitionJobAsync(
                    connection,
                    transaction,
                    activeJob,
                    "expired",
                    "policy_expired",
                    transitionAt,
                    ct);
                await DeactivatePolicyAsync(
                    connection,
                    transaction,
                    policy,
                    "policy_expired",
                    transitionAt,
                    ct);
                await transaction.CommitAsync(ct);
                throw new InvalidOperationException(
                    "The retirement policy expired.");
            }
            await TransitionJobAsync(
                connection,
                transaction,
                activeJob,
                "superseded",
                staleReason,
                transitionAt,
                ct);
            await transaction.CommitAsync(ct);
            throw new InvalidOperationException(
                "The stale retirement plan was superseded; rerun plan-cycle against the current cycle.");
        }

        var usage = await ReadPolicyUsageAsync(
            connection,
            transaction,
            policy.PolicyEpochId,
            ct);
        if (usage.JobCount >= policy.MaxJobs)
        {
            await DeactivatePolicyAsync(
                connection,
                transaction,
                policy,
                "policy_job_budget_exhausted",
                now,
                ct);
            await transaction.CommitAsync(ct);
            throw new InvalidOperationException(
                "The retirement policy job budget is exhausted.");
        }
        var target = await SelectTargetAsync(
            connection,
            transaction,
            ct);
        if (target is null)
        {
            await transaction.CommitAsync(ct);
            throw new InvalidOperationException(
                "The newest retention cycle has no accepted eligible target.");
        }
        var budgetDecisionAt = await ReadNowAsync(
            connection,
            transaction,
            ct);
        if (budgetDecisionAt >= policy.ExpiresAt)
        {
            await DeactivatePolicyAsync(
                connection,
                transaction,
                policy,
                "policy_expired",
                budgetDecisionAt,
                ct);
            await transaction.CommitAsync(ct);
            throw new InvalidOperationException(
                "The retirement policy expired.");
        }
        if (usage.TotalBytes >
            policy.MaxTotalBytes -
            target.TargetBytes)
        {
            await DeactivatePolicyAsync(
                connection,
                transaction,
                policy,
                "policy_byte_budget_exhausted",
                budgetDecisionAt,
                ct);
            await transaction.CommitAsync(ct);
            throw new InvalidOperationException(
                "The retirement policy byte budget is exhausted.");
        }

        var plannedAt = await ReadNowAsync(
            connection,
            transaction,
            ct);
        if (plannedAt >= policy.ExpiresAt)
        {
            await DeactivatePolicyAsync(
                connection,
                transaction,
                policy,
                "policy_expired",
                plannedAt,
                ct);
            await transaction.CommitAsync(ct);
            throw new InvalidOperationException(
                "The retirement policy expired.");
        }
        var jobId = Guid.NewGuid();
        var planDigest = RetirementJson.Sha256(
            new RetirementPlanDigestInput(
                SnapshotGenerationRetirementContract
                    .SchemaVersion,
                SnapshotGenerationRetirementContract
                    .ToolId,
                policy.PolicyEpochId,
                target,
                runtimeIdentity.SourceIdentitySha256,
                plannedAt));
        var job = await InsertJobAsync(
            connection,
            transaction,
            jobId,
            policy.PolicyEpochId,
            target,
            runtimeIdentity.SourceIdentitySha256,
            planDigest,
            plannedAt,
            ct);
        await AppendEventAsync(
            connection,
            transaction,
            policy.PolicyEpochId,
            jobId,
            "job_planned",
            new
            {
                jobId,
                target.CycleId,
                target.ObservationId,
                target.TriggerScrapeId,
                target.TriggerPublicationId,
                target.Instrument,
                target.SnapshotId,
                target.ChildOid,
                target.ChildRelfilenode,
                target.TargetBytes,
                ordering =
                    "current_bytes_desc,snapshot_id,instrument_order,child_oid",
                excluded = "Solo_Bass/1308",
                planDigest,
            },
            ct);
        await transaction.CommitAsync(ct);
        return job;
    }

    private static async Task ConfigureTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        bool readOnly,
        CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText = readOnly
            ? """
              SET TRANSACTION READ ONLY;
              SET LOCAL search_path TO pg_catalog, public;
              SET LOCAL lock_timeout TO '2s';
              SET LOCAL statement_timeout TO '15s';
              SET LOCAL idle_in_transaction_session_timeout TO '20s';
              SET LOCAL transaction_timeout TO '30s';
              SELECT pg_catalog.pg_advisory_xact_lock_shared(
                  @schemaLockKey);
              """
            : """
              SET LOCAL search_path TO pg_catalog, public;
              SET LOCAL lock_timeout TO '2s';
              SET LOCAL statement_timeout TO '15s';
              SET LOCAL idle_in_transaction_session_timeout TO '20s';
              SET LOCAL transaction_timeout TO '30s';
              SELECT pg_catalog.pg_advisory_xact_lock_shared(
                  @schemaLockKey);
              """;
        command.Parameters.AddWithValue(
            "schemaLockKey",
            SnapshotGenerationRetirementContract
                .SchemaAdvisoryLockKey);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool>
        IsSchemaInitializedAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                pg_catalog.to_regclass(
                    'public.snapshot_generation_retirement_control')
                    IS NOT NULL
                AND pg_catalog.to_regclass(
                    'public.snapshot_generation_retirement_policy_epochs')
                    IS NOT NULL
                AND pg_catalog.to_regclass(
                    'public.snapshot_generation_retirement_jobs')
                    IS NOT NULL
                AND pg_catalog.to_regclass(
                    'public.snapshot_generation_retirement_events')
                    IS NOT NULL
            """;
        return (bool)(
            await command.ExecuteScalarAsync(ct)
            ?? false);
    }

    private static async Task RequireSchemaInitializedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        if (!await IsSchemaInitializedAsync(
                connection,
                transaction,
                ct))
        {
            throw new InvalidOperationException(
                "Snapshot-generation retirement control-plane schema is not initialized.");
        }
    }

    private static async Task<DateTimeOffset>
        ReadNowAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText =
            "SELECT pg_catalog.clock_timestamp()";
        return ToDateTimeOffset(
            await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException(
                "PostgreSQL did not return a current timestamp."));
    }

    private static async Task
        AcquirePlanningSnapshotFenceAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText = """
            SELECT pg_catalog.pg_advisory_xact_lock_shared(
                @publicationLockKey);
            SELECT pg_catalog.pg_advisory_xact_lock_shared(
                @plannerLockKey);
            SELECT pg_catalog.pg_advisory_xact_lock_shared(
                pg_catalog.hashtextextended(
                    'fst.snapshot-generation-partition-ddl',
                    0));
            LOCK TABLE ONLY
                public.scrape_writer_failures
            IN SHARE MODE;
            LOCK TABLE ONLY
                public.snapshot_generation_retention_holds
            IN SHARE MODE;
            LOCK TABLE ONLY
                public.service_worker_status
            IN SHARE MODE;
            SELECT state.id
            FROM public.scrape_publication_state state
            WHERE state.id = TRUE
            FOR SHARE;
            """;
        command.Parameters.AddWithValue(
            "publicationLockKey",
            SnapshotGenerationRetirementContract
                .PublicationAdvisoryLockKey);
        command.Parameters.AddWithValue(
            "plannerLockKey",
            SnapshotGenerationRetirementContract
                .PlannerAdvisoryLockKey);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<RetirementDatabaseIdentity>
        ReadDatabaseIdentityAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                pg_catalog.current_database(),
                database.oid::BIGINT,
                control.system_identifier::TEXT,
                pg_catalog.current_setting(
                    'server_version_num')::INTEGER,
                pg_catalog.current_setting(
                    'data_directory'),
                pg_catalog.pg_postmaster_start_time()
            FROM pg_catalog.pg_database database
            CROSS JOIN pg_catalog.pg_control_system()
                control
            WHERE database.datname =
                    pg_catalog.current_database()
            """;
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                "PostgreSQL source identity is unavailable.");
        }
        var identity = new RetirementDatabaseIdentity(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4),
            ReadTimestamp(reader, 5));
        identity.Validate();
        return identity;
    }

    private static async Task<string>
        ReadControlSchemaFingerprintAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText = """
            WITH managed_tables(table_name) AS (
                VALUES
                    ('snapshot_generation_retirement_policy_epochs'),
                    ('snapshot_generation_retirement_control'),
                    ('snapshot_generation_retirement_jobs'),
                    ('snapshot_generation_retirement_events')
            ),
            managed_relations AS (
                SELECT relation.oid,
                       relation.relname,
                       relation.relkind,
                       relation.relpersistence
                FROM managed_tables managed
                JOIN pg_catalog.pg_class relation
                  ON relation.relname =
                        managed.table_name
                JOIN pg_catalog.pg_namespace namespace
                  ON namespace.oid =
                        relation.relnamespace
                 AND namespace.nspname = 'public'
            ),
            objects AS (
                SELECT
                    'relation'::TEXT AS object_kind,
                    relation.relname AS object_name,
                    relation.relkind::TEXT || '|'
                        || relation.relpersistence::TEXT
                        AS definition
                FROM managed_relations relation

                UNION ALL

                SELECT
                    'column',
                    relation.relname || '.'
                        || attribute.attnum::TEXT,
                    attribute.attname || '|'
                        || pg_catalog.format_type(
                            attribute.atttypid,
                            attribute.atttypmod)
                        || '|'
                        || attribute.attnotnull::TEXT
                        || '|'
                        || COALESCE(
                            pg_catalog.pg_get_expr(
                                default_value.adbin,
                                default_value.adrelid),
                            '')
                FROM managed_relations relation
                JOIN pg_catalog.pg_attribute attribute
                  ON attribute.attrelid = relation.oid
                 AND attribute.attnum > 0
                 AND NOT attribute.attisdropped
                LEFT JOIN pg_catalog.pg_attrdef default_value
                  ON default_value.adrelid =
                        attribute.attrelid
                 AND default_value.adnum =
                        attribute.attnum

                UNION ALL

                SELECT
                    'constraint',
                    relation.relname || '.'
                        || constraint_row.conname,
                    pg_catalog.pg_get_constraintdef(
                        constraint_row.oid,
                        TRUE)
                FROM managed_relations relation
                JOIN pg_catalog.pg_constraint constraint_row
                  ON constraint_row.conrelid =
                        relation.oid

                UNION ALL

                SELECT
                    'index',
                    index_relation.relname,
                    index_row.indisvalid::TEXT || '|'
                        || index_row.indisready::TEXT || '|'
                        || index_row.indislive::TEXT || '|'
                        || index_row.indisunique::TEXT || '|'
                        || index_row.indisprimary::TEXT || '|'
                        || pg_catalog.pg_get_indexdef(
                            index_relation.oid)
                FROM managed_relations relation
                JOIN pg_catalog.pg_index index_row
                  ON index_row.indrelid = relation.oid
                JOIN pg_catalog.pg_class index_relation
                  ON index_relation.oid =
                        index_row.indexrelid

                UNION ALL

                SELECT
                    'trigger',
                    relation.relname || '.'
                        || trigger_row.tgname,
                    trigger_row.tgenabled::TEXT || '|'
                        || pg_catalog.pg_get_triggerdef(
                            trigger_row.oid,
                            TRUE)
                FROM managed_relations relation
                JOIN pg_catalog.pg_trigger trigger_row
                  ON trigger_row.tgrelid = relation.oid
                 AND NOT trigger_row.tgisinternal

                UNION ALL

                SELECT
                    'function',
                    procedure.oid::REGPROCEDURE::TEXT,
                    pg_catalog.pg_get_functiondef(
                        procedure.oid)
                FROM pg_catalog.pg_proc procedure
                JOIN pg_catalog.pg_namespace namespace
                  ON namespace.oid =
                        procedure.pronamespace
                 AND namespace.nspname = 'public'
                WHERE procedure.proname IN (
                    'fst_reject_snapshot_generation_retirement_immutable_mutation',
                    'fst_snapshot_generation_retirement_index_configuration',
                    'fst_lock_snapshot_generation_retirement_plan_target',
                    'fst_validate_snapshot_generation_retirement_job_insert',
                    'fst_guard_snapshot_generation_retirement_job_update')
            )
            SELECT object_kind,
                   object_name,
                   definition
            FROM objects
            ORDER BY object_kind,
                     object_name,
                     definition
            """;
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        using var hash =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        var count = 0;
        while (await reader.ReadAsync(ct))
        {
            AppendFingerprintPart(
                hash,
                reader.GetString(0));
            AppendFingerprintPart(
                hash,
                reader.GetString(1));
            AppendFingerprintPart(
                hash,
                reader.GetString(2));
            count++;
        }
        if (count == 0)
        {
            throw new InvalidOperationException(
                "The installed retirement control schema fingerprint is empty.");
        }
        return Convert.ToHexString(
                hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static void AppendFingerprintPart(
        IncrementalHash hash,
        string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var length = BitConverter.GetBytes(
            bytes.Length);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static RetirementRuntimeIdentity
        BuildRuntimeIdentity(
            RetirementCodeIdentity codeIdentity,
            RetirementDatabaseIdentity databaseIdentity,
            string controlSchemaSha256)
    {
        var identity = new RetirementRuntimeIdentity(
            codeIdentity,
            databaseIdentity,
            controlSchemaSha256,
            databaseIdentity.ComputeDigest());
        identity.Validate();
        return identity;
    }

    private static async Task<RetirementControlState>
        ReadControlAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            bool forUpdate,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                control.enabled,
                control.active_policy_epoch_id,
                control.updated_by,
                control.updated_at
            FROM public.snapshot_generation_retirement_control
                control
            WHERE control.control_key = TRUE
            """;
        if (forUpdate)
            command.CommandText += "\nFOR UPDATE";
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                "Snapshot-generation retirement control row is missing.");
        }
        return new(
            reader.GetBoolean(0),
            reader.IsDBNull(1)
                ? null
                : reader.GetGuid(1),
            reader.GetString(2),
            ReadTimestamp(reader, 3));
    }

    private static async Task<
        SnapshotGenerationRetirementPolicy?>
        ReadPolicyAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid policyId,
            bool forUpdate,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                policy.policy_epoch_id,
                policy.not_before,
                policy.expires_at,
                policy.max_jobs,
                policy.max_total_bytes,
                policy.repository_commit,
                policy.repository_tree,
                policy.supervisor_binary_sha256,
                policy.supervisor_source_sha256,
                policy.wrapper_sha256,
                policy.control_schema_sha256,
                policy.source_database_name,
                policy.source_database_oid,
                policy.source_system_identifier,
                policy.source_server_version_num,
                policy.source_data_directory,
                policy.source_postmaster_started_at,
                policy.source_identity_sha256,
                policy.approved_by,
                policy.reviewed_by,
                policy.approval_reference,
                policy.authorized_at,
                policy.policy_digest
            FROM public.snapshot_generation_retirement_policy_epochs
                policy
            WHERE policy.policy_epoch_id = @policyId
            """;
        if (forUpdate)
            command.CommandText += "\nFOR UPDATE";
        command.Parameters.AddWithValue(
            "policyId",
            policyId);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return ReadPolicy(reader);
    }

    private static SnapshotGenerationRetirementPolicy
        ReadPolicy(NpgsqlDataReader reader)
    {
        var databaseIdentity =
            new RetirementDatabaseIdentity(
                reader.GetString(11),
                reader.GetInt64(12),
                reader.GetString(13),
                reader.GetInt32(14),
                reader.GetString(15),
                ReadTimestamp(reader, 16));
        var codeIdentity =
            new RetirementCodeIdentity(
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9));
        var runtimeIdentity =
            new RetirementRuntimeIdentity(
                codeIdentity,
                databaseIdentity,
                reader.GetString(10),
                reader.GetString(17));
        runtimeIdentity.Validate();
        return new(
            reader.GetGuid(0),
            ReadTimestamp(reader, 1),
            ReadTimestamp(reader, 2),
            reader.GetInt32(3),
            reader.GetInt64(4),
            runtimeIdentity,
            reader.GetString(18),
            reader.GetString(19),
            reader.GetString(20),
            ReadTimestamp(reader, 21),
            reader.GetString(22));
    }

    private static async Task<
        SnapshotGenerationRetirementPolicy?>
        ReadLatestPolicyAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                policy.policy_epoch_id,
                policy.not_before,
                policy.expires_at,
                policy.max_jobs,
                policy.max_total_bytes,
                policy.repository_commit,
                policy.repository_tree,
                policy.supervisor_binary_sha256,
                policy.supervisor_source_sha256,
                policy.wrapper_sha256,
                policy.control_schema_sha256,
                policy.source_database_name,
                policy.source_database_oid,
                policy.source_system_identifier,
                policy.source_server_version_num,
                policy.source_data_directory,
                policy.source_postmaster_started_at,
                policy.source_identity_sha256,
                policy.approved_by,
                policy.reviewed_by,
                policy.approval_reference,
                policy.authorized_at,
                policy.policy_digest
            FROM public.snapshot_generation_retirement_policy_epochs
                policy
            ORDER BY policy.created_at DESC,
                     policy.policy_epoch_id DESC
            LIMIT 1
            """;
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? ReadPolicy(reader)
            : null;
    }

    private static async Task<
        SnapshotGenerationRetirementJob?>
        ReadPlannedJobAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            bool forUpdate,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText =
            JobSelectSql
            + """

              WHERE job.state = 'planned'
              ORDER BY job.created_at, job.job_id
              LIMIT 1
              """;
        if (forUpdate)
            command.CommandText += "\nFOR UPDATE";
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? ReadJob(reader)
            : null;
    }

    private static async Task<
        SnapshotGenerationRetirementJob?>
        ReadLatestJobAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText =
            JobSelectSql
            + """

              ORDER BY job.created_at DESC, job.job_id DESC
              LIMIT 1
              """;
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? ReadJob(reader)
            : null;
    }

    private const string JobSelectSql = """
        SELECT
            job.job_id,
            job.policy_epoch_id,
            job.cycle_id,
            job.observation_id,
            job.trigger_scrape_id,
            job.trigger_publication_id,
            job.instrument,
            job.instrument_order,
            job.snapshot_id,
            job.root_schema,
            job.root_relation,
            job.root_oid,
            job.child_schema,
            job.child_relation,
            job.child_oid,
            job.child_relfilenode,
            job.stable_child_identity_hash,
            job.stable_config_schema_hash,
            job.target_bytes,
            job.source_identity_sha256,
            job.plan_digest,
            job.state,
            job.state_reason,
            job.planned_at,
            job.terminal_at,
            job.created_at,
            job.updated_at
        FROM public.snapshot_generation_retirement_jobs job
        """;

    private static SnapshotGenerationRetirementJob ReadJob(
        NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetInt16(7),
            reader.GetInt64(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetInt64(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetInt64(14),
            reader.GetInt64(15),
            reader.GetString(16),
            reader.GetString(17),
            reader.GetInt64(18),
            reader.GetString(19),
            reader.GetString(20),
            reader.GetString(21),
            reader.IsDBNull(22)
                ? null
                : reader.GetString(22),
            ReadTimestamp(reader, 23),
            reader.IsDBNull(24)
                ? null
                : ReadTimestamp(reader, 24),
            ReadTimestamp(reader, 25),
            ReadTimestamp(reader, 26));

    private static DateTimeOffset ReadTimestamp(
        NpgsqlDataReader reader,
        int ordinal) =>
        ToDateTimeOffset(reader.GetValue(ordinal));

    private static DateTimeOffset ToDateTimeOffset(
        object value) =>
        value switch
        {
            DateTimeOffset offset =>
                offset.ToUniversalTime(),
            DateTime dateTime =>
                new DateTimeOffset(
                    dateTime.Kind ==
                        DateTimeKind.Utc
                        ? dateTime
                        : DateTime.SpecifyKind(
                            dateTime,
                            DateTimeKind.Utc)),
            _ => throw new InvalidDataException(
                "PostgreSQL returned an invalid timestamp."),
        };

    private static async Task InsertPolicyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid policyId,
        RetirementAuthorizationRequest request,
        RetirementRuntimeIdentity runtimeIdentity,
        DateTimeOffset authorizedAt,
        string digest,
        CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText = """
            INSERT INTO
                public.snapshot_generation_retirement_policy_epochs (
                    policy_epoch_id,
                    schema_version,
                    tool_id,
                    stage_ceiling,
                    not_before,
                    expires_at,
                    max_jobs,
                    max_total_bytes,
                    repository_commit,
                    repository_tree,
                    supervisor_binary_sha256,
                    supervisor_source_sha256,
                    wrapper_sha256,
                    control_schema_sha256,
                    source_database_name,
                    source_database_oid,
                    source_system_identifier,
                    source_server_version_num,
                    source_data_directory,
                    source_postmaster_started_at,
                    source_identity_sha256,
                    approved_by,
                    reviewed_by,
                    approval_reference,
                    authorized_at,
                    policy_digest)
            VALUES (
                @policyId,
                @schemaVersion,
                @toolId,
                @stageCeiling,
                @notBefore,
                @expiresAt,
                @maxJobs,
                @maxTotalBytes,
                @repositoryCommit,
                @repositoryTree,
                @binarySha,
                @sourceSha,
                @wrapperSha,
                @controlSchemaSha,
                @databaseName,
                @databaseOid,
                @systemIdentifier,
                @serverVersion,
                @dataDirectory,
                @postmasterStartedAt,
                @sourceIdentitySha,
                @approvedBy,
                @reviewedBy,
                @approvalReference,
                @authorizedAt,
                @policyDigest)
            """;
        command.Parameters.AddWithValue(
            "policyId",
            policyId);
        command.Parameters.AddWithValue(
            "schemaVersion",
            SnapshotGenerationRetirementContract
                .SchemaVersion);
        command.Parameters.AddWithValue(
            "toolId",
            SnapshotGenerationRetirementContract
                .ToolId);
        command.Parameters.AddWithValue(
            "stageCeiling",
            SnapshotGenerationRetirementContract
                .StagePlan);
        command.Parameters.AddWithValue(
            "notBefore",
            request.NotBefore);
        command.Parameters.AddWithValue(
            "expiresAt",
            request.ExpiresAt);
        command.Parameters.AddWithValue(
            "maxJobs",
            request.MaxJobs);
        command.Parameters.AddWithValue(
            "maxTotalBytes",
            request.MaxTotalBytes);
        command.Parameters.AddWithValue(
            "repositoryCommit",
            runtimeIdentity.Code.RepositoryCommit);
        command.Parameters.AddWithValue(
            "repositoryTree",
            runtimeIdentity.Code.RepositoryTree);
        command.Parameters.AddWithValue(
            "binarySha",
            runtimeIdentity.Code
                .SupervisorBinarySha256);
        command.Parameters.AddWithValue(
            "sourceSha",
            runtimeIdentity.Code
                .SupervisorSourceSha256);
        command.Parameters.AddWithValue(
            "wrapperSha",
            runtimeIdentity.Code.WrapperSha256);
        command.Parameters.AddWithValue(
            "controlSchemaSha",
            runtimeIdentity.ControlSchemaSha256);
        command.Parameters.AddWithValue(
            "databaseName",
            runtimeIdentity.Database.DatabaseName);
        command.Parameters.AddWithValue(
            "databaseOid",
            runtimeIdentity.Database.DatabaseOid);
        command.Parameters.AddWithValue(
            "systemIdentifier",
            runtimeIdentity.Database.SystemIdentifier);
        command.Parameters.AddWithValue(
            "serverVersion",
            runtimeIdentity.Database.ServerVersionNum);
        command.Parameters.AddWithValue(
            "dataDirectory",
            runtimeIdentity.Database.DataDirectory);
        command.Parameters.AddWithValue(
            "postmasterStartedAt",
            runtimeIdentity.Database
                .PostmasterStartedAtUtc);
        command.Parameters.AddWithValue(
            "sourceIdentitySha",
            runtimeIdentity.SourceIdentitySha256);
        command.Parameters.AddWithValue(
            "approvedBy",
            request.ApprovedBy.Trim());
        command.Parameters.AddWithValue(
            "reviewedBy",
            request.ReviewedBy.Trim());
        command.Parameters.AddWithValue(
            "approvalReference",
            request.ApprovalReference.Trim());
        command.Parameters.AddWithValue(
            "authorizedAt",
            authorizedAt);
        command.Parameters.AddWithValue(
            "policyDigest",
            digest);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateControlAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        bool enabled,
        Guid? policyId,
        string actor,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText = """
            UPDATE
                public.snapshot_generation_retirement_control
            SET enabled = @enabled,
                active_policy_epoch_id = @policyId,
                updated_by = @actor,
                updated_at = @now
            WHERE control_key = TRUE
            """;
        command.Parameters.AddWithValue(
            "enabled",
            enabled);
        command.Parameters.Add(
            new NpgsqlParameter(
                "policyId",
                NpgsqlDbType.Uuid)
            {
                Value = policyId is null
                    ? DBNull.Value
                    : policyId.Value,
            });
        command.Parameters.AddWithValue(
            "actor",
            actor);
        command.Parameters.AddWithValue(
            "now",
            now);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException(
                "Snapshot-generation retirement control update failed.");
        }
    }

    private static void RequirePolicyRuntime(
        SnapshotGenerationRetirementPolicy policy,
        RetirementRuntimeIdentity observed,
        DateTimeOffset now)
    {
        observed.Validate();
        if (now < policy.NotBefore)
        {
            throw new InvalidOperationException(
                "The retirement policy is not active yet.");
        }
        if (policy.RuntimeIdentity != observed)
        {
            throw new InvalidOperationException(
                "The retirement policy runtime identity no longer matches.");
        }
    }

    private static async Task<RetirementTarget?>
        SelectTargetAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText = """
            WITH latest_cycle AS (
                SELECT cycle.*
                FROM public.snapshot_generation_retention_cycles
                    cycle
                ORDER BY cycle.created_at DESC,
                         cycle.cycle_id DESC
                LIMIT 1
            )
            SELECT
                cycle.cycle_id,
                observation.observation_id,
                cycle.trigger_scrape_id,
                cycle.trigger_publication_id,
                observation.instrument,
                CASE observation.instrument
                    WHEN 'Solo_Guitar' THEN 0
                    WHEN 'Solo_Bass' THEN 1
                    WHEN 'Solo_Vocals' THEN 2
                    WHEN 'Solo_Drums' THEN 3
                    WHEN 'Solo_PeripheralGuitar' THEN 4
                    WHEN 'Solo_PeripheralBass' THEN 5
                    WHEN 'Solo_PeripheralVocals' THEN 6
                    WHEN 'Solo_PeripheralCymbals' THEN 7
                    WHEN 'Solo_PeripheralDrums' THEN 8
                    ELSE 99
                END::SMALLINT,
                observation.snapshot_id,
                observation.root_schema,
                observation.root_relation,
                observation.root_oid,
                observation.child_schema,
                observation.child_relation,
                observation.child_oid,
                observation.child_relfilenode,
                observation.stable_child_identity_hash,
                observation.stable_config_schema_hash,
                observation.total_bytes
            FROM latest_cycle cycle
            JOIN public.snapshot_generation_retention_observations
                observation
              ON observation.cycle_id = cycle.cycle_id
            JOIN public.scrape_publication_state state
              ON state.id = TRUE
            JOIN public.publication_generations generation
              ON generation.publication_id =
                    state.current_publication_id
            WHERE cycle.status = 'observed'
              AND cycle.report_only
              AND cycle.oracle_agreement
              AND cycle.planner_version = 3
              AND cycle.config_version = 1
              AND cycle.blocked_count = 0
              AND cycle.global_blockers = '[]'::JSONB
              AND cycle.planner_child_set =
                    cycle.oracle_child_set
              AND cycle.planner_live_set =
                    cycle.oracle_live_set
              AND cycle.planner_candidate_set =
                    cycle.oracle_candidate_set
              AND cycle.trigger_publication_id =
                    state.current_publication_id
              AND cycle.trigger_scrape_id =
                    state.published_scrape_id
              AND generation.status = 'current'
              AND generation.scrape_id =
                    cycle.trigger_scrape_id
              AND state.working_publication_id IS NULL
              AND NOT state.public_reads_frozen
              AND
                    state.publication_commit_intent_started_at
                        IS NULL
              AND state.max_score_mutation_gate_token
                    IS NULL
              AND (
                    (
                        state.improvement_notifications_scrape_id =
                            state.published_scrape_id
                        AND
                            state.improvement_notifications_status =
                                'completed'
                        AND
                            state.improvement_notifications_completed_at
                                IS NOT NULL
                        AND
                            state.improvement_notifications_projection_ready
                        AND
                            state.improvement_notifications_projection_scrape_id =
                                state.published_scrape_id)
                    OR
                    (
                        state.improvement_notifications_status =
                            'disabled'
                        AND
                            state.improvement_notifications_scrape_id
                                IS NULL
                        AND
                            state.improvement_notifications_completed_at
                                IS NULL
                        AND NOT
                            state.improvement_notifications_projection_ready
                        AND
                            state.improvement_notifications_projection_scrape_id
                                IS NULL))
              AND observation.report_only
              AND observation.classification =
                    'candidate'
              AND NOT observation.planner_live
              AND NOT observation.oracle_live
              AND pg_catalog.cardinality(
                    observation.blocker_codes) = 0
              AND NOT (
                    observation.instrument = 'Solo_Bass'
                    AND observation.snapshot_id = 1308)
              AND observation.total_bytes > 0
            ORDER BY
                observation.total_bytes DESC,
                observation.snapshot_id,
                CASE observation.instrument
                    WHEN 'Solo_Guitar' THEN 0
                    WHEN 'Solo_Bass' THEN 1
                    WHEN 'Solo_Vocals' THEN 2
                    WHEN 'Solo_Drums' THEN 3
                    WHEN 'Solo_PeripheralGuitar' THEN 4
                    WHEN 'Solo_PeripheralBass' THEN 5
                    WHEN 'Solo_PeripheralVocals' THEN 6
                    WHEN 'Solo_PeripheralCymbals' THEN 7
                    WHEN 'Solo_PeripheralDrums' THEN 8
                    ELSE 99
                END,
                observation.child_oid
            LIMIT 1
            """;
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        var target = new RetirementTarget(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetInt16(5),
            reader.GetInt64(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetInt64(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetString(14),
            reader.GetString(15),
            reader.GetInt64(16));
        await reader.CloseAsync();
        var lockedBytes =
            await LockAndValidateTargetAsync(
                connection,
                transaction,
                target.CycleId,
                target.ObservationId,
                ct);
        if (lockedBytes is null
            || lockedBytes != target.TargetBytes)
        {
            throw new InvalidOperationException(
                "The retirement target bytes differ from immutable planner evidence.");
        }
        return target;
    }

    private static async Task<long?>
        LockAndValidateTargetAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long cycleId,
            long observationId,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                public.fst_lock_snapshot_generation_retirement_plan_target(
                    @cycleId,
                    @observationId)
            """;
        command.Parameters.AddWithValue(
            "cycleId",
            cycleId);
        command.Parameters.AddWithValue(
            "observationId",
            observationId);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull
            ? null
            : Convert.ToInt64(result);
    }

    private static async Task<
        SnapshotGenerationRetirementJob>
        InsertJobAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid jobId,
            Guid policyId,
            RetirementTarget target,
            string sourceIdentitySha256,
            string planDigest,
            DateTimeOffset now,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText = """
            INSERT INTO
                public.snapshot_generation_retirement_jobs (
                    job_id,
                    schema_version,
                    tool_id,
                    policy_epoch_id,
                    cycle_id,
                    observation_id,
                    trigger_scrape_id,
                    trigger_publication_id,
                    instrument,
                    instrument_order,
                    snapshot_id,
                    root_schema,
                    root_relation,
                    root_oid,
                    child_schema,
                    child_relation,
                    child_oid,
                    child_relfilenode,
                    stable_child_identity_hash,
                    stable_config_schema_hash,
                    target_bytes,
                    source_identity_sha256,
                    plan_digest,
                    state,
                    planned_at,
                    created_at,
                    updated_at)
            VALUES (
                @jobId,
                @schemaVersion,
                @toolId,
                @policyId,
                @cycleId,
                @observationId,
                @scrapeId,
                @publicationId,
                @instrument,
                @instrumentOrder,
                @snapshotId,
                @rootSchema,
                @rootRelation,
                @rootOid,
                @childSchema,
                @childRelation,
                @childOid,
                @childRelfilenode,
                @childHash,
                @configHash,
                @targetBytes,
                @sourceIdentitySha,
                @planDigest,
                'planned',
                @now,
                @now,
                @now)
            """;
        command.Parameters.AddWithValue(
            "jobId",
            jobId);
        command.Parameters.AddWithValue(
            "schemaVersion",
            SnapshotGenerationRetirementContract
                .SchemaVersion);
        command.Parameters.AddWithValue(
            "toolId",
            SnapshotGenerationRetirementContract
                .ToolId);
        command.Parameters.AddWithValue(
            "policyId",
            policyId);
        command.Parameters.AddWithValue(
            "cycleId",
            target.CycleId);
        command.Parameters.AddWithValue(
            "observationId",
            target.ObservationId);
        command.Parameters.AddWithValue(
            "scrapeId",
            target.TriggerScrapeId);
        command.Parameters.AddWithValue(
            "publicationId",
            target.TriggerPublicationId);
        command.Parameters.AddWithValue(
            "instrument",
            target.Instrument);
        command.Parameters.AddWithValue(
            "instrumentOrder",
            target.InstrumentOrder);
        command.Parameters.AddWithValue(
            "snapshotId",
            target.SnapshotId);
        command.Parameters.AddWithValue(
            "rootSchema",
            target.RootSchema);
        command.Parameters.AddWithValue(
            "rootRelation",
            target.RootRelation);
        command.Parameters.AddWithValue(
            "rootOid",
            target.RootOid);
        command.Parameters.AddWithValue(
            "childSchema",
            target.ChildSchema);
        command.Parameters.AddWithValue(
            "childRelation",
            target.ChildRelation);
        command.Parameters.AddWithValue(
            "childOid",
            target.ChildOid);
        command.Parameters.AddWithValue(
            "childRelfilenode",
            target.ChildRelfilenode);
        command.Parameters.AddWithValue(
            "childHash",
            target.StableChildIdentityHash);
        command.Parameters.AddWithValue(
            "configHash",
            target.StableConfigSchemaHash);
        command.Parameters.AddWithValue(
            "targetBytes",
            target.TargetBytes);
        command.Parameters.AddWithValue(
            "sourceIdentitySha",
            sourceIdentitySha256);
        command.Parameters.AddWithValue(
            "planDigest",
            planDigest);
        command.Parameters.AddWithValue(
            "now",
            now);
        await command.ExecuteNonQueryAsync(ct);
        return new(
            jobId,
            policyId,
            target.CycleId,
            target.ObservationId,
            target.TriggerScrapeId,
            target.TriggerPublicationId,
            target.Instrument,
            target.InstrumentOrder,
            target.SnapshotId,
            target.RootSchema,
            target.RootRelation,
            target.RootOid,
            target.ChildSchema,
            target.ChildRelation,
            target.ChildOid,
            target.ChildRelfilenode,
            target.StableChildIdentityHash,
            target.StableConfigSchemaHash,
            target.TargetBytes,
            sourceIdentitySha256,
            planDigest,
            "planned",
            null,
            now,
            null,
            now,
            now);
    }

    private static async Task<string?>
        ReadJobStaleReasonAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            SnapshotGenerationRetirementJob job,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText = """
            WITH latest_cycle AS (
                SELECT cycle.cycle_id
                FROM public.snapshot_generation_retention_cycles
                    cycle
                ORDER BY cycle.created_at DESC,
                         cycle.cycle_id DESC
                LIMIT 1
            )
            SELECT
                (SELECT cycle_id FROM latest_cycle),
                state.current_publication_id::BIGINT,
                state.published_scrape_id::BIGINT,
                state.working_publication_id IS NULL
                    AND NOT state.public_reads_frozen
                    AND
                        state.publication_commit_intent_started_at
                            IS NULL
                    AND state.max_score_mutation_gate_token
                            IS NULL
                    AND (
                        (
                            state.improvement_notifications_scrape_id =
                                state.published_scrape_id
                            AND
                                state.improvement_notifications_status =
                                    'completed'
                            AND
                                state.improvement_notifications_completed_at
                                    IS NOT NULL
                            AND
                                state.improvement_notifications_projection_ready
                            AND
                                state.improvement_notifications_projection_scrape_id =
                                    state.published_scrape_id)
                        OR
                        (
                            state.improvement_notifications_status =
                                'disabled'
                            AND
                                state.improvement_notifications_scrape_id
                                    IS NULL
                            AND
                                state.improvement_notifications_completed_at
                                    IS NULL
                            AND NOT
                                state.improvement_notifications_projection_ready
                            AND
                                state.improvement_notifications_projection_scrape_id
                                    IS NULL)),
                observation.classification,
                observation.report_only,
                observation.planner_live,
                observation.oracle_live,
                pg_catalog.cardinality(
                    observation.blocker_codes),
                child.oid::BIGINT,
                child.relfilenode::BIGINT,
                pg_catalog.pg_total_relation_size(
                    child.oid)::BIGINT
            FROM public.scrape_publication_state state
            LEFT JOIN
                public.snapshot_generation_retention_observations
                    observation
              ON observation.cycle_id = @cycleId
             AND observation.observation_id =
                    @observationId
            LEFT JOIN pg_catalog.pg_namespace namespace
              ON namespace.nspname =
                    observation.child_schema
            LEFT JOIN pg_catalog.pg_class child
              ON child.relnamespace = namespace.oid
             AND child.relname =
                    observation.child_relation
            WHERE state.id = TRUE
            """;
        command.Parameters.AddWithValue(
            "cycleId",
            job.CycleId);
        command.Parameters.AddWithValue(
            "observationId",
            job.ObservationId);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return "publication_state_missing";
        if (reader.IsDBNull(0)
            || reader.GetInt64(0) != job.CycleId)
        {
            return "newer_retention_cycle";
        }
        if (reader.IsDBNull(1)
            || reader.GetInt64(1) !=
                job.TriggerPublicationId
            || reader.IsDBNull(2)
            || reader.GetInt64(2) !=
                job.TriggerScrapeId
            || !reader.GetBoolean(3))
        {
            return "publication_changed";
        }
        if (reader.IsDBNull(4)
            || reader.GetString(4) != "candidate"
            || !reader.GetBoolean(5)
            || reader.GetBoolean(6)
            || reader.GetBoolean(7)
            || reader.GetInt32(8) != 0)
        {
            return "target_no_longer_candidate";
        }
        if (reader.IsDBNull(9)
            || reader.GetInt64(9) != job.ChildOid
            || reader.IsDBNull(10)
            || reader.GetInt64(10) !=
                job.ChildRelfilenode
            || reader.IsDBNull(11)
            || reader.GetInt64(11) !=
                job.TargetBytes)
        {
            return "target_physical_identity_changed";
        }
        await reader.CloseAsync();
        if (await LockAndValidateTargetAsync(
                connection,
                transaction,
                job.CycleId,
                job.ObservationId,
                ct)
            != job.TargetBytes)
        {
            return "target_catalog_changed";
        }
        return null;
    }

    private static async Task<
        SnapshotGenerationRetirementJob>
        TransitionJobAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            SnapshotGenerationRetirementJob job,
            string state,
            string reason,
            DateTimeOffset now,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText = """
            UPDATE
                public.snapshot_generation_retirement_jobs
            SET state = @state,
                state_reason = @reason,
                terminal_at = @now,
                updated_at = @now
            WHERE job_id = @jobId
              AND state = 'planned'
            """;
        command.Parameters.AddWithValue(
            "state",
            state);
        command.Parameters.AddWithValue(
            "reason",
            reason);
        command.Parameters.AddWithValue(
            "now",
            now);
        command.Parameters.AddWithValue(
            "jobId",
            job.JobId);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException(
                "Retirement job transition lost its planned-state fence.");
        }
        var updated = job with
        {
            State = state,
            StateReason = reason,
            TerminalAt = now,
            UpdatedAt = now,
        };
        await AppendEventAsync(
            connection,
            transaction,
            job.PolicyEpochId,
            job.JobId,
            state == "expired"
                ? "job_expired"
                : "job_superseded",
            new
            {
                job.JobId,
                fromState = "planned",
                toState = state,
                reason,
                at = now,
            },
            ct);
        return updated;
    }

    private static async Task DeactivatePolicyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SnapshotGenerationRetirementPolicy policy,
        string reason,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await AppendEventAsync(
            connection,
            transaction,
            policy.PolicyEpochId,
            jobId: null,
            "policy_deactivated",
            new
            {
                policy.PolicyEpochId,
                reason,
                at = now,
            },
            ct);
        await UpdateControlAsync(
            connection,
            transaction,
            enabled: false,
            policyId: null,
            $"reconcile:{reason}",
            now,
            ct);
    }

    private static async Task<(int JobCount, long TotalBytes)>
        ReadPolicyUsageAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid policyId,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout =
            CommandTimeoutSeconds;
        command.CommandText = """
            SELECT COUNT(*)::INTEGER,
                   COALESCE(SUM(job.target_bytes), 0)::BIGINT
            FROM public.snapshot_generation_retirement_jobs job
            WHERE job.policy_epoch_id = @policyId
            """;
        command.Parameters.AddWithValue(
            "policyId",
            policyId);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (
            reader.GetInt32(0),
            reader.GetInt64(1));
    }

    private static async Task<int>
        ReadPolicyJobCountAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid policyId,
            CancellationToken ct)
    {
        var usage = await ReadPolicyUsageAsync(
            connection,
            transaction,
            policyId,
            ct);
        return usage.JobCount;
    }

    private static async Task AppendEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid policyId,
        Guid? jobId,
        string eventType,
        object payload,
        CancellationToken ct)
    {
        int sequence;
        string? previousHash;
        await using (var read =
                     connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandTimeout =
                CommandTimeoutSeconds;
            read.CommandText = """
                SELECT event.sequence,
                       event.current_hash
                FROM public.snapshot_generation_retirement_events
                    event
                WHERE event.policy_epoch_id = @policyId
                ORDER BY event.sequence DESC
                LIMIT 1
                """;
            read.Parameters.AddWithValue(
                "policyId",
                policyId);
            await using var reader =
                await read.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                sequence = checked(
                    reader.GetInt32(0) + 1);
                previousHash =
                    reader.GetString(1);
            }
            else
            {
                sequence = 1;
                previousHash = null;
            }
        }

        var payloadElement =
            JsonSerializer.SerializeToElement(
                payload,
                RetirementJson.Output);
        var currentHash = RetirementJson.Sha256(
            new RetirementEventHashInput(
                policyId,
                jobId,
                sequence,
                eventType,
                payloadElement,
                previousHash));
        await using var insert =
            connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandTimeout =
            CommandTimeoutSeconds;
        insert.CommandText = """
            INSERT INTO
                public.snapshot_generation_retirement_events (
                    policy_epoch_id,
                    job_id,
                    sequence,
                    event_type,
                    payload,
                    previous_hash,
                    current_hash)
            VALUES (
                @policyId,
                @jobId,
                @sequence,
                @eventType,
                @payload,
                @previousHash,
                @currentHash)
            """;
        insert.Parameters.AddWithValue(
            "policyId",
            policyId);
        insert.Parameters.Add(
            new NpgsqlParameter(
                "jobId",
                NpgsqlDbType.Uuid)
            {
                Value = jobId is null
                    ? DBNull.Value
                    : jobId.Value,
            });
        insert.Parameters.AddWithValue(
            "sequence",
            sequence);
        insert.Parameters.AddWithValue(
            "eventType",
            eventType);
        insert.Parameters.Add(
            new NpgsqlParameter(
                "payload",
                NpgsqlDbType.Jsonb)
            {
                Value = payloadElement.GetRawText(),
            });
        insert.Parameters.Add(
            new NpgsqlParameter(
                "previousHash",
                NpgsqlDbType.Text)
            {
                Value = previousHash is null
                    ? DBNull.Value
                    : previousHash,
            });
        insert.Parameters.AddWithValue(
            "currentHash",
            currentHash);
        await insert.ExecuteNonQueryAsync(ct);
    }
}
