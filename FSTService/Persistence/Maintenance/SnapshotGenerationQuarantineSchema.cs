namespace FSTService.Persistence.Maintenance;

public static class SnapshotGenerationQuarantineContract
{
    public const int SchemaVersion = 1;
    public const string ToolId =
        "fst.snapshot-generation-quarantine.v1";
    public const string QuarantineSchema =
        "fst_snapshot_quarantine";
    public const string SnapshotDdlLockName =
        "fst.snapshot-generation-partition-ddl";
    public const long RegistrationAdvisoryLockKey =
        5067481511116518500L;
    public const long ServiceMaintenanceAdvisoryLockKey =
        2026050901L;
    public const long PublicationAdvisoryLockKey =
        5067481511116519500L;
    public const long PlannerAdvisoryLockKey =
        2026082301L;
    public const long ExecutorAdvisoryLockKey = 2026083001;
}

public static class SnapshotGenerationQuarantineSchema
{
    public const string Sql = """
        CREATE SCHEMA IF NOT EXISTS fst_snapshot_quarantine;
        REVOKE ALL ON SCHEMA fst_snapshot_quarantine FROM PUBLIC;

        CREATE TABLE IF NOT EXISTS
            snapshot_generation_quarantine_operations (
                operation_id                   TEXT PRIMARY KEY,
                schema_version                 INTEGER NOT NULL,
                tool_id                        TEXT NOT NULL,
                plan_digest                    TEXT NOT NULL UNIQUE,
                archive_manifest_sha256        TEXT NOT NULL,
                archive_proof_manifest_sha256  TEXT NOT NULL,
                source_evidence_manifest_sha256 TEXT NOT NULL,
                baseline_route_manifest_sha256 TEXT NOT NULL,
                candidate_route_manifest_sha256 TEXT NOT NULL,
                cycle_id                       BIGINT NOT NULL,
                observation_id                 BIGINT NOT NULL,
                trigger_scrape_id              BIGINT NOT NULL
                                                REFERENCES scrape_log(id)
                                                ON DELETE RESTRICT,
                trigger_publication_id         BIGINT NOT NULL
                                                REFERENCES
                                                    publication_generations(
                                                        publication_id)
                                                ON DELETE RESTRICT,
                instrument                     TEXT NOT NULL,
                snapshot_id                    BIGINT NOT NULL
                                                REFERENCES scrape_log(id)
                                                ON DELETE RESTRICT,
                root_schema                    TEXT NOT NULL,
                root_relation                  TEXT NOT NULL,
                root_oid                       BIGINT NOT NULL,
                child_schema                   TEXT NOT NULL,
                child_relation                 TEXT NOT NULL,
                child_oid                      BIGINT NOT NULL,
                child_relfilenode              BIGINT NOT NULL,
                quarantine_schema              TEXT NOT NULL,
                quarantine_relation            TEXT NOT NULL,
                snapshot_check_constraint      TEXT NOT NULL,
                mutation_guard_trigger          TEXT NOT NULL,
                default_partition_schema        TEXT NOT NULL,
                default_partition_relation      TEXT NOT NULL,
                default_partition_oid           BIGINT NOT NULL,
                default_exclusion_constraint    TEXT NOT NULL,
                stable_child_identity_hash     TEXT NOT NULL,
                stable_config_schema_hash      TEXT NOT NULL,
                row_count                      BIGINT NOT NULL,
                row_fingerprint_sha256         TEXT NOT NULL,
                logical_catalog_sha256         TEXT NOT NULL,
                total_bytes                    BIGINT NOT NULL,
                hold_id                        BIGINT NOT NULL UNIQUE
                                                REFERENCES
                                                    snapshot_generation_retention_holds(
                                                        hold_id)
                                                ON DELETE RESTRICT,
                approved_by                    TEXT NOT NULL,
                approval_reference             TEXT NOT NULL,
                preflight_evidence              JSONB NOT NULL,
                quarantine_evidence             JSONB NOT NULL,
                quarantined_at                 TIMESTAMPTZ NOT NULL
                                                DEFAULT clock_timestamp(),
                CONSTRAINT
                    fk_snapshot_generation_quarantine_observation
                    FOREIGN KEY (cycle_id, observation_id)
                    REFERENCES
                        snapshot_generation_retention_observations(
                            cycle_id,
                            observation_id)
                    ON DELETE RESTRICT,
                CONSTRAINT
                    ck_snapshot_generation_quarantine_operation_id
                    CHECK (
                        operation_id ~ '^[0-9a-f]{32}$'),
                CONSTRAINT
                    ck_snapshot_generation_quarantine_contract
                    CHECK (
                        schema_version = 1
                        AND tool_id =
                            'fst.snapshot-generation-quarantine.v1'),
                CONSTRAINT
                    ck_snapshot_generation_quarantine_hashes
                    CHECK (
                        plan_digest ~ '^[0-9a-f]{64}$'
                        AND archive_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND archive_proof_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND source_evidence_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND baseline_route_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND candidate_route_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND stable_child_identity_hash
                            ~ '^[0-9a-f]{64}$'
                        AND stable_config_schema_hash
                            ~ '^[0-9a-f]{64}$'
                        AND row_fingerprint_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND logical_catalog_sha256
                            ~ '^[0-9a-f]{64}$'),
                CONSTRAINT
                    ck_snapshot_generation_quarantine_instrument
                    CHECK (
                        instrument IN (
                            'Solo_Guitar',
                            'Solo_Bass',
                            'Solo_Vocals',
                            'Solo_Drums',
                            'Solo_PeripheralGuitar',
                            'Solo_PeripheralBass',
                            'Solo_PeripheralVocals',
                            'Solo_PeripheralCymbals',
                            'Solo_PeripheralDrums')),
                CONSTRAINT
                    ck_snapshot_generation_quarantine_identity
                    CHECK (
                        cycle_id > 0
                        AND observation_id > 0
                        AND trigger_scrape_id > 0
                        AND trigger_publication_id > 0
                        AND snapshot_id > 0
                        AND root_oid > 0
                        AND child_oid > 0
                        AND child_relfilenode > 0
                        AND row_count >= 0
                        AND total_bytes >= 0),
                CONSTRAINT
                    ck_snapshot_generation_quarantine_names
                    CHECK (
                        root_schema = 'public'
                        AND child_schema = 'public'
                        AND quarantine_schema =
                            'fst_snapshot_quarantine'
                        AND root_relation <> ''
                        AND child_relation <> ''
                        AND quarantine_relation <> ''
                        AND snapshot_check_constraint <> ''
                        AND mutation_guard_trigger <> ''
                        AND default_partition_schema = 'public'
                        AND default_partition_relation <> ''
                        AND default_partition_oid > 0
                        AND default_exclusion_constraint <> ''),
                CONSTRAINT
                    ck_snapshot_generation_quarantine_approval
                    CHECK (
                        approved_by <> ''
                        AND approval_reference <> ''),
                CONSTRAINT
                    ck_snapshot_generation_quarantine_evidence
                    CHECK (
                        jsonb_typeof(preflight_evidence) = 'object'
                        AND jsonb_typeof(quarantine_evidence) = 'object')
            );

        CREATE INDEX IF NOT EXISTS
            ix_snapshot_generation_quarantine_operations_target
            ON snapshot_generation_quarantine_operations (
                instrument,
                snapshot_id,
                quarantined_at DESC);

        CREATE TABLE IF NOT EXISTS
            snapshot_generation_quarantine_reattachments (
                operation_id                   TEXT PRIMARY KEY
                                                REFERENCES
                                                    snapshot_generation_quarantine_operations(
                                                        operation_id)
                                                ON DELETE RESTRICT,
                reattached_by                  TEXT NOT NULL,
                reattach_reference             TEXT NOT NULL,
                reattach_evidence              JSONB NOT NULL,
                reattached_at                  TIMESTAMPTZ NOT NULL
                                                DEFAULT clock_timestamp(),
                CONSTRAINT
                    ck_snapshot_generation_quarantine_reattach_values
                    CHECK (
                        reattached_by <> ''
                        AND reattach_reference <> ''
                        AND jsonb_typeof(reattach_evidence) =
                            'object')
            );

        CREATE TABLE IF NOT EXISTS
            snapshot_generation_quarantine_attestations (
                attestation_id                 BIGINT
                                                GENERATED BY DEFAULT AS IDENTITY
                                                PRIMARY KEY,
                operation_id                   TEXT NOT NULL
                                                REFERENCES
                                                    snapshot_generation_quarantine_operations(
                                                        operation_id)
                                                ON DELETE RESTRICT,
                stage                          TEXT NOT NULL,
                publication_id                 BIGINT NOT NULL,
                published_scrape_id            BIGINT NOT NULL,
                route_count                    INTEGER NOT NULL,
                status_parity                  BOOLEAN NOT NULL,
                semantic_json_parity           BOOLEAN NOT NULL,
                difference_count               INTEGER NOT NULL,
                baseline_route_manifest_sha256 TEXT NOT NULL,
                candidate_route_manifest_sha256 TEXT NOT NULL,
                database_evidence              JSONB NOT NULL,
                evidence_sha256                TEXT NOT NULL,
                attested_by                    TEXT NOT NULL,
                attested_at                    TIMESTAMPTZ NOT NULL
                                                DEFAULT clock_timestamp(),
                CONSTRAINT
                    ck_snapshot_generation_quarantine_attestation_stage
                    CHECK (
                        stage IN (
                            'quarantined',
                            'soak',
                            'reattached')),
                CONSTRAINT
                    ck_snapshot_generation_quarantine_attestation_values
                    CHECK (
                        publication_id > 0
                        AND published_scrape_id > 0
                        AND route_count = 55
                        AND difference_count >= 0
                        AND attested_by <> ''
                        AND jsonb_typeof(database_evidence) =
                            'object'),
                CONSTRAINT
                    ck_snapshot_generation_quarantine_attestation_hashes
                    CHECK (
                        baseline_route_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND candidate_route_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND evidence_sha256 ~ '^[0-9a-f]{64}$'),
                CONSTRAINT
                    ck_snapshot_generation_quarantine_attestation_parity
                    CHECK (
                        (difference_count = 0
                            AND status_parity
                            AND semantic_json_parity)
                        OR difference_count > 0)
            );

        CREATE INDEX IF NOT EXISTS
            ix_snapshot_generation_quarantine_attestations_operation
            ON snapshot_generation_quarantine_attestations (
                operation_id,
                attestation_id);

        CREATE TABLE IF NOT EXISTS
            snapshot_generation_quarantine_index_renames (
                operation_id                   TEXT NOT NULL
                                                REFERENCES
                                                    snapshot_generation_quarantine_operations(
                                                        operation_id)
                                                ON DELETE RESTRICT,
                index_role                     TEXT NOT NULL,
                index_oid                      BIGINT NOT NULL,
                index_relfilenode              BIGINT NOT NULL,
                old_index_name                 TEXT NOT NULL,
                new_index_name                 TEXT NOT NULL,
                old_constraint_name            TEXT,
                new_constraint_name            TEXT,
                source_phase                   TEXT NOT NULL,
                semantic_before                JSONB NOT NULL,
                semantic_after                 JSONB NOT NULL,
                semantic_before_sha256         TEXT NOT NULL,
                semantic_after_sha256          TEXT NOT NULL,
                backend_pid                    INTEGER NOT NULL,
                transaction_id                 TEXT NOT NULL,
                renamed_at                     TIMESTAMPTZ NOT NULL
                                                DEFAULT clock_timestamp(),
                PRIMARY KEY (operation_id, index_role),
                CONSTRAINT
                    ck_snapshot_generation_quarantine_index_rename_role
                    CHECK (index_role IN ('pk', 'score')),
                CONSTRAINT
                    ck_snapshot_generation_quarantine_index_rename_phase
                    CHECK (
                        source_phase IN (
                            'quarantine',
                            'reattach_repair')),
                CONSTRAINT
                    ck_snapshot_generation_quarantine_index_rename_identity
                    CHECK (
                        index_oid > 0
                        AND index_relfilenode > 0
                        AND old_index_name <> ''
                        AND new_index_name =
                            'sgqi_' || operation_id || '_' ||
                            index_role
                        AND (
                            (
                                index_role = 'pk'
                                AND old_constraint_name IS NOT NULL
                                AND new_constraint_name =
                                    new_index_name)
                            OR (
                                index_role = 'score'
                                AND old_constraint_name IS NULL
                                AND new_constraint_name IS NULL))
                        AND transaction_id ~ '^[0-9]+$'),
                CONSTRAINT
                    ck_snapshot_generation_quarantine_index_rename_hashes
                    CHECK (
                        semantic_before_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND semantic_after_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND semantic_before_sha256 =
                            semantic_after_sha256),
                CONSTRAINT
                    ck_snapshot_generation_quarantine_index_rename_evidence
                    CHECK (
                        jsonb_typeof(semantic_before) =
                            'object'
                        AND jsonb_typeof(semantic_after) =
                            'object'
                        AND semantic_before = semantic_after)
            );

        CREATE OR REPLACE FUNCTION
            fst_reject_snapshot_generation_quarantine_evidence_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $quarantine_immutable$
        BEGIN
            RAISE EXCEPTION
                'Snapshot-generation quarantine evidence is immutable.'
                USING ERRCODE = '55000';
        END
        $quarantine_immutable$;

        CREATE OR REPLACE FUNCTION
            fst_reject_snapshot_generation_quarantine_relation_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $quarantine_relation_immutable$
        BEGIN
            RAISE EXCEPTION
                'Quarantined snapshot-generation rows are immutable.'
                USING ERRCODE = '55000';
        END
        $quarantine_relation_immutable$;

        DO $quarantine_triggers$
        DECLARE
            relation_name TEXT;
            trigger_name TEXT;
        BEGIN
            FOR relation_name, trigger_name IN
                SELECT relation_value, trigger_value
                FROM (
                    VALUES
                        (
                            'snapshot_generation_quarantine_operations',
                            'trg_sgq_operations_immutable'),
                        (
                            'snapshot_generation_quarantine_reattachments',
                            'trg_sgq_reattachments_immutable'),
                        (
                            'snapshot_generation_quarantine_attestations',
                            'trg_sgq_attestations_immutable'),
                        (
                            'snapshot_generation_quarantine_index_renames',
                            'trg_sgq_index_renames_immutable')
                ) names(relation_value, trigger_value)
            LOOP
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_trigger trigger_row
                    WHERE trigger_row.tgrelid =
                            to_regclass(
                                'public.' || relation_name)
                      AND trigger_row.tgname = trigger_name
                      AND NOT trigger_row.tgisinternal
                ) THEN
                    EXECUTE format(
                        'CREATE TRIGGER %I BEFORE UPDATE OR DELETE OR TRUNCATE ON public.%I FOR EACH STATEMENT EXECUTE FUNCTION fst_reject_snapshot_generation_quarantine_evidence_mutation()',
                        trigger_name,
                        relation_name);
                END IF;
            END LOOP;
        END
        $quarantine_triggers$;

        CREATE OR REPLACE FUNCTION
            fst_snapshot_generation_index_inventory(
                p_child_oid BIGINT,
                p_expected_root_oid BIGINT,
                p_expect_attached BOOLEAN)
        RETURNS JSONB
        LANGUAGE plpgsql
        SECURITY INVOKER
        SET search_path = pg_catalog, public
        AS $quarantine_index_inventory$
        DECLARE
            inventory JSONB;
            pk JSONB;
            score JSONB;
        BEGIN
            WITH expected_parent AS (
                SELECT
                    CASE
                        WHEN root_index.indisprimary
                            THEN 'pk'
                        ELSE 'score'
                    END AS index_role,
                    root_index.indexrelid::BIGINT
                        AS root_index_oid,
                    top_inheritance.inhparent::BIGINT
                        AS top_index_oid,
                    top_index_relation.relname
                        AS top_index_name,
                    to_jsonb(
                        root_index.indclass::OID[])
                        AS opclass_oids,
                    to_jsonb(
                        root_index.indcollation::OID[])
                        AS collation_oids,
                    to_jsonb(
                        root_index.indoption::SMALLINT[])
                        AS ind_options
                FROM pg_index root_index
                JOIN pg_inherits top_inheritance
                  ON top_inheritance.inhrelid =
                        root_index.indexrelid
                JOIN pg_class top_index_relation
                  ON top_index_relation.oid =
                        top_inheritance.inhparent
                WHERE root_index.indrelid =
                        p_expected_root_oid
                  AND top_index_relation.relname IN (
                        'leaderboard_entries_snapshot_pkey',
                        'ix_les_snapshot_song_score')
            ),
            child_index AS (
                SELECT
                    CASE
                        WHEN index_row.indisprimary
                            THEN 'pk'
                        ELSE 'score'
                    END AS index_role,
                    index_relation.oid::BIGINT
                        AS index_oid,
                    index_relation.relfilenode::BIGINT
                        AS index_relfilenode,
                    index_relation.relname
                        AS index_name,
                    constraint_row.conname
                        AS constraint_name,
                    index_row.indisprimary,
                    index_row.indisunique,
                    index_row.indisvalid,
                    index_row.indisready,
                    index_row.indnkeyatts,
                    index_row.indnatts,
                    index_method.amname
                        AS access_method,
                    COALESCE(
                        index_tablespace.spcname,
                        'pg_default')
                        AS tablespace_name,
                    to_jsonb(
                        COALESCE(
                            index_relation.reloptions,
                            ARRAY[]::TEXT[]))
                        AS relation_options,
                    to_jsonb(
                        index_row.indkey::SMALLINT[])
                        AS key_attnums,
                    (
                        SELECT to_jsonb(
                            array_agg(
                                attribute.attname
                                ORDER BY key_row.ordinality))
                        FROM unnest(
                                index_row.indkey::SMALLINT[])
                            WITH ORDINALITY
                            AS key_row(attnum, ordinality)
                        JOIN pg_attribute attribute
                          ON attribute.attrelid =
                                index_row.indrelid
                         AND attribute.attnum =
                                key_row.attnum
                        WHERE key_row.ordinality <=
                                index_row.indnkeyatts)
                        AS key_columns,
                    to_jsonb(
                        index_row.indclass::OID[])
                        AS opclass_oids,
                    to_jsonb(
                        index_row.indcollation::OID[])
                        AS collation_oids,
                    to_jsonb(
                        index_row.indoption::SMALLINT[])
                        AS ind_options,
                    pg_get_expr(
                        index_row.indexprs,
                        index_row.indrelid,
                        TRUE)
                        AS expressions,
                    pg_get_expr(
                        index_row.indpred,
                        index_row.indrelid,
                        TRUE)
                        AS predicate,
                    index_parent.inhparent::BIGINT
                        AS actual_parent_index_oid,
                    expected_parent.root_index_oid,
                    expected_parent.top_index_oid,
                    expected_parent.top_index_name,
                    expected_parent.opclass_oids
                        AS expected_opclass_oids,
                    expected_parent.collation_oids
                        AS expected_collation_oids,
                    expected_parent.ind_options
                        AS expected_ind_options
                FROM pg_index index_row
                JOIN pg_class index_relation
                  ON index_relation.oid =
                        index_row.indexrelid
                JOIN pg_am index_method
                  ON index_method.oid =
                        index_relation.relam
                LEFT JOIN pg_tablespace index_tablespace
                  ON index_tablespace.oid =
                        index_relation.reltablespace
                LEFT JOIN pg_constraint constraint_row
                  ON constraint_row.conindid =
                        index_row.indexrelid
                LEFT JOIN pg_inherits index_parent
                  ON index_parent.inhrelid =
                        index_row.indexrelid
                JOIN expected_parent
                  ON expected_parent.index_role =
                        CASE
                            WHEN index_row.indisprimary
                                THEN 'pk'
                            ELSE 'score'
                        END
                WHERE index_row.indrelid =
                        p_child_oid
            )
            SELECT jsonb_object_agg(
                index_role,
                jsonb_build_object(
                    'indexOid', index_oid,
                    'indexRelfilenode',
                        index_relfilenode,
                    'indexName', index_name,
                    'constraintName',
                        constraint_name,
                    'actualParentIndexOid',
                        actual_parent_index_oid,
                    'semantic',
                        jsonb_build_object(
                            'schemaVersion', 1,
                            'role', index_role,
                            'indexOid', index_oid,
                            'indexRelfilenode',
                                index_relfilenode,
                            'primary', indisprimary,
                            'unique', indisunique,
                            'valid', indisvalid,
                            'ready', indisready,
                            'indNKeyAtts',
                                indnkeyatts,
                            'indNAtts', indnatts,
                            'accessMethod',
                                access_method,
                            'tablespaceName',
                                tablespace_name,
                            'relationOptions',
                                relation_options,
                            'keyAttnums',
                                key_attnums,
                            'keyColumns',
                                key_columns,
                            'opclassOids',
                                opclass_oids,
                            'collationOids',
                                collation_oids,
                            'indOptions',
                                ind_options,
                            'expressions',
                                expressions,
                            'predicate', predicate,
                            'expectedParentIndexOid',
                                root_index_oid,
                            'expectedTopIndexOid',
                                top_index_oid,
                            'expectedTopIndexName',
                                top_index_name)))
            INTO inventory
            FROM child_index;

            pk := inventory -> 'pk';
            score := inventory -> 'score';
            IF jsonb_typeof(inventory) <> 'object'
               OR (
                    SELECT COUNT(*)
                    FROM jsonb_object_keys(inventory)) <> 2
               OR pk IS NULL
               OR score IS NULL
               OR (pk #>> '{semantic,primary}')::BOOLEAN
                    IS DISTINCT FROM TRUE
               OR (pk #>> '{semantic,unique}')::BOOLEAN
                    IS DISTINCT FROM TRUE
               OR (score #>> '{semantic,primary}')::BOOLEAN
                    IS DISTINCT FROM FALSE
               OR (score #>> '{semantic,unique}')::BOOLEAN
                    IS DISTINCT FROM FALSE
               OR (pk #>> '{semantic,valid}')::BOOLEAN
                    IS DISTINCT FROM TRUE
               OR (pk #>> '{semantic,ready}')::BOOLEAN
                    IS DISTINCT FROM TRUE
               OR (score #>> '{semantic,valid}')::BOOLEAN
                    IS DISTINCT FROM TRUE
               OR (score #>> '{semantic,ready}')::BOOLEAN
                    IS DISTINCT FROM TRUE
               OR pk ->> 'constraintName' IS NULL
               OR score ->> 'constraintName' IS NOT NULL
               OR pk #>> '{semantic,accessMethod}' <>
                    'btree'
               OR score #>> '{semantic,accessMethod}' <>
                    'btree'
               OR pk #>> '{semantic,tablespaceName}' <>
                    'pg_default'
               OR score #>> '{semantic,tablespaceName}' <>
                    'pg_default'
               OR pk #> '{semantic,relationOptions}' <>
                    '[]'::JSONB
               OR score #> '{semantic,relationOptions}' <>
                    '[]'::JSONB
               OR (pk #>> '{semantic,indNKeyAtts}')::INTEGER
                    <> 4
               OR (pk #>> '{semantic,indNAtts}')::INTEGER
                    <> 4
               OR (score #>> '{semantic,indNKeyAtts}')::INTEGER
                    <> 4
               OR (score #>> '{semantic,indNAtts}')::INTEGER
                    <> 4
               OR pk #> '{semantic,keyColumns}' <>
                    '["snapshot_id","song_id","instrument","account_id"]'
                        ::JSONB
               OR score #> '{semantic,keyColumns}' <>
                    '["snapshot_id","song_id","instrument","score"]'
                        ::JSONB
               OR pk #> '{semantic,indOptions}' <>
                    '[0,0,0,0]'::JSONB
               OR score #> '{semantic,indOptions}' <>
                    '[0,0,0,3]'::JSONB
               OR pk #> '{semantic,opclassOids}' <>
                    (
                        SELECT to_jsonb(
                            index_row.indclass::OID[])
                        FROM pg_index index_row
                        WHERE index_row.indexrelid =
                            (pk #>>
                                '{semantic,expectedParentIndexOid}')
                                ::OID)
               OR score #> '{semantic,opclassOids}' <>
                    (
                        SELECT to_jsonb(
                            index_row.indclass::OID[])
                        FROM pg_index index_row
                        WHERE index_row.indexrelid =
                            (score #>>
                                '{semantic,expectedParentIndexOid}')
                                ::OID)
               OR pk #> '{semantic,collationOids}' <>
                    (
                        SELECT to_jsonb(
                            index_row.indcollation::OID[])
                        FROM pg_index index_row
                        WHERE index_row.indexrelid =
                            (pk #>>
                                '{semantic,expectedParentIndexOid}')
                                ::OID)
               OR score #> '{semantic,collationOids}' <>
                    (
                        SELECT to_jsonb(
                            index_row.indcollation::OID[])
                        FROM pg_index index_row
                        WHERE index_row.indexrelid =
                            (score #>>
                                '{semantic,expectedParentIndexOid}')
                                ::OID)
               OR pk #> '{semantic,expressions}' <>
                    'null'::JSONB
               OR score #> '{semantic,expressions}' <>
                    'null'::JSONB
               OR pk #> '{semantic,predicate}' <>
                    'null'::JSONB
               OR score #> '{semantic,predicate}' <>
                    'null'::JSONB
               OR pk #>> '{semantic,expectedTopIndexName}' <>
                    'leaderboard_entries_snapshot_pkey'
               OR score #>>
                    '{semantic,expectedTopIndexName}' <>
                    'ix_les_snapshot_song_score'
               OR (
                    p_expect_attached
                    AND (
                        (pk ->> 'actualParentIndexOid')::BIGINT
                            <> (pk #>>
                                '{semantic,expectedParentIndexOid}')
                                ::BIGINT
                        OR
                        (score ->> 'actualParentIndexOid')::BIGINT
                            <> (score #>>
                                '{semantic,expectedParentIndexOid}')
                                ::BIGINT))
               OR (
                    NOT p_expect_attached
                    AND (
                        pk ->> 'actualParentIndexOid'
                            IS NOT NULL
                        OR score ->>
                            'actualParentIndexOid'
                            IS NOT NULL))
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation child index inventory is unsupported or inconsistent.'
                    USING ERRCODE = '55000';
            END IF;
            RETURN inventory;
        END
        $quarantine_index_inventory$;

        CREATE OR REPLACE FUNCTION
            fst_lock_snapshot_generation_for_quarantine(
                p_cycle_id BIGINT,
                p_observation_id BIGINT,
                p_expected_child_oid BIGINT,
                p_expected_child_relfilenode BIGINT)
        RETURNS TEXT
        LANGUAGE plpgsql
        SECURITY INVOKER
        AS $quarantine_lock$
        DECLARE
            cycle_row
                snapshot_generation_retention_cycles%ROWTYPE;
            observation_row
                snapshot_generation_retention_observations%ROWTYPE;
            state_row scrape_publication_state%ROWTYPE;
            root_relation_name TEXT;
            child_relation_name TEXT;
            observed_parent_oid BIGINT;
            observed_child_relfilenode BIGINT;
            observed_bound TEXT;
        BEGIN
            PERFORM set_config('lock_timeout', '5s', TRUE);
            PERFORM set_config('statement_timeout', '120s', TRUE);
            PERFORM set_config(
                'idle_in_transaction_session_timeout',
                '120s',
                TRUE);

            IF NOT pg_try_advisory_xact_lock(5067481511116518500)
               OR NOT pg_try_advisory_xact_lock(2026050901)
               OR NOT pg_try_advisory_xact_lock(5067481511116519500)
               OR NOT pg_try_advisory_xact_lock(2026082301)
               OR NOT pg_try_advisory_xact_lock(
                    hashtextextended(
                        'fst.snapshot-generation-partition-ddl',
                        0))
               OR NOT pg_try_advisory_xact_lock(2026083001)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation quarantine lock chain is busy; retry in a new transaction.'
                    USING ERRCODE = '55P03';
            END IF;

            SELECT cycle.*
            INTO STRICT cycle_row
            FROM snapshot_generation_retention_cycles cycle
            WHERE cycle.cycle_id = p_cycle_id;

            SELECT observation.*
            INTO STRICT observation_row
            FROM snapshot_generation_retention_observations observation
            WHERE observation.cycle_id = p_cycle_id
              AND observation.observation_id = p_observation_id;

            IF cycle_row.cycle_id IS DISTINCT FROM (
                    SELECT latest.cycle_id
                    FROM snapshot_generation_retention_cycles latest
                    ORDER BY latest.created_at DESC, latest.cycle_id DESC
                    LIMIT 1)
               OR cycle_row.status <> 'observed'
               OR NOT cycle_row.report_only
               OR NOT cycle_row.oracle_agreement
               OR cycle_row.blocked_count <> 0
               OR observation_row.classification <> 'candidate'
               OR observation_row.planner_live
               OR observation_row.oracle_live
               OR cardinality(observation_row.blocker_codes) <> 0
               OR observation_row.child_oid <>
                    p_expected_child_oid
               OR observation_row.child_relfilenode <>
                    p_expected_child_relfilenode
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation candidate changed before fingerprint locking.'
                    USING ERRCODE = '55000';
            END IF;

            root_relation_name := CASE observation_row.instrument
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
            child_relation_name :=
                root_relation_name || '_s' ||
                observation_row.snapshot_id::TEXT;

            IF observation_row.instrument = 'Solo_Bass'
                    AND observation_row.snapshot_id = 1308
               OR observation_row.root_schema <> 'public'
               OR observation_row.child_schema <> 'public'
               OR observation_row.root_relation <>
                    root_relation_name
               OR observation_row.child_relation <>
                    child_relation_name
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation candidate relation identity is invalid.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT state.*
            INTO STRICT state_row
            FROM scrape_publication_state state
            WHERE state.id = TRUE;
            IF state_row.current_publication_id IS DISTINCT FROM
                    cycle_row.trigger_publication_id
               OR state_row.published_scrape_id IS DISTINCT FROM
                    cycle_row.trigger_scrape_id
               OR state_row.public_reads_frozen
               OR state_row.working_publication_id IS NOT NULL
               OR EXISTS (
                    SELECT 1
                    FROM scrape_log scrape
                    WHERE scrape.status = 'running')
            THEN
                RAISE EXCEPTION
                    'Publication state changed before fingerprint locking.'
                    USING ERRCODE = '55000';
            END IF;

            EXECUTE format(
                'LOCK TABLE %I.%I IN SHARE MODE',
                'public',
                child_relation_name);

            SELECT
                inheritance.inhparent::BIGINT,
                child.relfilenode::BIGINT,
                pg_get_expr(
                    child.relpartbound,
                    child.oid,
                    TRUE)
            INTO STRICT
                observed_parent_oid,
                observed_child_relfilenode,
                observed_bound
            FROM pg_class child
            JOIN pg_namespace child_namespace
              ON child_namespace.oid = child.relnamespace
            JOIN pg_inherits inheritance
              ON inheritance.inhrelid = child.oid
            WHERE child_namespace.nspname = 'public'
              AND child.relname = child_relation_name
              AND child.oid::BIGINT =
                    observation_row.child_oid;

            IF observed_parent_oid <> observation_row.root_oid
               OR observed_child_relfilenode <>
                    observation_row.child_relfilenode
               OR observed_bound <>
                    observation_row.partition_bound
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation physical identity changed after fingerprint locking.'
                    USING ERRCODE = '55000';
            END IF;

            RETURN child_relation_name;
        END
        $quarantine_lock$;

        CREATE OR REPLACE FUNCTION
            fst_quarantine_snapshot_generation(
                p_operation_id TEXT,
                p_plan_digest TEXT,
                p_archive_manifest_sha256 TEXT,
                p_archive_proof_manifest_sha256 TEXT,
                p_source_evidence_manifest_sha256 TEXT,
                p_baseline_route_manifest_sha256 TEXT,
                p_candidate_route_manifest_sha256 TEXT,
                p_cycle_id BIGINT,
                p_observation_id BIGINT,
                p_expected_child_oid BIGINT,
                p_expected_child_relfilenode BIGINT,
                p_expected_row_count BIGINT,
                p_row_fingerprint_sha256 TEXT,
                p_logical_catalog_sha256 TEXT,
                p_approved_by TEXT,
                p_approval_reference TEXT,
                p_preflight_evidence JSONB)
        RETURNS TEXT
        LANGUAGE plpgsql
        SECURITY INVOKER
        AS $quarantine_execute$
        DECLARE
            cycle_row
                snapshot_generation_retention_cycles%ROWTYPE;
            observation_row
                snapshot_generation_retention_observations%ROWTYPE;
            state_row scrape_publication_state%ROWTYPE;
            root_relation_name TEXT;
            child_relation_name TEXT;
            instrument_key TEXT;
            quarantine_relation_name TEXT;
            check_constraint_name TEXT;
            mutation_guard_trigger_name TEXT;
            default_relation_name TEXT;
            default_exclusion_constraint_name TEXT;
            observed_parent_oid BIGINT;
            observed_bound TEXT;
            observed_child_relfilenode BIGINT;
            observed_row_count BIGINT;
            observed_total_bytes BIGINT;
            observed_default_oid BIGINT;
            observed_default_row_count BIGINT;
            active_hold_id BIGINT;
            accepted_cycle_count INTEGER;
            accepted_publication_count INTEGER;
            accepted_candidate_identity_count INTEGER;
            index_inventory_before JSONB;
            index_inventory_detached JSONB;
            index_inventory_after JSONB;
            index_renames JSONB := '{}'::JSONB;
            role_key TEXT;
            old_index_name TEXT;
            new_index_name TEXT;
            old_constraint_name TEXT;
            new_constraint_name TEXT;
            semantic_before JSONB;
            semantic_after JSONB;
        BEGIN
            PERFORM set_config('lock_timeout', '5s', TRUE);
            PERFORM set_config('statement_timeout', '120s', TRUE);
            PERFORM set_config(
                'idle_in_transaction_session_timeout',
                '120s',
                TRUE);

            IF NOT pg_try_advisory_xact_lock(5067481511116518500)
               OR NOT pg_try_advisory_xact_lock(2026050901)
               OR NOT pg_try_advisory_xact_lock(5067481511116519500)
               OR NOT pg_try_advisory_xact_lock(2026082301)
               OR NOT pg_try_advisory_xact_lock(
                    hashtextextended(
                        'fst.snapshot-generation-partition-ddl',
                        0))
               OR NOT pg_try_advisory_xact_lock(2026083001)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation quarantine lock chain is busy; retry in a new transaction.'
                    USING ERRCODE = '55P03';
            END IF;

            IF p_operation_id !~ '^[0-9a-f]{32}$'
               OR p_plan_digest !~ '^[0-9a-f]{64}$'
               OR p_archive_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_archive_proof_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_source_evidence_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_baseline_route_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_candidate_route_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_row_fingerprint_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_logical_catalog_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_expected_row_count < 0
               OR COALESCE(p_approved_by, '') = ''
               OR COALESCE(p_approval_reference, '') = ''
               OR jsonb_typeof(p_preflight_evidence) <> 'object'
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation quarantine arguments are invalid.'
                    USING ERRCODE = '22023';
            END IF;

            IF EXISTS (
                SELECT 1
                FROM snapshot_generation_quarantine_operations operation
                WHERE operation.operation_id = p_operation_id
                  AND operation.plan_digest = p_plan_digest)
            THEN
                RETURN p_operation_id;
            END IF;

            IF EXISTS (
                SELECT 1
                FROM snapshot_generation_quarantine_operations operation
                WHERE operation.operation_id = p_operation_id
                   OR operation.plan_digest = p_plan_digest)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation quarantine operation identity conflicts with existing evidence.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT cycle.*
            INTO STRICT cycle_row
            FROM snapshot_generation_retention_cycles cycle
            WHERE cycle.cycle_id = p_cycle_id;

            IF cycle_row.cycle_id IS DISTINCT FROM (
                    SELECT latest.cycle_id
                    FROM snapshot_generation_retention_cycles latest
                    ORDER BY latest.created_at DESC, latest.cycle_id DESC
                    LIMIT 1)
               OR cycle_row.status <> 'observed'
               OR NOT cycle_row.report_only
               OR NOT cycle_row.oracle_agreement
               OR cycle_row.blocked_count <> 0
               OR cycle_row.candidate_count <= 0
               OR cycle_row.global_blockers <> '[]'::jsonb
               OR cycle_row.planner_child_set
                    IS DISTINCT FROM cycle_row.oracle_child_set
               OR cycle_row.planner_live_set
                    IS DISTINCT FROM cycle_row.oracle_live_set
               OR cycle_row.planner_candidate_set
                    IS DISTINCT FROM cycle_row.oracle_candidate_set
            THEN
                RAISE EXCEPTION
                    'Latest snapshot-generation retention cycle is not an accepted destructive-tier observation.'
                    USING ERRCODE = '55000';
            END IF;

            WITH accepted AS (
                SELECT recent.*
                FROM snapshot_generation_retention_cycles recent
                ORDER BY recent.created_at DESC, recent.cycle_id DESC
                LIMIT 5
            )
            SELECT
                COUNT(*)::INTEGER,
                COUNT(DISTINCT trigger_publication_id)::INTEGER,
                COUNT(DISTINCT candidate_identity_hash)::INTEGER
            INTO
                accepted_cycle_count,
                accepted_publication_count,
                accepted_candidate_identity_count
            FROM accepted
            WHERE status = 'observed'
              AND report_only
              AND oracle_agreement
              AND blocked_count = 0
              AND planner_version = 3
              AND config_version = 1
              AND global_blockers = '[]'::jsonb
              AND planner_child_set = oracle_child_set
              AND planner_live_set = oracle_live_set
              AND planner_candidate_set = oracle_candidate_set;

            IF accepted_cycle_count <> 5
               OR accepted_publication_count < 2
               OR accepted_candidate_identity_count < 2
            THEN
                RAISE EXCEPTION
                    'Five-cycle snapshot-generation observation gate is not satisfied.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT observation.*
            INTO STRICT observation_row
            FROM snapshot_generation_retention_observations observation
            WHERE observation.cycle_id = p_cycle_id
              AND observation.observation_id = p_observation_id;

            IF observation_row.classification <> 'candidate'
               OR observation_row.planner_live
               OR observation_row.oracle_live
               OR cardinality(observation_row.blocker_codes) <> 0
               OR observation_row.child_oid <>
                    p_expected_child_oid
               OR observation_row.child_relfilenode <>
                    p_expected_child_relfilenode
               OR observation_row.instrument = 'Solo_Bass'
                    AND observation_row.snapshot_id = 1308
            THEN
                RAISE EXCEPTION
                    'Selected snapshot-generation observation is not an exact eligible candidate.'
                    USING ERRCODE = '55000';
            END IF;

            root_relation_name := CASE observation_row.instrument
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
            instrument_key := CASE observation_row.instrument
                WHEN 'Solo_Guitar' THEN 'sg'
                WHEN 'Solo_Bass' THEN 'sb'
                WHEN 'Solo_Drums' THEN 'sd'
                WHEN 'Solo_Vocals' THEN 'sv'
                WHEN 'Solo_PeripheralGuitar' THEN 'pg'
                WHEN 'Solo_PeripheralBass' THEN 'pb'
                WHEN 'Solo_PeripheralVocals' THEN 'pv'
                WHEN 'Solo_PeripheralCymbals' THEN 'pc'
                WHEN 'Solo_PeripheralDrums' THEN 'pd'
                ELSE NULL
            END;
            child_relation_name :=
                root_relation_name || '_s' ||
                observation_row.snapshot_id::TEXT;
            quarantine_relation_name :=
                'sgq_' || instrument_key || '_' ||
                observation_row.snapshot_id::TEXT || '_' ||
                left(p_operation_id, 12);
            check_constraint_name :=
                'ck_sgq_' || observation_row.snapshot_id::TEXT ||
                '_' || left(p_operation_id, 12);
            mutation_guard_trigger_name :=
                'trg_sgq_' || observation_row.snapshot_id::TEXT ||
                '_' || left(p_operation_id, 12);
            default_relation_name :=
                root_relation_name || '_default';
            default_exclusion_constraint_name :=
                'ck_sgq_default_' ||
                observation_row.snapshot_id::TEXT || '_' ||
                left(p_operation_id, 12);

            IF observation_row.root_schema <> 'public'
               OR observation_row.child_schema <> 'public'
               OR observation_row.root_relation <>
                    root_relation_name
               OR observation_row.child_relation <>
                    child_relation_name
               OR observation_row.partition_bound <>
                    format(
                        'FOR VALUES IN (%L)',
                        observation_row.snapshot_id)
            THEN
                RAISE EXCEPTION
                    'Selected snapshot-generation relation naming or bound is invalid.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT state.*
            INTO STRICT state_row
            FROM scrape_publication_state state
            WHERE state.id = TRUE;

            IF state_row.current_publication_id IS DISTINCT FROM
                    cycle_row.trigger_publication_id
               OR state_row.published_scrape_id IS DISTINCT FROM
                    cycle_row.trigger_scrape_id
               OR state_row.public_reads_frozen
               OR state_row.working_publication_id IS NOT NULL
               OR state_row.publication_commit_intent_started_at
                    IS NOT NULL
               OR state_row.max_score_mutation_gate_token IS NOT NULL
               OR state_row.improvement_notifications_scrape_id
                    IS DISTINCT FROM
                    cycle_row.trigger_scrape_id
               OR state_row.improvement_notifications_status
                    IS DISTINCT FROM
                    'completed'
               OR state_row.improvement_notifications_completed_at
                    IS NULL
               OR state_row.improvement_notifications_projection_ready
                    IS DISTINCT FROM TRUE
               OR state_row.improvement_notifications_projection_scrape_id
                    IS DISTINCT FROM
                    cycle_row.trigger_scrape_id
            THEN
                RAISE EXCEPTION
                    'Publication state is not idle, current, unfrozen, and notification-complete for the selected cycle.'
                    USING ERRCODE = '55000';
            END IF;

            IF EXISTS (
                SELECT 1
                FROM scrape_log scrape
                WHERE scrape.status = 'running')
               OR EXISTS (
                    SELECT 1
                    FROM snapshot_generation_retention_holds hold_row
                    WHERE hold_row.instrument =
                            observation_row.instrument
                      AND hold_row.snapshot_id =
                            observation_row.snapshot_id
                      AND hold_row.released_at IS NULL)
               OR EXISTS (
                    SELECT 1
                    FROM scrape_writer_failures failure
                    WHERE failure.instrument =
                            observation_row.instrument
                      AND failure.scrape_id =
                            observation_row.snapshot_id
                      AND failure.replayed_at IS NULL)
            THEN
                RAISE EXCEPTION
                    'Running scrape, active hold, or unreplayed writer failure blocks quarantine.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT
                inheritance.inhparent::BIGINT,
                pg_get_expr(
                    child.relpartbound,
                    child.oid,
                    TRUE),
                child.relfilenode::BIGINT
            INTO STRICT
                observed_parent_oid,
                observed_bound,
                observed_child_relfilenode
            FROM pg_class child
            JOIN pg_namespace child_namespace
              ON child_namespace.oid = child.relnamespace
            JOIN pg_inherits inheritance
              ON inheritance.inhrelid = child.oid
            WHERE child_namespace.nspname = 'public'
              AND child.relname = child_relation_name
              AND child.oid::BIGINT = observation_row.child_oid
              AND child.relkind = 'r';

            IF observed_parent_oid <> observation_row.root_oid
               OR observed_bound <> observation_row.partition_bound
               OR observed_child_relfilenode <>
                    observation_row.child_relfilenode
               OR to_regclass(
                    format(
                        '%I.%I',
                        'fst_snapshot_quarantine',
                        quarantine_relation_name)) IS NOT NULL
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation physical identity changed before quarantine.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT default_child.oid::BIGINT
            INTO STRICT observed_default_oid
            FROM pg_class default_child
            JOIN pg_namespace default_namespace
              ON default_namespace.oid =
                    default_child.relnamespace
            JOIN pg_inherits default_inheritance
              ON default_inheritance.inhrelid =
                    default_child.oid
            WHERE default_namespace.nspname = 'public'
              AND default_child.relname =
                    default_relation_name
              AND default_child.relkind = 'r'
              AND default_inheritance.inhparent =
                    observation_row.root_oid
              AND pg_get_expr(
                    default_child.relpartbound,
                    default_child.oid,
                    TRUE) = 'DEFAULT';
            EXECUTE format(
                'SELECT COUNT(*)::BIGINT FROM ONLY %I.%I',
                'public',
                default_relation_name)
            INTO observed_default_row_count;
            IF observed_default_row_count <> 0
               OR EXISTS (
                    SELECT 1
                    FROM pg_constraint constraint_row
                    WHERE constraint_row.conrelid =
                            observed_default_oid
                      AND constraint_row.conname =
                            default_exclusion_constraint_name)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation default partition is not empty and constraint-free for quarantine.'
                    USING ERRCODE = '55000';
            END IF;

            EXECUTE format(
                'SELECT COUNT(*)::BIGINT FROM ONLY %I.%I',
                'public',
                child_relation_name)
            INTO observed_row_count;
            IF observed_row_count <> p_expected_row_count THEN
                RAISE EXCEPTION
                    'Snapshot-generation row count changed before quarantine.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT pg_total_relation_size(
                observation_row.child_oid::OID)::BIGINT
            INTO observed_total_bytes;
            index_inventory_before :=
                fst_snapshot_generation_index_inventory(
                    observation_row.child_oid,
                    observation_row.root_oid,
                    TRUE);

            INSERT INTO snapshot_generation_retention_holds (
                instrument,
                snapshot_id,
                hold_kind,
                reason,
                created_by)
            VALUES (
                observation_row.instrument,
                observation_row.snapshot_id,
                'retention_in_flight',
                format(
                    'snapshot-generation quarantine operation %s',
                    p_operation_id),
                current_user)
            RETURNING hold_id
            INTO active_hold_id;

            EXECUTE format(
                'ALTER TABLE %I.%I ADD CONSTRAINT %I CHECK (snapshot_id <> %s) NOT VALID',
                'public',
                default_relation_name,
                default_exclusion_constraint_name,
                observation_row.snapshot_id);
            EXECUTE format(
                'ALTER TABLE %I.%I VALIDATE CONSTRAINT %I',
                'public',
                default_relation_name,
                default_exclusion_constraint_name);
            EXECUTE format(
                'ALTER TABLE %I.%I DETACH PARTITION %I.%I',
                'public',
                root_relation_name,
                'public',
                child_relation_name);
            index_inventory_detached :=
                fst_snapshot_generation_index_inventory(
                    observation_row.child_oid,
                    observation_row.root_oid,
                    FALSE);
            FOREACH role_key IN ARRAY ARRAY[
                'pk',
                'score'
            ]
            LOOP
                semantic_before :=
                    index_inventory_detached
                        -> role_key
                        -> 'semantic';
                IF semantic_before IS DISTINCT FROM
                        index_inventory_before
                            -> role_key
                            -> 'semantic'
                THEN
                    RAISE EXCEPTION
                        'Snapshot-generation % index semantics changed during detach.',
                        role_key
                        USING ERRCODE = '55000';
                END IF;
                old_index_name :=
                    index_inventory_detached
                        -> role_key
                        ->> 'indexName';
                old_constraint_name :=
                    index_inventory_detached
                        -> role_key
                        ->> 'constraintName';
                IF old_index_name IS NULL THEN
                    RAISE EXCEPTION
                        'Snapshot-generation % index inventory is missing: %.',
                        role_key,
                        index_inventory_detached
                        USING ERRCODE = '55000';
                END IF;
                new_index_name :=
                    'sgqi_' || p_operation_id || '_' ||
                    role_key;
                IF to_regclass(
                        format(
                            '%I.%I',
                            'public',
                            new_index_name)) IS NOT NULL
                   OR to_regclass(
                        format(
                            '%I.%I',
                            'fst_snapshot_quarantine',
                            new_index_name)) IS NOT NULL
                THEN
                    RAISE EXCEPTION
                        'Snapshot-generation derived % index name % is already occupied.',
                        role_key,
                        new_index_name
                        USING ERRCODE = '42P07';
                END IF;
                EXECUTE format(
                    'ALTER INDEX %I.%I RENAME TO %I',
                    'public',
                    old_index_name,
                    new_index_name);
            END LOOP;
            index_inventory_after :=
                fst_snapshot_generation_index_inventory(
                    observation_row.child_oid,
                    observation_row.root_oid,
                    FALSE);
            FOREACH role_key IN ARRAY ARRAY[
                'pk',
                'score'
            ]
            LOOP
                semantic_before :=
                    index_inventory_detached
                        -> role_key
                        -> 'semantic';
                semantic_after :=
                    index_inventory_after
                        -> role_key
                        -> 'semantic';
                old_index_name :=
                    index_inventory_detached
                        -> role_key
                        ->> 'indexName';
                new_index_name :=
                    'sgqi_' || p_operation_id || '_' ||
                    role_key;
                old_constraint_name :=
                    index_inventory_detached
                        -> role_key
                        ->> 'constraintName';
                new_constraint_name :=
                    index_inventory_after
                        -> role_key
                        ->> 'constraintName';
                IF semantic_after IS DISTINCT FROM
                        semantic_before
                   OR index_inventory_after
                        -> role_key
                        ->> 'indexName' <>
                        new_index_name
                   OR (
                        role_key = 'pk'
                        AND new_constraint_name <>
                            new_index_name)
                   OR (
                        role_key = 'score'
                        AND new_constraint_name IS NOT NULL)
                THEN
                    RAISE EXCEPTION
                        'Snapshot-generation % index normalization changed physical or semantic identity.',
                        role_key
                        USING ERRCODE = '55000';
                END IF;
                index_renames :=
                    index_renames ||
                    jsonb_build_object(
                        role_key,
                        jsonb_build_object(
                            'role', role_key,
                            'indexOid',
                                (
                                    index_inventory_after
                                    -> role_key
                                    ->> 'indexOid')::BIGINT,
                            'indexRelfilenode',
                                (
                                    index_inventory_after
                                    -> role_key
                                    ->> 'indexRelfilenode')
                                    ::BIGINT,
                            'oldIndexName',
                                old_index_name,
                            'newIndexName',
                                new_index_name,
                            'oldConstraintName',
                                old_constraint_name,
                            'newConstraintName',
                                new_constraint_name,
                            'semantic',
                                semantic_after,
                            'semanticSha256',
                                encode(
                                    digest(
                                        convert_to(
                                            semantic_after::TEXT,
                                            'UTF8'),
                                        'sha256'),
                                    'hex')));
            END LOOP;
            EXECUTE format(
                'ALTER TABLE %I.%I SET SCHEMA %I',
                'public',
                child_relation_name,
                'fst_snapshot_quarantine');
            EXECUTE format(
                'ALTER TABLE %I.%I RENAME TO %I',
                'fst_snapshot_quarantine',
                child_relation_name,
                quarantine_relation_name);
            EXECUTE format(
                'ALTER TABLE %I.%I ADD CONSTRAINT %I CHECK (snapshot_id = %s)',
                'fst_snapshot_quarantine',
                quarantine_relation_name,
                check_constraint_name,
                observation_row.snapshot_id);
            EXECUTE format(
                'CREATE TRIGGER %I BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE ON %I.%I FOR EACH STATEMENT EXECUTE FUNCTION fst_reject_snapshot_generation_quarantine_relation_mutation()',
                mutation_guard_trigger_name,
                'fst_snapshot_quarantine',
                quarantine_relation_name);

            INSERT INTO snapshot_generation_quarantine_operations (
                operation_id,
                schema_version,
                tool_id,
                plan_digest,
                archive_manifest_sha256,
                archive_proof_manifest_sha256,
                source_evidence_manifest_sha256,
                baseline_route_manifest_sha256,
                candidate_route_manifest_sha256,
                cycle_id,
                observation_id,
                trigger_scrape_id,
                trigger_publication_id,
                instrument,
                snapshot_id,
                root_schema,
                root_relation,
                root_oid,
                child_schema,
                child_relation,
                child_oid,
                child_relfilenode,
                quarantine_schema,
                quarantine_relation,
                snapshot_check_constraint,
                mutation_guard_trigger,
                default_partition_schema,
                default_partition_relation,
                default_partition_oid,
                default_exclusion_constraint,
                stable_child_identity_hash,
                stable_config_schema_hash,
                row_count,
                row_fingerprint_sha256,
                logical_catalog_sha256,
                total_bytes,
                hold_id,
                approved_by,
                approval_reference,
                preflight_evidence,
                quarantine_evidence)
            VALUES (
                p_operation_id,
                1,
                'fst.snapshot-generation-quarantine.v1',
                p_plan_digest,
                p_archive_manifest_sha256,
                p_archive_proof_manifest_sha256,
                p_source_evidence_manifest_sha256,
                p_baseline_route_manifest_sha256,
                p_candidate_route_manifest_sha256,
                cycle_row.cycle_id,
                observation_row.observation_id,
                cycle_row.trigger_scrape_id,
                cycle_row.trigger_publication_id,
                observation_row.instrument,
                observation_row.snapshot_id,
                observation_row.root_schema,
                observation_row.root_relation,
                observation_row.root_oid,
                observation_row.child_schema,
                observation_row.child_relation,
                observation_row.child_oid,
                observation_row.child_relfilenode,
                'fst_snapshot_quarantine',
                quarantine_relation_name,
                check_constraint_name,
                mutation_guard_trigger_name,
                'public',
                default_relation_name,
                observed_default_oid,
                default_exclusion_constraint_name,
                observation_row.stable_child_identity_hash,
                observation_row.stable_config_schema_hash,
                observed_row_count,
                p_row_fingerprint_sha256,
                p_logical_catalog_sha256,
                observed_total_bytes,
                active_hold_id,
                p_approved_by,
                p_approval_reference,
                p_preflight_evidence,
                jsonb_build_object(
                    'childOid',
                    observation_row.child_oid,
                    'childRelfilenode',
                    observation_row.child_relfilenode,
                    'constraint',
                    check_constraint_name,
                    'mutationGuardTrigger',
                    mutation_guard_trigger_name,
                    'defaultPartition',
                    format(
                        '%I.%I',
                        'public',
                        default_relation_name),
                    'defaultExclusionConstraint',
                    default_exclusion_constraint_name,
                    'quarantineRelation',
                    format(
                        '%I.%I',
                        'fst_snapshot_quarantine',
                        quarantine_relation_name),
                    'rowCount',
                    observed_row_count,
                    'totalBytes',
                    observed_total_bytes,
                    'indexRenames',
                    index_renames));

            INSERT INTO
                snapshot_generation_quarantine_index_renames (
                    operation_id,
                    index_role,
                    index_oid,
                    index_relfilenode,
                    old_index_name,
                    new_index_name,
                    old_constraint_name,
                    new_constraint_name,
                    source_phase,
                    semantic_before,
                    semantic_after,
                    semantic_before_sha256,
                    semantic_after_sha256,
                    backend_pid,
                    transaction_id)
            SELECT
                p_operation_id,
                rename_row.key,
                (rename_row.value ->> 'indexOid')
                    ::BIGINT,
                (rename_row.value ->> 'indexRelfilenode')
                    ::BIGINT,
                rename_row.value ->> 'oldIndexName',
                rename_row.value ->> 'newIndexName',
                rename_row.value ->> 'oldConstraintName',
                rename_row.value ->> 'newConstraintName',
                'quarantine',
                rename_row.value -> 'semantic',
                rename_row.value -> 'semantic',
                rename_row.value ->> 'semanticSha256',
                rename_row.value ->> 'semanticSha256',
                pg_backend_pid(),
                pg_current_xact_id()::TEXT
            FROM jsonb_each(index_renames)
                AS rename_row;

            RETURN p_operation_id;
        END
        $quarantine_execute$;

        CREATE OR REPLACE FUNCTION
            fst_reattach_snapshot_generation(
                p_operation_id TEXT,
                p_plan_digest TEXT,
                p_reattached_by TEXT,
                p_reattach_reference TEXT,
                p_reattach_evidence JSONB)
        RETURNS TEXT
        LANGUAGE plpgsql
        SECURITY INVOKER
        AS $quarantine_reattach$
        DECLARE
            operation_row
                snapshot_generation_quarantine_operations%ROWTYPE;
            observed_child_oid BIGINT;
            observed_child_relfilenode BIGINT;
            observed_constraint_valid BOOLEAN;
            observed_constraint_expression TEXT;
            observed_mutation_guard_count INTEGER;
            observed_row_count BIGINT;
            observed_default_oid BIGINT;
            observed_default_parent_oid BIGINT;
            observed_default_bound TEXT;
            observed_default_constraint_valid BOOLEAN;
            observed_default_constraint_expression TEXT;
            observed_default_target_row_count BIGINT;
            observed_parent_oid BIGINT;
            observed_bound TEXT;
            attached_required_index_count INTEGER;
            released_hold_count INTEGER;
            successful_quarantined_attestations INTEGER;
            successful_current_soak_attestations INTEGER;
            target_reference_count BIGINT;
            state_row scrape_publication_state%ROWTYPE;
            index_inventory_before JSONB;
            index_inventory_after JSONB;
            repair_role TEXT;
            old_index_name TEXT;
            new_index_name TEXT;
            old_constraint_name TEXT;
            new_constraint_name TEXT;
            semantic_before JSONB;
            semantic_after JSONB;
            existing_rename_count INTEGER;
            stored_rename
                snapshot_generation_quarantine_index_renames%ROWTYPE;
            index_rename_evidence JSONB;
        BEGIN
            PERFORM set_config('lock_timeout', '5s', TRUE);
            PERFORM set_config('statement_timeout', '120s', TRUE);
            PERFORM set_config(
                'idle_in_transaction_session_timeout',
                '120s',
                TRUE);

            IF NOT pg_try_advisory_xact_lock(5067481511116518500)
               OR NOT pg_try_advisory_xact_lock(2026050901)
               OR NOT pg_try_advisory_xact_lock(5067481511116519500)
               OR NOT pg_try_advisory_xact_lock(2026082301)
               OR NOT pg_try_advisory_xact_lock(
                    hashtextextended(
                        'fst.snapshot-generation-partition-ddl',
                        0))
               OR NOT pg_try_advisory_xact_lock(2026083001)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation quarantine lock chain is busy; retry in a new transaction.'
                    USING ERRCODE = '55P03';
            END IF;

            IF COALESCE(p_operation_id, '') = ''
               OR p_plan_digest !~ '^[0-9a-f]{64}$'
               OR COALESCE(p_reattached_by, '') = ''
               OR COALESCE(p_reattach_reference, '') = ''
               OR jsonb_typeof(p_reattach_evidence) <> 'object'
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation reattach arguments are invalid.'
                    USING ERRCODE = '22023';
            END IF;

            SELECT operation.*
            INTO STRICT operation_row
            FROM snapshot_generation_quarantine_operations operation
            WHERE operation.operation_id = p_operation_id
              AND operation.plan_digest = p_plan_digest;

            IF EXISTS (
                SELECT 1
                FROM snapshot_generation_quarantine_reattachments reattach
                WHERE reattach.operation_id = p_operation_id)
            THEN
                RETURN p_operation_id;
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM snapshot_generation_retention_holds hold_row
                WHERE hold_row.hold_id = operation_row.hold_id
                  AND hold_row.instrument =
                        operation_row.instrument
                  AND hold_row.snapshot_id =
                        operation_row.snapshot_id
                  AND hold_row.hold_kind =
                        'retention_in_flight'
                  AND hold_row.released_at IS NULL)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation quarantine hold is missing or released.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT
                COUNT(*) FILTER (
                    WHERE attestation.stage = 'quarantined'
                      AND attestation.publication_id =
                            operation_row.trigger_publication_id
                      AND attestation.published_scrape_id =
                            operation_row.trigger_scrape_id
                      AND attestation.route_count = 55
                      AND attestation.status_parity
                      AND attestation.semantic_json_parity
                      AND attestation.difference_count = 0)::INTEGER,
                COUNT(*) FILTER (
                    WHERE attestation.stage = 'soak'
                      AND attestation.publication_id =
                            state.current_publication_id
                      AND attestation.published_scrape_id =
                            state.published_scrape_id
                      AND attestation.route_count = 55
                      AND attestation.status_parity
                      AND attestation.semantic_json_parity
                      AND attestation.difference_count = 0)::INTEGER
            INTO
                successful_quarantined_attestations,
                successful_current_soak_attestations
            FROM snapshot_generation_quarantine_attestations
                attestation,
                scrape_publication_state state
            WHERE attestation.operation_id = p_operation_id
              AND state.id = TRUE;
            IF successful_quarantined_attestations < 1
               OR successful_current_soak_attestations < 1
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation reattach requires a successful original-publication quarantine attestation and a successful current-publication soak attestation.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT state.*
            INTO STRICT state_row
            FROM scrape_publication_state state
            WHERE state.id = TRUE;
            IF state_row.current_publication_id IS NULL
               OR state_row.published_scrape_id IS NULL
               OR state_row.public_reads_frozen
               OR state_row.working_publication_id IS NOT NULL
               OR state_row.publication_commit_intent_started_at
                    IS NOT NULL
               OR state_row.max_score_mutation_gate_token
                    IS NOT NULL
               OR state_row.improvement_notifications_scrape_id
                    IS DISTINCT FROM
                        state_row.published_scrape_id
               OR state_row.improvement_notifications_status
                    IS DISTINCT FROM 'completed'
               OR state_row.improvement_notifications_completed_at
                    IS NULL
               OR state_row.improvement_notifications_projection_ready
                    IS DISTINCT FROM TRUE
               OR state_row.improvement_notifications_projection_scrape_id
                    IS DISTINCT FROM
                        state_row.published_scrape_id
               OR NOT EXISTS (
                    SELECT 1
                    FROM publication_generations current_generation
                    WHERE current_generation.publication_id =
                            state_row.current_publication_id
                      AND current_generation.scrape_id =
                            state_row.published_scrape_id
                      AND current_generation.status = 'current')
               OR EXISTS (
                    SELECT 1
                    FROM scrape_log scrape
                    WHERE scrape.status = 'running')
            THEN
                RAISE EXCEPTION
                    'Publication state is not healthy, idle, and unfrozen for snapshot-generation reattach.'
                    USING ERRCODE = '55000';
            END IF;

            WITH named_publication_scrapes AS (
                SELECT generation.scrape_id
                FROM publication_generations generation
                WHERE generation.publication_id IN (
                    state_row.current_publication_id,
                    state_row.previous_publication_id,
                    state_row.working_publication_id)
                  AND generation.scrape_id IS NOT NULL
            ),
            target_roots AS (
                SELECT 1
                FROM leaderboard_snapshot_state snapshot_state
                WHERE snapshot_state.instrument =
                        operation_row.instrument
                  AND snapshot_state.active_snapshot_id =
                        operation_row.snapshot_id

                UNION ALL

                SELECT 1
                FROM solo_current_projection_scope projection
                WHERE projection.instrument =
                        operation_row.instrument
                  AND projection.source_snapshot_id =
                        operation_row.snapshot_id

                UNION ALL

                SELECT 1
                FROM leaderboard_published_scope_source source
                WHERE source.instrument =
                        operation_row.instrument
                  AND source.source_snapshot_id =
                        operation_row.snapshot_id
                  AND source.published_scrape_id IN (
                        SELECT scrape_id
                        FROM named_publication_scrapes)

                UNION ALL

                SELECT 1
                FROM scrape_writer_failures failure
                WHERE failure.instrument =
                        operation_row.instrument
                  AND failure.scrape_id =
                        operation_row.snapshot_id
                  AND failure.replayed_at IS NULL

                UNION ALL

                SELECT 1
                FROM snapshot_generation_retention_holds hold_row
                WHERE hold_row.instrument =
                        operation_row.instrument
                  AND hold_row.snapshot_id =
                        operation_row.snapshot_id
                  AND hold_row.released_at IS NULL
                  AND hold_row.hold_id <>
                        operation_row.hold_id
            )
            SELECT COUNT(*)::BIGINT
            INTO target_reference_count
            FROM target_roots;
            IF target_reference_count <> 0 THEN
                RAISE EXCEPTION
                    'Snapshot-generation target gained % live or recovery reference(s) while quarantined.',
                    target_reference_count
                    USING ERRCODE = '55000';
            END IF;

            SELECT
                child.oid::BIGINT,
                child.relfilenode::BIGINT,
                constraint_row.convalidated,
                regexp_replace(
                    pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE),
                    '[()[:space:]]',
                    '',
                    'g'),
                COUNT(trigger_row.oid)::INTEGER
            INTO STRICT
                observed_child_oid,
                observed_child_relfilenode,
                observed_constraint_valid,
                observed_constraint_expression,
                observed_mutation_guard_count
            FROM pg_class child
            JOIN pg_namespace child_namespace
              ON child_namespace.oid = child.relnamespace
            JOIN pg_constraint constraint_row
              ON constraint_row.conrelid = child.oid
             AND constraint_row.conname =
                    operation_row.snapshot_check_constraint
             AND constraint_row.contype = 'c'
            LEFT JOIN pg_trigger trigger_row
              ON trigger_row.tgrelid = child.oid
             AND trigger_row.tgname =
                    operation_row.mutation_guard_trigger
             AND NOT trigger_row.tgisinternal
             AND trigger_row.tgenabled = 'O'
            WHERE child_namespace.nspname =
                    operation_row.quarantine_schema
              AND child.relname =
                    operation_row.quarantine_relation
              AND child.relkind = 'r'
            GROUP BY
                child.oid,
                child.relfilenode,
                constraint_row.convalidated,
                constraint_row.conbin,
                constraint_row.conrelid;

            IF observed_child_oid <> operation_row.child_oid
               OR observed_child_relfilenode <>
                    operation_row.child_relfilenode
               OR NOT observed_constraint_valid
               OR observed_constraint_expression <>
                    'snapshot_id=' ||
                    operation_row.snapshot_id::TEXT
               OR observed_mutation_guard_count <> 1
               OR to_regclass(
                    format(
                        '%I.%I',
                        operation_row.child_schema,
                        operation_row.child_relation)) IS NOT NULL
               OR EXISTS (
                    SELECT 1
                    FROM pg_inherits inheritance
                    WHERE inheritance.inhrelid =
                            operation_row.child_oid)
            THEN
                RAISE EXCEPTION
                    'Quarantined snapshot-generation physical identity changed before reattach.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT
                default_child.oid::BIGINT,
                default_inheritance.inhparent::BIGINT,
                pg_get_expr(
                    default_child.relpartbound,
                    default_child.oid,
                    TRUE),
                default_constraint.convalidated,
                regexp_replace(
                    pg_get_expr(
                        default_constraint.conbin,
                        default_constraint.conrelid,
                        TRUE),
                    '[()[:space:]]',
                    '',
                    'g')
            INTO STRICT
                observed_default_oid,
                observed_default_parent_oid,
                observed_default_bound,
                observed_default_constraint_valid,
                observed_default_constraint_expression
            FROM pg_class default_child
            JOIN pg_namespace default_namespace
              ON default_namespace.oid =
                    default_child.relnamespace
            JOIN pg_inherits default_inheritance
              ON default_inheritance.inhrelid =
                    default_child.oid
            JOIN pg_constraint default_constraint
              ON default_constraint.conrelid =
                    default_child.oid
             AND default_constraint.conname =
                    operation_row.default_exclusion_constraint
             AND default_constraint.contype = 'c'
            WHERE default_namespace.nspname =
                    operation_row.default_partition_schema
              AND default_child.relname =
                    operation_row.default_partition_relation
              AND default_child.relkind = 'r';
            EXECUTE format(
                'SELECT COUNT(*)::BIGINT FROM ONLY %I.%I WHERE snapshot_id = %s',
                operation_row.default_partition_schema,
                operation_row.default_partition_relation,
                operation_row.snapshot_id)
            INTO observed_default_target_row_count;
            IF observed_default_oid <>
                    operation_row.default_partition_oid
               OR observed_default_parent_oid <>
                    operation_row.root_oid
               OR observed_default_bound <> 'DEFAULT'
               OR NOT observed_default_constraint_valid
               OR observed_default_constraint_expression <>
                    'snapshot_id<>' ||
                    operation_row.snapshot_id::TEXT
               OR observed_default_target_row_count <> 0
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation default-partition exclusion changed before reattach.'
                    USING ERRCODE = '55000';
            END IF;

            EXECUTE format(
                'SELECT EXISTS (SELECT 1 FROM ONLY %I.%I WHERE snapshot_id <> %s LIMIT 1)',
                operation_row.quarantine_schema,
                operation_row.quarantine_relation,
                operation_row.snapshot_id)
            INTO observed_constraint_valid;
            IF observed_constraint_valid THEN
                RAISE EXCEPTION
                    'Quarantined snapshot-generation rows violate the exact snapshot constraint.'
                    USING ERRCODE = '55000';
            END IF;

            EXECUTE format(
                'SELECT COUNT(*)::BIGINT FROM ONLY %I.%I',
                operation_row.quarantine_schema,
                operation_row.quarantine_relation)
            INTO observed_row_count;
            IF observed_row_count <> operation_row.row_count THEN
                RAISE EXCEPTION
                    'Quarantined snapshot-generation row count changed before reattach.'
                    USING ERRCODE = '55000';
            END IF;

            EXECUTE format(
                'LOCK TABLE ONLY %I.%I IN ACCESS EXCLUSIVE MODE',
                operation_row.quarantine_schema,
                operation_row.quarantine_relation);
            index_inventory_before :=
                fst_snapshot_generation_index_inventory(
                    operation_row.child_oid,
                    operation_row.root_oid,
                    FALSE);
            FOREACH repair_role IN ARRAY ARRAY[
                'pk',
                'score'
            ]
            LOOP
                old_index_name :=
                    index_inventory_before
                        -> repair_role
                        ->> 'indexName';
                old_constraint_name :=
                    index_inventory_before
                        -> repair_role
                        ->> 'constraintName';
                semantic_before :=
                    index_inventory_before
                        -> repair_role
                        -> 'semantic';
                new_index_name :=
                    'sgqi_' || p_operation_id || '_' ||
                    repair_role;
                SELECT COUNT(*)::INTEGER
                INTO existing_rename_count
                FROM
                    snapshot_generation_quarantine_index_renames
                        rename_row
                WHERE rename_row.operation_id =
                        p_operation_id
                  AND rename_row.index_role =
                        repair_role;

                IF old_index_name = new_index_name THEN
                    IF existing_rename_count <> 1 THEN
                        RAISE EXCEPTION
                            'Snapshot-generation % index is normalized without exact immutable evidence.',
                            repair_role
                            USING ERRCODE = '55000';
                    END IF;
                    SELECT rename_row.*
                    INTO STRICT stored_rename
                    FROM
                        snapshot_generation_quarantine_index_renames
                            rename_row
                    WHERE rename_row.operation_id =
                            p_operation_id
                      AND rename_row.index_role =
                            repair_role;
                    IF stored_rename.index_oid <>
                            (
                                index_inventory_before
                                -> repair_role
                                ->> 'indexOid')::BIGINT
                       OR stored_rename.index_relfilenode <>
                            (
                                index_inventory_before
                                -> repair_role
                                ->> 'indexRelfilenode')
                                ::BIGINT
                       OR stored_rename.new_index_name <>
                            new_index_name
                       OR stored_rename.semantic_after <>
                            semantic_before
                    THEN
                        RAISE EXCEPTION
                            'Snapshot-generation % index normalization evidence is inconsistent.',
                            repair_role
                            USING ERRCODE = '55000';
                    END IF;
                ELSE
                    IF existing_rename_count <> 0
                       OR to_regclass(
                            format(
                                '%I.%I',
                                operation_row.quarantine_schema,
                                new_index_name)) IS NOT NULL
                       OR to_regclass(
                            format(
                                '%I.%I',
                                operation_row.child_schema,
                                new_index_name)) IS NOT NULL
                    THEN
                        RAISE EXCEPTION
                            'Snapshot-generation derived % index name or evidence is already occupied.',
                            repair_role
                            USING ERRCODE = '42P07';
                    END IF;
                    EXECUTE format(
                        'ALTER INDEX %I.%I RENAME TO %I',
                        operation_row.quarantine_schema,
                        old_index_name,
                        new_index_name);
                    index_inventory_after :=
                        fst_snapshot_generation_index_inventory(
                            operation_row.child_oid,
                            operation_row.root_oid,
                            FALSE);
                    semantic_after :=
                        index_inventory_after
                            -> repair_role
                            -> 'semantic';
                    new_constraint_name :=
                        index_inventory_after
                            -> repair_role
                            ->> 'constraintName';
                    IF semantic_after IS DISTINCT FROM
                            semantic_before
                       OR index_inventory_after
                            -> repair_role
                            ->> 'indexName' <>
                            new_index_name
                       OR (
                            repair_role = 'pk'
                            AND new_constraint_name <>
                                new_index_name)
                       OR (
                            repair_role = 'score'
                            AND new_constraint_name
                                IS NOT NULL)
                    THEN
                        RAISE EXCEPTION
                            'Snapshot-generation % index repair changed physical or semantic identity.',
                            repair_role
                            USING ERRCODE = '55000';
                    END IF;
                    INSERT INTO
                        snapshot_generation_quarantine_index_renames (
                            operation_id,
                            index_role,
                            index_oid,
                            index_relfilenode,
                            old_index_name,
                            new_index_name,
                            old_constraint_name,
                            new_constraint_name,
                            source_phase,
                            semantic_before,
                            semantic_after,
                            semantic_before_sha256,
                            semantic_after_sha256,
                            backend_pid,
                            transaction_id)
                    VALUES (
                        p_operation_id,
                        repair_role,
                        (
                            index_inventory_after
                            -> repair_role
                            ->> 'indexOid')::BIGINT,
                        (
                            index_inventory_after
                            -> repair_role
                            ->> 'indexRelfilenode')::BIGINT,
                        old_index_name,
                        new_index_name,
                        old_constraint_name,
                        new_constraint_name,
                        'reattach_repair',
                        semantic_before,
                        semantic_after,
                        encode(
                            digest(
                                convert_to(
                                    semantic_before::TEXT,
                                    'UTF8'),
                                'sha256'),
                            'hex'),
                        encode(
                            digest(
                                convert_to(
                                    semantic_after::TEXT,
                                    'UTF8'),
                                'sha256'),
                            'hex'),
                        pg_backend_pid(),
                        pg_current_xact_id()::TEXT);
                    index_inventory_before :=
                        index_inventory_after;
                END IF;
            END LOOP;
            SELECT jsonb_object_agg(
                rename_row.index_role,
                to_jsonb(rename_row)
                    - 'operation_id'
                    - 'index_role')
            INTO STRICT index_rename_evidence
            FROM
                snapshot_generation_quarantine_index_renames
                    rename_row
            WHERE rename_row.operation_id =
                    p_operation_id;
            IF (
                    SELECT COUNT(*)
                    FROM jsonb_object_keys(
                        index_rename_evidence)) <> 2
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation index normalization evidence is incomplete.'
                    USING ERRCODE = '55000';
            END IF;

            EXECUTE format(
                'DROP TRIGGER %I ON %I.%I',
                operation_row.mutation_guard_trigger,
                operation_row.quarantine_schema,
                operation_row.quarantine_relation);
            EXECUTE format(
                'ALTER TABLE %I.%I RENAME TO %I',
                operation_row.quarantine_schema,
                operation_row.quarantine_relation,
                operation_row.child_relation);
            EXECUTE format(
                'ALTER TABLE %I.%I SET SCHEMA %I',
                operation_row.quarantine_schema,
                operation_row.child_relation,
                operation_row.child_schema);
            EXECUTE format(
                'ALTER TABLE %I.%I ATTACH PARTITION %I.%I FOR VALUES IN (%s)',
                operation_row.root_schema,
                operation_row.root_relation,
                operation_row.child_schema,
                operation_row.child_relation,
                operation_row.snapshot_id);

            SELECT
                inheritance.inhparent::BIGINT,
                pg_get_expr(
                    child.relpartbound,
                    child.oid,
                    TRUE)
            INTO STRICT
                observed_parent_oid,
                observed_bound
            FROM pg_class child
            JOIN pg_namespace child_namespace
              ON child_namespace.oid = child.relnamespace
            JOIN pg_inherits inheritance
              ON inheritance.inhrelid = child.oid
            WHERE child_namespace.nspname =
                    operation_row.child_schema
              AND child.relname = operation_row.child_relation
              AND child.oid::BIGINT = operation_row.child_oid;

            SELECT COUNT(*)::INTEGER
            INTO attached_required_index_count
            FROM pg_index child_index
            JOIN pg_class child_index_relation
              ON child_index_relation.oid =
                    child_index.indexrelid
            JOIN
                snapshot_generation_quarantine_index_renames
                    rename_row
              ON rename_row.operation_id =
                    operation_row.operation_id
             AND rename_row.index_oid =
                    child_index.indexrelid
             AND rename_row.index_relfilenode =
                    child_index_relation.relfilenode
             AND rename_row.new_index_name =
                    child_index_relation.relname
            JOIN pg_inherits child_index_inheritance
              ON child_index_inheritance.inhrelid =
                    child_index.indexrelid
            JOIN pg_index root_index
              ON root_index.indexrelid =
                    child_index_inheritance.inhparent
             AND root_index.indrelid =
                    operation_row.root_oid
            JOIN pg_inherits root_index_inheritance
              ON root_index_inheritance.inhrelid =
                    root_index.indexrelid
            JOIN pg_class top_index_relation
              ON top_index_relation.oid =
                    root_index_inheritance.inhparent
            WHERE child_index.indrelid = operation_row.child_oid
              AND child_index.indisvalid
              AND child_index.indisready
              AND root_index.indisvalid
              AND root_index.indisready
              AND top_index_relation.relname IN (
                    'leaderboard_entries_snapshot_pkey',
                    'ix_les_snapshot_song_score');

            IF observed_parent_oid <> operation_row.root_oid
               OR observed_bound <>
                    format(
                        'FOR VALUES IN (%L)',
                        operation_row.snapshot_id)
               OR attached_required_index_count <> 2
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation reattach did not restore the exact parent/index hierarchy (parent %, expected %, bound %, expected %, attached required indexes %).',
                    observed_parent_oid,
                    operation_row.root_oid,
                    observed_bound,
                    format(
                        'FOR VALUES IN (%L)',
                        operation_row.snapshot_id),
                    attached_required_index_count
                    USING ERRCODE = '55000';
            END IF;

            EXECUTE format(
                'ALTER TABLE %I.%I DROP CONSTRAINT %I',
                operation_row.child_schema,
                operation_row.child_relation,
                operation_row.snapshot_check_constraint);
            EXECUTE format(
                'ALTER TABLE %I.%I DROP CONSTRAINT %I',
                operation_row.default_partition_schema,
                operation_row.default_partition_relation,
                operation_row.default_exclusion_constraint);

            UPDATE snapshot_generation_retention_holds
            SET released_by = current_user,
                released_at = clock_timestamp(),
                release_reason = format(
                    'reattached by snapshot-generation quarantine operation %s',
                    p_operation_id)
            WHERE hold_id = operation_row.hold_id
              AND released_at IS NULL;
            GET DIAGNOSTICS released_hold_count = ROW_COUNT;
            IF released_hold_count <> 1 THEN
                RAISE EXCEPTION
                    'Snapshot-generation quarantine hold release was not exact.'
                    USING ERRCODE = '55000';
            END IF;

            INSERT INTO
                snapshot_generation_quarantine_reattachments (
                    operation_id,
                    reattached_by,
                    reattach_reference,
                    reattach_evidence)
            VALUES (
                p_operation_id,
                p_reattached_by,
                p_reattach_reference,
                p_reattach_evidence ||
                    jsonb_build_object(
                        'indexRenames',
                        index_rename_evidence));

            RETURN p_operation_id;
        END
        $quarantine_reattach$;

        CREATE OR REPLACE FUNCTION
            fst_record_snapshot_generation_quarantine_attestation(
                p_operation_id TEXT,
                p_stage TEXT,
                p_publication_id BIGINT,
                p_published_scrape_id BIGINT,
                p_route_count INTEGER,
                p_status_parity BOOLEAN,
                p_semantic_json_parity BOOLEAN,
                p_difference_count INTEGER,
                p_baseline_route_manifest_sha256 TEXT,
                p_candidate_route_manifest_sha256 TEXT,
                p_database_evidence JSONB,
                p_evidence_sha256 TEXT,
                p_attested_by TEXT)
        RETURNS BIGINT
        LANGUAGE plpgsql
        SECURITY INVOKER
        AS $quarantine_attestation$
        DECLARE
            inserted_attestation_id BIGINT;
            operation_reattached BOOLEAN;
            operation_publication_id BIGINT;
            operation_scrape_id BIGINT;
            operation_candidate_route_sha256 TEXT;
            latest_soak_candidate_sha256 TEXT;
            state_row scrape_publication_state%ROWTYPE;
        BEGIN
            IF NOT pg_try_advisory_xact_lock(5067481511116518500)
               OR NOT pg_try_advisory_xact_lock(2026050901)
               OR NOT pg_try_advisory_xact_lock(5067481511116519500)
               OR NOT pg_try_advisory_xact_lock(2026082301)
               OR NOT pg_try_advisory_xact_lock(
                    hashtextextended(
                        'fst.snapshot-generation-partition-ddl',
                        0))
               OR NOT pg_try_advisory_xact_lock(2026083001)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation quarantine lock chain is busy; retry in a new transaction.'
                    USING ERRCODE = '55P03';
            END IF;

            IF p_stage NOT IN (
                    'quarantined',
                    'soak',
                    'reattached')
               OR p_route_count <> 55
               OR p_difference_count < 0
               OR p_baseline_route_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_candidate_route_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_evidence_sha256 !~ '^[0-9a-f]{64}$'
               OR COALESCE(p_attested_by, '') = ''
               OR jsonb_typeof(p_database_evidence) <> 'object'
               OR (
                    p_difference_count = 0
                    AND (
                        NOT p_status_parity
                        OR NOT p_semantic_json_parity))
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation quarantine attestation arguments are invalid.'
                    USING ERRCODE = '22023';
            END IF;

            SELECT
                operation.trigger_publication_id,
                operation.trigger_scrape_id,
                operation.candidate_route_manifest_sha256,
                EXISTS (
                    SELECT 1
                    FROM
                        snapshot_generation_quarantine_reattachments
                            reattach
                    WHERE reattach.operation_id =
                            operation.operation_id)
            INTO
                operation_publication_id,
                operation_scrape_id,
                operation_candidate_route_sha256,
                operation_reattached
            FROM snapshot_generation_quarantine_operations
                operation
            WHERE operation.operation_id = p_operation_id;

            SELECT state.*
            INTO STRICT state_row
            FROM scrape_publication_state state
            WHERE state.id = TRUE;
            SELECT attestation.candidate_route_manifest_sha256
            INTO latest_soak_candidate_sha256
            FROM snapshot_generation_quarantine_attestations
                attestation
            WHERE attestation.operation_id = p_operation_id
              AND attestation.stage = 'soak'
              AND attestation.difference_count = 0
              AND attestation.status_parity
              AND attestation.semantic_json_parity
            ORDER BY attestation.attestation_id DESC
            LIMIT 1;

            IF operation_publication_id IS NULL
               OR state_row.current_publication_id IS NULL
               OR state_row.published_scrape_id IS NULL
               OR p_publication_id <>
                    state_row.current_publication_id
               OR p_published_scrape_id <>
                    state_row.published_scrape_id
               OR state_row.public_reads_frozen
               OR state_row.working_publication_id IS NOT NULL
               OR state_row.publication_commit_intent_started_at
                    IS NOT NULL
               OR state_row.max_score_mutation_gate_token
                    IS NOT NULL
               OR state_row.improvement_notifications_scrape_id
                    IS DISTINCT FROM
                        state_row.published_scrape_id
               OR state_row.improvement_notifications_status
                    IS DISTINCT FROM 'completed'
               OR state_row.improvement_notifications_completed_at
                    IS NULL
               OR state_row.improvement_notifications_projection_ready
                    IS DISTINCT FROM TRUE
               OR state_row.improvement_notifications_projection_scrape_id
                    IS DISTINCT FROM
                        state_row.published_scrape_id
               OR NOT EXISTS (
                    SELECT 1
                    FROM publication_generations current_generation
                    WHERE current_generation.publication_id =
                            state_row.current_publication_id
                      AND current_generation.scrape_id =
                            state_row.published_scrape_id
                      AND current_generation.status = 'current')
               OR EXISTS (
                    SELECT 1
                    FROM scrape_log scrape
                    WHERE scrape.status = 'running')
               OR (
                    p_stage = 'quarantined'
                    AND (
                        p_publication_id <>
                            operation_publication_id
                        OR p_published_scrape_id <>
                            operation_scrape_id
                        OR p_baseline_route_manifest_sha256 <>
                            operation_candidate_route_sha256))
               OR (
                    p_stage = 'reattached'
                    AND (
                        latest_soak_candidate_sha256 IS NULL
                        OR p_baseline_route_manifest_sha256 <>
                            latest_soak_candidate_sha256))
               OR (
                    p_stage = 'reattached'
                    AND NOT operation_reattached)
               OR (
                    p_stage <> 'reattached'
                    AND operation_reattached)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation quarantine attestation stage does not match durable operation state.'
                    USING ERRCODE = '55000';
            END IF;

            INSERT INTO snapshot_generation_quarantine_attestations (
                operation_id,
                stage,
                publication_id,
                published_scrape_id,
                route_count,
                status_parity,
                semantic_json_parity,
                difference_count,
                baseline_route_manifest_sha256,
                candidate_route_manifest_sha256,
                database_evidence,
                evidence_sha256,
                attested_by)
            VALUES (
                p_operation_id,
                p_stage,
                p_publication_id,
                p_published_scrape_id,
                p_route_count,
                p_status_parity,
                p_semantic_json_parity,
                p_difference_count,
                p_baseline_route_manifest_sha256,
                p_candidate_route_manifest_sha256,
                p_database_evidence,
                p_evidence_sha256,
                p_attested_by)
            RETURNING attestation_id
            INTO inserted_attestation_id;

            RETURN inserted_attestation_id;
        END
        $quarantine_attestation$;

        REVOKE ALL ON FUNCTION
            fst_snapshot_generation_index_inventory(
                BIGINT,
                BIGINT,
                BOOLEAN)
            FROM PUBLIC;
        REVOKE ALL ON FUNCTION
            fst_lock_snapshot_generation_for_quarantine(
                BIGINT,
                BIGINT,
                BIGINT,
                BIGINT)
            FROM PUBLIC;
        REVOKE ALL ON FUNCTION
            fst_quarantine_snapshot_generation(
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                BIGINT,
                BIGINT,
                BIGINT,
                BIGINT,
                BIGINT,
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                JSONB)
            FROM PUBLIC;
        REVOKE ALL ON FUNCTION
            fst_reattach_snapshot_generation(
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                JSONB)
            FROM PUBLIC;
        REVOKE ALL ON FUNCTION
            fst_record_snapshot_generation_quarantine_attestation(
                TEXT,
                TEXT,
                BIGINT,
                BIGINT,
                INTEGER,
                BOOLEAN,
                BOOLEAN,
                INTEGER,
                TEXT,
                TEXT,
                JSONB,
                TEXT,
                TEXT)
            FROM PUBLIC;
        """;
}
