using Npgsql;

namespace FSTService.Persistence;

public interface IRegistrationMutationLease : IDisposable, IAsyncDisposable
{
}

public interface IMaxScoreMaintenanceLease : IDisposable, IAsyncDisposable
{
    IDisposable EnterAmbientScope();
    Task AcquireSourceLocksAsync(CancellationToken ct = default);
}

public sealed class RegistrationMutationBlockedException
    : InvalidOperationException
{
    public RegistrationMutationBlockedException()
        : base(
            "Registration changes are paused while max-score maintenance owns the current publication.")
    {
    }
}

internal static class RegistrationMutationGate
{
    public const long AdvisoryLockKey =
        PublicationGenerationSchema.AdvisoryLockKey - 1_000L;
}

internal sealed class PostgresRegistrationMutationLease
    : IRegistrationMutationLease
{
    private const int ReleaseCommandTimeoutSeconds = 5;
    private NpgsqlConnection? _connection;

    public PostgresRegistrationMutationLease(
        NpgsqlConnection connection)
    {
        _connection = connection;
    }

    public void Dispose()
    {
        var connection = Interlocked.Exchange(
            ref _connection,
            null);
        if (connection is null)
            return;

        try
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = ReleaseCommandTimeoutSeconds;
            command.CommandText =
                "SELECT pg_advisory_unlock_shared(@lockKey)";
            command.Parameters.AddWithValue(
                "lockKey",
                RegistrationMutationGate.AdvisoryLockKey);
            command.ExecuteScalar();
        }
        catch
        {
            NpgsqlConnection.ClearPool(connection);
        }
        finally
        {
            connection.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var connection = Interlocked.Exchange(
            ref _connection,
            null);
        if (connection is null)
            return;

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = ReleaseCommandTimeoutSeconds;
            command.CommandText =
                "SELECT pg_advisory_unlock_shared(@lockKey)";
            command.Parameters.AddWithValue(
                "lockKey",
                RegistrationMutationGate.AdvisoryLockKey);
            await command.ExecuteScalarAsync(
                CancellationToken.None);
        }
        catch
        {
            NpgsqlConnection.ClearPool(connection);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
