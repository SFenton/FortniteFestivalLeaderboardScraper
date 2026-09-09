using Npgsql;

namespace FSTService.Persistence.Maintenance;

public sealed partial class SnapshotGenerationRetentionPlanner
{
    private async Task<PlannerLockAcquisition> AcquirePlannerLocksAsync(
        NpgsqlConnection connection,
        TimeSpan waitTimeout,
        bool includeSnapshotDdl,
        CancellationToken ct)
    {
        var required = await ReadPlannerLockRequirementsAsync(
            connection, transaction: null, includeSnapshotDdl, ct);
        var scope = new PlannerLockScope();
        try
        {
            foreach (var requirement in required)
            {
                var lease = requirement.Key == ServiceMaintenanceLock.AdvisoryLockKey
                    ? await _serviceMaintenanceLock.TryAcquireAsync(
                        connection, waitTimeout, ct)
                    : await PostgresSessionAdvisoryLock.TryAcquireAsync(
                        connection, requirement.Key, requirement.Shared,
                        waitTimeout, ct);
                if (lease is null)
                {
                    await scope.DisposeAsync();
                    return new(null, requirement);
                }
                scope.Leases.Add(lease);
            }
            return new(scope, null);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    private static async Task<PlannerLockRequirement?> AcquirePlannerTransactionLocksAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        TimeSpan waitTimeout, CancellationToken ct)
    {
        var required = await ReadPlannerLockRequirementsAsync(
            connection, transaction, includeSnapshotDdl: true, ct);
        foreach (var requirement in required)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            while (true)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandTimeout = OfflineCommandTimeoutSeconds;
                command.CommandText = requirement.Shared
                    ? "SELECT pg_catalog.pg_try_advisory_xact_lock_shared(@key)"
                    : "SELECT pg_catalog.pg_try_advisory_xact_lock(@key)";
                command.Parameters.AddWithValue("key", requirement.Key);
                if (await command.ExecuteScalarAsync(ct) is true)
                    break;
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= waitTimeout)
                    return requirement;
                await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
            }
        }
        return null;
    }

    private static async Task<IReadOnlyList<PlannerLockRequirement>> ReadPlannerLockRequirementsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        bool includeSnapshotDdl, CancellationToken ct)
    {
        await using var names = connection.CreateCommand();
        names.Transaction = transaction;
        names.CommandTimeout = ServiceMaintenanceLock.CommandTimeoutSeconds;
        names.CommandText = """
            SELECT
                pg_catalog.hashtextextended(
                    'fst.snapshot-generation-retention-schema', 0),
                pg_catalog.hashtextextended(
                    'fst.snapshot-generation-partition-ddl', 0)
            """;
        long schemaKey;
        long ddlKey;
        await using (var reader = await names.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            schemaKey = reader.GetInt64(0);
            ddlKey = reader.GetInt64(1);
        }

        var required = new List<PlannerLockRequirement>
        {
            new(schemaKey, true, "retention_schema_lock_busy",
                "Snapshot-generation retention schema initialization is in progress."),
            new(RegistrationMutationGate.AdvisoryLockKey, false,
                "registration_mutation_lock_busy",
                "Registration mutation work currently owns the first advisory lock in the maintenance order."),
            new(ServiceMaintenanceLock.AdvisoryLockKey, false,
                "service_maintenance_lock_busy",
                "Database TTL or another service-maintenance observer currently owns the centralized maintenance lock."),
            new(PublicationGenerationSchema.AdvisoryLockKey, true,
                "publication_lock_busy",
                "Publication allocation or commit currently owns the publication advisory lock."),
            new(SnapshotGenerationRetentionContract.PlannerAdvisoryLockKey, false,
                "retention_planner_lock_busy",
                "Another report-only snapshot-generation retention planner owns the final planner lock."),
        };
        if (includeSnapshotDdl)
        {
            required.Add(new(
                ddlKey, true, "snapshot_partition_ddl_lock_busy",
                "Supported snapshot partition DDL is in progress."));
        }

        return required;
    }

    private sealed record PlannerLockRequirement(
        long Key, bool Shared, string Code, string Detail);

    private sealed record PlannerLockAcquisition(
        PlannerLockScope? Lease, PlannerLockRequirement? Failure);

    private sealed class PlannerLockScope : IAsyncDisposable
    {
        internal List<PostgresSessionAdvisoryLockLease> Leases { get; } = [];

        public async ValueTask DisposeAsync()
        {
            List<Exception>? failures = null;
            for (var index = Leases.Count - 1; index >= 0; index--)
            {
                try
                {
                    await Leases[index].DisposeAsync();
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }
            Leases.Clear();
            if (failures is not null)
                throw new AggregateException("Retention admission lock release failed.", failures);
        }
    }
}
