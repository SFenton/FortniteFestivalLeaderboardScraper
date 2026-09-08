using System.Text.Json;
using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Tests.Unit;

public sealed class SnapshotRetentionSchemaCombinedProofTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData("CREATE TABLE x(id int);TRUNCATE public.source")]
    [InlineData("TRUNCATE\nTABLE\nONLY public.source;")]
    [InlineData("DO $$BEGIN TRUNCATE public.source; END$$;")]
    [InlineData("DO $body$\nBEGIN\nTRUNCATE TABLE \"other\".\"source\";\nEND $body$")]
    [InlineData("SELECT 1; COPY public.source FROM STDIN")]
    [InlineData("COPY\n\"other\".\"source\"\n(id,value)\nFROM\n'/fixture.csv'")]
    [InlineData("DO $$BEGIN COPY public.source FROM '/fixture.csv'; END$$")]
    [InlineData("DO $$BEGIN EXECUTE 'COPY public.source FROM STDIN'; END$$")]
    [InlineData("DO $$BEGIN INSERT INTO public.source VALUES(1); END$$")]
    [InlineData("SELECT 1;UPDATE public.source AS s SET value=1")]
    [InlineData("DO $$BEGIN UPDATE public.source\nSET value=2; END$$")]
    [InlineData("SELECT 1;DELETE\nFROM public.source;")]
    [InlineData("DO $$BEGIN MERGE INTO public.source USING incoming ON true WHEN MATCHED THEN DELETE; END$$")]
    [InlineData("TRUNCATE /* delimiter */ public.source")]
    [InlineData("COPY public.source -- delimiter\n FROM STDIN")]
    public void Static_backstop_rejects_mutations_in_any_statement_position(string sql)
    {
        Assert.Equal("retention_schema_not_ddl_only",
            Assert.Throws<SnapshotRetentionSchemaDmlRefusal>(() =>
                SnapshotRetentionSchemaSqlBackstop.RequireDdlOnly(sql)).Code);
    }

    [Theory]
    [InlineData("CREATE TRIGGER guard BEFORE UPDATE OR DELETE OR TRUNCATE ON public.source FOR EACH STATEMENT EXECUTE FUNCTION guard()")]
    [InlineData("CREATE TRIGGER guard BEFORE TRUNCATE OR INSERT OR UPDATE OR DELETE ON public.source FOR EACH STATEMENT EXECUTE FUNCTION guard()")]
    [InlineData("COPY public.source TO STDOUT")]
    [InlineData("COPY (SELECT 1) TO STDOUT")]
    public void Static_backstop_preserves_trigger_DDL_and_copy_out(string sql) =>
        SnapshotRetentionSchemaSqlBackstop.RequireDdlOnly(sql);

    [Theory]
    [InlineData("TRUNCATE public.identity_source")]
    [InlineData("ALTER TABLE public.identity_source ALTER COLUMN value TYPE bigint USING value::bigint")]
    [InlineData("ALTER TABLE public.identity_source RENAME TO identity_source_renamed")]
    public async Task Source_identity_drift_refuses_and_rolls_back_even_without_tuple_deletes(string sql)
    {
        SeedSource();
        var before = SourceState();
        var failure = await Assert.ThrowsAsync<SnapshotRetentionSchemaDmlRefusal>(() =>
            DatabaseInitializer.EnsureSnapshotGenerationRetentionSchemaAsync(SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource),
                beforeDmlAssertionForTest: (connection, transaction, ct) => ExecuteAsync(connection, transaction, sql, ct)));

        Assert.Equal("non_retention_relation_identity_changed", failure.Code);
        Assert.False(failure.Proof!.NonRetentionRelationIdentity.Unchanged);
        Assert.NotEqual(failure.Proof.NonRetentionRelationIdentity.BeforeSha256, failure.Proof.NonRetentionRelationIdentity.AfterSha256);
        if (sql.StartsWith("TRUNCATE", StringComparison.Ordinal))
            Assert.Equal(new SnapshotRetentionSchemaDmlTotals(0, 0, 0), failure.Proof.NonRetentionDml);
        Assert.Equal(before, SourceState());
        Assert.False(Scalar<bool>("SELECT to_regclass('public.snapshot_generation_retention_worker_configuration') IS NOT NULL"));
    }

    [Fact]
    public async Task Copy_from_hook_is_refused_and_rolled_back_in_the_schema_transaction()
    {
        SeedSource();
        var before = SourceState();
        var failure = await Assert.ThrowsAsync<SnapshotRetentionSchemaDmlRefusal>(() =>
            DatabaseInitializer.EnsureSnapshotGenerationRetentionSchemaAsync(SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource),
                beforeDmlAssertionForTest: async (connection, _, ct) =>
                {
                    await using var copy = await connection.BeginBinaryImportAsync(
                        "COPY public.identity_source(id,value) FROM STDIN (FORMAT BINARY)", ct);
                    await copy.StartRowAsync(ct);
                    await copy.WriteAsync(3, NpgsqlDbType.Integer, ct);
                    await copy.WriteAsync(3, NpgsqlDbType.Integer, ct);
                    await copy.CompleteAsync(ct);
                }));

        Assert.Equal("non_retention_dml_detected", failure.Code);
        Assert.Equal(1, failure.Proof!.NonRetentionDml.Inserted);
        Assert.True(failure.Proof.NonRetentionRelationIdentity.Unchanged);
        Assert.Equal(before, SourceState());
    }

    [Fact]
    public async Task Materialized_heap_rewrite_is_detected_and_rolled_back()
    {
        Execute("CREATE MATERIALIZED VIEW public.identity_materialized AS SELECT 1::integer AS value");
        var before = Scalar<long>("SELECT relfilenode::bigint FROM pg_class WHERE oid='public.identity_materialized'::regclass");
        var failure = await Assert.ThrowsAsync<SnapshotRetentionSchemaDmlRefusal>(() =>
            DatabaseInitializer.EnsureSnapshotGenerationRetentionSchemaAsync(SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource),
                beforeDmlAssertionForTest: (connection, transaction, ct) => ExecuteAsync(connection, transaction,
                    "REFRESH MATERIALIZED VIEW public.identity_materialized", ct)));
        Assert.Equal("non_retention_relation_identity_changed", failure.Code);
        Assert.Equal(before, Scalar<long>("SELECT relfilenode::bigint FROM pg_class WHERE oid='public.identity_materialized'::regclass"));
    }

    [Fact]
    public async Task Inventory_covers_all_user_schemas_partition_kinds_and_materialized_foreign_relations()
    {
        Execute("""
            CREATE SCHEMA identity_other;
            CREATE TABLE identity_other.regular(id integer);
            CREATE TABLE identity_other.snapshot_generation_retention_cycles(id integer);
            CREATE TABLE public.snapshot_generation_retention_unreviewed(id integer);
            CREATE TABLE identity_other.parent(id integer) PARTITION BY RANGE(id);
            CREATE TABLE identity_other.child PARTITION OF identity_other.parent FOR VALUES FROM(0) TO(10);
            CREATE MATERIALIZED VIEW identity_other.materialized AS SELECT 1::integer AS value;
            CREATE FOREIGN DATA WRAPPER identity_test_fdw;
            CREATE SERVER identity_test_server FOREIGN DATA WRAPPER identity_test_fdw;
            CREATE FOREIGN TABLE identity_other.foreign_relation(id integer) SERVER identity_test_server;
            """);
        using var connection = _fixture.DataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        await ExecuteAsync(connection, transaction, "CREATE TEMP TABLE identity_temporary(id integer)", CancellationToken.None);
        var identities = await SnapshotRetentionSchemaRelationIdentity.CaptureAsync(connection, transaction, CancellationToken.None);

        Assert.Contains(identities, item => item.Schema == "identity_other" && item.Relation == "regular" && item.Kind == "r");
        Assert.Contains(identities, item => item.Schema == "identity_other" && item.Relation == "parent" && item.Kind == "p" && item.Relfilenode == 0);
        Assert.Contains(identities, item => item.Schema == "identity_other" && item.Relation == "child" && item.Kind == "r");
        Assert.Contains(identities, item => item.Schema == "identity_other" && item.Relation == "materialized" && item.Kind == "m");
        Assert.Contains(identities, item => item.Schema == "identity_other" && item.Relation == "foreign_relation" && item.Kind == "f");
        Assert.Contains(identities, item => item.Schema == "identity_other" && item.Relation == "snapshot_generation_retention_cycles");
        Assert.Contains(identities, item => item.Relation == "snapshot_generation_retention_unreviewed");
        Assert.DoesNotContain(identities, item => item.Schema is "pg_catalog" or "information_schema" or "pg_toast"
            || item.Relation == "identity_temporary");
        Assert.DoesNotContain(identities, item => item.Schema == "public" && item.Relation == "snapshot_generation_retention_cycles");
    }

    [Fact]
    public async Task Precommit_failure_is_a_definite_noncommit_with_no_schema_change()
    {
        SeedSource();
        var result = await RunAsync((_, _, _) =>
            throw new SnapshotRetentionSchemaDmlRefusal("test_precommit_failure"));
        Assert.Equal(2, result.Code);
        Assert.Equal("refused", result.Json.GetProperty("outcome").GetString());
        Assert.False(result.Json.GetProperty("transactionCommitted").GetBoolean());
        Assert.False(Scalar<bool>("SELECT to_regclass('public.snapshot_generation_retention_worker_configuration') IS NOT NULL"));
    }

    [Fact]
    public async Task Explicit_precommit_rollback_is_not_misclassified_as_unknown_commit()
    {
        SeedSource();
        var result = await RunAsync(async (_, transaction, ct) =>
        {
            await transaction.RollbackAsync(ct);
            throw new SnapshotRetentionSchemaDmlRefusal("test_definite_rollback");
        });
        Assert.Equal(2, result.Code);
        Assert.False(result.Json.GetProperty("transactionCommitted").GetBoolean());
        Assert.Equal("test_definite_rollback", result.Json.GetProperty("code").GetString());
        Assert.False(Scalar<bool>("SELECT to_regclass('public.snapshot_generation_retention_worker_configuration') IS NOT NULL"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Server_committed_client_ack_lost_is_uncertain_and_never_claims_rollback(bool cancellation)
    {
        SeedSource();
        var before = SourceState();
        var calls = 0;
        var result = await RunAsync(afterServerCommit: _ =>
        {
            calls++;
            if (cancellation)
                throw new OperationCanceledException("test-only lost acknowledgement with private detail");
            throw new NpgsqlException("test-only lost acknowledgement with private detail");
        });

        Assert.Equal(1, calls);
        Assert.Equal(2, result.Code);
        Assert.Equal("uncertain", result.Json.GetProperty("outcome").GetString());
        Assert.Equal("commit_acknowledgement_unknown", result.Json.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("transactionCommitted").ValueKind);
        Assert.Equal(2, result.Json.GetProperty("dmlProof").GetProperty("version").GetInt32());
        Assert.Equal(result.Json.GetProperty("dmlProof").GetProperty("sha256").GetString(),
            result.Json.GetProperty("possibleSchemaProof").GetProperty("combinedProofSha256").GetString());
        Assert.DoesNotContain("private detail", result.Json.GetRawText(), StringComparison.Ordinal);
        Assert.True(Scalar<bool>("SELECT to_regclass('public.snapshot_generation_retention_worker_configuration') IS NOT NULL"));
        Assert.Equal(before, SourceState());
    }

    private async Task<(int Code, JsonElement Json)> RunAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? beforeProof = null,
        Func<CancellationToken, Task>? afterServerCommit = null)
    {
        using var output = new StringWriter();
        var code = await SnapshotRetentionSchemaCommand.RunAsync([SnapshotRetentionSchemaCommand.Flag],
            SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource), output,
            beforeDmlAssertionForTest: beforeProof, afterServerCommitForTest: afterServerCommit);
        using var json = JsonDocument.Parse(output.ToString());
        return (code, json.RootElement.Clone());
    }

    private void SeedSource() => Execute("""
        CREATE TABLE public.identity_source(id integer PRIMARY KEY,value integer NOT NULL);
        INSERT INTO public.identity_source VALUES(1,1),(2,2);
        DROP TABLE public.snapshot_generation_retention_worker_configuration;
        """);

    private string SourceState() => Scalar<string>("""
        SELECT jsonb_build_object('oid',c.oid,'relfilenode',c.relfilenode,
            'columns',(SELECT jsonb_agg(format_type(atttypid,atttypmod) ORDER BY attnum)
                FROM pg_attribute WHERE attrelid=c.oid AND attnum>0 AND NOT attisdropped),
            'rows',(SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM public.identity_source t))::text
        FROM pg_class c WHERE c.oid='public.identity_source'::regclass;
        """);

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
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
