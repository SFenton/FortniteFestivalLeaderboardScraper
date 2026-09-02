using System.Buffers;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FstSnapshotGenerationQuarantine;
using Npgsql;

namespace FstSnapshotGenerationRestoreContinuation;

public sealed record RestoreContinuationFingerprint(
    string Algorithm,
    string Sha256,
    long RowCount,
    long StreamBytes);

public sealed record RestoreContinuationAttestationResult(
    JsonElement State,
    RestoreContinuationFingerprint Fingerprint,
    string EvidenceSha256);

public sealed class ContinuationDatabase
    : IAsyncDisposable
{
    public const string ConnectionEnvironment =
        "FST_SNAPSHOT_RESTORE_CONTINUATION_CONNECTION_STRING";

    private readonly NpgsqlDataSource _dataSource;

    private ContinuationDatabase(
        NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public static ContinuationDatabase FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(
            ConnectionEnvironment);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{ConnectionEnvironment} is required.");
        }
        return FromConnectionString(value);
    }

    public static ContinuationDatabase
        FromConnectionString(string connectionString)
    {
        var builder =
            new NpgsqlConnectionStringBuilder(
                connectionString)
            {
                ApplicationName =
                    "fst-snapshot-restore-continuation",
                Timeout = 15,
                CommandTimeout = 180,
                MinPoolSize = 0,
                MaxPoolSize = 2,
                Options =
                    "-c statement_timeout=180000 "
                    + "-c lock_timeout=5000 "
                    + "-c idle_in_transaction_session_timeout=240000 "
                    + "-c transaction_timeout=240000",
            };
        if ((builder.Host ?? "").Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
                .Length != 1
            || string.IsNullOrWhiteSpace(
                builder.Database)
            || string.IsNullOrWhiteSpace(
                builder.Username))
        {
            throw new InvalidOperationException(
                "Continuation connection must specify one host, database, and username.");
        }
        return new ContinuationDatabase(
            NpgsqlDataSource.Create(
                builder.ConnectionString));
    }

    public async Task<JsonElement> ReadStateAsync(
        string restoreOperationId,
        string continuationAuthorizationId,
        CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        return await ReadStateAsync(
            connection,
            transaction: null,
            restoreOperationId,
            continuationAuthorizationId,
            ct);
    }

    public async Task<RestoreContinuationAttestationResult>
        AttestAsync(
            RestoreContinuationPackageManifest manifest,
            string continuationAuthorizationId,
            ShopDailyInventoryRolloverEvidence
                historicalBridge,
            DetailedRouteParityEvidence parity,
            string attestedBy,
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                ct);
        var state = await ReadStateAsync(
            connection,
            transaction,
            manifest.RestoreOperationId,
            continuationAuthorizationId,
            ct);
        ValidateState(
            manifest,
            continuationAuthorizationId,
            state);
        var childSchema = RequireString(
            state,
            "childSchema");
        var childRelation = RequireString(
            state,
            "childRelation");
        await using (var lockCommand =
                     connection.CreateCommand())
        {
            lockCommand.Transaction = transaction;
            lockCommand.CommandText =
                $"LOCK TABLE ONLY {Quote(childSchema)}.{Quote(childRelation)} IN SHARE MODE";
            await lockCommand.ExecuteNonQueryAsync(ct);
        }
        var fingerprint = await FingerprintAsync(
            connection,
            transaction,
            childSchema,
            childRelation,
            ct);
        if (fingerprint.RowCount !=
                state.GetProperty("rowCount")
                    .GetInt64()
            || fingerprint.Sha256 !=
                RequireString(
                    state,
                    "rowFingerprintSha256"))
        {
            throw new InvalidDataException(
                "Continuation fingerprint differs from the immutable restore row.");
        }
        var databaseEvidence =
            JsonSerializer.SerializeToElement(
                new
                {
                    State = state,
                    Fingerprint = fingerprint,
                });
        var evidenceSha256 =
            RestoreContinuationContract.Sha256(
                new
                {
                    HistoricalTemporalBridge =
                        historicalBridge,
                    Parity = parity,
                    Database = databaseEvidence,
                });
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                fst_record_snapshot_generation_restore_attestation(
                    @restoreOperationId,
                    @publicationId,
                    @publishedScrapeId,
                    55,
                    @baselineRouteManifestSha256,
                    @candidateRouteManifestSha256,
                    @routeSemanticEvidenceSha256,
                    @temporalBridgeEvidenceSha256,
                    @databaseEvidence::jsonb,
                    @evidenceSha256,
                    @attestedBy,
                    @evidenceToolSha256,
                    @continuationAuthorizationId)
            """;
        command.Parameters.AddWithValue(
            "restoreOperationId",
            manifest.RestoreOperationId);
        command.Parameters.AddWithValue(
            "publicationId",
            manifest.PublicationId);
        command.Parameters.AddWithValue(
            "publishedScrapeId",
            manifest.PublishedScrapeId);
        command.Parameters.AddWithValue(
            "baselineRouteManifestSha256",
            manifest.BaselineRouteManifestSha256);
        command.Parameters.AddWithValue(
            "candidateRouteManifestSha256",
            manifest.CandidateRouteManifestSha256);
        command.Parameters.AddWithValue(
            "routeSemanticEvidenceSha256",
            parity.RouteSemanticEvidenceSha256);
        command.Parameters.AddWithValue(
            "temporalBridgeEvidenceSha256",
            manifest.TemporalBridgeEvidenceSha256);
        command.Parameters.AddWithValue(
            "databaseEvidence",
            databaseEvidence.GetRawText());
        command.Parameters.AddWithValue(
            "evidenceSha256",
            evidenceSha256);
        command.Parameters.AddWithValue(
            "attestedBy",
            attestedBy);
        command.Parameters.AddWithValue(
            "evidenceToolSha256",
            manifest.AuthorizedContinuationToolSha256);
        command.Parameters.AddWithValue(
            "continuationAuthorizationId",
            continuationAuthorizationId);
        _ = await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidDataException(
                "Continuation attestation returned no operation ID.");
        await transaction.CommitAsync(ct);
        return new RestoreContinuationAttestationResult(
            state,
            fingerprint,
            evidenceSha256);
    }

    public async Task FinalizeAsync(
        RestoreContinuationPackageManifest manifest,
        string continuationAuthorizationId,
        string finalizedBy,
        string finalizeReference,
        JsonElement finalizationEvidence,
        CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                ct);
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                fst_finalize_snapshot_generation_restore(
                    @restoreOperationId,
                    @finalizedBy,
                    @finalizeReference,
                    @finalizationEvidence::jsonb,
                    @evidenceToolSha256,
                    @continuationAuthorizationId)
            """;
        command.Parameters.AddWithValue(
            "restoreOperationId",
            manifest.RestoreOperationId);
        command.Parameters.AddWithValue(
            "finalizedBy",
            finalizedBy);
        command.Parameters.AddWithValue(
            "finalizeReference",
            finalizeReference);
        command.Parameters.AddWithValue(
            "finalizationEvidence",
            finalizationEvidence.GetRawText());
        command.Parameters.AddWithValue(
            "evidenceToolSha256",
            manifest.AuthorizedContinuationToolSha256);
        command.Parameters.AddWithValue(
            "continuationAuthorizationId",
            continuationAuthorizationId);
        _ = await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidDataException(
                "Continuation finalization returned no operation ID.");
        await transaction.CommitAsync(ct);
    }

    public async ValueTask DisposeAsync() =>
        await _dataSource.DisposeAsync();

    private static async Task<JsonElement>
        ReadStateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            string restoreOperationId,
            string continuationAuthorizationId,
            CancellationToken ct)
    {
        await using var command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT jsonb_build_object(
                'restoreOperationId',
                    restore_row.restore_operation_id,
                'dropOperationId',
                    restore_row.drop_operation_id,
                'planDigest',
                    restore_row.plan_digest,
                'predecessorAuthorizationId',
                    restore_row.authorization_id,
                'predecessorRestoreToolSha256',
                    restore_row.executing_tool_sha256,
                'recoveryBundleManifestSha256',
                    restore_row.recovery_bundle_manifest_sha256,
                'childSchema',
                    restore_row.child_schema,
                'childRelation',
                    restore_row.child_relation,
                'restoredChildOid',
                    restore_row.restored_child_oid,
                'restoredChildRelfilenode',
                    restore_row.restored_child_relfilenode,
                'rootOid',
                    restore_row.root_oid,
                'partitionBound',
                    restore_row.partition_bound,
                'originalIdentityMatches', EXISTS (
                    SELECT 1
                    FROM pg_class child
                    JOIN pg_namespace namespace
                      ON namespace.oid =
                            child.relnamespace
                    JOIN pg_inherits inheritance
                      ON inheritance.inhrelid =
                            child.oid
                    WHERE child.oid =
                            restore_row.restored_child_oid
                      AND child.relfilenode::BIGINT =
                            restore_row.restored_child_relfilenode
                      AND namespace.nspname =
                            restore_row.child_schema
                      AND child.relname =
                            restore_row.child_relation
                      AND inheritance.inhparent =
                            restore_row.root_oid
                      AND pg_get_expr(
                            child.relpartbound,
                            child.oid,
                            TRUE) =
                            restore_row.partition_bound),
                'attachedIndexCount', (
                    SELECT COUNT(*)::INTEGER
                    FROM pg_index child_index
                    JOIN pg_inherits child_index_inheritance
                      ON child_index_inheritance.inhrelid =
                            child_index.indexrelid
                    JOIN pg_index root_index
                      ON root_index.indexrelid =
                            child_index_inheritance.inhparent
                     AND root_index.indrelid =
                            restore_row.root_oid
                    JOIN pg_inherits root_index_inheritance
                      ON root_index_inheritance.inhrelid =
                            root_index.indexrelid
                    JOIN pg_class top_index_relation
                      ON top_index_relation.oid =
                            root_index_inheritance.inhparent
                    WHERE child_index.indrelid =
                            restore_row.restored_child_oid
                      AND child_index.indisvalid
                      AND child_index.indisready
                      AND root_index.indisvalid
                      AND root_index.indisready
                      AND top_index_relation.relname IN (
                            'leaderboard_entries_snapshot_pkey',
                            'ix_les_snapshot_song_score')),
                'mutationGuardPresent', EXISTS (
                    SELECT 1
                    FROM pg_trigger trigger_row
                    WHERE trigger_row.tgrelid =
                            restore_row.restored_child_oid
                      AND trigger_row.tgname =
                            'trg_sgr_' ||
                            restore_row.snapshot_id::TEXT ||
                            '_' ||
                            left(
                                restore_row.restore_operation_id,
                                12)
                      AND NOT trigger_row.tgisinternal
                      AND trigger_row.tgenabled = 'O'
                      AND trigger_row.tgfoid =
                            'public.fst_reject_snapshot_generation_quarantine_relation_mutation()'
                                ::regprocedure),
                'oldOidExists', EXISTS (
                    SELECT 1
                    FROM pg_class old_child
                    WHERE old_child.oid =
                            drop_row.child_oid),
                'defaultFencePresent', EXISTS (
                    SELECT 1
                    FROM pg_constraint constraint_row
                    WHERE constraint_row.conrelid =
                            drop_row.default_partition_oid
                      AND constraint_row.conname =
                            drop_row.durable_default_exclusion_constraint),
                'defaultPartitionSchema',
                    drop_row.default_partition_schema,
                'defaultPartitionRelation',
                    drop_row.default_partition_relation,
                'rowCount',
                    restore_row.row_count,
                'rowFingerprintSha256',
                    restore_row.row_fingerprint_sha256,
                'logicalCatalogSha256',
                    restore_row.logical_catalog_sha256,
                'semanticCatalogSha256',
                    restore_row.semantic_catalog_sha256,
                'logicalIndexShapeSha256',
                    restore_row.logical_index_shape_sha256,
                'holdActive', EXISTS (
                    SELECT 1
                    FROM snapshot_generation_retention_holds
                        hold_row
                    WHERE hold_row.hold_id =
                            restore_row.hold_id
                      AND hold_row.released_at IS NULL))
                || jsonb_build_object(
                'attested', EXISTS (
                    SELECT 1
                    FROM snapshot_generation_restore_attestations
                        attestation
                    WHERE attestation.restore_operation_id =
                            restore_row.restore_operation_id),
                'attestationAuthorizationId', (
                    SELECT
                        attestation.continuation_authorization_id
                    FROM snapshot_generation_restore_attestations
                        attestation
                    WHERE attestation.restore_operation_id =
                            restore_row.restore_operation_id),
                'attestationEvidenceToolSha256', (
                    SELECT
                        attestation.evidence_tool_sha256
                    FROM snapshot_generation_restore_attestations
                        attestation
                    WHERE attestation.restore_operation_id =
                            restore_row.restore_operation_id),
                'finalized', EXISTS (
                    SELECT 1
                    FROM snapshot_generation_restore_finalizations
                        finalization
                    WHERE finalization.restore_operation_id =
                            restore_row.restore_operation_id),
                'finalizationAuthorizationId', (
                    SELECT
                        finalization.continuation_authorization_id
                    FROM snapshot_generation_restore_finalizations
                        finalization
                    WHERE finalization.restore_operation_id =
                            restore_row.restore_operation_id),
                'finalizationEvidenceToolSha256', (
                    SELECT
                        finalization.evidence_tool_sha256
                    FROM snapshot_generation_restore_finalizations
                        finalization
                    WHERE finalization.restore_operation_id =
                            restore_row.restore_operation_id),
                'continuationAuthorizationId',
                    authorization_row.continuation_authorization_id,
                'authorizedContinuationToolSha256',
                    authorization_row.authorized_continuation_tool_sha256,
                'authorizedEvidenceAssemblySha256',
                    authorization_row.authorized_evidence_assembly_sha256,
                'restorePlanFileSha256',
                    authorization_row.restore_plan_file_sha256,
                'restoreReportSha256',
                    authorization_row.restore_report_sha256,
                'predecessorRepairPackageManifestSha256',
                    authorization_row.predecessor_repair_package_manifest_sha256,
                'continuationPackageManifestSha256',
                    authorization_row.continuation_package_manifest_sha256,
                'routeParityAlgorithmId',
                    authorization_row.route_parity_algorithm_id,
                'routeParityPreflightSha256',
                    authorization_row.route_parity_preflight_sha256,
                'stabilizedRouteSemanticEvidenceSha256',
                    authorization_row.stabilized_route_semantic_evidence_sha256,
                'temporalBridgePredicateId',
                    authorization_row.temporal_bridge_predicate_id,
                'temporalBridgeEvidenceSha256',
                    authorization_row.temporal_bridge_evidence_sha256,
                'restoreScopeIsolationEvidenceSha256',
                    authorization_row.restore_scope_isolation_evidence_sha256,
                'serviceRuntimeIsolationEvidenceSha256',
                    authorization_row.service_runtime_isolation_evidence_sha256,
                'historicalBaselineRouteManifestSha256',
                    authorization_row.historical_baseline_route_manifest_sha256,
                'baselineRouteManifestSha256',
                    authorization_row.baseline_route_manifest_sha256,
                'candidateRouteManifestSha256',
                    authorization_row.candidate_route_manifest_sha256,
                'publicationId',
                    authorization_row.publication_id,
                'publishedScrapeId',
                    authorization_row.published_scrape_id,
                'authorizedAt',
                    authorization_row.authorized_at,
                'currentPublicationId',
                    state_row.current_publication_id,
                'currentPublishedScrapeId',
                    state_row.published_scrape_id,
                'publicReadsFrozen',
                    state_row.public_reads_frozen,
                'workingPublicationId',
                    state_row.working_publication_id,
                'publicationCommitIntentActive',
                    state_row.publication_commit_intent_started_at
                        IS NOT NULL,
                'maxScoreMutationGateActive',
                    state_row.max_score_mutation_gate_token
                        IS NOT NULL,
                'runningScrape', EXISTS (
                    SELECT 1
                    FROM scrape_log scrape
                    WHERE scrape.status = 'running'),
                'workerOffline', EXISTS (
                    SELECT 1
                    FROM service_worker_status worker
                    WHERE worker.worker_key = 'scraper'
                      AND worker.status = 'offline'
                      AND worker.current_operation_json
                            IS NULL))
            FROM snapshot_generation_restore_operations
                restore_row
            JOIN snapshot_generation_drop_operations
                drop_row
              ON drop_row.drop_operation_id =
                    restore_row.drop_operation_id
            JOIN
                snapshot_generation_restore_continuation_authorizations
                    authorization_row
              ON authorization_row.restore_operation_id =
                    restore_row.restore_operation_id
             AND authorization_row.continuation_authorization_id =
                    @continuationAuthorizationId
            JOIN scrape_publication_state state_row
              ON state_row.id = TRUE
            WHERE restore_row.restore_operation_id =
                    @restoreOperationId
            """;
        command.Parameters.AddWithValue(
            "restoreOperationId",
            restoreOperationId);
        command.Parameters.AddWithValue(
            "continuationAuthorizationId",
            continuationAuthorizationId);
        var json =
            (string?)(await command.ExecuteScalarAsync(ct));
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException(
                "Continuation authorization or restore row was not found.");
        }
        var state = JsonNode.Parse(json)
            ?.AsObject()
            ?? throw new InvalidDataException(
                "Continuation state JSON is invalid.");
        var defaultSchema =
            state["defaultPartitionSchema"]
                ?.GetValue<string>()
            ?? throw new InvalidDataException(
                "Continuation default schema is missing.");
        var defaultRelation =
            state["defaultPartitionRelation"]
                ?.GetValue<string>()
            ?? throw new InvalidDataException(
                "Continuation default relation is missing.");
        await using var count =
            connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText =
            $"SELECT COUNT(*)::BIGINT FROM ONLY {Quote(defaultSchema)}.{Quote(defaultRelation)}";
        state["defaultRowCount"] =
            Convert.ToInt64(
                await count.ExecuteScalarAsync(ct));
        return JsonSerializer.SerializeToElement(
            state);
    }

    private static async Task<RestoreContinuationFingerprint>
        FingerprintAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string schema,
            string relation,
            CancellationToken ct)
    {
        var copySql = $"""
            COPY (
                SELECT to_jsonb(row_value)::text
                FROM ONLY {Quote(schema)}.{Quote(relation)}
                    AS row_value
                ORDER BY
                    snapshot_id,
                    song_id,
                    instrument,
                    account_id
            ) TO STDOUT
            """;
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        var chars = ArrayPool<char>.Shared.Rent(
            64 * 1024);
        var bytes = ArrayPool<byte>.Shared.Rent(
            Encoding.UTF8.GetMaxByteCount(chars.Length));
        long streamBytes = 0;
        try
        {
            using var reader =
                await connection.BeginTextExportAsync(
                    copySql,
                    ct);
            var encoder = Encoding.UTF8.GetEncoder();
            while (true)
            {
                var read = await reader.ReadAsync(
                    chars.AsMemory(),
                    ct);
                if (read == 0)
                    break;
                encoder.Convert(
                    chars,
                    0,
                    read,
                    bytes,
                    0,
                    bytes.Length,
                    flush: false,
                    out _,
                    out var used,
                    out _);
                hash.AppendData(bytes, 0, used);
                streamBytes += used;
            }
            encoder.Convert(
                [],
                0,
                0,
                bytes,
                0,
                bytes.Length,
                flush: true,
                out _,
                out var finalBytes,
                out _);
            if (finalBytes > 0)
            {
                hash.AppendData(
                    bytes,
                    0,
                    finalBytes);
                streamBytes += finalBytes;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
            ArrayPool<byte>.Shared.Return(bytes);
        }
        await using var count =
            connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText =
            $"SELECT COUNT(*)::BIGINT FROM ONLY {Quote(schema)}.{Quote(relation)}";
        var rowCount = Convert.ToInt64(
            await count.ExecuteScalarAsync(ct));
        return new RestoreContinuationFingerprint(
            "sha256-copy-to-jsonb-text-ordered-snapshot_id-song_id-instrument-account_id-v1",
            Convert.ToHexString(
                    hash.GetHashAndReset())
                .ToLowerInvariant(),
            rowCount,
            streamBytes);
    }

    private static void ValidateState(
        RestoreContinuationPackageManifest manifest,
        string continuationAuthorizationId,
        JsonElement state)
    {
        if (RequireString(
                state,
                "restoreOperationId") !=
                manifest.RestoreOperationId
            || RequireString(
                state,
                "dropOperationId") !=
                manifest.DropOperationId
            || RequireString(
                state,
                "planDigest") !=
                manifest.RestorePlanDigest
            || RequireString(
                state,
                "predecessorAuthorizationId") !=
                manifest.PredecessorAuthorizationId
            || RequireString(
                state,
                "predecessorRestoreToolSha256") !=
                manifest.PredecessorRestoreToolSha256
            || RequireString(
                state,
                "recoveryBundleManifestSha256") !=
                manifest.RecoveryBundleManifestSha256
            || RequireString(
                state,
                "continuationAuthorizationId") !=
                continuationAuthorizationId
            || RequireString(
                state,
                "authorizedContinuationToolSha256") !=
                manifest.AuthorizedContinuationToolSha256
            || RequireString(
                state,
                "authorizedEvidenceAssemblySha256") !=
                manifest.AuthorizedEvidenceAssemblySha256
            || RequireString(
                state,
                "stabilizedRouteSemanticEvidenceSha256") !=
                manifest
                    .StabilizedRouteSemanticEvidenceSha256
            || RequireString(
                state,
                "temporalBridgePredicateId") !=
                manifest.TemporalBridgePredicateId
            || RequireString(
                state,
                "temporalBridgeEvidenceSha256") !=
                manifest.TemporalBridgeEvidenceSha256
            || RequireString(
                state,
                "restoreScopeIsolationEvidenceSha256") !=
                manifest.RestoreScopeIsolationEvidenceSha256
            || RequireString(
                state,
                "serviceRuntimeIsolationEvidenceSha256") !=
                manifest.ServiceRuntimeIsolationEvidenceSha256
            || RequireString(
                state,
                "historicalBaselineRouteManifestSha256") !=
                manifest.HistoricalBaselineRouteManifestSha256
            || RequireString(
                state,
                "baselineRouteManifestSha256") !=
                manifest.BaselineRouteManifestSha256
            || RequireString(
                state,
                "candidateRouteManifestSha256") !=
                manifest.CandidateRouteManifestSha256
            || state.GetProperty("holdActive")
                    .GetBoolean() is not true)
        {
            throw new InvalidDataException(
                "Continuation database state differs from its package.");
        }
    }

    public static string RequireString(
        JsonElement value,
        string propertyName) =>
        value.TryGetProperty(
                propertyName,
                out var property)
            ? property.GetString()
              ?? throw new InvalidDataException(
                  $"{propertyName} is null.")
            : throw new InvalidDataException(
                $"{propertyName} is missing.");

    private static string Quote(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)
            || identifier.Any(character =>
                character is not (
                    >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '_'))
            || identifier[0] is >= '0' and <= '9')
        {
            throw new InvalidDataException(
                "Database identifier is invalid.");
        }
        return '"' + identifier.Replace(
            "\"",
            "\"\"",
            StringComparison.Ordinal) + '"';
    }
}
