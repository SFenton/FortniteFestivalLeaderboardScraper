using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace FSTService.Tests.Unit;

public class DatabaseInitializerTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaFixture;
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly string _tempDir;

    public DatabaseInitializerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"startup_init_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _metaFixture = new InMemoryMetaDatabase();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _persistence = new GlobalLeaderboardPersistence(
            _metaFixture.Db, loggerFactory,
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>(),
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions()));
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _metaFixture.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task CheckHealthAsync_BeforeInit_ReturnsUnhealthy()
    {
        var festivalService = new FestivalService((IFestivalPersistence?)null);
        var handler = new HttpClient(new NoOpHandler());
        var shopService = new ItemShopService(handler, festivalService, _metaFixture.Db,
            Substitute.For<ILogger<ItemShopService>>());
        var lifetime = Substitute.For<IHostApplicationLifetime>();

        var init = new StartupInitializer(
            _persistence, _metaFixture.DataSource, festivalService, shopService, lifetime,
            Options.Create(new ScraperOptions { DataDirectory = _tempDir }),
            Substitute.For<ILogger<StartupInitializer>>());

        Assert.False(init.IsReady);
        var result = await init.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task StartAsync_InitializesAndSignalsReady()
    {
        var festivalService = new FestivalService((IFestivalPersistence?)null);
        var handler = new HttpClient(new NoOpHandler());
        var shopService = new ItemShopService(handler, festivalService, _metaFixture.Db,
            Substitute.For<ILogger<ItemShopService>>());
        var lifetime = Substitute.For<IHostApplicationLifetime>();

        var init = new StartupInitializer(
            _persistence, _metaFixture.DataSource, festivalService, shopService, lifetime,
            Options.Create(new ScraperOptions { DataDirectory = _tempDir }),
            Substitute.For<ILogger<StartupInitializer>>());

        await init.StartAsync(CancellationToken.None);

        // Wait for background init
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await init.WaitForReadyAsync(cts.Token);

        Assert.True(init.IsReady);
        var result = await init.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task StopAsync_ReturnsCompletedTask()
    {
        var festivalService = new FestivalService((IFestivalPersistence?)null);
        var handler = new HttpClient(new NoOpHandler());
        var shopService = new ItemShopService(handler, festivalService, _metaFixture.Db,
            Substitute.For<ILogger<ItemShopService>>());
        var lifetime = Substitute.For<IHostApplicationLifetime>();

        var init = new StartupInitializer(
            _persistence, _metaFixture.DataSource, festivalService, shopService, lifetime,
            Options.Create(new ScraperOptions { DataDirectory = _tempDir }),
            Substitute.For<ILogger<StartupInitializer>>());

        await init.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EnsureSchemaAsync_creates_idempotent_published_scope_source_schema()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                to_regclass('public.leaderboard_published_scope_source') IS NOT NULL,
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'leaderboard_scope_fingerprints'
                      AND column_name = 'is_complete'
                      AND is_nullable = 'NO'
                ),
                (
                    SELECT array_agg(attribute.attname ORDER BY key.ordinality)
                    FROM pg_constraint constraint_row
                    CROSS JOIN LATERAL unnest(constraint_row.conkey) WITH ORDINALITY AS key(attnum, ordinality)
                    JOIN pg_attribute attribute
                      ON attribute.attrelid = constraint_row.conrelid
                     AND attribute.attnum = key.attnum
                    WHERE constraint_row.conrelid = 'leaderboard_published_scope_source'::regclass
                      AND constraint_row.contype = 'p'
                )
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(
            new[] { "published_scrape_id", "instrument", "song_id", "scope_kind" },
            reader.GetFieldValue<string[]>(2));
    }

    [Fact]
    public async Task EnsureSchemaAsync_creates_idempotent_path_generation_atomicity_schema()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                to_regclass('public.path_generation_errors') IS NOT NULL,
                COUNT(*) = 6,
                EXISTS (
                    SELECT 1
                    FROM pg_trigger
                    WHERE tgname = 'trg_reject_incoherent_legacy_path_write'
                      AND NOT tgisinternal)
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'songs'
              AND column_name = ANY(ARRAY[
                  'chopt_binary_sha256',
                  'path_generation_profile',
                  'path_artifact_generation_id',
                  'path_expected_instruments',
                  'path_generation_revision',
                  'path_generation_pending'
              ])
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
    }

    [Fact]
    public void SchemaInitializationPlanSeparatesShortNotificationMigration()
    {
        var plan = DatabaseInitializer.GetSchemaInitializationPlan();

        Assert.Equal(
            new[]
            {
                "improvement-notifications",
                "score-history-dedup-audit",
                "main-publication",
            },
            plan.Select(static step => step.Name));
        var notification = plan[0];
        Assert.True(notification.UseShortTransaction);
        Assert.Equal(20, notification.CommandTimeoutSeconds);
        Assert.Equal("2s", notification.LockTimeout);
        Assert.Equal("15s", notification.StatementTimeout);
        Assert.Contains(ImprovementNotificationSchema.Sql, notification.Sql);
        var scoreHistoryAudit = plan[1];
        Assert.True(scoreHistoryAudit.UseShortTransaction);
        Assert.Equal(20, scoreHistoryAudit.CommandTimeoutSeconds);
        Assert.Equal("2s", scoreHistoryAudit.LockTimeout);
        Assert.Equal("15s", scoreHistoryAudit.StatementTimeout);
        Assert.Equal(
            ScoreHistoryDedupMaintenanceSchema.Sql,
            scoreHistoryAudit.Sql);
        Assert.False(plan[2].UseShortTransaction);
    }

    [Fact]
    public async Task EnsureSchemaAsync_DoesNotCreateRetiredAggregateRankingDeltaRelations()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM pg_class relation
            JOIN pg_namespace schema_row
              ON schema_row.oid = relation.relnamespace
            WHERE schema_row.nspname = 'public'
              AND relation.relkind IN ('r', 'p')
              AND (
                    relation.relname = 'ranking_deltas'
                 OR relation.relname LIKE 'ranking_deltas_%'
                 OR relation.relname = 'ranking_delta_tiers'
                 OR relation.relname LIKE 'ranking_delta_tiers_%'
                 OR relation.relname = 'rank_history_deltas'
                 OR relation.relname LIKE 'rank_history_deltas_%'
                 OR relation.relname = 'composite_ranking_deltas'
                 OR relation.relname = 'combo_ranking_deltas'
              )
            """;

        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task EnsureSchemaAsync_creates_immutable_score_history_dedup_audit_schema()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using (var inspect = conn.CreateCommand())
        {
            inspect.CommandText = """
                SELECT
                    to_regclass(
                        'public.score_history_dedup_maintenance_runs')
                        IS NOT NULL,
                    to_regclass(
                        'public.score_history_dedup_original_rows')
                        IS NOT NULL,
                    COUNT(*) FILTER (
                        WHERE trigger_row.tgname =
                            'trg_reject_score_history_dedup_run_mutation'
                          AND NOT trigger_row.tgisinternal) = 1,
                    COUNT(*) FILTER (
                        WHERE trigger_row.tgname =
                            'trg_reject_score_history_dedup_original_mutation'
                          AND NOT trigger_row.tgisinternal) = 1,
                    NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint constraint_row
                        WHERE constraint_row.conrelid =
                            'score_history_dedup_maintenance_runs'::regclass
                          AND constraint_row.contype = 'f')
                FROM pg_trigger trigger_row
                WHERE trigger_row.tgrelid IN (
                    'score_history_dedup_maintenance_runs'::regclass,
                    'score_history_dedup_original_rows'::regclass);
                """;
            using var reader = inspect.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));
            Assert.True(reader.GetBoolean(4));
        }

        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO score_history_dedup_maintenance_runs (
                    maintenance_purpose,
                    maintenance_contract_version,
                    execution_source,
                    dry_run_digest,
                    canonical_candidate_data,
                    safety_classification,
                    database_name,
                    database_user,
                    server_version_num,
                    duplicate_row_count,
                    duplicate_group_count,
                    excess_row_count,
                    affected_account_count,
                    affected_song_count,
                    original_rows_audited,
                    survivor_rows_updated,
                    rows_deleted,
                    index_replaced,
                    index_definition_before,
                    index_definition_after,
                    rollback_sql)
                VALUES (
                    'score_history_null_timestamp_dedup_v1',
                    1,
                    'explicit_cli',
                    @digest,
                    '{}',
                    'ready',
                    current_database(),
                    current_user,
                    current_setting('server_version_num')::INTEGER,
                    0, 0, 0, 0, 0, 0, 0, 0,
                    TRUE,
                    'legacy',
                    'null-safe',
                    'rollback')
                RETURNING maintenance_run_id;
                """;
            insert.Parameters.AddWithValue("digest", new string('a', 64));
            var runId = Convert.ToInt64(insert.ExecuteScalar());

            using var mutate = conn.CreateCommand();
            mutate.CommandText = """
                UPDATE score_history_dedup_maintenance_runs
                SET rollback_sql = 'changed'
                WHERE maintenance_run_id = @runId;
                """;
            mutate.Parameters.AddWithValue("runId", runId);
            var exception = Assert.Throws<PostgresException>(
                () => mutate.ExecuteNonQuery());
            Assert.Equal("55000", exception.SqlState);
        }
    }

    [Fact]
    public async Task MaintenanceAuditPublishedScrapeProvenanceIsDurableAndImmutable()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        int scrapeId;
        using (var insertScrape = conn.CreateCommand())
        {
            insertScrape.CommandText = """
                INSERT INTO scrape_log (
                    started_at, completed_at, status,
                    songs_scraped, total_entries, total_requests, total_bytes)
                VALUES (
                    now(), now(), 'completed',
                    0, 0, 0, 0)
                RETURNING id;
                """;
            scrapeId = (int)insertScrape.ExecuteScalar()!;
        }

        using (var insertRun = conn.CreateCommand())
        {
            insertRun.CommandText = """
                INSERT INTO improvement_notification_maintenance_runs (
                    notification_purpose,
                    notification_cause,
                    delivery_state,
                    published_scrape_id,
                    dry_run_digest,
                    canonical_candidate_data,
                    repair_manifest,
                    total_charted_songs)
                VALUES (
                    'maintenance_pro_lead_max_score_repair_v1',
                    'max_score_recompute',
                    'quarantined',
                    @scrapeId,
                    @digest,
                    '{}',
                    '{"manifestVersion":1,"songs":[]}'::jsonb,
                    4);
                """;
            insertRun.Parameters.AddWithValue("scrapeId", scrapeId);
            insertRun.Parameters.AddWithValue("digest", new string('a', 64));
            Assert.Equal(1, insertRun.ExecuteNonQuery());
        }

        using (var deleteScrape = conn.CreateCommand())
        {
            deleteScrape.CommandText = "DELETE FROM scrape_log WHERE id = @scrapeId;";
            deleteScrape.Parameters.AddWithValue("scrapeId", scrapeId);
            Assert.Equal(1, deleteScrape.ExecuteNonQuery());
        }

        using (var inspect = conn.CreateCommand())
        {
            inspect.CommandText = """
                SELECT run.published_scrape_id,
                       column_row.is_nullable = 'NO',
                       NOT EXISTS (
                           SELECT 1
                           FROM pg_constraint constraint_row
                           JOIN pg_attribute attribute
                             ON attribute.attrelid = constraint_row.conrelid
                            AND attribute.attnum = ANY(constraint_row.conkey)
                           WHERE constraint_row.conrelid =
                               'improvement_notification_maintenance_runs'::regclass
                             AND constraint_row.contype = 'f'
                             AND attribute.attname = 'published_scrape_id'
                       )
                FROM improvement_notification_maintenance_runs run
                CROSS JOIN information_schema.columns column_row
                WHERE column_row.table_schema = 'public'
                  AND column_row.table_name =
                      'improvement_notification_maintenance_runs'
                  AND column_row.column_name = 'published_scrape_id';
                """;
            using var reader = inspect.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(scrapeId, reader.GetInt32(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
        }

        using var mutate = conn.CreateCommand();
        mutate.CommandText = """
            UPDATE improvement_notification_maintenance_runs
            SET published_scrape_id = @replacement
            WHERE published_scrape_id = @scrapeId;
            """;
        mutate.Parameters.AddWithValue("replacement", scrapeId + 1);
        mutate.Parameters.AddWithValue("scrapeId", scrapeId);
        var exception = Assert.Throws<PostgresException>(
            () => mutate.ExecuteNonQuery());
        Assert.Equal("55000", exception.SqlState);
    }

    [Fact]
    public async Task EnsureSchemaAsync_creates_idempotent_registered_user_refresh_scope_schema()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                to_regclass('public.registered_user_refresh_scope_progress') IS NOT NULL,
                to_regclass('public.ix_registered_user_refresh_scope_checked_at') IS NOT NULL,
                (
                    SELECT array_agg(attribute.attname ORDER BY key.ordinality)
                    FROM pg_constraint constraint_row
                    CROSS JOIN LATERAL unnest(constraint_row.conkey)
                        WITH ORDINALITY AS key(attnum, ordinality)
                    JOIN pg_attribute attribute
                      ON attribute.attrelid = constraint_row.conrelid
                     AND attribute.attnum = key.attnum
                    WHERE constraint_row.conrelid =
                        'registered_user_refresh_scope_progress'::regclass
                      AND constraint_row.contype = 'p'
                ),
                (
                    SELECT array_agg(column_name ORDER BY ordinal_position)
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'registered_user_refresh_scope_progress'
                      AND is_nullable = 'NO'
                ),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'registered_user_refresh_scope_progress'
                      AND column_name = 'scrape_id'
                      AND is_nullable = 'YES'
                ),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conname = 'ck_registered_user_refresh_scope_provenance_v2'
                      AND conrelid =
                        'registered_user_refresh_scope_progress'::regclass
                      AND convalidated
                )
            """;

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(new[] { "song_id", "instrument" }, reader.GetFieldValue<string[]>(2));
        Assert.Equal(
            new[] { "song_id", "instrument", "status", "checked_at", "provenance" },
            reader.GetFieldValue<string[]>(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
    }

    [Fact]
    public async Task EnsureSchemaAsync_upgrades_registered_user_refresh_scrape_provenance_idempotently()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using (var conn = _metaFixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                ALTER TABLE registered_user_refresh_scope_progress
                    DROP CONSTRAINT ck_registered_user_refresh_scope_provenance_v2;
                ALTER TABLE registered_user_refresh_scope_progress
                    DROP COLUMN provenance;
                ALTER TABLE registered_user_refresh_scope_progress
                    ALTER COLUMN scrape_id SET NOT NULL;

                INSERT INTO registered_user_refresh_scope_progress (
                    song_id,
                    instrument,
                    status,
                    checked_at,
                    scrape_id)
                VALUES (
                    'legacy-refresh-song',
                    'Solo_Guitar',
                    'complete',
                    '2026-08-01T00:00:00Z',
                    1272);
                """;
            cmd.ExecuteNonQuery();
        }

        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var verifyConn = _metaFixture.DataSource.OpenConnection();
        using var verifyCmd = verifyConn.CreateCommand();
        verifyCmd.CommandText = """
            SELECT
                scrape_id,
                provenance,
                (
                    SELECT is_nullable
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'registered_user_refresh_scope_progress'
                      AND column_name = 'scrape_id'
                )
            FROM registered_user_refresh_scope_progress
            WHERE song_id = 'legacy-refresh-song'
              AND instrument = 'Solo_Guitar'
            """;
        using var reader = verifyCmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1272, reader.GetInt64(0));
        Assert.Equal("scrape", reader.GetString(1));
        Assert.Equal("YES", reader.GetString(2));
    }

    [Fact]
    public async Task EnsureSchemaAsync_replaces_prior_provenance_constraint_and_enforces_null_semantics()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using (var conn = _metaFixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                ALTER TABLE registered_user_refresh_scope_progress
                    DROP CONSTRAINT ck_registered_user_refresh_scope_provenance_v2;
                ALTER TABLE registered_user_refresh_scope_progress
                    ADD CONSTRAINT ck_registered_user_refresh_scope_provenance
                    CHECK (
                        (provenance = 'scrape' AND scrape_id > 0)
                        OR (provenance = 'phase_only' AND scrape_id IS NULL));
                """;
            cmd.ExecuteNonQuery();
        }

        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var verifyConn = _metaFixture.DataSource.OpenConnection();
        using (var verifyConstraint = verifyConn.CreateCommand())
        {
            verifyConstraint.CommandText = """
                SELECT
                    NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint
                        WHERE conname = 'ck_registered_user_refresh_scope_provenance'
                          AND conrelid =
                            'registered_user_refresh_scope_progress'::regclass),
                    EXISTS (
                        SELECT 1
                        FROM pg_constraint
                        WHERE conname = 'ck_registered_user_refresh_scope_provenance_v2'
                          AND conrelid =
                            'registered_user_refresh_scope_progress'::regclass
                          AND convalidated)
                """;
            using var reader = verifyConstraint.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
        }

        using (var invalid = verifyConn.CreateCommand())
        {
            invalid.CommandText = """
                INSERT INTO registered_user_refresh_scope_progress (
                    song_id,
                    instrument,
                    status,
                    checked_at,
                    scrape_id,
                    provenance)
                VALUES (
                    'invalid-null-scrape',
                    'Solo_Guitar',
                    'complete',
                    @now,
                    NULL,
                    'scrape')
                """;
            invalid.Parameters.AddWithValue("now", DateTime.UtcNow);

            var exception = Assert.Throws<PostgresException>(
                () => invalid.ExecuteNonQuery());
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        using (var valid = verifyConn.CreateCommand())
        {
            valid.CommandText = """
                INSERT INTO registered_user_refresh_scope_progress (
                    song_id,
                    instrument,
                    status,
                    checked_at,
                    scrape_id,
                    provenance)
                VALUES (
                    'valid-null-phase-only',
                    'Solo_Guitar',
                    'complete',
                    @now,
                    NULL,
                    'phase_only')
                """;
            valid.Parameters.AddWithValue("now", DateTime.UtcNow);
            Assert.Equal(1, valid.ExecuteNonQuery());
        }
    }

    [Fact]
    public async Task EnsureSchemaAsync_adds_season_and_history_identity_columns_idempotently()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using (var dropConn = _metaFixture.DataSource.OpenConnection())
        using (var dropCmd = dropConn.CreateCommand())
        {
            dropCmd.CommandText = """
                ALTER TABLE registered_band_processing_progress
                    DROP COLUMN window_id;
                ALTER TABLE registered_player_band_discovery_progress
                    DROP COLUMN window_id;
                ALTER TABLE season_windows
                    DROP COLUMN source_kind;
                ALTER TABLE history_recon_status
                    DROP COLUMN reconstruction_version,
                    DROP COLUMN window_fingerprint,
                    DROP COLUMN admission_revision;
                ALTER TABLE history_recon_progress
                    DROP COLUMN reconstruction_version,
                    DROP COLUMN window_fingerprint,
                    DROP COLUMN admission_revision;
                ALTER TABLE song_first_seen_season
                    DROP COLUMN window_fingerprint,
                    DROP COLUMN max_season;
                """;
            dropCmd.ExecuteNonQuery();
        }

        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'registered_band_processing_progress'
                      AND column_name = 'window_id'
                      AND is_nullable = 'NO'),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'registered_player_band_discovery_progress'
                      AND column_name = 'window_id'
                      AND is_nullable = 'NO'),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'season_windows'
                      AND column_name = 'source_kind'
                      AND is_nullable = 'NO'),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'history_recon_status'
                      AND column_name = 'window_fingerprint'
                      AND is_nullable = 'NO'),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'history_recon_progress'
                      AND column_name = 'reconstruction_version'
                      AND is_nullable = 'NO'),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'history_recon_status'
                      AND column_name = 'admission_revision'
                      AND is_nullable = 'NO'),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'song_first_seen_season'
                      AND column_name = 'window_fingerprint'
                      AND is_nullable = 'NO')
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
    }

    [Fact]
    public async Task EnsureSchemaAsync_invalidates_legacy_completed_history_reconstruction()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using (var conn = _metaFixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO history_recon_status (
                    account_id,
                    status,
                    songs_processed,
                    total_songs_to_process,
                    completed_at,
                    reconstruction_version,
                    window_fingerprint)
                VALUES (
                    'legacy-history',
                    'complete',
                    10,
                    10,
                    @now,
                    1,
                    '');
                INSERT INTO history_recon_progress (
                    account_id,
                    song_id,
                    instrument,
                    processed,
                    processed_at,
                    reconstruction_version,
                    window_fingerprint)
                VALUES (
                    'legacy-history',
                    'song-a',
                    'Solo_Guitar',
                    1,
                    @now,
                    1,
                    '');
                """;
            cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
            cmd.ExecuteNonQuery();
        }

        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var verifyConn = _metaFixture.DataSource.OpenConnection();
        using var verifyCmd = verifyConn.CreateCommand();
        verifyCmd.CommandText = """
            SELECT status, songs_processed, completed_at, reconstruction_version
            FROM history_recon_status
            WHERE account_id = 'legacy-history'
            """;
        using var reader = verifyCmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("pending", reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal(1, reader.GetInt32(3));
    }

    [Fact]
    public async Task EnsureSchemaAsync_creates_worker_correctness_ledgers()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                to_regclass('public.leaderboard_scope_manifests') IS NOT NULL,
                to_regclass('public.scrape_writer_failures') IS NOT NULL,
                to_regclass('public.scrape_phase_outcomes') IS NOT NULL,
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'scrape_log'
                      AND column_name = 'status'
                      AND is_nullable = 'NO'
                ),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'scrape_log'
                      AND column_name = 'best_effort_failed_phases'
                      AND data_type = 'ARRAY'
                )
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
    }

    [Fact]
    public async Task EnsureSchemaAsync_does_not_recreate_retired_composite_history_latest_index()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT to_regclass('public.ix_crh_latest') IS NULL";

        Assert.True((bool)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task EnsureSchemaAsync_does_not_recreate_residual_capacity_indexes()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                to_regclass('public.ix_cr_rank') IS NULL,
                to_regclass('public.ix_pso_union_lookup') IS NULL,
                to_regclass('public.ix_pso_band_source') IS NULL,
                to_regclass('public.ix_band_identity_type_appearance') IS NULL,
                to_regclass('public.ix_bstp_type_appearance') IS NULL,
                to_regclass('public.ix_btr_current_duets_team') IS NULL,
                to_regclass('public.ix_btr_current_trios_team') IS NULL,
                to_regclass('public.ix_btr_current_quad_team') IS NULL,
                to_regclass('public.band_team_rankings_published_band_duets_ix_adjusted') IS NULL,
                to_regclass('public.band_team_rankings_published_band_trios_ix_adjusted') IS NULL,
                to_regclass('public.band_team_rankings_published_band_quad_ix_adjusted') IS NULL
            """;

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            Assert.True(reader.GetBoolean(ordinal));
    }

    [Fact]
    public async Task EnsureSchemaAsync_does_not_create_retired_logical_shadow_schema()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                to_regclass('public.leaderboard_current_entries') IS NULL,
                to_regclass('public.leaderboard_entry_versions') IS NULL,
                to_regclass('public.leaderboard_logical_write_metrics') IS NULL,
                to_regclass('public.ix_llwm_scrape') IS NULL,
                to_regclass('public.ix_lce_scope_rank') IS NULL,
                to_regclass('public.ix_lce_last_changed') IS NULL,
                to_regclass('public.ix_lev_open_versions') IS NULL,
                to_regclass('public.ix_lev_from_scrape') IS NULL
            """;

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            Assert.True(reader.GetBoolean(ordinal));
    }

    [Fact]
    public async Task EnsureSchemaAsync_does_not_create_retired_player_score_observation_schema()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                to_regclass('public.player_score_observations') IS NULL,
                to_regclass('public.player_score_observation_union') IS NULL,
                to_regclass('public.player_score_observations_id_seq') IS NULL,
                to_regclass('public.player_score_observations_pkey') IS NULL,
                to_regclass('public.ux_pso_source') IS NULL
            """;

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            Assert.True(reader.GetBoolean(ordinal));
    }

    [Fact]
    public async Task EnsureSchemaAsync_excludes_only_retired_band_song_team_ranking_projection_schema()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                to_regclass('public.band_song_team_rankings') IS NULL,
                to_regclass('public.band_song_team_ranking_state') IS NULL,
                to_regclass('public.band_song_team_rankings_current_band_duets') IS NULL,
                to_regclass('public.band_song_team_rankings_current_band_trios') IS NULL,
                to_regclass('public.band_song_team_rankings_current_band_quad') IS NULL,
                to_regclass('public.band_song_team_rankings_pkey') IS NULL,
                to_regclass('public.band_song_team_ranking_state_pkey') IS NULL,
                to_regclass('public.band_song_team_rankings_current_band_duets_pkey') IS NULL,
                to_regclass('public.band_song_team_rankings_current_band_trios_pkey') IS NULL,
                to_regclass('public.band_song_team_rankings_current_band_quad_pkey') IS NULL,
                to_regclass('public.ix_bstr_team_best') IS NULL,
                to_regclass('public.ix_bstr_team_worst') IS NULL,
                to_regclass('public.band_current_projection_generation_seq') IS NOT NULL,
                to_regclass('public.current_band_leaderboard_entries') IS NOT NULL,
                to_regclass('public.current_band_leaderboard_entries_duets') IS NOT NULL,
                to_regclass('public.current_band_leaderboard_entries_trios') IS NOT NULL,
                to_regclass('public.current_band_leaderboard_entries_quad') IS NOT NULL,
                to_regclass('public.band_current_projection_scope') IS NOT NULL,
                to_regclass('public.band_current_projection_state') IS NOT NULL,
                to_regclass('public.band_team_rankings_current_band_duets') IS NOT NULL,
                to_regclass('public.band_team_rankings_current_band_trios') IS NOT NULL,
                to_regclass('public.band_team_rankings_current_band_quad') IS NOT NULL,
                to_regclass('public.band_team_rankings_published_band_duets') IS NOT NULL,
                to_regclass('public.band_team_rankings_published_band_trios') IS NOT NULL,
                to_regclass('public.band_team_rankings_published_band_quad') IS NOT NULL,
                to_regclass('public.band_team_ranking_stats_current_band_duets') IS NOT NULL,
                to_regclass('public.band_team_ranking_stats_current_band_trios') IS NOT NULL,
                to_regclass('public.band_team_ranking_stats_current_band_quad') IS NOT NULL,
                to_regclass('public.band_team_ranking_stats_published_band_duets') IS NOT NULL,
                to_regclass('public.band_team_ranking_stats_published_band_trios') IS NOT NULL,
                to_regclass('public.band_team_ranking_stats_published_band_quad') IS NOT NULL
            """;

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            Assert.True(reader.GetBoolean(ordinal));
    }

    [Fact]
    public async Task Retired_composite_history_latest_index_maintenance_sql_is_valid()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE INDEX CONCURRENTLY ix_crh_latest
                    ON public.composite_rank_history USING btree (account_id, snapshot_date DESC)
                """;
            create.ExecuteNonQuery();
        }

        using var verify = conn.CreateCommand();
        verify.CommandText = "SELECT pg_get_indexdef('public.ix_crh_latest'::regclass)";

        Assert.Equal(
            "CREATE INDEX ix_crh_latest ON public.composite_rank_history USING btree (account_id, snapshot_date DESC)",
            verify.ExecuteScalar());

        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "DROP INDEX CONCURRENTLY public.ix_crh_latest";
            drop.ExecuteNonQuery();
        }

        verify.CommandText = "SELECT to_regclass('public.ix_crh_latest') IS NULL";
        Assert.True((bool)verify.ExecuteScalar()!);
    }

    [Fact]
    public async Task EnsureSchemaAsync_does_not_recreate_retired_rank_history_latest_index()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT to_regclass('public.ix_rh_latest') IS NULL";

        Assert.True((bool)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task Retired_rank_history_latest_index_family_maintenance_sql_is_valid()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        foreach (var statement in new[]
        {
            """
            CREATE INDEX CONCURRENTLY rank_history_solo_guitar_instrument_account_id_snapshot_dat_idx
                ON public.rank_history_solo_guitar USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX CONCURRENTLY rank_history_solo_bass_instrument_account_id_snapshot_date_idx
                ON public.rank_history_solo_bass USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX CONCURRENTLY rank_history_solo_drums_instrument_account_id_snapshot_date_idx
                ON public.rank_history_solo_drums USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX CONCURRENTLY rank_history_solo_vocals_instrument_account_id_snapshot_dat_idx
                ON public.rank_history_solo_vocals USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX CONCURRENTLY rank_history_pro_guitar_instrument_account_id_snapshot_date_idx
                ON public.rank_history_pro_guitar USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX CONCURRENTLY rank_history_pro_bass_instrument_account_id_snapshot_date_idx
                ON public.rank_history_pro_bass USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX CONCURRENTLY rank_history_pro_vocals_instrument_account_id_snapshot_date_idx
                ON public.rank_history_pro_vocals USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX CONCURRENTLY rank_history_pro_cymbals_instrument_account_id_snapshot_dat_idx
                ON public.rank_history_pro_cymbals USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX CONCURRENTLY rank_history_pro_drums_instrument_account_id_snapshot_date_idx
                ON public.rank_history_pro_drums USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            CREATE INDEX ix_rh_latest
                ON ONLY public.rank_history USING btree (instrument, account_id, snapshot_date DESC)
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_solo_guitar_instrument_account_id_snapshot_dat_idx
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_solo_bass_instrument_account_id_snapshot_date_idx
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_solo_drums_instrument_account_id_snapshot_date_idx
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_solo_vocals_instrument_account_id_snapshot_dat_idx
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_pro_guitar_instrument_account_id_snapshot_date_idx
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_pro_bass_instrument_account_id_snapshot_date_idx
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_pro_vocals_instrument_account_id_snapshot_date_idx
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_pro_cymbals_instrument_account_id_snapshot_dat_idx
            """,
            """
            ALTER INDEX public.ix_rh_latest
                ATTACH PARTITION public.rank_history_pro_drums_instrument_account_id_snapshot_date_idx
            """,
        })
        {
            using var maintenance = conn.CreateCommand();
            maintenance.CommandText = statement;
            maintenance.ExecuteNonQuery();
        }

        using (var verify = conn.CreateCommand())
        {
            verify.CommandText = """
                SELECT COUNT(*)
                FROM pg_index
                WHERE (
                    indexrelid = 'public.ix_rh_latest'::regclass
                    OR indexrelid IN (
                        SELECT inhrelid
                        FROM pg_inherits
                        WHERE inhparent = 'public.ix_rh_latest'::regclass)
                )
                  AND indisvalid
                  AND indisready
                """;

            Assert.Equal(10L, (long)verify.ExecuteScalar()!);
        }

        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "DROP INDEX public.ix_rh_latest";
            drop.ExecuteNonQuery();
        }

        using var removed = conn.CreateCommand();
        removed.CommandText = """
            SELECT
                to_regclass('public.ix_rh_latest') IS NULL,
                to_regclass('public.rank_history_solo_guitar_instrument_account_id_snapshot_dat_idx') IS NULL,
                to_regclass('public.rank_history_pro_bass_instrument_account_id_snapshot_date_idx') IS NULL
            """;
        using var removedReader = removed.ExecuteReader();
        Assert.True(removedReader.Read());
        Assert.True(removedReader.GetBoolean(0));
        Assert.True(removedReader.GetBoolean(1));
        Assert.True(removedReader.GetBoolean(2));
    }

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
