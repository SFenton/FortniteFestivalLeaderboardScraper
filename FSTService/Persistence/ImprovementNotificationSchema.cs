namespace FSTService.Persistence;

public static class ImprovementNotificationSchema
{
    public const string Sql = """

        -- =====================================================================
        -- IMPROVEMENT NOTIFICATIONS
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS improvement_detection_runs (
            run_id                       BIGSERIAL PRIMARY KEY,
            published_scrape_id          INTEGER     REFERENCES scrape_log(id) ON DELETE SET NULL,
            started_at                   TIMESTAMPTZ NOT NULL DEFAULT now(),
            completed_at                 TIMESTAMPTZ,
            status                       TEXT        NOT NULL DEFAULT 'running',
            scope                        TEXT        NOT NULL DEFAULT 'registered',
            mode                         TEXT        NOT NULL DEFAULT 'dry-run',
            source                       TEXT        NOT NULL DEFAULT 'precompute',
            notification_purpose         TEXT        NOT NULL DEFAULT 'routine_score_observation_v1',
            notification_cause           TEXT        NOT NULL DEFAULT 'score_observation',
            delivery_state               TEXT        NOT NULL DEFAULT 'visible',
            baseline_only                BOOLEAN     NOT NULL DEFAULT false,
            include_players              BOOLEAN     NOT NULL DEFAULT true,
            include_bands                BOOLEAN     NOT NULL DEFAULT true,
            include_song_events          BOOLEAN     NOT NULL DEFAULT true,
            include_rankings             BOOLEAN     NOT NULL DEFAULT true,
            prune_expired                BOOLEAN     NOT NULL DEFAULT true,
            player_song_rows_scanned     BIGINT      NOT NULL DEFAULT 0,
            player_song_events_inserted  BIGINT      NOT NULL DEFAULT 0,
            player_song_state_upserts    BIGINT      NOT NULL DEFAULT 0,
            player_rank_rows_scanned     BIGINT      NOT NULL DEFAULT 0,
            player_rank_events_inserted  BIGINT      NOT NULL DEFAULT 0,
            player_rank_state_upserts    BIGINT      NOT NULL DEFAULT 0,
            band_subjects_upserted       BIGINT      NOT NULL DEFAULT 0,
            band_song_rows_scanned       BIGINT      NOT NULL DEFAULT 0,
            band_song_events_inserted    BIGINT      NOT NULL DEFAULT 0,
            band_song_state_upserts      BIGINT      NOT NULL DEFAULT 0,
            band_rank_rows_scanned       BIGINT      NOT NULL DEFAULT 0,
            band_rank_events_inserted    BIGINT      NOT NULL DEFAULT 0,
            band_rank_state_upserts      BIGINT      NOT NULL DEFAULT 0,
            expired_player_events_deleted BIGINT     NOT NULL DEFAULT 0,
            expired_band_events_deleted  BIGINT      NOT NULL DEFAULT 0,
            player_song_baseline_rows    BIGINT      NOT NULL DEFAULT 0,
            player_rank_baseline_rows    BIGINT      NOT NULL DEFAULT 0,
            band_song_baseline_rows      BIGINT      NOT NULL DEFAULT 0,
            band_rank_baseline_rows      BIGINT      NOT NULL DEFAULT 0,
            error_message                TEXT
        );

        ALTER TABLE improvement_detection_runs
            ADD COLUMN IF NOT EXISTS source TEXT NOT NULL DEFAULT 'precompute';
        ALTER TABLE improvement_detection_runs
            ADD COLUMN IF NOT EXISTS published_scrape_id INTEGER REFERENCES scrape_log(id) ON DELETE SET NULL;
        ALTER TABLE improvement_detection_runs
            ADD COLUMN IF NOT EXISTS player_song_baseline_rows BIGINT NOT NULL DEFAULT 0;
        ALTER TABLE improvement_detection_runs
            ADD COLUMN IF NOT EXISTS player_rank_baseline_rows BIGINT NOT NULL DEFAULT 0;
        ALTER TABLE improvement_detection_runs
            ADD COLUMN IF NOT EXISTS band_song_baseline_rows BIGINT NOT NULL DEFAULT 0;
        ALTER TABLE improvement_detection_runs
            ADD COLUMN IF NOT EXISTS band_rank_baseline_rows BIGINT NOT NULL DEFAULT 0;
        CREATE INDEX IF NOT EXISTS ix_improvement_detection_runs_published_scrape
            ON improvement_detection_runs (published_scrape_id, completed_at DESC)
            WHERE status = 'completed';

        ALTER TABLE scrape_publication_state
            ADD COLUMN IF NOT EXISTS improvement_notifications_scrape_id INTEGER REFERENCES scrape_log(id);
        ALTER TABLE scrape_publication_state
            ADD COLUMN IF NOT EXISTS improvement_notifications_status TEXT;
        ALTER TABLE scrape_publication_state
            ADD COLUMN IF NOT EXISTS improvement_notifications_attempt_count INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE scrape_publication_state
            ADD COLUMN IF NOT EXISTS improvement_notifications_started_at TIMESTAMPTZ;
        ALTER TABLE scrape_publication_state
            ADD COLUMN IF NOT EXISTS improvement_notifications_completed_at TIMESTAMPTZ;
        ALTER TABLE scrape_publication_state
            ADD COLUMN IF NOT EXISTS improvement_notifications_error TEXT;
        ALTER TABLE scrape_publication_state
            ADD COLUMN IF NOT EXISTS improvement_notifications_projection_scopes JSONB NOT NULL DEFAULT '[]'::jsonb;
        ALTER TABLE scrape_publication_state
            ADD COLUMN IF NOT EXISTS improvement_notifications_projection_ready BOOLEAN NOT NULL DEFAULT false;
        ALTER TABLE scrape_publication_state
            ADD COLUMN IF NOT EXISTS improvement_notifications_projection_scrape_id INTEGER REFERENCES scrape_log(id);

        UPDATE scrape_publication_state
        SET improvement_notifications_projection_scopes = '[]'::jsonb,
            improvement_notifications_projection_ready = true,
            improvement_notifications_projection_scrape_id = published_scrape_id
        WHERE improvement_notifications_status = 'completed'
          AND improvement_notifications_scrape_id = published_scrape_id
          AND (
              NOT improvement_notifications_projection_ready
              OR improvement_notifications_projection_scrape_id IS DISTINCT FROM published_scrape_id
          );

        UPDATE scrape_publication_state
        SET improvement_notifications_scrape_id = NULL,
            improvement_notifications_projection_scopes = '[]'::jsonb,
            improvement_notifications_projection_ready = false,
            improvement_notifications_projection_scrape_id = NULL
        WHERE improvement_notifications_status = 'disabled';

        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid = 'scrape_publication_state'::regclass
                  AND conname = 'ck_scrape_publication_notification_plan'
            ) THEN
                ALTER TABLE scrape_publication_state
                ADD CONSTRAINT ck_scrape_publication_notification_plan
                CHECK (
                    improvement_notifications_status IS NULL
                    OR (
                        improvement_notifications_status = 'disabled'
                        AND improvement_notifications_scrape_id IS NULL
                        AND NOT improvement_notifications_projection_ready
                        AND improvement_notifications_projection_scrape_id IS NULL
                    )
                    OR (
                        improvement_notifications_status IN ('pending', 'running', 'failed', 'completed')
                        AND published_scrape_id IS NOT NULL
                        AND improvement_notifications_scrape_id IS NOT NULL
                        AND improvement_notifications_projection_scrape_id IS NOT NULL
                        AND improvement_notifications_scrape_id = published_scrape_id
                        AND improvement_notifications_projection_ready
                        AND improvement_notifications_projection_scrape_id = published_scrape_id
                    )
                ) NOT VALID;
            END IF;
        END $$;

        CREATE TABLE IF NOT EXISTS player_improvement_state (
            account_id       TEXT        NOT NULL,
            song_id          TEXT        NOT NULL,
            instrument       TEXT        NOT NULL,
            score            INTEGER,
            rank             INTEGER,
            stars            INTEGER,
            is_full_combo    BOOLEAN,
            difficulty       INTEGER,
            percentile       REAL,
            season           INTEGER,
            first_seen_at    TIMESTAMPTZ,
            last_updated_at  TIMESTAMPTZ,
            observed_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (account_id, song_id, instrument)
        );

        CREATE TABLE IF NOT EXISTS player_rank_improvement_state (
            account_id              TEXT        NOT NULL,
            instrument              TEXT        NOT NULL,
            adjusted_skill_rank     INTEGER,
            weighted_rank           INTEGER,
            fc_rate_rank            INTEGER,
            total_score_rank        INTEGER,
            max_score_percent_rank  INTEGER,
            total_score             BIGINT,
            full_combo_count        INTEGER,
            computed_at             TIMESTAMPTZ,
            observed_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (account_id, instrument)
        );

        CREATE TABLE IF NOT EXISTS player_improvement_events (
            event_id        BIGSERIAL PRIMARY KEY,
            notification_guid UUID     NOT NULL DEFAULT gen_random_uuid(),
            run_id          BIGINT REFERENCES improvement_detection_runs(run_id) ON DELETE SET NULL,
            account_id      TEXT        NOT NULL,
            event_kind      TEXT        NOT NULL,
            song_id         TEXT,
            instrument      TEXT,
            metric          TEXT,
            old_numeric     NUMERIC,
            new_numeric     NUMERIC,
            old_rank        INTEGER,
            new_rank        INTEGER,
            payload         JSONB       NOT NULL DEFAULT '{}'::jsonb,
            detected_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
            expires_at      TIMESTAMPTZ NOT NULL,
            source          TEXT        NOT NULL DEFAULT 'precompute',
            notification_purpose TEXT   NOT NULL DEFAULT 'routine_score_observation_v1',
            notification_cause   TEXT   NOT NULL DEFAULT 'score_observation',
            delivery_state       TEXT   NOT NULL DEFAULT 'visible'
        );

        ALTER TABLE player_improvement_events
            ADD COLUMN IF NOT EXISTS notification_guid UUID;
        UPDATE player_improvement_events
            SET notification_guid = gen_random_uuid()
            WHERE notification_guid IS NULL;
        ALTER TABLE player_improvement_events
            ALTER COLUMN notification_guid SET DEFAULT gen_random_uuid(),
            ALTER COLUMN notification_guid SET NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_player_improvement_events_subject_live
            ON player_improvement_events (account_id, expires_at DESC, detected_at DESC);
        CREATE INDEX IF NOT EXISTS ix_player_improvement_events_expiry
            ON player_improvement_events (expires_at);
        CREATE INDEX IF NOT EXISTS ix_player_improvement_events_kind
            ON player_improvement_events (event_kind, detected_at DESC);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_player_improvement_events_notification_guid
            ON player_improvement_events (notification_guid);

        CREATE TABLE IF NOT EXISTS band_improvement_subjects (
            band_subject_id BIGSERIAL PRIMARY KEY,
            band_type       TEXT        NOT NULL,
            team_key        TEXT        NOT NULL,
            team_members    TEXT[]      NOT NULL DEFAULT ARRAY[]::TEXT[],
            first_seen_at   TIMESTAMPTZ,
            last_seen_at    TIMESTAMPTZ,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
            UNIQUE (band_type, team_key)
        );

        CREATE INDEX IF NOT EXISTS ix_band_improvement_subjects_team_key
            ON band_improvement_subjects (team_key);

        CREATE TABLE IF NOT EXISTS band_improvement_state (
            band_subject_id        BIGINT      NOT NULL REFERENCES band_improvement_subjects(band_subject_id) ON DELETE CASCADE,
            song_id                TEXT        NOT NULL,
            ranking_scope          TEXT        NOT NULL DEFAULT 'overall',
            scope_combo_id         TEXT        NOT NULL DEFAULT '',
            entry_combo_id         TEXT,
            entry_instrument_combo TEXT,
            score                  INTEGER,
            rank                   INTEGER,
            stars                  INTEGER,
            is_full_combo          BOOLEAN,
            difficulty             INTEGER,
            percentile             DOUBLE PRECISION,
            season                 INTEGER,
            total_entries          INTEGER,
            first_seen_at          TIMESTAMPTZ,
            last_updated_at        TIMESTAMPTZ,
            observed_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (band_subject_id, song_id, ranking_scope, scope_combo_id)
        );

        CREATE INDEX IF NOT EXISTS ix_band_improvement_state_song
            ON band_improvement_state (song_id, ranking_scope, scope_combo_id);

        CREATE TABLE IF NOT EXISTS band_rank_improvement_state (
            band_subject_id       BIGINT      NOT NULL REFERENCES band_improvement_subjects(band_subject_id) ON DELETE CASCADE,
            ranking_scope         TEXT        NOT NULL DEFAULT 'overall',
            combo_id              TEXT        NOT NULL DEFAULT '',
            adjusted_skill_rank   INTEGER,
            weighted_rank         INTEGER,
            fc_rate_rank          INTEGER,
            total_score_rank      INTEGER,
            total_score           BIGINT,
            full_combo_count      INTEGER,
            computed_at           TIMESTAMPTZ,
            observed_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (band_subject_id, ranking_scope, combo_id)
        );

        CREATE INDEX IF NOT EXISTS ix_band_rank_improvement_state_scope
            ON band_rank_improvement_state (ranking_scope, combo_id);

        CREATE TABLE IF NOT EXISTS band_improvement_events (
            event_id        BIGSERIAL PRIMARY KEY,
            notification_guid UUID     NOT NULL DEFAULT gen_random_uuid(),
            run_id          BIGINT REFERENCES improvement_detection_runs(run_id) ON DELETE SET NULL,
            band_subject_id BIGINT      NOT NULL REFERENCES band_improvement_subjects(band_subject_id) ON DELETE CASCADE,
            event_kind      TEXT        NOT NULL,
            song_id         TEXT,
            ranking_scope   TEXT        NOT NULL DEFAULT 'overall',
            combo_id        TEXT        NOT NULL DEFAULT '',
            metric          TEXT,
            old_numeric     NUMERIC,
            new_numeric     NUMERIC,
            old_rank        INTEGER,
            new_rank        INTEGER,
            payload         JSONB       NOT NULL DEFAULT '{}'::jsonb,
            detected_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
            expires_at      TIMESTAMPTZ NOT NULL,
            source          TEXT        NOT NULL DEFAULT 'precompute',
            notification_purpose TEXT   NOT NULL DEFAULT 'routine_score_observation_v1',
            notification_cause   TEXT   NOT NULL DEFAULT 'score_observation',
            delivery_state       TEXT   NOT NULL DEFAULT 'visible'
        );

        ALTER TABLE band_improvement_events
            ADD COLUMN IF NOT EXISTS notification_guid UUID;
        UPDATE band_improvement_events
            SET notification_guid = gen_random_uuid()
            WHERE notification_guid IS NULL;
        ALTER TABLE band_improvement_events
            ALTER COLUMN notification_guid SET DEFAULT gen_random_uuid(),
            ALTER COLUMN notification_guid SET NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_band_improvement_events_subject_live
            ON band_improvement_events (band_subject_id, ranking_scope, combo_id, expires_at DESC, detected_at DESC);
        CREATE INDEX IF NOT EXISTS ix_band_improvement_events_expiry
            ON band_improvement_events (expires_at);
        CREATE INDEX IF NOT EXISTS ix_band_improvement_events_kind
            ON band_improvement_events (event_kind, detected_at DESC);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_band_improvement_events_notification_guid
            ON band_improvement_events (notification_guid);

        CREATE TABLE IF NOT EXISTS service_notifications (
            event_id          BIGSERIAL PRIMARY KEY,
            notification_guid UUID        NOT NULL DEFAULT gen_random_uuid(),
            notification_kind TEXT        NOT NULL,
            song_id           TEXT        NOT NULL,
            title             TEXT        NOT NULL,
            artist            TEXT        NOT NULL,
            album_art         TEXT,
            payload           JSONB       NOT NULL DEFAULT '{}'::jsonb,
            detected_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
            expires_at        TIMESTAMPTZ NOT NULL,
            source            TEXT        NOT NULL DEFAULT 'item_shop',
            source_key        TEXT        NOT NULL,
            created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
            notification_purpose TEXT     NOT NULL DEFAULT 'routine_item_shop_observation_v1',
            notification_cause   TEXT     NOT NULL DEFAULT 'item_shop_observation',
            delivery_state       TEXT     NOT NULL DEFAULT 'visible'
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_service_notifications_notification_guid
            ON service_notifications (notification_guid);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_service_notifications_kind_song_source
            ON service_notifications (notification_kind, song_id, source_key);
        CREATE INDEX IF NOT EXISTS ix_service_notifications_live
            ON service_notifications (expires_at DESC, detected_at DESC);
        CREATE INDEX IF NOT EXISTS ix_service_notifications_kind
            ON service_notifications (notification_kind, detected_at DESC);

        ALTER TABLE improvement_detection_runs
            ADD COLUMN IF NOT EXISTS notification_purpose TEXT NOT NULL DEFAULT 'routine_score_observation_v1';
        ALTER TABLE improvement_detection_runs
            ADD COLUMN IF NOT EXISTS notification_cause TEXT NOT NULL DEFAULT 'score_observation';
        ALTER TABLE improvement_detection_runs
            ADD COLUMN IF NOT EXISTS delivery_state TEXT NOT NULL DEFAULT 'visible';
        ALTER TABLE player_improvement_events
            ADD COLUMN IF NOT EXISTS notification_purpose TEXT NOT NULL DEFAULT 'routine_score_observation_v1';
        ALTER TABLE player_improvement_events
            ADD COLUMN IF NOT EXISTS notification_cause TEXT NOT NULL DEFAULT 'score_observation';
        ALTER TABLE player_improvement_events
            ADD COLUMN IF NOT EXISTS delivery_state TEXT NOT NULL DEFAULT 'visible';
        ALTER TABLE band_improvement_events
            ADD COLUMN IF NOT EXISTS notification_purpose TEXT NOT NULL DEFAULT 'routine_score_observation_v1';
        ALTER TABLE band_improvement_events
            ADD COLUMN IF NOT EXISTS notification_cause TEXT NOT NULL DEFAULT 'score_observation';
        ALTER TABLE band_improvement_events
            ADD COLUMN IF NOT EXISTS delivery_state TEXT NOT NULL DEFAULT 'visible';
        ALTER TABLE service_notifications
            ADD COLUMN IF NOT EXISTS notification_purpose TEXT NOT NULL DEFAULT 'routine_item_shop_observation_v1';
        ALTER TABLE service_notifications
            ADD COLUMN IF NOT EXISTS notification_cause TEXT NOT NULL DEFAULT 'item_shop_observation';
        ALTER TABLE service_notifications
            ADD COLUMN IF NOT EXISTS delivery_state TEXT NOT NULL DEFAULT 'visible';

        CREATE TABLE IF NOT EXISTS improvement_notification_maintenance_runs (
            maintenance_run_id             BIGSERIAL   PRIMARY KEY,
            notification_purpose           TEXT        NOT NULL
                CHECK (notification_purpose = 'maintenance_pro_lead_max_score_repair_v1'),
            notification_cause             TEXT        NOT NULL
                CHECK (notification_cause = 'max_score_recompute'),
            delivery_state                 TEXT        NOT NULL DEFAULT 'quarantined'
                CHECK (delivery_state = 'quarantined'),
            published_scrape_id            INTEGER     NOT NULL
                CHECK (published_scrape_id > 0),
            dry_run_digest                 TEXT        NOT NULL CHECK (length(dry_run_digest) = 64),
            canonical_candidate_data       TEXT        NOT NULL,
            repair_manifest                JSONB       NOT NULL,
            total_charted_songs            INTEGER     NOT NULL
                CHECK (total_charted_songs > 0),
            status                         TEXT        NOT NULL DEFAULT 'completed',
            candidate_count                BIGINT      NOT NULL DEFAULT 0,
            allowed_candidate_count        BIGINT      NOT NULL DEFAULT 0,
            external_routine_candidate_count BIGINT    NOT NULL DEFAULT 0,
            rejected_candidate_count       BIGINT      NOT NULL DEFAULT 0,
            quarantined_candidate_count    BIGINT      NOT NULL DEFAULT 0,
            player_rank_state_rows_updated BIGINT      NOT NULL DEFAULT 0,
            visible_delivery_cap           INTEGER     NOT NULL DEFAULT 0
                CHECK (visible_delivery_cap = 0),
            visible_delivery_count         INTEGER     NOT NULL DEFAULT 0
                CHECK (visible_delivery_count = 0),
            started_at                     TIMESTAMPTZ NOT NULL DEFAULT now(),
            completed_at                   TIMESTAMPTZ NOT NULL DEFAULT now(),
            UNIQUE (notification_purpose, published_scrape_id, dry_run_digest)
        );

        DO $$
        DECLARE
            fk_name TEXT;
        BEGIN
            FOR fk_name IN
                SELECT constraint_row.conname
                FROM pg_constraint constraint_row
                JOIN pg_attribute attribute
                  ON attribute.attrelid = constraint_row.conrelid
                 AND attribute.attnum = ANY(constraint_row.conkey)
                WHERE constraint_row.conrelid =
                    'improvement_notification_maintenance_runs'::regclass
                  AND constraint_row.contype = 'f'
                  AND attribute.attname = 'published_scrape_id'
            LOOP
                EXECUTE format(
                    'ALTER TABLE improvement_notification_maintenance_runs ' ||
                    'DROP CONSTRAINT %I',
                    fk_name);
            END LOOP;
        END $$;

        ALTER TABLE improvement_notification_maintenance_runs
            ALTER COLUMN published_scrape_id SET NOT NULL;
        ALTER TABLE improvement_notification_maintenance_runs
            ADD COLUMN IF NOT EXISTS repair_manifest JSONB
                NOT NULL DEFAULT '{}'::jsonb;
        ALTER TABLE improvement_notification_maintenance_runs
            ADD COLUMN IF NOT EXISTS total_charted_songs INTEGER
                NOT NULL DEFAULT 0;

        CREATE OR REPLACE FUNCTION reject_maintenance_scrape_provenance_change()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            IF NEW.published_scrape_id IS DISTINCT FROM OLD.published_scrape_id
            THEN
                RAISE EXCEPTION
                    'Maintenance audit published scrape provenance is immutable.'
                    USING ERRCODE = '55000';
            END IF;

            RETURN NEW;
        END
        $$;

        DROP TRIGGER IF EXISTS
            trg_reject_maintenance_scrape_provenance_change
            ON improvement_notification_maintenance_runs;
        CREATE TRIGGER trg_reject_maintenance_scrape_provenance_change
        BEFORE UPDATE OF published_scrape_id
        ON improvement_notification_maintenance_runs
        FOR EACH ROW
        EXECUTE FUNCTION reject_maintenance_scrape_provenance_change();

        CREATE TABLE IF NOT EXISTS improvement_notification_maintenance_candidates (
            maintenance_run_id   BIGINT      NOT NULL
                REFERENCES improvement_notification_maintenance_runs(maintenance_run_id)
                ON DELETE CASCADE,
            candidate_key        TEXT        NOT NULL CHECK (length(candidate_key) = 64),
            notification_purpose TEXT        NOT NULL
                CHECK (notification_purpose = 'maintenance_pro_lead_max_score_repair_v1'),
            notification_cause   TEXT        NOT NULL
                CHECK (notification_cause = 'max_score_recompute'),
            delivery_state       TEXT        NOT NULL DEFAULT 'quarantined'
                CHECK (delivery_state = 'quarantined'),
            subject_type         TEXT        NOT NULL,
            subject_key          TEXT        NOT NULL,
            instrument           TEXT,
            song_id              TEXT,
            scope_key            TEXT,
            candidate_kind       TEXT        NOT NULL,
            metric               TEXT        NOT NULL,
            old_numeric          NUMERIC,
            new_numeric          NUMERIC,
            old_rank             INTEGER,
            new_rank             INTEGER,
            classification       TEXT        NOT NULL,
            allowed              BOOLEAN     NOT NULL,
            payload              JSONB       NOT NULL DEFAULT '{}'::jsonb,
            created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (maintenance_run_id, candidate_key)
        );
        """;
}