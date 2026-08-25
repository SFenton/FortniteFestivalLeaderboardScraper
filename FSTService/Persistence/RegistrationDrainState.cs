using Npgsql;

namespace FSTService.Persistence;

public sealed record RegistrationDrainState(
    int RemainingBackfills,
    int RemainingHistory)
{
    public bool IsComplete =>
        RemainingBackfills == 0
        && RemainingHistory == 0;
}

internal static class RegistrationDrainStateReader
{
    internal const string Sql = """
        WITH pending_backfills AS (
            SELECT COUNT(DISTINCT outstanding.account_id)::INTEGER AS count
            FROM (
                SELECT registered.account_id
                FROM registered_users registered
                LEFT JOIN backfill_status backfill
                  ON backfill.account_id = registered.account_id
                WHERE backfill.status IS DISTINCT FROM 'complete'

                UNION ALL

                SELECT backfill.account_id
                FROM backfill_status backfill
                WHERE backfill.status IN (
                    'pending',
                    'in_progress',
                    'deferred')
            ) outstanding
        ), pending_history AS (
            SELECT COUNT(DISTINCT registered.account_id)::INTEGER AS count
            FROM registered_users registered
            JOIN backfill_status backfill
              ON backfill.account_id = registered.account_id
             AND backfill.status = 'complete'
            LEFT JOIN history_recon_status history
              ON history.account_id = registered.account_id
            WHERE history.status IS DISTINCT FROM 'complete'
        )
        SELECT
            pending_backfills.count,
            pending_history.count
        FROM pending_backfills
        CROSS JOIN pending_history
        """;

    internal static RegistrationDrainState Load(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                "Registration drain query returned no state.");
        }

        return new RegistrationDrainState(
            reader.GetInt32(0),
            reader.GetInt32(1));
    }

    internal static async Task<RegistrationDrainState> LoadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = Sql;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                "Registration drain query returned no state.");
        }

        return new RegistrationDrainState(
            reader.GetInt32(0),
            reader.GetInt32(1));
    }
}
