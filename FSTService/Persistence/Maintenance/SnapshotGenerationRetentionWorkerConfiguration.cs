using System.Security.Cryptography;
using FSTService.Scraping.Replay;
using Npgsql;

namespace FSTService.Persistence.Maintenance;

public sealed record SnapshotGenerationRetentionWorkerConfiguration(
    string WorkerInstanceId,
    bool ReportOnlyEnabled,
    int PlannerVersion,
    int ConfigVersion,
    int CanonicalCycleIdentityVersion,
    string WorkerCodeSha256,
    string ConfigurationSha256,
    DateTime ConfiguredAtUtc)
{
    public static string ComputeDigest(
        string workerInstanceId, bool enabled, int plannerVersion, int configVersion,
        int canonicalCycleIdentityVersion, string workerCodeSha256) =>
        Convert.ToHexString(SHA256.HashData(TierZeroCanonicalJson.Serialize(new
        {
            WorkerInstanceId = workerInstanceId,
            ReportOnlyEnabled = enabled,
            PlannerVersion = plannerVersion,
            ConfigVersion = configVersion,
            CanonicalCycleIdentityVersion = canonicalCycleIdentityVersion,
            WorkerCodeSha256 = workerCodeSha256,
        }))).ToLowerInvariant();

    public bool IsAuthentic =>
        ConfigurationSha256 == ComputeDigest(
            WorkerInstanceId, ReportOnlyEnabled, PlannerVersion, ConfigVersion,
            CanonicalCycleIdentityVersion, WorkerCodeSha256);
}

public sealed class SnapshotGenerationRetentionWorkerConfigurationStore(NpgsqlDataSource source)
{
    public async Task PublishFromWorkerAsync(
        string instanceId, bool reportOnlyEnabled, string workerCodeSha256,
        CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        ct = deadline.Token;
        if (string.IsNullOrWhiteSpace(instanceId) || workerCodeSha256.Length != 64
            || workerCodeSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Worker retention configuration identity is invalid.");
        var digest = SnapshotGenerationRetentionWorkerConfiguration.ComputeDigest(
            instanceId, reportOnlyEnabled,
            SnapshotGenerationRetentionContract.PlannerVersion,
            SnapshotGenerationRetentionContract.ConfigVersion,
            SnapshotGenerationRetentionContract.CanonicalCycleIdentityVersion, workerCodeSha256);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 5;
        command.CommandText = """
            SET LOCAL lock_timeout='2s';
            SET LOCAL statement_timeout='5s';
            DO $configuration_admission$
            BEGIN
                IF NOT pg_catalog.pg_try_advisory_xact_lock_shared(
                    pg_catalog.hashtextextended('fst.snapshot-generation-retention-schema',0))
                THEN
                    RAISE EXCEPTION 'Retention configuration schema admission is busy.'
                        USING ERRCODE='55P03';
                END IF;
            END
            $configuration_admission$;
            INSERT INTO public.snapshot_generation_retention_worker_configuration (
                worker_instance_id,report_only_enabled,planner_version,config_version,
                canonical_cycle_identity_version,worker_code_sha256,configuration_sha256)
            SELECT @instance,@enabled,@planner,@config,@identityVersion,@code,@digest
            FROM public.service_worker_status worker
            WHERE worker.worker_key='scraper' AND worker.instance_id=@instance
              AND worker.mode='scraper' AND worker.status IN ('starting','running')
            ON CONFLICT (worker_instance_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("instance", instanceId);
        command.Parameters.AddWithValue("enabled", reportOnlyEnabled);
        command.Parameters.AddWithValue("planner", SnapshotGenerationRetentionContract.PlannerVersion);
        command.Parameters.AddWithValue("config", SnapshotGenerationRetentionContract.ConfigVersion);
        command.Parameters.AddWithValue("identityVersion", SnapshotGenerationRetentionContract.CanonicalCycleIdentityVersion);
        command.Parameters.AddWithValue("code", workerCodeSha256);
        command.Parameters.AddWithValue("digest", digest);
        await command.ExecuteNonQueryAsync(ct);
        var stored = await ReadCurrentAsync(connection, transaction, ct);
        if (stored is null || stored.WorkerInstanceId != instanceId
            || stored.ConfigurationSha256 != digest || !stored.IsAuthentic)
            throw new InvalidOperationException("The active worker configuration receipt was not established.");
        await transaction.CommitAsync(ct);
    }

    public static async Task<SnapshotGenerationRetentionWorkerConfiguration?> ReadCurrentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 15;
        command.CommandText = """
            SELECT configuration.worker_instance_id,configuration.report_only_enabled,
                configuration.planner_version,configuration.config_version,
                configuration.canonical_cycle_identity_version,configuration.worker_code_sha256,
                configuration.configuration_sha256,configuration.configured_at
            FROM public.service_worker_status worker
            JOIN public.snapshot_generation_retention_worker_configuration configuration
              ON configuration.worker_instance_id=worker.instance_id
            WHERE worker.worker_key='scraper'
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new(reader.GetString(0), reader.GetBoolean(1), reader.GetInt32(2),
            reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5),
            reader.GetString(6), reader.GetDateTime(7));
    }
}
