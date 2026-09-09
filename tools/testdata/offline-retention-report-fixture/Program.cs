using System.Security.Cryptography;
using System.Text.Json;
using FortniteFestival.Core;
using FSTService;
using FSTService.Persistence;
using FSTService.Persistence.Maintenance;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

if (args.Length != 1 || args[0] is not ("publish-worker-configuration" or "seed-schema-repair" or "authentication-probe"))
    return 64;
var scope = Environment.GetEnvironmentVariable("FST_TEST_POSTGRES_SCOPE");
var connectionString = Environment.GetEnvironmentVariable("FST_TEST_POSTGRES_CONNECTION_STRING");
if (!Guid.TryParseExact(scope, "D", out _) || string.IsNullOrWhiteSpace(connectionString))
    return 64;
var builder = new NpgsqlConnectionStringBuilder(connectionString)
{
    Pooling = false, Timeout = 5, CommandTimeout = 5, Options = "",
};
var tcp = builder.Host == "127.0.0.1" && builder.Port > 0
    && !string.IsNullOrEmpty(builder.Password) && !builder.PersistSecurityInfo;
var socket = builder.Host?.StartsWith(
    "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/.s/",
    StringComparison.Ordinal) is true;
if (builder.Database != "fst_offline_report_tests" || builder.Username != "fst_test"
    || (!tcp && !socket))
    return 64;
await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
await using var connection = await source.OpenConnectionAsync();
await using var command = connection.CreateCommand();
command.CommandText = """
    SELECT current_setting('fst.offline_report_test_scope',true)=@scope
        AND (inet_server_addr() IS NOT NULL)=@tcp
        AND current_database()='fst_offline_report_tests'
        AND current_user='fst_test'
        AND current_setting('server_version_num')::INTEGER BETWEEN 170000 AND 179999
    """;
command.Parameters.AddWithValue("scope", scope!);
command.Parameters.AddWithValue("tcp", tcp);
if (await command.ExecuteScalarAsync() is not true)
    return 64;
command.Parameters.Clear();
if (args[0] == "authentication-probe")
{
    var display = new NpgsqlConnectionStringBuilder(source.ConnectionString);
    if (!tcp || builder.PersistSecurityInfo || display.PersistSecurityInfo
        || !string.IsNullOrEmpty(display.Password))
        return 2;
    await using var sanitizedReconstruction = new NpgsqlConnection(source.ConnectionString);
    try
    {
        await sanitizedReconstruction.OpenAsync();
        return 2;
    }
    catch (NpgsqlException)
    {
    }
    await using var fresh = new PostgresUnpooledConnectionFactory(builder.ConnectionString).CreateConnection();
    await fresh.OpenAsync();
    await using var freshIdentity = fresh.CreateCommand();
    freshIdentity.CommandText = """
        SELECT current_setting('fst.offline_report_test_scope',true)=@scope
            AND current_user='fst_test' AND inet_client_addr() IS NOT NULL
        """;
    freshIdentity.Parameters.AddWithValue("scope", scope!);
    if (await freshIdentity.ExecuteScalarAsync() is not true)
        return 2;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        outcome = "authenticated_fresh_connection_proved",
        serverVersion = connection.PostgreSqlVersion.Major,
        passwordNonempty = true,
        persistSecurityInfo = false,
        dataSourcePasswordSanitized = true,
        sanitizedReconstructionRefused = true,
        directDataSourceAuthenticated = true,
        privateFactoryAuthenticated = true,
        tcp = true,
    }));
    return 0;
}
if (args[0] == "seed-schema-repair")
{
    await new FestivalPersistence(source).SaveSongsVersionedAsync(
    [
        new Song
        {
            _title = "schema-repair-song",
            lastModified = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            track = new Track
            {
                su = "schema-repair-song", tt = "Schema repair", an = "Fixture",
                ab = "Fixture", au = "https://example.test/image",
                mu = "https://example.test/song.dat", sig = "4/4",
                ge = ["rock"], ry = 2026, mt = 120, dn = 200,
                @in = new In { gr = 1, ba = 2, vl = 3, ds = 4 },
            },
        },
    ]);
    command.CommandText = """
        UPDATE songs SET path_generation_revision=7,
            path_artifact_generation_id='schema-repair-artifact',
            dat_file_hash='fixture-dat',song_last_modified='2026-08-01T00:00:00Z',
            paths_generated_at=TIMESTAMPTZ '2026-08-01T01:00:00Z',
            chopt_version='fixture',chopt_binary_sha256=repeat('a',64),
            path_generation_profile='fixture',path_expected_instruments=ARRAY['Solo_Guitar'],
            max_lead_score=123456,path_generation_pending=FALSE
        WHERE song_id='schema-repair-song';
        """;
    await command.ExecuteNonQueryAsync();
    using var meta = new MetaDatabase(source, NullLogger<MetaDatabase>.Instance,
        unpooledConnections: new PostgresUnpooledConnectionFactory(builder.ConnectionString));
    var scrapeId = meta.StartScrapeRun();
    meta.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
    meta.PublishScrapeRun(scrapeId, promoteCachedResponses: false);
    var publicationId = meta.GetPublicationPointerState().CurrentPublicationId!.Value;
    command.CommandText = """
        UPDATE publication_surface_bindings
        SET binding_json=binding_json || '{"operatorEvidence":"preserve-exactly"}'::jsonb,
            built_at=TIMESTAMPTZ '2026-08-02T03:04:05.123456Z'
        WHERE publication_id=@publication AND surface_name='path_artifacts';
        INSERT INTO service_worker_status(worker_key,status,mode,instance_id,started_at,
            last_status_change_at,last_heartbeat_at,current_operation_json,updated_at)
        VALUES('scraper','offline','scraper','owned-schema-drill',now()-interval '1 hour',
            now(),now(),NULL,now());
        SELECT pg_stat_force_next_flush();
        """;
    command.Parameters.AddWithValue("publication", publicationId);
    await command.ExecuteNonQueryAsync();
    Console.WriteLine(JsonSerializer.Serialize(new { fixture = true, scrapeId, publicationId }));
    return 0;
}
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
