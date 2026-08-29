using System.Text.Json;
using FSTService.Scraping.Replay;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence.Maintenance;

public sealed class SnapshotGenerationRetentionRepository
{
    private const int CommandTimeoutSeconds = 15;
    private readonly NpgsqlDataSource _dataSource;

    public SnapshotGenerationRetentionRepository(
        NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task RecordDeferralAsync(
        SnapshotGenerationRetentionPlanRequest request,
        string code,
        string detail,
        bool retryable,
        object evidence,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        var evidenceJson =
            TierZeroCanonicalJson.SerializeToString(evidence);

        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            INSERT INTO snapshot_generation_retention_deferrals (
                trigger_scrape_id,
                trigger_publication_id,
                safe_point_kind,
                safe_point_at,
                report_only,
                code,
                detail,
                retryable,
                evidence)
            VALUES (
                @triggerScrapeId,
                @triggerPublicationId,
                @safePointKind,
                @safePointAt,
                TRUE,
                @code,
                @detail,
                @retryable,
                @evidence)
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
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("detail", detail);
        command.Parameters.AddWithValue("retryable", retryable);
        command.Parameters.Add("evidence", NpgsqlDbType.Jsonb).Value =
            evidenceJson;
        await command.ExecuteNonQueryAsync(ct);
    }

    internal async Task<(
        SnapshotGenerationRetentionCycle Cycle,
        bool Inserted)>
        PersistAsync(
            NpgsqlConnection connection,
            SnapshotGenerationRetentionPersistRequest request,
            int commandTimeoutSeconds,
            CancellationToken ct)
    {
        await using var transaction =
            await connection.BeginTransactionAsync(ct);
        await ApplyWriteTimeoutsAsync(
            connection,
            transaction,
            commandTimeoutSeconds,
            ct);

        var cycleId = await TryInsertCycleAsync(
            connection,
            transaction,
            request,
            commandTimeoutSeconds,
            ct);
        if (!cycleId.HasValue)
        {
            var existing = await GetCycleForSafePointAsync(
                connection,
                transaction,
                request.Request,
                commandTimeoutSeconds,
                ct);
            await transaction.CommitAsync(ct);
            return (
                existing
                    ?? throw new InvalidOperationException(
                        "Retention cycle uniqueness conflict did not resolve to an existing cycle."),
                Inserted: false);
        }

        var evidenceSequence = 0;
        string? previousEvidenceHash = null;
        (evidenceSequence, previousEvidenceHash) =
            await AppendEvidenceAsync(
                connection,
                transaction,
                cycleId.Value,
                observationId: null,
                evidenceSequence,
                previousEvidenceHash,
                "observation",
                "summary",
                new
                {
                    request.Status,
                    request.OracleAgreement,
                    request.CandidateIdentityHash,
                    request.ObservationHash,
                    PlannerChildKeys = request.PlannerChildKeys,
                    PlannerLiveKeys = request.PlannerLiveKeys,
                    PlannerCandidateKeys =
                        request.PlannerCandidateKeys,
                    OracleChildKeys = request.OracleChildKeys,
                    OracleLiveKeys = request.OracleLiveKeys,
                    OracleCandidateKeys =
                        request.OracleCandidateKeys,
                    PlannerPublicationSourceValidations =
                        request
                            .PlannerPublicationSourceValidations,
                    OraclePublicationSourceValidations =
                        request
                            .OraclePublicationSourceValidations,
                    PlannerIndexTopologyValidations =
                        request
                            .PlannerIndexTopologyValidations,
                    OracleIndexTopologyValidations =
                        request
                            .OracleIndexTopologyValidations,
                    GlobalBlockers = request.GlobalBlockers,
                    Anomalies = request.Anomalies,
                    request.ErrorMessage,
                },
                commandTimeoutSeconds,
                ct);

        foreach (var evaluation in request.Evaluations
                     .OrderBy(
                         static item =>
                             item.Child.InstrumentDefinition
                                 .CanonicalOrder)
                     .ThenBy(
                         static item => item.Child.SnapshotId)
                     .ThenBy(
                         static item =>
                             item.Child.ChildRelation,
                         StringComparer.Ordinal))
        {
            var observationId =
                await InsertObservationAsync(
                    connection,
                    transaction,
                    cycleId.Value,
                    evaluation,
                    commandTimeoutSeconds,
                    ct);
            (evidenceSequence, previousEvidenceHash) =
                await AppendEvidenceAsync(
                    connection,
                    transaction,
                    cycleId.Value,
                    observationId,
                    evidenceSequence,
                    previousEvidenceHash,
                    "observation",
                    "child",
                    new
                    {
                        evaluation.Child.PhysicalKey,
                        evaluation.Child.StableChildIdentityHash,
                        evaluation.Child.StableConfigSchemaHash,
                        evaluation.Child.ObservationMetricsHash,
                        evaluation.PlannerLive,
                        evaluation.OracleLive,
                        evaluation.Classification,
                        evaluation.RootReasons,
                        evaluation.Blockers,
                    },
                    commandTimeoutSeconds,
                    ct);
        }

        await transaction.CommitAsync(ct);
        return (
            await GetCycleAsync(
                connection,
                cycleId.Value,
                commandTimeoutSeconds,
                ct),
            Inserted: true);
    }

    public async Task<SnapshotGenerationRetentionCycle?>
        GetCycleForSafePointAsync(
            long triggerScrapeId,
            long triggerPublicationId,
            string safePointKind =
                SnapshotGenerationRetentionContract
                    .TerminalWorkerSafePoint,
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        return await GetCycleForSafePointAsync(
            connection,
            transaction: null,
            new SnapshotGenerationRetentionPlanRequest(
                triggerScrapeId,
                triggerPublicationId,
                DateTime.UnixEpoch,
                BroadcastCompletedScrapeId: null,
                BackgroundWorkQuiesced: false,
                safePointKind),
            CommandTimeoutSeconds,
            ct);
    }

    public async Task<IReadOnlyList<
        SnapshotGenerationRetentionObservation>>
        GetObservationsAsync(
            long cycleId,
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                observation_id,
                cycle_id,
                instrument,
                root_schema,
                root_relation,
                snapshot_parent_oid,
                root_oid,
                root_partition_key,
                root_partition_bound,
                root_tablespace_name,
                root_relation_options::TEXT,
                root_index_configuration::TEXT,
                child_schema,
                child_relation,
                snapshot_id,
                child_oid,
                child_relfilenode,
                partition_bound,
                tablespace_name,
                relation_kind,
                persistence_kind,
                access_method,
                relation_options::TEXT,
                index_configuration::TEXT,
                stable_child_identity_hash,
                stable_config_schema_hash,
                row_estimate,
                total_bytes,
                observation_metrics_hash,
                planner_live,
                oracle_live,
                classification,
                root_reasons,
                blocker_codes,
                details::TEXT,
                created_at
            FROM snapshot_generation_retention_observations
            WHERE cycle_id = @cycleId
            ORDER BY snapshot_id, instrument, observation_id
            """;
        command.Parameters.AddWithValue("cycleId", cycleId);

        var observations =
            new List<SnapshotGenerationRetentionObservation>();
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            observations.Add(
                new SnapshotGenerationRetentionObservation(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetString(12),
                    reader.GetString(13),
                    reader.GetInt64(14),
                    reader.GetInt64(15),
                    reader.GetInt64(16),
                    reader.GetString(17),
                    reader.GetString(18),
                    reader.GetString(19),
                    reader.GetString(20),
                    reader.GetString(21),
                    reader.GetString(22),
                    reader.GetString(23),
                    reader.GetString(24),
                    reader.GetString(25),
                    reader.GetInt64(26),
                    reader.GetInt64(27),
                    reader.GetString(28),
                    reader.GetBoolean(29),
                    reader.GetBoolean(30),
                    reader.GetString(31),
                    reader.GetFieldValue<string[]>(32),
                    reader.GetFieldValue<string[]>(33),
                    reader.GetString(34),
                    reader.GetDateTime(35)));
        }

        return observations;
    }

    public async Task<IReadOnlyList<
        SnapshotGenerationRetentionDeferral>>
        GetDeferralsAsync(
            long triggerPublicationId,
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                deferral_id,
                trigger_scrape_id,
                trigger_publication_id,
                safe_point_kind,
                safe_point_at,
                code,
                detail,
                retryable,
                evidence::TEXT,
                created_at
            FROM snapshot_generation_retention_deferrals
            WHERE trigger_publication_id =
                    @triggerPublicationId
            ORDER BY deferral_id
            """;
        command.Parameters.AddWithValue(
            "triggerPublicationId",
            triggerPublicationId);

        var deferrals =
            new List<SnapshotGenerationRetentionDeferral>();
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            deferrals.Add(
                new SnapshotGenerationRetentionDeferral(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetDateTime(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetBoolean(7),
                    reader.GetString(8),
                    reader.GetDateTime(9)));
        }

        return deferrals;
    }

    private static async Task<long?> TryInsertCycleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SnapshotGenerationRetentionPersistRequest request,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        var candidateCount = request.Status ==
            SnapshotGenerationRetentionCycleStatus
                .OracleMismatch
            ? 0
            : request.Evaluations.Count(
                static evaluation =>
                    evaluation.Classification ==
                    SnapshotGenerationRetentionClassification
                        .Candidate);
        var protectedCount = request.Evaluations.Count(
            static evaluation =>
                evaluation.Classification ==
                SnapshotGenerationRetentionClassification
                    .Protected);
        var blockedCount = request.Evaluations.Count -
            candidateCount -
            protectedCount;
        var candidateBytes = request.Status ==
            SnapshotGenerationRetentionCycleStatus
                .OracleMismatch
            ? 0L
            : request.Evaluations
                .Where(static evaluation =>
                    evaluation.Classification ==
                    SnapshotGenerationRetentionClassification
                        .Candidate)
                .Sum(static evaluation =>
                    evaluation.Child.TotalBytes);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = """
            INSERT INTO snapshot_generation_retention_cycles (
                trigger_scrape_id,
                trigger_publication_id,
                safe_point_kind,
                safe_point_at,
                planner_version,
                config_version,
                report_only,
                status,
                oracle_agreement,
                candidate_identity_hash,
                observation_hash,
                planner_child_set,
                planner_live_set,
                planner_candidate_set,
                oracle_child_set,
                oracle_live_set,
                oracle_candidate_set,
                candidate_count,
                protected_count,
                blocked_count,
                candidate_bytes,
                global_blockers,
                anomalies,
                error_message)
            VALUES (
                @triggerScrapeId,
                @triggerPublicationId,
                @safePointKind,
                @safePointAt,
                @plannerVersion,
                @configVersion,
                TRUE,
                @status,
                @oracleAgreement,
                @candidateIdentityHash,
                @observationHash,
                @plannerChildSet,
                @plannerLiveSet,
                @plannerCandidateSet,
                @oracleChildSet,
                @oracleLiveSet,
                @oracleCandidateSet,
                @candidateCount,
                @protectedCount,
                @blockedCount,
                @candidateBytes,
                @globalBlockers,
                @anomalies,
                @errorMessage)
            ON CONFLICT (
                trigger_scrape_id,
                trigger_publication_id,
                safe_point_kind)
            DO NOTHING
            RETURNING cycle_id
            """;
        command.Parameters.AddWithValue(
            "triggerScrapeId",
            request.Request.TriggerScrapeId);
        command.Parameters.AddWithValue(
            "triggerPublicationId",
            request.Request.TriggerPublicationId);
        command.Parameters.AddWithValue(
            "safePointKind",
            request.Request.SafePointKind);
        command.Parameters.AddWithValue(
            "safePointAt",
            request.Request.SafePointAtUtc);
        command.Parameters.AddWithValue(
            "plannerVersion",
            SnapshotGenerationRetentionContract.PlannerVersion);
        command.Parameters.AddWithValue(
            "configVersion",
            SnapshotGenerationRetentionContract.ConfigVersion);
        command.Parameters.AddWithValue("status", request.Status);
        command.Parameters.AddWithValue(
            "oracleAgreement",
            request.OracleAgreement);
        command.Parameters.AddWithValue(
            "candidateIdentityHash",
            request.CandidateIdentityHash);
        command.Parameters.AddWithValue(
            "observationHash",
            request.ObservationHash);
        AddJsonParameter(
            command,
            "plannerChildSet",
            request.PlannerChildKeys);
        AddJsonParameter(
            command,
            "plannerLiveSet",
            request.PlannerLiveKeys);
        AddJsonParameter(
            command,
            "plannerCandidateSet",
            request.PlannerCandidateKeys);
        AddJsonParameter(
            command,
            "oracleChildSet",
            request.OracleChildKeys);
        AddJsonParameter(
            command,
            "oracleLiveSet",
            request.OracleLiveKeys);
        AddJsonParameter(
            command,
            "oracleCandidateSet",
            request.OracleCandidateKeys);
        command.Parameters.AddWithValue(
            "candidateCount",
            candidateCount);
        command.Parameters.AddWithValue(
            "protectedCount",
            protectedCount);
        command.Parameters.AddWithValue(
            "blockedCount",
            blockedCount);
        command.Parameters.AddWithValue(
            "candidateBytes",
            candidateBytes);
        AddJsonParameter(
            command,
            "globalBlockers",
            request.GlobalBlockers);
        AddJsonParameter(
            command,
            "anomalies",
            request.Anomalies);
        command.Parameters.AddWithValue(
            "errorMessage",
            (object?)request.ErrorMessage ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull
            ? null
            : Convert.ToInt64(result);
    }

    private static async Task<long> InsertObservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long cycleId,
        SnapshotGenerationRetentionEvaluation evaluation,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        var child = evaluation.Child;
        var blockers = evaluation.Blockers
            .OrderBy(
                static blocker => blocker.Code,
                StringComparer.Ordinal)
            .ThenBy(
                static blocker => blocker.Detail,
                StringComparer.Ordinal)
            .ToArray();
        var rootReasons = evaluation.RootReasons
            .Distinct(StringComparer.Ordinal)
            .OrderBy(
                static reason => reason,
                StringComparer.Ordinal)
            .ToArray();
        var relationOptions = child.RelationOptions
            .OrderBy(
                static option => option,
                StringComparer.Ordinal)
            .ToArray();
        var indexes = child.Indexes
            .OrderBy(
                static index => index.IndexName,
                StringComparer.Ordinal)
            .ThenBy(static index => index.IndexOid)
            .ToArray();
        var rootRelationOptions = child.RootRelationOptions
            .OrderBy(
                static option => option,
                StringComparer.Ordinal)
            .ToArray();
        var rootIndexes = child.RootIndexes
            .OrderBy(
                static index => index.IndexName,
                StringComparer.Ordinal)
            .ThenBy(static index => index.IndexOid)
            .ToArray();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = """
            INSERT INTO snapshot_generation_retention_observations (
                cycle_id,
                report_only,
                instrument,
                root_schema,
                root_relation,
                snapshot_parent_oid,
                root_oid,
                root_partition_key,
                root_partition_bound,
                root_tablespace_name,
                root_relation_options,
                root_index_configuration,
                child_schema,
                child_relation,
                snapshot_id,
                child_oid,
                child_relfilenode,
                partition_bound,
                tablespace_name,
                relation_kind,
                persistence_kind,
                access_method,
                relation_options,
                index_configuration,
                stable_child_identity_hash,
                stable_config_schema_hash,
                row_estimate,
                total_bytes,
                observation_metrics_hash,
                planner_live,
                oracle_live,
                classification,
                root_reasons,
                blocker_codes,
                details)
            VALUES (
                @cycleId,
                TRUE,
                @instrument,
                @rootSchema,
                @rootRelation,
                @snapshotParentOid,
                @rootOid,
                @rootPartitionKey,
                @rootPartitionBound,
                @rootTablespaceName,
                @rootRelationOptions,
                @rootIndexConfiguration,
                @childSchema,
                @childRelation,
                @snapshotId,
                @childOid,
                @childRelfilenode,
                @partitionBound,
                @tablespaceName,
                @relationKind,
                @persistenceKind,
                @accessMethod,
                @relationOptions,
                @indexConfiguration,
                @stableChildIdentityHash,
                @stableConfigSchemaHash,
                @rowEstimate,
                @totalBytes,
                @observationMetricsHash,
                @plannerLive,
                @oracleLive,
                @classification,
                @rootReasons,
                @blockerCodes,
                @details)
            RETURNING observation_id
            """;
        command.Parameters.AddWithValue("cycleId", cycleId);
        command.Parameters.AddWithValue(
            "instrument",
            child.InstrumentDefinition.Instrument);
        command.Parameters.AddWithValue(
            "rootSchema",
            child.RootSchema);
        command.Parameters.AddWithValue(
            "rootRelation",
            child.InstrumentDefinition.RootRelation);
        command.Parameters.AddWithValue(
            "snapshotParentOid",
            child.SnapshotParentOid);
        command.Parameters.AddWithValue(
            "rootOid",
            child.RootOid);
        command.Parameters.AddWithValue(
            "rootPartitionKey",
            child.RootPartitionKey);
        command.Parameters.AddWithValue(
            "rootPartitionBound",
            child.RootPartitionBound);
        command.Parameters.AddWithValue(
            "rootTablespaceName",
            child.RootTablespaceName);
        AddJsonParameter(
            command,
            "rootRelationOptions",
            rootRelationOptions);
        AddJsonParameter(
            command,
            "rootIndexConfiguration",
            rootIndexes);
        command.Parameters.AddWithValue(
            "childSchema",
            child.ChildSchema);
        command.Parameters.AddWithValue(
            "childRelation",
            child.ChildRelation);
        command.Parameters.AddWithValue(
            "snapshotId",
            child.SnapshotId);
        command.Parameters.AddWithValue(
            "childOid",
            child.ChildOid);
        command.Parameters.AddWithValue(
            "childRelfilenode",
            child.ChildRelfilenode);
        command.Parameters.AddWithValue(
            "partitionBound",
            child.PartitionBound);
        command.Parameters.AddWithValue(
            "tablespaceName",
            child.TablespaceName);
        command.Parameters.AddWithValue(
            "relationKind",
            child.RelationKind);
        command.Parameters.AddWithValue(
            "persistenceKind",
            child.PersistenceKind);
        command.Parameters.AddWithValue(
            "accessMethod",
            child.AccessMethod);
        AddJsonParameter(
            command,
            "relationOptions",
            relationOptions);
        AddJsonParameter(
            command,
            "indexConfiguration",
            indexes);
        command.Parameters.AddWithValue(
            "stableChildIdentityHash",
            child.StableChildIdentityHash);
        command.Parameters.AddWithValue(
            "stableConfigSchemaHash",
            child.StableConfigSchemaHash);
        command.Parameters.AddWithValue(
            "rowEstimate",
            child.RowEstimate);
        command.Parameters.AddWithValue(
            "totalBytes",
            child.TotalBytes);
        command.Parameters.AddWithValue(
            "observationMetricsHash",
            child.ObservationMetricsHash);
        command.Parameters.AddWithValue(
            "plannerLive",
            evaluation.PlannerLive);
        command.Parameters.AddWithValue(
            "oracleLive",
            evaluation.OracleLive);
        command.Parameters.AddWithValue(
            "classification",
            evaluation.Classification);
        command.Parameters.AddWithValue(
            "rootReasons",
            rootReasons);
        command.Parameters.AddWithValue(
            "blockerCodes",
            blockers
                .Select(static blocker => blocker.Code)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
        AddJsonParameter(
            command,
            "details",
            new
            {
                ChildPhysicalKey = child.PhysicalKey,
                RootReasons = rootReasons,
                Blockers = blockers,
            });
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException(
                "Retention observation insert did not return an identity."));
    }

    private static async Task<(int Sequence, string Hash)>
        AppendEvidenceAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long cycleId,
            long? observationId,
            int previousSequence,
            string? previousHash,
            string phase,
            string kind,
            object payload,
            int commandTimeoutSeconds,
            CancellationToken ct)
    {
        var sequence = checked(previousSequence + 1);
        var payloadJson =
            TierZeroCanonicalJson.SerializeToString(payload);
        using var document = JsonDocument.Parse(payloadJson);
        var currentHash = TierZeroCanonicalJson.Sha256Hex(
            TierZeroCanonicalJson.Serialize(new
            {
                CycleId = cycleId,
                ObservationId = observationId,
                Sequence = sequence,
                Phase = phase,
                Kind = kind,
                Payload = document.RootElement,
                PreviousHash = previousHash,
            }));

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = """
            INSERT INTO snapshot_generation_retention_evidence (
                cycle_id,
                observation_id,
                sequence,
                phase,
                kind,
                payload,
                previous_hash,
                current_hash)
            VALUES (
                @cycleId,
                @observationId,
                @sequence,
                @phase,
                @kind,
                @payload,
                @previousHash,
                @currentHash)
            """;
        command.Parameters.AddWithValue("cycleId", cycleId);
        command.Parameters.AddWithValue(
            "observationId",
            (object?)observationId ?? DBNull.Value);
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
        return (sequence, currentHash);
    }

    private static async Task ApplyWriteTimeoutsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = """
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

    private static void AddJsonParameter(
        NpgsqlCommand command,
        string name,
        object value)
    {
        command.Parameters.Add(name, NpgsqlDbType.Jsonb).Value =
            TierZeroCanonicalJson.SerializeToString(value);
    }

    private static async Task<SnapshotGenerationRetentionCycle?>
        GetCycleForSafePointAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            SnapshotGenerationRetentionPlanRequest request,
            int commandTimeoutSeconds,
            CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = """
            SELECT
                cycle_id,
                trigger_scrape_id,
                trigger_publication_id,
                safe_point_kind,
                safe_point_at,
                planner_version,
                config_version,
                status,
                oracle_agreement,
                candidate_identity_hash,
                observation_hash,
                planner_child_set::TEXT,
                planner_live_set::TEXT,
                planner_candidate_set::TEXT,
                oracle_child_set::TEXT,
                oracle_live_set::TEXT,
                oracle_candidate_set::TEXT,
                candidate_count,
                protected_count,
                blocked_count,
                candidate_bytes,
                global_blockers::TEXT,
                anomalies::TEXT,
                error_message,
                created_at
            FROM snapshot_generation_retention_cycles
            WHERE trigger_scrape_id = @triggerScrapeId
              AND trigger_publication_id =
                    @triggerPublicationId
              AND safe_point_kind = @safePointKind
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
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? ReadCycle(reader)
            : null;
    }

    private static async Task<SnapshotGenerationRetentionCycle>
        GetCycleAsync(
            NpgsqlConnection connection,
            long cycleId,
            int commandTimeoutSeconds,
            CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = """
            SELECT
                cycle_id,
                trigger_scrape_id,
                trigger_publication_id,
                safe_point_kind,
                safe_point_at,
                planner_version,
                config_version,
                status,
                oracle_agreement,
                candidate_identity_hash,
                observation_hash,
                planner_child_set::TEXT,
                planner_live_set::TEXT,
                planner_candidate_set::TEXT,
                oracle_child_set::TEXT,
                oracle_live_set::TEXT,
                oracle_candidate_set::TEXT,
                candidate_count,
                protected_count,
                blocked_count,
                candidate_bytes,
                global_blockers::TEXT,
                anomalies::TEXT,
                error_message,
                created_at
            FROM snapshot_generation_retention_cycles
            WHERE cycle_id = @cycleId
            """;
        command.Parameters.AddWithValue("cycleId", cycleId);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                $"Retention cycle {cycleId} disappeared after insertion.");
        }

        return ReadCycle(reader);
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
            reader.GetString(7),
            reader.GetBoolean(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.GetInt32(17),
            reader.GetInt32(18),
            reader.GetInt32(19),
            reader.GetInt64(20),
            reader.GetString(21),
            reader.GetString(22),
            reader.IsDBNull(23)
                ? null
                : reader.GetString(23),
            reader.GetDateTime(24));
}
