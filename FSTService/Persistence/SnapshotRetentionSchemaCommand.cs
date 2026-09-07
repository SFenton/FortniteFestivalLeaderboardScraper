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
        CancellationToken ct = default)
    {
        if (args.Count != 1 || !string.Equals(args[0], Flag, StringComparison.OrdinalIgnoreCase))
            return await WriteAsync(output, "refused", "invalid_command_arguments", 64);
        if (string.IsNullOrWhiteSpace(connectionString))
            return await WriteAsync(output, "refused", "postgresql_connection_missing", 2);

        try
        {
            var connection = new NpgsqlConnectionStringBuilder(connectionString)
            {
                ApplicationName = ApplicationName,
                Pooling = false,
                Timeout = 10,
                CommandTimeout = 20,
                SearchPath = "pg_catalog,public",
            };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            await using (var dataSource = NpgsqlDataSource.Create(connection.ConnectionString))
            {
                await DatabaseInitializer.EnsureSnapshotGenerationRetentionSchemaAsync(
                    dataSource, deadline.Token);
            }
            return await WriteAsync(output, "schema_current", null, 0);
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
        string? sqlState = null)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            outcome,
            scope = "snapshot_generation_retention",
            code,
            sqlState,
            hostedServicesStarted = false,
        }));
        return exitCode;
    }
}
