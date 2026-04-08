using Npgsql;

namespace FSTService.Persistence;

/// <summary>
/// Creates the PostgreSQL schema for FSTService.
/// All statements are idempotent (IF NOT EXISTS).
/// </summary>
public static class DatabaseInitializer
{
    public static async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema;
        await cmd.ExecuteNonQueryAsync(ct);

        // Reset SERIAL sequences to max(id)+1 — needed after COPY migration inserts explicit IDs
        await using var seqCmd = conn.CreateCommand();
        seqCmd.CommandText = """
            SELECT setval(pg_get_serial_sequence('scrape_log', 'id'), COALESCE((SELECT MAX(id) FROM scrape_log), 0) + 1, false);
            SELECT setval(pg_get_serial_sequence('score_history', 'id'), COALESCE((SELECT MAX(id) FROM score_history), 0) + 1, false);
            SELECT setval(pg_get_serial_sequence('user_sessions', 'id'), COALESCE((SELECT MAX(id) FROM user_sessions), 0) + 1, false);
            """;
        await seqCmd.ExecuteNonQueryAsync(ct);
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
            first_seen_at  TIMESTAMPTZ NOT NULL,
            last_updated_at TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (song_id, instrument, account_id)
        ) PARTITION BY LIST (instrument);

        CREATE TABLE IF NOT EXISTS leaderboard_entries_solo_guitar    PARTITION OF leaderboard_entries FOR VALUES IN ('Solo_Guitar');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_solo_bass      PARTITION OF leaderboard_entries FOR VALUES IN ('Solo_Bass');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_solo_drums     PARTITION OF leaderboard_entries FOR VALUES IN ('Solo_Drums');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_solo_vocals    PARTITION OF leaderboard_entries FOR VALUES IN ('Solo_Vocals');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_pro_guitar     PARTITION OF leaderboard_entries FOR VALUES IN ('Solo_PeripheralGuitar');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_pro_bass       PARTITION OF leaderboard_entries FOR VALUES IN ('Solo_PeripheralBass');

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
        -- RANK HISTORY (partitioned by instrument)
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
        ) PARTITION BY LIST (instrument);

        CREATE TABLE IF NOT EXISTS rank_history_solo_guitar    PARTITION OF rank_history FOR VALUES IN ('Solo_Guitar');
        CREATE TABLE IF NOT EXISTS rank_history_solo_bass      PARTITION OF rank_history FOR VALUES IN ('Solo_Bass');
        CREATE TABLE IF NOT EXISTS rank_history_solo_drums     PARTITION OF rank_history FOR VALUES IN ('Solo_Drums');
        CREATE TABLE IF NOT EXISTS rank_history_solo_vocals    PARTITION OF rank_history FOR VALUES IN ('Solo_Vocals');
        CREATE TABLE IF NOT EXISTS rank_history_pro_guitar     PARTITION OF rank_history FOR VALUES IN ('Solo_PeripheralGuitar');
        CREATE TABLE IF NOT EXISTS rank_history_pro_bass       PARTITION OF rank_history FOR VALUES IN ('Solo_PeripheralBass');

        -- Efficient change-detection: latest snapshot per (instrument, account)
        CREATE INDEX IF NOT EXISTS ix_rh_latest
            ON rank_history (instrument, account_id, snapshot_date DESC);

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
            song_id               TEXT    PRIMARY KEY,
            first_seen_season     INTEGER,
            min_observed_season   INTEGER,
            estimated_season      INTEGER NOT NULL,
            probe_result          TEXT,
            calculated_at         TIMESTAMPTZ NOT NULL,
            calculation_version   INTEGER
        );
        ALTER TABLE song_first_seen_season ADD COLUMN IF NOT EXISTS calculation_version INTEGER;

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

        CREATE INDEX IF NOT EXISTS ix_cr_rank
            ON composite_rankings (composite_rank);

        -- Per-metric composite rank indexes for pagination
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS composite_rating_weighted REAL;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS composite_rank_weighted INTEGER;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS composite_rating_fcrate REAL;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS composite_rank_fcrate INTEGER;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS composite_rating_totalscore REAL;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS composite_rank_totalscore INTEGER;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS composite_rating_maxscore REAL;
        ALTER TABLE composite_rankings ADD COLUMN IF NOT EXISTS composite_rank_maxscore INTEGER;

        CREATE INDEX IF NOT EXISTS ix_cr_rank_weighted
            ON composite_rankings (composite_rank_weighted);
        CREATE INDEX IF NOT EXISTS ix_cr_rank_fcrate
            ON composite_rankings (composite_rank_fcrate);
        CREATE INDEX IF NOT EXISTS ix_cr_rank_totalscore
            ON composite_rankings (composite_rank_totalscore);
        CREATE INDEX IF NOT EXISTS ix_cr_rank_maxscore
            ON composite_rankings (composite_rank_maxscore);

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

        -- Efficient change-detection: latest composite snapshot per account
        CREATE INDEX IF NOT EXISTS ix_crh_latest
            ON composite_rank_history (account_id, snapshot_date DESC);

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

        -- =====================================================================
        -- MIGRATIONS: raw rating columns + schema version on rank_history
        -- =====================================================================

        ALTER TABLE account_rankings ADD COLUMN IF NOT EXISTS raw_max_score_percent REAL;

        ALTER TABLE rank_history ADD COLUMN IF NOT EXISTS raw_max_score_percent REAL;
        ALTER TABLE rank_history ADD COLUMN IF NOT EXISTS schema_version SMALLINT NOT NULL DEFAULT 1;

        ALTER TABLE account_rankings DROP COLUMN IF EXISTS raw_fc_rate;
        ALTER TABLE rank_history DROP COLUMN IF EXISTS raw_fc_rate;

        -- =====================================================================
        -- RANKING DELTAS (leeway-responsive rankings)
        -- Stores per-account metric overrides at each leeway bucket where
        -- their metrics differ from the base ranking (leeway = -5.0%).
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS ranking_deltas (
            account_id       TEXT    NOT NULL,
            instrument       TEXT    NOT NULL,
            leeway_bucket    REAL    NOT NULL,
            songs_played     INTEGER NOT NULL,
            adjusted_skill   REAL    NOT NULL,
            weighted         REAL    NOT NULL,
            fc_rate          REAL    NOT NULL,
            total_score      BIGINT  NOT NULL,
            max_score_pct    REAL    NOT NULL,
            full_combo_count INTEGER NOT NULL,
            avg_accuracy     REAL    NOT NULL,
            best_rank        INTEGER NOT NULL,
            coverage         REAL    NOT NULL,
            PRIMARY KEY (instrument, leeway_bucket, account_id)
        ) PARTITION BY LIST (instrument);

        CREATE TABLE IF NOT EXISTS ranking_deltas_solo_guitar    PARTITION OF ranking_deltas FOR VALUES IN ('Solo_Guitar');
        CREATE TABLE IF NOT EXISTS ranking_deltas_solo_bass      PARTITION OF ranking_deltas FOR VALUES IN ('Solo_Bass');
        CREATE TABLE IF NOT EXISTS ranking_deltas_solo_drums     PARTITION OF ranking_deltas FOR VALUES IN ('Solo_Drums');
        CREATE TABLE IF NOT EXISTS ranking_deltas_solo_vocals    PARTITION OF ranking_deltas FOR VALUES IN ('Solo_Vocals');
        CREATE TABLE IF NOT EXISTS ranking_deltas_pro_guitar     PARTITION OF ranking_deltas FOR VALUES IN ('Solo_PeripheralGuitar');
        CREATE TABLE IF NOT EXISTS ranking_deltas_pro_bass       PARTITION OF ranking_deltas FOR VALUES IN ('Solo_PeripheralBass');

        CREATE INDEX IF NOT EXISTS ix_rd_bucket_adj
            ON ranking_deltas (instrument, leeway_bucket, adjusted_skill ASC);
        CREATE INDEX IF NOT EXISTS ix_rd_bucket_total
            ON ranking_deltas (instrument, leeway_bucket, total_score DESC);
        CREATE INDEX IF NOT EXISTS ix_rd_account
            ON ranking_deltas (account_id, instrument);

        -- =====================================================================
        -- RANKING DELTA TIERS (interval-compressed leeway deltas)
        -- Stores per-account metric overrides as half-open bucket index
        -- intervals [start_bucket_idx, end_bucket_idx).
        -- Bucket index 0 = leeway -4.9%, 99 = +5.0%, 100 = unfiltered.
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS ranking_delta_tiers (
            account_id       TEXT     NOT NULL,
            instrument       TEXT     NOT NULL,
            start_bucket_idx SMALLINT NOT NULL,
            end_bucket_idx   SMALLINT NOT NULL,
            songs_played     INTEGER  NOT NULL,
            adjusted_skill   REAL     NOT NULL,
            weighted         REAL     NOT NULL,
            fc_rate          REAL     NOT NULL,
            total_score      BIGINT   NOT NULL,
            max_score_pct    REAL     NOT NULL,
            full_combo_count INTEGER  NOT NULL,
            avg_accuracy     REAL     NOT NULL,
            best_rank        INTEGER  NOT NULL,
            coverage         REAL     NOT NULL,
            PRIMARY KEY (instrument, account_id, start_bucket_idx)
        ) PARTITION BY LIST (instrument);

        CREATE TABLE IF NOT EXISTS ranking_delta_tiers_solo_guitar    PARTITION OF ranking_delta_tiers FOR VALUES IN ('Solo_Guitar');
        CREATE TABLE IF NOT EXISTS ranking_delta_tiers_solo_bass      PARTITION OF ranking_delta_tiers FOR VALUES IN ('Solo_Bass');
        CREATE TABLE IF NOT EXISTS ranking_delta_tiers_solo_drums     PARTITION OF ranking_delta_tiers FOR VALUES IN ('Solo_Drums');
        CREATE TABLE IF NOT EXISTS ranking_delta_tiers_solo_vocals    PARTITION OF ranking_delta_tiers FOR VALUES IN ('Solo_Vocals');
        CREATE TABLE IF NOT EXISTS ranking_delta_tiers_pro_guitar     PARTITION OF ranking_delta_tiers FOR VALUES IN ('Solo_PeripheralGuitar');
        CREATE TABLE IF NOT EXISTS ranking_delta_tiers_pro_bass       PARTITION OF ranking_delta_tiers FOR VALUES IN ('Solo_PeripheralBass');

        CREATE INDEX IF NOT EXISTS ix_rdt_account_range
            ON ranking_delta_tiers (instrument, account_id, start_bucket_idx, end_bucket_idx);

        -- =====================================================================
        -- RANK HISTORY DELTAS (leeway-responsive rank history)
        -- Stores daily rank history deltas for accounts whose rank differs
        -- from the base snapshot at each leeway bucket.
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS rank_history_deltas (
            account_id          TEXT    NOT NULL,
            instrument          TEXT    NOT NULL,
            snapshot_date       DATE    NOT NULL,
            leeway_bucket       REAL    NOT NULL,
            rank_adjusted       INTEGER,
            rank_weighted       INTEGER,
            rank_fcrate         INTEGER,
            rank_totalscore     INTEGER,
            rank_maxscore       INTEGER,
            PRIMARY KEY (instrument, snapshot_date, leeway_bucket, account_id)
        ) PARTITION BY LIST (instrument);

        CREATE TABLE IF NOT EXISTS rank_history_deltas_solo_guitar    PARTITION OF rank_history_deltas FOR VALUES IN ('Solo_Guitar');
        CREATE TABLE IF NOT EXISTS rank_history_deltas_solo_bass      PARTITION OF rank_history_deltas FOR VALUES IN ('Solo_Bass');
        CREATE TABLE IF NOT EXISTS rank_history_deltas_solo_drums     PARTITION OF rank_history_deltas FOR VALUES IN ('Solo_Drums');
        CREATE TABLE IF NOT EXISTS rank_history_deltas_solo_vocals    PARTITION OF rank_history_deltas FOR VALUES IN ('Solo_Vocals');
        CREATE TABLE IF NOT EXISTS rank_history_deltas_pro_guitar     PARTITION OF rank_history_deltas FOR VALUES IN ('Solo_PeripheralGuitar');
        CREATE TABLE IF NOT EXISTS rank_history_deltas_pro_bass       PARTITION OF rank_history_deltas FOR VALUES IN ('Solo_PeripheralBass');

        -- Efficient change-detection: latest delta per (instrument, bucket, account)
        CREATE INDEX IF NOT EXISTS ix_rhd_latest
            ON rank_history_deltas (instrument, leeway_bucket, account_id, snapshot_date DESC);

        -- =====================================================================
        -- COMPOSITE RANKING DELTAS (leeway-responsive composite rankings)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS composite_ranking_deltas (
            account_id       TEXT    NOT NULL,
            leeway_bucket    REAL    NOT NULL,
            adjusted_rating  REAL    NOT NULL,
            weighted_rating  REAL    NOT NULL,
            fc_rate_rating   REAL    NOT NULL,
            total_score      REAL    NOT NULL,
            max_score_rating REAL    NOT NULL,
            instruments_played INTEGER NOT NULL,
            total_songs_played INTEGER NOT NULL,
            PRIMARY KEY (leeway_bucket, account_id)
        );

        CREATE INDEX IF NOT EXISTS ix_crd_bucket_adj
            ON composite_ranking_deltas (leeway_bucket, adjusted_rating ASC);
        CREATE INDEX IF NOT EXISTS ix_crd_account
            ON composite_ranking_deltas (account_id);

        -- =====================================================================
        -- COMBO RANKING DELTAS (leeway-responsive combo rankings)
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS combo_ranking_deltas (
            combo_id         TEXT    NOT NULL,
            account_id       TEXT    NOT NULL,
            leeway_bucket    REAL    NOT NULL,
            adjusted_rating  REAL    NOT NULL,
            weighted_rating  REAL    NOT NULL,
            fc_rate          REAL    NOT NULL,
            total_score      BIGINT  NOT NULL,
            max_score_pct    REAL    NOT NULL,
            songs_played     INTEGER NOT NULL,
            full_combo_count INTEGER NOT NULL,
            PRIMARY KEY (combo_id, leeway_bucket, account_id)
        );

        CREATE INDEX IF NOT EXISTS ix_comrd_bucket_adj
            ON combo_ranking_deltas (combo_id, leeway_bucket, adjusted_rating ASC);
        CREATE INDEX IF NOT EXISTS ix_comrd_account
            ON combo_ranking_deltas (account_id, combo_id);

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

        CREATE INDEX IF NOT EXISTS ix_staging_scrape
            ON leaderboard_staging (scrape_id);
        CREATE INDEX IF NOT EXISTS ix_staging_instrument
            ON leaderboard_staging (scrape_id, instrument);
        CREATE INDEX IF NOT EXISTS ix_staging_combo
            ON leaderboard_staging (scrape_id, song_id, instrument);

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

        CREATE TABLE IF NOT EXISTS band_entries_duets  PARTITION OF band_entries FOR VALUES IN ('Band_Duets');
        CREATE TABLE IF NOT EXISTS band_entries_trios  PARTITION OF band_entries FOR VALUES IN ('Band_Trios');
        CREATE TABLE IF NOT EXISTS band_entries_quad   PARTITION OF band_entries FOR VALUES IN ('Band_Quad');

        CREATE INDEX IF NOT EXISTS ix_be_song_score
            ON band_entries (song_id, band_type, score DESC);
        CREATE INDEX IF NOT EXISTS ix_be_song_rank
            ON band_entries (song_id, band_type, rank);

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

        CREATE INDEX IF NOT EXISTS ix_bms_account
            ON band_member_stats (account_id);

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

        CREATE INDEX IF NOT EXISTS ix_bm_song_type
            ON band_members (song_id, band_type);

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

        CREATE INDEX IF NOT EXISTS ix_be_combo
            ON band_entries (song_id, band_type, instrument_combo);

        """;
}
