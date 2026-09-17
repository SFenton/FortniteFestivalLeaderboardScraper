using System.Text.Json;
using Npgsql;

namespace FSTService.Persistence;

internal static class SnapshotRetentionSchemaCommand
{
    internal const string Flag = "--initialize-snapshot-retention-schema-only";
    internal const string ConnectionEnvironment = "ConnectionStrings__PostgreSQL";
    internal const string ApplicationName = "fst-snapshot-retention-schema-only";

    internal static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(static argument => argument.TrimStart('-', '/').StartsWith(
            Flag[2..], StringComparison.OrdinalIgnoreCase));

    internal static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        string? connectionString,
        TextWriter output,
        CancellationToken ct = default,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? beforeDmlAssertionForTest = null,
        Func<CancellationToken, Task>? afterServerCommitForTest = null,
        Func<Task>? beforeConnectionDisposeForTest = null)
    {
        if (args.Count != 1 || !string.Equals(args[0], Flag, StringComparison.OrdinalIgnoreCase))
            return await WriteAsync(output, "refused", "invalid_command_arguments", 64);
        if (string.IsNullOrWhiteSpace(connectionString))
            return await WriteAsync(output, "refused", "postgresql_connection_missing", 2);

        SnapshotRetentionSchemaDmlProof? acknowledgedProof = null;
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(connectionString)
            {
                ApplicationName = ApplicationName,
                Pooling = false,
                Multiplexing = false,
                PersistSecurityInfo = false,
                IncludeErrorDetail = false,
                Timeout = 10,
                CommandTimeout = 20,
                SearchPath = "pg_catalog,public",
            };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var proof = await DatabaseInitializer.EnsureSnapshotGenerationRetentionSchemaAsync(
                connection.ConnectionString, deadline.Token, beforeDmlAssertionForTest, afterServerCommitForTest,
                beforeConnectionDisposeForTest);
            acknowledgedProof = proof;
            return await WriteAsync(output, "schema_current", null, 0, proof: proof, transactionCommitted: true);
        }
        catch (SnapshotRetentionSchemaCommitOutcomeException exception)
        {
            return await WriteAsync(output,
                exception.TransactionCommitted is true ? "committed_cleanup_unconfirmed" : "uncertain",
                exception.Code, 2, proof: exception.Proof, transactionCommitted: exception.TransactionCommitted,
                possibleSchemaProof: new
                {
                    schemaStep = "snapshot-generation-retention-report-only",
                    schemaSqlSha256 = exception.Proof.SchemaSqlSha256,
                    combinedProofSha256 = exception.Proof.Sha256,
                });
        }
        catch (SnapshotRetentionSchemaDmlRefusal exception)
        {
            return await WriteAsync(output, "refused", exception.Code, 2, proof: exception.Proof);
        }
        catch (Exception exception) when (acknowledgedProof is not null
            && exception is ArgumentException or NpgsqlException or OperationCanceledException)
        {
            return await WriteAsync(output, "committed_cleanup_unconfirmed", "post_commit_cleanup_failed", 2,
                proof: acknowledgedProof, transactionCommitted: true);
        }
        catch (ArgumentException)
        {
            return await WriteAsync(output, "refused", "postgresql_connection_invalid", 2);
        }
        catch (PostgresException exception)
        {
            return await WriteAsync(output, "refused", "retention_schema_refused", 2, exception.SqlState);
        }
        catch (NpgsqlException)
        {
            return await WriteAsync(output, "refused", "postgresql_connection_or_protocol_failed", 2);
        }
        catch (OperationCanceledException)
        {
            return await WriteAsync(output, "refused", "cancelled_or_deadline_exceeded", 130);
        }
    }

    private static async Task<int> WriteAsync(
        TextWriter output,
        string outcome,
        string? code,
        int exitCode,
        string? sqlState = null,
        SnapshotRetentionSchemaDmlProof? proof = null,
        bool? transactionCommitted = false,
        object? possibleSchemaProof = null)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            outcome,
            scope = "snapshot_generation_retention",
            code,
            sqlState,
            hostedServicesStarted = false,
            transactionCommitted,
            dmlProof = proof,
            possibleSchemaProof,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return exitCode;
    }
}
