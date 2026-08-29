using System.Text.Json;
using FSTService.Persistence;
using FSTService.Persistence.Maintenance;
using FSTService.Tests.Helpers;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class SnapshotGenerationRetentionSchemaTests
    : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Schema_IsIdempotentImmutableAndHasNoExecutableWorkTable()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);

        Assert.True(
            Scalar<bool>(
                """
                SELECT
                    (
                        SELECT confdeltype = 'c'
                        FROM pg_constraint
                        WHERE conrelid =
                            'publication_generations'::regclass
                          AND conname =
                            'publication_generations_scrape_id_fkey'
                    )
                    AND (
                        SELECT confdeltype = 'r'
                               AND convalidated
                        FROM pg_constraint
                        WHERE conrelid =
                            'publication_generations'::regclass
                          AND conname =
                            'publication_generations_scrape_id_restrict_fkey_v2'
                    )
                    AND EXISTS (
                        SELECT 1
                        FROM pg_trigger
                        WHERE tgrelid =
                            'scrape_log'::regclass
                          AND tgname =
                            'trg_scrape_log_restrict_publication_generation_delete_v2'
                          AND NOT tgisinternal
                          AND tgenabled = 'O'
                    )
                """));
        Assert.True(
            Scalar<bool>(
                """
                SELECT
                    EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name =
                            'publication_generations'
                          AND column_name = 'retired_at')
                    AND EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name =
                            'publication_generations'
                          AND column_name =
                            'retired_scrape_id')
                """));
        Assert.False(
            Scalar<bool>(
                """
                SELECT to_regclass(
                    'public.snapshot_generation_retention_jobs')
                    IS NOT NULL
                """));
        Assert.Empty(
            QueryStrings(
                """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name LIKE
                        'snapshot_generation_retention_%'
                  AND column_name IN (
                      'operation_kind',
                      'lease_owner',
                      'lease_token',
                      'lease_expires_at',
                      'attempt_count',
                      'executor_state')
                ORDER BY column_name
                """));

        SeedPublication();
        var falseReportOnly = Assert.Throws<PostgresException>(
            () => Execute(
                """
                INSERT INTO snapshot_generation_retention_cycles (
                    trigger_scrape_id,
                    trigger_publication_id,
                    safe_point_kind,
                    safe_point_at,
                    planner_version,
                    config_version,
                    report_only,
                    status,
                    oracle_agreement,
                    candidate_identity_hash,
                    observation_hash,
                    planner_child_set,
                    planner_live_set,
                    planner_candidate_set,
                    oracle_child_set,
                    oracle_live_set,
                    oracle_candidate_set,
                    candidate_count,
                    protected_count,
                    blocked_count,
                    candidate_bytes)
                VALUES (
                    200,
                    2,
                    'terminal_worker_post_publication',
                    now(),
                    1,
                    1,
                    FALSE,
                    'observed',
                    TRUE,
                    repeat('a', 64),
                    repeat('b', 64),
                    '[]',
                    '[]',
                    '[]',
                    '[]',
                    '[]',
                    '[]',
                    0,
                    0,
                    0,
                    0)
                """));
        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            falseReportOnly.SqlState);

        Execute(
            """
            INSERT INTO snapshot_generation_retention_cycles (
                trigger_scrape_id,
                trigger_publication_id,
                safe_point_kind,
                safe_point_at,
                planner_version,
                config_version,
                report_only,
                status,
                oracle_agreement,
                candidate_identity_hash,
                observation_hash,
                planner_child_set,
                planner_live_set,
                planner_candidate_set,
                oracle_child_set,
                oracle_live_set,
                oracle_candidate_set,
                candidate_count,
                protected_count,
                blocked_count,
                candidate_bytes)
            VALUES (
                200,
                2,
                'terminal_worker_post_publication',
                now(),
                1,
                1,
                TRUE,
                'observed',
                TRUE,
                repeat('a', 64),
                repeat('b', 64),
                '[]',
                '[]',
                '[]',
                '[]',
                '[]',
                '[]',
                0,
                0,
                0,
                0)
            """);
        var mutation = Assert.Throws<PostgresException>(
            () => Execute(
                """
                UPDATE snapshot_generation_retention_cycles
                SET status = 'blocked'
                """));
        Assert.Equal("55000", mutation.SqlState);

        var provenanceDelete =
            Assert.Throws<PostgresException>(
                () => Execute(
                    "DELETE FROM scrape_log WHERE id = 200"));
        Assert.Equal(
            PostgresErrorCodes.ForeignKeyViolation,
            provenanceDelete.SqlState);
    }

    [Fact]
    public void SchemaAndPlannerContainNoDestructiveExecutionSql()
    {
        var sql = string.Join(
            "\n",
            SnapshotGenerationRetentionSchema.Sql,
            SnapshotGenerationRetentionOracle.Sql);

        Assert.DoesNotContain(
            "DROP TABLE",
            sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "DETACH PARTITION",
            sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "DELETE FROM",
            sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "fst_max_score_evidence_sources",
            sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "snapshot-generation-partition-ddl",
            sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExistingCycleSchemaAddsDurableAnomalyEvidenceColumn()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        SeedPublication();
        Execute(
            """
            INSERT INTO snapshot_generation_retention_cycles (
                trigger_scrape_id,
                trigger_publication_id,
                safe_point_kind,
                safe_point_at,
                planner_version,
                config_version,
                report_only,
                status,
                oracle_agreement,
                candidate_identity_hash,
                observation_hash,
                planner_child_set,
                planner_live_set,
                planner_candidate_set,
                oracle_child_set,
                oracle_live_set,
                oracle_candidate_set,
                candidate_count,
                protected_count,
                blocked_count,
                candidate_bytes)
            VALUES (
                200,
                2,
                'terminal_worker_post_publication',
                now(),
                1,
                1,
                TRUE,
                'observed',
                TRUE,
                repeat('a', 64),
                repeat('b', 64),
                '[]',
                '[]',
                '[]',
                '[]',
                '[]',
                '[]',
                0,
                0,
                0,
                0)
            """);
        Execute(
            """
            ALTER TABLE snapshot_generation_retention_cycles
                DROP COLUMN IF EXISTS anomalies
            """);

        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);

        Assert.True(
            Scalar<bool>(
                """
                SELECT
                    data_type = 'jsonb'
                    AND is_nullable = 'NO'
                    AND column_default = '''[]''::jsonb'
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name =
                        'snapshot_generation_retention_cycles'
                  AND column_name = 'anomalies'
                """));
        Assert.Equal(
            "[]",
            Scalar<string>(
                """
                SELECT anomalies::TEXT
                FROM snapshot_generation_retention_cycles
                WHERE trigger_scrape_id = 200
                  AND trigger_publication_id = 2
                """));
    }

    [Fact]
    public void DefaultsAreOffBoundedAndWriterFailuresCannotBeDisabled()
    {
        var options = new DatabaseMaintenanceOptions();

        Assert.False(
            options
                .SnapshotGenerationRetentionReportOnlyEnabled);
        Assert.Equal(
            30,
            options
                .SnapshotGenerationRetentionCommandTimeoutSeconds);
        Assert.Equal(
            500,
            options.ServiceMaintenanceLockWaitMilliseconds);
        Assert.DoesNotContain(
            typeof(DatabaseMaintenanceOptions).GetProperties(),
            property =>
                property.Name.Contains(
                    "WriterFailure",
                    StringComparison.OrdinalIgnoreCase)
                && property.PropertyType == typeof(bool));

        using var document = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "appsettings.json")));
        var maintenance = document.RootElement.GetProperty(
            DatabaseMaintenanceOptions.Section);
        Assert.False(
            maintenance.GetProperty(
                    "SnapshotGenerationRetentionReportOnlyEnabled")
                .GetBoolean());
        Assert.Equal(
            30,
            maintenance.GetProperty(
                    "SnapshotGenerationRetentionCommandTimeoutSeconds")
                .GetInt32());
        Assert.Equal(
            500,
            maintenance.GetProperty(
                    "ServiceMaintenanceLockWaitMilliseconds")
                .GetInt32());
    }

    [Fact]
    public void DatabaseInitializerRegistersRetentionAfterPublicationSchema()
    {
        var plan =
            DatabaseInitializer.GetSchemaInitializationPlan();
        var publicationIndex = plan
            .Select((step, index) => (step, index))
            .Single(item =>
                item.step.Name == "main-publication")
            .index;
        var retention = plan
            .Select((step, index) => (step, index))
            .Single(item =>
                item.step.Name ==
                    "snapshot-generation-retention-report-only");

        Assert.True(retention.index > publicationIndex);
        Assert.True(retention.step.UseShortTransaction);
        Assert.Equal("2s", retention.step.LockTimeout);
        Assert.Equal("15s", retention.step.StatementTimeout);
    }

    [Fact]
    public async Task ReportOnlyEvidenceCannotBlockNextScrapeAdmission()
    {
        var catalog =
            (await new FestivalPersistence(
                    _fixture.DataSource)
                .GetExactSongCatalogTokenAsync())!;
        var scrapeId =
            _fixture.Db.StartScrapeRun(catalog);
        _fixture.Db.CompleteScrapeRun(
            scrapeId,
            1,
            1,
            1,
            1);
        _fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false);
        var publicationId =
            _fixture.Db.GetPublicationPointerState()
                .CurrentPublicationId!.Value;
        Execute(
            """
            INSERT INTO snapshot_generation_retention_cycles (
                trigger_scrape_id,
                trigger_publication_id,
                safe_point_kind,
                safe_point_at,
                planner_version,
                config_version,
                report_only,
                status,
                oracle_agreement,
                candidate_identity_hash,
                observation_hash,
                planner_child_set,
                planner_live_set,
                planner_candidate_set,
                oracle_child_set,
                oracle_live_set,
                oracle_candidate_set,
                candidate_count,
                protected_count,
                blocked_count,
                candidate_bytes)
            VALUES (
                @scrapeId,
                @publicationId,
                'terminal_worker_post_publication',
                now(),
                1,
                1,
                TRUE,
                'observed',
                TRUE,
                repeat('a', 64),
                repeat('b', 64),
                '[]',
                '[]',
                '[]',
                '[]',
                '[]',
                '[]',
                0,
                0,
                0,
                0)
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "scrapeId",
                    scrapeId);
                command.Parameters.AddWithValue(
                    "publicationId",
                    publicationId);
            });

        var nextScrapeId =
            _fixture.Db.StartScrapeRun(catalog);

        Assert.True(nextScrapeId > scrapeId);
    }

    private void SeedPublication()
    {
        Execute(
            """
            INSERT INTO scrape_log (
                id,
                started_at,
                completed_at,
                status)
            VALUES (
                200,
                now(),
                now(),
                'completed');

            INSERT INTO publication_generations (
                publication_id,
                scrape_id,
                status,
                created_at,
                ready_at,
                published_at)
            VALUES (
                2,
                200,
                'current',
                now(),
                now(),
                now());
            """);
    }

    private void Execute(
        string sql,
        Action<NpgsqlCommand>? configure = null)
    {
        using var connection =
            _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var connection =
            _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(
            command.ExecuteScalar()!,
            typeof(T));
    }

    private IReadOnlyList<string> QueryStrings(string sql)
    {
        using var connection =
            _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values;
    }
}
