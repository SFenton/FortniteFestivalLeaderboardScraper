using Npgsql;

namespace FSTService.Persistence;

public interface IPathRepairMaintenanceLeaseProvider
{
    Task<IAsyncDisposable?> TryAcquireAsync(
        string operation,
        bool holdPublicationLock,
        CancellationToken ct);
}

public static class PathRepairMaintenanceLock
{
    public const long AdvisoryLockKey =
        PublicationGenerationSchema.AdvisoryLockKey - 500L;
}

public sealed class PostgresPathRepairMaintenanceLeaseProvider
    : IPathRepairMaintenanceLeaseProvider
{
    private const int CommandTimeoutSeconds = 5;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresPathRepairMaintenanceLeaseProvider> _log;

    public PostgresPathRepairMaintenanceLeaseProvider(
        NpgsqlDataSource dataSource,
        ILogger<PostgresPathRepairMaintenanceLeaseProvider> log)
    {
        _dataSource = dataSource;
        _log = log;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(
        string operation,
        bool holdPublicationLock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("Maintenance operation cannot be blank.", nameof(operation));

        var connection = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            await SetApplicationNameAsync(
                connection,
                operation.Trim(),
                ct);
            if (!await TryLockAsync(
                    connection,
                    PathRepairMaintenanceLock.AdvisoryLockKey,
                    ct))
            {
                await connection.DisposeAsync();
                return null;
            }

            var publicationLocked = false;
            if (holdPublicationLock)
            {
                publicationLocked = await TryLockAsync(
                    connection,
                    PublicationGenerationSchema.AdvisoryLockKey,
                    ct);
                if (!publicationLocked)
                {
                    await UnlockAsync(
                        connection,
                        PathRepairMaintenanceLock.AdvisoryLockKey,
                        CancellationToken.None);
                    await connection.DisposeAsync();
                    return null;
                }
            }

            _log.LogInformation(
                "Acquired path-repair maintenance lease for {Operation}; publicationLock={PublicationLock}.",
                operation,
                publicationLocked);
            return new Lease(
                connection,
                operation.Trim(),
                publicationLocked,
                _log);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task SetApplicationNameAsync(
        NpgsqlConnection connection,
        string operation,
        CancellationToken ct)
    {
        var applicationName = $"fst-path-repair:{operation}";
        if (applicationName.Length > 63)
            applicationName = applicationName[..63];

        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText =
            "SELECT set_config('application_name', @applicationName, false)";
        command.Parameters.AddWithValue("applicationName", applicationName);
        await command.ExecuteScalarAsync(ct);
    }

    private static async Task<bool> TryLockAsync(
        NpgsqlConnection connection,
        long lockKey,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = "SELECT pg_try_advisory_lock(@lockKey)";
        command.Parameters.AddWithValue("lockKey", lockKey);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private static async Task UnlockAsync(
        NpgsqlConnection connection,
        long lockKey,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = "SELECT pg_advisory_unlock(@lockKey)";
        command.Parameters.AddWithValue("lockKey", lockKey);
        await command.ExecuteScalarAsync(ct);
    }

    private sealed class Lease : IAsyncDisposable
    {
        private NpgsqlConnection? _connection;
        private readonly string _operation;
        private readonly bool _publicationLocked;
        private readonly ILogger _log;

        public Lease(
            NpgsqlConnection connection,
            string operation,
            bool publicationLocked,
            ILogger log)
        {
            _connection = connection;
            _operation = operation;
            _publicationLocked = publicationLocked;
            _log = log;
        }

        public async ValueTask DisposeAsync()
        {
            var connection = Interlocked.Exchange(ref _connection, null);
            if (connection is null)
                return;

            try
            {
                if (_publicationLocked)
                {
                    await UnlockAsync(
                        connection,
                        PublicationGenerationSchema.AdvisoryLockKey,
                        CancellationToken.None);
                }

                await UnlockAsync(
                    connection,
                    PathRepairMaintenanceLock.AdvisoryLockKey,
                    CancellationToken.None);
                _log.LogInformation(
                    "Released path-repair maintenance lease for {Operation}.",
                    _operation);
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Explicit release of the path-repair maintenance lease for {Operation} failed; closing the PostgreSQL session.",
                    _operation);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}

internal sealed class UncontendedPathRepairMaintenanceLeaseProvider
    : IPathRepairMaintenanceLeaseProvider
{
    public static UncontendedPathRepairMaintenanceLeaseProvider Instance { get; } =
        new();

    public Task<IAsyncDisposable?> TryAcquireAsync(
        string operation,
        bool holdPublicationLock,
        CancellationToken ct)
        => Task.FromResult<IAsyncDisposable?>(NoopLease.Instance);

    private sealed class NoopLease : IAsyncDisposable
    {
        public static NoopLease Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
