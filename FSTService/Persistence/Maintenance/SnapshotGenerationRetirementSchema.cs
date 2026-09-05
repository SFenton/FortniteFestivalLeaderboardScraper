namespace FSTService.Persistence.Maintenance;

public static class SnapshotGenerationRetirementSchema
{
    public const int Version = 1;
    public const long SchemaAdvisoryLockKey =
        2026090402L;

    public const string Sql = """
        SELECT pg_catalog.pg_advisory_xact_lock(
            2026090402);

        CREATE TABLE IF NOT EXISTS
            public.snapshot_generation_retirement_policy_epochs (
                policy_epoch_id                 UUID PRIMARY KEY,
                schema_version                  INTEGER NOT NULL,
                tool_id                         TEXT NOT NULL,
                stage_ceiling                   TEXT NOT NULL,
                not_before                      TIMESTAMPTZ NOT NULL,
                expires_at                      TIMESTAMPTZ NOT NULL,
                max_jobs                        INTEGER NOT NULL,
                max_total_bytes                 BIGINT NOT NULL,
                repository_commit               TEXT NOT NULL,
                repository_tree                 TEXT NOT NULL,
                supervisor_binary_sha256        TEXT NOT NULL,
                supervisor_source_sha256        TEXT NOT NULL,
                wrapper_sha256                  TEXT NOT NULL,
                control_schema_sha256           TEXT NOT NULL,
                source_database_name            TEXT NOT NULL,
                source_database_oid             BIGINT NOT NULL,
                source_system_identifier        TEXT NOT NULL,
                source_server_version_num       INTEGER NOT NULL,
                source_data_directory           TEXT NOT NULL,
                source_postmaster_started_at    TIMESTAMPTZ NOT NULL,
                source_identity_sha256          TEXT NOT NULL,
                approved_by                     TEXT NOT NULL,
                reviewed_by                     TEXT NOT NULL,
                approval_reference              TEXT NOT NULL,
                authorized_at                   TIMESTAMPTZ NOT NULL,
                policy_digest                   TEXT NOT NULL UNIQUE,
                created_at                      TIMESTAMPTZ NOT NULL
                                                DEFAULT clock_timestamp(),
                CONSTRAINT
                    ck_snapshot_generation_retirement_policy_contract
                    CHECK (
                        schema_version = 1
                        AND tool_id =
                            'fst.snapshot-generation-retirement-plan.v1'
                        AND stage_ceiling = 'plan'),
                CONSTRAINT
                    ck_snapshot_generation_retirement_policy_window
                    CHECK (
                        expires_at > not_before
                        AND expires_at <=
                            not_before + INTERVAL '7 days'),
                CONSTRAINT
                    ck_snapshot_generation_retirement_policy_budgets
                    CHECK (
                        max_jobs BETWEEN 1 AND 32
                        AND max_total_bytes BETWEEN 1
                            AND 17592186044416),
                CONSTRAINT
                    ck_snapshot_generation_retirement_policy_review
                    CHECK (
                        btrim(approved_by) <> ''
                        AND btrim(reviewed_by) <> ''
                        AND lower(btrim(approved_by)) <>
                            lower(btrim(reviewed_by))
                        AND btrim(approval_reference) <> ''),
                CONSTRAINT
                    ck_snapshot_generation_retirement_policy_git
                    CHECK (
                        repository_commit ~ '^[0-9a-f]{40}$'
                        AND repository_tree ~ '^[0-9a-f]{40}$'),
                CONSTRAINT
                    ck_snapshot_generation_retirement_policy_hashes
                    CHECK (
                        supervisor_binary_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND supervisor_source_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND wrapper_sha256 ~ '^[0-9a-f]{64}$'
                        AND control_schema_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND source_identity_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND policy_digest ~ '^[0-9a-f]{64}$'),
                CONSTRAINT
                    ck_snapshot_generation_retirement_policy_source
                    CHECK (
                        btrim(source_database_name) <> ''
                        AND source_database_oid > 0
                        AND source_system_identifier
                            ~ '^[0-9]+$'
                        AND source_server_version_num
                            BETWEEN 170000 AND 179999
                        AND source_data_directory LIKE '/%'
                        AND source_postmaster_started_at >
                            '2000-01-01T00:00:00Z'::TIMESTAMPTZ)
            );

        CREATE TABLE IF NOT EXISTS
            public.snapshot_generation_retirement_control (
                control_key                    BOOLEAN PRIMARY KEY
                                               DEFAULT TRUE,
                enabled                        BOOLEAN NOT NULL
                                               DEFAULT FALSE,
                active_policy_epoch_id         UUID
                                               REFERENCES
                                                   public.snapshot_generation_retirement_policy_epochs(
                                                       policy_epoch_id)
                                               ON DELETE RESTRICT,
                updated_by                     TEXT NOT NULL
                                               DEFAULT 'schema-default',
                updated_at                     TIMESTAMPTZ NOT NULL
                                               DEFAULT clock_timestamp(),
                CONSTRAINT
                    ck_snapshot_generation_retirement_control_singleton
                    CHECK (control_key),
                CONSTRAINT
                    ck_snapshot_generation_retirement_control_policy
                    CHECK (
                        (enabled
                            AND active_policy_epoch_id IS NOT NULL)
                        OR
                        (NOT enabled
                            AND active_policy_epoch_id IS NULL)),
                CONSTRAINT
                    ck_snapshot_generation_retirement_control_actor
                    CHECK (btrim(updated_by) <> '')
            );

        INSERT INTO public.snapshot_generation_retirement_control (
            control_key,
            enabled,
            active_policy_epoch_id,
            updated_by)
        VALUES (
            TRUE,
            FALSE,
            NULL,
            'schema-default')
        ON CONFLICT (control_key) DO NOTHING;

        CREATE TABLE IF NOT EXISTS
            public.snapshot_generation_retirement_jobs (
                job_id                         UUID PRIMARY KEY,
                schema_version                 INTEGER NOT NULL,
                tool_id                        TEXT NOT NULL,
                policy_epoch_id                UUID NOT NULL
                                               REFERENCES
                                                   public.snapshot_generation_retirement_policy_epochs(
                                                       policy_epoch_id)
                                               ON DELETE RESTRICT,
                cycle_id                       BIGINT NOT NULL
                                               REFERENCES
                                                   public.snapshot_generation_retention_cycles(
                                                       cycle_id)
                                               ON DELETE RESTRICT,
                observation_id                 BIGINT NOT NULL,
                trigger_scrape_id              BIGINT NOT NULL
                                               REFERENCES public.scrape_log(id)
                                               ON DELETE RESTRICT,
                trigger_publication_id         BIGINT NOT NULL
                                               REFERENCES
                                                   public.publication_generations(
                                                       publication_id)
                                               ON DELETE RESTRICT,
                instrument                     TEXT NOT NULL,
                instrument_order               SMALLINT NOT NULL,
                snapshot_id                    BIGINT NOT NULL,
                root_schema                    TEXT NOT NULL,
                root_relation                  TEXT NOT NULL,
                root_oid                       BIGINT NOT NULL,
                child_schema                   TEXT NOT NULL,
                child_relation                 TEXT NOT NULL,
                child_oid                      BIGINT NOT NULL,
                child_relfilenode              BIGINT NOT NULL,
                stable_child_identity_hash     TEXT NOT NULL,
                stable_config_schema_hash      TEXT NOT NULL,
                target_bytes                   BIGINT NOT NULL,
                source_identity_sha256         TEXT NOT NULL,
                plan_digest                    TEXT NOT NULL UNIQUE,
                state                          TEXT NOT NULL,
                state_reason                   TEXT,
                planned_at                     TIMESTAMPTZ NOT NULL,
                terminal_at                    TIMESTAMPTZ,
                created_at                     TIMESTAMPTZ NOT NULL
                                               DEFAULT clock_timestamp(),
                updated_at                     TIMESTAMPTZ NOT NULL
                                               DEFAULT clock_timestamp(),
                CONSTRAINT
                    fk_snapshot_generation_retirement_job_observation
                    FOREIGN KEY (
                        cycle_id,
                        observation_id)
                    REFERENCES
                        public.snapshot_generation_retention_observations(
                            cycle_id,
                            observation_id)
                    ON DELETE RESTRICT,
                CONSTRAINT
                    ux_snapshot_generation_retirement_job_cycle
                    UNIQUE (cycle_id),
                CONSTRAINT
                    ck_snapshot_generation_retirement_job_contract
                    CHECK (
                        schema_version = 1
                        AND tool_id =
                            'fst.snapshot-generation-retirement-plan.v1'),
                CONSTRAINT
                    ck_snapshot_generation_retirement_job_state
                    CHECK (
                        state IN (
                            'planned',
                            'expired',
                            'superseded')
                        AND (
                            (state = 'planned'
                                AND state_reason IS NULL
                                AND terminal_at IS NULL)
                            OR
                            (state <> 'planned'
                                AND state_reason IS NOT NULL
                                AND btrim(state_reason) <> ''
                                AND terminal_at IS NOT NULL))),
                CONSTRAINT
                    ck_snapshot_generation_retirement_job_target
                    CHECK (
                        snapshot_id > 0
                        AND root_oid > 0
                        AND child_oid > 0
                        AND child_relfilenode > 0
                        AND target_bytes > 0
                        AND instrument_order BETWEEN 0 AND 8
                        AND btrim(root_schema) <> ''
                        AND btrim(root_relation) <> ''
                        AND btrim(child_schema) <> ''
                        AND btrim(child_relation) <> ''),
                CONSTRAINT
                    ck_snapshot_generation_retirement_job_exclusion
                    CHECK (
                        NOT (
                            instrument = 'Solo_Bass'
                            AND snapshot_id = 1308)),
                CONSTRAINT
                    ck_snapshot_generation_retirement_job_hashes
                    CHECK (
                        stable_child_identity_hash
                            ~ '^[0-9a-f]{64}$'
                        AND stable_config_schema_hash
                            ~ '^[0-9a-f]{64}$'
                        AND source_identity_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND plan_digest ~ '^[0-9a-f]{64}$')
            );

        DO $retirement_index_repair$
        DECLARE
            index_name TEXT;
        BEGIN
            FOREACH index_name IN ARRAY ARRAY[
                'ux_snapshot_generation_retirement_one_planned_job',
                'ix_snapshot_generation_retirement_jobs_policy',
                'ix_snapshot_generation_retirement_events_job'
            ]
            LOOP
                IF EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_class index_relation
                    JOIN pg_catalog.pg_namespace namespace
                      ON namespace.oid =
                            index_relation.relnamespace
                    JOIN pg_catalog.pg_index index_row
                      ON index_row.indexrelid =
                            index_relation.oid
                    WHERE namespace.nspname = 'public'
                      AND index_relation.relname =
                            index_name
                      AND NOT (
                            index_row.indisvalid
                            AND index_row.indisready
                            AND index_row.indislive))
                THEN
                    EXECUTE pg_catalog.format(
                        'DROP INDEX public.%I',
                        index_name);
                END IF;
            END LOOP;
        END
        $retirement_index_repair$;

        CREATE UNIQUE INDEX IF NOT EXISTS
            ux_snapshot_generation_retirement_one_planned_job
            ON public.snapshot_generation_retirement_jobs (
                (TRUE))
            WHERE state = 'planned';

        CREATE INDEX IF NOT EXISTS
            ix_snapshot_generation_retirement_jobs_policy
            ON public.snapshot_generation_retirement_jobs (
                policy_epoch_id,
                created_at,
                job_id);

        CREATE TABLE IF NOT EXISTS
            public.snapshot_generation_retirement_events (
                event_id                       BIGINT
                                               GENERATED BY DEFAULT AS IDENTITY
                                               PRIMARY KEY,
                policy_epoch_id                UUID NOT NULL
                                               REFERENCES
                                                   public.snapshot_generation_retirement_policy_epochs(
                                                       policy_epoch_id)
                                               ON DELETE RESTRICT,
                job_id                         UUID
                                               REFERENCES
                                                   public.snapshot_generation_retirement_jobs(
                                                       job_id)
                                               ON DELETE RESTRICT,
                sequence                       INTEGER NOT NULL,
                event_type                     TEXT NOT NULL,
                payload                        JSONB NOT NULL,
                previous_hash                  TEXT,
                current_hash                   TEXT NOT NULL,
                created_at                     TIMESTAMPTZ NOT NULL
                                               DEFAULT clock_timestamp(),
                CONSTRAINT
                    ux_snapshot_generation_retirement_event_sequence
                    UNIQUE (
                        policy_epoch_id,
                        sequence),
                CONSTRAINT
                    ck_snapshot_generation_retirement_event_values
                    CHECK (
                        sequence > 0
                        AND event_type IN (
                            'policy_authorized',
                            'policy_deactivated',
                            'job_planned',
                            'job_expired',
                            'job_superseded')
                        AND (
                            (event_type LIKE 'job_%'
                                AND job_id IS NOT NULL)
                            OR
                            (event_type NOT LIKE 'job_%'
                                AND job_id IS NULL))),
                CONSTRAINT
                    ck_snapshot_generation_retirement_event_hashes
                    CHECK (
                        (previous_hash IS NULL
                            OR previous_hash ~ '^[0-9a-f]{64}$')
                        AND current_hash ~ '^[0-9a-f]{64}$')
            );

        CREATE INDEX IF NOT EXISTS
            ix_snapshot_generation_retirement_events_job
            ON public.snapshot_generation_retirement_events (
                job_id,
                sequence)
            WHERE job_id IS NOT NULL;

        CREATE OR REPLACE FUNCTION
            public.fst_reject_snapshot_generation_retirement_immutable_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        SET search_path = pg_catalog, public
        AS $retirement_immutable$
        BEGIN
            RAISE EXCEPTION
                'Snapshot-generation retirement authorization and event evidence is immutable.'
                USING ERRCODE = '55000';
        END
        $retirement_immutable$;

        CREATE OR REPLACE FUNCTION
            public.fst_snapshot_generation_retirement_index_configuration(
                p_table_oid BIGINT)
        RETURNS JSONB
        LANGUAGE sql
        STABLE
        SECURITY INVOKER
        SET search_path = pg_catalog, public
        AS $retirement_index_configuration$
            SELECT COALESCE(
                pg_catalog.jsonb_agg(
                    pg_catalog.jsonb_strip_nulls(
                        pg_catalog.jsonb_build_object(
                            'tableOid',
                                index_row.indrelid::BIGINT,
                            'indexOid',
                                index_relation.oid::BIGINT,
                            'indexRelfilenode',
                                index_relation.relfilenode::BIGINT,
                            'indexName',
                                index_relation.relname,
                            'relationKind',
                                index_relation.relkind::TEXT,
                            'isValid',
                                index_row.indisvalid,
                            'isReady',
                                index_row.indisready,
                            'isPrimary',
                                index_row.indisprimary,
                            'isUnique',
                                index_row.indisunique,
                            'accessMethod',
                                access_method.amname,
                            'tablespaceName',
                                COALESCE(
                                    tablespace.spcname,
                                    database_tablespace.spcname),
                            'parentIndexOid',
                                parent_index.oid::BIGINT,
                            'definition',
                                pg_catalog.pg_get_indexdef(
                                    index_relation.oid)))
                    ORDER BY
                        index_relation.relname,
                        index_relation.oid),
                '[]'::JSONB)
            FROM pg_catalog.pg_index index_row
            JOIN pg_catalog.pg_class index_relation
              ON index_relation.oid =
                    index_row.indexrelid
            JOIN pg_catalog.pg_am access_method
              ON access_method.oid =
                    index_relation.relam
            LEFT JOIN pg_catalog.pg_inherits inheritance
              ON inheritance.inhrelid =
                    index_relation.oid
            LEFT JOIN pg_catalog.pg_class parent_index
              ON parent_index.oid =
                    inheritance.inhparent
            LEFT JOIN pg_catalog.pg_tablespace tablespace
              ON tablespace.oid =
                    index_relation.reltablespace
            CROSS JOIN LATERAL (
                SELECT default_tablespace.spcname
                FROM pg_catalog.pg_database database
                JOIN pg_catalog.pg_tablespace default_tablespace
                  ON default_tablespace.oid =
                        database.dattablespace
                WHERE database.datname =
                        pg_catalog.current_database()
            ) database_tablespace
            WHERE index_row.indrelid::BIGINT =
                    p_table_oid
        $retirement_index_configuration$;

        CREATE OR REPLACE FUNCTION
            public.fst_lock_snapshot_generation_retirement_plan_target(
                p_cycle_id BIGINT,
                p_observation_id BIGINT)
        RETURNS BIGINT
        LANGUAGE plpgsql
        SECURITY INVOKER
        SET search_path = pg_catalog, public
        AS $retirement_plan_target$
        DECLARE
            cycle_row
                public.snapshot_generation_retention_cycles%ROWTYPE;
            observation_row
                public.snapshot_generation_retention_observations%ROWTYPE;
            state_row
                public.scrape_publication_state%ROWTYPE;
            expected_root_relation TEXT;
            expected_child_relation TEXT;
            observed_snapshot_parent_oid BIGINT;
            observed_root_partition_key TEXT;
            observed_root_partition_bound TEXT;
            observed_root_tablespace_name TEXT;
            observed_root_relation_options JSONB;
            observed_root_index_configuration JSONB;
            observed_child_parent_oid BIGINT;
            observed_child_relfilenode BIGINT;
            observed_partition_bound TEXT;
            observed_tablespace_name TEXT;
            observed_relation_kind TEXT;
            observed_persistence_kind TEXT;
            observed_access_method TEXT;
            observed_relation_options JSONB;
            observed_index_configuration JSONB;
            observed_total_bytes BIGINT;
            target_scrape_status TEXT;
            worker_operation_active BOOLEAN;
        BEGIN
            PERFORM pg_catalog.pg_advisory_xact_lock_shared(
                5067481511116519500);
            PERFORM pg_catalog.pg_advisory_xact_lock_shared(
                2026082301);
            PERFORM pg_catalog.pg_advisory_xact_lock_shared(
                pg_catalog.hashtextextended(
                    'fst.snapshot-generation-partition-ddl',
                    0));
            LOCK TABLE ONLY
                public.scrape_writer_failures
            IN SHARE MODE;
            LOCK TABLE ONLY
                public.snapshot_generation_retention_holds
            IN SHARE MODE;
            LOCK TABLE ONLY
                public.service_worker_status
            IN SHARE MODE;

            SELECT state.*
            INTO STRICT state_row
            FROM public.scrape_publication_state state
            WHERE state.id = TRUE
            FOR SHARE;

            SELECT cycle.*
            INTO STRICT cycle_row
            FROM public.snapshot_generation_retention_cycles
                cycle
            WHERE cycle.cycle_id = p_cycle_id;

            SELECT observation.*
            INTO STRICT observation_row
            FROM public.snapshot_generation_retention_observations
                observation
            WHERE observation.cycle_id =
                    p_cycle_id
              AND observation.observation_id =
                    p_observation_id;

            SELECT scrape.status
            INTO STRICT target_scrape_status
            FROM public.scrape_log scrape
            WHERE scrape.id =
                    observation_row.snapshot_id
            FOR SHARE;

            SELECT EXISTS (
                SELECT 1
                FROM public.service_worker_status worker
                WHERE worker.worker_key = 'scraper'
                  AND worker.current_operation_json
                        IS NOT NULL)
            INTO worker_operation_active;

            IF cycle_row.cycle_id IS DISTINCT FROM (
                    SELECT latest.cycle_id
                    FROM
                        public.snapshot_generation_retention_cycles
                            latest
                    ORDER BY latest.created_at DESC,
                             latest.cycle_id DESC
                    LIMIT 1)
               OR cycle_row.status <> 'observed'
               OR NOT cycle_row.report_only
               OR NOT cycle_row.oracle_agreement
               OR cycle_row.planner_version <> 3
               OR cycle_row.config_version <> 1
               OR cycle_row.blocked_count <> 0
               OR cycle_row.global_blockers <> '[]'::JSONB
               OR cycle_row.planner_child_set <>
                    cycle_row.oracle_child_set
               OR cycle_row.planner_live_set <>
                    cycle_row.oracle_live_set
               OR cycle_row.planner_candidate_set <>
                    cycle_row.oracle_candidate_set
               OR observation_row.classification <>
                    'candidate'
               OR NOT observation_row.report_only
               OR observation_row.planner_live
               OR observation_row.oracle_live
               OR pg_catalog.cardinality(
                    observation_row.blocker_codes) <> 0
               OR target_scrape_status <> 'completed'
               OR worker_operation_active
               OR EXISTS (
                    SELECT 1
                    FROM public.scrape_writer_failures failure
                    WHERE failure.scrape_id =
                            observation_row.snapshot_id
                      AND failure.instrument =
                            observation_row.instrument
                      AND failure.replayed_at IS NULL)
               OR EXISTS (
                    SELECT 1
                    FROM
                        public.snapshot_generation_retention_holds
                            hold
                    WHERE hold.instrument =
                            observation_row.instrument
                      AND hold.snapshot_id =
                            observation_row.snapshot_id
                      AND hold.released_at IS NULL)
            THEN
                RETURN NULL;
            END IF;

            expected_root_relation :=
                CASE observation_row.instrument
                    WHEN 'Solo_Guitar'
                        THEN 'leaderboard_entries_snapshot_solo_guitar'
                    WHEN 'Solo_Bass'
                        THEN 'leaderboard_entries_snapshot_solo_bass'
                    WHEN 'Solo_Drums'
                        THEN 'leaderboard_entries_snapshot_solo_drums'
                    WHEN 'Solo_Vocals'
                        THEN 'leaderboard_entries_snapshot_solo_vocals'
                    WHEN 'Solo_PeripheralGuitar'
                        THEN 'leaderboard_entries_snapshot_pro_guitar'
                    WHEN 'Solo_PeripheralBass'
                        THEN 'leaderboard_entries_snapshot_pro_bass'
                    WHEN 'Solo_PeripheralVocals'
                        THEN 'leaderboard_entries_snapshot_pro_vocals'
                    WHEN 'Solo_PeripheralCymbals'
                        THEN 'leaderboard_entries_snapshot_pro_cymbals'
                    WHEN 'Solo_PeripheralDrums'
                        THEN 'leaderboard_entries_snapshot_pro_drums'
                    ELSE NULL
                END;
            expected_child_relation :=
                expected_root_relation || '_s'
                || observation_row.snapshot_id::TEXT;

            IF observation_row.instrument = 'Solo_Bass'
                    AND observation_row.snapshot_id = 1308
               OR observation_row.root_schema <> 'public'
               OR observation_row.child_schema <> 'public'
               OR observation_row.root_relation <>
                    expected_root_relation
               OR observation_row.child_relation <>
                    expected_child_relation
            THEN
                RETURN NULL;
            END IF;

            IF state_row.current_publication_id IS DISTINCT FROM
                    cycle_row.trigger_publication_id
               OR state_row.published_scrape_id IS DISTINCT FROM
                    cycle_row.trigger_scrape_id
               OR state_row.public_reads_frozen
               OR state_row.working_publication_id IS NOT NULL
               OR
                    state_row.publication_commit_intent_started_at
                        IS NOT NULL
               OR state_row.max_score_mutation_gate_token
                        IS NOT NULL
               OR NOT (
                    (
                        state_row.improvement_notifications_scrape_id =
                            state_row.published_scrape_id
                        AND
                            state_row.improvement_notifications_status =
                                'completed'
                        AND
                            state_row.improvement_notifications_completed_at
                                IS NOT NULL
                        AND
                            state_row.improvement_notifications_projection_ready
                        AND
                            state_row.improvement_notifications_projection_scrape_id =
                                state_row.published_scrape_id)
                    OR
                    (
                        state_row.improvement_notifications_status =
                            'disabled'
                        AND
                            state_row.improvement_notifications_scrape_id
                                IS NULL
                        AND
                            state_row.improvement_notifications_completed_at
                                IS NULL
                        AND NOT
                            state_row.improvement_notifications_projection_ready
                        AND
                            state_row.improvement_notifications_projection_scrape_id
                                IS NULL))
               OR NOT EXISTS (
                    SELECT 1
                    FROM public.publication_generations generation
                    WHERE generation.publication_id =
                            state_row.current_publication_id
                      AND generation.scrape_id =
                            state_row.published_scrape_id
                      AND generation.status = 'current')
            THEN
                RETURN NULL;
            END IF;

            EXECUTE pg_catalog.format(
                'LOCK TABLE ONLY %I.%I IN SHARE ROW EXCLUSIVE MODE',
                observation_row.root_schema,
                observation_row.root_relation);
            EXECUTE pg_catalog.format(
                'LOCK TABLE ONLY %I.%I IN SHARE ROW EXCLUSIVE MODE',
                observation_row.child_schema,
                observation_row.child_relation);

            SELECT
                inheritance.inhparent::BIGINT,
                COALESCE(
                    pg_catalog.pg_get_partkeydef(
                        root.oid),
                    ''),
                COALESCE(
                    pg_catalog.pg_get_expr(
                        root.relpartbound,
                        root.oid,
                        TRUE),
                    ''),
                COALESCE(
                    tablespace.spcname,
                    database_tablespace.spcname),
                pg_catalog.to_jsonb(
                    ARRAY(
                        SELECT option
                        FROM pg_catalog.unnest(
                            COALESCE(
                                root.reloptions,
                                ARRAY[]::TEXT[]))
                            option
                        ORDER BY option)),
                public.fst_snapshot_generation_retirement_index_configuration(
                    root.oid::BIGINT)
            INTO STRICT
                observed_snapshot_parent_oid,
                observed_root_partition_key,
                observed_root_partition_bound,
                observed_root_tablespace_name,
                observed_root_relation_options,
                observed_root_index_configuration
            FROM pg_catalog.pg_class root
            JOIN pg_catalog.pg_namespace namespace
              ON namespace.oid =
                    root.relnamespace
            JOIN pg_catalog.pg_inherits inheritance
              ON inheritance.inhrelid = root.oid
            LEFT JOIN pg_catalog.pg_tablespace tablespace
              ON tablespace.oid =
                    root.reltablespace
            CROSS JOIN LATERAL (
                SELECT default_tablespace.spcname
                FROM pg_catalog.pg_database database
                JOIN pg_catalog.pg_tablespace default_tablespace
                  ON default_tablespace.oid =
                        database.dattablespace
                WHERE database.datname =
                        pg_catalog.current_database()
            ) database_tablespace
            WHERE namespace.nspname =
                    observation_row.root_schema
              AND root.relname =
                    observation_row.root_relation
              AND root.oid::BIGINT =
                    observation_row.root_oid;

            SELECT
                inheritance.inhparent::BIGINT,
                child.relfilenode::BIGINT,
                COALESCE(
                    pg_catalog.pg_get_expr(
                        child.relpartbound,
                        child.oid,
                        TRUE),
                    ''),
                COALESCE(
                    tablespace.spcname,
                    database_tablespace.spcname),
                child.relkind::TEXT,
                child.relpersistence::TEXT,
                access_method.amname,
                pg_catalog.to_jsonb(
                    ARRAY(
                        SELECT option
                        FROM pg_catalog.unnest(
                            COALESCE(
                                child.reloptions,
                                ARRAY[]::TEXT[]))
                            option
                        ORDER BY option)),
                public.fst_snapshot_generation_retirement_index_configuration(
                    child.oid::BIGINT),
                pg_catalog.pg_total_relation_size(
                    child.oid)::BIGINT
            INTO STRICT
                observed_child_parent_oid,
                observed_child_relfilenode,
                observed_partition_bound,
                observed_tablespace_name,
                observed_relation_kind,
                observed_persistence_kind,
                observed_access_method,
                observed_relation_options,
                observed_index_configuration,
                observed_total_bytes
            FROM pg_catalog.pg_class child
            JOIN pg_catalog.pg_namespace namespace
              ON namespace.oid =
                    child.relnamespace
            JOIN pg_catalog.pg_inherits inheritance
              ON inheritance.inhrelid = child.oid
            JOIN pg_catalog.pg_am access_method
              ON access_method.oid = child.relam
            LEFT JOIN pg_catalog.pg_tablespace tablespace
              ON tablespace.oid =
                    child.reltablespace
            CROSS JOIN LATERAL (
                SELECT default_tablespace.spcname
                FROM pg_catalog.pg_database database
                JOIN pg_catalog.pg_tablespace default_tablespace
                  ON default_tablespace.oid =
                        database.dattablespace
                WHERE database.datname =
                        pg_catalog.current_database()
            ) database_tablespace
            WHERE namespace.nspname =
                    observation_row.child_schema
              AND child.relname =
                    observation_row.child_relation
              AND child.oid::BIGINT =
                    observation_row.child_oid;

            IF observed_snapshot_parent_oid IS DISTINCT FROM
                    observation_row.snapshot_parent_oid
               OR observed_root_partition_key IS DISTINCT FROM
                    observation_row.root_partition_key
               OR observed_root_partition_bound IS DISTINCT FROM
                    observation_row.root_partition_bound
               OR observed_root_tablespace_name IS DISTINCT FROM
                    observation_row.root_tablespace_name
               OR observed_root_relation_options IS DISTINCT FROM
                    observation_row.root_relation_options
               OR observed_root_index_configuration IS DISTINCT FROM
                    observation_row.root_index_configuration
               OR observed_child_parent_oid IS DISTINCT FROM
                    observation_row.root_oid
               OR observed_child_relfilenode IS DISTINCT FROM
                    observation_row.child_relfilenode
               OR observed_partition_bound IS DISTINCT FROM
                    observation_row.partition_bound
               OR observed_tablespace_name IS DISTINCT FROM
                    observation_row.tablespace_name
               OR observed_relation_kind IS DISTINCT FROM
                    observation_row.relation_kind
               OR observed_persistence_kind IS DISTINCT FROM
                    observation_row.persistence_kind
               OR observed_access_method IS DISTINCT FROM
                    observation_row.access_method
               OR observed_relation_options IS DISTINCT FROM
                    observation_row.relation_options
               OR observed_index_configuration IS DISTINCT FROM
                    observation_row.index_configuration
               OR observed_total_bytes IS DISTINCT FROM
                    observation_row.total_bytes
               OR observed_total_bytes <= 0
            THEN
                RETURN NULL;
            END IF;

            RETURN observed_total_bytes;
        EXCEPTION
            WHEN NO_DATA_FOUND OR TOO_MANY_ROWS
            THEN
                RETURN NULL;
        END
        $retirement_plan_target$;

        CREATE OR REPLACE FUNCTION
            public.fst_validate_snapshot_generation_retirement_job_insert()
        RETURNS trigger
        LANGUAGE plpgsql
        SET search_path = pg_catalog, public
        AS $retirement_job_insert$
        DECLARE
            policy_row
                public.snapshot_generation_retirement_policy_epochs%ROWTYPE;
            cycle_row
                public.snapshot_generation_retention_cycles%ROWTYPE;
            observation_row
                public.snapshot_generation_retention_observations%ROWTYPE;
            control_enabled BOOLEAN;
            control_policy UUID;
            existing_jobs INTEGER;
            existing_bytes BIGINT;
            current_child_bytes BIGINT;
            expected_instrument_order SMALLINT;
        BEGIN
            current_child_bytes :=
                public.fst_lock_snapshot_generation_retirement_plan_target(
                    NEW.cycle_id,
                    NEW.observation_id);

            IF NEW.state <> 'planned'
               OR NEW.state_reason IS NOT NULL
               OR NEW.terminal_at IS NOT NULL
            THEN
                RAISE EXCEPTION
                    'A retirement job must be inserted in planned state.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT control.enabled,
                   control.active_policy_epoch_id
            INTO control_enabled,
                 control_policy
            FROM public.snapshot_generation_retirement_control
                control
            WHERE control.control_key = TRUE
            FOR UPDATE;

            SELECT *
            INTO policy_row
            FROM public.snapshot_generation_retirement_policy_epochs
                policy
            WHERE policy.policy_epoch_id =
                    NEW.policy_epoch_id
            FOR UPDATE;

            IF NOT FOUND
               OR NOT control_enabled
               OR control_policy IS DISTINCT FROM
                    NEW.policy_epoch_id
               OR clock_timestamp() <
                    policy_row.not_before
               OR clock_timestamp() >=
                    policy_row.expires_at
               OR NEW.source_identity_sha256 <>
                    policy_row.source_identity_sha256
            THEN
                RAISE EXCEPTION
                    'The retirement job is not admitted by the active policy.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT COUNT(*)::INTEGER,
                   COALESCE(SUM(job.target_bytes), 0)::BIGINT
            INTO existing_jobs,
                 existing_bytes
            FROM public.snapshot_generation_retirement_jobs job
            WHERE job.policy_epoch_id =
                    NEW.policy_epoch_id;

            IF existing_jobs >= policy_row.max_jobs
               OR existing_bytes >
                    policy_row.max_total_bytes -
                    NEW.target_bytes
            THEN
                RAISE EXCEPTION
                    'The retirement policy budget is exhausted.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT *
            INTO cycle_row
            FROM public.snapshot_generation_retention_cycles
                cycle
            WHERE cycle.cycle_id = NEW.cycle_id;

            SELECT *
            INTO observation_row
            FROM public.snapshot_generation_retention_observations
                observation
            WHERE observation.cycle_id =
                    NEW.cycle_id
              AND observation.observation_id =
                    NEW.observation_id;

            IF cycle_row.cycle_id IS NULL
               OR observation_row.observation_id IS NULL
               OR cycle_row.status <> 'observed'
               OR NOT cycle_row.report_only
               OR NOT cycle_row.oracle_agreement
               OR cycle_row.planner_version <> 3
               OR cycle_row.config_version <> 1
               OR cycle_row.blocked_count <> 0
               OR cycle_row.global_blockers <> '[]'::JSONB
               OR cycle_row.planner_child_set <>
                    cycle_row.oracle_child_set
               OR cycle_row.planner_live_set <>
                    cycle_row.oracle_live_set
               OR cycle_row.planner_candidate_set <>
                    cycle_row.oracle_candidate_set
               OR cycle_row.cycle_id IS DISTINCT FROM (
                    SELECT latest.cycle_id
                    FROM
                        public.snapshot_generation_retention_cycles
                            latest
                    ORDER BY latest.created_at DESC,
                             latest.cycle_id DESC
                    LIMIT 1)
               OR observation_row.classification <>
                    'candidate'
               OR NOT observation_row.report_only
               OR observation_row.planner_live
               OR observation_row.oracle_live
               OR cardinality(
                    observation_row.blocker_codes) <> 0
            THEN
                RAISE EXCEPTION
                    'The retirement target is not from an accepted report-only cycle.'
                    USING ERRCODE = '55000';
            END IF;

            expected_instrument_order :=
                CASE observation_row.instrument
                    WHEN 'Solo_Guitar' THEN 0
                    WHEN 'Solo_Bass' THEN 1
                    WHEN 'Solo_Vocals' THEN 2
                    WHEN 'Solo_Drums' THEN 3
                    WHEN 'Solo_PeripheralGuitar' THEN 4
                    WHEN 'Solo_PeripheralBass' THEN 5
                    WHEN 'Solo_PeripheralVocals' THEN 6
                    WHEN 'Solo_PeripheralCymbals' THEN 7
                    WHEN 'Solo_PeripheralDrums' THEN 8
                    ELSE NULL
                END;

            IF NEW.trigger_scrape_id <>
                    cycle_row.trigger_scrape_id
               OR NEW.trigger_publication_id <>
                    cycle_row.trigger_publication_id
               OR NEW.instrument <>
                    observation_row.instrument
               OR NEW.instrument_order IS DISTINCT FROM
                    expected_instrument_order
               OR NEW.snapshot_id <>
                    observation_row.snapshot_id
               OR NEW.root_schema <>
                    observation_row.root_schema
               OR NEW.root_relation <>
                    observation_row.root_relation
               OR NEW.root_oid <>
                    observation_row.root_oid
               OR NEW.child_schema <>
                    observation_row.child_schema
               OR NEW.child_relation <>
                    observation_row.child_relation
               OR NEW.child_oid <>
                    observation_row.child_oid
               OR NEW.child_relfilenode <>
                    observation_row.child_relfilenode
               OR NEW.stable_child_identity_hash <>
                    observation_row.stable_child_identity_hash
               OR NEW.stable_config_schema_hash <>
                    observation_row.stable_config_schema_hash
            THEN
                RAISE EXCEPTION
                    'The retirement job target differs from planner evidence.'
                    USING ERRCODE = '55000';
            END IF;

            IF current_child_bytes IS NULL
               OR current_child_bytes IS DISTINCT FROM
                    NEW.target_bytes
            THEN
                RAISE EXCEPTION
                    'The retirement job target physical identity changed.'
                    USING ERRCODE = '55000';
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM public.scrape_publication_state state
                JOIN public.publication_generations generation
                  ON generation.publication_id =
                        state.current_publication_id
                WHERE state.id = TRUE
                  AND state.current_publication_id =
                        NEW.trigger_publication_id
                  AND state.published_scrape_id =
                        NEW.trigger_scrape_id
                  AND state.working_publication_id IS NULL
                  AND NOT state.public_reads_frozen
                  AND
                        state.publication_commit_intent_started_at
                            IS NULL
                  AND state.max_score_mutation_gate_token
                            IS NULL
                  AND (
                        (
                            state.improvement_notifications_scrape_id =
                                state.published_scrape_id
                            AND
                                state.improvement_notifications_status =
                                    'completed'
                            AND
                                state.improvement_notifications_completed_at
                                    IS NOT NULL
                            AND
                                state.improvement_notifications_projection_ready
                            AND
                                state.improvement_notifications_projection_scrape_id =
                                    state.published_scrape_id)
                        OR
                        (
                            state.improvement_notifications_status =
                                'disabled'
                            AND
                                state.improvement_notifications_scrape_id
                                    IS NULL
                            AND
                                state.improvement_notifications_completed_at
                                    IS NULL
                            AND NOT
                                state.improvement_notifications_projection_ready
                            AND
                                state.improvement_notifications_projection_scrape_id
                                    IS NULL))
                  AND generation.status = 'current'
                  AND generation.scrape_id =
                        NEW.trigger_scrape_id)
            THEN
                RAISE EXCEPTION
                    'The retirement job is not bound to the current idle publication.'
                    USING ERRCODE = '55000';
            END IF;

            RETURN NEW;
        END
        $retirement_job_insert$;

        CREATE OR REPLACE FUNCTION
            public.fst_guard_snapshot_generation_retirement_job_update()
        RETURNS trigger
        LANGUAGE plpgsql
        SET search_path = pg_catalog, public
        AS $retirement_job_update$
        BEGIN
            IF ROW(
                    OLD.job_id,
                    OLD.schema_version,
                    OLD.tool_id,
                    OLD.policy_epoch_id,
                    OLD.cycle_id,
                    OLD.observation_id,
                    OLD.trigger_scrape_id,
                    OLD.trigger_publication_id,
                    OLD.instrument,
                    OLD.instrument_order,
                    OLD.snapshot_id,
                    OLD.root_schema,
                    OLD.root_relation,
                    OLD.root_oid,
                    OLD.child_schema,
                    OLD.child_relation,
                    OLD.child_oid,
                    OLD.child_relfilenode,
                    OLD.stable_child_identity_hash,
                    OLD.stable_config_schema_hash,
                    OLD.target_bytes,
                    OLD.source_identity_sha256,
                    OLD.plan_digest,
                    OLD.planned_at,
                    OLD.created_at)
                IS DISTINCT FROM
                ROW(
                    NEW.job_id,
                    NEW.schema_version,
                    NEW.tool_id,
                    NEW.policy_epoch_id,
                    NEW.cycle_id,
                    NEW.observation_id,
                    NEW.trigger_scrape_id,
                    NEW.trigger_publication_id,
                    NEW.instrument,
                    NEW.instrument_order,
                    NEW.snapshot_id,
                    NEW.root_schema,
                    NEW.root_relation,
                    NEW.root_oid,
                    NEW.child_schema,
                    NEW.child_relation,
                    NEW.child_oid,
                    NEW.child_relfilenode,
                    NEW.stable_child_identity_hash,
                    NEW.stable_config_schema_hash,
                    NEW.target_bytes,
                    NEW.source_identity_sha256,
                    NEW.plan_digest,
                    NEW.planned_at,
                    NEW.created_at)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation retirement job identity is immutable.'
                    USING ERRCODE = '55000';
            END IF;

            IF OLD.state <> 'planned'
               OR NEW.state NOT IN (
                    'expired',
                    'superseded')
               OR NEW.state = OLD.state
               OR NEW.state_reason IS NULL
               OR NEW.terminal_at IS NULL
               OR btrim(NEW.state_reason) = ''
               OR NEW.updated_at < OLD.updated_at
               OR NEW.updated_at <>
                    NEW.terminal_at
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation retirement job transition is invalid.'
                    USING ERRCODE = '55000';
            END IF;

            RETURN NEW;
        END
        $retirement_job_update$;

        DO $retirement_triggers$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_trigger trigger_row
                WHERE trigger_row.tgrelid =
                        'public.snapshot_generation_retirement_policy_epochs'::REGCLASS
                  AND trigger_row.tgname =
                        'trg_reject_snapshot_generation_retirement_policy_mutation'
                  AND NOT trigger_row.tgisinternal)
            THEN
                CREATE TRIGGER
                    trg_reject_snapshot_generation_retirement_policy_mutation
                BEFORE UPDATE OR DELETE OR TRUNCATE
                ON public.snapshot_generation_retirement_policy_epochs
                FOR EACH STATEMENT
                EXECUTE FUNCTION
                    public.fst_reject_snapshot_generation_retirement_immutable_mutation();
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_trigger trigger_row
                WHERE trigger_row.tgrelid =
                        'public.snapshot_generation_retirement_events'::REGCLASS
                  AND trigger_row.tgname =
                        'trg_reject_snapshot_generation_retirement_event_mutation'
                  AND NOT trigger_row.tgisinternal)
            THEN
                CREATE TRIGGER
                    trg_reject_snapshot_generation_retirement_event_mutation
                BEFORE UPDATE OR DELETE OR TRUNCATE
                ON public.snapshot_generation_retirement_events
                FOR EACH STATEMENT
                EXECUTE FUNCTION
                    public.fst_reject_snapshot_generation_retirement_immutable_mutation();
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_trigger trigger_row
                WHERE trigger_row.tgrelid =
                        'public.snapshot_generation_retirement_jobs'::REGCLASS
                  AND trigger_row.tgname =
                        'trg_validate_snapshot_generation_retirement_job_insert'
                  AND NOT trigger_row.tgisinternal)
            THEN
                CREATE TRIGGER
                    trg_validate_snapshot_generation_retirement_job_insert
                BEFORE INSERT
                ON public.snapshot_generation_retirement_jobs
                FOR EACH ROW
                EXECUTE FUNCTION
                    public.fst_validate_snapshot_generation_retirement_job_insert();
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_trigger trigger_row
                WHERE trigger_row.tgrelid =
                        'public.snapshot_generation_retirement_jobs'::REGCLASS
                  AND trigger_row.tgname =
                        'trg_guard_snapshot_generation_retirement_job_update'
                  AND NOT trigger_row.tgisinternal)
            THEN
                CREATE TRIGGER
                    trg_guard_snapshot_generation_retirement_job_update
                BEFORE UPDATE
                ON public.snapshot_generation_retirement_jobs
                FOR EACH ROW
                EXECUTE FUNCTION
                    public.fst_guard_snapshot_generation_retirement_job_update();
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_trigger trigger_row
                WHERE trigger_row.tgrelid =
                        'public.snapshot_generation_retirement_jobs'::REGCLASS
                  AND trigger_row.tgname =
                        'trg_reject_snapshot_generation_retirement_job_removal'
                  AND NOT trigger_row.tgisinternal)
            THEN
                CREATE TRIGGER
                    trg_reject_snapshot_generation_retirement_job_removal
                BEFORE DELETE OR TRUNCATE
                ON public.snapshot_generation_retirement_jobs
                FOR EACH STATEMENT
                EXECUTE FUNCTION
                    public.fst_reject_snapshot_generation_retirement_immutable_mutation();
            END IF;
        END
        $retirement_triggers$;

        ALTER TABLE
            public.snapshot_generation_retirement_policy_epochs
        ENABLE TRIGGER
            trg_reject_snapshot_generation_retirement_policy_mutation;

        ALTER TABLE
            public.snapshot_generation_retirement_events
        ENABLE TRIGGER
            trg_reject_snapshot_generation_retirement_event_mutation;

        ALTER TABLE
            public.snapshot_generation_retirement_jobs
        ENABLE TRIGGER
            trg_validate_snapshot_generation_retirement_job_insert;

        ALTER TABLE
            public.snapshot_generation_retirement_jobs
        ENABLE TRIGGER
            trg_guard_snapshot_generation_retirement_job_update;

        ALTER TABLE
            public.snapshot_generation_retirement_jobs
        ENABLE TRIGGER
            trg_reject_snapshot_generation_retirement_job_removal;

        REVOKE ALL ON TABLE
            public.snapshot_generation_retirement_policy_epochs,
            public.snapshot_generation_retirement_control,
            public.snapshot_generation_retirement_jobs,
            public.snapshot_generation_retirement_events
        FROM PUBLIC;

        REVOKE ALL ON SEQUENCE
            public.snapshot_generation_retirement_events_event_id_seq
        FROM PUBLIC;

        REVOKE ALL ON FUNCTION
            public.fst_reject_snapshot_generation_retirement_immutable_mutation(),
            public.fst_snapshot_generation_retirement_index_configuration(
                BIGINT),
            public.fst_lock_snapshot_generation_retirement_plan_target(
                BIGINT,
                BIGINT),
            public.fst_validate_snapshot_generation_retirement_job_insert(),
            public.fst_guard_snapshot_generation_retirement_job_update()
        FROM PUBLIC;
        """;
}
