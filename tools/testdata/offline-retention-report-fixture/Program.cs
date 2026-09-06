using System.Security.Cryptography;
using System.Text.Json;
using FSTService;
using FSTService.Persistence.Maintenance;
using Npgsql;

if (args.Length != 1 || args[0] != "publish-worker-configuration")
    return 64;
var scope = Environment.GetEnvironmentVariable("FST_TEST_POSTGRES_SCOPE");
var connectionString = Environment.GetEnvironmentVariable("FST_TEST_POSTGRES_CONNECTION_STRING");
if (!Guid.TryParseExact(scope, "D", out _) || string.IsNullOrWhiteSpace(connectionString))
    return 64;
var builder = new NpgsqlConnectionStringBuilder(connectionString)
{
    Pooling = false, Timeout = 5, CommandTimeout = 5, Options = "",
};
if (builder.Database != "fst_offline_report_tests" || builder.Username != "fst_test"
    || builder.Host?.StartsWith(
        "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/.s/",
        StringComparison.Ordinal) != true)
    return 64;
await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
await using var connection = await source.OpenConnectionAsync();
await using var command = connection.CreateCommand();
command.CommandText = """
    SELECT current_setting('fst.offline_report_test_scope',true)=@scope
        AND inet_server_addr() IS NULL
        AND current_database()='fst_offline_report_tests'
        AND current_user='fst_test'
        AND current_setting('server_version_num')::INTEGER BETWEEN 170000 AND 179999
    """;
command.Parameters.AddWithValue("scope", scope!);
if (await command.ExecuteScalarAsync() is not true)
    return 64;
command.Parameters.Clear();
command.CommandText = """
    UPDATE service_worker_status SET status='running'
    WHERE worker_key='scraper' AND instance_id='owned-offline-drill'
    """;
if (await command.ExecuteNonQueryAsync() != 1)
    return 64;
var workerCode = Convert.ToHexString(SHA256.HashData(
    await File.ReadAllBytesAsync(typeof(ScraperWorker).Assembly.Location))).ToLowerInvariant();
await new SnapshotGenerationRetentionWorkerConfigurationStore(source)
    .PublishFromWorkerAsync("owned-offline-drill", true, workerCode);
command.CommandText = """
    UPDATE service_worker_status
    SET status='offline',last_status_change_at=now(),last_heartbeat_at=now(),updated_at=now()
    WHERE worker_key='scraper' AND instance_id='owned-offline-drill'
    """;
await command.ExecuteNonQueryAsync();
Console.WriteLine(JsonSerializer.Serialize(new { fixture = true, configurationPublished = true }));
return 0;
