using System.Text.Json;
using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class SnapshotRetentionSchemaInitializationTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Dedicated_command_creates_missing_retention_objects_and_is_idempotent()
    {
        Execute("""
            DROP TABLE snapshot_generation_retention_worker_configuration;
            ALTER TABLE snapshot_generation_retention_cycles
                DROP CONSTRAINT ux_snapshot_generation_retention_cycle_trigger;
            CREATE FUNCTION forbid_schema_test_source_write() RETURNS trigger
            LANGUAGE plpgsql AS $body$
            BEGIN RAISE EXCEPTION 'non-retention write forbidden'; END $body$;
            DO $guards$
            DECLARE relation record;
            BEGIN
                FOR relation IN
                    SELECT relname FROM pg_class JOIN pg_namespace n ON n.oid=relnamespace
                    WHERE n.nspname='public' AND relkind IN ('r','p')
                      AND relname NOT LIKE 'snapshot_generation_retention_%'
                LOOP
                    EXECUTE format(
                        'CREATE TRIGGER retention_schema_test_guard
                         BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE ON public.%I
                         FOR EACH STATEMENT EXECUTE FUNCTION forbid_schema_test_source_write()',
                        relation.relname);
                END LOOP;
            END $guards$;
            """);
        var before = Scalar<string>("SELECT to_jsonb(state)::text FROM scrape_publication_state state");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await RunAsync(SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource));
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("schema_current", result.Json.GetProperty("outcome").GetString());
            Assert.False(result.Json.GetProperty("hostedServicesStarted").GetBoolean());
            Assert.Equal(before, Scalar<string>("SELECT to_jsonb(state)::text FROM scrape_publication_state state"));
            Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_worker_configuration"));
            Assert.Equal("UNIQUE (trigger_scrape_id, trigger_publication_id)", Scalar<string>("""
                SELECT pg_get_constraintdef(oid) FROM pg_constraint
                WHERE conname='ux_snapshot_generation_retention_cycle_trigger'
                """));
        }
    }

    [Theory]
    [InlineData("running", "scraper", false)]
    [InlineData("offline", "api", false)]
    [InlineData("offline", "scraper", true)]
    public async Task Legacy_migration_refuses_incompatible_worker_without_partial_schema(
        string status, string mode, bool stale)
    {
        Execute("""
            DROP TABLE snapshot_generation_retention_worker_configuration;
            ALTER TABLE snapshot_generation_retention_cycles
                DROP CONSTRAINT ux_snapshot_generation_retention_cycle_trigger;
            """);
        using (var connection = _fixture.DataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO service_worker_status(worker_key,status,mode,instance_id,
                    started_at,last_status_change_at,last_heartbeat_at,updated_at)
                VALUES('scraper',@status,@mode,'schema-test-worker',now()-interval '1 hour',
                    now()-CASE WHEN @stale THEN interval '16 minutes' ELSE interval '1 second' END,
                    now(),now())
                """;
            command.Parameters.AddWithValue("status", status);
            command.Parameters.AddWithValue("mode", mode);
            command.Parameters.AddWithValue("stale", stale);
            command.ExecuteNonQuery();
        }
        var before = Scalar<string>("SELECT to_jsonb(worker)::text FROM service_worker_status worker");

        var result = await RunAsync(SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource));

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("55000", result.Json.GetProperty("sqlState").GetString());
        Assert.False(Scalar<bool>("SELECT to_regclass('public.snapshot_generation_retention_worker_configuration') IS NOT NULL"));
        Assert.Equal(before, Scalar<string>("SELECT to_jsonb(worker)::text FROM service_worker_status worker"));
    }

    [Fact]
    public async Task Schema_fence_contention_refuses_without_waiting_for_release()
    {
        await using var blocker = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using var command = blocker.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT pg_advisory_xact_lock(
                hashtextextended('fst.snapshot-generation-retention-schema',0))
            """;
        await command.ExecuteNonQueryAsync();

        var result = await RunAsync(SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource));

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("55P03", result.Json.GetProperty("sqlState").GetString());
    }

    [Fact]
    public async Task Empty_database_is_not_given_any_non_retention_prerequisites()
    {
        var connectionString = SharedPostgresContainer.CreateEmptyDatabaseConnectionString();
        var result = await RunAsync(connectionString);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("42P01", result.Json.GetProperty("sqlState").GetString());
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='public' AND c.relkind IN ('r','p')
            """;
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Caller_cancellation_is_observed_before_schema_work()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var output = new StringWriter();
        var exit = await SnapshotRetentionSchemaCommand.RunAsync(
            [SnapshotRetentionSchemaCommand.Flag], SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource),
            output, cancellation.Token);
        Assert.Equal(130, exit);
        Assert.Contains("cancelled_or_deadline_exceeded", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hostile_public_shadows_cannot_hijack_catalog_first_schema_initialization()
    {
        Execute("""
            DROP TABLE public.snapshot_generation_retention_worker_configuration;
            ALTER TABLE public.snapshot_generation_retention_cycles
                DROP CONSTRAINT ux_snapshot_generation_retention_cycle_trigger;
            INSERT INTO public.service_worker_status(worker_key,status,mode,instance_id,
                started_at,last_status_change_at,last_heartbeat_at,updated_at)
            VALUES('scraper','offline','scraper','hostile-shadow-fixture',
                now()-interval '1 hour',now(),now(),now());
            CREATE TABLE public.pg_trigger AS SELECT * FROM pg_catalog.pg_trigger WHERE FALSE;
            CREATE TABLE public.pg_constraint AS SELECT * FROM pg_catalog.pg_constraint WHERE FALSE;
            CREATE TABLE public.pg_stat_xact_user_tables AS
                SELECT * FROM pg_catalog.pg_stat_xact_user_tables WHERE FALSE;
            CREATE FUNCTION public.set_config(text,text,boolean) RETURNS text LANGUAGE plpgsql
                AS $f$ BEGIN RAISE EXCEPTION 'hostile set_config invoked'; END $f$;
            CREATE FUNCTION public.clock_timestamp() RETURNS timestamptz LANGUAGE plpgsql
                AS $f$ BEGIN RAISE EXCEPTION 'hostile clock_timestamp invoked'; END $f$;
            CREATE FUNCTION public.btrim(text) RETURNS text LANGUAGE plpgsql
                AS $f$ BEGIN RAISE EXCEPTION 'hostile btrim invoked'; END $f$;
            CREATE FUNCTION public.to_regclass(text) RETURNS regclass LANGUAGE plpgsql
                AS $f$ BEGIN RAISE EXCEPTION 'hostile to_regclass invoked'; END $f$;
            CREATE FUNCTION public.format(text,text,text) RETURNS text LANGUAGE plpgsql
                AS $f$ BEGIN RAISE EXCEPTION 'hostile format overload invoked'; END $f$;
            CREATE FUNCTION public.assert_retention_schema_search_path() RETURNS event_trigger
            LANGUAGE plpgsql AS $f$
            BEGIN
                IF pg_catalog.current_setting('search_path') <> 'pg_catalog,public' THEN
                    RAISE EXCEPTION 'retention DDL search_path is not exact';
                END IF;
            END $f$;
            CREATE EVENT TRIGGER assert_retention_schema_search_path
                ON ddl_command_start EXECUTE FUNCTION public.assert_retention_schema_search_path();
            """);

        var result = await RunAsync(SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource));

        Assert.Equal(0, result.ExitCode);
        Assert.True(Scalar<bool>("""
            SELECT pg_catalog.to_regclass('public.snapshot_generation_retention_worker_configuration') IS NOT NULL
               AND pg_catalog.to_regclass('pg_catalog.snapshot_generation_retention_worker_configuration') IS NULL
               AND EXISTS (
                   SELECT 1 FROM pg_catalog.pg_constraint
                   WHERE conrelid='public.snapshot_generation_retention_cycles'::regclass
                     AND conname='ux_snapshot_generation_retention_cycle_trigger')
            """));
        Assert.Equal("pg_catalog.clock_timestamp()", Scalar<string>("""
            SET search_path=public,pg_catalog;
            SELECT pg_catalog.pg_get_expr(definition.adbin,definition.adrelid)
            FROM pg_catalog.pg_attrdef definition
            JOIN pg_catalog.pg_attribute attribute
              ON attribute.attrelid=definition.adrelid AND attribute.attnum=definition.adnum
            WHERE definition.adrelid='public.snapshot_generation_retention_worker_configuration'::regclass
              AND attribute.attname='configured_at'
            """));
    }

    private static async Task<(int ExitCode, JsonElement Json)> RunAsync(string connectionString)
    {
        using var output = new StringWriter();
        var code = await SnapshotRetentionSchemaCommand.RunAsync(
            [SnapshotRetentionSchemaCommand.Flag], connectionString, output);
        using var json = JsonDocument.Parse(output.ToString());
        return (code, json.RootElement.Clone());
    }

    private void Execute(string sql)
    {
        using var connection = _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var connection = _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }
}
