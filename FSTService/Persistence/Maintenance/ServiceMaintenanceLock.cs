using System.Data;
using System.Diagnostics;
using Npgsql;

namespace FSTService.Persistence.Maintenance;

public sealed class ServiceMaintenanceLock
{
    public const long AdvisoryLockKey = 2026050901;
    internal const int CommandTimeoutSeconds = 5;

    public Task<PostgresSessionAdvisoryLockLease?> TryAcquireAsync(
        NpgsqlConnection connection,
        TimeSpan waitTimeout,
        CancellationToken ct = default) =>
        PostgresSessionAdvisoryLock.TryAcquireAsync(
            connection,
            AdvisoryLockKey,
            shared: false,
            waitTimeout,
            ct);
}

internal static class PostgresSessionAdvisoryLock
{
    private static readonly TimeSpan RetryDelay =
        TimeSpan.FromMilliseconds(50);

    internal static async Task<PostgresSessionAdvisoryLockLease?>
        TryAcquireAsync(
            NpgsqlConnection connection,
            long lockKey,
            bool shared,
            TimeSpan waitTimeout,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "Advisory locks require an open PostgreSQL connection.");
        }

        var boundedTimeout = waitTimeout < TimeSpan.Zero
            ? TimeSpan.Zero
            : waitTimeout;
        var stopwatch = Stopwatch.StartNew();
        do
        {
            ct.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.CommandTimeout =
                ServiceMaintenanceLock.CommandTimeoutSeconds;
            command.CommandText = shared
                ? "SELECT pg_try_advisory_lock_shared(@lockKey)"
                : "SELECT pg_try_advisory_lock(@lockKey)";
            command.Parameters.AddWithValue("lockKey", lockKey);
            if (await command.ExecuteScalarAsync(ct) is true)
            {
                return new PostgresSessionAdvisoryLockLease(
                    connection,
                    lockKey,
                    shared);
            }

            var remaining = boundedTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return null;

            await Task.Delay(
                remaining < RetryDelay ? remaining : RetryDelay,
                ct);
        }
        while (true);
    }
}

public sealed class PostgresSessionAdvisoryLockLease
    : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly long _lockKey;
    private readonly bool _shared;
    private int _released;

    internal PostgresSessionAdvisoryLockLease(
        NpgsqlConnection connection,
        long lockKey,
        bool shared)
    {
        _connection = connection;
        _lockKey = lockKey;
        _shared = shared;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0
            || _connection.State != ConnectionState.Open)
        {
            return;
        }

        await using var command = _connection.CreateCommand();
        command.CommandTimeout =
            ServiceMaintenanceLock.CommandTimeoutSeconds;
        command.CommandText = _shared
            ? "SELECT pg_advisory_unlock_shared(@lockKey)"
            : "SELECT pg_advisory_unlock(@lockKey)";
        command.Parameters.AddWithValue("lockKey", _lockKey);
        if (await command.ExecuteScalarAsync(
                CancellationToken.None) is not true)
        {
            throw new InvalidOperationException(
                $"PostgreSQL advisory lock {_lockKey} was not held by the releasing session.");
        }
    }
}
