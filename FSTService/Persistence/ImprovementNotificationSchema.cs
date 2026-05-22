namespace FSTService.Persistence;

public static class ImprovementNotificationSchema
{
    public const string Sql = """

        -- =====================================================================
        -- IMPROVEMENT NOTIFICATIONS
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS improvement_detection_runs (
            run_id                       BIGSERIAL PRIMARY KEY,
            started_at                   TIMESTAMPTZ NOT NULL DEFAULT now(),
            completed_at                 TIMESTAMPTZ,
            status                       TEXT        NOT NULL DEFAULT 'running',
            scope                        TEXT        NOT NULL DEFAULT 'registered',
            mode                         TEXT        NOT NULL DEFAULT 'dry-run',
            source                       TEXT        NOT NULL DEFAULT 'precompute',
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
            error_message                TEXT
        );

        ALTER TABLE improvement_detection_runs
            ADD COLUMN IF NOT EXISTS source TEXT NOT NULL DEFAULT 'precompute';

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
            source          TEXT        NOT NULL DEFAULT 'precompute'
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
            source          TEXT        NOT NULL DEFAULT 'precompute'
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
            created_at        TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_service_notifications_notification_guid
            ON service_notifications (notification_guid);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_service_notifications_kind_song_source
            ON service_notifications (notification_kind, song_id, source_key);
        CREATE INDEX IF NOT EXISTS ix_service_notifications_live
            ON service_notifications (expires_at DESC, detected_at DESC);
        CREATE INDEX IF NOT EXISTS ix_service_notifications_kind
            ON service_notifications (notification_kind, detected_at DESC);
        """;
}