using Npgsql;

namespace FSTService.Persistence;

public interface IRegistrationMutationLease : IDisposable, IAsyncDisposable
{
    int BackendProcessId { get; }
    void VerifyHeld();
    Task VerifyHeldAsync(CancellationToken ct = default);
}

public interface IMaxScoreMaintenanceLease : IDisposable, IAsyncDisposable
{
    int BackendProcessId { get; }
    IDisposable EnterAmbientScope();
    void VerifyHeld(bool requireSourceLocks);
    Task VerifyHeldAsync(
        bool requireSourceLocks,
        CancellationToken ct = default);
    Task AcquireSourceLocksAsync(CancellationToken ct = default);
    Task CompleteAsync(
        long publishedScrapeId,
        string manifestSha256,
        CancellationToken ct = default);
}

public class RegistrationMutationBlockedException
    : InvalidOperationException
{
    public RegistrationMutationBlockedException()
        : base(
            "Registration changes are paused while max-score maintenance owns the current publication.")
    {
    }

    internal RegistrationMutationBlockedException(Exception innerException)
        : base(
            "Registration changes are paused while max-score maintenance owns the current publication.",
            innerException)
    {
    }
}

public sealed class MaxScoreMaintenanceLeaseLostException
    : InvalidOperationException
{
    public MaxScoreMaintenanceLeaseLostException()
        : base(
            "The PostgreSQL session that owns the max-score maintenance locks is no longer valid. Any established durable freeze and gate fence remain in place; resume or rerun with a new lease.")
    {
    }

    internal MaxScoreMaintenanceLeaseLostException(
        Exception innerException)
        : base(
            "The PostgreSQL session that owns the max-score maintenance locks is no longer valid. Any established durable freeze and gate fence remain in place; resume or rerun with a new lease.",
            innerException)
    {
    }
}

internal static class RegistrationMutationGate
{
    public const long AdvisoryLockKey =
        PublicationGenerationSchema.AdvisoryLockKey - 1_000L;

    public static bool IsDatabaseFenceRejection(
        PostgresException exception)
        => exception.SqlState == "55000"
           && exception.MessageText.StartsWith(
               "Registration mutation rejected ",
               StringComparison.Ordinal);
}

public sealed class PostgresUnpooledConnectionFactory
{
    private readonly string _connectionString;

    public PostgresUnpooledConnectionFactory(
        string connectionString)
    {
        var builder =
            new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = false,
                Multiplexing = false,
            };
        _connectionString = builder.ConnectionString;
    }

    public NpgsqlConnection CreateConnection()
        => new(_connectionString);
}

internal sealed class PostgresRegistrationMutationLease
    : IRegistrationMutationLease
{
    private const int ReleaseCommandTimeoutSeconds = 5;

    private NpgsqlConnection? _connection;
    private readonly string _leaseToken;
    private readonly SemaphoreSlim? _boundedAdmission;

    public int BackendProcessId { get; }

    public PostgresRegistrationMutationLease(
        NpgsqlConnection connection,
        string leaseToken,
        int backendProcessId,
        SemaphoreSlim? boundedAdmission)
    {
        _connection = connection;
        _leaseToken = leaseToken;
        BackendProcessId = backendProcessId;
        _boundedAdmission = boundedAdmission;
    }

    public void VerifyHeld()
    {
        var connection = _connection
            ?? throw new RegistrationMutationBlockedException();
        try
        {
            using var command = CreateVerificationCommand(connection);
            if (command.ExecuteScalar() is not true)
                throw new RegistrationMutationBlockedException();
        }
        catch (RegistrationMutationBlockedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RegistrationMutationBlockedException(ex);
        }
    }

    public async Task VerifyHeldAsync(
        CancellationToken ct = default)
    {
        var connection = _connection
            ?? throw new RegistrationMutationBlockedException();
        try
        {
            await using var command =
                CreateVerificationCommand(connection);
            if (await command.ExecuteScalarAsync(ct) is not true)
                throw new RegistrationMutationBlockedException();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (RegistrationMutationBlockedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RegistrationMutationBlockedException(ex);
        }
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
        }
        finally
        {
            try
            {
                connection.Dispose();
            }
            finally
            {
                _boundedAdmission?.Release();
            }
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
        }
        finally
        {
            try
            {
                await connection.DisposeAsync();
            }
            finally
            {
                _boundedAdmission?.Release();
            }
        }
    }

    private NpgsqlCommand CreateVerificationCommand(
        NpgsqlConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandTimeout = ReleaseCommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                pg_backend_pid() = @backendProcessId
                AND current_setting(
                        'fst.registration_mutation_lease_token',
                        TRUE) = @leaseToken
                AND EXISTS (
                    SELECT 1
                    FROM pg_locks held
                    WHERE held.pid = pg_backend_pid()
                      AND held.locktype = 'advisory'
                      AND held.mode = 'ShareLock'
                      AND held.granted
                      AND held.classid =
                          (((@lockKey::BIGINT >> 32)
                              & 4294967295)::OID)
                      AND held.objid =
                          ((@lockKey::BIGINT
                              & 4294967295)::OID)
                      AND held.objsubid = 1
                )
                AND EXISTS (
                    SELECT 1
                    FROM scrape_publication_state state
                    WHERE state.id = TRUE
                      AND state.max_score_mutation_gate_token
                          IS NULL
                      AND NOT (
                          state.public_reads_frozen
                          AND state.public_reads_frozen_reason
                              LIKE 'max-score-maintenance:v1:%'
                      )
                )
            """;
        command.Parameters.AddWithValue(
            "backendProcessId",
            BackendProcessId);
        command.Parameters.AddWithValue(
            "leaseToken",
            _leaseToken);
        command.Parameters.AddWithValue(
            "lockKey",
            RegistrationMutationGate.AdvisoryLockKey);
        return command;
    }
}
