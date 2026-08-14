using Npgsql;

namespace FSTService.Persistence;

public interface IRegistrationMutationLease : IDisposable
{
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

internal sealed class PostgresRegistrationMutationLease
    : IRegistrationMutationLease
{
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;

    public PostgresRegistrationMutationLease(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public void Dispose()
    {
        var transaction = Interlocked.Exchange(
            ref _transaction,
            null);
        var connection = Interlocked.Exchange(
            ref _connection,
            null);
        try
        {
            transaction?.Rollback();
        }
        finally
        {
            transaction?.Dispose();
            connection?.Dispose();
        }
    }
}
