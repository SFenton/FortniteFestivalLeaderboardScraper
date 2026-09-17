using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FSTService.Persistence;
using FSTService.Persistence.Maintenance;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Npgsql;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class SnapshotRetentionSchemaDmlTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Exact_allowlist_is_empty_because_the_accepted_step_has_no_seed_or_repair_DML()
    {
        Assert.Empty(SnapshotRetentionSchemaDmlAssertion.AllowedRetentionRelations);
        SnapshotRetentionSchemaSqlBackstop.RequireDdlOnly(SnapshotGenerationRetentionSchema.Sql);
    }

    [Fact]
    public async Task Dedicated_command_returns_reproducible_zero_transaction_DML_proof()
    {
        JsonElement? first = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await RunAsync();
            Assert.Equal(0, result.Code);
            Assert.Equal("schema_current", result.Json.GetProperty("outcome").GetString());
            Assert.True(result.Json.GetProperty("transactionCommitted").GetBoolean());
            var proof = result.Json.GetProperty("dmlProof");
            Assert.Equal(2, proof.GetProperty("version").GetInt32());
            Assert.Equal("pg_stat_xact_user_tables", proof.GetProperty("statisticsSource").GetString());
            Assert.Equal("fresh_unpooled_single_transaction", proof.GetProperty("backendScope").GetString());
            Assert.Empty(proof.GetProperty("allowedRetentionRelations").EnumerateArray());
            Assert.Empty(proof.GetProperty("allowedRetentionChanges").EnumerateArray());
            var identity = proof.GetProperty("nonRetentionRelationIdentity");
            Assert.True(identity.GetProperty("unchanged").GetBoolean());
            Assert.Equal(identity.GetProperty("beforeSha256").GetString(), identity.GetProperty("afterSha256").GetString());
            foreach (var name in new[] { "inserted", "updated", "deleted" })
                Assert.Equal(0, proof.GetProperty("nonRetentionDml").GetProperty(name).GetInt64());
            var canonical = JsonSerializer.Serialize(new
            {
                version = proof.GetProperty("version"),
                statisticsSource = proof.GetProperty("statisticsSource"),
                backendScope = proof.GetProperty("backendScope"),
                schemaSqlSha256 = proof.GetProperty("schemaSqlSha256"),
                allowedRetentionRelations = proof.GetProperty("allowedRetentionRelations"),
                nonRetentionDml = proof.GetProperty("nonRetentionDml"),
                allowedRetentionChanges = proof.GetProperty("allowedRetentionChanges"),
                nonRetentionRelationIdentity = proof.GetProperty("nonRetentionRelationIdentity"),
            });
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
                proof.GetProperty("sha256").GetString());
            if (first.HasValue)
                Assert.Equal(first.Value.GetRawText(), proof.GetRawText());
            first = proof.Clone();
        }
    }

    [Theory]
    [InlineData("INSERT INTO public.causal_source(id,value) VALUES(3,3)", 1, 0, 0)]
    [InlineData("UPDATE public.causal_source SET value=9 WHERE id=1", 0, 1, 0)]
    [InlineData("DELETE FROM public.causal_source WHERE id=1", 0, 0, 1)]
    public async Task Injected_non_retention_DML_refuses_and_rolls_back_rows_and_schema(
        string sql, long inserted, long updated, long deleted)
    {
        Execute("""
            CREATE TABLE public.causal_source(id integer PRIMARY KEY,value integer NOT NULL);
            INSERT INTO public.causal_source VALUES(1,1),(2,2);
            DROP TABLE public.snapshot_generation_retention_worker_configuration;
            """);
        var before = Scalar<string>("SELECT jsonb_agg(to_jsonb(t) ORDER BY id)::text FROM causal_source t");
        var failure = await Assert.ThrowsAsync<SnapshotRetentionSchemaDmlRefusal>(() =>
            DatabaseInitializer.EnsureSnapshotGenerationRetentionSchemaAsync(
                SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource),
                beforeDmlAssertionForTest: async (connection, transaction, ct) =>
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync(ct);
                }));

        Assert.Equal("non_retention_dml_detected", failure.Code);
        Assert.Equal(new SnapshotRetentionSchemaDmlTotals(inserted, updated, deleted), failure.Proof!.NonRetentionDml);
        Assert.Equal(before, Scalar<string>("SELECT jsonb_agg(to_jsonb(t) ORDER BY id)::text FROM causal_source t"));
        Assert.False(Scalar<bool>("SELECT to_regclass('public.snapshot_generation_retention_worker_configuration') IS NOT NULL"));
    }

    [Fact]
    public async Task A_retention_like_prefix_is_not_a_DML_authorization()
    {
        var failure = await Assert.ThrowsAsync<SnapshotRetentionSchemaDmlRefusal>(() =>
            DatabaseInitializer.EnsureSnapshotGenerationRetentionSchemaAsync(
                SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource),
                beforeDmlAssertionForTest: async (connection, transaction, ct) =>
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        CREATE TABLE public.snapshot_generation_retention_unreviewed(value integer);
                        INSERT INTO public.snapshot_generation_retention_unreviewed VALUES(1);
                        """;
                    await command.ExecuteNonQueryAsync(ct);
                }));
        Assert.Equal(1, failure.Proof!.NonRetentionDml.Inserted);
        Assert.Empty(failure.Proof.AllowedRetentionChanges);
        Assert.False(Scalar<bool>("SELECT to_regclass('public.snapshot_generation_retention_unreviewed') IS NOT NULL"));
    }

    [Fact]
    public async Task Real_CLI_path_refuses_DML_from_a_test_only_DDL_hook_without_echoing_input()
    {
        Execute("""
            CREATE TABLE public.causal_source(value integer);
            CREATE FUNCTION public.causal_test_hook() RETURNS event_trigger LANGUAGE plpgsql AS $body$
            BEGIN
                IF pg_catalog.current_setting('application_name')='fst-snapshot-retention-schema-only' THEN
                    INSERT INTO public.causal_source VALUES(1);
                END IF;
            END $body$;
            CREATE EVENT TRIGGER causal_test_hook ON ddl_command_start EXECUTE FUNCTION public.causal_test_hook();
            """);

        var result = await RunAsync();

        Assert.Equal(2, result.Code);
        Assert.Equal("non_retention_dml_detected", result.Json.GetProperty("code").GetString());
        Assert.False(result.Json.GetProperty("transactionCommitted").GetBoolean());
        Assert.True(result.Json.GetProperty("dmlProof").GetProperty("nonRetentionDml").GetProperty("inserted").GetInt64() > 0);
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM public.causal_source"));
        Assert.DoesNotContain("causal_source", result.Json.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_transaction_statistics_refuse_instead_of_claiming_zero_DML()
    {
        var connection = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource))
        {
            Options = "-c track_counts=off",
        };
        var result = await RunAsync(connection.ConnectionString);
        Assert.Equal(2, result.Code);
        Assert.Equal("transaction_statistics_unavailable", result.Json.GetProperty("code").GetString());
        Assert.False(result.Json.GetProperty("transactionCommitted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("dmlProof").ValueKind);
    }

    [Fact]
    public async Task Existing_registration_writer_excludes_the_initializer_before_DDL()
    {
        await using var writer = await _fixture.Db.AcquireRegistrationMutationLeaseAsync();
        Execute("DROP TABLE public.snapshot_generation_retention_worker_configuration");

        var result = await RunAsync();

        Assert.Equal(2, result.Code);
        Assert.Equal("55P03", result.Json.GetProperty("sqlState").GetString());
        Assert.False(Scalar<bool>("SELECT to_regclass('public.snapshot_generation_retention_worker_configuration') IS NOT NULL"));
    }

    [Fact]
    public async Task Registration_is_excluded_until_commit_and_ambient_after_work_is_not_initializer_DML()
    {
        _fixture.Db.RegisterUser("web-tracker", "causal-user");
        Execute("UPDATE public.registered_users SET last_activity_at='2000-01-01T00:00:00Z' WHERE account_id='causal-user'");
        var before = Scalar<string>("SELECT to_jsonb(t)::text FROM registered_users t WHERE account_id='causal-user'");
        var cumulativeBefore = Scalar<long>("SELECT n_tup_upd FROM pg_stat_user_tables WHERE relname='registered_users'");
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var initializer = DatabaseInitializer.EnsureSnapshotGenerationRetentionSchemaAsync(
            SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource), deadline.Token,
            beforeDmlAssertionForTest: async (connection, _, ct) =>
            {
                entered.SetResult(connection.ProcessID);
                await release.Task.WaitAsync(ct);
            });
        var pid = await entered.Task.WaitAsync(deadline.Token);
        var coordinator = new RegistrationMutationCoordinator(_fixture.Db,
            Substitute.For<IPathDataStore>(), Substitute.For<ISongInstrumentSupportCache>());
        var writer = Task.Run(async () =>
        {
            await using var lease = await coordinator.AcquireWriteLeaseAsync(deadline.Token);
            await lease.VerifyHeldAsync(deadline.Token);
            _fixture.Db.TouchWebRegistrationActivity("causal-user");
        }, deadline.Token);
        try
        {
            await WaitForAsync("""
                SELECT EXISTS(SELECT 1 FROM pg_locks l JOIN pg_stat_activity a USING(pid)
                    WHERE a.datname=current_database() AND a.application_name='fst-registration-mutation'
                      AND l.locktype='advisory' AND l.mode='ShareLock' AND NOT l.granted)
                """, deadline.Token);
            Assert.False(writer.IsCompleted);
            Assert.True(Scalar<bool>($"""
                SELECT EXISTS(SELECT 1 FROM pg_locks WHERE pid={pid} AND locktype='advisory'
                    AND mode='ExclusiveLock' AND granted
                    AND (classid::bigint<<32 | objid::bigint)={RegistrationMutationGate.AdvisoryLockKey})
                """));
            Assert.Equal(before, Scalar<string>("SELECT to_jsonb(t)::text FROM registered_users t WHERE account_id='causal-user'"));
        }
        finally
        {
            release.TrySetResult();
        }
        var proof = await initializer;
        await writer;
        Assert.Equal(new SnapshotRetentionSchemaDmlTotals(0, 0, 0), proof.NonRetentionDml);
        Assert.NotEqual(before, Scalar<string>("SELECT to_jsonb(t)::text FROM registered_users t WHERE account_id='causal-user'"));
        await WaitForAsync($"SELECT n_tup_upd>{cumulativeBefore} FROM pg_stat_user_tables WHERE relname='registered_users'", deadline.Token);
        Assert.Equal(new SnapshotRetentionSchemaDmlTotals(0, 0, 0), proof.NonRetentionDml);
    }

    private async Task WaitForAsync(string sql, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Scalar<bool>(sql))
                return;
            await Task.Delay(25, ct);
        }
    }

    private async Task<(int Code, JsonElement Json)> RunAsync(string? connection = null)
    {
        using var output = new StringWriter();
        var code = await SnapshotRetentionSchemaCommand.RunAsync(
            [SnapshotRetentionSchemaCommand.Flag], connection ?? SharedPostgresContainer.OriginalConnectionStringFor(_fixture.DataSource), output);
        using var json = JsonDocument.Parse(output.ToString());
        return (code, json.RootElement.Clone());
    }

    private void Execute(string sql)
    {
        using var connection = _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var connection = _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }
}
