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
            score_history_fingerprint    TEXT        NOT NULL
                CHECK (length(score_history_fingerprint) = 64),
            population_evidence          JSONB       NOT NULL
                DEFAULT
                    '{"scopeCount":0,"minimumTotalEntries":0,"maximumTotalEntries":0,"fingerprint":"0000000000000000000000000000000000000000000000000000000000000000"}'::JSONB,
            score_history_evidence       JSONB       NOT NULL
                DEFAULT
                    '{"rowCount":0,"minimumId":null,"maximumId":null,"minimumChangedAtUtc":null,"maximumChangedAtUtc":null,"fingerprint":"0000000000000000000000000000000000000000000000000000000000000000"}'::JSONB,
            CONSTRAINT ck_max_score_population_evidence
                CHECK (
                    length(
                        population_evidence
                            ->> 'fingerprint') = 64),
            CONSTRAINT ck_max_score_history_evidence
                CHECK (
                    length(
                        score_history_evidence
                            ->> 'fingerprint') = 64),
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
                    'completed',
                    'rollback_validating',
                    'rollback_paths_restored',
                    'rollback_derived_state_rebuilt',
                    'rollback_notifications_quarantined',
                    'rollback_caches_staged',
                    'rollback_validated',
                    'rolled_back')),
            status                      TEXT        NOT NULL
                CHECK (
                    status IN (
                        'running',
                        'failed',
                        'completed',
                        'rolled_back')),
            CONSTRAINT ck_max_score_maintenance_terminal_state
                CHECK (
                    (
                        phase = 'completed'
                        AND status = 'completed'
                    )
                    OR (
                        phase = 'rolled_back'
                        AND status = 'rolled_back'
                    )
                    OR (
                        phase NOT IN (
                            'completed',
                            'rolled_back')
                        AND status IN (
                            'running',
                            'failed')
                    )),
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
            staged_cache_evidence       JSONB,
            rollback_started_at         TIMESTAMPTZ,
            rollback_paths_restored_at  TIMESTAMPTZ,
            rollback_derived_state_rebuilt_at
                                            TIMESTAMPTZ,
            rollback_notifications_quarantined_at
                                            TIMESTAMPTZ,
            rollback_caches_staged_at   TIMESTAMPTZ,
            rollback_validated_at       TIMESTAMPTZ,
            rolled_back_at              TIMESTAMPTZ,
            rollback_before_path_fingerprint TEXT,
            rollback_after_path_fingerprint TEXT,
            rollback_restored_song_count INTEGER NOT NULL
                                            DEFAULT 0,
            rollback_rebuilt_instrument_count INTEGER NOT NULL
                                            DEFAULT 0,
            rollback_notification_maintenance_run_id BIGINT,
            rollback_quarantined_candidate_count BIGINT NOT NULL
                                            DEFAULT 0,
            rollback_visible_delivery_count INTEGER NOT NULL
                                            DEFAULT 0
                CHECK (rollback_visible_delivery_count = 0),
            rollback_staged_cache_entry_count BIGINT NOT NULL
                                            DEFAULT 0,
            rollback_cache_evidence     JSONB,
            rollback_failure_stage      TEXT,
            rollback_failure_detail     TEXT,
            CONSTRAINT ck_max_score_cache_evidence
                CHECK (
                    staged_cache_evidence IS NULL
                    OR (
                        length(
                            staged_cache_evidence
                                ->> 'contentFingerprint') = 64
                        AND length(
                            staged_cache_evidence
                                ->> 'publishedScopeCacheKeyFingerprint') = 64
                        AND (
                            staged_cache_evidence
                                ->> 'publishedScopeCacheKeyCount')::INTEGER
                            >= 0
                        AND (
                            staged_cache_evidence
                                ->> 'entryCount')::BIGINT =
                            staged_cache_entry_count
                    )),
            failure_stage               TEXT,
            failure_detail              TEXT,
            created_at                  TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at                  TIMESTAMPTZ NOT NULL DEFAULT now(),
            completed_at                TIMESTAMPTZ
        );

        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS score_history_fingerprint TEXT;
        UPDATE max_score_maintenance_runs
        SET score_history_fingerprint = repeat('0', 64)
        WHERE score_history_fingerprint IS NULL;
        ALTER TABLE max_score_maintenance_runs
            ALTER COLUMN score_history_fingerprint SET NOT NULL;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS population_evidence JSONB NOT NULL
                DEFAULT
                    '{"scopeCount":0,"minimumTotalEntries":0,"maximumTotalEntries":0,"fingerprint":"0000000000000000000000000000000000000000000000000000000000000000"}'::JSONB;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS score_history_evidence JSONB NOT NULL
                DEFAULT
                    '{"rowCount":0,"minimumId":null,"maximumId":null,"minimumChangedAtUtc":null,"maximumChangedAtUtc":null,"fingerprint":"0000000000000000000000000000000000000000000000000000000000000000"}'::JSONB;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS staged_cache_evidence JSONB;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS rollback_started_at TIMESTAMPTZ;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS
                rollback_paths_restored_at TIMESTAMPTZ;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS
                rollback_derived_state_rebuilt_at TIMESTAMPTZ;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS
                rollback_notifications_quarantined_at TIMESTAMPTZ;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS
                rollback_caches_staged_at TIMESTAMPTZ;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS rollback_validated_at TIMESTAMPTZ;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS rolled_back_at TIMESTAMPTZ;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS
                rollback_before_path_fingerprint TEXT;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS
                rollback_after_path_fingerprint TEXT;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS
                rollback_restored_song_count INTEGER NOT NULL
                    DEFAULT 0;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS
                rollback_rebuilt_instrument_count INTEGER NOT NULL
                    DEFAULT 0;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS
                rollback_notification_maintenance_run_id BIGINT;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS
                rollback_quarantined_candidate_count BIGINT NOT NULL
                    DEFAULT 0;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS
                rollback_visible_delivery_count INTEGER NOT NULL
                    DEFAULT 0;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS
                rollback_staged_cache_entry_count BIGINT NOT NULL
                    DEFAULT 0;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS rollback_cache_evidence JSONB;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS rollback_failure_stage TEXT;
        ALTER TABLE max_score_maintenance_runs
            ADD COLUMN IF NOT EXISTS rollback_failure_detail TEXT;
        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                        'max_score_maintenance_runs'::regclass
                  AND conname =
                        'max_score_maintenance_runs_phase_check'
                  AND pg_get_constraintdef(oid)
                        NOT LIKE '%rolled_back%'
            ) THEN
                ALTER TABLE max_score_maintenance_runs
                    DROP CONSTRAINT
                        max_score_maintenance_runs_phase_check;
            END IF;
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                        'max_score_maintenance_runs'::regclass
                  AND conname =
                        'max_score_maintenance_runs_phase_check'
            ) THEN
                ALTER TABLE max_score_maintenance_runs
                    ADD CONSTRAINT
                        max_score_maintenance_runs_phase_check
                    CHECK (
                        phase IN (
                            'freeze_established',
                            'rollback_captured',
                            'paths_promoted',
                            'derived_state_rebuilt',
                            'notifications_quarantined',
                            'caches_staged',
                            'validated',
                            'completed',
                            'rollback_validating',
                            'rollback_paths_restored',
                            'rollback_derived_state_rebuilt',
                            'rollback_notifications_quarantined',
                            'rollback_caches_staged',
                            'rollback_validated',
                            'rolled_back'));
            END IF;

            IF EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                        'max_score_maintenance_runs'::regclass
                  AND conname =
                        'max_score_maintenance_runs_status_check'
                  AND pg_get_constraintdef(oid)
                        NOT LIKE '%rolled_back%'
            ) THEN
                ALTER TABLE max_score_maintenance_runs
                    DROP CONSTRAINT
                        max_score_maintenance_runs_status_check;
            END IF;
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                        'max_score_maintenance_runs'::regclass
                  AND conname =
                        'max_score_maintenance_runs_status_check'
            ) THEN
                ALTER TABLE max_score_maintenance_runs
                    ADD CONSTRAINT
                        max_score_maintenance_runs_status_check
                    CHECK (
                        status IN (
                            'running',
                            'failed',
                            'completed',
                            'rolled_back'));
            END IF;

            IF EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                        'max_score_maintenance_runs'::regclass
                  AND conname =
                        'ck_max_score_maintenance_terminal_state'
                  AND pg_get_constraintdef(oid)
                        NOT LIKE '%rolled_back%'
            ) THEN
                ALTER TABLE max_score_maintenance_runs
                    DROP CONSTRAINT
                        ck_max_score_maintenance_terminal_state;
            END IF;
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                        'max_score_maintenance_runs'::regclass
                  AND conname =
                        'ck_max_score_maintenance_terminal_state'
            ) THEN
                ALTER TABLE max_score_maintenance_runs
                    ADD CONSTRAINT
                        ck_max_score_maintenance_terminal_state
                    CHECK (
                        (
                            phase = 'completed'
                            AND status = 'completed'
                        )
                        OR (
                            phase = 'rolled_back'
                            AND status = 'rolled_back'
                        )
                        OR (
                            phase NOT IN (
                                'completed',
                                'rolled_back')
                            AND status IN (
                                'running',
                                'failed')
                        ));
            END IF;
        END
        $$;
        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                        'max_score_maintenance_runs'::regclass
                  AND conname =
                        'ck_max_score_maintenance_score_history_fingerprint'
            ) THEN
                ALTER TABLE max_score_maintenance_runs
                    ADD CONSTRAINT
                        ck_max_score_maintenance_score_history_fingerprint
                    CHECK (
                        length(score_history_fingerprint) = 64);
            END IF;
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                        'max_score_maintenance_runs'::regclass
                  AND conname =
                        'ck_max_score_population_evidence'
            ) THEN
                ALTER TABLE max_score_maintenance_runs
                    ADD CONSTRAINT
                        ck_max_score_population_evidence
                    CHECK (
                        length(
                            population_evidence
                                ->> 'fingerprint') = 64);
            END IF;
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                        'max_score_maintenance_runs'::regclass
                  AND conname =
                        'ck_max_score_history_evidence'
            ) THEN
                ALTER TABLE max_score_maintenance_runs
                    ADD CONSTRAINT
                        ck_max_score_history_evidence
                    CHECK (
                        length(
                            score_history_evidence
                                ->> 'fingerprint') = 64);
            END IF;
            IF EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                        'max_score_maintenance_runs'::regclass
                  AND conname =
                        'ck_max_score_cache_evidence'
                  AND pg_get_constraintdef(oid)
                        NOT LIKE
                            '%publishedScopeCacheKeyFingerprint%'
            ) THEN
                ALTER TABLE max_score_maintenance_runs
                    DROP CONSTRAINT
                        ck_max_score_cache_evidence;
            END IF;
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                        'max_score_maintenance_runs'::regclass
                  AND conname =
                        'ck_max_score_cache_evidence'
            ) THEN
                ALTER TABLE max_score_maintenance_runs
                    ADD CONSTRAINT
                        ck_max_score_cache_evidence
                    CHECK (
                        staged_cache_evidence IS NULL
                        OR (
                            length(
                                staged_cache_evidence
                                    ->> 'contentFingerprint') = 64
                            AND length(
                                staged_cache_evidence
                                    ->> 'publishedScopeCacheKeyFingerprint') = 64
                            AND (
                                staged_cache_evidence
                                    ->> 'publishedScopeCacheKeyCount')::INTEGER
                                >= 0
                            AND (
                                staged_cache_evidence
                                    ->> 'entryCount')::BIGINT =
                                staged_cache_entry_count
                        ));
            END IF;
        END
        $$;

        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                        'max_score_maintenance_runs'::regclass
                  AND conname =
                        'ck_max_score_rollback_path_fingerprints'
            ) THEN
                ALTER TABLE max_score_maintenance_runs
                    ADD CONSTRAINT
                        ck_max_score_rollback_path_fingerprints
                    CHECK (
                        (
                            rollback_before_path_fingerprint
                                IS NULL
                            OR length(
                                rollback_before_path_fingerprint)
                                = 64
                        )
                        AND (
                            rollback_after_path_fingerprint
                                IS NULL
                            OR length(
                                rollback_after_path_fingerprint)
                                = 64
                        ));
            END IF;
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                        'max_score_maintenance_runs'::regclass
                  AND conname =
                        'ck_max_score_rollback_counts'
            ) THEN
                ALTER TABLE max_score_maintenance_runs
                    ADD CONSTRAINT
                        ck_max_score_rollback_counts
                    CHECK (
                        rollback_restored_song_count >= 0
                        AND rollback_rebuilt_instrument_count >= 0
                        AND rollback_quarantined_candidate_count >= 0
                        AND rollback_visible_delivery_count = 0
                        AND rollback_staged_cache_entry_count >= 0);
            END IF;
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid =
                        'max_score_maintenance_runs'::regclass
                  AND conname =
                        'ck_max_score_rollback_cache_evidence'
            ) THEN
                ALTER TABLE max_score_maintenance_runs
                    ADD CONSTRAINT
                        ck_max_score_rollback_cache_evidence
                    CHECK (
                        rollback_cache_evidence IS NULL
                        OR (
                            length(
                                rollback_cache_evidence
                                    ->> 'contentFingerprint')
                                = 64
                            AND length(
                                rollback_cache_evidence
                                    ->> 'publishedScopeCacheKeyFingerprint')
                                = 64
                            AND (
                                rollback_cache_evidence
                                    ->> 'entryCount')::BIGINT
                                =
                                rollback_staged_cache_entry_count
                        ));
            END IF;
        END
        $$;

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

        CREATE TABLE IF NOT EXISTS
            max_score_maintenance_cache_entries (
                manifest_sha256 TEXT NOT NULL
                    REFERENCES max_score_maintenance_runs(
                        manifest_sha256),
                cache_key      TEXT NOT NULL,
                etag           TEXT NOT NULL,
                json_sha256    TEXT NOT NULL
                    CHECK (length(json_sha256) = 64),
                PRIMARY KEY (manifest_sha256, cache_key)
            );

        CREATE TABLE IF NOT EXISTS
            max_score_maintenance_rollback_cache_entries (
                manifest_sha256 TEXT NOT NULL
                    REFERENCES max_score_maintenance_runs(
                        manifest_sha256),
                cache_key      TEXT NOT NULL,
                etag           TEXT NOT NULL,
                json_sha256    TEXT NOT NULL
                    CHECK (length(json_sha256) = 64),
                PRIMARY KEY (manifest_sha256, cache_key)
            );

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
                NEW.score_history_fingerprint,
                NEW.population_evidence,
                NEW.score_history_evidence,
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
                OLD.score_history_fingerprint,
                OLD.population_evidence,
                OLD.score_history_evidence,
                OLD.manifest_json,
                OLD.freeze_reason)
            THEN
                RAISE EXCEPTION
                    'Max-score maintenance identity is immutable.'
                    USING ERRCODE = '55000';
            END IF;
            IF OLD.staged_cache_evidence IS NOT NULL
               AND (
                   NEW.staged_cache_evidence
                       IS DISTINCT FROM
                       OLD.staged_cache_evidence
                   OR NEW.staged_cache_entry_count
                       IS DISTINCT FROM
                       OLD.staged_cache_entry_count
               )
            THEN
                RAISE EXCEPTION
                    'Max-score staged cache evidence is immutable.'
                    USING ERRCODE = '55000';
            END IF;
            IF OLD.rollback_cache_evidence IS NOT NULL
               AND (
                   NEW.rollback_cache_evidence
                       IS DISTINCT FROM
                       OLD.rollback_cache_evidence
                   OR NEW.rollback_staged_cache_entry_count
                       IS DISTINCT FROM
                       OLD.rollback_staged_cache_entry_count
               )
            THEN
                RAISE EXCEPTION
                    'Max-score rollback cache evidence is immutable.'
                    USING ERRCODE = '55000';
            END IF;
            RETURN NEW;
        END
        $$;

        CREATE OR REPLACE FUNCTION
            enforce_max_score_cache_entry_evidence_immutability()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            IF TG_OP = 'INSERT' THEN
                IF NOT EXISTS (
                    SELECT 1
                    FROM max_score_maintenance_runs run
                    WHERE run.manifest_sha256 =
                            NEW.manifest_sha256
                      AND (
                          (
                              TG_TABLE_NAME =
                                  'max_score_maintenance_cache_entries'
                              AND run.phase =
                                  'notifications_quarantined'
                          )
                          OR (
                              TG_TABLE_NAME =
                                  'max_score_maintenance_rollback_cache_entries'
                              AND run.phase =
                                  'rollback_notifications_quarantined'
                          )
                      )
                      AND run.status IN ('running', 'failed')
                ) THEN
                    RAISE EXCEPTION
                        'Max-score cache entry evidence can only be captured at the cache-staging checkpoint.'
                        USING ERRCODE = '55000';
                END IF;
                RETURN NEW;
            END IF;

            RAISE EXCEPTION
                'Max-score cache entry evidence is immutable.'
                USING ERRCODE = '55000';
        END
        $$;

        CREATE OR REPLACE FUNCTION
            fst_assert_max_score_cache_staging_mutation_allowed()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        DECLARE
            maintenance_owner_token TEXT;
            session_lease_token TEXT;
        BEGIN
            SELECT publication.max_score_mutation_gate_token
            INTO maintenance_owner_token
            FROM scrape_publication_state publication
            JOIN max_score_maintenance_runs run
              ON run.freeze_reason =
                    publication.public_reads_frozen_reason
             AND run.expected_publication_id =
                    publication.current_publication_id
             AND run.expected_published_scrape_id =
                    publication.published_scrape_id
            WHERE publication.id = TRUE
              AND publication.public_reads_frozen
              AND run.phase NOT IN (
                  'completed',
                  'rolled_back')
              AND run.status IN ('running', 'failed')
            ORDER BY run.created_at DESC
            LIMIT 1;

            IF NOT FOUND THEN
                RETURN NULL;
            END IF;

            session_lease_token := current_setting(
                'fst.max_score_maintenance_lease_token',
                TRUE);
            IF maintenance_owner_token IS NOT NULL
               AND session_lease_token =
                    maintenance_owner_token
            THEN
                RETURN NULL;
            END IF;

            RAISE EXCEPTION
                'Cache staging mutation rejected while max-score maintenance owns the publication.'
                USING ERRCODE = '55000';
        END
        $$;

        CREATE OR REPLACE FUNCTION
            fst_assert_max_score_publication_cache_staging_row_mutation_allowed()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        DECLARE
            maintenance_owner_token TEXT;
            session_lease_token TEXT;
            current_publication_id BIGINT;
            previous_publication_id BIGINT;
            working_publication_id BIGINT;
        BEGIN
            SELECT publication.max_score_mutation_gate_token,
                   publication.current_publication_id,
                   publication.previous_publication_id,
                   publication.working_publication_id
            INTO maintenance_owner_token,
                 current_publication_id,
                 previous_publication_id,
                 working_publication_id
            FROM scrape_publication_state publication
            JOIN max_score_maintenance_runs run
              ON run.freeze_reason =
                    publication.public_reads_frozen_reason
             AND run.expected_publication_id =
                    publication.current_publication_id
             AND run.expected_published_scrape_id =
                    publication.published_scrape_id
            WHERE publication.id = TRUE
              AND publication.public_reads_frozen
              AND run.phase NOT IN (
                  'completed',
                  'rolled_back')
              AND run.status IN ('running', 'failed')
            ORDER BY run.created_at DESC
            LIMIT 1;

            IF NOT FOUND THEN
                IF TG_OP = 'DELETE' THEN
                    RETURN OLD;
                END IF;
                RETURN NEW;
            END IF;

            session_lease_token := current_setting(
                'fst.max_score_maintenance_lease_token',
                TRUE);
            IF maintenance_owner_token IS NOT NULL
               AND session_lease_token =
                    maintenance_owner_token
            THEN
                IF TG_OP = 'DELETE' THEN
                    RETURN OLD;
                END IF;
                RETURN NEW;
            END IF;

            IF TG_OP = 'DELETE'
               AND OLD.publication_id IS DISTINCT FROM
                    current_publication_id
               AND OLD.publication_id IS DISTINCT FROM
                    previous_publication_id
               AND OLD.publication_id IS DISTINCT FROM
                    working_publication_id
            THEN
                RETURN OLD;
            END IF;

            RAISE EXCEPTION
                'Publication cache staging mutation rejected while max-score maintenance owns the publication.'
                USING ERRCODE = '55000';
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
                        'max_score_maintenance_cache_entries'::regclass
                  AND tgname =
                        'trg_enforce_max_score_cache_entry_evidence_immutability'
                  AND NOT tgisinternal
            ) THEN
                CREATE TRIGGER
                    trg_enforce_max_score_cache_entry_evidence_immutability
                BEFORE INSERT OR UPDATE OR DELETE
                ON max_score_maintenance_cache_entries
                FOR EACH ROW
                EXECUTE FUNCTION
                    enforce_max_score_cache_entry_evidence_immutability();
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_trigger
                WHERE tgrelid =
                        'max_score_maintenance_rollback_cache_entries'::regclass
                  AND tgname =
                        'trg_enforce_max_score_rollback_cache_entry_evidence_immutability'
                  AND NOT tgisinternal
            ) THEN
                CREATE TRIGGER
                    trg_enforce_max_score_rollback_cache_entry_evidence_immutability
                BEFORE INSERT OR UPDATE OR DELETE
                ON max_score_maintenance_rollback_cache_entries
                FOR EACH ROW
                EXECUTE FUNCTION
                    enforce_max_score_cache_entry_evidence_immutability();
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_trigger
                WHERE tgrelid =
                        'api_response_cache_staging'::regclass
                  AND tgname =
                        'trg_max_score_cache_staging_mutation_guard'
                  AND NOT tgisinternal
            ) THEN
                CREATE TRIGGER
                    trg_max_score_cache_staging_mutation_guard
                BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
                ON api_response_cache_staging
                FOR EACH STATEMENT
                EXECUTE FUNCTION
                    fst_assert_max_score_cache_staging_mutation_allowed();
            END IF;

            DROP TRIGGER IF EXISTS
                trg_max_score_publication_cache_staging_mutation_guard
                ON publication_api_response_cache_staging;
            CREATE TRIGGER
                trg_max_score_publication_cache_staging_mutation_guard
            BEFORE INSERT OR UPDATE OR DELETE
            ON publication_api_response_cache_staging
            FOR EACH ROW
            EXECUTE FUNCTION
                fst_assert_max_score_publication_cache_staging_row_mutation_allowed();

            DROP TRIGGER IF EXISTS
                trg_max_score_publication_cache_staging_truncate_guard
                ON publication_api_response_cache_staging;
            CREATE TRIGGER
                trg_max_score_publication_cache_staging_truncate_guard
            BEFORE TRUNCATE
            ON publication_api_response_cache_staging
            FOR EACH STATEMENT
            EXECUTE FUNCTION
                fst_assert_max_score_cache_staging_mutation_allowed();

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
