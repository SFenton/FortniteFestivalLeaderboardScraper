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
        cmd.CommandTimeout = 0; // No timeout — schema init must complete before the service can start
        cmd.CommandText = $"{Schema}{Environment.NewLine}{Environment.NewLine}{BandRankingStorageNames.GetCurrentSchemaSql()}";
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

        CREATE EXTENSION IF NOT EXISTS pg_trgm;

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
        -- ix_le_song_rank removed 2026-04-23 (Phase 2): idx_scan=0 across all
        -- 9 partitions for the life of the database. Per-song rank ordering is
        -- provided instead by the (song_id, instrument, score DESC) index
        -- (ix_le_song_score), which supports the actual access pattern.
        -- Saves ~3.9 GB.

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

        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_solo_guitar    PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_Guitar');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_solo_bass      PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_Bass');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_solo_drums     PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_Drums');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_solo_vocals    PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_Vocals');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_pro_guitar     PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_PeripheralGuitar');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_pro_bass       PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_PeripheralBass');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_pro_vocals     PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_PeripheralVocals');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_pro_cymbals    PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_PeripheralCymbals');
        CREATE TABLE IF NOT EXISTS leaderboard_entries_snapshot_pro_drums      PARTITION OF leaderboard_entries_snapshot FOR VALUES IN ('Solo_PeripheralDrums');

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

        -- Efficient change-detection: latest snapshot per (instrument, account)
        CREATE INDEX IF NOT EXISTS ix_rh_latest
            ON rank_history (instrument, account_id, snapshot_date DESC);

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
        CREATE INDEX IF NOT EXISTS ix_sh_valid_lookup
            ON score_history (account_id, song_id, instrument, new_score DESC)
            INCLUDE (accuracy, is_full_combo, stars);
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

        -- =====================================================================
        -- MIGRATION: deduplicate rank_history + enforce PRIMARY KEY
        -- The original CREATE TABLE IF NOT EXISTS is a no-op on tables that
        -- predate the PK definition, so ON CONFLICT could silently INSERT
        -- duplicates.  Clean up and retrofit the constraint.
        -- =====================================================================

        DELETE FROM rank_history rh
        WHERE EXISTS (
            SELECT 1 FROM rank_history rh2
            WHERE rh2.account_id = rh.account_id
              AND rh2.instrument = rh.instrument
              AND rh2.snapshot_date = rh.snapshot_date
              AND rh2.ctid > rh.ctid
        );

        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = 'rank_history'::regclass
                  AND contype = 'p'
            ) THEN
                ALTER TABLE rank_history
                    ADD PRIMARY KEY (account_id, instrument, snapshot_date);
            END IF;
        END $$;

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
        CREATE TABLE IF NOT EXISTS ranking_deltas_pro_vocals     PARTITION OF ranking_deltas FOR VALUES IN ('Solo_PeripheralVocals');
        CREATE TABLE IF NOT EXISTS ranking_deltas_pro_cymbals    PARTITION OF ranking_deltas FOR VALUES IN ('Solo_PeripheralCymbals');
        CREATE TABLE IF NOT EXISTS ranking_deltas_pro_drums      PARTITION OF ranking_deltas FOR VALUES IN ('Solo_PeripheralDrums');

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
        CREATE TABLE IF NOT EXISTS ranking_delta_tiers_pro_vocals     PARTITION OF ranking_delta_tiers FOR VALUES IN ('Solo_PeripheralVocals');
        CREATE TABLE IF NOT EXISTS ranking_delta_tiers_pro_cymbals    PARTITION OF ranking_delta_tiers FOR VALUES IN ('Solo_PeripheralCymbals');
        CREATE TABLE IF NOT EXISTS ranking_delta_tiers_pro_drums      PARTITION OF ranking_delta_tiers FOR VALUES IN ('Solo_PeripheralDrums');

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
        CREATE TABLE IF NOT EXISTS rank_history_deltas_pro_vocals     PARTITION OF rank_history_deltas FOR VALUES IN ('Solo_PeripheralVocals');
        CREATE TABLE IF NOT EXISTS rank_history_deltas_pro_cymbals    PARTITION OF rank_history_deltas FOR VALUES IN ('Solo_PeripheralCymbals');
        CREATE TABLE IF NOT EXISTS rank_history_deltas_pro_drums      PARTITION OF rank_history_deltas FOR VALUES IN ('Solo_PeripheralDrums');

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
