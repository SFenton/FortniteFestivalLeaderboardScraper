using System.Data;
using System.Text.Json;
using FstSnapshotGenerationRestoreContinuation;
using Npgsql;
using NpgsqlTypes;

namespace FstSnapshotGenerationRestoreAuthorization;

public sealed class ContinuationAuthorizationDatabase
    : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    private ContinuationAuthorizationDatabase(
        NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public static ContinuationAuthorizationDatabase
        FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(
            AuthorizationDatabase.ConnectionEnvironment);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{AuthorizationDatabase.ConnectionEnvironment} is required.");
        }
        return FromConnectionString(value);
    }

    public static ContinuationAuthorizationDatabase
        FromConnectionString(string connectionString)
    {
        var builder =
            new NpgsqlConnectionStringBuilder(
                connectionString)
            {
                ApplicationName =
                    "fst-snapshot-restore-continuation-authorizer",
                Timeout = 15,
                CommandTimeout = 30,
                MinPoolSize = 0,
                MaxPoolSize = 2,
                Options =
                    "-c statement_timeout=30000 "
                    + "-c lock_timeout=5000 "
                    + "-c idle_in_transaction_session_timeout=60000 "
                    + "-c transaction_timeout=60000",
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
                "Continuation authorization connection must specify one host, database, and username.");
        }
        return new ContinuationAuthorizationDatabase(
            NpgsqlDataSource.Create(
                builder.ConnectionString));
    }

    public async Task<RestoreContinuationAuthorizationRecord>
        AuthorizeAsync(
            RestoreContinuationAuthorizationRequest request,
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
                fst_authorize_snapshot_generation_restore_continuation(
                    @restoreOperationId,
                    @dropOperationId,
                    @predecessorAuthorizationId,
                    @restorePlanDigest,
                    @restorePlanFileSha256,
                    @restoreReportSha256,
                    @predecessorRestoreToolSha256,
                    @predecessorRepairPackageManifestSha256,
                    @recoveryBundleManifestSha256,
                    @authorizedContinuationToolSha256,
                    @authorizedEvidenceAssemblySha256,
                    @routeParityReferenceSourceSha256,
                    @authorizerBinarySha256,
                    @continuationPackageManifestSha256,
                    @routeParityPreflightSha256,
                    @baselineRouteManifestSha256,
                    @baselineRouteChecksumsSha256,
                    @candidateRouteManifestSha256,
                    @candidateRouteChecksumsSha256,
                    @publicationId,
                    @publishedScrapeId,
                    @repositoryCommit,
                    @repositoryTreeId,
                    @predecessorToContinuationDiffSha256,
                    @sourceManifestSha256,
                    @testEvidenceManifestSha256,
                    @reasonCode,
                    @reasonText,
                    @approvedBy,
                    @reviewedBy,
                    @approvalReference,
                    @canonicalEvidence,
                    @evidenceSha256)
            """;
        AddParameters(command, request);
        var authorizationId =
            (string)(await command.ExecuteScalarAsync(ct)
                ?? throw new InvalidDataException(
                    "Continuation authorization returned no ID."));
        await transaction.CommitAsync(ct);
        var record = await ReadAsync(
            request.RestoreOperationId,
            authorizationId,
            ct);
        if (authorizationId !=
            RestoreContinuationContract
                .DeriveAuthorizationId(
                    request,
                    record.CanonicalEvidenceDbSha256))
        {
            throw new InvalidDataException(
                "Database returned another continuation authorization ID.");
        }
        return record;
    }

    public async Task<RestoreContinuationAuthorizationRecord>
        ReadAsync(
            string restoreOperationId,
            string continuationAuthorizationId,
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var command =
            connection.CreateCommand();
        command.CommandText = """
            SELECT
                continuation_authorization_id,
                restore_operation_id,
                drop_operation_id,
                predecessor_authorization_id,
                restore_plan_digest,
                restore_plan_file_sha256,
                restore_report_sha256,
                predecessor_restore_tool_sha256,
                predecessor_repair_package_manifest_sha256,
                recovery_bundle_manifest_sha256,
                authorized_continuation_tool_sha256,
                authorized_evidence_assembly_sha256,
                route_parity_reference_source_sha256,
                authorizer_binary_sha256,
                continuation_package_manifest_sha256,
                route_parity_algorithm_id,
                route_parity_preflight_sha256,
                baseline_route_manifest_sha256,
                baseline_route_checksums_sha256,
                candidate_route_manifest_sha256,
                candidate_route_checksums_sha256,
                publication_id,
                published_scrape_id,
                repository_commit,
                repository_tree_id,
                predecessor_to_continuation_diff_sha256,
                source_manifest_sha256,
                test_evidence_manifest_sha256,
                reason_code,
                reason_text,
                approved_by,
                reviewed_by,
                approval_reference,
                canonical_evidence,
                evidence_sha256,
                canonical_evidence_db_sha256,
                database_user,
                backend_pid,
                transaction_id,
                authorized_at
            FROM
                snapshot_generation_restore_continuation_authorizations
            WHERE restore_operation_id =
                    @restoreOperationId
              AND continuation_authorization_id =
                    @continuationAuthorizationId
            """;
        command.Parameters.AddWithValue(
            "restoreOperationId",
            restoreOperationId);
        command.Parameters.AddWithValue(
            "continuationAuthorizationId",
            continuationAuthorizationId);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidDataException(
                "Continuation authorization was not found.");
        }
        return new RestoreContinuationAuthorizationRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.GetString(17),
            reader.GetString(18),
            reader.GetString(19),
            reader.GetString(20),
            reader.GetInt64(21),
            reader.GetInt64(22),
            reader.GetString(23),
            reader.GetString(24),
            reader.GetString(25),
            reader.GetString(26),
            reader.GetString(27),
            reader.GetString(28),
            reader.GetString(29),
            reader.GetString(30),
            reader.GetString(31),
            reader.GetString(32),
            JsonDocument.Parse(
                reader.GetFieldValue<string>(33))
                .RootElement.Clone(),
            reader.GetString(34),
            reader.GetString(35),
            reader.GetString(36),
            reader.GetInt32(37),
            reader.GetString(38),
            reader.GetFieldValue<DateTimeOffset>(39));
    }

    public async ValueTask DisposeAsync() =>
        await _dataSource.DisposeAsync();

    private static void AddParameters(
        NpgsqlCommand command,
        RestoreContinuationAuthorizationRequest request)
    {
        command.Parameters.AddWithValue(
            "restoreOperationId",
            request.RestoreOperationId);
        command.Parameters.AddWithValue(
            "dropOperationId",
            request.DropOperationId);
        command.Parameters.AddWithValue(
            "predecessorAuthorizationId",
            request.PredecessorAuthorizationId);
        command.Parameters.AddWithValue(
            "restorePlanDigest",
            request.RestorePlanDigest);
        command.Parameters.AddWithValue(
            "restorePlanFileSha256",
            request.RestorePlanFileSha256);
        command.Parameters.AddWithValue(
            "restoreReportSha256",
            request.RestoreReportSha256);
        command.Parameters.AddWithValue(
            "predecessorRestoreToolSha256",
            request.PredecessorRestoreToolSha256);
        command.Parameters.AddWithValue(
            "predecessorRepairPackageManifestSha256",
            request.PredecessorRepairPackageManifestSha256);
        command.Parameters.AddWithValue(
            "recoveryBundleManifestSha256",
            request.RecoveryBundleManifestSha256);
        command.Parameters.AddWithValue(
            "authorizedContinuationToolSha256",
            request.AuthorizedContinuationToolSha256);
        command.Parameters.AddWithValue(
            "authorizedEvidenceAssemblySha256",
            request.AuthorizedEvidenceAssemblySha256);
        command.Parameters.AddWithValue(
            "routeParityReferenceSourceSha256",
            request.RouteParityReferenceSourceSha256);
        command.Parameters.AddWithValue(
            "authorizerBinarySha256",
            request.AuthorizerBinarySha256);
        command.Parameters.AddWithValue(
            "continuationPackageManifestSha256",
            request.ContinuationPackageManifestSha256);
        command.Parameters.AddWithValue(
            "routeParityPreflightSha256",
            request.RouteParityPreflightSha256);
        command.Parameters.AddWithValue(
            "baselineRouteManifestSha256",
            request.BaselineRouteManifestSha256);
        command.Parameters.AddWithValue(
            "baselineRouteChecksumsSha256",
            request.BaselineRouteChecksumsSha256);
        command.Parameters.AddWithValue(
            "candidateRouteManifestSha256",
            request.CandidateRouteManifestSha256);
        command.Parameters.AddWithValue(
            "candidateRouteChecksumsSha256",
            request.CandidateRouteChecksumsSha256);
        command.Parameters.AddWithValue(
            "publicationId",
            request.PublicationId);
        command.Parameters.AddWithValue(
            "publishedScrapeId",
            request.PublishedScrapeId);
        command.Parameters.AddWithValue(
            "repositoryCommit",
            request.RepositoryCommit);
        command.Parameters.AddWithValue(
            "repositoryTreeId",
            request.RepositoryTreeId);
        command.Parameters.AddWithValue(
            "predecessorToContinuationDiffSha256",
            request.PredecessorToContinuationDiffSha256);
        command.Parameters.AddWithValue(
            "sourceManifestSha256",
            request.SourceManifestSha256);
        command.Parameters.AddWithValue(
            "testEvidenceManifestSha256",
            request.TestEvidenceManifestSha256);
        command.Parameters.AddWithValue(
            "reasonCode",
            request.ReasonCode);
        command.Parameters.AddWithValue(
            "reasonText",
            request.ReasonText);
        command.Parameters.AddWithValue(
            "approvedBy",
            request.ApprovedBy);
        command.Parameters.AddWithValue(
            "reviewedBy",
            request.ReviewedBy);
        command.Parameters.AddWithValue(
            "approvalReference",
            request.ApprovalReference);
        command.Parameters.Add(
            "canonicalEvidence",
            NpgsqlDbType.Jsonb).Value =
            request.CanonicalEvidence.GetRawText();
        command.Parameters.AddWithValue(
            "evidenceSha256",
            request.EvidenceSha256);
    }
}
