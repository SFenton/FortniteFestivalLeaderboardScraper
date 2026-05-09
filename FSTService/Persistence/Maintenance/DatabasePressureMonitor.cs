using Npgsql;

namespace FSTService.Persistence.Maintenance;

public interface IDatabasePressureMonitor
{
    Task<DatabasePressureSnapshot> GetPressureSnapshotAsync(DatabaseMaintenanceOptions options, CancellationToken ct = default);
}

public sealed record DatabasePressureSnapshot(
    bool IsUnderPressure,
    int ActiveVacuumCount,
    int LongRunningMaintenanceQueryCount,
    int WaitingQueryCount,
    long MaxWatchedDeadTuples,
    string[] Reasons)
{
    public static DatabasePressureSnapshot None { get; } = new(false, 0, 0, 0, 0, []);
}

public sealed class DatabasePressureMonitor : IDatabasePressureMonitor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<DatabasePressureMonitor> _log;

    public DatabasePressureMonitor(NpgsqlDataSource dataSource, ILogger<DatabasePressureMonitor> log)
    {
        _dataSource = dataSource;
        _log = log;
    }

    public async Task<DatabasePressureSnapshot> GetPressureSnapshotAsync(DatabaseMaintenanceOptions options, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT
                    (SELECT COUNT(*)::int FROM pg_stat_progress_vacuum) AS active_vacuums,
                    (
                        SELECT COUNT(*)::int
                        FROM pg_stat_activity
                        WHERE pid <> pg_backend_pid()
                          AND state = 'active'
                          AND query_start IS NOT NULL
                          AND query_start < now() - (@longRunningSeconds * INTERVAL '1 second')
                          AND (
                              query ILIKE 'delete %'
                              OR query ILIKE 'vacuum%'
                              OR query ILIKE 'autovacuum:%'
                              OR query ILIKE 'with %delete%'
                              OR query ILIKE 'create index%'
                              OR query ILIKE 'reindex%'
                              OR query ILIKE 'alter table%'
                              OR query ILIKE '%rewrite%'
                              OR query ILIKE '%repack%'
                          )
                    ) AS long_running_maintenance,
                    (
                        SELECT COUNT(*)::int
                        FROM pg_stat_activity
                        WHERE pid <> pg_backend_pid()
                          AND wait_event_type IN ('IO', 'Lock')
                    ) AS waiting_queries,
                    COALESCE((
                        SELECT MAX(n_dead_tup)::bigint
                        FROM pg_stat_user_tables
                                WHERE relname = ANY(@watchedTables)
                                    OR EXISTS (
                                        SELECT 1
                                        FROM unnest(@watchedTables) watched(table_name)
                                        WHERE relname LIKE watched.table_name || '\_%' ESCAPE '\'
                                    )
                    ), 0) AS max_watched_dead_tuples;";
            cmd.Parameters.AddWithValue("longRunningSeconds", Math.Max(1, options.LongRunningMaintenanceSeconds));
            cmd.Parameters.AddWithValue("watchedTables", options.WatchedTables.Length == 0 ? Array.Empty<string>() : options.WatchedTables);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return DatabasePressureSnapshot.None;

            var activeVacuumCount = reader.GetInt32(0);
            var longRunningMaintenanceQueryCount = reader.GetInt32(1);
            var waitingQueryCount = reader.GetInt32(2);
            var maxWatchedDeadTuples = reader.GetInt64(3);
            var reasons = new List<string>(4);

            if (activeVacuumCount > 0)
                reasons.Add($"active vacuum count {activeVacuumCount:N0}");
            if (longRunningMaintenanceQueryCount > 0)
                reasons.Add($"long-running maintenance query count {longRunningMaintenanceQueryCount:N0}");
            if (waitingQueryCount > 0)
                reasons.Add($"IO/lock wait count {waitingQueryCount:N0}");
            if (maxWatchedDeadTuples >= options.WatchedTableDeadTupleThreshold && options.WatchedTableDeadTupleThreshold > 0)
                reasons.Add($"watched table dead tuples {maxWatchedDeadTuples:N0}");

            return new DatabasePressureSnapshot(
                reasons.Count > 0,
                activeVacuumCount,
                longRunningMaintenanceQueryCount,
                waitingQueryCount,
                maxWatchedDeadTuples,
                reasons.ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Database pressure check failed; continuing cleanup without pressure signal.");
            return DatabasePressureSnapshot.None;
        }
    }
}