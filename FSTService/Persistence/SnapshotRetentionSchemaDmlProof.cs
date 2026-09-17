using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FSTService.Persistence.Maintenance;
using Npgsql;

namespace FSTService.Persistence;

internal sealed record SnapshotRetentionSchemaDmlTotals(long Inserted, long Updated, long Deleted);

internal sealed record SnapshotRetentionSchemaRelationDml(
    string Relation, long Inserted, long Updated, long Deleted);

internal sealed record SnapshotRetentionSchemaDmlProof(
    int Version,
    string StatisticsSource,
    string BackendScope,
    string SchemaSqlSha256,
    IReadOnlyList<string> AllowedRetentionRelations,
    SnapshotRetentionSchemaDmlTotals NonRetentionDml,
    IReadOnlyList<SnapshotRetentionSchemaRelationDml> AllowedRetentionChanges,
    SnapshotRetentionSchemaIdentityEvidence NonRetentionRelationIdentity,
    string Sha256);

internal sealed class SnapshotRetentionSchemaDmlRefusal(
    string code, SnapshotRetentionSchemaDmlProof? proof = null)
    : InvalidOperationException("Dedicated retention schema transaction refused: " + code)
{
    internal string Code { get; } = code;
    internal SnapshotRetentionSchemaDmlProof? Proof { get; } = proof;
}

internal sealed class SnapshotRetentionSchemaCommitOutcomeException(
    bool? transactionCommitted,
    SnapshotRetentionSchemaDmlProof proof,
    Exception innerException)
    : InvalidOperationException("Dedicated retention schema commit completion is unconfirmed.", innerException)
{
    internal bool? TransactionCommitted { get; } = transactionCommitted;
    internal SnapshotRetentionSchemaDmlProof Proof { get; } = proof;
    internal string Code => TransactionCommitted is true
        ? "post_commit_cleanup_failed" : "commit_acknowledgement_unknown";
}

internal static class SnapshotRetentionSchemaDmlAssertion
{
    internal const string StatisticsSource = "pg_stat_xact_user_tables";
    internal const string BackendScope = "fresh_unpooled_single_transaction";
    internal static string SchemaSqlSha256 { get; } = Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(SnapshotGenerationRetentionSchema.Sql)));

    // SnapshotGenerationRetentionSchema.Sql installs DDL only; it seeds no user-table rows.
    internal static IReadOnlyList<string> AllowedRetentionRelations { get; } = Array.Empty<string>();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task AcquireAdmissionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await RequireStatisticsAsync(connection, transaction, ct);
        await using (var baseline = connection.CreateCommand())
        {
            baseline.Transaction = transaction;
            baseline.CommandTimeout = 20;
            baseline.CommandText = """
                SELECT NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_stat_xact_user_tables
                    WHERE n_tup_ins>0 OR n_tup_upd>0 OR n_tup_del>0);
                """;
            if (await baseline.ExecuteScalarAsync(ct) is not true)
                throw new SnapshotRetentionSchemaDmlRefusal("transaction_dml_baseline_not_zero");
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 20;
        command.CommandText = $$"""
            SET LOCAL transaction_timeout='30s';
            SET LOCAL idle_in_transaction_session_timeout='20s';
            DO $retention_only_admission$
            BEGIN
                IF NOT pg_catalog.pg_try_advisory_xact_lock(
                    pg_catalog.hashtextextended('fst.snapshot-generation-retention-schema',0))
                THEN
                    RAISE EXCEPTION 'Dedicated retention schema admission is busy.'
                        USING ERRCODE='55P03';
                END IF;
                IF NOT pg_catalog.pg_try_advisory_xact_lock(
                    {{RegistrationMutationGate.AdvisoryLockKey.ToString(CultureInfo.InvariantCulture)}})
                THEN
                    RAISE EXCEPTION 'Dedicated retention schema registration admission is busy.'
                        USING ERRCODE='55P03';
                END IF;
            END
            $retention_only_admission$;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task<SnapshotRetentionSchemaDmlProof> AssertBeforeCommitAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyList<SnapshotRetentionSchemaTableIdentity> identitiesBefore, CancellationToken ct)
    {
        await RequireStatisticsAsync(connection, transaction, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 20;
        command.CommandText = """
            SELECT schemaname::text,relname::text,n_tup_ins,n_tup_upd,n_tup_del
            FROM pg_catalog.pg_stat_xact_user_tables
            WHERE n_tup_ins>0 OR n_tup_upd>0 OR n_tup_del>0
            ORDER BY schemaname::text COLLATE "C",relname::text COLLATE "C";
            """;
        var allowed = new List<SnapshotRetentionSchemaRelationDml>();
        long inserted = 0, updated = 0, deleted = 0;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var relation = reader.GetString(0) + "." + reader.GetString(1);
                var row = new SnapshotRetentionSchemaRelationDml(
                    relation, reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
                if (AllowedRetentionRelations.Contains(relation, StringComparer.Ordinal))
                    allowed.Add(row);
                else
                {
                    inserted = checked(inserted + row.Inserted);
                    updated = checked(updated + row.Updated);
                    deleted = checked(deleted + row.Deleted);
                }
            }
        }
        var identitiesAfter = await SnapshotRetentionSchemaRelationIdentity.CaptureAsync(connection, transaction, ct);
        var identity = SnapshotRetentionSchemaRelationIdentity.Compare(identitiesBefore, identitiesAfter);
        var totals = new SnapshotRetentionSchemaDmlTotals(inserted, updated, deleted);
        var canonical = JsonSerializer.Serialize(new
        {
            version = 2,
            statisticsSource = StatisticsSource,
            backendScope = BackendScope,
            schemaSqlSha256 = SchemaSqlSha256,
            allowedRetentionRelations = AllowedRetentionRelations,
            nonRetentionDml = totals,
            allowedRetentionChanges = allowed,
            nonRetentionRelationIdentity = identity,
        }, JsonOptions);
        var proof = new SnapshotRetentionSchemaDmlProof(
            2, StatisticsSource, BackendScope, SchemaSqlSha256, AllowedRetentionRelations, totals, allowed.AsReadOnly(), identity,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
        if (!identity.Unchanged)
            throw new SnapshotRetentionSchemaDmlRefusal("non_retention_relation_identity_changed", proof);
        if (inserted != 0 || updated != 0 || deleted != 0)
            throw new SnapshotRetentionSchemaDmlRefusal("non_retention_dml_detected", proof);
        return proof;
    }

    private static async Task RequireStatisticsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("The schema DML proof requires its exact active transaction.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 20;
        command.CommandText = """
            SELECT pg_catalog.current_setting('track_counts')='on'
              AND pg_catalog.current_setting('server_version_num')::integer >= 170000
              AND pg_catalog.current_setting('server_version_num')::integer < 180000;
            """;
        if (await command.ExecuteScalarAsync(ct) is not true)
            throw new SnapshotRetentionSchemaDmlRefusal("transaction_statistics_unavailable");
    }
}
