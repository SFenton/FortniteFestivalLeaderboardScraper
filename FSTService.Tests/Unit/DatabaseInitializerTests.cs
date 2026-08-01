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
    public async Task EnsureSchemaAsync_adds_registered_band_window_identity_columns_idempotently()
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
                      AND is_nullable = 'NO')
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
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
    public async Task EnsureSchemaAsync_does_not_recreate_retired_logical_shadow_secondary_indexes()
    {
        await DatabaseInitializer.EnsureSchemaAsync(_metaFixture.DataSource);

        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
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
