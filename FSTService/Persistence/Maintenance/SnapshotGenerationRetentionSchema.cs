namespace FSTService.Persistence.Maintenance;

public static class SnapshotGenerationRetentionSchema
{
    public const string Sql = """

        -- =====================================================================
        -- SNAPSHOT GENERATION RETENTION CONTROL PLANE
        -- =====================================================================

        CREATE TABLE IF NOT EXISTS snapshot_generation_retention_cycles (
            cycle_id                BIGSERIAL   PRIMARY KEY,
            trigger_scrape_id       BIGINT      NOT NULL,
            trigger_publication_id  BIGINT      NOT NULL,
            safe_point_kind         TEXT        NOT NULL,
            safe_point_at           TIMESTAMPTZ NOT NULL,
            planner_version         INTEGER     NOT NULL,
            config_version          INTEGER     NOT NULL,
            report_only             BOOLEAN     NOT NULL,
            plan_digest             TEXT,
            status                  TEXT        NOT NULL,
            candidate_count         INTEGER     NOT NULL DEFAULT 0,
            blocked_count           INTEGER     NOT NULL DEFAULT 0,
            candidate_bytes         BIGINT      NOT NULL DEFAULT 0,
            blocked_bytes           BIGINT      NOT NULL DEFAULT 0,
            started_at              TIMESTAMPTZ NOT NULL
                DEFAULT clock_timestamp(),
            completed_at            TIMESTAMPTZ,
            error_message           TEXT,
            created_at              TIMESTAMPTZ NOT NULL
                DEFAULT clock_timestamp(),
            updated_at              TIMESTAMPTZ NOT NULL
                DEFAULT clock_timestamp(),
            CONSTRAINT ck_snapshot_generation_retention_cycle_safe_point
                CHECK (safe_point_kind IN ('post_publication')),
            CONSTRAINT ck_snapshot_generation_retention_cycle_status
                CHECK (status IN (
                    'planning',
                    'observed',
                    'planned',
                    'blocked',
                    'deferred',
                    'failed',
                    'completed',
                    'cancelled',
                    'safety_failed')),
            CONSTRAINT ck_snapshot_generation_retention_cycle_versions
                CHECK (planner_version > 0 AND config_version > 0),
            CONSTRAINT ck_snapshot_generation_retention_cycle_triggers
                CHECK (
                    trigger_scrape_id > 0
                    AND trigger_publication_id > 0),
            CONSTRAINT ck_snapshot_generation_retention_cycle_counts
                CHECK (
                    candidate_count >= 0
                    AND blocked_count >= 0
                    AND candidate_bytes >= 0
                    AND blocked_bytes >= 0),
            CONSTRAINT ck_snapshot_generation_retention_cycle_digest
                CHECK (
                    plan_digest IS NULL
                    OR plan_digest ~ '^[0-9a-f]{64}$'),
            CONSTRAINT ck_snapshot_generation_retention_cycle_completion
                CHECK (
                    (status = 'planning' AND completed_at IS NULL)
                    OR status <> 'planning'),
            CONSTRAINT ck_snapshot_generation_retention_cycle_mode
                CHECK (
                    (report_only AND status NOT IN (
                        'planned',
                        'completed',
                        'safety_failed'))
                    OR
                    (NOT report_only AND status <> 'observed')),
            CONSTRAINT ux_snapshot_generation_retention_cycle_mode
                UNIQUE (cycle_id, report_only)
        );

        CREATE UNIQUE INDEX IF NOT EXISTS
            ux_snapshot_generation_retention_cycle_safe_point
            ON snapshot_generation_retention_cycles
                (safe_point_kind, trigger_publication_id);

        CREATE TABLE IF NOT EXISTS snapshot_generation_retention_jobs (
            job_id                  BIGSERIAL   PRIMARY KEY,
            cycle_id                BIGINT      NOT NULL,
            report_only             BOOLEAN     NOT NULL,
            operation_kind          TEXT        NOT NULL,
            instrument              TEXT        NOT NULL,
            root_relation           TEXT        NOT NULL,
            child_relation          TEXT        NOT NULL,
            snapshot_id             BIGINT      NOT NULL,
            child_oid               BIGINT      NOT NULL,
            child_relfilenode       BIGINT      NOT NULL,
            partition_bound         TEXT        NOT NULL,
            tablespace_name         TEXT        NOT NULL,
            row_estimate            BIGINT      NOT NULL,
            total_bytes             BIGINT      NOT NULL,
            protected_evidence      JSONB       NOT NULL
                DEFAULT '{}'::jsonb,
            reference_evidence      JSONB       NOT NULL
                DEFAULT '{}'::jsonb,
            blocker_codes           TEXT[]      NOT NULL
                DEFAULT ARRAY[]::TEXT[],
            blocker_details         JSONB       NOT NULL
                DEFAULT '[]'::jsonb,
            status                  TEXT        NOT NULL,
            attempt_count           INTEGER     NOT NULL DEFAULT 0,
            lease_owner             TEXT,
            lease_token             UUID,
            lease_acquired_at       TIMESTAMPTZ,
            lease_expires_at        TIMESTAMPTZ,
            started_at              TIMESTAMPTZ,
            completed_at            TIMESTAMPTZ,
            error_message           TEXT,
            created_at              TIMESTAMPTZ NOT NULL
                DEFAULT clock_timestamp(),
            updated_at              TIMESTAMPTZ NOT NULL
                DEFAULT clock_timestamp(),
            CONSTRAINT ux_snapshot_generation_retention_job_cycle
                UNIQUE (cycle_id, job_id),
            CONSTRAINT ux_snapshot_generation_retention_job_identity
                UNIQUE (
                    cycle_id,
                    operation_kind,
                    instrument,
                    child_oid,
                    child_relfilenode),
            CONSTRAINT fk_snapshot_generation_retention_job_cycle_mode
                FOREIGN KEY (cycle_id, report_only)
                REFERENCES snapshot_generation_retention_cycles(
                    cycle_id,
                    report_only)
                ON DELETE RESTRICT,
            CONSTRAINT ck_snapshot_generation_retention_job_operation
                CHECK (operation_kind IN (
                    'drop_whole_child',
                    'compact_sparse_child')),
            CONSTRAINT ck_snapshot_generation_retention_job_instrument
                CHECK (instrument IN (
                    'Solo_Guitar',
                    'Solo_Bass',
                    'Solo_Vocals',
                    'Solo_Drums',
                    'Solo_PeripheralGuitar',
                    'Solo_PeripheralBass',
                    'Solo_PeripheralVocals',
                    'Solo_PeripheralCymbals',
                    'Solo_PeripheralDrums')),
            CONSTRAINT ck_snapshot_generation_retention_job_root
                CHECK (
                    (instrument = 'Solo_Guitar'
                        AND root_relation =
                            'leaderboard_entries_snapshot_solo_guitar')
                    OR
                    (instrument = 'Solo_Bass'
                        AND root_relation =
                            'leaderboard_entries_snapshot_solo_bass')
                    OR
                    (instrument = 'Solo_Vocals'
                        AND root_relation =
                            'leaderboard_entries_snapshot_solo_vocals')
                    OR
                    (instrument = 'Solo_Drums'
                        AND root_relation =
                            'leaderboard_entries_snapshot_solo_drums')
                    OR
                    (instrument = 'Solo_PeripheralGuitar'
                        AND root_relation =
                            'leaderboard_entries_snapshot_pro_guitar')
                    OR
                    (instrument = 'Solo_PeripheralBass'
                        AND root_relation =
                            'leaderboard_entries_snapshot_pro_bass')
                    OR
                    (instrument = 'Solo_PeripheralVocals'
                        AND root_relation =
                            'leaderboard_entries_snapshot_pro_vocals')
                    OR
                    (instrument = 'Solo_PeripheralCymbals'
                        AND root_relation =
                            'leaderboard_entries_snapshot_pro_cymbals')
                    OR
                    (instrument = 'Solo_PeripheralDrums'
                        AND root_relation =
                            'leaderboard_entries_snapshot_pro_drums')),
            CONSTRAINT ck_snapshot_generation_retention_job_child
                CHECK (
                    child_relation ~ (
                        '^'
                        || root_relation
                        || '_s[1-9][0-9]*$')),
            CONSTRAINT ck_snapshot_generation_retention_job_status
                CHECK (status IN (
                    'observed',
                    'planned',
                    'blocked',
                    'deferred',
                    'leased',
                    'executing',
                    'succeeded',
                    'failed',
                    'cancelled',
                    'safety_failed')),
            CONSTRAINT ck_snapshot_generation_retention_job_mode
                CHECK (
                    (
                        report_only
                        AND status IN (
                            'observed',
                            'blocked',
                            'deferred',
                            'failed',
                            'cancelled')
                        AND attempt_count = 0
                        AND lease_owner IS NULL
                        AND lease_token IS NULL
                        AND lease_acquired_at IS NULL
                        AND lease_expires_at IS NULL
                        AND started_at IS NULL
                        AND completed_at IS NULL
                    )
                    OR
                    (
                        NOT report_only
                        AND status <> 'observed'
                    )),
            CONSTRAINT ck_snapshot_generation_retention_job_identity_values
                CHECK (
                    snapshot_id > 0
                    AND child_oid > 0
                    AND child_relfilenode > 0
                    AND row_estimate >= 0
                    AND total_bytes >= 0
                    AND attempt_count >= 0),
            CONSTRAINT ck_snapshot_generation_retention_job_names
                CHECK (
                    root_relation <> ''
                    AND child_relation <> ''
                    AND partition_bound <> ''
                    AND tablespace_name <> ''),
            CONSTRAINT ck_snapshot_generation_retention_job_lease
                CHECK (
                    (
                        lease_owner IS NULL
                        AND lease_token IS NULL
                        AND lease_acquired_at IS NULL
                        AND lease_expires_at IS NULL
                    )
                    OR (
                        lease_owner IS NOT NULL
                        AND lease_token IS NOT NULL
                        AND lease_acquired_at IS NOT NULL
                        AND lease_expires_at IS NOT NULL
                        AND lease_expires_at > lease_acquired_at
                    ))
        );

        CREATE INDEX IF NOT EXISTS
            ix_snapshot_generation_retention_jobs_cycle_order
            ON snapshot_generation_retention_jobs
                (cycle_id, snapshot_id, instrument, job_id);

        CREATE INDEX IF NOT EXISTS
            ix_snapshot_generation_retention_jobs_executor
            ON snapshot_generation_retention_jobs
                (status, created_at, job_id)
            WHERE NOT report_only
              AND status IN (
                'planned',
                'leased',
                'executing',
                'safety_failed');

        CREATE UNIQUE INDEX IF NOT EXISTS
            ux_snapshot_generation_retention_one_active_job
            ON snapshot_generation_retention_jobs ((TRUE))
            WHERE NOT report_only
              AND status IN ('leased', 'executing');

        CREATE UNIQUE INDEX IF NOT EXISTS
            ux_snapshot_generation_retention_nonterminal_child
            ON snapshot_generation_retention_jobs (
                instrument,
                child_oid,
                child_relfilenode)
            WHERE NOT report_only
              AND status IN (
                  'planned',
                  'leased',
                  'executing',
                  'safety_failed');

        CREATE TABLE IF NOT EXISTS snapshot_generation_retention_evidence (
            evidence_id             BIGSERIAL   PRIMARY KEY,
            cycle_id                BIGINT      NOT NULL
                REFERENCES snapshot_generation_retention_cycles(cycle_id)
                ON DELETE RESTRICT,
            job_id                  BIGINT,
            sequence                INTEGER     NOT NULL,
            phase                   TEXT        NOT NULL,
            kind                    TEXT        NOT NULL,
            payload                 JSONB       NOT NULL,
            previous_hash           TEXT,
            current_hash            TEXT        NOT NULL,
            created_at              TIMESTAMPTZ NOT NULL
                DEFAULT clock_timestamp(),
            CONSTRAINT fk_snapshot_generation_retention_evidence_job
                FOREIGN KEY (cycle_id, job_id)
                REFERENCES snapshot_generation_retention_jobs(
                    cycle_id,
                    job_id)
                ON DELETE RESTRICT,
            CONSTRAINT ux_snapshot_generation_retention_evidence_sequence
                UNIQUE (cycle_id, sequence),
            CONSTRAINT ck_snapshot_generation_retention_evidence_sequence
                CHECK (sequence > 0),
            CONSTRAINT ck_snapshot_generation_retention_evidence_names
                CHECK (phase <> '' AND kind <> ''),
            CONSTRAINT ck_snapshot_generation_retention_evidence_previous_hash
                CHECK (
                    previous_hash IS NULL
                    OR previous_hash ~ '^[0-9a-f]{64}$'),
            CONSTRAINT ck_snapshot_generation_retention_evidence_current_hash
                CHECK (current_hash ~ '^[0-9a-f]{64}$')
        );

        CREATE INDEX IF NOT EXISTS
            ix_snapshot_generation_retention_evidence_job_sequence
            ON snapshot_generation_retention_evidence
                (job_id, sequence)
            WHERE job_id IS NOT NULL;

        CREATE OR REPLACE FUNCTION
            reject_snapshot_generation_retention_evidence_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            RAISE EXCEPTION
                'Snapshot-generation retention evidence is append-only.'
                USING ERRCODE = '55000';
        END
        $$;

        DROP TRIGGER IF EXISTS
            trg_reject_snapshot_generation_retention_evidence_mutation
            ON snapshot_generation_retention_evidence;
        CREATE TRIGGER
            trg_reject_snapshot_generation_retention_evidence_mutation
        BEFORE UPDATE OR DELETE OR TRUNCATE
        ON snapshot_generation_retention_evidence
        FOR EACH STATEMENT
        EXECUTE FUNCTION
            reject_snapshot_generation_retention_evidence_mutation();
        """;
}
