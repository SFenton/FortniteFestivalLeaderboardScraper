using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace FSTService.Persistence;

internal sealed record SnapshotRetentionSchemaTableIdentity(
    string Schema, string Relation, long Oid, long Relfilenode, string Kind);

internal sealed record SnapshotRetentionSchemaIdentityEvidence(
    string Scope,
    IReadOnlyList<string> RelationKinds,
    IReadOnlyList<string> ExcludedRetentionRelations,
    int BeforeCount,
    int AfterCount,
    string BeforeSha256,
    string AfterSha256,
    bool Unchanged);

internal static class SnapshotRetentionSchemaRelationIdentity
{
    internal const string Scope = "non_system_non_temporary_user_tables";
    internal static IReadOnlyList<string> RelationKinds { get; } = Array.AsReadOnly(new[] { "f", "m", "p", "r" });
    private static readonly string[] RetentionNames =
    [
        "snapshot_generation_retention_cycles",
        "snapshot_generation_retention_deferrals",
        "snapshot_generation_retention_evidence",
        "snapshot_generation_retention_holds",
        "snapshot_generation_retention_observations",
        "snapshot_generation_retention_worker_configuration",
    ];
    internal static IReadOnlyList<string> ExcludedRetentionRelations { get; } =
        Array.AsReadOnly(RetentionNames.Select(static name => "public." + name).ToArray());
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<IReadOnlyList<SnapshotRetentionSchemaTableIdentity>> CaptureAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 20;
        command.CommandText = """
            SELECT n.nspname::text,c.relname::text,c.oid::bigint,c.relfilenode::bigint,c.relkind::text
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE c.relkind IN ('r','p','m','f')
              AND c.relpersistence<>'t'
              AND n.nspname NOT IN ('pg_catalog','information_schema','pg_toast')
              AND NOT (n.nspname='public' AND c.relname=ANY(@retentionNames))
            ORDER BY n.nspname::text COLLATE "C",c.relname::text COLLATE "C",c.oid;
            """;
        command.Parameters.AddWithValue("retentionNames", RetentionNames);
        var result = new List<SnapshotRetentionSchemaTableIdentity>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                reader.GetInt64(3), reader.GetString(4)));
        return result.AsReadOnly();
    }

    internal static SnapshotRetentionSchemaIdentityEvidence Compare(
        IReadOnlyList<SnapshotRetentionSchemaTableIdentity> before,
        IReadOnlyList<SnapshotRetentionSchemaTableIdentity> after) =>
        new(Scope, RelationKinds, ExcludedRetentionRelations, before.Count, after.Count,
            Digest(before), Digest(after), before.SequenceEqual(after));

    private static string Digest(IReadOnlyList<SnapshotRetentionSchemaTableIdentity> identities) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identities, JsonOptions))));
}
