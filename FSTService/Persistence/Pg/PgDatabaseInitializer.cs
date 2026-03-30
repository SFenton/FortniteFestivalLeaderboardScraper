using Npgsql;

namespace FSTService.Persistence.Pg;

/// <summary>
/// Creates the PostgreSQL schema for FSTService.
/// All statements are idempotent (IF NOT EXISTS).
/// Consolidates 8 SQLite databases into a single PostgreSQL database.
/// </summary>
public static class PgDatabaseInitializer
{
    public static async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Complete DDL ──────────────────────────────────────────────────────

    private const string Schema = """

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
            -- Path generation fields (from PathDataStore)
            max_lead_score       INTEGER,
            max_bass_score       INTEGER,
            max_drums_score      INTEGER,
            max_vocals_score     INTEGER,
            max_pro_lead_score   INTEGER,
            max_pro_bass_score   INTEGER,
            dat_file_hash        TEXT,
            song_last_modified   TEXT,
            paths_generated_at   TIMESTAMPTZ,
            chopt_version        TEXT
        );

        -- =====================================================================
        -- LEADERBOARD ENTRIES (consolidated from 6 × fst-{instrument}.db)
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
            first_seen_at  TIMESTAMPTZ NOT NULL,
            last_updated_at TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (song_id, instrument, account_id)
        );

        CREATE INDEX IF NOT EXISTS ix_le_song_score
            ON leaderboard_entries (song_id, instrument, score DESC);
        CREATE INDEX IF NOT EXISTS ix_le_account
            ON leaderboard_entries (account_id, instrument);
        CREATE INDEX IF NOT EXISTS ix_le_account_song
            ON leaderboard_entries (account_id, song_id, instrument);
        CREATE INDEX IF NOT EXISTS ix_le_song_source
            ON leaderboard_entries (song_id, instrument, source);
        CREATE INDEX IF NOT EXISTS ix_le_song_rank
            ON leaderboard_entries (song_id, instrument, rank);

        -- =====================================================================
        -- SONG STATS (consolidated from 6 × instrument DBs)
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
        );

        -- =====================================================================
        -- ACCOUNT RANKINGS (consolidated from 6 × instrument DBs)
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
        );

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

        -- =====================================================================
        -- RANK HISTORY (consolidated from 6 × instrument DBs)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS rank_history (
            account_id              TEXT        NOT NULL,
            instrument              TEXT        NOT NULL,
            snapshot_date           DATE        NOT NULL,
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
        );

        -- =====================================================================
        -- VALID SCORE OVERRIDES (consolidated from 6 × instrument DBs)
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
        );

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
            total_bytes     BIGINT
        );

        CREATE INDEX IF NOT EXISTS ix_scrapelog_completed
            ON scrape_log (id DESC) WHERE completed_at IS NOT NULL;

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
        CREATE UNIQUE INDEX IF NOT EXISTS ix_sh_dedup
            ON score_history (account_id, song_id, instrument, new_score, score_achieved_at);

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
        CREATE INDEX IF NOT EXISTS ix_an_name
            ON account_names (display_name) WHERE display_name IS NOT NULL;

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
            PRIMARY KEY (device_id, account_id)
        );

        CREATE INDEX IF NOT EXISTS ix_reg_account
            ON registered_users (account_id);

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
            error_message        TEXT
        );

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
            error_message            TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_hr_status
            ON history_recon_status (status);

        -- =====================================================================
        -- HISTORY RECON PROGRESS (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS history_recon_progress (
            account_id  TEXT    NOT NULL,
            song_id     TEXT    NOT NULL,
            instrument  TEXT    NOT NULL,
            processed   INTEGER NOT NULL DEFAULT 0,
            processed_at TIMESTAMPTZ,
            PRIMARY KEY (account_id, song_id, instrument)
        );

        CREATE INDEX IF NOT EXISTS ix_hrp_account
            ON history_recon_progress (account_id);

        -- =====================================================================
        -- SEASON WINDOWS (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS season_windows (
            season_number INTEGER PRIMARY KEY,
            event_id      TEXT        NOT NULL,
            window_id     TEXT        NOT NULL,
            discovered_at TIMESTAMPTZ NOT NULL
        );

        -- =====================================================================
        -- SONG FIRST SEEN SEASON (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS song_first_seen_season (
            song_id            TEXT    PRIMARY KEY,
            first_seen_season  INTEGER,
            min_observed_season INTEGER,
            estimated_season   INTEGER NOT NULL,
            probe_result       TEXT,
            calculated_at      TIMESTAMPTZ NOT NULL
        );

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
            started_at               TIMESTAMPTZ,
            completed_at             TIMESTAMPTZ,
            error_message            TEXT
        );

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
        -- ITEM SHOP TRACKS (from fst-meta.db)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS item_shop_tracks (
            song_id           TEXT    PRIMARY KEY,
            scraped_at        TIMESTAMPTZ NOT NULL,
            leaving_tomorrow  BOOLEAN NOT NULL DEFAULT FALSE
        );

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
            computed_at              TIMESTAMPTZ NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_cr_rank
            ON composite_rankings (composite_rank);

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
        CREATE INDEX IF NOT EXISTS ix_combo_weighted
            ON combo_leaderboard (combo_id, weighted_rating ASC);
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

        """;
}
