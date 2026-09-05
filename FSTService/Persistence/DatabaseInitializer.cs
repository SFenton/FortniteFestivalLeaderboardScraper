using Npgsql;

namespace FSTService.Persistence;

/// <summary>
/// Creates the PostgreSQL schema for FSTService.
/// All statements are idempotent (IF NOT EXISTS).
/// </summary>
public static class DatabaseInitializer
{
    private const int NotificationSchemaCommandTimeoutSeconds = 20;
    private const string NotificationSchemaLockTimeout = "2s";
    private const string NotificationSchemaStatementTimeout = "15s";
    private const string NotificationSchemaPrerequisites = """
        CREATE EXTENSION IF NOT EXISTS pgcrypto;

        CREATE TABLE IF NOT EXISTS scrape_log (
            id              SERIAL      PRIMARY KEY,
            started_at      TIMESTAMPTZ NOT NULL,
            completed_at    TIMESTAMPTZ,
            songs_scraped   INTEGER,
            total_entries   INTEGER,
            total_requests  INTEGER,
            total_bytes     BIGINT,
            epic_reported_over_100_pages BOOLEAN NOT NULL DEFAULT FALSE
        );

        CREATE TABLE IF NOT EXISTS scrape_publication_state (
            id                  BOOLEAN     PRIMARY KEY DEFAULT TRUE CHECK (id),
            published_scrape_id INTEGER     REFERENCES scrape_log(id),
            published_at        TIMESTAMPTZ,
            public_reads_frozen BOOLEAN     NOT NULL DEFAULT FALSE,
            public_reads_frozen_at TIMESTAMPTZ,
            public_reads_frozen_scrape_id INTEGER REFERENCES scrape_log(id),
            public_reads_frozen_reason TEXT,
            publication_commit_intent_started_at TIMESTAMPTZ,
            publication_commit_intent_heartbeat_at TIMESTAMPTZ,
            publication_commit_intent_owner TEXT,
            band_projection_generation BIGINT,
            max_score_mutation_gate_token TEXT,
            max_score_mutation_gate_publication_id BIGINT,
            max_score_mutation_gate_backend_pid INTEGER,
            max_score_mutation_gate_backend_start TIMESTAMPTZ,
            max_score_mutation_gate_acquired_at TIMESTAMPTZ,
            updated_at          TIMESTAMPTZ NOT NULL
        );
        """;

    public static async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        foreach (var step in GetSchemaInitializationPlan())
        {
            if (step.UseConcurrentIndex)
            {
                await ExecuteConcurrentIndexInitializationStepAsync(
                    dataSource,
                    step,
                    ct);
                continue;
            }

            await ExecuteSchemaInitializationStepAsync(
                conn,
                step,
                ct);
        }

        // Advance SERIAL sequences after COPY-style explicit ID inserts, but never rewind them after retention/deletion.
        await using var seqCmd = conn.CreateCommand();
        seqCmd.CommandText = """
            SELECT setval('scrape_log_id_seq', GREATEST(COALESCE((SELECT MAX(id) FROM scrape_log), 0) + 1, (SELECT last_value + CASE WHEN is_called THEN 1 ELSE 0 END FROM scrape_log_id_seq)), false);
            SELECT setval('score_history_id_seq', GREATEST(COALESCE((SELECT MAX(id) FROM score_history), 0) + 1, (SELECT last_value + CASE WHEN is_called THEN 1 ELSE 0 END FROM score_history_id_seq)), false);
            SELECT setval('user_sessions_id_seq', GREATEST(COALESCE((SELECT MAX(id) FROM user_sessions), 0) + 1, (SELECT last_value + CASE WHEN is_called THEN 1 ELSE 0 END FROM user_sessions_id_seq)), false);
            """;
        await seqCmd.ExecuteNonQueryAsync(ct);
    }

    internal static async Task
        EnsurePublicationGenerationRetirementSchemaAsync(
            NpgsqlDataSource dataSource,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        foreach (var step in GetSchemaInitializationPlan()
                     .Where(static item =>
                         item.Name is
                             "publication-generation-retirement-columns"
                             or
                             "publication-generation-retirement-index"))
        {
            if (step.UseConcurrentIndex)
            {
                await ExecuteConcurrentIndexInitializationStepAsync(
                    dataSource,
                    step,
                    ct);
                continue;
            }

            await using var connection =
                await dataSource.OpenConnectionAsync(ct);
            await ExecuteSchemaInitializationStepAsync(
                connection,
                step,
                ct);
        }
    }

    internal static async Task
        EnsurePublicationGenerationRetirementIndexAsync(
            NpgsqlDataSource dataSource,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var step = GetSchemaInitializationPlan()
            .Single(static item =>
                item.Name ==
                    "publication-generation-retirement-index");
        await ExecuteConcurrentIndexInitializationStepAsync(
            dataSource,
            step,
            ct);
    }

    internal static async Task
        EnsurePublicationGenerationForeignKeysAsync(
            NpgsqlDataSource dataSource,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var step = GetSchemaInitializationPlan()
            .Single(static item =>
                item.Name ==
                    "publication-generation-foreign-keys");
        await using var connection =
            await dataSource.OpenConnectionAsync(ct);
        await ExecuteSchemaInitializationStepAsync(
            connection,
            step,
            ct);
    }

    private static async Task ExecuteSchemaInitializationStepAsync(
        NpgsqlConnection connection,
        DatabaseSchemaInitializationStep step,
        CancellationToken ct)
    {
        if (step.UseShortTransaction)
        {
            await using var transaction =
                await connection.BeginTransactionAsync(ct);
            await using (var timeout = connection.CreateCommand())
            {
                timeout.Transaction = transaction;
                timeout.CommandTimeout =
                    NotificationSchemaCommandTimeoutSeconds;
                timeout.CommandText = """
                    SELECT set_config(
                        'lock_timeout',
                        @lockTimeout,
                        true);
                    SELECT set_config(
                        'statement_timeout',
                        @statementTimeout,
                        true);
                    """;
                timeout.Parameters.AddWithValue(
                    "lockTimeout",
                    step.LockTimeout
                    ?? NotificationSchemaLockTimeout);
                timeout.Parameters.AddWithValue(
                    "statementTimeout",
                    step.StatementTimeout
                    ?? NotificationSchemaStatementTimeout);
                await timeout.ExecuteNonQueryAsync(ct);
            }

            await using (var command =
                         connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandTimeout =
                    step.CommandTimeoutSeconds;
                command.CommandText = step.Sql;
                await command.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return;
        }

        await using var unbounded =
            connection.CreateCommand();
        unbounded.CommandTimeout = step.CommandTimeoutSeconds;
        unbounded.CommandText = step.Sql;
        await unbounded.ExecuteNonQueryAsync(ct);
    }

    private static async Task
        ExecuteConcurrentIndexInitializationStepAsync(
            NpgsqlDataSource dataSource,
            DatabaseSchemaInitializationStep step,
            CancellationToken ct)
    {
        if (!step.UseConcurrentIndex
            || string.IsNullOrWhiteSpace(
                step.ValidationSql)
            || string.IsNullOrWhiteSpace(
                step.CleanupSql))
        {
            throw new InvalidOperationException(
                $"Concurrent index initialization step {step.Name} is incomplete.");
        }

        await using var connection =
            await dataSource.OpenConnectionAsync(ct);
        await using (var timeout = connection.CreateCommand())
        {
            timeout.CommandTimeout =
                step.CommandTimeoutSeconds;
            timeout.CommandText = """
                SELECT set_config(
                    'lock_timeout',
                    @lockTimeout,
                    false);
                SELECT set_config(
                    'statement_timeout',
                    @statementTimeout,
                    false);
                """;
            timeout.Parameters.AddWithValue(
                "lockTimeout",
                step.LockTimeout
                ?? NotificationSchemaLockTimeout);
            timeout.Parameters.AddWithValue(
                "statementTimeout",
                step.StatementTimeout
                ?? NotificationSchemaStatementTimeout);
            await timeout.ExecuteNonQueryAsync(ct);
        }

        var advisoryLockHeld = false;
        try
        {
            await using (var advisoryLock =
                         connection.CreateCommand())
            {
                advisoryLock.CommandTimeout =
                    step.CommandTimeoutSeconds;
                advisoryLock.CommandText =
                    "SELECT pg_advisory_lock(@lockKey)";
                advisoryLock.Parameters.AddWithValue(
                    "lockKey",
                    PublicationGenerationRetirementSchemaMigration
                        .AdvisoryLockKey);
                await advisoryLock.ExecuteNonQueryAsync(ct);
                advisoryLockHeld = true;
            }

            if (await IsConcurrentIndexValidAsync(
                    connection,
                    step,
                    ct))
            {
                return;
            }

            await using (var cleanup =
                         connection.CreateCommand())
            {
                cleanup.CommandTimeout =
                    step.CommandTimeoutSeconds;
                cleanup.CommandText =
                    step.CleanupSql;
                await cleanup.ExecuteNonQueryAsync(ct);
            }

            await using (var create =
                         connection.CreateCommand())
            {
                create.CommandTimeout =
                    step.CommandTimeoutSeconds;
                create.CommandText = step.Sql;
                await create.ExecuteNonQueryAsync(ct);
            }

            if (!await IsConcurrentIndexValidAsync(
                    connection,
                    step,
                    ct))
            {
                throw new InvalidOperationException(
                    $"Concurrent index initialization step {step.Name} did not produce its exact valid index.");
            }
        }
        finally
        {
            if (advisoryLockHeld)
            {
                await using var advisoryUnlock =
                    connection.CreateCommand();
                advisoryUnlock.CommandTimeout =
                    step.CommandTimeoutSeconds;
                advisoryUnlock.CommandText =
                    "SELECT pg_advisory_unlock(@lockKey)";
                advisoryUnlock.Parameters.AddWithValue(
                    "lockKey",
                    PublicationGenerationRetirementSchemaMigration
                        .AdvisoryLockKey);
                var unlocked =
                    (bool)(await advisoryUnlock
                        .ExecuteScalarAsync(
                            CancellationToken.None))!;
                if (!unlocked)
                {
                    throw new InvalidOperationException(
                        $"Concurrent index initialization step {step.Name} lost its advisory lock.");
                }
            }
        }
    }

    private static async Task<bool>
        IsConcurrentIndexValidAsync(
            NpgsqlConnection connection,
            DatabaseSchemaInitializationStep step,
            CancellationToken ct)
    {
        await using var validation =
            connection.CreateCommand();
        validation.CommandTimeout =
            step.CommandTimeoutSeconds;
        validation.CommandText =
            step.ValidationSql!;
        return (bool)(await validation
            .ExecuteScalarAsync(ct))!;
    }

    internal static IReadOnlyList<DatabaseSchemaInitializationStep>
        GetSchemaInitializationPlan() =>
        [
            new(
                Name: "improvement-notifications",
                Sql:
                    $"{NotificationSchemaPrerequisites}" +
                    $"{Environment.NewLine}{Environment.NewLine}" +
                    ImprovementNotificationSchema.Sql,
                CommandTimeoutSeconds: NotificationSchemaCommandTimeoutSeconds,
                UseShortTransaction: true,
                LockTimeout: NotificationSchemaLockTimeout,
                StatementTimeout: NotificationSchemaStatementTimeout),
            new(
                Name: "score-history-dedup-audit",
                Sql: ScoreHistoryDedupMaintenanceSchema.Sql,
                CommandTimeoutSeconds: NotificationSchemaCommandTimeoutSeconds,
                UseShortTransaction: true,
                LockTimeout: NotificationSchemaLockTimeout,
                StatementTimeout: NotificationSchemaStatementTimeout),
            new(
                Name: "main-publication",
                Sql:
                    $"{Schema}{Environment.NewLine}{Environment.NewLine}" +
                    $"{BandRankingStorageNames.GetCurrentSchemaSql()}" +
                    $"{Environment.NewLine}{Environment.NewLine}" +
                    PublicationGenerationSchema.Sql,
                CommandTimeoutSeconds: 0,
                UseShortTransaction: false,
                LockTimeout: null,
                StatementTimeout: null),
            new(
                Name:
                    "publication-generation-retirement-columns",
                Sql:
                    PublicationGenerationRetirementSchemaMigration
                        .ColumnsSql,
                CommandTimeoutSeconds:
                    NotificationSchemaCommandTimeoutSeconds,
                UseShortTransaction: true,
                LockTimeout: NotificationSchemaLockTimeout,
                StatementTimeout:
                    NotificationSchemaStatementTimeout),
            new(
                Name:
                    "publication-generation-foreign-keys",
                Sql:
                    PublicationGenerationForeignKeyMigration
                        .Sql,
                CommandTimeoutSeconds:
                    NotificationSchemaCommandTimeoutSeconds,
                UseShortTransaction: true,
                LockTimeout: NotificationSchemaLockTimeout,
                StatementTimeout:
                    NotificationSchemaStatementTimeout),
            new(
                Name:
                    "publication-generation-retirement-index",
                Sql:
                    PublicationGenerationRetirementSchemaMigration
                        .CreateIndexSql,
                CommandTimeoutSeconds:
                    NotificationSchemaCommandTimeoutSeconds,
                UseShortTransaction: false,
                LockTimeout: NotificationSchemaLockTimeout,
                StatementTimeout:
                    NotificationSchemaStatementTimeout,
                UseConcurrentIndex: true,
                ValidationSql:
                    PublicationGenerationRetirementSchemaMigration
                        .IndexValidationSql,
                CleanupSql:
                    PublicationGenerationRetirementSchemaMigration
                        .DropIndexSql),
            new(
                Name: "publication-path-artifacts",
                Sql: PublicationPathArtifactSchema.Sql,
                CommandTimeoutSeconds:
                    NotificationSchemaCommandTimeoutSeconds,
                UseShortTransaction: true,
                LockTimeout: NotificationSchemaLockTimeout,
                StatementTimeout: NotificationSchemaStatementTimeout),
            new(
                Name:
                    "snapshot-generation-retention-report-only",
                Sql: Maintenance
                    .SnapshotGenerationRetentionSchema.Sql,
                CommandTimeoutSeconds:
                    NotificationSchemaCommandTimeoutSeconds,
                UseShortTransaction: true,
                LockTimeout: NotificationSchemaLockTimeout,
                StatementTimeout:
                    NotificationSchemaStatementTimeout),
            new(
                Name:
                    "snapshot-generation-retirement-control-plane",
                Sql: Maintenance
                    .SnapshotGenerationRetirementSchema.Sql,
                CommandTimeoutSeconds:
                    NotificationSchemaCommandTimeoutSeconds,
                UseShortTransaction: true,
                LockTimeout: NotificationSchemaLockTimeout,
                StatementTimeout:
                    NotificationSchemaStatementTimeout),
            new(
                Name:
                    "snapshot-generation-quarantine",
                Sql: Maintenance
                    .SnapshotGenerationQuarantineSchema.Sql,
                CommandTimeoutSeconds:
                    NotificationSchemaCommandTimeoutSeconds,
                UseShortTransaction: true,
                LockTimeout: NotificationSchemaLockTimeout,
                StatementTimeout:
                    NotificationSchemaStatementTimeout),
            new(
                Name:
                    "snapshot-generation-drop",
                Sql: Maintenance
                    .SnapshotGenerationDropSchema.Sql,
                CommandTimeoutSeconds:
                    NotificationSchemaCommandTimeoutSeconds,
                UseShortTransaction: true,
                LockTimeout: NotificationSchemaLockTimeout,
                StatementTimeout:
                    NotificationSchemaStatementTimeout),
            new(
                Name: "max-score-maintenance",
                Sql: MaxScoreMaintenanceSchema.Sql,
                CommandTimeoutSeconds:
                    NotificationSchemaCommandTimeoutSeconds,
                UseShortTransaction: true,
                LockTimeout: NotificationSchemaLockTimeout,
                StatementTimeout:
                    NotificationSchemaStatementTimeout),
        ];

    // ── Complete DDL ──────────────────────────────────────────────────────

    private const string Schema = """

        CREATE EXTENSION IF NOT EXISTS pg_trgm;
        CREATE EXTENSION IF NOT EXISTS pgcrypto;

        -- =====================================================================
        -- SONGS (from fst-service.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS songs (
            song_id              TEXT        PRIMARY KEY,
            title                TEXT,
            artist               TEXT,
            active_date          TEXT,
            last_modified        TEXT,
            image_path           TEXT,
            lead_diff            INTEGER,
            bass_diff            INTEGER,
            vocals_diff          INTEGER,
            drums_diff           INTEGER,
            pro_lead_diff        INTEGER,
            pro_bass_diff        INTEGER,
            release_year         INTEGER,
            tempo                INTEGER,
            plastic_guitar_diff  INTEGER,
            plastic_bass_diff    INTEGER,
            plastic_drums_diff   INTEGER,
            pro_vocals_diff      INTEGER,
            provider_json         JSONB,
            -- Path generation fields (from PathDataStore)
            max_lead_score       INTEGER,
            max_bass_score       INTEGER,
            max_drums_score      INTEGER,
            max_vocals_score     INTEGER,
            max_pro_lead_score   INTEGER,
            max_pro_bass_score   INTEGER,
            max_pro_cymbals_score INTEGER,
            max_pro_drums_score  INTEGER,
            dat_file_hash        TEXT,
            song_last_modified   TEXT,
            paths_generated_at   TIMESTAMPTZ,
            chopt_version        TEXT,
            chopt_binary_sha256  TEXT,
            path_generation_profile TEXT,
            path_artifact_generation_id TEXT,
            path_expected_instruments TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
            path_generation_revision BIGINT NOT NULL DEFAULT 0,
            path_generation_pending BOOLEAN NOT NULL DEFAULT FALSE
        );

        ALTER TABLE songs
            ADD COLUMN IF NOT EXISTS provider_json JSONB;
        ALTER TABLE songs
            ADD COLUMN IF NOT EXISTS max_pro_cymbals_score INTEGER;
        ALTER TABLE songs
            ADD COLUMN IF NOT EXISTS max_pro_drums_score INTEGER;
        ALTER TABLE songs
            ADD COLUMN IF NOT EXISTS chopt_binary_sha256 TEXT;
        ALTER TABLE songs
            ADD COLUMN IF NOT EXISTS path_generation_profile TEXT;
        ALTER TABLE songs
            ADD COLUMN IF NOT EXISTS path_artifact_generation_id TEXT;
        ALTER TABLE songs
            ADD COLUMN IF NOT EXISTS path_expected_instruments TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[];
        ALTER TABLE songs
            ADD COLUMN IF NOT EXISTS path_generation_revision BIGINT NOT NULL DEFAULT 0;
        ALTER TABLE songs
            ADD COLUMN IF NOT EXISTS path_generation_pending BOOLEAN NOT NULL DEFAULT FALSE;

        -- Publication-safe scrape-pass staging deferral state. These columns
        -- never clear path_generation_pending: a deferred song stays pending
        -- and auditable, it is only excluded from automatic selection.
        ALTER TABLE songs
            ADD COLUMN IF NOT EXISTS path_generation_review_required BOOLEAN NOT NULL DEFAULT FALSE;
        ALTER TABLE songs
            ADD COLUMN IF NOT EXISTS path_generation_review_reason TEXT;
        ALTER TABLE songs
            ADD COLUMN IF NOT EXISTS path_generation_review_at TIMESTAMPTZ;
        ALTER TABLE songs
            ADD COLUMN IF NOT EXISTS path_generation_next_attempt_at TIMESTAMPTZ;
        ALTER TABLE songs
            ADD COLUMN IF NOT EXISTS path_generation_attempt_count INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE songs
            ADD COLUMN IF NOT EXISTS path_generation_deferral_identity TEXT;

        CREATE OR REPLACE FUNCTION reject_incoherent_legacy_path_write()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            IF (OLD.path_generation_revision > 0
                OR OLD.path_artifact_generation_id IS NOT NULL)
               AND NEW.path_generation_revision = OLD.path_generation_revision
               AND ROW(
                    NEW.max_lead_score,
                    NEW.max_bass_score,
                    NEW.max_drums_score,
                    NEW.max_vocals_score,
                    NEW.max_pro_lead_score,
                    NEW.max_pro_bass_score,
                    NEW.max_pro_cymbals_score,
                    NEW.max_pro_drums_score,
                    NEW.dat_file_hash,
                    NEW.song_last_modified,
                    NEW.paths_generated_at,
                    NEW.chopt_version,
                    NEW.chopt_binary_sha256,
                    NEW.path_generation_profile,
                    NEW.path_artifact_generation_id,
                    NEW.path_expected_instruments)
                   IS DISTINCT FROM
                   ROW(
                    OLD.max_lead_score,
                    OLD.max_bass_score,
                    OLD.max_drums_score,
                    OLD.max_vocals_score,
                    OLD.max_pro_lead_score,
                    OLD.max_pro_bass_score,
                    OLD.max_pro_cymbals_score,
                    OLD.max_pro_drums_score,
                    OLD.dat_file_hash,
                    OLD.song_last_modified,
                    OLD.paths_generated_at,
                    OLD.chopt_version,
                    OLD.chopt_binary_sha256,
                    OLD.path_generation_profile,
                    OLD.path_artifact_generation_id,
                    OLD.path_expected_instruments)
            THEN
                RAISE EXCEPTION
                    'Legacy path metadata write rejected for song %; atomic generation revision must advance.',
                    OLD.song_id
                    USING ERRCODE = '55000';
            END IF;

            RETURN NEW;
        END
        $$;

        DROP TRIGGER IF EXISTS trg_reject_incoherent_legacy_path_write ON songs;
        CREATE TRIGGER trg_reject_incoherent_legacy_path_write
        BEFORE UPDATE OF
            max_lead_score,
            max_bass_score,
            max_drums_score,
            max_vocals_score,
            max_pro_lead_score,
            max_pro_bass_score,
            max_pro_cymbals_score,
            max_pro_drums_score,
            dat_file_hash,
            song_last_modified,
            paths_generated_at,
            chopt_version,
            chopt_binary_sha256,
            path_generation_profile,
            path_artifact_generation_id,
            path_expected_instruments,
            path_generation_revision
        ON songs
        FOR EACH ROW
        EXECUTE FUNCTION reject_incoherent_legacy_path_write();

        CREATE TABLE IF NOT EXISTS path_generation_errors (
            id                      BIGSERIAL PRIMARY KEY,
            attempt_id              TEXT        NOT NULL,
            song_id                 TEXT        NOT NULL,
            dat_file_hash           TEXT,
            chopt_version           TEXT,
            chopt_binary_sha256     TEXT,
            path_generation_profile TEXT,
            expected_instruments    TEXT[]      NOT NULL DEFAULT ARRAY[]::TEXT[],
            failure_stage           TEXT        NOT NULL,
            instrument              TEXT,
            difficulty              TEXT,
            detail                  TEXT        NOT NULL CHECK (length(detail) <= 2048),
            created_at              TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- =====================================================================
        -- LEADERBOARD ENTRIES (partitioned by instrument)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS leaderboard_entries (
            song_id        TEXT        NOT NULL,
            instrument     TEXT        NOT NULL,
            account_id     TEXT        NOT NULL,
            score          INTEGER     NOT NULL,
            accuracy       INTEGER,
            is_full_combo  BOOLEAN,
            stars          INTEGER,
            season         INTEGER,
            percentile     REAL,
            rank           INTEGER     DEFAULT 0,
            source         TEXT        NOT NULL DEFAULT 'scrape',
            difficulty     INTEGER     DEFAULT -1,
            api_rank       INTEGER,
            end_time       TEXT,
            band_members_json JSONB,
            band_score     INTEGER,
            base_score     INTEGER,
            instrument_bonus INTEGER,
            overdrive_bonus INTEGER,
            instrument_combo TEXT,
            first_seen_at  TIMESTAMPTZ NOT NULL,
            last_updated_at TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (song_id, instrument, account_id)
        ) PARTITION BY LIST (instrument);

        -- FILLFACTOR=85 leaves 15% free space per page for HOT updates (updates
        -- that don't touch indexed columns can be performed in-page without
        -- re-inserting into every index). leaderboard_entries sees ~25× more
        -- UPDATEs than INSERTs (score/rank rewrites during scrape), so HOT
        -- significantly reduces index bloat and WAL volume.
        CREATE TABLE IF NOT EXISTS leaderboard_entries_solo_guitar    PARTITION OF leaderboard_entries FOR VALUES IN ('Solo_Guitar')            WITH (fillfactor=85);
        CREATE TABLE IF NOT EXISTS leaderboard_entries_solo_bass      PARTITION OF leaderboard_entries FOR VALUES IN ('Solo_Bass')              WITH (fillfactor=85);
        CREATE TABLE IF NOT EXISTS leaderboard_entries_solo_drums     PARTITION OF leaderboard_entries FOR VALUES IN ('Solo_Drums')             WITH (fillfactor=85);
        CREATE TABLE IF NOT EXISTS leaderboard_entries_solo_vocals    PARTITION OF leaderboard_entries FOR VALUES IN ('Solo_Vocals')            WITH (fillfactor=85);
        CREATE TABLE IF NOT EXISTS leaderboard_entries_pro_guitar     PARTITION OF leaderboard_entries FOR VALUES IN ('Solo_PeripheralGuitar')  WITH (fillfactor=85);
        CREATE TABLE IF NOT EXISTS leaderboard_entries_pro_bass       PARTITION OF leaderboard_entries FOR VALUES IN ('Solo_PeripheralBass')    WITH (fillfactor=85);
        CREATE TABLE IF NOT EXISTS leaderboard_entries_pro_vocals     PARTITION OF leaderboard_entries FOR VALUES IN ('Solo_PeripheralVocals')  WITH (fillfactor=85);
        CREATE TABLE IF NOT EXISTS leaderboard_entries_pro_cymbals    PARTITION OF leaderboard_entries FOR VALUES IN ('Solo_PeripheralCymbals') WITH (fillfactor=85);
        CREATE TABLE IF NOT EXISTS leaderboard_entries_pro_drums      PARTITION OF leaderboard_entries FOR VALUES IN ('Solo_PeripheralDrums')   WITH (fillfactor=85);

        -- Idempotent migration: ensure fillfactor is applied on pre-existing
        -- partitions from databases created before the FILLFACTOR change.
        -- ALTER TABLE SET (fillfactor=...) is metadata-only and cheap; new
        -- pages (and pages rewritten by VACUUM FULL / pg_repack) honour it.
        ALTER TABLE leaderboard_entries_solo_guitar    SET (fillfactor=85);
        ALTER TABLE leaderboard_entries_solo_bass      SET (fillfactor=85);
        ALTER TABLE leaderboard_entries_solo_drums     SET (fillfactor=85);
        ALTER TABLE leaderboard_entries_solo_vocals    SET (fillfactor=85);
        ALTER TABLE leaderboard_entries_pro_guitar     SET (fillfactor=85);
        ALTER TABLE leaderboard_entries_pro_bass       SET (fillfactor=85);
        ALTER TABLE leaderboard_entries_pro_vocals     SET (fillfactor=85);
        ALTER TABLE leaderboard_entries_pro_cymbals    SET (fillfactor=85);
        ALTER TABLE leaderboard_entries_pro_drums      SET (fillfactor=85);

        CREATE INDEX IF NOT EXISTS ix_le_song_score
            ON leaderboard_entries (song_id, instrument, score DESC);
        -- ix_le_account removed 2026-04-23 (Phase 2): total 3-3-0-3-3-0 scans
        -- across partitions over the lifetime of the database, vs. ~2 GB of
        -- storage. The composite ix_le_account_song index (account_id, song_id,
        -- instrument) covers the (account_id, instrument) prefix for any query
        -- that could benefit.
        CREATE INDEX IF NOT EXISTS ix_le_account_song
            ON leaderboard_entries (account_id, song_id, instrument);
        CREATE INDEX IF NOT EXISTS ix_le_song_source
            ON leaderboard_entries (song_id, instrument, source);
        -- ix_le_song_rank is intentionally absent from bootstrap DDL. Its
        -- remaining live parent/leaf family is owned only by the guarded
        -- retirement package after a dated zero-use observation.

        CREATE TABLE IF NOT EXISTS instrument_scrape_state (
            instrument         TEXT        PRIMARY KEY,
            max_observed_season INTEGER    NOT NULL,
            last_scrape_id     BIGINT,
            updated_at         TIMESTAMPTZ NOT NULL
        );

        -- =====================================================================
        -- OPTION B SCAFFOLDING (snapshot + overlay current-state model)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot (
            snapshot_id        BIGINT      NOT NULL,
            song_id            TEXT        NOT NULL,
            instrument         TEXT        NOT NULL,
            account_id         TEXT        NOT NULL,
            score              INTEGER     NOT NULL,
            accuracy           INTEGER,
            is_full_combo      BOOLEAN,
            stars              INTEGER,
            season             INTEGER,
            percentile         REAL,
            rank               INTEGER     DEFAULT 0,
            source             TEXT        NOT NULL DEFAULT 'scrape',
            difficulty         INTEGER     DEFAULT -1,
            api_rank           INTEGER,
            end_time           TEXT,
            band_members_json  JSONB,
            band_score         INTEGER,
            base_score         INTEGER,
            instrument_bonus   INTEGER,
            overdrive_bonus    INTEGER,
            instrument_combo   TEXT,
            first_seen_at      TIMESTAMPTZ NOT NULL,
            last_updated_at    TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (snapshot_id, song_id, instrument, account_id)
        ) PARTITION BY LIST (instrument);

        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_solo_guitar    PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_Guitar')            PARTITION BY LIST (snapshot_id);
        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_solo_bass      PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_Bass')              PARTITION BY LIST (snapshot_id);
        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_solo_drums     PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_Drums')             PARTITION BY LIST (snapshot_id);
        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_solo_vocals    PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_Vocals')            PARTITION BY LIST (snapshot_id);
        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_pro_guitar     PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_PeripheralGuitar')  PARTITION BY LIST (snapshot_id);
        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_pro_bass       PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_PeripheralBass')    PARTITION BY LIST (snapshot_id);
        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_pro_vocals     PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_PeripheralVocals')  PARTITION BY LIST (snapshot_id);
        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_pro_cymbals    PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_PeripheralCymbals') PARTITION BY LIST (snapshot_id);
        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_pro_drums      PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_PeripheralDrums')   PARTITION BY LIST (snapshot_id);

        DO $snapshot_defaults$
        DECLARE
            partition_name TEXT;
        BEGIN
            FOREACH partition_name IN ARRAY ARRAY[
                'leaderboard_entries_snapshot_solo_guitar',
                'leaderboard_entries_snapshot_solo_bass',
                'leaderboard_entries_snapshot_solo_drums',
                'leaderboard_entries_snapshot_solo_vocals',
                'leaderboard_entries_snapshot_pro_guitar',
                'leaderboard_entries_snapshot_pro_bass',
                'leaderboard_entries_snapshot_pro_vocals',
                'leaderboard_entries_snapshot_pro_cymbals',
                'leaderboard_entries_snapshot_pro_drums'
            ]
            LOOP
                IF EXISTS (
                    SELECT 1
                    FROM pg_class relation
                    JOIN pg_namespace namespace
                      ON namespace.oid = relation.relnamespace
                    WHERE namespace.nspname = 'public'
                      AND relation.relname = partition_name
                      AND relation.relkind = 'p'
                ) THEN
                    EXECUTE format(
                        'CREATE TABLE IF NOT EXISTS public.%I PARTITION OF public.%I DEFAULT',
                        partition_name || '_default',
                        partition_name);
                END IF;
            END LOOP;
        END
        $snapshot_defaults$;

        CREATE OR REPLACE FUNCTION ensure_leaderboard_snapshot_generation_partition(
            p_instrument TEXT,
            p_snapshot_id BIGINT)
        RETURNS TEXT
        LANGUAGE plpgsql
        AS $snapshot_generation$
        DECLARE
            instrument_partition TEXT;
            generation_partition TEXT;
            observed_bound TEXT;
            observed_oid BIGINT;
            active_hold_exists BOOLEAN := FALSE;
            committed_drop_exists BOOLEAN := FALSE;
        BEGIN
            IF p_snapshot_id <= 0 THEN
                RAISE EXCEPTION 'snapshot generation ID must be positive';
            END IF;

            instrument_partition := CASE p_instrument
                WHEN 'Solo_Guitar' THEN 'leaderboard_entries_snapshot_solo_guitar'
                WHEN 'Solo_Bass' THEN 'leaderboard_entries_snapshot_solo_bass'
                WHEN 'Solo_Drums' THEN 'leaderboard_entries_snapshot_solo_drums'
                WHEN 'Solo_Vocals' THEN 'leaderboard_entries_snapshot_solo_vocals'
                WHEN 'Solo_PeripheralGuitar' THEN 'leaderboard_entries_snapshot_pro_guitar'
                WHEN 'Solo_PeripheralBass' THEN 'leaderboard_entries_snapshot_pro_bass'
                WHEN 'Solo_PeripheralVocals' THEN 'leaderboard_entries_snapshot_pro_vocals'
                WHEN 'Solo_PeripheralCymbals' THEN 'leaderboard_entries_snapshot_pro_cymbals'
                WHEN 'Solo_PeripheralDrums' THEN 'leaderboard_entries_snapshot_pro_drums'
                ELSE NULL
            END;

            IF instrument_partition IS NULL THEN
                RAISE EXCEPTION 'unsupported snapshot instrument: %', p_instrument;
            END IF;

            -- Existing production partitions remain regular tables until the
            -- guarded migration converts them. The write path stays compatible
            -- before and after that cutover.
            IF NOT EXISTS (
                SELECT 1
                FROM pg_class relation
                JOIN pg_namespace namespace
                  ON namespace.oid = relation.relnamespace
                WHERE namespace.nspname = 'public'
                  AND relation.relname = instrument_partition
                  AND relation.relkind = 'p'
            ) THEN
                RETURN instrument_partition;
            END IF;

            generation_partition :=
                instrument_partition || '_s' || p_snapshot_id::TEXT;

            IF to_regclass(
                    'public.snapshot_generation_retention_holds')
                    IS NOT NULL
            THEN
                EXECUTE
                    'SELECT EXISTS (
                        SELECT 1
                        FROM public.snapshot_generation_retention_holds
                        WHERE instrument = $1
                          AND snapshot_id = $2
                          AND hold_kind IN (
                                ''retention_in_flight'',
                                ''restore_in_flight'')
                          AND released_at IS NULL)'
                INTO active_hold_exists
                USING p_instrument, p_snapshot_id;
                IF active_hold_exists THEN
                    RAISE EXCEPTION
                        'snapshot generation %/% has an active retention or restore hold',
                        p_instrument,
                        p_snapshot_id
                        USING ERRCODE = '55000';
                END IF;
            END IF;

            SELECT
                pg_get_expr(
                    relation.relpartbound,
                    relation.oid,
                    TRUE),
                relation.oid::BIGINT
            INTO
                observed_bound,
                observed_oid
            FROM pg_class relation
            JOIN pg_namespace namespace
              ON namespace.oid = relation.relnamespace
            JOIN pg_inherits inheritance
              ON inheritance.inhrelid = relation.oid
            WHERE namespace.nspname = 'public'
              AND relation.relname = generation_partition
              AND inheritance.inhparent =
                    to_regclass('public.' || instrument_partition);

            IF observed_bound IS NOT NULL THEN
                IF to_regclass(
                        'public.snapshot_generation_drop_operations')
                        IS NOT NULL
                   AND to_regclass(
                        'public.snapshot_generation_restore_operations')
                        IS NOT NULL
                   AND to_regclass(
                        'public.snapshot_generation_restore_finalizations')
                        IS NOT NULL
                THEN
                    EXECUTE
                        'WITH latest_drop AS (
                            SELECT drop_row.drop_operation_id
                            FROM public.snapshot_generation_drop_operations
                                drop_row
                            WHERE drop_row.instrument = $1
                              AND drop_row.snapshot_id = $2
                            ORDER BY
                                drop_row.dropped_at DESC,
                                drop_row.drop_operation_id DESC
                            LIMIT 1)
                        SELECT EXISTS (
                            SELECT 1
                            FROM latest_drop)
                           AND NOT EXISTS (
                            SELECT 1
                            FROM latest_drop
                            JOIN public.snapshot_generation_restore_operations
                                restore_row
                              ON restore_row.drop_operation_id =
                                    latest_drop.drop_operation_id
                            JOIN public.snapshot_generation_restore_finalizations
                                finalization
                              ON finalization.restore_operation_id =
                                    restore_row.restore_operation_id
                            WHERE restore_row.restored_child_oid = $3)'
                    INTO committed_drop_exists
                    USING
                        p_instrument,
                        p_snapshot_id,
                        observed_oid;
                    IF committed_drop_exists THEN
                        RAISE EXCEPTION
                            'snapshot generation %/% does not match a finalized logical restore',
                            p_instrument,
                            p_snapshot_id
                            USING ERRCODE = '55000';
                    END IF;
                END IF;
                IF observed_bound IS DISTINCT FROM
                        format('FOR VALUES IN (%L)', p_snapshot_id) THEN
                    RAISE EXCEPTION
                        'snapshot generation partition % has unexpected bound %',
                        generation_partition,
                        observed_bound;
                END IF;
                RETURN generation_partition;
            END IF;

            IF to_regclass('public.' || generation_partition) IS NOT NULL THEN
                RAISE EXCEPTION
                    'snapshot generation relation % exists outside expected parent',
                    generation_partition;
            END IF;

            IF to_regclass(
                    'public.snapshot_generation_drop_operations')
                    IS NOT NULL
            THEN
                EXECUTE
                    'SELECT EXISTS (
                        SELECT 1
                        FROM public.snapshot_generation_drop_operations
                        WHERE instrument = $1
                          AND snapshot_id = $2)'
                INTO committed_drop_exists
                USING p_instrument, p_snapshot_id;
                IF committed_drop_exists THEN
                    RAISE EXCEPTION
                        'snapshot generation %/% has a committed DROP tombstone',
                        p_instrument,
                        p_snapshot_id
                        USING ERRCODE = '55000';
                END IF;
            END IF;

            -- PostgreSQL chooses inherited index names in the shared schema.
            -- Serialize generation DDL across instruments so concurrent first
            -- batches cannot select the same truncated index name.
            PERFORM pg_advisory_xact_lock(
                hashtextextended(
                    'fst.snapshot-generation-partition-ddl',
                    0));

            IF to_regclass(
                    'public.snapshot_generation_retention_holds')
                    IS NOT NULL
            THEN
                EXECUTE
                    'SELECT EXISTS (
                        SELECT 1
                        FROM public.snapshot_generation_retention_holds
                        WHERE instrument = $1
                          AND snapshot_id = $2
                          AND hold_kind IN (
                                ''retention_in_flight'',
                                ''restore_in_flight'')
                          AND released_at IS NULL)'
                INTO active_hold_exists
                USING p_instrument, p_snapshot_id;
                IF active_hold_exists THEN
                    RAISE EXCEPTION
                        'snapshot generation %/% has an active retention or restore hold',
                        p_instrument,
                        p_snapshot_id
                        USING ERRCODE = '55000';
                END IF;
            END IF;

            SELECT
                pg_get_expr(
                    relation.relpartbound,
                    relation.oid,
                    TRUE),
                relation.oid::BIGINT
            INTO
                observed_bound,
                observed_oid
            FROM pg_class relation
            JOIN pg_namespace namespace
              ON namespace.oid = relation.relnamespace
            JOIN pg_inherits inheritance
              ON inheritance.inhrelid = relation.oid
            WHERE namespace.nspname = 'public'
              AND relation.relname = generation_partition
              AND inheritance.inhparent =
                    to_regclass('public.' || instrument_partition);

            IF observed_bound IS NOT NULL THEN
                IF to_regclass(
                        'public.snapshot_generation_drop_operations')
                        IS NOT NULL
                   AND to_regclass(
                        'public.snapshot_generation_restore_operations')
                        IS NOT NULL
                   AND to_regclass(
                        'public.snapshot_generation_restore_finalizations')
                        IS NOT NULL
                THEN
                    EXECUTE
                        'WITH latest_drop AS (
                            SELECT drop_row.drop_operation_id
                            FROM public.snapshot_generation_drop_operations
                                drop_row
                            WHERE drop_row.instrument = $1
                              AND drop_row.snapshot_id = $2
                            ORDER BY
                                drop_row.dropped_at DESC,
                                drop_row.drop_operation_id DESC
                            LIMIT 1)
                        SELECT EXISTS (
                            SELECT 1
                            FROM latest_drop)
                           AND NOT EXISTS (
                            SELECT 1
                            FROM latest_drop
                            JOIN public.snapshot_generation_restore_operations
                                restore_row
                              ON restore_row.drop_operation_id =
                                    latest_drop.drop_operation_id
                            JOIN public.snapshot_generation_restore_finalizations
                                finalization
                              ON finalization.restore_operation_id =
                                    restore_row.restore_operation_id
                            WHERE restore_row.restored_child_oid = $3)'
                    INTO committed_drop_exists
                    USING
                        p_instrument,
                        p_snapshot_id,
                        observed_oid;
                    IF committed_drop_exists THEN
                        RAISE EXCEPTION
                            'snapshot generation %/% does not match a finalized logical restore',
                            p_instrument,
                            p_snapshot_id
                            USING ERRCODE = '55000';
                    END IF;
                END IF;
                IF observed_bound IS DISTINCT FROM
                        format('FOR VALUES IN (%L)', p_snapshot_id) THEN
                    RAISE EXCEPTION
                        'snapshot generation partition % has unexpected bound %',
                        generation_partition,
                        observed_bound;
                END IF;
                RETURN generation_partition;
            END IF;

            IF to_regclass('public.' || generation_partition) IS NOT NULL THEN
                RAISE EXCEPTION
                    'snapshot generation relation % exists outside expected parent',
                    generation_partition;
            END IF;

            IF to_regclass(
                    'public.snapshot_generation_retention_holds')
                    IS NOT NULL
            THEN
                EXECUTE
                    'SELECT EXISTS (
                        SELECT 1
                        FROM public.snapshot_generation_retention_holds
                        WHERE instrument = $1
                          AND snapshot_id = $2
                          AND hold_kind IN (
                                ''retention_in_flight'',
                                ''restore_in_flight'')
                          AND released_at IS NULL)'
                INTO active_hold_exists
                USING p_instrument, p_snapshot_id;
                IF active_hold_exists THEN
                    RAISE EXCEPTION
                        'snapshot generation %/% has an active retention or restore hold',
                        p_instrument,
                        p_snapshot_id
                        USING ERRCODE = '55000';
                END IF;
            END IF;
            IF to_regclass(
                    'public.snapshot_generation_drop_operations')
                    IS NOT NULL
            THEN
                EXECUTE
                    'SELECT EXISTS (
                        SELECT 1
                        FROM public.snapshot_generation_drop_operations
                        WHERE instrument = $1
                          AND snapshot_id = $2)'
                INTO committed_drop_exists
                USING p_instrument, p_snapshot_id;
                IF committed_drop_exists THEN
                    RAISE EXCEPTION
                        'snapshot generation %/% has a committed DROP tombstone',
                        p_instrument,
                        p_snapshot_id
                        USING ERRCODE = '55000';
                END IF;
            END IF;

            EXECUTE format(
                'CREATE TABLE IF NOT EXISTS public.%I PARTITION OF public.%I FOR VALUES IN (%s)',
                generation_partition,
                instrument_partition,
                p_snapshot_id);

            SELECT pg_get_expr(relation.relpartbound, relation.oid, TRUE)
            INTO observed_bound
            FROM pg_class relation
            JOIN pg_namespace namespace
              ON namespace.oid = relation.relnamespace
            JOIN pg_inherits inheritance
              ON inheritance.inhrelid = relation.oid
            WHERE namespace.nspname = 'public'
              AND relation.relname = generation_partition
              AND inheritance.inhparent =
                    to_regclass('public.' || instrument_partition);

            IF observed_bound IS DISTINCT FROM
                    format('FOR VALUES IN (%L)', p_snapshot_id) THEN
                RAISE EXCEPTION
                    'snapshot generation partition % has unexpected bound %',
                    generation_partition,
                    observed_bound;
            END IF;

            RETURN generation_partition;
        END
        $snapshot_generation$;

        CREATE INDEX IF NOT EXISTS ix_les_snapshot_song_score
            ON leaderboard_entries_snapshot (snapshot_id, song_id, instrument, score DESC);

        CREATE TABLE IF NOT EXISTS leaderboard_snapshot_state (
            song_id             TEXT        NOT NULL,
            instrument          TEXT        NOT NULL,
            active_snapshot_id  BIGINT,
            scrape_id           BIGINT,
            is_finalized        BOOLEAN     NOT NULL DEFAULT FALSE,
            wave1_finalized_at  TIMESTAMPTZ,
            wave2_finalized_at  TIMESTAMPTZ,
            updated_at          TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (song_id, instrument)
        );

        CREATE TABLE IF NOT EXISTS leaderboard_scope_fingerprints (
            song_id              TEXT        NOT NULL,
            instrument           TEXT        NOT NULL,
            scope_kind           TEXT        NOT NULL DEFAULT 'alltime',
            fingerprint_version  INTEGER     NOT NULL,
            content_fingerprint  TEXT        NOT NULL,
            coverage_fingerprint TEXT        NOT NULL,
            entry_count          INTEGER     NOT NULL,
            reported_total_entries BIGINT,
            reported_total_pages INTEGER,
            is_complete          BOOLEAN     NOT NULL DEFAULT FALSE,
            min_rank             INTEGER,
            max_rank             INTEGER,
            source_scrape_id     BIGINT      NOT NULL,
            published_scrape_id  BIGINT,
            first_seen_scrape_id BIGINT      NOT NULL,
            last_changed_scrape_id BIGINT    NOT NULL,
            last_seen_scrape_id  BIGINT      NOT NULL,
            changed_at           TIMESTAMPTZ NOT NULL,
            seen_at              TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (song_id, instrument, scope_kind)
        );

        ALTER TABLE leaderboard_scope_fingerprints
            ADD COLUMN IF NOT EXISTS is_complete BOOLEAN NOT NULL DEFAULT FALSE;

        CREATE INDEX IF NOT EXISTS ix_lsf_last_changed
            ON leaderboard_scope_fingerprints (last_changed_scrape_id, instrument);

        CREATE TABLE IF NOT EXISTS leaderboard_entries_overlay (
            song_id            TEXT        NOT NULL,
            instrument         TEXT        NOT NULL,
            account_id         TEXT        NOT NULL,
            score              INTEGER     NOT NULL,
            accuracy           INTEGER,
            is_full_combo      BOOLEAN,
            stars              INTEGER,
            season             INTEGER,
            percentile         REAL,
            rank               INTEGER     DEFAULT 0,
            source             TEXT        NOT NULL DEFAULT 'overlay',
            difficulty         INTEGER     DEFAULT -1,
            api_rank           INTEGER,
            end_time           TEXT,
            band_members_json  JSONB,
            band_score         INTEGER,
            base_score         INTEGER,
            instrument_bonus   INTEGER,
            overdrive_bonus    INTEGER,
            instrument_combo   TEXT,
            first_seen_at      TIMESTAMPTZ NOT NULL,
            last_updated_at    TIMESTAMPTZ NOT NULL,
            source_priority    INTEGER     NOT NULL DEFAULT 0,
            overlay_reason     TEXT,
            PRIMARY KEY (song_id, instrument, account_id)
        ) PARTITION BY LIST (instrument);

        CREATE TABLE IF NOT EXISTS leaderboard_entries_overlay_solo_guitar    PARTITION OF leaderboard_entries_overlay FOR VALUES IN ('Solo_Guitar');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_overlay_solo_bass      PARTITION OF leaderboard_entries_overlay FOR VALUES IN ('Solo_Bass');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_overlay_solo_drums     PARTITION OF leaderboard_entries_overlay FOR VALUES IN ('Solo_Drums');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_overlay_solo_vocals    PARTITION OF leaderboard_entries_overlay FOR VALUES IN ('Solo_Vocals');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_overlay_pro_guitar     PARTITION OF leaderboard_entries_overlay FOR VALUES IN ('Solo_PeripheralGuitar');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_overlay_pro_bass       PARTITION OF leaderboard_entries_overlay FOR VALUES IN ('Solo_PeripheralBass');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_overlay_pro_vocals     PARTITION OF leaderboard_entries_overlay FOR VALUES IN ('Solo_PeripheralVocals');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_overlay_pro_cymbals    PARTITION OF leaderboard_entries_overlay FOR VALUES IN ('Solo_PeripheralCymbals');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_overlay_pro_drums      PARTITION OF leaderboard_entries_overlay FOR VALUES IN ('Solo_PeripheralDrums');

        CREATE INDEX IF NOT EXISTS ix_leo_song_priority_score
            ON leaderboard_entries_overlay (song_id, instrument, source_priority DESC, score DESC);

        -- Narrow accumulated band-context source. Snapshot pages update scalar
        -- fields for known contexts and insert newly observed band payloads, so
        -- full snapshots no longer depend on legacy live-row COALESCE behavior.
        CREATE TABLE IF NOT EXISTS leaderboard_band_context (
            song_id             TEXT        NOT NULL,
            instrument          TEXT        NOT NULL,
            account_id          TEXT        NOT NULL,
            score               INTEGER     NOT NULL,
            accuracy            INTEGER,
            is_full_combo       BOOLEAN,
            stars               INTEGER,
            season              INTEGER,
            percentile          REAL,
            source              TEXT        NOT NULL DEFAULT 'scrape',
            difficulty          INTEGER     DEFAULT -1,
            end_time            TEXT,
            band_members_json   JSONB       NOT NULL,
            band_score          INTEGER,
            base_score          INTEGER,
            instrument_bonus    INTEGER,
            overdrive_bonus     INTEGER,
            instrument_combo    TEXT,
            first_seen_at       TIMESTAMPTZ NOT NULL,
            last_updated_at     TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (song_id, instrument, account_id)
        );

        CREATE INDEX IF NOT EXISTS ix_lbc_song
            ON leaderboard_band_context (song_id);

        ALTER TABLE leaderboard_band_context
            ADD COLUMN IF NOT EXISTS percentile REAL;
        ALTER TABLE leaderboard_band_context
            ADD COLUMN IF NOT EXISTS source TEXT NOT NULL DEFAULT 'scrape';

        CREATE TABLE IF NOT EXISTS leaderboard_band_context_state (
            id                   BOOLEAN     PRIMARY KEY DEFAULT TRUE CHECK (id),
            seeded_at            TIMESTAMPTZ,
            legacy_source_rows   BIGINT      NOT NULL DEFAULT 0,
            overlay_source_rows  BIGINT      NOT NULL DEFAULT 0,
            context_rows         BIGINT      NOT NULL DEFAULT 0,
            updated_at           TIMESTAMPTZ NOT NULL
        );

        -- =====================================================================
        -- SOLO CURRENT PROJECTION (incremental snapshot + overlay read model)
        -- =====================================================================

        CREATE SEQUENCE IF NOT EXISTS solo_current_projection_generation_seq;

        CREATE TABLE IF NOT EXISTS current_leaderboard_entries (
            song_id               TEXT        NOT NULL,
            instrument            TEXT        NOT NULL,
            account_id            TEXT        NOT NULL,
            score                 INTEGER     NOT NULL,
            accuracy              INTEGER,
            is_full_combo         BOOLEAN,
            stars                 INTEGER,
            season                INTEGER,
            percentile            REAL,
            rank                  INTEGER     NOT NULL DEFAULT 0,
            api_rank              INTEGER,
            source                TEXT        NOT NULL DEFAULT 'projection',
            difficulty            INTEGER     DEFAULT -1,
            end_time              TEXT,
            first_seen_at         TIMESTAMPTZ NOT NULL,
            last_updated_at       TIMESTAMPTZ NOT NULL,
            projection_generation BIGINT      NOT NULL DEFAULT 0,
            computed_at           TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (song_id, instrument, account_id)
        ) PARTITION BY LIST (instrument);

        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_solo_guitar    PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_Guitar');
        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_solo_bass      PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_Bass');
        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_solo_drums     PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_Drums');
        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_solo_vocals    PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_Vocals');
        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_pro_guitar     PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_PeripheralGuitar');
        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_pro_bass       PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_PeripheralBass');
        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_pro_vocals     PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_PeripheralVocals');
        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_pro_cymbals    PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_PeripheralCymbals');
        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_pro_drums      PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_PeripheralDrums');

        CREATE INDEX IF NOT EXISTS ix_cle_account_instrument_song
            ON current_leaderboard_entries (account_id, instrument, song_id);

        CREATE INDEX IF NOT EXISTS ix_cle_song_rank
            ON current_leaderboard_entries (song_id, instrument, rank);

        CREATE INDEX IF NOT EXISTS ix_cle_song_score
            ON current_leaderboard_entries (song_id, instrument, score DESC);

        CREATE TABLE IF NOT EXISTS solo_current_projection_state (
            id                    BOOLEAN     PRIMARY KEY DEFAULT TRUE CHECK (id),
            current_generation    BIGINT      NOT NULL DEFAULT 0,
            row_count             BIGINT      NOT NULL DEFAULT 0,
            scope_count           BIGINT      NOT NULL DEFAULT 0,
            failed_scope_count    BIGINT      NOT NULL DEFAULT 0,
            full_rebuilt_at       TIMESTAMPTZ,
            last_scope_rebuilt_at TIMESTAMPTZ,
            updated_at            TIMESTAMPTZ NOT NULL
        );

        CREATE TABLE IF NOT EXISTS solo_current_projection_scope (
            song_id               TEXT        NOT NULL,
            instrument            TEXT        NOT NULL,
            projection_generation BIGINT      NOT NULL DEFAULT 0,
            row_count             BIGINT      NOT NULL DEFAULT 0,
            source_snapshot_id    BIGINT,
            source_kind           TEXT        NOT NULL DEFAULT 'legacy-compatible',
            status                TEXT        NOT NULL DEFAULT 'ready',
            error_message         TEXT,
            last_rebuilt_at       TIMESTAMPTZ,
            updated_at            TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (song_id, instrument)
        );

        ALTER TABLE solo_current_projection_scope
            ADD COLUMN IF NOT EXISTS source_kind TEXT NOT NULL DEFAULT 'legacy-compatible';
        UPDATE solo_current_projection_scope
        SET source_kind = 'snapshot'
        WHERE source_kind = 'legacy-compatible'
          AND source_snapshot_id IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_scps_status_updated
            ON solo_current_projection_scope (status, updated_at DESC);

        -- =====================================================================
        -- SONG STATS (partitioned by instrument)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS song_stats (
            song_id              TEXT    NOT NULL,
            instrument           TEXT    NOT NULL,
            entry_count          INTEGER NOT NULL,
            previous_entry_count INTEGER NOT NULL DEFAULT 0,
            log_weight           REAL    NOT NULL,
            max_score            INTEGER,
            computed_at          TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (song_id, instrument)
        ) PARTITION BY LIST (instrument);

        CREATE TABLE IF NOT EXISTS song_stats_solo_guitar    PARTITION OF song_stats FOR VALUES IN ('Solo_Guitar');
        CREATE TABLE IF NOT EXISTS song_stats_solo_bass      PARTITION OF song_stats FOR VALUES IN ('Solo_Bass');
        CREATE TABLE IF NOT EXISTS song_stats_solo_drums     PARTITION OF song_stats FOR VALUES IN ('Solo_Drums');
        CREATE TABLE IF NOT EXISTS song_stats_solo_vocals    PARTITION OF song_stats FOR VALUES IN ('Solo_Vocals');
        CREATE TABLE IF NOT EXISTS song_stats_pro_guitar     PARTITION OF song_stats FOR VALUES IN ('Solo_PeripheralGuitar');
        CREATE TABLE IF NOT EXISTS song_stats_pro_bass       PARTITION OF song_stats FOR VALUES IN ('Solo_PeripheralBass');
        CREATE TABLE IF NOT EXISTS song_stats_pro_vocals     PARTITION OF song_stats FOR VALUES IN ('Solo_PeripheralVocals');
        CREATE TABLE IF NOT EXISTS song_stats_pro_cymbals    PARTITION OF song_stats FOR VALUES IN ('Solo_PeripheralCymbals');
        CREATE TABLE IF NOT EXISTS song_stats_pro_drums      PARTITION OF song_stats FOR VALUES IN ('Solo_PeripheralDrums');

        -- =====================================================================
        -- ACCOUNT RANKINGS (partitioned by instrument)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS account_rankings (
            account_id              TEXT    NOT NULL,
            instrument              TEXT    NOT NULL,
            songs_played            INTEGER NOT NULL,
            total_charted_songs     INTEGER NOT NULL,
            coverage                REAL    NOT NULL,
            raw_skill_rating        REAL    NOT NULL,
            adjusted_skill_rating   REAL    NOT NULL,
            adjusted_skill_rank     INTEGER NOT NULL,
            weighted_rating         REAL    NOT NULL,
            weighted_rank           INTEGER NOT NULL,
            fc_rate                 REAL    NOT NULL,
            fc_rate_rank            INTEGER NOT NULL,
            total_score             INTEGER NOT NULL,
            total_score_rank        INTEGER NOT NULL,
            max_score_percent       REAL    NOT NULL,
            max_score_percent_rank  INTEGER NOT NULL,
            avg_accuracy            REAL    NOT NULL,
            full_combo_count        INTEGER NOT NULL,
            avg_stars               REAL    NOT NULL,
            best_rank               INTEGER NOT NULL,
            avg_rank                REAL    NOT NULL,
            computed_at             TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (account_id, instrument)
        ) PARTITION BY LIST (instrument);

        CREATE TABLE IF NOT EXISTS account_rankings_solo_guitar    PARTITION OF account_rankings FOR VALUES IN ('Solo_Guitar');
        CREATE TABLE IF NOT EXISTS account_rankings_solo_bass      PARTITION OF account_rankings FOR VALUES IN ('Solo_Bass');
        CREATE TABLE IF NOT EXISTS account_rankings_solo_drums     PARTITION OF account_rankings FOR VALUES IN ('Solo_Drums');
        CREATE TABLE IF NOT EXISTS account_rankings_solo_vocals    PARTITION OF account_rankings FOR VALUES IN ('Solo_Vocals');
        CREATE TABLE IF NOT EXISTS account_rankings_pro_guitar     PARTITION OF account_rankings FOR VALUES IN ('Solo_PeripheralGuitar');
        CREATE TABLE IF NOT EXISTS account_rankings_pro_bass       PARTITION OF account_rankings FOR VALUES IN ('Solo_PeripheralBass');
        CREATE TABLE IF NOT EXISTS account_rankings_pro_vocals     PARTITION OF account_rankings FOR VALUES IN ('Solo_PeripheralVocals');
        CREATE TABLE IF NOT EXISTS account_rankings_pro_cymbals    PARTITION OF account_rankings FOR VALUES IN ('Solo_PeripheralCymbals');
        CREATE TABLE IF NOT EXISTS account_rankings_pro_drums      PARTITION OF account_rankings FOR VALUES IN ('Solo_PeripheralDrums');

        CREATE UNIQUE INDEX IF NOT EXISTS ix_ar_skill
            ON account_rankings (instrument, adjusted_skill_rank);
        CREATE UNIQUE INDEX IF NOT EXISTS ix_ar_weighted
            ON account_rankings (instrument, weighted_rank);
        CREATE UNIQUE INDEX IF NOT EXISTS ix_ar_fc_rate
            ON account_rankings (instrument, fc_rate_rank);
        CREATE UNIQUE INDEX IF NOT EXISTS ix_ar_total_score
            ON account_rankings (instrument, total_score_rank);
        CREATE UNIQUE INDEX IF NOT EXISTS ix_ar_max_score_pct
            ON account_rankings (instrument, max_score_percent_rank);

        CREATE TABLE IF NOT EXISTS account_ranking_stats (
            instrument           TEXT        PRIMARY KEY,
            ranked_account_count INTEGER     NOT NULL,
            computed_at          TIMESTAMPTZ NOT NULL
        );

        -- =====================================================================
        -- RANK HISTORY (partitioned by instrument)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS rank_history (
            account_id              TEXT        NOT NULL,
            instrument              TEXT        NOT NULL,
            snapshot_date           DATE        NOT NULL,
            snapshot_taken_at       TIMESTAMPTZ,
            adjusted_skill_rank     INTEGER     NOT NULL,
            weighted_rank           INTEGER     NOT NULL,
            fc_rate_rank            INTEGER     NOT NULL,
            total_score_rank        INTEGER     NOT NULL,
            max_score_percent_rank  INTEGER     NOT NULL,
            adjusted_skill_rating   REAL,
            weighted_rating         REAL,
            fc_rate                 REAL,
            total_score             INTEGER,
            max_score_percent       REAL,
            songs_played            INTEGER,
            coverage                REAL,
            full_combo_count        INTEGER,
            PRIMARY KEY (account_id, instrument, snapshot_date)
        ) PARTITION BY LIST (instrument);

        CREATE TABLE IF NOT EXISTS rank_history_solo_guitar    PARTITION OF rank_history FOR VALUES IN ('Solo_Guitar');
        CREATE TABLE IF NOT EXISTS rank_history_solo_bass      PARTITION OF rank_history FOR VALUES IN ('Solo_Bass');
        CREATE TABLE IF NOT EXISTS rank_history_solo_drums     PARTITION OF rank_history FOR VALUES IN ('Solo_Drums');
        CREATE TABLE IF NOT EXISTS rank_history_solo_vocals    PARTITION OF rank_history FOR VALUES IN ('Solo_Vocals');
        CREATE TABLE IF NOT EXISTS rank_history_pro_guitar     PARTITION OF rank_history FOR VALUES IN ('Solo_PeripheralGuitar');
        CREATE TABLE IF NOT EXISTS rank_history_pro_bass       PARTITION OF rank_history FOR VALUES IN ('Solo_PeripheralBass');
        CREATE TABLE IF NOT EXISTS rank_history_pro_vocals     PARTITION OF rank_history FOR VALUES IN ('Solo_PeripheralVocals');
        CREATE TABLE IF NOT EXISTS rank_history_pro_cymbals    PARTITION OF rank_history FOR VALUES IN ('Solo_PeripheralCymbals');
        CREATE TABLE IF NOT EXISTS rank_history_pro_drums      PARTITION OF rank_history FOR VALUES IN ('Solo_PeripheralDrums');

        CREATE TABLE IF NOT EXISTS rank_history_snapshot_stats (
            instrument              TEXT        NOT NULL,
            snapshot_date           DATE        NOT NULL,
            snapshot_taken_at       TIMESTAMPTZ,
            total_charted_songs     INTEGER     NOT NULL,
            ranked_account_count    INTEGER,
            PRIMARY KEY (instrument, snapshot_date)
        );

        -- =====================================================================
        -- VALID SCORE OVERRIDES (partitioned by instrument)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS valid_score_overrides (
            song_id      TEXT    NOT NULL,
            instrument   TEXT    NOT NULL,
            account_id   TEXT    NOT NULL,
            score        INTEGER NOT NULL,
            accuracy     INTEGER,
            is_full_combo BOOLEAN,
            stars        INTEGER,
            PRIMARY KEY (song_id, instrument, account_id)
        ) PARTITION BY LIST (instrument);

        CREATE TABLE IF NOT EXISTS valid_score_overrides_solo_guitar    PARTITION OF valid_score_overrides FOR VALUES IN ('Solo_Guitar');
        CREATE TABLE IF NOT EXISTS valid_score_overrides_solo_bass      PARTITION OF valid_score_overrides FOR VALUES IN ('Solo_Bass');
        CREATE TABLE IF NOT EXISTS valid_score_overrides_solo_drums     PARTITION OF valid_score_overrides FOR VALUES IN ('Solo_Drums');
        CREATE TABLE IF NOT EXISTS valid_score_overrides_solo_vocals    PARTITION OF valid_score_overrides FOR VALUES IN ('Solo_Vocals');
        CREATE TABLE IF NOT EXISTS valid_score_overrides_pro_guitar     PARTITION OF valid_score_overrides FOR VALUES IN ('Solo_PeripheralGuitar');
        CREATE TABLE IF NOT EXISTS valid_score_overrides_pro_bass       PARTITION OF valid_score_overrides FOR VALUES IN ('Solo_PeripheralBass');
        CREATE TABLE IF NOT EXISTS valid_score_overrides_pro_vocals     PARTITION OF valid_score_overrides FOR VALUES IN ('Solo_PeripheralVocals');
        CREATE TABLE IF NOT EXISTS valid_score_overrides_pro_cymbals    PARTITION OF valid_score_overrides FOR VALUES IN ('Solo_PeripheralCymbals');
        CREATE TABLE IF NOT EXISTS valid_score_overrides_pro_drums      PARTITION OF valid_score_overrides FOR VALUES IN ('Solo_PeripheralDrums');

        -- =====================================================================
        -- SCRAPE LOG (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS scrape_log (
            id              SERIAL      PRIMARY KEY,
            started_at      TIMESTAMPTZ NOT NULL,
            completed_at    TIMESTAMPTZ,
            songs_scraped   INTEGER,
            total_entries   INTEGER,
            total_requests  INTEGER,
            total_bytes     BIGINT,
            epic_reported_over_100_pages BOOLEAN NOT NULL DEFAULT FALSE
        );

        ALTER TABLE scrape_log ADD COLUMN IF NOT EXISTS epic_reported_over_100_pages BOOLEAN NOT NULL DEFAULT FALSE;
        ALTER TABLE scrape_log ADD COLUMN IF NOT EXISTS status TEXT;
        ALTER TABLE scrape_log ADD COLUMN IF NOT EXISTS failed_at TIMESTAMPTZ;
        ALTER TABLE scrape_log ADD COLUMN IF NOT EXISTS failure_phase TEXT;
        ALTER TABLE scrape_log ADD COLUMN IF NOT EXISTS failure_message TEXT;
        ALTER TABLE scrape_log ADD COLUMN IF NOT EXISTS best_effort_failure_count INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE scrape_log ADD COLUMN IF NOT EXISTS best_effort_failed_phases TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[];
        UPDATE scrape_log
        SET status = CASE
            WHEN completed_at IS NOT NULL THEN 'completed'
            WHEN failed_at IS NOT NULL THEN 'failed'
            ELSE 'running'
        END
        WHERE status IS NULL;
        ALTER TABLE scrape_log ALTER COLUMN status SET DEFAULT 'running';
        ALTER TABLE scrape_log ALTER COLUMN status SET NOT NULL;

        CREATE TABLE IF NOT EXISTS leaderboard_scope_manifests (
            scrape_id               BIGINT      NOT NULL REFERENCES scrape_log(id) ON DELETE CASCADE,
            song_id                 TEXT        NOT NULL,
            instrument              TEXT        NOT NULL,
            scope_kind              TEXT        NOT NULL DEFAULT 'alltime',
            expected_first_page     INTEGER     NOT NULL,
            expected_last_page      INTEGER     NOT NULL,
            received_pages          INTEGER[]   NOT NULL,
            page_statuses           JSONB       NOT NULL,
            terminal_boundary       TEXT        NOT NULL,
            terminal_boundary_page  INTEGER,
            parse_status            TEXT        NOT NULL,
            retry_exhausted         BOOLEAN     NOT NULL,
            reported_total_entries  BIGINT      NOT NULL,
            reported_total_pages    INTEGER     NOT NULL,
            deep_start_page         INTEGER,
            deep_end_page           INTEGER,
            content_fingerprint     TEXT        NOT NULL,
            coverage_fingerprint    TEXT        NOT NULL,
            is_complete             BOOLEAN     NOT NULL,
            failure_reason          TEXT,
            created_at              TIMESTAMPTZ NOT NULL,
            updated_at              TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (scrape_id, song_id, instrument, scope_kind)
        );

        CREATE INDEX IF NOT EXISTS ix_lsm_incomplete
            ON leaderboard_scope_manifests (scrape_id, instrument, song_id)
            WHERE NOT is_complete;

        CREATE INDEX IF NOT EXISTS ix_scrapelog_completed
            ON scrape_log (id DESC) WHERE completed_at IS NOT NULL;

        CREATE TABLE IF NOT EXISTS scrape_writer_failures (
            id                BIGSERIAL   PRIMARY KEY,
            scrape_id         BIGINT      NOT NULL REFERENCES scrape_log(id) ON DELETE CASCADE,
            writer_kind       TEXT        NOT NULL,
            instrument        TEXT        NOT NULL,
            song_id           TEXT        NOT NULL,
            page_count        INTEGER     NOT NULL,
            row_count         BIGINT      NOT NULL,
            artifact_path     TEXT,
            exception_type    TEXT        NOT NULL,
            error_message     TEXT        NOT NULL,
            occurred_at       TIMESTAMPTZ NOT NULL,
            replayed_at       TIMESTAMPTZ
        );

        CREATE INDEX IF NOT EXISTS ix_swf_scrape
            ON scrape_writer_failures (scrape_id, writer_kind, instrument, song_id);

        CREATE TABLE IF NOT EXISTS scrape_phase_outcomes (
            scrape_id          BIGINT      NOT NULL REFERENCES scrape_log(id) ON DELETE CASCADE,
            phase              TEXT        NOT NULL,
            criticality        TEXT        NOT NULL,
            status             TEXT        NOT NULL,
            started_at         TIMESTAMPTZ NOT NULL,
            completed_at       TIMESTAMPTZ NOT NULL,
            duration_ms        BIGINT      NOT NULL,
            error_message      TEXT,
            PRIMARY KEY (scrape_id, phase)
        );

        CREATE INDEX IF NOT EXISTS ix_spo_failures
            ON scrape_phase_outcomes (scrape_id, criticality, phase)
            WHERE status = 'failed';

        CREATE TABLE IF NOT EXISTS scrape_phase_timings (
            id            BIGSERIAL   PRIMARY KEY,
            scrape_id     BIGINT      NOT NULL,
            phase         TEXT        NOT NULL,
            subphase      TEXT,
            item_key      TEXT,
            started_at    TIMESTAMPTZ NOT NULL,
            completed_at  TIMESTAMPTZ NOT NULL,
            duration_ms   BIGINT      NOT NULL,
            rows_read     BIGINT,
            rows_written  BIGINT,
            rows_deleted  BIGINT,
            scope_count   BIGINT,
            success       BOOLEAN     NOT NULL DEFAULT TRUE,
            error_message TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_scrape_phase_timings_scrape
            ON scrape_phase_timings
               (scrape_id, phase, subphase, item_key);

        CREATE INDEX IF NOT EXISTS ix_scrape_phase_timings_started
            ON scrape_phase_timings (started_at DESC);

        CREATE TABLE IF NOT EXISTS scrape_phase_attempts (
            scrape_id             BIGINT           NOT NULL,
            phase_id              TEXT             NOT NULL,
            attempt               INTEGER          NOT NULL,
            operation_id          TEXT             NOT NULL,
            phase_ordinal         INTEGER          NOT NULL,
            plan_version          TEXT             NOT NULL,
            worker_instance_id    TEXT             NOT NULL,
            current_subphase_id   TEXT,
            status                TEXT             NOT NULL,
            units_kind            TEXT,
            units_completed       BIGINT,
            units_total           BIGINT,
            units_total_final     BOOLEAN          NOT NULL DEFAULT FALSE,
            phase_percent         DOUBLE PRECISION,
            overall_percent_kind  TEXT             NOT NULL DEFAULT 'indeterminate',
            overall_percent       DOUBLE PRECISION,
            overall_model_version TEXT,
            eta_lower_seconds     DOUBLE PRECISION,
            eta_upper_seconds     DOUBLE PRECISION,
            eta_confidence        TEXT,
            eta_sample_count      INTEGER,
            current_subphase_epoch INTEGER         NOT NULL DEFAULT 0,
            subphase_sequence      BIGINT          NOT NULL DEFAULT 0,
            subphase_progress_kind TEXT            NOT NULL DEFAULT 'indeterminate',
            subphase_units_kind    TEXT,
            subphase_units_completed BIGINT,
            subphase_units_total   BIGINT,
            subphase_units_total_final BOOLEAN     NOT NULL DEFAULT FALSE,
            subphase_percent       DOUBLE PRECISION,
            subphase_started_at    TIMESTAMPTZ,
            subphase_last_progress_at TIMESTAMPTZ,
            started_at            TIMESTAMPTZ      NOT NULL,
            last_progress_at      TIMESTAMPTZ      NOT NULL,
            heartbeat_at          TIMESTAMPTZ      NOT NULL,
            completed_at          TIMESTAMPTZ,
            build_id              TEXT,
            config_id             TEXT,
            warning_message       TEXT,
            error_message         TEXT,
            PRIMARY KEY (scrape_id, phase_id, attempt),
            CHECK (attempt > 0),
            CHECK (phase_ordinal >= 0),
            CHECK (status IN (
                'running', 'completed', 'failed', 'cancelled',
                'interrupted', 'skipped', 'deferred')),
            CHECK (units_completed IS NULL OR units_completed >= 0),
            CHECK (units_total IS NULL OR units_total >= 0),
            CHECK (NOT units_total_final OR units_total IS NOT NULL),
            CHECK (
                NOT units_total_final
                OR units_completed IS NULL
                OR units_completed <= units_total),
            CHECK (phase_percent IS NULL OR (
                units_total_final
                AND phase_percent >= 0
                AND phase_percent <= 100)),
            CHECK (overall_percent IS NULL OR (
                overall_percent >= 0
                AND overall_percent <= 100)),
            CHECK (eta_lower_seconds IS NULL OR eta_lower_seconds >= 0),
            CHECK (eta_upper_seconds IS NULL OR eta_upper_seconds >= 0),
            CHECK (
                eta_lower_seconds IS NULL
                OR eta_upper_seconds IS NULL
                OR eta_upper_seconds >= eta_lower_seconds),
            CHECK (eta_sample_count IS NULL OR eta_sample_count >= 0),
            CHECK (current_subphase_epoch >= 0),
            CHECK (subphase_sequence >= 0),
            CHECK (subphase_progress_kind IN (
                'exact', 'indeterminate', 'not_applicable')),
            CHECK (
                subphase_units_completed IS NULL
                OR subphase_units_completed >= 0),
            CHECK (
                subphase_units_total IS NULL
                OR subphase_units_total >= 0),
            CHECK (
                NOT subphase_units_total_final
                OR subphase_units_total IS NOT NULL),
            CHECK (
                NOT subphase_units_total_final
                OR subphase_units_completed IS NULL
                OR subphase_units_completed <= subphase_units_total),
            CHECK (
                subphase_percent IS NULL
                OR (
                    subphase_progress_kind = 'exact'
                    AND subphase_units_total_final
                    AND subphase_percent >= 0
                    AND subphase_percent <= 100)),
            CHECK (
                subphase_progress_kind <> 'exact'
                OR subphase_units_total_final),
            CHECK (last_progress_at >= started_at),
            CHECK (heartbeat_at >= started_at),
            CHECK (completed_at IS NULL OR completed_at >= started_at),
            CHECK (
                (status = 'running' AND completed_at IS NULL)
                OR (status <> 'running' AND completed_at IS NOT NULL))
        );

        ALTER TABLE scrape_phase_attempts
            ADD COLUMN IF NOT EXISTS current_subphase_epoch INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE scrape_phase_attempts
            ADD COLUMN IF NOT EXISTS subphase_sequence BIGINT NOT NULL DEFAULT 0;
        ALTER TABLE scrape_phase_attempts
            ADD COLUMN IF NOT EXISTS subphase_progress_kind TEXT NOT NULL DEFAULT 'indeterminate';
        ALTER TABLE scrape_phase_attempts
            ADD COLUMN IF NOT EXISTS subphase_units_kind TEXT;
        ALTER TABLE scrape_phase_attempts
            ADD COLUMN IF NOT EXISTS subphase_units_completed BIGINT;
        ALTER TABLE scrape_phase_attempts
            ADD COLUMN IF NOT EXISTS subphase_units_total BIGINT;
        ALTER TABLE scrape_phase_attempts
            ADD COLUMN IF NOT EXISTS subphase_units_total_final BOOLEAN NOT NULL DEFAULT FALSE;
        ALTER TABLE scrape_phase_attempts
            ADD COLUMN IF NOT EXISTS subphase_percent DOUBLE PRECISION;
        ALTER TABLE scrape_phase_attempts
            ADD COLUMN IF NOT EXISTS subphase_started_at TIMESTAMPTZ;
        ALTER TABLE scrape_phase_attempts
            ADD COLUMN IF NOT EXISTS subphase_last_progress_at TIMESTAMPTZ;

        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conname = 'ck_scrape_phase_attempts_subphase_progress'
                  AND conrelid = 'scrape_phase_attempts'::regclass
            ) THEN
                ALTER TABLE scrape_phase_attempts
                    ADD CONSTRAINT ck_scrape_phase_attempts_subphase_progress
                    CHECK (
                        current_subphase_epoch >= 0
                        AND subphase_sequence >= 0
                        AND subphase_progress_kind IN (
                            'exact', 'indeterminate', 'not_applicable')
                        AND (
                            subphase_units_completed IS NULL
                            OR subphase_units_completed >= 0)
                        AND (
                            subphase_units_total IS NULL
                            OR subphase_units_total >= 0)
                        AND (
                            NOT subphase_units_total_final
                            OR subphase_units_total IS NOT NULL)
                        AND (
                            NOT subphase_units_total_final
                            OR subphase_units_completed IS NULL
                            OR subphase_units_completed <= subphase_units_total)
                        AND (
                            subphase_percent IS NULL
                            OR (
                                subphase_progress_kind = 'exact'
                                AND subphase_units_total_final
                                AND subphase_percent >= 0
                                AND subphase_percent <= 100))
                        AND (
                            subphase_progress_kind <> 'exact'
                            OR subphase_units_total_final))
                    NOT VALID;
            END IF;
        END $$;

        ALTER TABLE scrape_phase_attempts
            VALIDATE CONSTRAINT ck_scrape_phase_attempts_subphase_progress;

        CREATE INDEX IF NOT EXISTS ix_scrape_phase_attempts_watchdog
            ON scrape_phase_attempts
               (scrape_id, last_progress_at DESC,
                phase_ordinal DESC, attempt DESC)
            WHERE status = 'running';

        CREATE INDEX IF NOT EXISTS ix_scrape_phase_attempts_instance
            ON scrape_phase_attempts
               (worker_instance_id, scrape_id)
            WHERE status = 'running';

        CREATE INDEX IF NOT EXISTS ix_scrape_phase_attempts_history
            ON scrape_phase_attempts
               (phase_id, plan_version, config_id, completed_at DESC)
            WHERE status = 'completed';

        CREATE TABLE IF NOT EXISTS scrape_publication_state (
            id                  BOOLEAN     PRIMARY KEY DEFAULT TRUE CHECK (id),
            published_scrape_id INTEGER     REFERENCES scrape_log(id),
            published_at        TIMESTAMPTZ,
            public_reads_frozen BOOLEAN     NOT NULL DEFAULT FALSE,
            public_reads_frozen_at TIMESTAMPTZ,
            public_reads_frozen_scrape_id INTEGER REFERENCES scrape_log(id),
            public_reads_frozen_reason TEXT,
            publication_commit_intent_started_at TIMESTAMPTZ,
            publication_commit_intent_heartbeat_at TIMESTAMPTZ,
            publication_commit_intent_owner TEXT,
            band_projection_generation BIGINT,
            max_score_mutation_gate_token TEXT,
            max_score_mutation_gate_publication_id BIGINT,
            max_score_mutation_gate_backend_pid INTEGER,
            max_score_mutation_gate_backend_start TIMESTAMPTZ,
            max_score_mutation_gate_acquired_at TIMESTAMPTZ,
            updated_at          TIMESTAMPTZ NOT NULL
        );

        ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS public_reads_frozen BOOLEAN NOT NULL DEFAULT FALSE;
        ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS public_reads_frozen_at TIMESTAMPTZ;
        ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS public_reads_frozen_scrape_id INTEGER REFERENCES scrape_log(id);
        ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS public_reads_frozen_reason TEXT;
        ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS publication_commit_intent_started_at TIMESTAMPTZ;
        ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS publication_commit_intent_heartbeat_at TIMESTAMPTZ;
        ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS publication_commit_intent_owner TEXT;
        ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS band_projection_generation BIGINT;
        ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS max_score_mutation_gate_token TEXT;
        ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS max_score_mutation_gate_publication_id BIGINT;
        ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS max_score_mutation_gate_backend_pid INTEGER;
        ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS max_score_mutation_gate_backend_start TIMESTAMPTZ;
        ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS max_score_mutation_gate_acquired_at TIMESTAMPTZ;

        CREATE TABLE IF NOT EXISTS leaderboard_published_scope_source (
            published_scrape_id    BIGINT      NOT NULL REFERENCES scrape_log(id) ON DELETE CASCADE,
            song_id                TEXT        NOT NULL,
            instrument             TEXT        NOT NULL,
            scope_kind             TEXT        NOT NULL DEFAULT 'alltime',
            source_kind            TEXT        NOT NULL,
            source_snapshot_id     BIGINT,
            source_scrape_id       BIGINT      NOT NULL,
            row_count              BIGINT      NOT NULL,
            content_fingerprint    TEXT        NOT NULL,
            coverage_fingerprint   TEXT        NOT NULL,
            reported_total_entries BIGINT      NOT NULL,
            reported_total_pages   INTEGER     NOT NULL,
            is_complete            BOOLEAN     NOT NULL,
            created_at             TIMESTAMPTZ NOT NULL,
            validated_at           TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (published_scrape_id, instrument, song_id, scope_kind),
            CHECK (source_kind IN ('snapshot', 'empty')),
            CHECK (source_scrape_id > 0 AND source_scrape_id <= published_scrape_id),
            CHECK (row_count >= 0),
            CHECK (reported_total_entries >= row_count),
            CHECK (reported_total_pages >= 0),
            CHECK (
                (source_kind = 'snapshot' AND source_snapshot_id IS NOT NULL
                    AND source_snapshot_id = source_scrape_id AND row_count > 0
                    AND reported_total_pages > 0)
                OR
                (source_kind = 'empty' AND source_snapshot_id IS NULL
                    AND row_count = 0 AND reported_total_entries = 0
                    AND reported_total_pages = 0)
            )
        );

        CREATE TABLE IF NOT EXISTS service_worker_status (
            worker_key             TEXT        PRIMARY KEY,
            status                 TEXT        NOT NULL,
            mode                   TEXT,
            instance_id            TEXT,
            started_at             TIMESTAMPTZ,
            last_heartbeat_at      TIMESTAMPTZ,
            last_status_change_at  TIMESTAMPTZ NOT NULL,
            message                TEXT,
            current_operation_json JSONB,
            last_operation_json    JSONB,
            updated_at             TIMESTAMPTZ NOT NULL
        );

        ALTER TABLE service_worker_status ADD COLUMN IF NOT EXISTS mode TEXT;
        ALTER TABLE service_worker_status ADD COLUMN IF NOT EXISTS instance_id TEXT;
        ALTER TABLE service_worker_status ADD COLUMN IF NOT EXISTS started_at TIMESTAMPTZ;
        ALTER TABLE service_worker_status ADD COLUMN IF NOT EXISTS last_heartbeat_at TIMESTAMPTZ;
        ALTER TABLE service_worker_status ADD COLUMN IF NOT EXISTS last_status_change_at TIMESTAMPTZ NOT NULL DEFAULT now();
        ALTER TABLE service_worker_status ADD COLUMN IF NOT EXISTS message TEXT;
        ALTER TABLE service_worker_status ADD COLUMN IF NOT EXISTS current_operation_json JSONB;
        ALTER TABLE service_worker_status ADD COLUMN IF NOT EXISTS last_operation_json JSONB;
        ALTER TABLE service_worker_status ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT now();

        INSERT INTO scrape_publication_state (id, published_scrape_id, published_at, updated_at)
        SELECT TRUE, latest.id, latest.completed_at, now()
        FROM (
            SELECT id, completed_at
            FROM scrape_log
            WHERE completed_at IS NOT NULL
              AND status = 'completed'
            ORDER BY id DESC
            LIMIT 1
        ) latest
        WHERE NOT EXISTS (SELECT 1 FROM scrape_publication_state WHERE id = TRUE);

        INSERT INTO scrape_publication_state (
            id,
            updated_at)
        VALUES (
            TRUE,
            now())
        ON CONFLICT (id) DO NOTHING;

        -- =====================================================================
        -- SCORE HISTORY (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS score_history (
            id               SERIAL      PRIMARY KEY,
            song_id          TEXT        NOT NULL,
            instrument       TEXT        NOT NULL,
            account_id       TEXT        NOT NULL,
            old_score        INTEGER,
            new_score        INTEGER,
            old_rank         INTEGER,
            new_rank         INTEGER,
            accuracy         INTEGER,
            is_full_combo    BOOLEAN,
            stars            INTEGER,
            percentile       REAL,
            season           INTEGER,
            score_achieved_at TIMESTAMPTZ,
            season_rank      INTEGER,
            all_time_rank    INTEGER,
            difficulty       INTEGER,
            changed_at       TIMESTAMPTZ NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_sh_account
            ON score_history (account_id);
        CREATE INDEX IF NOT EXISTS ix_sh_song
            ON score_history (song_id, instrument);
        CREATE INDEX IF NOT EXISTS ix_sh_valid_lookup
            ON score_history (account_id, song_id, instrument, new_score DESC)
            INCLUDE (accuracy, is_full_combo, stars);
        CREATE UNIQUE INDEX IF NOT EXISTS ix_sh_dedup
            ON score_history (account_id, song_id, instrument, new_score, score_achieved_at)
            NULLS NOT DISTINCT;

        -- =====================================================================
        -- ACCOUNT NAMES (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS account_names (
            account_id    TEXT PRIMARY KEY,
            display_name  TEXT,
            last_resolved TIMESTAMPTZ
        );

        CREATE INDEX IF NOT EXISTS ix_an_unresolved
            ON account_names (last_resolved) WHERE last_resolved IS NULL;
        -- 2026-04-23 (Phase 2): replaced `ix_an_name ON account_names (display_name)`
        -- with an expression index on LOWER(display_name). The only query that
        -- hit this column was `GetAccountIdForUsername` using
        -- `WHERE LOWER(display_name) = LOWER(@username)`, which the raw btree
        -- could never satisfy, so the old index had idx_scan=0 forever despite
        -- being 458 MB. The expression form matches the query.
        CREATE INDEX IF NOT EXISTS ix_an_name_lower
            ON account_names (LOWER(display_name)) WHERE display_name IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_an_name_lower_trgm
            ON account_names USING GIN (LOWER(display_name) gin_trgm_ops)
            WHERE display_name IS NOT NULL;

        -- =====================================================================
        -- REGISTERED USERS (from fst-meta.db — kept for backfill/rivals)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS registered_users (
            device_id     TEXT        NOT NULL,
            account_id    TEXT        NOT NULL,
            display_name  TEXT,
            platform      TEXT,
            last_login_at TIMESTAMPTZ,
            registered_at TIMESTAMPTZ NOT NULL,
            last_sync_at  TIMESTAMPTZ,
            last_activity_at TIMESTAMPTZ,
            PRIMARY KEY (device_id, account_id)
        );

        CREATE INDEX IF NOT EXISTS ix_reg_account
            ON registered_users (account_id);

        ALTER TABLE registered_users
            ADD COLUMN IF NOT EXISTS last_activity_at TIMESTAMPTZ;

        UPDATE registered_users
        SET last_activity_at = COALESCE(last_activity_at, last_sync_at, last_login_at, registered_at)
        WHERE last_activity_at IS NULL;

        CREATE TABLE IF NOT EXISTS registered_user_refresh_scope_progress (
            song_id     TEXT        NOT NULL,
            instrument  TEXT        NOT NULL,
            status      TEXT        NOT NULL DEFAULT 'complete',
            checked_at  TIMESTAMPTZ NOT NULL,
            scrape_id   BIGINT,
            provenance  TEXT        NOT NULL DEFAULT 'scrape',
            PRIMARY KEY (song_id, instrument),
            CONSTRAINT ck_registered_user_refresh_scope_status
                CHECK (status IN ('complete')),
            CONSTRAINT ck_registered_user_refresh_scope_provenance_v2
                CHECK (
                    (provenance = 'scrape'
                        AND scrape_id IS NOT NULL
                        AND scrape_id > 0)
                    OR (provenance = 'phase_only' AND scrape_id IS NULL))
        );

        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM pg_attribute
                WHERE attrelid =
                    'registered_user_refresh_scope_progress'::regclass
                  AND attname = 'scrape_id'
                  AND attnotnull
            ) THEN
                ALTER TABLE registered_user_refresh_scope_progress
                    ALTER COLUMN scrape_id DROP NOT NULL;
            END IF;
        END
        $$;

        ALTER TABLE registered_user_refresh_scope_progress
            ADD COLUMN IF NOT EXISTS provenance TEXT NOT NULL DEFAULT 'scrape';

        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conname = 'ck_registered_user_refresh_scope_provenance_v2'
                  AND conrelid = 'registered_user_refresh_scope_progress'::regclass
            ) THEN
                ALTER TABLE registered_user_refresh_scope_progress
                    ADD CONSTRAINT ck_registered_user_refresh_scope_provenance_v2
                    CHECK (
                        (provenance = 'scrape'
                            AND scrape_id IS NOT NULL
                            AND scrape_id > 0)
                        OR (provenance = 'phase_only' AND scrape_id IS NULL))
                    NOT VALID;
            END IF;
        END
        $$;

        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conname = 'ck_registered_user_refresh_scope_provenance_v2'
                  AND conrelid = 'registered_user_refresh_scope_progress'::regclass
                  AND NOT convalidated
            ) THEN
                ALTER TABLE registered_user_refresh_scope_progress
                    VALIDATE CONSTRAINT ck_registered_user_refresh_scope_provenance_v2;
            END IF;
        END
        $$;

        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conname = 'ck_registered_user_refresh_scope_provenance'
                  AND conrelid = 'registered_user_refresh_scope_progress'::regclass
            ) THEN
                ALTER TABLE registered_user_refresh_scope_progress
                    DROP CONSTRAINT ck_registered_user_refresh_scope_provenance;
            END IF;
        END
        $$;

        CREATE INDEX IF NOT EXISTS ix_registered_user_refresh_scope_checked_at
            ON registered_user_refresh_scope_progress (checked_at, song_id, instrument)
            WHERE status = 'complete';

        CREATE TABLE IF NOT EXISTS registered_bands (
            source_id           TEXT        NOT NULL,
            band_type           TEXT        NOT NULL,
            team_key            TEXT        NOT NULL,
            band_id             TEXT        NOT NULL,
            registered_at       TIMESTAMPTZ NOT NULL,
            last_activity_at    TIMESTAMPTZ,
            last_member_sync_at TIMESTAMPTZ,
            PRIMARY KEY (source_id, band_type, team_key)
        );

        CREATE INDEX IF NOT EXISTS ix_registered_bands_band
            ON registered_bands (band_type, team_key);

        CREATE INDEX IF NOT EXISTS ix_registered_bands_band_id
            ON registered_bands (band_id);

        ALTER TABLE registered_bands
            ADD COLUMN IF NOT EXISTS last_activity_at TIMESTAMPTZ;

        ALTER TABLE registered_bands
            ADD COLUMN IF NOT EXISTS last_member_sync_at TIMESTAMPTZ;

        CREATE OR REPLACE FUNCTION fst_assert_registration_mutation_allowed()
        RETURNS TRIGGER
        LANGUAGE plpgsql
        AS $$
        DECLARE
            reads_frozen BOOLEAN;
            freeze_reason TEXT;
            mutation_gate_token TEXT;
            guard_bypass TEXT;
            gate_bypass_allowed BOOLEAN;
            freeze_bypass_allowed BOOLEAN;
        BEGIN
            SELECT public_reads_frozen,
                   public_reads_frozen_reason,
                   max_score_mutation_gate_token
            INTO reads_frozen,
                 freeze_reason,
                 mutation_gate_token
            FROM scrape_publication_state
            WHERE id = TRUE
            FOR SHARE;

            IF NOT FOUND THEN
                RAISE EXCEPTION
                    USING ERRCODE = '55000',
                          MESSAGE =
                              'Registration mutation rejected because publication state is unavailable.';
            END IF;

            guard_bypass := current_setting(
                'fst.max_score_registration_guard_bypass',
                TRUE);
            gate_bypass_allowed :=
                guard_bypass IS NOT NULL
                AND guard_bypass = mutation_gate_token;
            freeze_bypass_allowed :=
                gate_bypass_allowed
                OR (
                    guard_bypass IS NOT NULL
                    AND guard_bypass = freeze_reason
                );

            IF mutation_gate_token IS NOT NULL
               AND NOT gate_bypass_allowed
            THEN
                RAISE EXCEPTION
                    USING ERRCODE = '55000',
                          MESSAGE =
                              'Registration mutation rejected while max-score maintenance owns the mutation gate.';
            END IF;

            IF reads_frozen
               AND freeze_reason LIKE 'max-score-maintenance:v1:%'
               AND NOT freeze_bypass_allowed
            THEN
                RAISE EXCEPTION
                    USING ERRCODE = '55000',
                          MESSAGE =
                              'Registration mutation rejected while max-score maintenance owns the publication.';
            END IF;

            IF TG_LEVEL = 'STATEMENT' THEN
                RETURN NULL;
            END IF;
            IF TG_OP = 'DELETE' THEN
                RETURN OLD;
            END IF;
            RETURN NEW;
        END
        $$;

        DROP TRIGGER IF EXISTS
            trg_leaderboard_entries_registration_mutation_guard
            ON leaderboard_entries;
        CREATE TRIGGER
            trg_leaderboard_entries_registration_mutation_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON leaderboard_entries
            FOR EACH STATEMENT
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        DROP TRIGGER IF EXISTS
            trg_leaderboard_entries_overlay_registration_mutation_guard
            ON leaderboard_entries_overlay;
        CREATE TRIGGER
            trg_leaderboard_entries_overlay_registration_mutation_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON leaderboard_entries_overlay
            FOR EACH STATEMENT
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        DROP TRIGGER IF EXISTS
            trg_score_history_registration_mutation_guard
            ON score_history;
        CREATE TRIGGER
            trg_score_history_registration_mutation_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON score_history
            FOR EACH STATEMENT
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        DROP TRIGGER IF EXISTS trg_registered_users_maintenance_guard
            ON registered_users;
        CREATE TRIGGER trg_registered_users_maintenance_guard
            BEFORE INSERT OR UPDATE OR DELETE ON registered_users
            FOR EACH ROW
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        DROP TRIGGER IF EXISTS trg_registered_bands_maintenance_guard
            ON registered_bands;
        CREATE TRIGGER trg_registered_bands_maintenance_guard
            BEFORE INSERT OR UPDATE OR DELETE ON registered_bands
            FOR EACH ROW
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        DROP TRIGGER IF EXISTS
            trg_registered_user_refresh_scope_maintenance_guard
            ON registered_user_refresh_scope_progress;
        CREATE TRIGGER
            trg_registered_user_refresh_scope_maintenance_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON registered_user_refresh_scope_progress
            FOR EACH ROW
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        CREATE TABLE IF NOT EXISTS registered_band_processing_status (
            source_id              TEXT    NOT NULL,
            band_type              TEXT    NOT NULL,
            team_key               TEXT    NOT NULL,
            status                 TEXT    NOT NULL DEFAULT 'pending',
            lookups_checked        INTEGER NOT NULL DEFAULT 0,
            entries_found          INTEGER NOT NULL DEFAULT 0,
            total_lookups_to_check INTEGER NOT NULL DEFAULT 0,
            started_at             TIMESTAMPTZ,
            completed_at           TIMESTAMPTZ,
            last_resumed_at        TIMESTAMPTZ,
            error_message          TEXT,
            PRIMARY KEY (source_id, band_type, team_key)
        );

        CREATE INDEX IF NOT EXISTS ix_registered_band_processing_status
            ON registered_band_processing_status (status);

        DROP TRIGGER IF EXISTS
            trg_registered_band_processing_status_maintenance_guard
            ON registered_band_processing_status;
        CREATE TRIGGER
            trg_registered_band_processing_status_maintenance_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON registered_band_processing_status
            FOR EACH ROW
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        CREATE TABLE IF NOT EXISTS registered_band_processing_progress (
            source_id   TEXT        NOT NULL,
            band_type   TEXT        NOT NULL,
            team_key    TEXT        NOT NULL,
            song_id     TEXT        NOT NULL,
            scope       TEXT        NOT NULL,
            season      INTEGER     NOT NULL DEFAULT 0,
            checked     INTEGER     NOT NULL DEFAULT 0,
            entry_found INTEGER     NOT NULL DEFAULT 0,
            checked_at  TIMESTAMPTZ,
            window_id   TEXT        NOT NULL DEFAULT '',
            PRIMARY KEY (source_id, band_type, team_key, song_id, scope, season)
        );

        ALTER TABLE registered_band_processing_progress
            ADD COLUMN IF NOT EXISTS window_id TEXT NOT NULL DEFAULT '';

        CREATE INDEX IF NOT EXISTS ix_registered_band_processing_progress_band
            ON registered_band_processing_progress (source_id, band_type, team_key);

        DROP TRIGGER IF EXISTS
            trg_registered_band_processing_progress_maintenance_guard
            ON registered_band_processing_progress;
        CREATE TRIGGER
            trg_registered_band_processing_progress_maintenance_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON registered_band_processing_progress
            FOR EACH ROW
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        CREATE TABLE IF NOT EXISTS registered_player_band_discovery_progress (
            account_id  TEXT        NOT NULL,
            song_id     TEXT        NOT NULL,
            band_type   TEXT        NOT NULL,
            scope       TEXT        NOT NULL,
            season      INTEGER     NOT NULL DEFAULT 0,
            checked     INTEGER     NOT NULL DEFAULT 0,
            entry_found INTEGER     NOT NULL DEFAULT 0,
            checked_at  TIMESTAMPTZ,
            window_id   TEXT        NOT NULL DEFAULT '',
            PRIMARY KEY (account_id, song_id, band_type, scope, season)
        );

        ALTER TABLE registered_player_band_discovery_progress
            ADD COLUMN IF NOT EXISTS window_id TEXT NOT NULL DEFAULT '';

        CREATE INDEX IF NOT EXISTS ix_registered_player_band_discovery_progress_account
            ON registered_player_band_discovery_progress (account_id);

        DROP TRIGGER IF EXISTS
            trg_registered_player_band_discovery_maintenance_guard
            ON registered_player_band_discovery_progress;
        CREATE TRIGGER
            trg_registered_player_band_discovery_maintenance_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON registered_player_band_discovery_progress
            FOR EACH ROW
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        -- =====================================================================
        -- USER SESSIONS (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS user_sessions (
            id                 SERIAL      PRIMARY KEY,
            username           TEXT        NOT NULL,
            device_id          TEXT        NOT NULL,
            refresh_token_hash TEXT        NOT NULL UNIQUE,
            platform           TEXT,
            issued_at          TIMESTAMPTZ NOT NULL,
            expires_at         TIMESTAMPTZ NOT NULL,
            last_refreshed_at  TIMESTAMPTZ,
            revoked_at         TIMESTAMPTZ
        );

        CREATE INDEX IF NOT EXISTS ix_sessions_username
            ON user_sessions (username);
        CREATE INDEX IF NOT EXISTS ix_sessions_token
            ON user_sessions (refresh_token_hash) WHERE revoked_at IS NULL;

        -- =====================================================================
        -- BACKFILL STATUS (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS backfill_status (
            account_id           TEXT    PRIMARY KEY,
            status               TEXT    NOT NULL DEFAULT 'pending',
            songs_checked        INTEGER NOT NULL DEFAULT 0,
            entries_found        INTEGER NOT NULL DEFAULT 0,
            total_songs_to_check INTEGER NOT NULL DEFAULT 0,
            started_at           TIMESTAMPTZ,
            completed_at         TIMESTAMPTZ,
            last_resumed_at      TIMESTAMPTZ,
            error_message        TEXT,
            rankings_pending     BOOLEAN NOT NULL DEFAULT FALSE,
            deferred_reason      TEXT
        );

        ALTER TABLE backfill_status ADD COLUMN IF NOT EXISTS rankings_pending BOOLEAN NOT NULL DEFAULT FALSE;
        ALTER TABLE backfill_status ADD COLUMN IF NOT EXISTS deferred_reason TEXT;

        CREATE INDEX IF NOT EXISTS ix_backfill_status
            ON backfill_status (status);

        -- =====================================================================
        -- BACKFILL PROGRESS (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS backfill_progress (
            account_id  TEXT    NOT NULL,
            song_id     TEXT    NOT NULL,
            instrument  TEXT    NOT NULL,
            checked     INTEGER NOT NULL DEFAULT 0,
            entry_found INTEGER NOT NULL DEFAULT 0,
            checked_at  TIMESTAMPTZ,
            PRIMARY KEY (account_id, song_id, instrument)
        );

        CREATE INDEX IF NOT EXISTS ix_bfp_account
            ON backfill_progress (account_id);

        DROP TRIGGER IF EXISTS trg_backfill_status_maintenance_guard
            ON backfill_status;
        CREATE TRIGGER trg_backfill_status_maintenance_guard
            BEFORE INSERT OR UPDATE OR DELETE ON backfill_status
            FOR EACH ROW
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        DROP TRIGGER IF EXISTS trg_backfill_progress_maintenance_guard
            ON backfill_progress;
        CREATE TRIGGER trg_backfill_progress_maintenance_guard
            BEFORE INSERT OR UPDATE OR DELETE ON backfill_progress
            FOR EACH ROW
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        -- =====================================================================
        -- HISTORY RECON STATUS (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS history_recon_status (
            account_id               TEXT    PRIMARY KEY,
            status                   TEXT    NOT NULL DEFAULT 'pending',
            songs_processed          INTEGER NOT NULL DEFAULT 0,
            total_songs_to_process   INTEGER NOT NULL DEFAULT 0,
            seasons_queried          INTEGER NOT NULL DEFAULT 0,
            history_entries_found    INTEGER NOT NULL DEFAULT 0,
            started_at               TIMESTAMPTZ,
            completed_at             TIMESTAMPTZ,
            error_message            TEXT,
            reconstruction_version   INTEGER NOT NULL DEFAULT 0,
            window_fingerprint       TEXT    NOT NULL DEFAULT '',
            admission_revision       BIGINT  NOT NULL DEFAULT 0
        );

        ALTER TABLE history_recon_status
            ADD COLUMN IF NOT EXISTS reconstruction_version INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE history_recon_status
            ADD COLUMN IF NOT EXISTS window_fingerprint TEXT NOT NULL DEFAULT '';
        ALTER TABLE history_recon_status
            ADD COLUMN IF NOT EXISTS admission_revision BIGINT NOT NULL DEFAULT 0;

        UPDATE history_recon_status
        SET status = 'pending',
            songs_processed = 0,
            seasons_queried = 0,
            history_entries_found = 0,
            completed_at = NULL,
            error_message = 'history_reconstruction_v2_required'
        WHERE status = 'complete'
          AND reconstruction_version < 2;

        CREATE INDEX IF NOT EXISTS ix_hr_status
            ON history_recon_status (status);

        DROP TRIGGER IF EXISTS
            trg_history_recon_status_maintenance_guard
            ON history_recon_status;
        CREATE TRIGGER
            trg_history_recon_status_maintenance_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON history_recon_status
            FOR EACH ROW
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        -- =====================================================================
        -- HISTORY RECON PROGRESS (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS history_recon_progress (
            account_id  TEXT    NOT NULL,
            song_id     TEXT    NOT NULL,
            instrument  TEXT    NOT NULL,
            processed   INTEGER NOT NULL DEFAULT 0,
            processed_at TIMESTAMPTZ,
            reconstruction_version INTEGER NOT NULL DEFAULT 0,
            window_fingerprint TEXT NOT NULL DEFAULT '',
            admission_revision BIGINT NOT NULL DEFAULT 0,
            PRIMARY KEY (account_id, song_id, instrument)
        );

        ALTER TABLE history_recon_progress
            ADD COLUMN IF NOT EXISTS reconstruction_version INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE history_recon_progress
            ADD COLUMN IF NOT EXISTS window_fingerprint TEXT NOT NULL DEFAULT '';
        ALTER TABLE history_recon_progress
            ADD COLUMN IF NOT EXISTS admission_revision BIGINT NOT NULL DEFAULT 0;

        CREATE INDEX IF NOT EXISTS ix_hrp_account
            ON history_recon_progress (account_id);

        DROP TRIGGER IF EXISTS
            trg_history_recon_progress_maintenance_guard
            ON history_recon_progress;
        CREATE TRIGGER
            trg_history_recon_progress_maintenance_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON history_recon_progress
            FOR EACH ROW
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        -- =====================================================================
        -- SEASON WINDOWS (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS season_windows (
            season_number INTEGER PRIMARY KEY,
            event_id      TEXT        NOT NULL,
            window_id     TEXT        NOT NULL,
            source_kind   TEXT        NOT NULL DEFAULT 'legacy',
            discovered_at TIMESTAMPTZ NOT NULL
        );

        ALTER TABLE season_windows
            ADD COLUMN IF NOT EXISTS source_kind TEXT NOT NULL DEFAULT 'legacy';

        UPDATE season_windows
        SET source_kind = 'synthetic'
        WHERE source_kind = 'legacy'
          AND event_id = ''
          AND window_id = '';

        -- =====================================================================
        -- SONG FIRST SEEN SEASON (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS song_first_seen_season (
            song_id               TEXT    PRIMARY KEY,
            first_seen_season     INTEGER,
            min_observed_season   INTEGER,
            estimated_season      INTEGER NOT NULL,
            probe_result          TEXT,
            calculated_at         TIMESTAMPTZ NOT NULL,
            calculation_version   INTEGER,
            window_fingerprint    TEXT NOT NULL DEFAULT '',
            max_season            INTEGER NOT NULL DEFAULT 0
        );
        ALTER TABLE song_first_seen_season ADD COLUMN IF NOT EXISTS calculation_version INTEGER;
        ALTER TABLE song_first_seen_season ADD COLUMN IF NOT EXISTS window_fingerprint TEXT NOT NULL DEFAULT '';
        ALTER TABLE song_first_seen_season ADD COLUMN IF NOT EXISTS max_season INTEGER NOT NULL DEFAULT 0;

        -- =====================================================================
        -- EPIC USER TOKENS (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS epic_user_tokens (
            account_id              TEXT    PRIMARY KEY,
            encrypted_access_token  BYTEA   NOT NULL,
            encrypted_refresh_token BYTEA   NOT NULL,
            token_expires_at        TIMESTAMPTZ NOT NULL,
            refresh_expires_at      TIMESTAMPTZ NOT NULL,
            nonce                   BYTEA   NOT NULL,
            updated_at              TIMESTAMPTZ NOT NULL
        );

        -- =====================================================================
        -- LEADERBOARD POPULATION (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS leaderboard_population (
            song_id       TEXT    NOT NULL,
            instrument    TEXT    NOT NULL,
            total_entries INTEGER NOT NULL DEFAULT -1,
            updated_at    TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (song_id, instrument)
        );

        DROP TRIGGER IF EXISTS
            trg_leaderboard_population_registration_mutation_guard
            ON leaderboard_population;
        CREATE TRIGGER
            trg_leaderboard_population_registration_mutation_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON leaderboard_population
            FOR EACH STATEMENT
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        -- =====================================================================
        -- PLAYER STATS (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS player_stats (
            account_id        TEXT    NOT NULL,
            instrument        TEXT    NOT NULL,
            songs_played      INTEGER NOT NULL DEFAULT 0,
            full_combo_count  INTEGER NOT NULL DEFAULT 0,
            gold_star_count   INTEGER NOT NULL DEFAULT 0,
            avg_accuracy      REAL    NOT NULL DEFAULT 0,
            best_rank         INTEGER NOT NULL DEFAULT 0,
            best_rank_song_id TEXT,
            total_score       INTEGER NOT NULL DEFAULT 0,
            percentile_dist   TEXT,
            avg_percentile    TEXT,
            overall_percentile TEXT,
            updated_at        TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (account_id, instrument)
        );

        -- =====================================================================
        -- PLAYER STATS TIERS (leeway breakpoint system)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS player_stats_tiers (
            account_id TEXT        NOT NULL,
            instrument TEXT        NOT NULL,
            tiers_json JSONB       NOT NULL DEFAULT '[]'::jsonb,
            updated_at TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (account_id, instrument)
        );
        CREATE INDEX IF NOT EXISTS ix_pst_account ON player_stats_tiers (account_id);

        -- =====================================================================
        -- DATA VERSION (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS data_version (
            key     TEXT    PRIMARY KEY,
            version INTEGER NOT NULL
        );

        -- =====================================================================
        -- RIVALS STATUS (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS rivals_status (
            account_id               TEXT    PRIMARY KEY,
            status                   TEXT    NOT NULL DEFAULT 'pending',
            combos_computed          INTEGER NOT NULL DEFAULT 0,
            total_combos_to_compute  INTEGER NOT NULL DEFAULT 0,
            rivals_found             INTEGER NOT NULL DEFAULT 0,
            algorithm_version        INTEGER NOT NULL DEFAULT 0,
            started_at               TIMESTAMPTZ,
            completed_at             TIMESTAMPTZ,
            error_message            TEXT
        );

        ALTER TABLE rivals_status ADD COLUMN IF NOT EXISTS algorithm_version INTEGER NOT NULL DEFAULT 0;

        -- =====================================================================
        -- USER RIVALS (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS user_rivals (
            user_id           TEXT    NOT NULL,
            rival_account_id  TEXT    NOT NULL,
            instrument_combo  TEXT    NOT NULL,
            direction         TEXT    NOT NULL,
            rival_score       REAL    NOT NULL,
            avg_signed_delta  REAL    NOT NULL,
            shared_song_count INTEGER NOT NULL,
            ahead_count       INTEGER NOT NULL,
            behind_count      INTEGER NOT NULL,
            computed_at       TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (user_id, rival_account_id, instrument_combo)
        );

        CREATE INDEX IF NOT EXISTS ix_ur_combo
            ON user_rivals (user_id, instrument_combo, direction, rival_score DESC);

        -- =====================================================================
        -- RIVAL SONG SAMPLES (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS rival_song_samples (
            user_id          TEXT    NOT NULL,
            rival_account_id TEXT    NOT NULL,
            instrument       TEXT    NOT NULL,
            song_id          TEXT    NOT NULL,
            user_rank        INTEGER NOT NULL,
            rival_rank       INTEGER NOT NULL,
            rank_delta       INTEGER NOT NULL,
            user_score       INTEGER,
            rival_score      INTEGER,
            PRIMARY KEY (user_id, rival_account_id, instrument, song_id)
        );

        CREATE INDEX IF NOT EXISTS ix_rs_rival
            ON rival_song_samples (user_id, rival_account_id, instrument);

        -- =====================================================================
        -- RIVALS DIRTY SONGS (selection refresh queue)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS rivals_dirty_songs (
            account_id    TEXT        NOT NULL,
            instrument    TEXT        NOT NULL,
            song_id       TEXT        NOT NULL,
            dirty_reason  TEXT        NOT NULL,
            detected_at   TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (account_id, instrument, song_id)
        );

        CREATE INDEX IF NOT EXISTS ix_rds_account
            ON rivals_dirty_songs (account_id, instrument);

        -- =====================================================================
        -- RIVALS SONG FINGERPRINTS (selection-state baseline)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS rival_song_fingerprints (
            account_id              TEXT        NOT NULL,
            instrument              TEXT        NOT NULL,
            song_id                 TEXT        NOT NULL,
            user_rank               INTEGER     NOT NULL,
            neighborhood_signature  TEXT        NOT NULL,
            computed_at             TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (account_id, instrument, song_id)
        );

        CREATE INDEX IF NOT EXISTS ix_rsf_account
            ON rival_song_fingerprints (account_id, instrument);

        -- =====================================================================
        -- RIVALS INSTRUMENT STATE (eligibility baseline)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS rival_instrument_state (
            account_id    TEXT        NOT NULL,
            instrument    TEXT        NOT NULL,
            song_count    INTEGER     NOT NULL,
            is_eligible   BOOLEAN     NOT NULL,
            computed_at   TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (account_id, instrument)
        );

        -- =====================================================================
        -- ITEM SHOP TRACKS (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS item_shop_tracks (
            song_id           TEXT    PRIMARY KEY,
            scraped_at        TIMESTAMPTZ NOT NULL,
            leaving_tomorrow  BOOLEAN NOT NULL DEFAULT FALSE,
            is_new            BOOLEAN NOT NULL DEFAULT FALSE
        );

        ALTER TABLE item_shop_tracks ADD COLUMN IF NOT EXISTS is_new BOOLEAN NOT NULL DEFAULT FALSE;

        -- =====================================================================
        -- COMPOSITE RANKINGS (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS composite_rankings (
            account_id               TEXT    PRIMARY KEY,
            instruments_played       INTEGER NOT NULL,
            total_songs_played       INTEGER NOT NULL,
            composite_rating         REAL    NOT NULL,
            composite_rank           INTEGER NOT NULL UNIQUE,
            guitar_adjusted_skill    REAL,
            guitar_skill_rank        INTEGER,
            bass_adjusted_skill      REAL,
            bass_skill_rank          INTEGER,
            drums_adjusted_skill     REAL,
            drums_skill_rank         INTEGER,
            vocals_adjusted_skill    REAL,
            vocals_skill_rank        INTEGER,
            pro_guitar_adjusted_skill REAL,
            pro_guitar_skill_rank    INTEGER,
            pro_bass_adjusted_skill  REAL,
            pro_bass_skill_rank      INTEGER,
            pro_vocals_adjusted_skill REAL,
            pro_vocals_skill_rank    INTEGER,
            pro_cymbals_adjusted_skill REAL,
            pro_cymbals_skill_rank   INTEGER,
            pro_drums_adjusted_skill REAL,
            pro_drums_skill_rank     INTEGER,
            composite_rating_weighted  REAL,
            composite_rank_weighted    INTEGER,
            composite_rating_fcrate    REAL,
            composite_rank_fcrate      INTEGER,
            composite_rating_totalscore REAL,
            composite_rank_totalscore  INTEGER,
            composite_rating_maxscore  REAL,
            composite_rank_maxscore    INTEGER,
            computed_at              TIMESTAMPTZ NOT NULL
        );

        -- composite_rank is UNIQUE, so its constraint index owns adjusted-rank
        -- ordering without a duplicate non-constraint ix_cr_rank index.

        -- Per-metric composite rank indexes for pagination
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS composite_rating_weighted REAL;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS composite_rank_weighted INTEGER;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS composite_rating_fcrate REAL;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS composite_rank_fcrate INTEGER;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS composite_rating_totalscore REAL;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS composite_rank_totalscore INTEGER;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS composite_rating_maxscore REAL;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS composite_rank_maxscore INTEGER;

        -- Peripheral instrument columns (Karaoke, Pro Drums + Cymbals, Pro Drums)
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS pro_vocals_adjusted_skill REAL;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS pro_vocals_skill_rank INTEGER;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS pro_cymbals_adjusted_skill REAL;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS pro_cymbals_skill_rank INTEGER;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS pro_drums_adjusted_skill REAL;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS pro_drums_skill_rank INTEGER;

        CREATE INDEX IF NOT EXISTS ix_cr_rank_weighted
            ON composite_rankings (composite_rank_weighted);
        -- ix_cr_rank_fcrate, ix_cr_rank_totalscore, ix_cr_rank_maxscore removed
        -- 2026-04-23 (Phase 2): idx_scan=0 over the life of the database; the
        -- endpoints that would use them use ix_cr_rank instead. Saves ~334 MB.

        -- =====================================================================
        -- SOLO FAMILY RANKINGS (fixed global Statistics scopes)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS solo_family_rankings (
            scope_id               TEXT        NOT NULL,
            account_id             TEXT        NOT NULL,
            songs_played           INTEGER     NOT NULL,
            total_charted_songs    INTEGER     NOT NULL,
            coverage               REAL        NOT NULL,
            raw_skill_rating       REAL        NOT NULL,
            adjusted_skill_rating  REAL        NOT NULL,
            adjusted_skill_rank    INTEGER     NOT NULL,
            weighted_rating        REAL        NOT NULL,
            weighted_rank          INTEGER     NOT NULL,
            fc_rate                REAL        NOT NULL,
            fc_rate_rank           INTEGER     NOT NULL,
            total_score            BIGINT      NOT NULL,
            total_score_rank       INTEGER     NOT NULL,
            max_score_percent      REAL        NOT NULL,
            max_score_percent_rank INTEGER     NOT NULL,
            full_combo_count       INTEGER     NOT NULL,
            raw_max_score_percent  REAL,
            raw_weighted_rating    REAL,
            computed_at            TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (scope_id, account_id)
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ix_sfr_adjusted_rank
            ON solo_family_rankings (scope_id, adjusted_skill_rank);
        CREATE UNIQUE INDEX IF NOT EXISTS ix_sfr_weighted_rank
            ON solo_family_rankings (scope_id, weighted_rank);
        CREATE UNIQUE INDEX IF NOT EXISTS ix_sfr_fc_rate_rank
            ON solo_family_rankings (scope_id, fc_rate_rank);
        CREATE UNIQUE INDEX IF NOT EXISTS ix_sfr_total_score_rank
            ON solo_family_rankings (scope_id, total_score_rank);
        CREATE UNIQUE INDEX IF NOT EXISTS ix_sfr_max_score_rank
            ON solo_family_rankings (scope_id, max_score_percent_rank);

        -- =====================================================================
        -- LEADERBOARD RIVALS (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS leaderboard_rivals (
            user_id           TEXT    NOT NULL,
            rival_account_id  TEXT    NOT NULL,
            instrument        TEXT    NOT NULL,
            rank_method       TEXT    NOT NULL,
            direction         TEXT    NOT NULL,
            user_rank         INTEGER NOT NULL,
            rival_rank        INTEGER NOT NULL,
            shared_song_count INTEGER NOT NULL,
            ahead_count       INTEGER NOT NULL,
            behind_count      INTEGER NOT NULL,
            avg_signed_delta  REAL    NOT NULL,
            computed_at       TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (user_id, rival_account_id, instrument, rank_method)
        );

        CREATE INDEX IF NOT EXISTS ix_lbr_user_inst
            ON leaderboard_rivals (user_id, instrument, rank_method, direction);

        CREATE TABLE IF NOT EXISTS leaderboard_rivals_state (
            user_id      TEXT        NOT NULL,
            instrument   TEXT        NOT NULL,
            rank_method  TEXT        NOT NULL,
            user_rank    INTEGER,
            computed_at  TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (user_id, instrument, rank_method)
        );

        -- =====================================================================
        -- LEADERBOARD RIVAL SONG SAMPLES (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS leaderboard_rival_song_samples (
            user_id          TEXT    NOT NULL,
            rival_account_id TEXT    NOT NULL,
            instrument       TEXT    NOT NULL,
            rank_method      TEXT    NOT NULL,
            song_id          TEXT    NOT NULL,
            user_rank        INTEGER NOT NULL,
            rival_rank       INTEGER NOT NULL,
            rank_delta       INTEGER NOT NULL,
            user_score       INTEGER,
            rival_score      INTEGER,
            PRIMARY KEY (user_id, rival_account_id, instrument, rank_method, song_id)
        );

        CREATE INDEX IF NOT EXISTS ix_lbrss_user_rival
            ON leaderboard_rival_song_samples (user_id, rival_account_id, instrument, rank_method);

        -- =====================================================================
        -- COMPOSITE RANK HISTORY (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS composite_rank_history (
            account_id         TEXT    NOT NULL,
            snapshot_date      DATE    NOT NULL,
            composite_rank     INTEGER NOT NULL,
            composite_rating   REAL,
            instruments_played INTEGER,
            total_songs_played INTEGER,
            PRIMARY KEY (account_id, snapshot_date)
        );

        -- =====================================================================
        -- COMBO LEADERBOARD (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS combo_leaderboard (
            combo_id         TEXT    NOT NULL,
            account_id       TEXT    NOT NULL,
            adjusted_rating  REAL    NOT NULL,
            weighted_rating  REAL    NOT NULL,
            fc_rate          REAL    NOT NULL,
            total_score      INTEGER NOT NULL,
            max_score_percent REAL   NOT NULL,
            songs_played     INTEGER NOT NULL,
            full_combo_count INTEGER NOT NULL,
            computed_at      TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (combo_id, account_id)
        );

        CREATE INDEX IF NOT EXISTS ix_combo_adjusted
            ON combo_leaderboard (combo_id, adjusted_rating ASC);
        -- ix_combo_weighted removed 2026-04-23 (Phase 2): idx_scan=0 forever.
        -- Other combo_* indexes (fc_rate, total_score, max_score) are in use.
        CREATE INDEX IF NOT EXISTS ix_combo_fc_rate
            ON combo_leaderboard (combo_id, fc_rate DESC);
        CREATE INDEX IF NOT EXISTS ix_combo_total_score
            ON combo_leaderboard (combo_id, total_score DESC);
        CREATE INDEX IF NOT EXISTS ix_combo_max_score
            ON combo_leaderboard (combo_id, max_score_percent DESC);

        -- =====================================================================
        -- COMBO STATS (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS combo_stats (
            combo_id       TEXT    PRIMARY KEY,
            total_accounts INTEGER NOT NULL,
            computed_at    TIMESTAMPTZ NOT NULL
        );

        -- =====================================================================
        -- MIGRATIONS: raw rating columns + schema version on rank_history
        -- =====================================================================

        ALTER TABLE account_rankings ADD COLUMN IF NOT EXISTS raw_max_score_percent REAL;

        ALTER TABLE rank_history ADD COLUMN IF NOT EXISTS raw_max_score_percent REAL;
        ALTER TABLE rank_history ADD COLUMN IF NOT EXISTS schema_version SMALLINT NOT NULL DEFAULT 1;
        ALTER TABLE rank_history ADD COLUMN IF NOT EXISTS snapshot_taken_at TIMESTAMPTZ;

        ALTER TABLE account_rankings DROP COLUMN IF EXISTS raw_fc_rate;
        ALTER TABLE rank_history DROP COLUMN IF EXISTS raw_fc_rate;

        ALTER TABLE account_rankings ADD COLUMN IF NOT EXISTS raw_weighted_rating REAL;
        ALTER TABLE rank_history ADD COLUMN IF NOT EXISTS raw_weighted_rating REAL;
        ALTER TABLE rank_history ADD COLUMN IF NOT EXISTS raw_skill_rating REAL;

                CREATE TABLE IF NOT EXISTS rank_history_snapshot_stats (
                        instrument              TEXT        NOT NULL,
                        snapshot_date           DATE        NOT NULL,
                        snapshot_taken_at       TIMESTAMPTZ,
                        total_charted_songs     INTEGER     NOT NULL,
                        ranked_account_count    INTEGER,
                        PRIMARY KEY (instrument, snapshot_date)
                );

                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM rank_history_snapshot_stats LIMIT 1) THEN
                        INSERT INTO rank_history_snapshot_stats (instrument, snapshot_date, snapshot_taken_at, total_charted_songs, ranked_account_count)
                        SELECT
                                instrument,
                                snapshot_date,
                                MAX(snapshot_taken_at) AS snapshot_taken_at,
                                MAX(ROUND(songs_played / NULLIF(coverage, 0))::INTEGER) AS total_charted_songs,
                                NULL::INTEGER AS ranked_account_count
                        FROM rank_history
                        WHERE songs_played IS NOT NULL
                            AND coverage IS NOT NULL
                            AND coverage > 0
                        GROUP BY instrument, snapshot_date
                        HAVING MAX(ROUND(songs_played / NULLIF(coverage, 0))::INTEGER) > 0
                        ON CONFLICT (instrument, snapshot_date) DO NOTHING;
                    END IF;
                END $$;

        -- =====================================================================
        -- MIGRATION: deduplicate rank_history + enforce PRIMARY KEY
        -- The original CREATE TABLE IF NOT EXISTS is a no-op on tables that
        -- predate the PK definition, so ON CONFLICT could silently INSERT
        -- duplicates.  Clean up and retrofit the constraint.
        -- =====================================================================

        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = 'rank_history'::regclass
                  AND contype = 'p'
            ) THEN
                DELETE FROM rank_history rh
                WHERE EXISTS (
                    SELECT 1 FROM rank_history rh2
                    WHERE rh2.account_id = rh.account_id
                      AND rh2.instrument = rh.instrument
                      AND rh2.snapshot_date = rh.snapshot_date
                      AND rh2.ctid > rh.ctid
                );

                ALTER TABLE rank_history
                    ADD PRIMARY KEY (account_id, instrument, snapshot_date);
            END IF;
        END $$;

        -- =====================================================================
        -- API RESPONSE CACHE (precomputed JSON responses, replaces RAM store)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS api_response_cache (
            cache_key   TEXT        NOT NULL PRIMARY KEY,
            json_data   BYTEA       NOT NULL,
            etag        TEXT        NOT NULL,
            cached_at   TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- Staging sibling for shadow precomputation (atomic swap into api_response_cache)
        CREATE TABLE IF NOT EXISTS api_response_cache_staging (
            cache_key   TEXT        NOT NULL PRIMARY KEY,
            json_data   BYTEA       NOT NULL,
            etag        TEXT        NOT NULL,
            cached_at   TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        -- Export ZIPs are generated on demand and must not be retained after delivery.
        DROP TABLE IF EXISTS export_archive_cache_staging;
        DROP TABLE IF EXISTS export_archive_cache;

        -- =====================================================================
        -- LEADERBOARD STAGING (chunked scrape entries, merged on finalize)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS leaderboard_staging (
            scrape_id    INT              NOT NULL,
            song_id      TEXT             NOT NULL,
            instrument   TEXT             NOT NULL,
            page_num     INT              NOT NULL,
            account_id   TEXT             NOT NULL,
            score        INT              NOT NULL,
            accuracy     INT,
            is_full_combo BOOLEAN,
            stars        INT,
            season       INT,
            difficulty   INT,
            percentile   DOUBLE PRECISION,
            rank         INT,
            end_time     TEXT,
            api_rank     INT,
            source       TEXT,
            staged_at    TIMESTAMPTZ      NOT NULL DEFAULT now(),
            PRIMARY KEY (scrape_id, song_id, instrument, account_id)
        );

        -- Staging indexes removed 2026-04-23 (Phase 2): idx_scan=0 forever.
        -- leaderboard_staging is truncated each scrape and only contains one
        -- scrape_id at a time, so indexes keyed on scrape_id add no selectivity
        -- beyond the existing PRIMARY KEY. Saves ~1.9 GB of index storage.

        -- Active staging table (v2): partitioned by instrument so finalized
        -- instruments can be truncated instead of row-deleted.
        CREATE TABLE IF NOT EXISTS leaderboard_staging_v2 (
            scrape_id    INT              NOT NULL,
            song_id      TEXT             NOT NULL,
            instrument   TEXT             NOT NULL,
            page_num     INT              NOT NULL,
            account_id   TEXT             NOT NULL,
            score        INT              NOT NULL,
            accuracy     INT,
            is_full_combo BOOLEAN,
            stars        INT,
            season       INT,
            difficulty   INT,
            percentile   DOUBLE PRECISION,
            rank         INT,
            end_time     TEXT,
            api_rank     INT,
            source       TEXT,
            staged_at    TIMESTAMPTZ      NOT NULL DEFAULT now(),
            PRIMARY KEY (scrape_id, song_id, instrument, account_id)
        ) PARTITION BY LIST (instrument);

        CREATE TABLE IF NOT EXISTS leaderboard_staging_v2_solo_guitar
            PARTITION OF leaderboard_staging_v2 FOR VALUES IN ('Solo_Guitar');
        CREATE TABLE IF NOT EXISTS leaderboard_staging_v2_solo_bass
            PARTITION OF leaderboard_staging_v2 FOR VALUES IN ('Solo_Bass');
        CREATE TABLE IF NOT EXISTS leaderboard_staging_v2_solo_drums
            PARTITION OF leaderboard_staging_v2 FOR VALUES IN ('Solo_Drums');
        CREATE TABLE IF NOT EXISTS leaderboard_staging_v2_solo_vocals
            PARTITION OF leaderboard_staging_v2 FOR VALUES IN ('Solo_Vocals');
        CREATE TABLE IF NOT EXISTS leaderboard_staging_v2_pro_guitar
            PARTITION OF leaderboard_staging_v2 FOR VALUES IN ('Solo_PeripheralGuitar');
        CREATE TABLE IF NOT EXISTS leaderboard_staging_v2_pro_bass
            PARTITION OF leaderboard_staging_v2 FOR VALUES IN ('Solo_PeripheralBass');
        CREATE TABLE IF NOT EXISTS leaderboard_staging_v2_pro_vocals
            PARTITION OF leaderboard_staging_v2 FOR VALUES IN ('Solo_PeripheralVocals');
        CREATE TABLE IF NOT EXISTS leaderboard_staging_v2_pro_cymbals
            PARTITION OF leaderboard_staging_v2 FOR VALUES IN ('Solo_PeripheralCymbals');
        CREATE TABLE IF NOT EXISTS leaderboard_staging_v2_pro_drums
            PARTITION OF leaderboard_staging_v2 FOR VALUES IN ('Solo_PeripheralDrums');

        -- =====================================================================
        -- LEADERBOARD STAGING METADATA (per-combo finalization state)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS leaderboard_staging_meta (
            scrape_id              INT     NOT NULL,
            song_id                TEXT    NOT NULL,
            instrument             TEXT    NOT NULL,
            reported_pages         INT     NOT NULL,
            pages_scraped          INT     NOT NULL,
            entries_staged         INT     NOT NULL,
            valid_entry_count      INT,
            requests               INT     NOT NULL,
            bytes_received         BIGINT  NOT NULL,
            deep_scrape_status     TEXT,
            wave1_finalized_at     TIMESTAMPTZ,
            wave2_finalized_at     TIMESTAMPTZ,
            PRIMARY KEY (scrape_id, song_id, instrument)
        );

        -- =====================================================================
        -- DEEP SCRAPE QUEUE (wave 2 job scheduling)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS deep_scrape_queue (
            scrape_id              INT     NOT NULL,
            song_id                TEXT    NOT NULL,
            instrument             TEXT    NOT NULL,
            label                  TEXT,
            valid_cutoff           INT     NOT NULL,
            valid_entry_target     INT     NOT NULL,
            wave2_start_page       INT     NOT NULL,
            reported_pages         INT     NOT NULL,
            initial_valid_count    INT     NOT NULL,
            status                 TEXT    NOT NULL DEFAULT 'pending',
            cursor_page            INT,
            current_valid_count    INT,
            created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
            completed_at           TIMESTAMPTZ,
            PRIMARY KEY (scrape_id, song_id, instrument)
        );

        CREATE INDEX IF NOT EXISTS ix_dsq_status
            ON deep_scrape_queue (scrape_id, status);

        -- =====================================================================
        -- BAND LEADERBOARDS (Duets, Trios, Quads)
        -- =====================================================================

        -- Band entries: one row per (song, band_type, team_key).
        -- team_key = sorted colon-joined account IDs (deterministic, Epic doesn't sort).
        CREATE TABLE IF NOT EXISTS band_entries (
            song_id             TEXT             NOT NULL,
            band_type           TEXT             NOT NULL,
            team_key            TEXT             NOT NULL,
            instrument_combo    TEXT             NOT NULL DEFAULT '',
            team_members        TEXT[]           NOT NULL,
            score               INT              NOT NULL,
            base_score          INT,
            instrument_bonus    INT,
            overdrive_bonus     INT,
            accuracy            INT,
            is_full_combo       BOOLEAN,
            stars               INT,
            difficulty          INT,
            season              INT,
            rank                INT              DEFAULT 0,
            percentile          DOUBLE PRECISION,
            end_time            TEXT,
            source              TEXT             NOT NULL DEFAULT 'scrape',
            is_over_threshold   BOOLEAN          NOT NULL DEFAULT FALSE,
            first_seen_at       TIMESTAMPTZ      NOT NULL DEFAULT now(),
            last_updated_at     TIMESTAMPTZ      NOT NULL DEFAULT now(),
            PRIMARY KEY (song_id, band_type, team_key, instrument_combo)
        ) PARTITION BY LIST (band_type);

        -- FILLFACTOR=80 — band_entries sees both heavy UPDATEs (team-key
        -- reassignment) and DELETEs (partition churn), so leaving 20% free
        -- page space gives more room for HOT updates and avoids immediate
        -- page splits when dead tuples get vacuumed.
        CREATE TABLE IF NOT EXISTS band_entries_duets  PARTITION OF band_entries FOR VALUES IN ('Band_Duets') WITH (fillfactor=80);
        CREATE TABLE IF NOT EXISTS band_entries_trios  PARTITION OF band_entries FOR VALUES IN ('Band_Trios') WITH (fillfactor=80);
        CREATE TABLE IF NOT EXISTS band_entries_quad   PARTITION OF band_entries FOR VALUES IN ('Band_Quad')  WITH (fillfactor=80);

        -- Idempotent fillfactor migration for pre-existing partitions.
        ALTER TABLE band_entries_duets SET (fillfactor=80);
        ALTER TABLE band_entries_trios SET (fillfactor=80);
        ALTER TABLE band_entries_quad  SET (fillfactor=80);

        -- ix_be_song_score + ix_be_song_rank removed 2026-04-23 (Phase 2):
        -- idx_scan=0 across all three band partitions forever. The per-song
        -- ordering queries read from band_team_rankings_current_band_* instead.
        -- Saves ~2.1 GB (score idx) + ~1.1 GB (rank idx).

        -- Per-member stats for each band entry.
        -- Populated from trackedStats M_{i}_* fields during V1 parsing or V2 enrichment.
        CREATE TABLE IF NOT EXISTS band_member_stats (
            song_id             TEXT    NOT NULL,
            band_type           TEXT    NOT NULL,
            team_key            TEXT    NOT NULL,
            instrument_combo    TEXT    NOT NULL DEFAULT '',
            member_index        INT     NOT NULL,
            account_id          TEXT    NOT NULL,
            instrument_id       INT,
            score               INT,
            accuracy            INT,
            is_full_combo       BOOLEAN,
            stars               INT,
            difficulty          INT,
            PRIMARY KEY (song_id, band_type, team_key, instrument_combo, member_index)
        );

        DROP TRIGGER IF EXISTS
            trg_band_member_stats_registration_mutation_guard
            ON band_member_stats;
        CREATE TRIGGER
            trg_band_member_stats_registration_mutation_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON band_member_stats
            FOR EACH STATEMENT
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        -- ix_bms_account removed 2026-04-23 (Phase 2): idx_scan=0 forever.
        -- Reverse lookup "get stats for all bands player X played" is not in use;
        -- the PK (song_id, band_type, ...) serves the forward direction. Saves ~650 MB.

        -- Denormalized lookup: all bands a player appears in.
        -- Enables "find all bands for player X" queries without scanning band_member_stats.
        CREATE TABLE IF NOT EXISTS band_members (
            account_id          TEXT    NOT NULL,
            song_id             TEXT    NOT NULL,
            band_type           TEXT    NOT NULL,
            team_key            TEXT    NOT NULL,
            instrument_combo    TEXT    NOT NULL DEFAULT '',
            PRIMARY KEY (account_id, song_id, band_type, team_key, instrument_combo)
        );

        -- ix_bm_song_type removed 2026-04-23 (Phase 2): idx_scan=0 forever.
        -- The lookup pattern "all members in song X of band_type Y" isn't
        -- exercised; the PK serves the account-first path used in practice.
        -- Saves ~393 MB.

        -- Player-band summary rows. One row per account × team × raw combo.
        -- This replaces repeated per-request grouping over band_members.
        CREATE TABLE IF NOT EXISTS band_team_membership (
            account_id              TEXT        NOT NULL,
            band_type               TEXT        NOT NULL,
            team_key                TEXT        NOT NULL,
            instrument_combo        TEXT        NOT NULL DEFAULT '',
            appearance_count        INTEGER     NOT NULL,
            member_instruments_json JSONB       NOT NULL DEFAULT '{}'::jsonb,
            updated_at              TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (account_id, band_type, team_key, instrument_combo)
        );

        CREATE INDEX IF NOT EXISTS ix_btm_account_band_type
            ON band_team_membership (account_id, band_type);

        CREATE INDEX IF NOT EXISTS ix_btm_band_team
            ON band_team_membership (band_type, team_key);

        -- Rollout safety: only accounts with a state row are allowed to read the
        -- summary directly. Existing accounts are backfilled once on first read.
        CREATE TABLE IF NOT EXISTS band_team_membership_state (
            account_id   TEXT        PRIMARY KEY,
            rebuilt_at   TIMESTAMPTZ NOT NULL
        );

        -- Exact member-to-instrument assignments observed for a band/team/combo.
        -- band_team_membership stores per-member instrument unions; this table
        -- preserves the exact tuple for selected-band instrument filters.
        CREATE TABLE IF NOT EXISTS band_team_configurations (
            band_type               TEXT        NOT NULL,
            team_key                TEXT        NOT NULL,
            instrument_combo        TEXT        NOT NULL DEFAULT '',
            assignment_key          TEXT        NOT NULL,
            appearance_count        INTEGER     NOT NULL,
            member_assignments_json JSONB       NOT NULL DEFAULT '{}'::jsonb,
            updated_at              TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (band_type, team_key, instrument_combo, assignment_key)
        );

        CREATE INDEX IF NOT EXISTS ix_btc_band_team
            ON band_team_configurations (band_type, team_key);

        DROP TRIGGER IF EXISTS
            trg_band_entries_registration_mutation_guard
            ON band_entries;
        CREATE TRIGGER
            trg_band_entries_registration_mutation_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON band_entries
            FOR EACH STATEMENT
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        DROP TRIGGER IF EXISTS
            trg_band_members_registration_mutation_guard
            ON band_members;
        CREATE TRIGGER
            trg_band_members_registration_mutation_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON band_members
            FOR EACH STATEMENT
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        DROP TRIGGER IF EXISTS
            trg_band_team_membership_registration_mutation_guard
            ON band_team_membership;
        CREATE TRIGGER
            trg_band_team_membership_registration_mutation_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON band_team_membership
            FOR EACH STATEMENT
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        DROP TRIGGER IF EXISTS
            trg_band_team_membership_state_registration_mutation_guard
            ON band_team_membership_state;
        CREATE TRIGGER
            trg_band_team_membership_state_registration_mutation_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON band_team_membership_state
            FOR EACH STATEMENT
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        DROP TRIGGER IF EXISTS
            trg_band_team_configurations_registration_mutation_guard
            ON band_team_configurations;
        CREATE TRIGGER
            trg_band_team_configurations_registration_mutation_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON band_team_configurations
            FOR EACH STATEMENT
            EXECUTE FUNCTION fst_assert_registration_mutation_allowed();

        CREATE TABLE IF NOT EXISTS band_identity (
            band_id            TEXT        PRIMARY KEY,
            band_type          TEXT        NOT NULL,
            team_key           TEXT        NOT NULL,
            member_account_ids TEXT[]      NOT NULL DEFAULT ARRAY[]::TEXT[],
            appearance_count   INTEGER     NOT NULL DEFAULT 0,
            first_seen_at      TIMESTAMPTZ,
            last_seen_at       TIMESTAMPTZ,
            updated_at         TIMESTAMPTZ NOT NULL,
            source             TEXT        NOT NULL DEFAULT 'unknown',
            UNIQUE (band_type, team_key)
        );

        -- Rich global band-search projection. Search reads these precomputed
        -- rows instead of triggering request-time per-account summary rebuilds.
        CREATE TABLE IF NOT EXISTS band_search_team_projection (
            band_type               TEXT        NOT NULL,
            team_key                TEXT        NOT NULL,
            band_id                 TEXT        NOT NULL DEFAULT '',
            appearance_count        INTEGER     NOT NULL,
            member_account_ids      TEXT[]      NOT NULL,
            member_instruments_json JSONB       NOT NULL DEFAULT '{}'::jsonb,
            combo_appearances_json  JSONB       NOT NULL DEFAULT '{}'::jsonb,
            updated_at              TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (band_type, team_key)
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ix_bstp_band_id
            ON band_search_team_projection (band_id)
            WHERE band_id <> '';

        CREATE TABLE IF NOT EXISTS band_search_member_projection (
            account_id              TEXT        NOT NULL,
            band_type               TEXT        NOT NULL,
            team_key                TEXT        NOT NULL,
            band_id                 TEXT        NOT NULL DEFAULT '',
            appearance_count        INTEGER     NOT NULL,
            team_appearance_count   INTEGER     NOT NULL,
            instrument_combos       TEXT[]      NOT NULL DEFAULT ARRAY[]::TEXT[],
            updated_at              TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (account_id, band_type, team_key)
        );

        CREATE INDEX IF NOT EXISTS ix_bsmp_account_type_appearance
            ON band_search_member_projection (account_id, band_type, team_appearance_count DESC, team_key);

        CREATE INDEX IF NOT EXISTS ix_bsmp_type_team
            ON band_search_member_projection (band_type, team_key);

        CREATE TABLE IF NOT EXISTS band_search_projection_state (
            id          BOOLEAN     PRIMARY KEY DEFAULT TRUE CHECK (id),
            rebuilt_at  TIMESTAMPTZ NOT NULL,
            refreshed_at TIMESTAMPTZ,
            team_rows   BIGINT      NOT NULL,
            member_rows BIGINT      NOT NULL
        );

        ALTER TABLE band_search_projection_state
            ADD COLUMN IF NOT EXISTS refreshed_at TIMESTAMPTZ;

        CREATE SEQUENCE IF NOT EXISTS band_current_projection_generation_seq;

        -- Per-song current band leaderboard projection. Song band leaderboard
        -- pages can read these rows instead of ranking band_entries at request time.
        CREATE TABLE IF NOT EXISTS current_band_leaderboard_entries (
            song_id               TEXT             NOT NULL,
            band_type             TEXT             NOT NULL,
            ranking_scope         TEXT             NOT NULL DEFAULT 'overall',
            scope_combo_id        TEXT             NOT NULL DEFAULT '',
            team_key              TEXT             NOT NULL,
            entry_combo_id        TEXT             NOT NULL DEFAULT '',
            entry_instrument_combo TEXT            NOT NULL DEFAULT '',
            team_members          TEXT[]           NOT NULL,
            member_account_ids    TEXT[]           NOT NULL DEFAULT ARRAY[]::TEXT[],
            member_instrument_ids INTEGER[]        NOT NULL DEFAULT ARRAY[]::INTEGER[],
            member_scores         INTEGER[]        NOT NULL DEFAULT ARRAY[]::INTEGER[],
            member_accuracies     INTEGER[]        NOT NULL DEFAULT ARRAY[]::INTEGER[],
            member_full_combos    INTEGER[]        NOT NULL DEFAULT ARRAY[]::INTEGER[],
            member_stars          INTEGER[]        NOT NULL DEFAULT ARRAY[]::INTEGER[],
            member_difficulties   INTEGER[]        NOT NULL DEFAULT ARRAY[]::INTEGER[],
            score                 INTEGER          NOT NULL,
            accuracy              INTEGER,
            is_full_combo         BOOLEAN,
            stars                 INTEGER,
            difficulty            INTEGER,
            season                INTEGER,
            rank                  INTEGER          NOT NULL DEFAULT 0,
            total_entries         INTEGER          NOT NULL DEFAULT 0,
            percentile            DOUBLE PRECISION NOT NULL DEFAULT 0,
            end_time              TEXT,
            first_seen_at         TIMESTAMPTZ      NOT NULL,
            last_updated_at       TIMESTAMPTZ      NOT NULL,
            projection_generation BIGINT           NOT NULL DEFAULT 0,
            computed_at           TIMESTAMPTZ      NOT NULL,
            PRIMARY KEY (song_id, band_type, ranking_scope, scope_combo_id, projection_generation, team_key)
        ) PARTITION BY LIST (band_type);

        DO $$
        DECLARE
            key_columns TEXT[];
        BEGIN
            SELECT array_agg(att.attname ORDER BY ord.ordinality)
            INTO key_columns
            FROM pg_constraint con
            JOIN unnest(con.conkey) WITH ORDINALITY AS ord(attnum, ordinality) ON TRUE
            JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = ord.attnum
            WHERE con.conrelid = 'current_band_leaderboard_entries'::regclass
              AND con.contype = 'p'
              AND con.conname = 'current_band_leaderboard_entries_pkey';

            IF key_columns IS NOT NULL AND key_columns <> ARRAY['song_id', 'band_type', 'ranking_scope', 'scope_combo_id', 'projection_generation', 'team_key'] THEN
                ALTER TABLE current_band_leaderboard_entries DROP CONSTRAINT current_band_leaderboard_entries_pkey;
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid = 'current_band_leaderboard_entries'::regclass
                  AND contype = 'p'
                  AND conname = 'current_band_leaderboard_entries_pkey'
            ) THEN
                ALTER TABLE current_band_leaderboard_entries
                    ADD CONSTRAINT current_band_leaderboard_entries_pkey
                    PRIMARY KEY (song_id, band_type, ranking_scope, scope_combo_id, projection_generation, team_key);
            END IF;
        END $$;

        CREATE TABLE IF NOT EXISTS current_band_leaderboard_entries_duets PARTITION OF current_band_leaderboard_entries FOR VALUES IN ('Band_Duets');
        CREATE TABLE IF NOT EXISTS current_band_leaderboard_entries_trios PARTITION OF current_band_leaderboard_entries FOR VALUES IN ('Band_Trios');
        CREATE TABLE IF NOT EXISTS current_band_leaderboard_entries_quad  PARTITION OF current_band_leaderboard_entries FOR VALUES IN ('Band_Quad');

        ALTER TABLE current_band_leaderboard_entries
            ADD COLUMN IF NOT EXISTS member_account_ids TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
            ADD COLUMN IF NOT EXISTS member_instrument_ids INTEGER[] NOT NULL DEFAULT ARRAY[]::INTEGER[],
            ADD COLUMN IF NOT EXISTS member_scores INTEGER[] NOT NULL DEFAULT ARRAY[]::INTEGER[],
            ADD COLUMN IF NOT EXISTS member_accuracies INTEGER[] NOT NULL DEFAULT ARRAY[]::INTEGER[],
            ADD COLUMN IF NOT EXISTS member_full_combos INTEGER[] NOT NULL DEFAULT ARRAY[]::INTEGER[],
            ADD COLUMN IF NOT EXISTS member_stars INTEGER[] NOT NULL DEFAULT ARRAY[]::INTEGER[],
            ADD COLUMN IF NOT EXISTS member_difficulties INTEGER[] NOT NULL DEFAULT ARRAY[]::INTEGER[];

        CREATE INDEX IF NOT EXISTS ix_cble_scope_rank
            ON current_band_leaderboard_entries (song_id, band_type, ranking_scope, scope_combo_id, rank);

        CREATE INDEX IF NOT EXISTS ix_cble_scope_generation_rank
            ON current_band_leaderboard_entries (song_id, band_type, ranking_scope, scope_combo_id, projection_generation, rank);

        CREATE INDEX IF NOT EXISTS ix_cble_team_song
            ON current_band_leaderboard_entries (band_type, team_key, song_id, ranking_scope, scope_combo_id);

        CREATE INDEX IF NOT EXISTS ix_cble_duets_team_scope_generation
            ON current_band_leaderboard_entries_duets (band_type, team_key, song_id, ranking_scope, scope_combo_id, projection_generation);
        CREATE INDEX IF NOT EXISTS ix_cble_trios_team_scope_generation
            ON current_band_leaderboard_entries_trios (band_type, team_key, song_id, ranking_scope, scope_combo_id, projection_generation);
        CREATE INDEX IF NOT EXISTS ix_cble_quad_team_scope_generation
            ON current_band_leaderboard_entries_quad (band_type, team_key, song_id, ranking_scope, scope_combo_id, projection_generation);

        CREATE TABLE IF NOT EXISTS band_current_projection_state (
            id                    BOOLEAN     PRIMARY KEY DEFAULT TRUE CHECK (id),
            current_generation    BIGINT      NOT NULL DEFAULT 0,
            row_count             BIGINT      NOT NULL DEFAULT 0,
            scope_count           BIGINT      NOT NULL DEFAULT 0,
            failed_scope_count    BIGINT      NOT NULL DEFAULT 0,
            full_rebuilt_at       TIMESTAMPTZ,
            last_scope_rebuilt_at TIMESTAMPTZ,
            updated_at            TIMESTAMPTZ NOT NULL
        );

        CREATE TABLE IF NOT EXISTS band_current_projection_scope (
            song_id               TEXT        NOT NULL,
            band_type             TEXT        NOT NULL,
            ranking_scope         TEXT        NOT NULL DEFAULT 'overall',
            scope_combo_id        TEXT        NOT NULL DEFAULT '',
            projection_generation BIGINT      NOT NULL DEFAULT 0,
            published_generation  BIGINT,
            row_count             BIGINT      NOT NULL DEFAULT 0,
            published_row_count   BIGINT      NOT NULL DEFAULT 0,
            status                TEXT        NOT NULL DEFAULT 'ready',
            error_message         TEXT,
            last_rebuilt_at       TIMESTAMPTZ,
            updated_at            TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (song_id, band_type, ranking_scope, scope_combo_id)
        );

        ALTER TABLE band_current_projection_scope
            ADD COLUMN IF NOT EXISTS published_generation BIGINT;

        ALTER TABLE band_current_projection_scope
            ADD COLUMN IF NOT EXISTS published_row_count BIGINT NOT NULL DEFAULT 0;

        UPDATE band_current_projection_scope scope
        SET published_generation = scope.projection_generation,
            published_row_count = scope.row_count
        WHERE scope.published_generation IS NULL
                    AND scope.status = 'ready';

        CREATE INDEX IF NOT EXISTS ix_bcps_status_updated
            ON band_current_projection_scope (status, updated_at DESC);

        CREATE INDEX IF NOT EXISTS ix_bcps_scope_ready
            ON band_current_projection_scope (band_type, ranking_scope, scope_combo_id, status);

        CREATE INDEX IF NOT EXISTS ix_bcps_scope_published
            ON band_current_projection_scope (band_type, ranking_scope, scope_combo_id, published_generation)
            WHERE published_generation IS NOT NULL;

        -- Aggregate band-team rankings are stored in per-band current tables.

        -- ── Migration: add instrument_combo column to existing band tables ──
        -- CREATE TABLE IF NOT EXISTS won't alter existing tables, so we add the
        -- column separately for databases created before instrument_combo was introduced.
        -- Must run AFTER all CREATE TABLE statements so tables exist on fresh init.
        DO $$ BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_name = 'band_entries' AND column_name = 'instrument_combo'
            ) THEN
                ALTER TABLE band_entries ADD COLUMN instrument_combo TEXT NOT NULL DEFAULT '';
                ALTER TABLE band_entries DROP CONSTRAINT IF EXISTS band_entries_pkey;
                ALTER TABLE band_entries ADD PRIMARY KEY (song_id, band_type, team_key, instrument_combo);
            END IF;
        END $$;

        DO $$ BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_name = 'band_member_stats' AND column_name = 'instrument_combo'
            ) THEN
                ALTER TABLE band_member_stats ADD COLUMN instrument_combo TEXT NOT NULL DEFAULT '';
                ALTER TABLE band_member_stats DROP CONSTRAINT IF EXISTS band_member_stats_pkey;
                ALTER TABLE band_member_stats ADD PRIMARY KEY (song_id, band_type, team_key, instrument_combo, member_index);
            END IF;
        END $$;

        DO $$ BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_name = 'band_members' AND column_name = 'instrument_combo'
            ) THEN
                ALTER TABLE band_members ADD COLUMN instrument_combo TEXT NOT NULL DEFAULT '';
                ALTER TABLE band_members DROP CONSTRAINT IF EXISTS band_members_pkey;
                ALTER TABLE band_members ADD PRIMARY KEY (account_id, song_id, band_type, team_key, instrument_combo);
            END IF;
        END $$;

        -- ix_be_combo removed 2026-04-23 (Phase 2): idx_scan=0 across all three
        -- band partitions forever. Combo lookups go through band_team_rankings_current.
        -- Saves ~1.1 GB.

        -- ── Migration: add band context columns to leaderboard_entries ──
        DO $$ BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_name = 'leaderboard_entries' AND column_name = 'band_members_json'
            ) THEN
                ALTER TABLE leaderboard_entries ADD COLUMN band_members_json JSONB;
                ALTER TABLE leaderboard_entries ADD COLUMN band_score INTEGER;
                ALTER TABLE leaderboard_entries ADD COLUMN base_score INTEGER;
                ALTER TABLE leaderboard_entries ADD COLUMN instrument_bonus INTEGER;
                ALTER TABLE leaderboard_entries ADD COLUMN overdrive_bonus INTEGER;
                ALTER TABLE leaderboard_entries ADD COLUMN instrument_combo TEXT;
            END IF;
        END $$;

        -- Index for post-scrape band extraction: find entries with band data
        CREATE INDEX IF NOT EXISTS ix_le_band_members
            ON leaderboard_entries (song_id, instrument)
            WHERE band_members_json IS NOT NULL;

        """;
}

internal sealed record DatabaseSchemaInitializationStep(
    string Name,
    string Sql,
    int CommandTimeoutSeconds,
    bool UseShortTransaction,
    string? LockTimeout,
    string? StatementTimeout,
    bool UseConcurrentIndex = false,
    string? ValidationSql = null,
    string? CleanupSql = null);
