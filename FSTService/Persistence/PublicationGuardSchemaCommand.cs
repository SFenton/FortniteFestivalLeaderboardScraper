using System.Text.Json;
using Npgsql;

namespace FSTService.Persistence;

/// <summary>
/// One-shot <c>--initialize-publication-guard-schema-only</c>: applies only the
/// <see cref="PublicationGuardSchema"/> step in one bounded transaction, without
/// loading .env, starting hosted services, or running unrelated schema work.
/// Run it at an idle, unfrozen boundary: dropping the legacy denominator trigger
/// briefly takes an ACCESS EXCLUSIVE lock on <c>account_rankings</c>.
/// </summary>
internal static class PublicationGuardSchemaCommand
{
    internal const string Flag = "--initialize-publication-guard-schema-only";
    internal const string ConnectionEnvironment = "ConnectionStrings__PostgreSQL";
    internal const string ApplicationName = "fst-publication-guard-schema-only";

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
                Multiplexing = false,
                PersistSecurityInfo = false,
                IncludeErrorDetail = false,
                Timeout = 10,
                CommandTimeout = 20,
                SearchPath = "public",
            };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            await DatabaseInitializer.EnsurePublicationGuardSchemaAsync(
                connection.ConnectionString, deadline.Token);
            return await WriteAsync(output, "schema_current", null, 0, transactionCommitted: true);
        }
        catch (ArgumentException)
        {
            return await WriteAsync(output, "refused", "postgresql_connection_invalid", 2);
        }
        catch (PostgresException exception)
        {
            return await WriteAsync(output, "refused", "publication_guard_schema_refused", 2, exception.SqlState);
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
        bool transactionCommitted = false)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            outcome,
            scope = "publication_guard",
            code,
            sqlState,
            hostedServicesStarted = false,
            transactionCommitted,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return exitCode;
    }
}
