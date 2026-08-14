namespace FSTService.Persistence;

internal static class MaxScoreMaintenanceSchema
{
    internal const string Purpose = "maintenance_max_score_correction_v1";
    internal const string Cause = "max_score_recompute";

    internal const string Sql = """
        CREATE TABLE IF NOT EXISTS max_score_maintenance_runs (
            manifest_sha256             TEXT        PRIMARY KEY
                CHECK (length(manifest_sha256) = 64),
            manifest_version            INTEGER     NOT NULL,
            plan_digest                 TEXT        NOT NULL
                CHECK (length(plan_digest) = 64),
            expected_published_scrape_id BIGINT      NOT NULL
                CHECK (expected_published_scrape_id > 0),
            expected_publication_id     BIGINT      NOT NULL
                CHECK (expected_publication_id > 0),
            expected_catalog_hash       TEXT        NOT NULL
                CHECK (length(expected_catalog_hash) = 64),
            expected_catalog_song_count INTEGER     NOT NULL
                CHECK (expected_catalog_song_count > 0),
            published_score_source_fingerprint TEXT NOT NULL
                CHECK (length(published_score_source_fingerprint) = 64),
            notification_state_fingerprint TEXT     NOT NULL
                CHECK (length(notification_state_fingerprint) = 64),
            rank_history_fingerprint     TEXT        NOT NULL
                CHECK (length(rank_history_fingerprint) = 64),
            manifest_json               JSONB       NOT NULL,
            freeze_reason               TEXT        NOT NULL UNIQUE,
            phase                       TEXT        NOT NULL
                CHECK (phase IN (
                    'freeze_established',
                    'rollback_captured',
                    'paths_promoted',
                    'derived_state_rebuilt',
                    'notifications_quarantined',
                    'caches_staged',
                    'validated',
                    'completed')),
            status                      TEXT        NOT NULL
                CHECK (status IN ('running', 'failed', 'completed')),
            CONSTRAINT ck_max_score_maintenance_terminal_state
                CHECK (
                    (phase = 'completed') =
                    (status = 'completed')),
            rollback_snapshot_path      TEXT,
            rollback_snapshot_sha256    TEXT
                CHECK (
                    rollback_snapshot_sha256 IS NULL
                    OR length(rollback_snapshot_sha256) = 64),
            notification_maintenance_run_id BIGINT,
            promoted_song_count         INTEGER     NOT NULL DEFAULT 0,
            rebuilt_instrument_count    INTEGER     NOT NULL DEFAULT 0,
            quarantined_candidate_count BIGINT      NOT NULL DEFAULT 0,
            visible_delivery_count      INTEGER     NOT NULL DEFAULT 0
                CHECK (visible_delivery_count = 0),
            staged_cache_entry_count    BIGINT      NOT NULL DEFAULT 0,
            failure_stage               TEXT,
            failure_detail              TEXT,
            created_at                  TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at                  TIMESTAMPTZ NOT NULL DEFAULT now(),
            completed_at                TIMESTAMPTZ
        );

        CREATE TABLE IF NOT EXISTS max_score_maintenance_rollback_songs (
            manifest_sha256             TEXT        NOT NULL
                REFERENCES max_score_maintenance_runs(manifest_sha256),
            song_id                     TEXT        NOT NULL,
            expected_catalog_last_modified TEXT     NOT NULL,
            path_generation_revision    BIGINT      NOT NULL,
            dat_file_hash               TEXT,
            song_last_modified          TEXT,
            paths_generated_at          TIMESTAMPTZ,
            chopt_version               TEXT,
            chopt_binary_sha256         TEXT,
            path_generation_profile     TEXT,
            path_artifact_generation_id TEXT,
            path_artifact_tree_sha256   TEXT,
            path_artifact_file_count    INTEGER,
            path_expected_instruments   TEXT[]      NOT NULL,
            max_lead_score              INTEGER,
            max_bass_score              INTEGER,
            max_drums_score             INTEGER,
            max_vocals_score            INTEGER,
            max_pro_lead_score          INTEGER,
            max_pro_bass_score          INTEGER,
            max_pro_cymbals_score       INTEGER,
            max_pro_drums_score         INTEGER,
            path_generation_pending     BOOLEAN     NOT NULL,
            CONSTRAINT ck_max_score_rollback_artifact_tree_sha256
                CHECK (
                    path_artifact_tree_sha256 IS NULL
                    OR length(path_artifact_tree_sha256) = 64),
            CONSTRAINT ck_max_score_rollback_artifact_file_count
                CHECK (
                    path_artifact_file_count IS NULL
                    OR path_artifact_file_count > 0),
            PRIMARY KEY (manifest_sha256, song_id)
        );

        ALTER TABLE max_score_maintenance_rollback_songs
            ADD COLUMN IF NOT EXISTS path_artifact_tree_sha256 TEXT;
        ALTER TABLE max_score_maintenance_rollback_songs
            ADD COLUMN IF NOT EXISTS path_artifact_file_count INTEGER;
        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                        'max_score_maintenance_rollback_songs'::regclass
                  AND conname =
                        'ck_max_score_rollback_artifact_tree_sha256'
            ) THEN
                ALTER TABLE max_score_maintenance_rollback_songs
                    ADD CONSTRAINT
                        ck_max_score_rollback_artifact_tree_sha256
                    CHECK (
                        path_artifact_tree_sha256 IS NULL
                        OR length(path_artifact_tree_sha256) = 64);
            END IF;
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                        'max_score_maintenance_rollback_songs'::regclass
                  AND conname =
                        'ck_max_score_rollback_artifact_file_count'
            ) THEN
                ALTER TABLE max_score_maintenance_rollback_songs
                    ADD CONSTRAINT
                        ck_max_score_rollback_artifact_file_count
                    CHECK (
                        path_artifact_file_count IS NULL
                        OR path_artifact_file_count > 0);
            END IF;
        END
        $$;

        CREATE INDEX IF NOT EXISTS
            ix_max_score_maintenance_runs_status
            ON max_score_maintenance_runs(status, updated_at DESC);

        CREATE OR REPLACE FUNCTION reject_max_score_maintenance_identity_change()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            IF ROW(
                NEW.manifest_sha256,
                NEW.manifest_version,
                NEW.plan_digest,
                NEW.expected_published_scrape_id,
                NEW.expected_publication_id,
                NEW.expected_catalog_hash,
                NEW.expected_catalog_song_count,
                NEW.published_score_source_fingerprint,
                NEW.notification_state_fingerprint,
                NEW.rank_history_fingerprint,
                NEW.manifest_json,
                NEW.freeze_reason)
               IS DISTINCT FROM
               ROW(
                OLD.manifest_sha256,
                OLD.manifest_version,
                OLD.plan_digest,
                OLD.expected_published_scrape_id,
                OLD.expected_publication_id,
                OLD.expected_catalog_hash,
                OLD.expected_catalog_song_count,
                OLD.published_score_source_fingerprint,
                OLD.notification_state_fingerprint,
                OLD.rank_history_fingerprint,
                OLD.manifest_json,
                OLD.freeze_reason)
            THEN
                RAISE EXCEPTION
                    'Max-score maintenance identity is immutable.'
                    USING ERRCODE = '55000';
            END IF;
            RETURN NEW;
        END
        $$;

        CREATE OR REPLACE FUNCTION reject_max_score_rollback_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            RAISE EXCEPTION
                'Max-score rollback evidence is immutable.'
                USING ERRCODE = '55000';
        END
        $$;

        CREATE OR REPLACE FUNCTION reject_improvement_notification_maintenance_delete()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            RAISE EXCEPTION
                'Improvement notification maintenance audit is immutable.'
                USING ERRCODE = '55000';
        END
        $$;

        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_trigger
                WHERE tgrelid =
                        'max_score_maintenance_runs'::regclass
                  AND tgname =
                        'trg_reject_max_score_maintenance_identity_change'
                  AND NOT tgisinternal
            ) THEN
                CREATE TRIGGER
                    trg_reject_max_score_maintenance_identity_change
                BEFORE UPDATE ON max_score_maintenance_runs
                FOR EACH ROW
                EXECUTE FUNCTION
                    reject_max_score_maintenance_identity_change();
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_trigger
                WHERE tgrelid =
                        'max_score_maintenance_rollback_songs'::regclass
                  AND tgname =
                        'trg_reject_max_score_rollback_mutation'
                  AND NOT tgisinternal
            ) THEN
                CREATE TRIGGER
                    trg_reject_max_score_rollback_mutation
                BEFORE UPDATE OR DELETE
                ON max_score_maintenance_rollback_songs
                FOR EACH ROW
                EXECUTE FUNCTION
                    reject_max_score_rollback_mutation();
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_trigger
                WHERE tgrelid =
                        'improvement_notification_maintenance_runs'::regclass
                  AND tgname =
                        'trg_reject_improvement_notification_maintenance_run_delete'
                  AND NOT tgisinternal
            ) THEN
                CREATE TRIGGER
                    trg_reject_improvement_notification_maintenance_run_delete
                BEFORE DELETE
                ON improvement_notification_maintenance_runs
                FOR EACH ROW
                EXECUTE FUNCTION
                    reject_improvement_notification_maintenance_delete();
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_trigger
                WHERE tgrelid =
                        'improvement_notification_maintenance_candidates'::regclass
                  AND tgname =
                        'trg_reject_improvement_notification_maintenance_candidate_mutation'
                  AND NOT tgisinternal
            ) THEN
                CREATE TRIGGER
                    trg_reject_improvement_notification_maintenance_candidate_mutation
                BEFORE UPDATE OR DELETE
                ON improvement_notification_maintenance_candidates
                FOR EACH ROW
                EXECUTE FUNCTION
                    reject_improvement_notification_maintenance_delete();
            END IF;
        END
        $$;

        DO $$
        DECLARE
            constraint_name TEXT;
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint constraint_row
                WHERE constraint_row.conrelid =
                        'improvement_notification_maintenance_runs'::regclass
                  AND constraint_row.conname =
                        'ck_improvement_notification_maintenance_runs_purpose'
                  AND pg_get_constraintdef(constraint_row.oid)
                        LIKE '%maintenance_max_score_correction_v1%'
            ) THEN
                FOR constraint_name IN
                    SELECT constraint_row.conname
                    FROM pg_constraint constraint_row
                    JOIN pg_attribute attribute
                      ON attribute.attrelid =
                            constraint_row.conrelid
                     AND attribute.attnum =
                            ANY(constraint_row.conkey)
                    WHERE constraint_row.conrelid =
                        'improvement_notification_maintenance_runs'::regclass
                      AND constraint_row.contype = 'c'
                      AND attribute.attname =
                            'notification_purpose'
                LOOP
                    EXECUTE format(
                        'ALTER TABLE improvement_notification_maintenance_runs DROP CONSTRAINT %I',
                        constraint_name);
                END LOOP;
                EXECUTE
                    'ALTER TABLE improvement_notification_maintenance_runs ' ||
                    'ADD CONSTRAINT ck_improvement_notification_maintenance_runs_purpose ' ||
                    'CHECK (notification_purpose IN (' ||
                    '''maintenance_pro_lead_max_score_repair_v1'', ' ||
                    '''maintenance_max_score_correction_v1'')) NOT VALID';
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint constraint_row
                WHERE constraint_row.conrelid =
                        'improvement_notification_maintenance_candidates'::regclass
                  AND constraint_row.conname =
                        'ck_improvement_notification_maintenance_candidates_purpose'
                  AND pg_get_constraintdef(constraint_row.oid)
                        LIKE '%maintenance_max_score_correction_v1%'
            ) THEN
                FOR constraint_name IN
                    SELECT constraint_row.conname
                    FROM pg_constraint constraint_row
                    JOIN pg_attribute attribute
                      ON attribute.attrelid =
                            constraint_row.conrelid
                     AND attribute.attnum =
                            ANY(constraint_row.conkey)
                    WHERE constraint_row.conrelid =
                        'improvement_notification_maintenance_candidates'::regclass
                      AND constraint_row.contype = 'c'
                      AND attribute.attname =
                            'notification_purpose'
                LOOP
                    EXECUTE format(
                        'ALTER TABLE improvement_notification_maintenance_candidates DROP CONSTRAINT %I',
                        constraint_name);
                END LOOP;
                EXECUTE
                    'ALTER TABLE improvement_notification_maintenance_candidates ' ||
                    'ADD CONSTRAINT ck_improvement_notification_maintenance_candidates_purpose ' ||
                    'CHECK (notification_purpose IN (' ||
                    '''maintenance_pro_lead_max_score_repair_v1'', ' ||
                    '''maintenance_max_score_correction_v1'')) NOT VALID';
            END IF;
        END
        $$;

        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                    'improvement_notification_maintenance_runs'::regclass
                  AND conname =
                    'ck_improvement_notification_maintenance_runs_purpose'
                  AND NOT convalidated
            ) THEN
                ALTER TABLE improvement_notification_maintenance_runs
                    VALIDATE CONSTRAINT
                        ck_improvement_notification_maintenance_runs_purpose;
            END IF;

            IF EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                    'improvement_notification_maintenance_candidates'::regclass
                  AND conname =
                    'ck_improvement_notification_maintenance_candidates_purpose'
                  AND NOT convalidated
            ) THEN
                ALTER TABLE improvement_notification_maintenance_candidates
                    VALIDATE CONSTRAINT
                        ck_improvement_notification_maintenance_candidates_purpose;
            END IF;
        END
        $$;
        """;
}
