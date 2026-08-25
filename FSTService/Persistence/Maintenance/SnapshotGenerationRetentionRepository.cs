using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence.Maintenance;

public sealed class SnapshotGenerationRetentionRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public SnapshotGenerationRetentionRepository(
        NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<SnapshotGenerationRetentionCycle?>
        GetCycleForSafePointAsync(
            string safePointKind,
            long triggerPublicationId,
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        return await GetCycleForSafePointAsync(
            connection,
            transaction: null,
            safePointKind,
            triggerPublicationId,
            ct);
    }

    public async Task<IReadOnlyList<SnapshotGenerationRetentionJob>>
        GetJobsAsync(
            long cycleId,
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                job_id,
                cycle_id,
                report_only,
                operation_kind,
                instrument,
                root_relation,
                child_relation,
                snapshot_id,
                child_oid,
                child_relfilenode,
                partition_bound,
                tablespace_name,
                row_estimate,
                total_bytes,
                protected_evidence::text,
                reference_evidence::text,
                blocker_codes,
                blocker_details::text,
                status,
                attempt_count,
                lease_owner,
                lease_token,
                lease_acquired_at,
                lease_expires_at,
                started_at,
                completed_at,
                error_message,
                created_at,
                updated_at
            FROM snapshot_generation_retention_jobs
            WHERE cycle_id = @cycleId
            ORDER BY snapshot_id, instrument, job_id
            """;
        command.Parameters.AddWithValue("cycleId", cycleId);

        var jobs = new List<SnapshotGenerationRetentionJob>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            jobs.Add(new SnapshotGenerationRetentionJob(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetBoolean(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetInt64(12),
                reader.GetInt64(13),
                reader.GetString(14),
                reader.GetString(15),
                reader.GetFieldValue<string[]>(16),
                reader.GetString(17),
                reader.GetString(18),
                reader.GetInt32(19),
                reader.IsDBNull(20) ? null : reader.GetString(20),
                reader.IsDBNull(21) ? null : reader.GetGuid(21),
                reader.IsDBNull(22) ? null : reader.GetDateTime(22),
                reader.IsDBNull(23) ? null : reader.GetDateTime(23),
                reader.IsDBNull(24) ? null : reader.GetDateTime(24),
                reader.IsDBNull(25) ? null : reader.GetDateTime(25),
                reader.IsDBNull(26) ? null : reader.GetString(26),
                reader.GetDateTime(27),
                reader.GetDateTime(28)));
        }

        return jobs;
    }

    public async Task<IReadOnlyList<SnapshotGenerationRetentionEvidence>>
        GetEvidenceAsync(
            long cycleId,
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                evidence_id,
                cycle_id,
                job_id,
                sequence,
                phase,
                kind,
                payload::text,
                previous_hash,
                current_hash,
                created_at
            FROM snapshot_generation_retention_evidence
            WHERE cycle_id = @cycleId
            ORDER BY sequence
            """;
        command.Parameters.AddWithValue("cycleId", cycleId);

        var evidence = new List<SnapshotGenerationRetentionEvidence>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            evidence.Add(new SnapshotGenerationRetentionEvidence(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(8),
                reader.GetDateTime(9)));
        }

        return evidence;
    }

    public async Task<bool> HasActiveDestructiveStateAsync(
        CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM snapshot_generation_retention_jobs
                WHERE NOT report_only
                  AND status = ANY(@statuses)
                UNION ALL
                SELECT 1
                FROM snapshot_generation_retention_cycles
                WHERE NOT report_only
                  AND status = 'safety_failed'
            )
            """;
        command.Parameters.AddWithValue(
            "statuses",
            SnapshotGenerationRetentionJobStatus
                .ScrapeAdmissionBlockingStatuses);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    internal static async Task<SnapshotGenerationRetentionCycle?>
        GetCycleForSafePointAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            string safePointKind,
            long triggerPublicationId,
            CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                cycle_id,
                trigger_scrape_id,
                trigger_publication_id,
                safe_point_kind,
                safe_point_at,
                planner_version,
                config_version,
                report_only,
                plan_digest,
                status,
                candidate_count,
                blocked_count,
                candidate_bytes,
                blocked_bytes,
                started_at,
                completed_at,
                error_message,
                created_at,
                updated_at
            FROM snapshot_generation_retention_cycles
            WHERE safe_point_kind = @safePointKind
              AND trigger_publication_id = @triggerPublicationId
            """;
        command.Parameters.AddWithValue(
            "safePointKind",
            safePointKind);
        command.Parameters.AddWithValue(
            "triggerPublicationId",
            triggerPublicationId);

        await using var reader =
            await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? ReadCycle(reader)
            : null;
    }

    internal static async Task<long> InsertPlanningCycleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SnapshotGenerationRetentionPlanRequest request,
        SnapshotGenerationRetentionPolicy policy,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO snapshot_generation_retention_cycles (
                trigger_scrape_id,
                trigger_publication_id,
                safe_point_kind,
                safe_point_at,
                planner_version,
                config_version,
                report_only,
                status)
            VALUES (
                @triggerScrapeId,
                @triggerPublicationId,
                @safePointKind,
                @safePointAt,
                @plannerVersion,
                @configVersion,
                @reportOnly,
                'planning')
            RETURNING cycle_id
            """;
        command.Parameters.AddWithValue(
            "triggerScrapeId",
            request.TriggerScrapeId);
        command.Parameters.AddWithValue(
            "triggerPublicationId",
            request.TriggerPublicationId);
        command.Parameters.AddWithValue(
            "safePointKind",
            request.SafePointKind);
        command.Parameters.AddWithValue(
            "safePointAt",
            request.SafePointAtUtc);
        command.Parameters.AddWithValue(
            "plannerVersion",
            SnapshotGenerationRetentionContract.PlannerVersion);
        command.Parameters.AddWithValue(
            "configVersion",
            SnapshotGenerationRetentionContract.ConfigVersion);
        command.Parameters.AddWithValue(
            "reportOnly",
            policy.ReportOnly);
        return (long)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException(
                "Retention cycle insert did not return a cycle ID."));
    }

    internal static async Task<long> InsertJobAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long cycleId,
        SnapshotGenerationRetentionJobDraft job,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO snapshot_generation_retention_jobs (
                cycle_id,
                report_only,
                operation_kind,
                instrument,
                root_relation,
                child_relation,
                snapshot_id,
                child_oid,
                child_relfilenode,
                partition_bound,
                tablespace_name,
                row_estimate,
                total_bytes,
                protected_evidence,
                reference_evidence,
                blocker_codes,
                blocker_details,
                status)
            VALUES (
                @cycleId,
                @reportOnly,
                @operationKind,
                @instrument,
                @rootRelation,
                @childRelation,
                @snapshotId,
                @childOid,
                @childRelfilenode,
                @partitionBound,
                @tablespaceName,
                @rowEstimate,
                @totalBytes,
                @protectedEvidence,
                @referenceEvidence,
                @blockerCodes,
                @blockerDetails,
                @status)
            RETURNING job_id
            """;
        command.Parameters.AddWithValue("cycleId", cycleId);
        command.Parameters.AddWithValue(
            "reportOnly",
            job.ReportOnly);
        command.Parameters.AddWithValue(
            "operationKind",
            job.OperationKind);
        command.Parameters.AddWithValue(
            "instrument",
            job.Instrument);
        command.Parameters.AddWithValue(
            "rootRelation",
            job.RootRelation);
        command.Parameters.AddWithValue(
            "childRelation",
            job.ChildRelation);
        command.Parameters.AddWithValue(
            "snapshotId",
            job.SnapshotId);
        command.Parameters.AddWithValue(
            "childOid",
            job.ChildOid);
        command.Parameters.AddWithValue(
            "childRelfilenode",
            job.ChildRelfilenode);
        command.Parameters.AddWithValue(
            "partitionBound",
            job.PartitionBound);
        command.Parameters.AddWithValue(
            "tablespaceName",
            job.TablespaceName);
        command.Parameters.AddWithValue(
            "rowEstimate",
            job.RowEstimate);
        command.Parameters.AddWithValue(
            "totalBytes",
            job.TotalBytes);
        command.Parameters.Add(
                "protectedEvidence",
                NpgsqlDbType.Jsonb)
            .Value = job.ProtectedEvidenceJson;
        command.Parameters.Add(
                "referenceEvidence",
                NpgsqlDbType.Jsonb)
            .Value = job.ReferenceEvidenceJson;
        command.Parameters.AddWithValue(
            "blockerCodes",
            job.BlockerCodes.ToArray());
        command.Parameters.Add(
                "blockerDetails",
                NpgsqlDbType.Jsonb)
            .Value = job.BlockerDetailsJson;
        command.Parameters.AddWithValue("status", job.Status);
        return (long)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException(
                "Retention job insert did not return a job ID."));
    }

    internal static async Task AppendEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long cycleId,
        long? jobId,
        int sequence,
        string phase,
        string kind,
        string payloadJson,
        string? previousHash,
        string currentHash,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO snapshot_generation_retention_evidence (
                cycle_id,
                job_id,
                sequence,
                phase,
                kind,
                payload,
                previous_hash,
                current_hash)
            VALUES (
                @cycleId,
                @jobId,
                @sequence,
                @phase,
                @kind,
                @payload,
                @previousHash,
                @currentHash)
            """;
        command.Parameters.AddWithValue("cycleId", cycleId);
        command.Parameters.AddWithValue(
            "jobId",
            (object?)jobId ?? DBNull.Value);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("phase", phase);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value =
            payloadJson;
        command.Parameters.AddWithValue(
            "previousHash",
            (object?)previousHash ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "currentHash",
            currentHash);
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task CompleteCycleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long cycleId,
        string status,
        string planDigest,
        int candidateCount,
        int blockedCount,
        long candidateBytes,
        long blockedBytes,
        string? errorMessage,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE snapshot_generation_retention_cycles
            SET status = @status,
                plan_digest = @planDigest,
                candidate_count = @candidateCount,
                blocked_count = @blockedCount,
                candidate_bytes = @candidateBytes,
                blocked_bytes = @blockedBytes,
                completed_at = clock_timestamp(),
                error_message = @errorMessage,
                updated_at = clock_timestamp()
            WHERE cycle_id = @cycleId
              AND status = 'planning'
            """;
        command.Parameters.AddWithValue("cycleId", cycleId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue(
            "planDigest",
            planDigest);
        command.Parameters.AddWithValue(
            "candidateCount",
            candidateCount);
        command.Parameters.AddWithValue(
            "blockedCount",
            blockedCount);
        command.Parameters.AddWithValue(
            "candidateBytes",
            candidateBytes);
        command.Parameters.AddWithValue(
            "blockedBytes",
            blockedBytes);
        command.Parameters.AddWithValue(
            "errorMessage",
            (object?)errorMessage ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException(
                $"Retention cycle {cycleId} was not in planning state.");
        }
    }

    private static SnapshotGenerationRetentionCycle ReadCycle(
        NpgsqlDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetDateTime(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetBoolean(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetDateTime(14),
            reader.IsDBNull(15) ? null : reader.GetDateTime(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.GetDateTime(17),
            reader.GetDateTime(18));
}
