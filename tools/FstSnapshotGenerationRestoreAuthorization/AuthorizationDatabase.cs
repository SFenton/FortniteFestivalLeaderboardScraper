using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace FstSnapshotGenerationRestoreAuthorization;

public sealed class AuthorizationDatabase
    : IAsyncDisposable
{
    public const string ConnectionEnvironment =
        "FST_SNAPSHOT_RESTORE_AUTHORIZATION_CONNECTION_STRING";

    private readonly NpgsqlDataSource _dataSource;

    private AuthorizationDatabase(
        NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public static AuthorizationDatabase FromEnvironment()
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

    public static AuthorizationDatabase
        FromConnectionString(string connectionString)
    {
        var builder =
            new NpgsqlConnectionStringBuilder(
                connectionString)
            {
                ApplicationName =
                    "fst-snapshot-restore-authorizer",
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
                "Authorization connection must specify one host, database, and username.");
        }
        return new AuthorizationDatabase(
            NpgsqlDataSource.Create(
                builder.ConnectionString));
    }

    public async Task<RestoreToolAuthorizationRecord>
        AuthorizeAsync(
            RestoreToolAuthorizationRequest request,
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
                fst_authorize_snapshot_generation_restore_tool(
                    @dropOperationId,
                    @dropPlanDigest,
                    @originalBundleManifestSha256,
                    @pinnedRestoreToolSha256,
                    @validatorBaseToolSha256,
                    @authorizedRestoreToolSha256,
                    @authorizedArchiveHelperSha256,
                    @authorizerBinarySha256,
                    @repairPackageManifestSha256,
                    @repositoryCommit,
                    @repositoryTreeId,
                    @pinnedToBaseDiffSha256,
                    @baseToFinalDiffSha256,
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
                    "Authorization function returned no ID."));
        await transaction.CommitAsync(ct);
        var record = await ReadAsync(
            request.DropOperationId,
            authorizationId,
            ct);
        var expected =
            RestoreToolAuthorizationContract
                .DeriveAuthorizationId(
                    request,
                    record.CanonicalEvidenceDbSha256);
        if (authorizationId != expected)
        {
            throw new InvalidDataException(
                "Database returned another authorization ID.");
        }
        return record;
    }

    public async Task<RestoreToolAuthorizationRecord>
        ReadByToolAsync(
            string dropOperationId,
            string authorizedRestoreToolSha256,
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var command =
            connection.CreateCommand();
        command.CommandText = """
            SELECT authorization_id
            FROM
                snapshot_generation_restore_tool_authorizations
            WHERE drop_operation_id =
                    @dropOperationId
              AND authorized_restore_tool_sha256 =
                    @authorizedRestoreToolSha256
            """;
        command.Parameters.AddWithValue(
            "dropOperationId",
            dropOperationId);
        command.Parameters.AddWithValue(
            "authorizedRestoreToolSha256",
            authorizedRestoreToolSha256);
        var authorizationId =
            (string)(await command.ExecuteScalarAsync(ct)
                ?? throw new InvalidDataException(
                    "Restore-tool authorization was not found."));
        return await ReadAsync(
            dropOperationId,
            authorizationId,
            ct);
    }

    public async Task<RestoreToolAuthorizationRecord>
        ReadAsync(
            string dropOperationId,
            string authorizationId,
            CancellationToken ct = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(ct);
        await using var command =
            connection.CreateCommand();
        command.CommandText = """
            SELECT
                authorization_id,
                drop_operation_id,
                drop_plan_digest,
                original_bundle_manifest_sha256,
                pinned_restore_tool_sha256,
                validator_base_tool_sha256,
                authorized_restore_tool_sha256,
                authorized_archive_helper_sha256,
                authorizer_binary_sha256,
                repair_package_manifest_sha256,
                repository_commit,
                repository_tree_id,
                pinned_to_base_diff_sha256,
                base_to_final_diff_sha256,
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
                backend_pid,
                transaction_id,
                authorized_at
            FROM
                snapshot_generation_restore_tool_authorizations
            WHERE drop_operation_id =
                    @dropOperationId
              AND authorization_id =
                    @authorizationId
            """;
        command.Parameters.AddWithValue(
            "dropOperationId",
            dropOperationId);
        command.Parameters.AddWithValue(
            "authorizationId",
            authorizationId);
        await using var reader =
            await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidDataException(
                "Restore-tool authorization was not found.");
        }
        return new RestoreToolAuthorizationRecord(
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
            JsonDocument.Parse(
                reader.GetFieldValue<string>(21))
                .RootElement.Clone(),
            reader.GetString(22),
            reader.GetString(23),
            reader.GetInt32(24),
            reader.GetString(25),
            reader.GetFieldValue<DateTimeOffset>(26));
    }

    public async ValueTask DisposeAsync() =>
        await _dataSource.DisposeAsync();

    private static void AddParameters(
        NpgsqlCommand command,
        RestoreToolAuthorizationRequest request)
    {
        command.Parameters.AddWithValue(
            "dropOperationId",
            request.DropOperationId);
        command.Parameters.AddWithValue(
            "dropPlanDigest",
            request.DropPlanDigest);
        command.Parameters.AddWithValue(
            "originalBundleManifestSha256",
            request.OriginalBundleManifestSha256);
        command.Parameters.AddWithValue(
            "pinnedRestoreToolSha256",
            request.PinnedRestoreToolSha256);
        command.Parameters.AddWithValue(
            "validatorBaseToolSha256",
            request.ValidatorBaseToolSha256);
        command.Parameters.AddWithValue(
            "authorizedRestoreToolSha256",
            request.AuthorizedRestoreToolSha256);
        command.Parameters.AddWithValue(
            "authorizedArchiveHelperSha256",
            request.AuthorizedArchiveHelperSha256);
        command.Parameters.AddWithValue(
            "authorizerBinarySha256",
            request.AuthorizerBinarySha256);
        command.Parameters.AddWithValue(
            "repairPackageManifestSha256",
            request.RepairPackageManifestSha256);
        command.Parameters.AddWithValue(
            "repositoryCommit",
            request.RepositoryCommit);
        command.Parameters.AddWithValue(
            "repositoryTreeId",
            request.RepositoryTreeId);
        command.Parameters.AddWithValue(
            "pinnedToBaseDiffSha256",
            request.PinnedToBaseDiffSha256);
        command.Parameters.AddWithValue(
            "baseToFinalDiffSha256",
            request.BaseToFinalDiffSha256);
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
