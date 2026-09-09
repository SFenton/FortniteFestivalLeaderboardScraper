using System.Diagnostics;
using Npgsql;

namespace FSTService.Persistence.Maintenance;

public sealed record SnapshotGenerationRetentionPhaseTiming(string Phase, double ElapsedMilliseconds);

public sealed record SnapshotGenerationRetentionTransactionOwner(
    int BackendPid, DateTime BackendStartedAtUtc, DateTime TransactionStartedAtUtc);

public sealed record SnapshotGenerationRetentionOfflineCompletion(
    string DatabaseSignature,
    IReadOnlyList<SnapshotGenerationRetentionTransactionOwner> Owners);

internal sealed class SnapshotGenerationRetentionTiming
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly List<SnapshotGenerationRetentionPhaseTiming> _phases = [];
    internal string Phase { get; private set; } = "initializing";
    internal double ElapsedMilliseconds => Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
    internal IReadOnlyList<SnapshotGenerationRetentionPhaseTiming> Snapshot() => _phases.ToArray();

    internal IDisposable Measure(string phase)
    {
        Phase = phase;
        return new Measurement(this, phase);
    }

    private sealed class Measurement(SnapshotGenerationRetentionTiming owner, string phase) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        public void Dispose() =>
            owner._phases.Add(new(phase, Stopwatch.GetElapsedTime(_started).TotalMilliseconds));
    }
}

public sealed partial class SnapshotGenerationRetentionPlanner
{
    internal TimeSpan OfflineObservationBudget { get; set; } =
        TimeSpan.FromSeconds(OfflineTotalTimeoutSeconds);
    internal int OfflineFenceTransactionTimeoutSeconds { get; set; } = OfflineTotalTimeoutSeconds;
    internal Func<Task>? OfflinePostCommitCleanupTestHook { get; set; }
    internal Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>?
        OfflineFenceAdmittedTestHook { get; set; }

    internal static async Task<string> CaptureOfflineDatabaseSignatureAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 5;
        command.CommandText = """
            SELECT jsonb_build_object(
                'database',current_database(),
                'oid',(SELECT oid::BIGINT FROM pg_catalog.pg_database WHERE datname=current_database()),
                'systemIdentifier',(SELECT system_identifier::TEXT FROM pg_catalog.pg_control_system()),
                'postmasterStartedAt',pg_catalog.pg_postmaster_start_time(),
                'dataDirectory',current_setting('data_directory'),
                'role',current_user)::TEXT
            """;
        return (string)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Offline database identity is unavailable."));
    }

    internal static async Task RequireOfflineDatabaseSignatureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string expected,
        CancellationToken ct)
    {
        if (!string.Equals(
                await CaptureOfflineDatabaseSignatureAsync(
                    connection,
                    transaction,
                    ct),
                expected,
                StringComparison.Ordinal))
        {
            throw new SnapshotGenerationRetentionOfflineRefusal(
                "offline_database_identity_changed");
        }
    }

    private static async Task<SnapshotGenerationRetentionTransactionOwner> CaptureOfflineOwnerAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 5;
        command.CommandText = """
            SELECT pid,backend_start,xact_start
            FROM pg_catalog.pg_stat_activity WHERE pid=pg_catalog.pg_backend_pid()
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(2))
            throw new InvalidOperationException("Offline transaction identity is unavailable.");
        return new(reader.GetInt32(0), reader.GetDateTime(1), reader.GetDateTime(2));
    }

    internal static NpgsqlConnection CreateOfflineCommitReconciliationConnection(
        PostgresUnpooledConnectionFactory dedicatedConnections) =>
        dedicatedConnections.CreateHostConnection(
            5, 5, "-c statement_timeout=5s -c lock_timeout=2s -c transaction_timeout=15s");

    public static async Task<SnapshotGenerationRetentionOfflineResult?>
        ConfirmCommittedAfterCleanupAsync(
            PostgresUnpooledConnectionFactory dedicatedConnections,
            SnapshotGenerationRetentionOfflineResult expected,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dedicatedConnections);
        if (expected.Completion is null)
            return null;
        try
        {
            await using var connection = CreateOfflineCommitReconciliationConnection(dedicatedConnections);
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using (var readOnly = connection.CreateCommand())
            {
                readOnly.Transaction = transaction;
                readOnly.CommandText = "SET TRANSACTION READ ONLY";
                await readOnly.ExecuteNonQueryAsync(ct);
            }
            if (await CaptureOfflineDatabaseSignatureAsync(connection, transaction, ct)
                != expected.Completion.DatabaseSignature)
                return null;
            foreach (var owner in expected.Completion.Owners)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandTimeout = 5;
                command.CommandText = """
                    SELECT EXISTS (
                        SELECT 1 FROM pg_catalog.pg_stat_activity activity
                        WHERE activity.pid=@pid AND activity.backend_start=@started
                          AND (activity.xact_start=@transactionStarted
                            OR EXISTS (SELECT 1 FROM pg_catalog.pg_locks locks
                                WHERE locks.pid=activity.pid AND locks.locktype='advisory')))
                    """;
                command.Parameters.AddWithValue("pid", owner.BackendPid);
                command.Parameters.AddWithValue("started", owner.BackendStartedAtUtc);
                command.Parameters.AddWithValue("transactionStarted", owner.TransactionStartedAtUtc);
                if (await command.ExecuteScalarAsync(ct) is not false)
                    return null;
            }
            var stored = await SnapshotGenerationRetentionRepository.GetCycleAsync(
                connection, expected.Cycle.CycleId, 5, ct, transaction);
            if (stored != expected.Cycle)
                return null;
            await transaction.CommitAsync(ct);
            return expected with
            {
                Warnings = expected.Warnings.Append("committed_cycle_cleanup_warning_verified").Distinct().ToArray(),
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool IsOfflineBudgetFailure(
        Exception exception, CancellationToken external, CancellationToken budget)
    {
        if (external.IsCancellationRequested)
            return false;
        if (budget.IsCancellationRequested || exception.GetBaseException() is TimeoutException)
            return true;
        return exception is PostgresException postgres
            && (postgres.SqlState == "25P04"
                || postgres.SqlState == "57014"
                    && postgres.MessageText.Contains("statement timeout", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class OfflineOperationState
    {
        internal SnapshotGenerationRetentionOfflineResult? CommittedCandidate { get; set; }
    }
}
