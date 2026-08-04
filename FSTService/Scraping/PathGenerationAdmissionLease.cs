using FSTService.Persistence;
using Npgsql;

namespace FSTService.Scraping;

public interface IPathGenerationAdmissionLeaseProvider
{
    Task<IAsyncDisposable> AcquireAsync(CancellationToken ct);
}

internal static class PathGenerationAdmissionLock
{
    public const long AdvisoryLockKey =
        PublicationGenerationSchema.AdvisoryLockKey - 500L;
}

internal sealed class PostgresPathGenerationAdmissionLeaseProvider
    : IPathGenerationAdmissionLeaseProvider
{
    private const int ReleaseCommandTimeoutSeconds = 5;

    private readonly string _dedicatedConnectionString;
    private readonly ILogger<PostgresPathGenerationAdmissionLeaseProvider> _log;
    private readonly SemaphoreSlim _localAdmission = new(1, 1);

    public PostgresPathGenerationAdmissionLeaseProvider(
        string connectionString,
        ILogger<PostgresPathGenerationAdmissionLeaseProvider> log)
    {
        var connectionStringBuilder =
            new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = false,
                Multiplexing = false,
            };
        _dedicatedConnectionString =
            connectionStringBuilder.ConnectionString;
        _log = log;
    }

    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
    {
        var localAdmissionAcquired = false;
        NpgsqlConnection? connection = null;
        try
        {
            await _localAdmission.WaitAsync(ct);
            localAdmissionAcquired = true;

            connection = new NpgsqlConnection(
                _dedicatedConnectionString);
            await connection.OpenAsync(ct);
            await SetApplicationNameAsync(connection, ct);

            await using var command = connection.CreateCommand();
            command.CommandTimeout = 0;
            command.CommandText = "SELECT pg_advisory_lock(@lockKey)";
            command.Parameters.AddWithValue(
                "lockKey",
                PathGenerationAdmissionLock.AdvisoryLockKey);
            await command.ExecuteScalarAsync(ct);

            _log.LogInformation(
                "Acquired distributed path-generation admission lease.");
            return new Lease(connection, _localAdmission, _log);
        }
        catch
        {
            try
            {
                if (connection is not null)
                    await connection.DisposeAsync();
            }
            finally
            {
                if (localAdmissionAcquired)
                    _localAdmission.Release();
            }
            throw;
        }
    }

    private static async Task SetApplicationNameAsync(
        NpgsqlConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = ReleaseCommandTimeoutSeconds;
        command.CommandText =
            "SELECT set_config('application_name', " +
            "'fst-path-generation-admission', false)";
        await command.ExecuteScalarAsync(ct);
    }

    private sealed class Lease : IAsyncDisposable
    {
        private NpgsqlConnection? _connection;
        private readonly SemaphoreSlim _localAdmission;
        private readonly ILogger _log;

        public Lease(
            NpgsqlConnection connection,
            SemaphoreSlim localAdmission,
            ILogger log)
        {
            _connection = connection;
            _localAdmission = localAdmission;
            _log = log;
        }

        public async ValueTask DisposeAsync()
        {
            var connection = Interlocked.Exchange(ref _connection, null);
            if (connection is null)
                return;

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandTimeout = ReleaseCommandTimeoutSeconds;
                command.CommandText = "SELECT pg_advisory_unlock(@lockKey)";
                command.Parameters.AddWithValue(
                    "lockKey",
                    PathGenerationAdmissionLock.AdvisoryLockKey);
                var unlocked = await command.ExecuteScalarAsync(
                    CancellationToken.None) is true;
                if (unlocked)
                {
                    _log.LogInformation(
                        "Released distributed path-generation admission lease.");
                }
                else
                {
                    _log.LogWarning(
                        "PostgreSQL reported that the path-generation advisory " +
                        "lock was not held during explicit release; physically " +
                        "closing the dedicated session.");
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Explicit path-generation admission unlock failed; " +
                    "closing the PostgreSQL session.");
            }
            finally
            {
                try
                {
                    await connection.DisposeAsync();
                }
                finally
                {
                    _localAdmission.Release();
                }
            }
        }
    }
}

internal sealed class UncontendedPathGenerationAdmissionLeaseProvider
    : IPathGenerationAdmissionLeaseProvider
{
    public static UncontendedPathGenerationAdmissionLeaseProvider Instance { get; } =
        new();

    public Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        => Task.FromResult<IAsyncDisposable>(NoopLease.Instance);

    private sealed class NoopLease : IAsyncDisposable
    {
        public static NoopLease Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
