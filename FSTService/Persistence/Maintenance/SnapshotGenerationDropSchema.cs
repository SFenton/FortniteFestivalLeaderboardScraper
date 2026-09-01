namespace FSTService.Persistence.Maintenance;

public static class SnapshotGenerationDropContract
{
    public const int SchemaVersion = 1;
    public const string ToolId =
        "fst.snapshot-generation-drop-only.v1";
    public const long DropAdvisoryLockKey = 2026083002;
    public const int MinimumSoakSeconds = 1800;
    public const int MinimumHealthSamples = 60;
    public const int HealthSampleIntervalSeconds = 30;
}

public static class SnapshotGenerationDropSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS
            snapshot_generation_drop_operations (
                drop_operation_id              TEXT PRIMARY KEY,
                schema_version                 INTEGER NOT NULL,
                tool_id                        TEXT NOT NULL,
                plan_digest                    TEXT NOT NULL UNIQUE,
                rehearsal_quarantine_operation_id
                                               TEXT NOT NULL
                                               REFERENCES
                                                   snapshot_generation_quarantine_operations(
                                                       operation_id)
                                               ON DELETE RESTRICT,
                active_quarantine_operation_id TEXT NOT NULL UNIQUE
                                               REFERENCES
                                                   snapshot_generation_quarantine_operations(
                                                       operation_id)
                                               ON DELETE RESTRICT,
                rehearsal_quarantined_attestation_id
                                               BIGINT NOT NULL
                                               REFERENCES
                                                   snapshot_generation_quarantine_attestations(
                                                       attestation_id)
                                               ON DELETE RESTRICT,
                rehearsal_soak_attestation_id  BIGINT NOT NULL
                                               REFERENCES
                                                   snapshot_generation_quarantine_attestations(
                                                       attestation_id)
                                               ON DELETE RESTRICT,
                rehearsal_reattached_attestation_id
                                               BIGINT NOT NULL
                                               REFERENCES
                                                   snapshot_generation_quarantine_attestations(
                                                       attestation_id)
                                               ON DELETE RESTRICT,
                active_quarantined_attestation_id
                                               BIGINT NOT NULL
                                               REFERENCES
                                                   snapshot_generation_quarantine_attestations(
                                                       attestation_id)
                                               ON DELETE RESTRICT,
                active_soak_attestation_id     BIGINT NOT NULL
                                               REFERENCES
                                                   snapshot_generation_quarantine_attestations(
                                                       attestation_id)
                                               ON DELETE RESTRICT,
                rehearsal_archive_manifest_sha256
                                               TEXT NOT NULL,
                rehearsal_archive_proof_manifest_sha256
                                               TEXT NOT NULL,
                archive_manifest_sha256        TEXT NOT NULL,
                archive_sha256                 TEXT NOT NULL,
                archive_proof_manifest_sha256  TEXT NOT NULL,
                fresh_archive_proof_manifest_sha256
                                               TEXT NOT NULL,
                source_evidence_manifest_sha256 TEXT NOT NULL,
                recovery_bundle_manifest_sha256 TEXT NOT NULL,
                semantic_projection_version    INTEGER NOT NULL,
                rehearsal_catalog_sha256       TEXT NOT NULL,
                catalog_sha256                 TEXT NOT NULL,
                rehearsal_semantic_catalog_sha256
                                               TEXT NOT NULL,
                semantic_catalog_sha256        TEXT NOT NULL,
                rehearsal_logical_index_shape_sha256
                                               TEXT NOT NULL,
                logical_index_shape_sha256     TEXT NOT NULL,
                rehearsal_physical_index_inventory_sha256
                                               TEXT NOT NULL,
                physical_index_inventory_sha256
                                               TEXT NOT NULL,
                pre_drop_baseline_route_manifest_sha256
                                               TEXT NOT NULL,
                pre_drop_candidate_route_manifest_sha256
                                               TEXT NOT NULL,
                health_evidence_sha256         TEXT NOT NULL,
                binary_sha256                  TEXT NOT NULL,
                restore_tool_sha256            TEXT NOT NULL,
                restore_image_id_sha256        TEXT NOT NULL,
                repository_commit              TEXT NOT NULL,
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
                default_partition_schema       TEXT NOT NULL,
                default_partition_relation     TEXT NOT NULL,
                default_partition_oid          BIGINT NOT NULL,
                quarantine_default_exclusion_constraint
                                               TEXT NOT NULL,
                durable_default_exclusion_constraint
                                               TEXT NOT NULL,
                hold_id                        BIGINT NOT NULL UNIQUE
                                               REFERENCES
                                                   snapshot_generation_retention_holds(
                                                       hold_id)
                                               ON DELETE RESTRICT,
                stable_child_identity_hash     TEXT NOT NULL,
                stable_config_schema_hash      TEXT NOT NULL,
                row_count                      BIGINT NOT NULL,
                row_fingerprint_sha256         TEXT NOT NULL,
                logical_catalog_sha256         TEXT NOT NULL,
                total_bytes                    BIGINT NOT NULL,
                dependency_inventory           JSONB NOT NULL,
                dependency_inventory_sha256    TEXT NOT NULL,
                topology_evidence              JSONB NOT NULL,
                topology_sha256                TEXT NOT NULL,
                liveness_evidence              JSONB NOT NULL,
                liveness_sha256                TEXT NOT NULL,
                database_name                  TEXT NOT NULL,
                database_oid                   BIGINT NOT NULL,
                system_identifier              TEXT NOT NULL,
                server_version_num             INTEGER NOT NULL,
                health_started_at              TIMESTAMPTZ NOT NULL,
                health_completed_at            TIMESTAMPTZ NOT NULL,
                health_sample_count            INTEGER NOT NULL,
                health_sample_interval_seconds INTEGER NOT NULL,
                proof_completed_at             TIMESTAMPTZ NOT NULL,
                backend_pid                    INTEGER NOT NULL,
                transaction_id                 TEXT NOT NULL,
                approved_by                    TEXT NOT NULL,
                approval_reference             TEXT NOT NULL,
                preflight_evidence             JSONB NOT NULL,
                drop_evidence                  JSONB NOT NULL,
                dropped_at                     TIMESTAMPTZ NOT NULL
                                               DEFAULT clock_timestamp(),
                CONSTRAINT
                    ck_snapshot_generation_drop_operation_id
                    CHECK (
                        drop_operation_id ~ '^[0-9a-f]{32}$'),
                CONSTRAINT
                    ck_snapshot_generation_drop_contract
                    CHECK (
                        schema_version = 1
                        AND tool_id =
                            'fst.snapshot-generation-drop-only.v1'),
                CONSTRAINT
                    ck_snapshot_generation_drop_hashes
                    CHECK (
                        plan_digest ~ '^[0-9a-f]{64}$'
                        AND rehearsal_archive_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND rehearsal_archive_proof_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND archive_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND archive_sha256 ~ '^[0-9a-f]{64}$'
                        AND archive_proof_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND fresh_archive_proof_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND source_evidence_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND recovery_bundle_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND rehearsal_catalog_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND catalog_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND rehearsal_semantic_catalog_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND semantic_catalog_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND rehearsal_logical_index_shape_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND logical_index_shape_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND rehearsal_physical_index_inventory_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND physical_index_inventory_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND pre_drop_baseline_route_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND pre_drop_candidate_route_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND health_evidence_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND binary_sha256 ~ '^[0-9a-f]{64}$'
                        AND restore_tool_sha256 ~ '^[0-9a-f]{64}$'
                        AND restore_image_id_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND stable_child_identity_hash
                            ~ '^[0-9a-f]{64}$'
                        AND stable_config_schema_hash
                            ~ '^[0-9a-f]{64}$'
                        AND row_fingerprint_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND logical_catalog_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND dependency_inventory_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND topology_sha256 ~ '^[0-9a-f]{64}$'
                        AND liveness_sha256 ~ '^[0-9a-f]{64}$'),
                CONSTRAINT
                    ck_snapshot_generation_drop_commit
                    CHECK (
                        repository_commit ~ '^[0-9a-f]{40}$'
                        AND transaction_id ~ '^[0-9]+$'),
                CONSTRAINT
                    ck_snapshot_generation_drop_instrument
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
                    ck_snapshot_generation_drop_identity
                    CHECK (
                        rehearsal_quarantine_operation_id <>
                            active_quarantine_operation_id
                        AND cycle_id > 0
                        AND observation_id > 0
                        AND trigger_scrape_id > 0
                        AND trigger_publication_id > 0
                        AND snapshot_id > 0
                        AND root_oid > 0
                        AND child_oid > 0
                        AND child_relfilenode > 0
                        AND default_partition_oid > 0
                        AND hold_id > 0
                        AND row_count > 0
                        AND total_bytes > 0
                        AND database_oid > 0
                        AND server_version_num / 10000 = 17
                        AND semantic_projection_version = 1),
                CONSTRAINT
                    ck_snapshot_generation_drop_names
                    CHECK (
                        root_schema = 'public'
                        AND child_schema = 'public'
                        AND quarantine_schema =
                            'fst_snapshot_quarantine'
                        AND default_partition_schema = 'public'
                        AND root_relation <> ''
                        AND child_relation <> ''
                        AND quarantine_relation <> ''
                        AND default_partition_relation <> ''
                        AND quarantine_default_exclusion_constraint <> ''
                        AND durable_default_exclusion_constraint <> ''),
                CONSTRAINT
                    ck_snapshot_generation_drop_health
                    CHECK (
                        health_sample_count >= 60
                        AND health_sample_interval_seconds = 30
                        AND health_completed_at - health_started_at
                            >= interval '30 minutes'
                        AND proof_completed_at >= health_completed_at),
                CONSTRAINT
                    ck_snapshot_generation_drop_approval
                    CHECK (
                        approved_by <> ''
                        AND approval_reference <> ''),
                CONSTRAINT
                    ck_snapshot_generation_drop_json
                    CHECK (
                        jsonb_typeof(dependency_inventory) = 'array'
                        AND jsonb_typeof(topology_evidence) = 'object'
                        AND jsonb_typeof(liveness_evidence) = 'object'
                        AND jsonb_typeof(preflight_evidence) = 'object'
                        AND jsonb_typeof(drop_evidence) = 'object')
            );

        DO $drop_operations_upgrade$
        DECLARE
            missing_semantic_column BOOLEAN;
            hash_constraint_current BOOLEAN;
            identity_constraint_current BOOLEAN;
        BEGIN
            SELECT EXISTS (
                SELECT 1
                FROM unnest(ARRAY[
                    'semantic_projection_version',
                    'rehearsal_catalog_sha256',
                    'catalog_sha256',
                    'rehearsal_semantic_catalog_sha256',
                    'semantic_catalog_sha256',
                    'rehearsal_logical_index_shape_sha256',
                    'logical_index_shape_sha256',
                    'rehearsal_physical_index_inventory_sha256',
                    'physical_index_inventory_sha256'
                ]) required(column_name)
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM pg_attribute attribute
                    WHERE attribute.attrelid =
                            'snapshot_generation_drop_operations'
                                ::regclass
                      AND attribute.attname =
                            required.column_name
                      AND attribute.attnum > 0
                      AND NOT attribute.attisdropped))
            INTO missing_semantic_column;

            IF missing_semantic_column
               AND EXISTS (
                    SELECT 1
                    FROM snapshot_generation_drop_operations)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation drop schema cannot upgrade nonempty pre-semantic committed evidence.'
                    USING ERRCODE = '55000';
            END IF;

            IF missing_semantic_column THEN
                ALTER TABLE snapshot_generation_drop_operations
                    ADD COLUMN IF NOT EXISTS
                        semantic_projection_version
                        INTEGER NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        rehearsal_catalog_sha256
                        TEXT NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        catalog_sha256
                        TEXT NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        rehearsal_semantic_catalog_sha256
                        TEXT NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        semantic_catalog_sha256
                        TEXT NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        rehearsal_logical_index_shape_sha256
                        TEXT NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        logical_index_shape_sha256
                        TEXT NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        rehearsal_physical_index_inventory_sha256
                        TEXT NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        physical_index_inventory_sha256
                        TEXT NOT NULL;
            END IF;

            IF EXISTS (
                SELECT 1
                FROM (
                    VALUES
                        ('semantic_projection_version',
                            'integer'::regtype),
                        ('rehearsal_catalog_sha256',
                            'text'::regtype),
                        ('catalog_sha256',
                            'text'::regtype),
                        ('rehearsal_semantic_catalog_sha256',
                            'text'::regtype),
                        ('semantic_catalog_sha256',
                            'text'::regtype),
                        ('rehearsal_logical_index_shape_sha256',
                            'text'::regtype),
                        ('logical_index_shape_sha256',
                            'text'::regtype),
                        ('rehearsal_physical_index_inventory_sha256',
                            'text'::regtype),
                        ('physical_index_inventory_sha256',
                            'text'::regtype)
                ) required(column_name, type_oid)
                LEFT JOIN pg_attribute attribute
                  ON attribute.attrelid =
                        'snapshot_generation_drop_operations'
                            ::regclass
                 AND attribute.attname =
                        required.column_name
                 AND attribute.attnum > 0
                 AND NOT attribute.attisdropped
                WHERE attribute.attname IS NULL
                   OR attribute.atttypid <> required.type_oid)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation drop semantic columns have an unsupported type.'
                    USING ERRCODE = '55000';
            END IF;

            IF EXISTS (
                SELECT 1
                FROM pg_attribute attribute
                WHERE attribute.attrelid =
                        'snapshot_generation_drop_operations'
                            ::regclass
                  AND attribute.attname = ANY(ARRAY[
                        'semantic_projection_version',
                        'rehearsal_catalog_sha256',
                        'catalog_sha256',
                        'rehearsal_semantic_catalog_sha256',
                        'semantic_catalog_sha256',
                        'rehearsal_logical_index_shape_sha256',
                        'logical_index_shape_sha256',
                        'rehearsal_physical_index_inventory_sha256',
                        'physical_index_inventory_sha256'
                    ])
                  AND attribute.attnum > 0
                  AND NOT attribute.attisdropped
                  AND NOT attribute.attnotnull)
            THEN
                ALTER TABLE snapshot_generation_drop_operations
                    ALTER COLUMN
                        semantic_projection_version
                        SET NOT NULL,
                    ALTER COLUMN
                        rehearsal_catalog_sha256
                        SET NOT NULL,
                    ALTER COLUMN catalog_sha256
                        SET NOT NULL,
                    ALTER COLUMN
                        rehearsal_semantic_catalog_sha256
                        SET NOT NULL,
                    ALTER COLUMN semantic_catalog_sha256
                        SET NOT NULL,
                    ALTER COLUMN
                        rehearsal_logical_index_shape_sha256
                        SET NOT NULL,
                    ALTER COLUMN
                        logical_index_shape_sha256
                        SET NOT NULL,
                    ALTER COLUMN
                        rehearsal_physical_index_inventory_sha256
                        SET NOT NULL,
                    ALTER COLUMN
                        physical_index_inventory_sha256
                        SET NOT NULL;
            END IF;

            SELECT
                COUNT(*) = 1
                AND bool_and(
                    constraint_row.convalidated
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\mrehearsal_catalog_sha256\M'
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\mcatalog_sha256\M'
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\mrehearsal_semantic_catalog_sha256\M'
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\msemantic_catalog_sha256\M'
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\mrehearsal_logical_index_shape_sha256\M'
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\mlogical_index_shape_sha256\M'
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\mrehearsal_physical_index_inventory_sha256\M'
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\mphysical_index_inventory_sha256\M')
            INTO hash_constraint_current
            FROM pg_constraint constraint_row
            WHERE constraint_row.conrelid =
                    'snapshot_generation_drop_operations'
                        ::regclass
              AND constraint_row.conname =
                    'ck_snapshot_generation_drop_hashes'
              AND constraint_row.contype = 'c';

            SELECT
                COUNT(*) = 1
                AND bool_and(
                    constraint_row.convalidated
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\msemantic_projection_version\M')
            INTO identity_constraint_current
            FROM pg_constraint constraint_row
            WHERE constraint_row.conrelid =
                    'snapshot_generation_drop_operations'
                        ::regclass
              AND constraint_row.conname =
                    'ck_snapshot_generation_drop_identity'
              AND constraint_row.contype = 'c';

            IF missing_semantic_column
               OR hash_constraint_current
                    IS DISTINCT FROM TRUE
               OR identity_constraint_current
                    IS DISTINCT FROM TRUE
            THEN
                ALTER TABLE snapshot_generation_drop_operations
                    DROP CONSTRAINT IF EXISTS
                        ck_snapshot_generation_drop_hashes,
                    DROP CONSTRAINT IF EXISTS
                        ck_snapshot_generation_drop_identity;
                ALTER TABLE snapshot_generation_drop_operations
                    ADD CONSTRAINT
                        ck_snapshot_generation_drop_hashes
                        CHECK (
                            plan_digest ~ '^[0-9a-f]{64}$'
                            AND rehearsal_archive_manifest_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND rehearsal_archive_proof_manifest_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND archive_manifest_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND archive_sha256 ~ '^[0-9a-f]{64}$'
                            AND archive_proof_manifest_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND fresh_archive_proof_manifest_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND source_evidence_manifest_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND recovery_bundle_manifest_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND rehearsal_catalog_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND catalog_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND rehearsal_semantic_catalog_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND semantic_catalog_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND rehearsal_logical_index_shape_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND logical_index_shape_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND rehearsal_physical_index_inventory_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND physical_index_inventory_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND pre_drop_baseline_route_manifest_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND pre_drop_candidate_route_manifest_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND health_evidence_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND binary_sha256 ~ '^[0-9a-f]{64}$'
                            AND restore_tool_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND restore_image_id_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND stable_child_identity_hash
                                ~ '^[0-9a-f]{64}$'
                            AND stable_config_schema_hash
                                ~ '^[0-9a-f]{64}$'
                            AND row_fingerprint_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND logical_catalog_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND dependency_inventory_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND topology_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND liveness_sha256
                                ~ '^[0-9a-f]{64}$'),
                    ADD CONSTRAINT
                        ck_snapshot_generation_drop_identity
                        CHECK (
                            rehearsal_quarantine_operation_id <>
                                active_quarantine_operation_id
                            AND cycle_id > 0
                            AND observation_id > 0
                            AND trigger_scrape_id > 0
                            AND trigger_publication_id > 0
                            AND snapshot_id > 0
                            AND root_oid > 0
                            AND child_oid > 0
                            AND child_relfilenode > 0
                            AND default_partition_oid > 0
                            AND hold_id > 0
                            AND row_count > 0
                            AND total_bytes > 0
                            AND database_oid > 0
                            AND server_version_num / 10000 = 17
                            AND semantic_projection_version = 1);
            END IF;
        END
        $drop_operations_upgrade$;

        CREATE INDEX IF NOT EXISTS
            ix_snapshot_generation_drop_operations_target
            ON snapshot_generation_drop_operations (
                instrument,
                snapshot_id,
                dropped_at DESC);

        CREATE TABLE IF NOT EXISTS
            snapshot_generation_drop_attestations (
                attestation_id                 BIGINT
                                               GENERATED BY DEFAULT AS IDENTITY
                                               PRIMARY KEY,
                drop_operation_id              TEXT NOT NULL
                                               REFERENCES
                                                   snapshot_generation_drop_operations(
                                                       drop_operation_id)
                                               ON DELETE RESTRICT,
                stage                          TEXT NOT NULL,
                publication_id                 BIGINT NOT NULL,
                published_scrape_id            BIGINT NOT NULL,
                route_count                    INTEGER NOT NULL,
                baseline_route_manifest_sha256 TEXT NOT NULL,
                candidate_route_manifest_sha256 TEXT NOT NULL,
                status_parity                  BOOLEAN NOT NULL,
                semantic_json_parity           BOOLEAN NOT NULL,
                difference_count               INTEGER NOT NULL,
                database_evidence              JSONB NOT NULL,
                evidence_sha256                TEXT NOT NULL,
                attested_by                    TEXT NOT NULL,
                attested_at                    TIMESTAMPTZ NOT NULL
                                               DEFAULT clock_timestamp(),
                CONSTRAINT
                    ck_snapshot_generation_drop_attestation_stage
                    CHECK (
                        stage IN (
                            'pre_drop',
                            'dropped',
                            'post_publication')),
                CONSTRAINT
                    ck_snapshot_generation_drop_attestation_values
                    CHECK (
                        publication_id > 0
                        AND published_scrape_id > 0
                        AND route_count = 55
                        AND status_parity
                        AND semantic_json_parity
                        AND difference_count = 0
                        AND attested_by <> ''
                        AND jsonb_typeof(database_evidence) =
                            'object'),
                CONSTRAINT
                    ck_snapshot_generation_drop_attestation_hashes
                    CHECK (
                        baseline_route_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND candidate_route_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND evidence_sha256 ~ '^[0-9a-f]{64}$'),
                CONSTRAINT
                    ux_snapshot_generation_drop_attestation
                    UNIQUE (
                        drop_operation_id,
                        stage,
                        publication_id,
                        candidate_route_manifest_sha256)
            );

        CREATE TABLE IF NOT EXISTS
            snapshot_generation_restore_tool_authorizations (
                authorization_id               TEXT PRIMARY KEY,
                schema_version                 INTEGER NOT NULL,
                tool_id                        TEXT NOT NULL,
                drop_operation_id              TEXT NOT NULL
                                               REFERENCES
                                                   snapshot_generation_drop_operations(
                                                       drop_operation_id)
                                               ON DELETE RESTRICT,
                drop_plan_digest               TEXT NOT NULL,
                original_bundle_manifest_sha256 TEXT NOT NULL,
                pinned_restore_tool_sha256     TEXT NOT NULL,
                validator_base_tool_sha256     TEXT NOT NULL,
                authorized_restore_tool_sha256 TEXT NOT NULL,
                authorized_archive_helper_sha256
                                               TEXT NOT NULL,
                authorizer_binary_sha256       TEXT NOT NULL,
                repair_package_manifest_sha256 TEXT NOT NULL,
                repository_commit              TEXT NOT NULL,
                repository_tree_id             TEXT NOT NULL,
                pinned_to_base_diff_sha256     TEXT NOT NULL,
                base_to_final_diff_sha256      TEXT NOT NULL,
                source_manifest_sha256         TEXT NOT NULL,
                test_evidence_manifest_sha256  TEXT NOT NULL,
                reason_code                    TEXT NOT NULL,
                reason_text                    TEXT NOT NULL,
                approved_by                    TEXT NOT NULL,
                reviewed_by                    TEXT NOT NULL,
                approval_reference             TEXT NOT NULL,
                canonical_evidence             JSONB NOT NULL,
                evidence_sha256                TEXT NOT NULL,
                canonical_evidence_db_sha256   TEXT NOT NULL,
                backend_pid                    INTEGER NOT NULL,
                transaction_id                 TEXT NOT NULL,
                authorized_at                  TIMESTAMPTZ NOT NULL
                                               DEFAULT clock_timestamp(),
                CONSTRAINT
                    ux_snapshot_generation_restore_tool_authorization_drop
                    UNIQUE (
                        drop_operation_id,
                        authorization_id),
                CONSTRAINT
                    ck_snapshot_generation_restore_tool_authorization_id
                    CHECK (
                        authorization_id ~
                            '^[0-9a-f]{32}$'),
                CONSTRAINT
                    ck_snapshot_generation_restore_tool_authorization_contract
                    CHECK (
                        schema_version = 1
                        AND tool_id =
                            'fst.snapshot-generation-restore-tool-authorization.v1'),
                CONSTRAINT
                    ck_snapshot_generation_restore_tool_authorization_hashes
                    CHECK (
                        drop_plan_digest ~ '^[0-9a-f]{64}$'
                        AND original_bundle_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND pinned_restore_tool_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND validator_base_tool_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND authorized_restore_tool_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND authorized_archive_helper_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND authorizer_binary_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND repair_package_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND pinned_to_base_diff_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND base_to_final_diff_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND source_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND test_evidence_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND evidence_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND canonical_evidence_db_sha256
                            ~ '^[0-9a-f]{64}$'),
                CONSTRAINT
                    ck_snapshot_generation_restore_tool_authorization_source
                    CHECK (
                        repository_commit ~ '^[0-9a-f]{40}$'
                        AND repository_tree_id
                            ~ '^[0-9a-f]{40}$'
                        AND authorized_restore_tool_sha256 <>
                            pinned_restore_tool_sha256),
                CONSTRAINT
                    ck_snapshot_generation_restore_tool_authorization_approval
                    CHECK (
                        reason_code <> ''
                        AND reason_text <> ''
                        AND approved_by <> ''
                        AND reviewed_by <> ''
                        AND approved_by <> reviewed_by
                        AND approval_reference <> ''
                        AND backend_pid > 0
                        AND transaction_id ~ '^[0-9]+$'
                        AND jsonb_typeof(
                            canonical_evidence) =
                            'object'),
                CONSTRAINT
                    ck_snapshot_generation_restore_tool_authorization_database_evidence
                    CHECK (
                        canonical_evidence_db_sha256 =
                            encode(
                                digest(
                                    convert_to(
                                        canonical_evidence::TEXT,
                                        'UTF8'),
                                    'sha256'),
                                'hex')
                        AND authorization_id =
                            left(
                                encode(
                                    digest(
                                        convert_to(
                                            tool_id || ':' ||
                                            drop_operation_id || ':' ||
                                            drop_plan_digest || ':' ||
                                            original_bundle_manifest_sha256 || ':' ||
                                            pinned_restore_tool_sha256 || ':' ||
                                            validator_base_tool_sha256 || ':' ||
                                            authorized_restore_tool_sha256 || ':' ||
                                            authorized_archive_helper_sha256 || ':' ||
                                            authorizer_binary_sha256 || ':' ||
                                            repair_package_manifest_sha256 || ':' ||
                                            repository_commit || ':' ||
                                            repository_tree_id || ':' ||
                                            pinned_to_base_diff_sha256 || ':' ||
                                            base_to_final_diff_sha256 || ':' ||
                                            source_manifest_sha256 || ':' ||
                                            test_evidence_manifest_sha256 || ':' ||
                                            evidence_sha256 || ':' ||
                                            canonical_evidence_db_sha256,
                                            'UTF8'),
                                        'sha256'),
                                    'hex'),
                                32))
            );

        CREATE INDEX IF NOT EXISTS
            ix_snapshot_generation_restore_tool_authorizations_drop
            ON snapshot_generation_restore_tool_authorizations (
                drop_operation_id,
                authorized_at DESC);

        CREATE TABLE IF NOT EXISTS
            snapshot_generation_restore_operations (
                restore_operation_id           TEXT PRIMARY KEY,
                schema_version                 INTEGER NOT NULL,
                tool_id                        TEXT NOT NULL,
                plan_digest                    TEXT NOT NULL UNIQUE,
                drop_operation_id              TEXT NOT NULL UNIQUE
                                               REFERENCES
                                                   snapshot_generation_drop_operations(
                                                       drop_operation_id)
                                               ON DELETE RESTRICT,
                archive_manifest_sha256        TEXT NOT NULL,
                archive_sha256                 TEXT NOT NULL,
                recovery_bundle_manifest_sha256 TEXT NOT NULL,
                pinned_tool_sha256              TEXT NOT NULL,
                executing_tool_sha256           TEXT NOT NULL,
                authorization_id                TEXT,
                instrument                     TEXT NOT NULL,
                snapshot_id                    BIGINT NOT NULL
                                               REFERENCES scrape_log(id)
                                               ON DELETE RESTRICT,
                root_schema                    TEXT NOT NULL,
                root_relation                  TEXT NOT NULL,
                root_oid                       BIGINT NOT NULL,
                child_schema                   TEXT NOT NULL,
                child_relation                 TEXT NOT NULL,
                restored_child_oid             BIGINT NOT NULL,
                restored_child_relfilenode     BIGINT NOT NULL,
                partition_bound                TEXT NOT NULL,
                row_count                      BIGINT NOT NULL,
                row_fingerprint_sha256         TEXT NOT NULL,
                logical_catalog_sha256         TEXT NOT NULL,
                semantic_catalog_sha256        TEXT NOT NULL,
                logical_index_shape_sha256     TEXT NOT NULL,
                archived_index_names           JSONB NOT NULL,
                restored_index_evidence        JSONB NOT NULL,
                attached_index_count           INTEGER NOT NULL,
                hold_id                        BIGINT NOT NULL
                                               REFERENCES
                                                   snapshot_generation_retention_holds(
                                                       hold_id)
                                               ON DELETE RESTRICT,
                restored_by                    TEXT NOT NULL,
                restore_reference              TEXT NOT NULL,
                restore_evidence               JSONB NOT NULL,
                backend_pid                    INTEGER NOT NULL,
                transaction_id                 TEXT NOT NULL,
                restored_at                    TIMESTAMPTZ NOT NULL
                                               DEFAULT clock_timestamp(),
                CONSTRAINT
                    ck_snapshot_generation_restore_operation_id
                    CHECK (
                        restore_operation_id ~ '^[0-9a-f]{32}$'),
                CONSTRAINT
                    ck_snapshot_generation_restore_contract
                    CHECK (
                        schema_version = 1
                        AND tool_id =
                            'fst.snapshot-generation-restore.v1'),
                CONSTRAINT
                    ck_snapshot_generation_restore_hashes
                    CHECK (
                        plan_digest ~ '^[0-9a-f]{64}$'
                        AND archive_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND archive_sha256 ~ '^[0-9a-f]{64}$'
                        AND recovery_bundle_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND pinned_tool_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND executing_tool_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND row_fingerprint_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND logical_catalog_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND semantic_catalog_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND logical_index_shape_sha256
                            ~ '^[0-9a-f]{64}$'),
                CONSTRAINT
                    ck_snapshot_generation_restore_identity
                    CHECK (
                        snapshot_id > 0
                        AND root_schema = 'public'
                        AND child_schema = 'public'
                        AND root_relation <> ''
                        AND child_relation <> ''
                        AND root_oid > 0
                        AND restored_child_oid > 0
                        AND restored_child_relfilenode > 0
                        AND partition_bound <> ''
                        AND row_count > 0
                        AND attached_index_count = 2
                        AND hold_id > 0
                        AND transaction_id ~ '^[0-9]+$'
                        AND restored_by <> ''
                        AND restore_reference <> ''
                        AND jsonb_typeof(
                            archived_index_names) =
                            'object'
                        AND jsonb_typeof(
                            restored_index_evidence) =
                            'object'
                        AND (
                            (
                                executing_tool_sha256 =
                                    pinned_tool_sha256
                                AND authorization_id
                                    IS NULL)
                            OR (
                                executing_tool_sha256 <>
                                    pinned_tool_sha256
                                AND authorization_id
                                    IS NOT NULL))
                        AND jsonb_typeof(restore_evidence) =
                            'object'),
                CONSTRAINT
                    fk_snapshot_generation_restore_tool_authorization
                    FOREIGN KEY (
                        drop_operation_id,
                        authorization_id)
                    REFERENCES
                        snapshot_generation_restore_tool_authorizations (
                            drop_operation_id,
                            authorization_id)
                    MATCH SIMPLE
                    ON DELETE RESTRICT,
                CONSTRAINT
                    ux_snapshot_generation_restore_tool_authorization_consumption
                    UNIQUE (authorization_id)
            );

        DO $restore_operations_upgrade$
        DECLARE
            missing_semantic_column BOOLEAN;
            hash_constraint_current BOOLEAN;
            identity_constraint_current BOOLEAN;
            authorization_fk_current BOOLEAN;
            authorization_consumption_current BOOLEAN;
        BEGIN
            SELECT EXISTS (
                SELECT 1
                FROM unnest(ARRAY[
                    'semantic_catalog_sha256',
                    'logical_index_shape_sha256',
                    'archived_index_names',
                    'restored_index_evidence',
                    'pinned_tool_sha256',
                    'executing_tool_sha256',
                    'authorization_id'
                ]) required(column_name)
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM pg_attribute attribute
                    WHERE attribute.attrelid =
                            'snapshot_generation_restore_operations'
                                ::regclass
                      AND attribute.attname =
                            required.column_name
                      AND attribute.attnum > 0
                      AND NOT attribute.attisdropped))
            INTO missing_semantic_column;

            IF missing_semantic_column
               AND EXISTS (
                    SELECT 1
                    FROM snapshot_generation_restore_operations)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore schema cannot upgrade nonempty pre-semantic committed evidence.'
                    USING ERRCODE = '55000';
            END IF;

            IF missing_semantic_column THEN
                ALTER TABLE snapshot_generation_restore_operations
                    ADD COLUMN IF NOT EXISTS
                        semantic_catalog_sha256
                        TEXT NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        logical_index_shape_sha256
                        TEXT NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        archived_index_names
                        JSONB NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        restored_index_evidence
                        JSONB NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        pinned_tool_sha256
                        TEXT NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        executing_tool_sha256
                        TEXT NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        authorization_id
                        TEXT;
            END IF;

            IF EXISTS (
                SELECT 1
                FROM (
                    VALUES
                        ('semantic_catalog_sha256',
                            'text'::regtype),
                        ('logical_index_shape_sha256',
                            'text'::regtype),
                        ('archived_index_names',
                            'jsonb'::regtype),
                        ('restored_index_evidence',
                            'jsonb'::regtype),
                        ('pinned_tool_sha256',
                            'text'::regtype),
                        ('executing_tool_sha256',
                            'text'::regtype),
                        ('authorization_id',
                            'text'::regtype)
                ) required(column_name, type_oid)
                LEFT JOIN pg_attribute attribute
                  ON attribute.attrelid =
                        'snapshot_generation_restore_operations'
                            ::regclass
                 AND attribute.attname =
                        required.column_name
                 AND attribute.attnum > 0
                 AND NOT attribute.attisdropped
                WHERE attribute.attname IS NULL
                   OR attribute.atttypid <> required.type_oid)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore semantic columns have an unsupported type.'
                    USING ERRCODE = '55000';
            END IF;

            IF EXISTS (
                SELECT 1
                FROM pg_attribute attribute
                WHERE attribute.attrelid =
                        'snapshot_generation_restore_operations'
                            ::regclass
                  AND attribute.attname = ANY(ARRAY[
                        'semantic_catalog_sha256',
                        'logical_index_shape_sha256',
                        'archived_index_names',
                        'restored_index_evidence',
                        'pinned_tool_sha256',
                        'executing_tool_sha256'
                    ])
                  AND attribute.attnum > 0
                  AND NOT attribute.attisdropped
                  AND NOT attribute.attnotnull)
            THEN
                ALTER TABLE snapshot_generation_restore_operations
                    ALTER COLUMN semantic_catalog_sha256
                        SET NOT NULL,
                    ALTER COLUMN logical_index_shape_sha256
                        SET NOT NULL,
                    ALTER COLUMN archived_index_names
                        SET NOT NULL,
                    ALTER COLUMN restored_index_evidence
                        SET NOT NULL,
                    ALTER COLUMN pinned_tool_sha256
                        SET NOT NULL,
                    ALTER COLUMN executing_tool_sha256
                        SET NOT NULL;
            END IF;

            SELECT
                COUNT(*) = 1
                AND bool_and(
                    constraint_row.convalidated
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\msemantic_catalog_sha256\M'
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\mlogical_index_shape_sha256\M'
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\mpinned_tool_sha256\M'
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\mexecuting_tool_sha256\M')
            INTO hash_constraint_current
            FROM pg_constraint constraint_row
            WHERE constraint_row.conrelid =
                    'snapshot_generation_restore_operations'
                        ::regclass
              AND constraint_row.conname =
                    'ck_snapshot_generation_restore_hashes'
              AND constraint_row.contype = 'c';

            SELECT
                COUNT(*) = 1
                AND bool_and(
                    constraint_row.convalidated
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\marchived_index_names\M'
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\mrestored_index_evidence\M'
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\mauthorization_id\M'
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\mpinned_tool_sha256\M'
                    AND pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE)
                        ~ '\mexecuting_tool_sha256\M')
            INTO identity_constraint_current
            FROM pg_constraint constraint_row
            WHERE constraint_row.conrelid =
                    'snapshot_generation_restore_operations'
                        ::regclass
              AND constraint_row.conname =
                    'ck_snapshot_generation_restore_identity'
              AND constraint_row.contype = 'c';

            IF missing_semantic_column
               OR hash_constraint_current
                    IS DISTINCT FROM TRUE
               OR identity_constraint_current
                    IS DISTINCT FROM TRUE
            THEN
                ALTER TABLE snapshot_generation_restore_operations
                    DROP CONSTRAINT IF EXISTS
                        ck_snapshot_generation_restore_hashes,
                    DROP CONSTRAINT IF EXISTS
                        ck_snapshot_generation_restore_identity;
                ALTER TABLE snapshot_generation_restore_operations
                    ADD CONSTRAINT
                        ck_snapshot_generation_restore_hashes
                        CHECK (
                            plan_digest ~ '^[0-9a-f]{64}$'
                            AND archive_manifest_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND archive_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND recovery_bundle_manifest_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND pinned_tool_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND executing_tool_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND row_fingerprint_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND logical_catalog_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND semantic_catalog_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND logical_index_shape_sha256
                                ~ '^[0-9a-f]{64}$'),
                    ADD CONSTRAINT
                        ck_snapshot_generation_restore_identity
                        CHECK (
                            snapshot_id > 0
                            AND root_schema = 'public'
                            AND child_schema = 'public'
                            AND root_relation <> ''
                            AND child_relation <> ''
                            AND root_oid > 0
                            AND restored_child_oid > 0
                            AND restored_child_relfilenode > 0
                            AND partition_bound <> ''
                            AND row_count > 0
                            AND attached_index_count = 2
                            AND hold_id > 0
                            AND transaction_id ~ '^[0-9]+$'
                            AND restored_by <> ''
                            AND restore_reference <> ''
                            AND jsonb_typeof(
                                archived_index_names) =
                                'object'
                            AND jsonb_typeof(
                                restored_index_evidence) =
                                'object'
                            AND (
                                (
                                    executing_tool_sha256 =
                                        pinned_tool_sha256
                                    AND authorization_id
                                        IS NULL)
                                OR (
                                    executing_tool_sha256 <>
                                        pinned_tool_sha256
                                    AND authorization_id
                                        IS NOT NULL))
                            AND jsonb_typeof(
                                restore_evidence) =
                                'object');
            END IF;

            SELECT
                COUNT(*) = 1
                AND bool_and(
                    constraint_row.convalidated)
            INTO authorization_fk_current
            FROM pg_constraint constraint_row
            WHERE constraint_row.conrelid =
                    'snapshot_generation_restore_operations'
                        ::regclass
              AND constraint_row.conname =
                    'fk_snapshot_generation_restore_tool_authorization'
              AND constraint_row.contype = 'f';

            SELECT
                COUNT(*) = 1
                AND bool_and(
                    constraint_row.convalidated)
            INTO authorization_consumption_current
            FROM pg_constraint constraint_row
            WHERE constraint_row.conrelid =
                    'snapshot_generation_restore_operations'
                        ::regclass
              AND constraint_row.conname =
                    'ux_snapshot_generation_restore_tool_authorization_consumption'
              AND constraint_row.contype = 'u';

            IF missing_semantic_column
               OR authorization_fk_current
                    IS DISTINCT FROM TRUE
               OR authorization_consumption_current
                    IS DISTINCT FROM TRUE
            THEN
                ALTER TABLE snapshot_generation_restore_operations
                    DROP CONSTRAINT IF EXISTS
                        fk_snapshot_generation_restore_tool_authorization,
                    DROP CONSTRAINT IF EXISTS
                        ux_snapshot_generation_restore_tool_authorization_consumption;
                ALTER TABLE snapshot_generation_restore_operations
                    ADD CONSTRAINT
                        fk_snapshot_generation_restore_tool_authorization
                        FOREIGN KEY (
                            drop_operation_id,
                            authorization_id)
                        REFERENCES
                            snapshot_generation_restore_tool_authorizations (
                                drop_operation_id,
                                authorization_id)
                        MATCH SIMPLE
                        ON DELETE RESTRICT,
                    ADD CONSTRAINT
                        ux_snapshot_generation_restore_tool_authorization_consumption
                        UNIQUE (authorization_id);
            END IF;
        END
        $restore_operations_upgrade$;

        CREATE TABLE IF NOT EXISTS
            snapshot_generation_restore_continuation_authorizations (
                continuation_authorization_id TEXT PRIMARY KEY,
                schema_version                 INTEGER NOT NULL,
                tool_id                        TEXT NOT NULL,
                authorization_scope            TEXT NOT NULL,
                restore_operation_id           TEXT NOT NULL
                                               REFERENCES
                                                   snapshot_generation_restore_operations(
                                                       restore_operation_id)
                                               ON DELETE RESTRICT,
                drop_operation_id              TEXT NOT NULL
                                               REFERENCES
                                                   snapshot_generation_drop_operations(
                                                       drop_operation_id)
                                               ON DELETE RESTRICT,
                predecessor_authorization_id   TEXT NOT NULL,
                restore_plan_digest            TEXT NOT NULL,
                restore_plan_file_sha256       TEXT NOT NULL,
                restore_report_sha256          TEXT NOT NULL,
                predecessor_restore_tool_sha256 TEXT NOT NULL,
                predecessor_repair_package_manifest_sha256
                                               TEXT NOT NULL,
                recovery_bundle_manifest_sha256 TEXT NOT NULL,
                authorized_continuation_tool_sha256
                                               TEXT NOT NULL,
                authorized_evidence_assembly_sha256
                                               TEXT NOT NULL,
                route_parity_reference_source_sha256
                                               TEXT NOT NULL,
                authorizer_binary_sha256       TEXT NOT NULL,
                continuation_package_manifest_sha256
                                               TEXT NOT NULL,
                route_parity_algorithm_id      TEXT NOT NULL,
                route_parity_preflight_sha256  TEXT NOT NULL,
                baseline_route_manifest_sha256 TEXT NOT NULL,
                baseline_route_checksums_sha256 TEXT NOT NULL,
                candidate_route_manifest_sha256 TEXT NOT NULL,
                candidate_route_checksums_sha256 TEXT NOT NULL,
                publication_id                 BIGINT NOT NULL,
                published_scrape_id            BIGINT NOT NULL,
                repository_commit              TEXT NOT NULL,
                repository_tree_id             TEXT NOT NULL,
                predecessor_to_continuation_diff_sha256
                                               TEXT NOT NULL,
                source_manifest_sha256         TEXT NOT NULL,
                test_evidence_manifest_sha256  TEXT NOT NULL,
                reason_code                    TEXT NOT NULL,
                reason_text                    TEXT NOT NULL,
                approved_by                    TEXT NOT NULL,
                reviewed_by                    TEXT NOT NULL,
                approval_reference             TEXT NOT NULL,
                canonical_evidence             JSONB NOT NULL,
                evidence_sha256                TEXT NOT NULL,
                canonical_evidence_db_sha256   TEXT NOT NULL,
                database_user                  TEXT NOT NULL,
                backend_pid                    INTEGER NOT NULL,
                transaction_id                 TEXT NOT NULL,
                authorized_at                  TIMESTAMPTZ NOT NULL
                                               DEFAULT clock_timestamp(),
                CONSTRAINT
                    ux_snapshot_generation_restore_continuation_authorization_restore
                    UNIQUE (
                        restore_operation_id,
                        continuation_authorization_id),
                CONSTRAINT
                    ux_snapshot_generation_restore_continuation_authorization_tool
                    UNIQUE (
                        restore_operation_id,
                        authorized_continuation_tool_sha256),
                CONSTRAINT
                    fk_snapshot_generation_restore_continuation_predecessor
                    FOREIGN KEY (
                        drop_operation_id,
                        predecessor_authorization_id)
                    REFERENCES
                        snapshot_generation_restore_tool_authorizations (
                            drop_operation_id,
                            authorization_id)
                    ON DELETE RESTRICT,
                CONSTRAINT
                    ck_snapshot_generation_restore_continuation_authorization_id
                    CHECK (
                        continuation_authorization_id
                            ~ '^[0-9a-f]{32}$'),
                CONSTRAINT
                    ck_snapshot_generation_restore_continuation_authorization_contract
                    CHECK (
                        schema_version = 1
                        AND tool_id =
                            'fst.snapshot-generation-restore-continuation-authorization.v1'
                        AND authorization_scope =
                            'confirm_attest_finalize'
                        AND route_parity_algorithm_id =
                            'fst.route-parity.canonical-zip.v1'),
                CONSTRAINT
                    ck_snapshot_generation_restore_continuation_authorization_hashes
                    CHECK (
                        restore_plan_digest
                            ~ '^[0-9a-f]{64}$'
                        AND restore_plan_file_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND restore_report_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND predecessor_restore_tool_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND predecessor_repair_package_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND recovery_bundle_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND authorized_continuation_tool_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND authorized_evidence_assembly_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND route_parity_reference_source_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND authorizer_binary_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND continuation_package_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND route_parity_preflight_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND baseline_route_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND baseline_route_checksums_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND candidate_route_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND candidate_route_checksums_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND predecessor_to_continuation_diff_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND source_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND test_evidence_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND evidence_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND canonical_evidence_db_sha256
                            ~ '^[0-9a-f]{64}$'),
                CONSTRAINT
                    ck_snapshot_generation_restore_continuation_authorization_source
                    CHECK (
                        repository_commit
                            ~ '^[0-9a-f]{40}$'
                        AND repository_tree_id
                            ~ '^[0-9a-f]{40}$'
                        AND predecessor_authorization_id
                            ~ '^[0-9a-f]{32}$'
                        AND authorized_continuation_tool_sha256 <>
                            predecessor_restore_tool_sha256),
                CONSTRAINT
                    ck_snapshot_generation_restore_continuation_authorization_evidence
                    CHECK (
                        restore_operation_id
                            ~ '^[0-9a-f]{32}$'
                        AND drop_operation_id
                            ~ '^[0-9a-f]{32}$'
                        AND publication_id > 0
                        AND published_scrape_id > 0
                        AND reason_code
                            ~ '^[a-z0-9_]+$'
                        AND reason_text <> ''
                        AND approved_by <> ''
                        AND reviewed_by <> ''
                        AND approved_by <> reviewed_by
                        AND approval_reference <> ''
                        AND database_user <> ''
                        AND backend_pid > 0
                        AND transaction_id
                            ~ '^[0-9]+$'
                        AND jsonb_typeof(
                            canonical_evidence) =
                            'object'),
                CONSTRAINT
                    ck_snapshot_generation_restore_continuation_authorization_database_evidence
                    CHECK (
                        canonical_evidence_db_sha256 =
                            encode(
                                digest(
                                    convert_to(
                                        canonical_evidence::TEXT,
                                        'UTF8'),
                                    'sha256'),
                                'hex')
                        AND continuation_authorization_id =
                            left(
                                encode(
                                    digest(
                                        convert_to(
                                            tool_id || ':' ||
                                            authorization_scope || ':' ||
                                            restore_operation_id || ':' ||
                                            drop_operation_id || ':' ||
                                            predecessor_authorization_id || ':' ||
                                            restore_plan_digest || ':' ||
                                            restore_plan_file_sha256 || ':' ||
                                            restore_report_sha256 || ':' ||
                                            predecessor_restore_tool_sha256 || ':' ||
                                            predecessor_repair_package_manifest_sha256 || ':' ||
                                            recovery_bundle_manifest_sha256 || ':' ||
                                            authorized_continuation_tool_sha256 || ':' ||
                                            authorized_evidence_assembly_sha256 || ':' ||
                                            route_parity_reference_source_sha256 || ':' ||
                                            authorizer_binary_sha256 || ':' ||
                                            continuation_package_manifest_sha256 || ':' ||
                                            route_parity_algorithm_id || ':' ||
                                            route_parity_preflight_sha256 || ':' ||
                                            baseline_route_manifest_sha256 || ':' ||
                                            baseline_route_checksums_sha256 || ':' ||
                                            candidate_route_manifest_sha256 || ':' ||
                                            candidate_route_checksums_sha256 || ':' ||
                                            publication_id::TEXT || ':' ||
                                            published_scrape_id::TEXT || ':' ||
                                            repository_commit || ':' ||
                                            repository_tree_id || ':' ||
                                            predecessor_to_continuation_diff_sha256 || ':' ||
                                            source_manifest_sha256 || ':' ||
                                            test_evidence_manifest_sha256 || ':' ||
                                            evidence_sha256 || ':' ||
                                            canonical_evidence_db_sha256,
                                            'UTF8'),
                                        'sha256'),
                                    'hex'),
                                32))
            );

        CREATE TABLE IF NOT EXISTS
            snapshot_generation_restore_attestations (
                restore_operation_id           TEXT PRIMARY KEY
                                               REFERENCES
                                                   snapshot_generation_restore_operations(
                                                       restore_operation_id)
                                               ON DELETE RESTRICT,
                publication_id                 BIGINT NOT NULL,
                published_scrape_id            BIGINT NOT NULL,
                route_count                    INTEGER NOT NULL,
                baseline_route_manifest_sha256 TEXT NOT NULL,
                candidate_route_manifest_sha256 TEXT NOT NULL,
                status_parity                  BOOLEAN NOT NULL,
                semantic_json_parity           BOOLEAN NOT NULL,
                semantic_binary_parity         BOOLEAN NOT NULL,
                difference_count               INTEGER NOT NULL,
                route_parity_algorithm_id      TEXT NOT NULL,
                route_semantic_evidence_sha256 TEXT NOT NULL,
                database_evidence              JSONB NOT NULL,
                evidence_sha256                TEXT NOT NULL,
                evidence_tool_sha256           TEXT NOT NULL,
                continuation_authorization_id  TEXT NOT NULL,
                attested_by                    TEXT NOT NULL,
                attested_at                    TIMESTAMPTZ NOT NULL
                                               DEFAULT clock_timestamp(),
                finalized_at                   TIMESTAMPTZ,
                CONSTRAINT
                    ck_snapshot_generation_restore_attestation
                    CHECK (
                        publication_id > 0
                        AND published_scrape_id > 0
                        AND route_count = 55
                        AND status_parity
                        AND semantic_json_parity
                        AND semantic_binary_parity
                        AND difference_count = 0
                        AND route_parity_algorithm_id =
                            'fst.route-parity.canonical-zip.v1'
                        AND baseline_route_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND candidate_route_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND route_semantic_evidence_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND evidence_sha256 ~ '^[0-9a-f]{64}$'
                        AND evidence_tool_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND continuation_authorization_id
                            ~ '^[0-9a-f]{32}$'
                        AND attested_by <> ''
                        AND jsonb_typeof(database_evidence) =
                            'object'),
                CONSTRAINT
                    fk_snapshot_generation_restore_attestation_continuation
                    FOREIGN KEY (
                        restore_operation_id,
                        continuation_authorization_id)
                    REFERENCES
                        snapshot_generation_restore_continuation_authorizations (
                            restore_operation_id,
                            continuation_authorization_id)
                    ON DELETE RESTRICT
            );

        CREATE TABLE IF NOT EXISTS
            snapshot_generation_restore_finalizations (
                restore_operation_id           TEXT PRIMARY KEY
                                               REFERENCES
                                                   snapshot_generation_restore_operations(
                                                       restore_operation_id)
                                               ON DELETE RESTRICT,
                finalized_by                   TEXT NOT NULL,
                finalize_reference             TEXT NOT NULL,
                finalization_evidence           JSONB NOT NULL,
                evidence_tool_sha256           TEXT NOT NULL,
                continuation_authorization_id  TEXT NOT NULL,
                finalized_at                   TIMESTAMPTZ NOT NULL
                                               DEFAULT clock_timestamp(),
                CONSTRAINT
                    ck_snapshot_generation_restore_finalize
                    CHECK (
                        finalized_by <> ''
                        AND finalize_reference <> ''
                        AND evidence_tool_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND continuation_authorization_id
                            ~ '^[0-9a-f]{32}$'
                        AND jsonb_typeof(finalization_evidence) =
                            'object'),
                CONSTRAINT
                    fk_snapshot_generation_restore_finalization_continuation
                    FOREIGN KEY (
                        restore_operation_id,
                        continuation_authorization_id)
                    REFERENCES
                        snapshot_generation_restore_continuation_authorizations (
                            restore_operation_id,
                            continuation_authorization_id)
                    ON DELETE RESTRICT
            );

        DO $restore_continuation_evidence_upgrade$
        DECLARE
            attestation_missing_columns BOOLEAN;
            attestation_check_current BOOLEAN;
            attestation_fk_current BOOLEAN;
            finalization_missing_columns BOOLEAN;
            finalization_check_current BOOLEAN;
            finalization_fk_current BOOLEAN;
        BEGIN
            SELECT EXISTS (
                SELECT 1
                FROM unnest(ARRAY[
                    'semantic_binary_parity',
                    'route_parity_algorithm_id',
                    'route_semantic_evidence_sha256',
                    'evidence_tool_sha256',
                    'continuation_authorization_id'
                ]) expected(column_name)
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM information_schema.columns column_row
                    WHERE column_row.table_schema = 'public'
                      AND column_row.table_name =
                            'snapshot_generation_restore_attestations'
                      AND column_row.column_name =
                            expected.column_name))
            INTO attestation_missing_columns;

            SELECT
                COUNT(*) = 1
                AND bool_and(
                    position(
                        'semantic_binary_parity'
                        IN pg_get_constraintdef(
                            constraint_row.oid)) > 0)
                AND bool_and(
                    position(
                        'route_parity_algorithm_id'
                        IN pg_get_constraintdef(
                            constraint_row.oid)) > 0)
            INTO attestation_check_current
            FROM pg_constraint constraint_row
            WHERE constraint_row.conrelid =
                    'snapshot_generation_restore_attestations'
                        ::regclass
              AND constraint_row.conname =
                    'ck_snapshot_generation_restore_attestation'
              AND constraint_row.contype = 'c';

            SELECT
                COUNT(*) = 1
                AND bool_and(
                    constraint_row.convalidated)
            INTO attestation_fk_current
            FROM pg_constraint constraint_row
            WHERE constraint_row.conrelid =
                    'snapshot_generation_restore_attestations'
                        ::regclass
              AND constraint_row.conname =
                    'fk_snapshot_generation_restore_attestation_continuation'
              AND constraint_row.contype = 'f';

            IF (
                    attestation_missing_columns
                    OR attestation_check_current
                        IS DISTINCT FROM TRUE
                    OR attestation_fk_current
                        IS DISTINCT FROM TRUE)
               AND EXISTS (
                    SELECT 1
                    FROM snapshot_generation_restore_attestations)
            THEN
                RAISE EXCEPTION
                    'Cannot add continuation identity to nonempty snapshot-generation restore attestations.'
                    USING ERRCODE = '55000';
            END IF;

            IF attestation_missing_columns THEN
                ALTER TABLE
                    snapshot_generation_restore_attestations
                    ADD COLUMN IF NOT EXISTS
                        semantic_binary_parity
                            BOOLEAN NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        route_parity_algorithm_id
                            TEXT NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        route_semantic_evidence_sha256
                            TEXT NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        evidence_tool_sha256
                            TEXT NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        continuation_authorization_id
                            TEXT NOT NULL;
            END IF;

            IF attestation_missing_columns
               OR attestation_check_current
                    IS DISTINCT FROM TRUE
               OR attestation_fk_current
                    IS DISTINCT FROM TRUE
            THEN
                ALTER TABLE
                    snapshot_generation_restore_attestations
                    DROP CONSTRAINT IF EXISTS
                        ck_snapshot_generation_restore_attestation,
                    DROP CONSTRAINT IF EXISTS
                        fk_snapshot_generation_restore_attestation_continuation;
                ALTER TABLE
                    snapshot_generation_restore_attestations
                    ADD CONSTRAINT
                        ck_snapshot_generation_restore_attestation
                        CHECK (
                            publication_id > 0
                            AND published_scrape_id > 0
                            AND route_count = 55
                            AND status_parity
                            AND semantic_json_parity
                            AND semantic_binary_parity
                            AND difference_count = 0
                            AND route_parity_algorithm_id =
                                'fst.route-parity.canonical-zip.v1'
                            AND baseline_route_manifest_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND candidate_route_manifest_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND route_semantic_evidence_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND evidence_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND evidence_tool_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND continuation_authorization_id
                                ~ '^[0-9a-f]{32}$'
                            AND attested_by <> ''
                            AND jsonb_typeof(
                                database_evidence) =
                                'object'),
                    ADD CONSTRAINT
                        fk_snapshot_generation_restore_attestation_continuation
                        FOREIGN KEY (
                            restore_operation_id,
                            continuation_authorization_id)
                        REFERENCES
                            snapshot_generation_restore_continuation_authorizations (
                                restore_operation_id,
                                continuation_authorization_id)
                        ON DELETE RESTRICT;
            END IF;

            SELECT EXISTS (
                SELECT 1
                FROM unnest(ARRAY[
                    'evidence_tool_sha256',
                    'continuation_authorization_id'
                ]) expected(column_name)
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM information_schema.columns column_row
                    WHERE column_row.table_schema = 'public'
                      AND column_row.table_name =
                            'snapshot_generation_restore_finalizations'
                      AND column_row.column_name =
                            expected.column_name))
            INTO finalization_missing_columns;

            SELECT
                COUNT(*) = 1
                AND bool_and(
                    position(
                        'evidence_tool_sha256'
                        IN pg_get_constraintdef(
                            constraint_row.oid)) > 0)
            INTO finalization_check_current
            FROM pg_constraint constraint_row
            WHERE constraint_row.conrelid =
                    'snapshot_generation_restore_finalizations'
                        ::regclass
              AND constraint_row.conname =
                    'ck_snapshot_generation_restore_finalize'
              AND constraint_row.contype = 'c';

            SELECT
                COUNT(*) = 1
                AND bool_and(
                    constraint_row.convalidated)
            INTO finalization_fk_current
            FROM pg_constraint constraint_row
            WHERE constraint_row.conrelid =
                    'snapshot_generation_restore_finalizations'
                        ::regclass
              AND constraint_row.conname =
                    'fk_snapshot_generation_restore_finalization_continuation'
              AND constraint_row.contype = 'f';

            IF (
                    finalization_missing_columns
                    OR finalization_check_current
                        IS DISTINCT FROM TRUE
                    OR finalization_fk_current
                        IS DISTINCT FROM TRUE)
               AND EXISTS (
                    SELECT 1
                    FROM snapshot_generation_restore_finalizations)
            THEN
                RAISE EXCEPTION
                    'Cannot add continuation identity to nonempty snapshot-generation restore finalizations.'
                    USING ERRCODE = '55000';
            END IF;

            IF finalization_missing_columns THEN
                ALTER TABLE
                    snapshot_generation_restore_finalizations
                    ADD COLUMN IF NOT EXISTS
                        evidence_tool_sha256
                            TEXT NOT NULL,
                    ADD COLUMN IF NOT EXISTS
                        continuation_authorization_id
                            TEXT NOT NULL;
            END IF;

            IF finalization_missing_columns
               OR finalization_check_current
                    IS DISTINCT FROM TRUE
               OR finalization_fk_current
                    IS DISTINCT FROM TRUE
            THEN
                ALTER TABLE
                    snapshot_generation_restore_finalizations
                    DROP CONSTRAINT IF EXISTS
                        ck_snapshot_generation_restore_finalize,
                    DROP CONSTRAINT IF EXISTS
                        fk_snapshot_generation_restore_finalization_continuation;
                ALTER TABLE
                    snapshot_generation_restore_finalizations
                    ADD CONSTRAINT
                        ck_snapshot_generation_restore_finalize
                        CHECK (
                            finalized_by <> ''
                            AND finalize_reference <> ''
                            AND evidence_tool_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND continuation_authorization_id
                                ~ '^[0-9a-f]{32}$'
                            AND jsonb_typeof(
                                finalization_evidence) =
                                'object'),
                    ADD CONSTRAINT
                        fk_snapshot_generation_restore_finalization_continuation
                        FOREIGN KEY (
                            restore_operation_id,
                            continuation_authorization_id)
                        REFERENCES
                            snapshot_generation_restore_continuation_authorizations (
                                restore_operation_id,
                                continuation_authorization_id)
                        ON DELETE RESTRICT;
            END IF;
        END
        $restore_continuation_evidence_upgrade$;

        CREATE TABLE IF NOT EXISTS
            snapshot_generation_drop_evidence (
                evidence_id                    BIGINT
                                               GENERATED BY DEFAULT AS IDENTITY
                                               PRIMARY KEY,
                drop_operation_id              TEXT NOT NULL
                                               REFERENCES
                                                   snapshot_generation_drop_operations(
                                                       drop_operation_id)
                                               ON DELETE RESTRICT,
                sequence                       INTEGER NOT NULL,
                phase                          TEXT NOT NULL,
                kind                           TEXT NOT NULL,
                payload                        JSONB NOT NULL,
                previous_hash                  TEXT,
                current_hash                   TEXT NOT NULL,
                created_at                     TIMESTAMPTZ NOT NULL
                                               DEFAULT clock_timestamp(),
                CONSTRAINT
                    ux_snapshot_generation_drop_evidence_sequence
                    UNIQUE (drop_operation_id, sequence),
                CONSTRAINT
                    ck_snapshot_generation_drop_evidence_values
                    CHECK (
                        sequence > 0
                        AND phase <> ''
                        AND kind <> ''
                        AND jsonb_typeof(payload) = 'object'),
                CONSTRAINT
                    ck_snapshot_generation_drop_evidence_hashes
                    CHECK (
                        (
                            previous_hash IS NULL
                            OR previous_hash ~ '^[0-9a-f]{64}$')
                        AND current_hash ~ '^[0-9a-f]{64}$')
            );

        CREATE OR REPLACE FUNCTION
            fst_reject_snapshot_generation_drop_evidence_mutation()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $drop_immutable$
        BEGIN
            RAISE EXCEPTION
                'Snapshot-generation drop evidence is immutable.'
                USING ERRCODE = '55000';
        END
        $drop_immutable$;

        DO $drop_immutable_triggers$
        DECLARE
            relation_name TEXT;
            trigger_name TEXT;
        BEGIN
            FOREACH relation_name IN ARRAY ARRAY[
                'snapshot_generation_drop_operations',
                'snapshot_generation_drop_attestations',
                'snapshot_generation_restore_tool_authorizations',
                'snapshot_generation_restore_operations',
                'snapshot_generation_restore_continuation_authorizations',
                'snapshot_generation_restore_attestations',
                'snapshot_generation_restore_finalizations',
                'snapshot_generation_drop_evidence'
            ]
            LOOP
                trigger_name := CASE
                    WHEN relation_name =
                        'snapshot_generation_restore_tool_authorizations'
                    THEN
                        'trg_reject_sgr_tool_authorization_mutation'
                    WHEN relation_name =
                        'snapshot_generation_restore_continuation_authorizations'
                    THEN
                        'trg_reject_sgr_continuation_authorization_mutation'
                    ELSE
                        'trg_reject_' || relation_name || '_mutation'
                END;
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_trigger trigger_row
                    WHERE trigger_row.tgrelid =
                            to_regclass(
                                'public.' || relation_name)
                      AND trigger_row.tgname = trigger_name
                      AND NOT trigger_row.tgisinternal)
                THEN
                    EXECUTE format(
                        'CREATE TRIGGER %I BEFORE UPDATE OR DELETE OR TRUNCATE ON public.%I FOR EACH STATEMENT EXECUTE FUNCTION fst_reject_snapshot_generation_drop_evidence_mutation()',
                        trigger_name,
                        relation_name);
                END IF;
            END LOOP;
        END
        $drop_immutable_triggers$;

        CREATE OR REPLACE FUNCTION
            fst_reject_snapshot_generation_quarantine_after_drop()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $drop_quarantine_terminal$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM snapshot_generation_drop_operations drop_row
                WHERE drop_row.active_quarantine_operation_id =
                        NEW.operation_id
                   OR drop_row.rehearsal_quarantine_operation_id =
                        NEW.operation_id)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation quarantine operation is terminally bound to a committed drop.'
                    USING ERRCODE = '55000';
            END IF;
            RETURN NEW;
        END
        $drop_quarantine_terminal$;

        DO $drop_quarantine_guards$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_trigger trigger_row
                WHERE trigger_row.tgrelid =
                        'snapshot_generation_quarantine_reattachments'
                            ::regclass
                  AND trigger_row.tgname =
                        'trg_sgq_reject_reattach_after_drop'
                  AND NOT trigger_row.tgisinternal)
            THEN
                CREATE TRIGGER
                    trg_sgq_reject_reattach_after_drop
                    BEFORE INSERT ON
                        snapshot_generation_quarantine_reattachments
                    FOR EACH ROW EXECUTE FUNCTION
                        fst_reject_snapshot_generation_quarantine_after_drop();
            END IF;
            IF NOT EXISTS (
                SELECT 1
                FROM pg_trigger trigger_row
                WHERE trigger_row.tgrelid =
                        'snapshot_generation_quarantine_attestations'
                            ::regclass
                  AND trigger_row.tgname =
                        'trg_sgq_reject_attestation_after_drop'
                  AND NOT trigger_row.tgisinternal)
            THEN
                CREATE TRIGGER
                    trg_sgq_reject_attestation_after_drop
                    BEFORE INSERT ON
                        snapshot_generation_quarantine_attestations
                    FOR EACH ROW EXECUTE FUNCTION
                        fst_reject_snapshot_generation_quarantine_after_drop();
            END IF;
        END
        $drop_quarantine_guards$;

        CREATE OR REPLACE FUNCTION
            fst_authorize_snapshot_generation_restore_tool(
                p_drop_operation_id TEXT,
                p_drop_plan_digest TEXT,
                p_original_bundle_manifest_sha256 TEXT,
                p_pinned_restore_tool_sha256 TEXT,
                p_validator_base_tool_sha256 TEXT,
                p_authorized_restore_tool_sha256 TEXT,
                p_authorized_archive_helper_sha256 TEXT,
                p_authorizer_binary_sha256 TEXT,
                p_repair_package_manifest_sha256 TEXT,
                p_repository_commit TEXT,
                p_repository_tree_id TEXT,
                p_pinned_to_base_diff_sha256 TEXT,
                p_base_to_final_diff_sha256 TEXT,
                p_source_manifest_sha256 TEXT,
                p_test_evidence_manifest_sha256 TEXT,
                p_reason_code TEXT,
                p_reason_text TEXT,
                p_approved_by TEXT,
                p_reviewed_by TEXT,
                p_approval_reference TEXT,
                p_canonical_evidence JSONB,
                p_evidence_sha256 TEXT)
        RETURNS TEXT
        LANGUAGE plpgsql
        SECURITY INVOKER
        SET search_path = pg_catalog, public
        AS $restore_tool_authorize$
        DECLARE
            drop_row
                snapshot_generation_drop_operations%ROWTYPE;
            existing
                snapshot_generation_restore_tool_authorizations%ROWTYPE;
            authorization_id_value TEXT;
            canonical_evidence_db_sha256_value TEXT;
            default_row_count BIGINT;
            reference_conflict_count INTEGER;
            state_row scrape_publication_state%ROWTYPE;
        BEGIN
            PERFORM set_config('lock_timeout', '5s', TRUE);
            PERFORM set_config(
                'statement_timeout',
                '30s',
                TRUE);
            PERFORM set_config(
                'idle_in_transaction_session_timeout',
                '60s',
                TRUE);
            PERFORM set_config(
                'transaction_timeout',
                '60s',
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
               OR NOT pg_try_advisory_xact_lock(2026083002)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore-tool authorization lock chain is busy.'
                    USING ERRCODE = '55P03';
            END IF;

            IF p_drop_operation_id !~ '^[0-9a-f]{32}$'
               OR p_drop_plan_digest !~ '^[0-9a-f]{64}$'
               OR p_original_bundle_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_pinned_restore_tool_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_validator_base_tool_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_authorized_restore_tool_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_authorized_archive_helper_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_authorizer_binary_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_repair_package_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_repository_commit !~ '^[0-9a-f]{40}$'
               OR p_repository_tree_id !~ '^[0-9a-f]{40}$'
               OR p_pinned_to_base_diff_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_base_to_final_diff_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_source_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_test_evidence_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_evidence_sha256 !~ '^[0-9a-f]{64}$'
               OR COALESCE(p_reason_code, '') = ''
               OR p_reason_code !~ '^[a-z0-9_]+$'
               OR COALESCE(p_reason_text, '') = ''
               OR COALESCE(p_approved_by, '') = ''
               OR COALESCE(p_reviewed_by, '') = ''
               OR p_approved_by = p_reviewed_by
               OR COALESCE(p_approval_reference, '') = ''
               OR jsonb_typeof(p_canonical_evidence) <>
                    'object'
               OR p_authorized_restore_tool_sha256 =
                    p_pinned_restore_tool_sha256
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore-tool authorization arguments are invalid.'
                    USING ERRCODE = '22023';
            END IF;

            SELECT operation.*
            INTO STRICT drop_row
            FROM snapshot_generation_drop_operations
                operation
            WHERE operation.drop_operation_id =
                    p_drop_operation_id
              AND operation.plan_digest =
                    p_drop_plan_digest;

            IF drop_row.restore_tool_sha256 <>
                    p_pinned_restore_tool_sha256
               OR drop_row.recovery_bundle_manifest_sha256 <>
                    p_original_bundle_manifest_sha256
               OR p_approved_by = drop_row.approved_by
               OR p_reviewed_by = drop_row.approved_by
               OR p_approval_reference =
                    drop_row.approval_reference
               OR (
                    drop_row.instrument = 'Solo_Bass'
                    AND drop_row.snapshot_id = 1308)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore-tool authorization differs from immutable drop evidence.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT COUNT(*)::INTEGER
            INTO reference_conflict_count
            FROM (
                SELECT operation.approval_reference
                FROM snapshot_generation_quarantine_operations
                    operation
                WHERE operation.operation_id IN (
                    drop_row.rehearsal_quarantine_operation_id,
                    drop_row.active_quarantine_operation_id)
                UNION ALL
                SELECT reattachment.reattach_reference
                FROM snapshot_generation_quarantine_reattachments
                    reattachment
                WHERE reattachment.operation_id =
                    drop_row.rehearsal_quarantine_operation_id
            ) prior_reference
            WHERE prior_reference.approval_reference =
                    p_approval_reference;
            IF reference_conflict_count <> 0 THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore-tool authorization reuses prior approval evidence.'
                    USING ERRCODE = '55000';
            END IF;

            IF EXISTS (
                    SELECT 1
                    FROM snapshot_generation_restore_operations
                        restore_row
                    WHERE restore_row.drop_operation_id =
                            drop_row.drop_operation_id)
               OR to_regclass(
                    format(
                        '%I.%I',
                        drop_row.child_schema,
                        drop_row.child_relation)) IS NOT NULL
               OR EXISTS (
                    SELECT 1
                    FROM pg_class relation
                    WHERE relation.oid =
                            drop_row.child_oid)
               OR NOT EXISTS (
                    SELECT 1
                    FROM snapshot_generation_retention_holds
                        hold_row
                    WHERE hold_row.hold_id =
                            drop_row.hold_id
                      AND hold_row.released_at IS NULL)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore-tool authorization drop state is invalid.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT state.*
            INTO STRICT state_row
            FROM scrape_publication_state state
            WHERE state.id = TRUE;
            IF state_row.current_publication_id IS NULL
               OR state_row.public_reads_frozen
               OR state_row.working_publication_id IS NOT NULL
               OR state_row.publication_commit_intent_started_at
                    IS NOT NULL
               OR state_row.max_score_mutation_gate_token
                    IS NOT NULL
               OR EXISTS (
                    SELECT 1
                    FROM scrape_log scrape
                    WHERE scrape.status = 'running')
               OR NOT EXISTS (
                    SELECT 1
                    FROM service_worker_status worker
                    WHERE worker.worker_key = 'scraper'
                      AND worker.status = 'offline'
                      AND worker.current_operation_json
                            IS NULL)
            THEN
                RAISE EXCEPTION
                    'Publication state is not idle for restore-tool authorization.'
                    USING ERRCODE = '55000';
            END IF;

            IF NOT EXISTS (
                    SELECT 1
                    FROM pg_constraint constraint_row
                    WHERE constraint_row.conrelid =
                            drop_row.default_partition_oid
                      AND constraint_row.conname =
                            drop_row.durable_default_exclusion_constraint
                      AND constraint_row.contype = 'c'
                      AND constraint_row.convalidated
                      AND regexp_replace(
                            pg_get_expr(
                                constraint_row.conbin,
                                constraint_row.conrelid,
                                TRUE),
                            '[()[:space:]]',
                            '',
                            'g') =
                            'snapshot_id<>' ||
                            drop_row.snapshot_id::TEXT)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore-tool authorization DEFAULT fence is invalid.'
                    USING ERRCODE = '55000';
            END IF;
            EXECUTE format(
                'SELECT COUNT(*)::BIGINT FROM ONLY %I.%I',
                drop_row.default_partition_schema,
                drop_row.default_partition_relation)
            INTO default_row_count;
            IF default_row_count <> 0 THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore-tool authorization DEFAULT partition is not empty.'
                    USING ERRCODE = '55000';
            END IF;

            canonical_evidence_db_sha256_value :=
                encode(
                    digest(
                        convert_to(
                            p_canonical_evidence::TEXT,
                            'UTF8'),
                        'sha256'),
                    'hex');
            authorization_id_value :=
                left(
                    encode(
                        digest(
                            convert_to(
                                'fst.snapshot-generation-restore-tool-authorization.v1'
                                || ':' ||
                                p_drop_operation_id
                                || ':' ||
                                p_drop_plan_digest
                                || ':' ||
                                p_original_bundle_manifest_sha256
                                || ':' ||
                                p_pinned_restore_tool_sha256
                                || ':' ||
                                p_validator_base_tool_sha256
                                || ':' ||
                                p_authorized_restore_tool_sha256
                                || ':' ||
                                p_authorized_archive_helper_sha256
                                || ':' ||
                                p_authorizer_binary_sha256
                                || ':' ||
                                p_repair_package_manifest_sha256
                                || ':' ||
                                p_repository_commit
                                || ':' ||
                                p_repository_tree_id
                                || ':' ||
                                p_pinned_to_base_diff_sha256
                                || ':' ||
                                p_base_to_final_diff_sha256
                                || ':' ||
                                p_source_manifest_sha256
                                || ':' ||
                                p_test_evidence_manifest_sha256
                                || ':' ||
                                p_evidence_sha256
                                || ':' ||
                                canonical_evidence_db_sha256_value,
                                'UTF8'),
                            'sha256'),
                        'hex'),
                    32);

            SELECT authorization_row.*
            INTO existing
            FROM snapshot_generation_restore_tool_authorizations
                authorization_row
            WHERE authorization_row.authorization_id =
                    authorization_id_value;
            IF FOUND THEN
                IF existing.drop_operation_id =
                        p_drop_operation_id
                   AND existing.drop_plan_digest =
                        p_drop_plan_digest
                   AND existing.original_bundle_manifest_sha256 =
                        p_original_bundle_manifest_sha256
                   AND existing.pinned_restore_tool_sha256 =
                        p_pinned_restore_tool_sha256
                   AND existing.validator_base_tool_sha256 =
                        p_validator_base_tool_sha256
                   AND existing.authorized_restore_tool_sha256 =
                        p_authorized_restore_tool_sha256
                   AND existing.authorized_archive_helper_sha256 =
                        p_authorized_archive_helper_sha256
                   AND existing.authorizer_binary_sha256 =
                        p_authorizer_binary_sha256
                   AND existing.repair_package_manifest_sha256 =
                        p_repair_package_manifest_sha256
                   AND existing.repository_commit =
                        p_repository_commit
                   AND existing.repository_tree_id =
                        p_repository_tree_id
                   AND existing.pinned_to_base_diff_sha256 =
                        p_pinned_to_base_diff_sha256
                   AND existing.base_to_final_diff_sha256 =
                        p_base_to_final_diff_sha256
                   AND existing.source_manifest_sha256 =
                        p_source_manifest_sha256
                   AND existing.test_evidence_manifest_sha256 =
                        p_test_evidence_manifest_sha256
                   AND existing.reason_code =
                        p_reason_code
                   AND existing.reason_text =
                        p_reason_text
                   AND existing.approved_by =
                        p_approved_by
                   AND existing.reviewed_by =
                        p_reviewed_by
                   AND existing.approval_reference =
                        p_approval_reference
                   AND existing.canonical_evidence =
                        p_canonical_evidence
                   AND existing.evidence_sha256 =
                        p_evidence_sha256
                   AND existing.canonical_evidence_db_sha256 =
                        canonical_evidence_db_sha256_value
                THEN
                    RETURN authorization_id_value;
                END IF;
                RAISE EXCEPTION
                    'Snapshot-generation restore-tool authorization identity conflicts with existing evidence.'
                    USING ERRCODE = '55000';
            END IF;

            IF EXISTS (
                SELECT 1
                FROM snapshot_generation_restore_tool_authorizations
                    authorization_row
                WHERE authorization_row.drop_operation_id =
                        p_drop_operation_id
                  AND authorization_row.authorized_restore_tool_sha256 =
                        p_authorized_restore_tool_sha256)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore tool already has conflicting authorization evidence.'
                    USING ERRCODE = '55000';
            END IF;

            INSERT INTO
                snapshot_generation_restore_tool_authorizations (
                    authorization_id,
                    schema_version,
                    tool_id,
                    drop_operation_id,
                    drop_plan_digest,
                    original_bundle_manifest_sha256,
                    pinned_restore_tool_sha256,
                    validator_base_tool_sha256,
                    authorized_restore_tool_sha256,
                    authorized_archive_helper_sha256,
                    authorizer_binary_sha256,
                    repair_package_manifest_sha256,
                    repository_commit,
                    repository_tree_id,
                    pinned_to_base_diff_sha256,
                    base_to_final_diff_sha256,
                    source_manifest_sha256,
                    test_evidence_manifest_sha256,
                    reason_code,
                    reason_text,
                    approved_by,
                    reviewed_by,
                    approval_reference,
                    canonical_evidence,
                    evidence_sha256,
                    canonical_evidence_db_sha256,
                    backend_pid,
                    transaction_id)
            VALUES (
                authorization_id_value,
                1,
                'fst.snapshot-generation-restore-tool-authorization.v1',
                p_drop_operation_id,
                p_drop_plan_digest,
                p_original_bundle_manifest_sha256,
                p_pinned_restore_tool_sha256,
                p_validator_base_tool_sha256,
                p_authorized_restore_tool_sha256,
                p_authorized_archive_helper_sha256,
                p_authorizer_binary_sha256,
                p_repair_package_manifest_sha256,
                p_repository_commit,
                p_repository_tree_id,
                p_pinned_to_base_diff_sha256,
                p_base_to_final_diff_sha256,
                p_source_manifest_sha256,
                p_test_evidence_manifest_sha256,
                p_reason_code,
                p_reason_text,
                p_approved_by,
                p_reviewed_by,
                p_approval_reference,
                p_canonical_evidence,
                p_evidence_sha256,
                canonical_evidence_db_sha256_value,
                pg_backend_pid(),
                pg_current_xact_id()::TEXT);
            RETURN authorization_id_value;
        END
        $restore_tool_authorize$;

        CREATE OR REPLACE FUNCTION
            fst_confirm_snapshot_generation_restore_tool_authorization(
                p_drop_operation_id TEXT,
                p_authorization_id TEXT)
        RETURNS JSONB
        LANGUAGE sql
        SECURITY INVOKER
        STABLE
        SET search_path = pg_catalog, public
        AS $restore_tool_confirm$
            SELECT to_jsonb(authorization_row)
            FROM snapshot_generation_restore_tool_authorizations
                authorization_row
            WHERE authorization_row.drop_operation_id =
                    p_drop_operation_id
              AND authorization_row.authorization_id =
                    p_authorization_id
        $restore_tool_confirm$;

        CREATE OR REPLACE FUNCTION
            fst_authorize_snapshot_generation_restore_continuation(
                p_restore_operation_id TEXT,
                p_drop_operation_id TEXT,
                p_predecessor_authorization_id TEXT,
                p_restore_plan_digest TEXT,
                p_restore_plan_file_sha256 TEXT,
                p_restore_report_sha256 TEXT,
                p_predecessor_restore_tool_sha256 TEXT,
                p_predecessor_repair_package_manifest_sha256 TEXT,
                p_recovery_bundle_manifest_sha256 TEXT,
                p_authorized_continuation_tool_sha256 TEXT,
                p_authorized_evidence_assembly_sha256 TEXT,
                p_route_parity_reference_source_sha256 TEXT,
                p_authorizer_binary_sha256 TEXT,
                p_continuation_package_manifest_sha256 TEXT,
                p_route_parity_preflight_sha256 TEXT,
                p_baseline_route_manifest_sha256 TEXT,
                p_baseline_route_checksums_sha256 TEXT,
                p_candidate_route_manifest_sha256 TEXT,
                p_candidate_route_checksums_sha256 TEXT,
                p_publication_id BIGINT,
                p_published_scrape_id BIGINT,
                p_repository_commit TEXT,
                p_repository_tree_id TEXT,
                p_predecessor_to_continuation_diff_sha256 TEXT,
                p_source_manifest_sha256 TEXT,
                p_test_evidence_manifest_sha256 TEXT,
                p_reason_code TEXT,
                p_reason_text TEXT,
                p_approved_by TEXT,
                p_reviewed_by TEXT,
                p_approval_reference TEXT,
                p_canonical_evidence JSONB,
                p_evidence_sha256 TEXT)
        RETURNS TEXT
        LANGUAGE plpgsql
        SECURITY INVOKER
        SET search_path = pg_catalog, public
        AS $restore_continuation_authorize$
        DECLARE
            restore_row
                snapshot_generation_restore_operations%ROWTYPE;
            drop_row
                snapshot_generation_drop_operations%ROWTYPE;
            predecessor_row
                snapshot_generation_restore_tool_authorizations%ROWTYPE;
            existing
                snapshot_generation_restore_continuation_authorizations%ROWTYPE;
            state_row scrape_publication_state%ROWTYPE;
            continuation_authorization_id_value TEXT;
            canonical_evidence_db_sha256_value TEXT;
            latest_drop_candidate_sha256 TEXT;
            restored_identity_count INTEGER;
            attached_index_count INTEGER;
            restored_row_count BIGINT;
            default_row_count BIGINT;
            current_index_inventory JSONB;
        BEGIN
            PERFORM set_config('lock_timeout', '5s', TRUE);
            PERFORM set_config(
                'statement_timeout',
                '30s',
                TRUE);
            PERFORM set_config(
                'idle_in_transaction_session_timeout',
                '60s',
                TRUE);
            PERFORM set_config(
                'transaction_timeout',
                '60s',
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
               OR NOT pg_try_advisory_xact_lock(2026083002)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore continuation authorization lock chain is busy.'
                    USING ERRCODE = '55P03';
            END IF;

            IF p_restore_operation_id
                    !~ '^[0-9a-f]{32}$'
               OR p_drop_operation_id
                    !~ '^[0-9a-f]{32}$'
               OR p_predecessor_authorization_id
                    !~ '^[0-9a-f]{32}$'
               OR p_restore_plan_digest
                    !~ '^[0-9a-f]{64}$'
               OR p_restore_plan_file_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_restore_report_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_predecessor_restore_tool_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_predecessor_repair_package_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_recovery_bundle_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_authorized_continuation_tool_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_authorized_evidence_assembly_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_route_parity_reference_source_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_authorizer_binary_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_continuation_package_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_route_parity_preflight_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_baseline_route_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_baseline_route_checksums_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_candidate_route_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_candidate_route_checksums_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_publication_id <= 0
               OR p_published_scrape_id <= 0
               OR p_repository_commit
                    !~ '^[0-9a-f]{40}$'
               OR p_repository_tree_id
                    !~ '^[0-9a-f]{40}$'
               OR p_predecessor_to_continuation_diff_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_source_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_test_evidence_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_evidence_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_reason_code
                    !~ '^[a-z0-9_]+$'
               OR COALESCE(p_reason_text, '') = ''
               OR COALESCE(p_approved_by, '') = ''
               OR COALESCE(p_reviewed_by, '') = ''
               OR p_approved_by = p_reviewed_by
               OR COALESCE(p_approval_reference, '') = ''
               OR jsonb_typeof(p_canonical_evidence) <>
                    'object'
               OR p_authorized_continuation_tool_sha256 =
                    p_predecessor_restore_tool_sha256
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore continuation authorization arguments are invalid.'
                    USING ERRCODE = '22023';
            END IF;

            SELECT operation.*
            INTO STRICT restore_row
            FROM snapshot_generation_restore_operations operation
            WHERE operation.restore_operation_id =
                    p_restore_operation_id
              AND operation.drop_operation_id =
                    p_drop_operation_id
              AND operation.plan_digest =
                    p_restore_plan_digest;

            SELECT operation.*
            INTO STRICT drop_row
            FROM snapshot_generation_drop_operations operation
            WHERE operation.drop_operation_id =
                    restore_row.drop_operation_id;

            SELECT authorization_row.*
            INTO STRICT predecessor_row
            FROM snapshot_generation_restore_tool_authorizations
                authorization_row
            WHERE authorization_row.drop_operation_id =
                    restore_row.drop_operation_id
              AND authorization_row.authorization_id =
                    p_predecessor_authorization_id;

            IF restore_row.authorization_id IS DISTINCT FROM
                    p_predecessor_authorization_id
               OR restore_row.executing_tool_sha256 <>
                    p_predecessor_restore_tool_sha256
               OR restore_row.recovery_bundle_manifest_sha256 <>
                    p_recovery_bundle_manifest_sha256
               OR predecessor_row.authorized_restore_tool_sha256 <>
                    restore_row.executing_tool_sha256
               OR predecessor_row.repair_package_manifest_sha256 <>
                    p_predecessor_repair_package_manifest_sha256
               OR predecessor_row.original_bundle_manifest_sha256 <>
                    restore_row.recovery_bundle_manifest_sha256
               OR p_approved_by IN (
                    restore_row.restored_by,
                    drop_row.approved_by,
                    predecessor_row.approved_by,
                    predecessor_row.reviewed_by)
               OR p_reviewed_by IN (
                    restore_row.restored_by,
                    drop_row.approved_by,
                    predecessor_row.approved_by,
                    predecessor_row.reviewed_by)
               OR p_approval_reference IN (
                    restore_row.restore_reference,
                    drop_row.approval_reference,
                    predecessor_row.approval_reference)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore continuation authorization differs from immutable predecessor evidence.'
                    USING ERRCODE = '55000';
            END IF;

            IF EXISTS (
                    SELECT 1
                    FROM snapshot_generation_restore_attestations
                    WHERE restore_operation_id =
                            restore_row.restore_operation_id)
               OR EXISTS (
                    SELECT 1
                    FROM snapshot_generation_restore_finalizations
                    WHERE restore_operation_id =
                            restore_row.restore_operation_id)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore continuation authorization state is already consumed.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT COUNT(*)::INTEGER
            INTO restored_identity_count
            FROM pg_class child
            JOIN pg_namespace namespace
              ON namespace.oid = child.relnamespace
            JOIN pg_inherits inheritance
              ON inheritance.inhrelid = child.oid
            WHERE child.oid =
                    restore_row.restored_child_oid
              AND child.relfilenode::BIGINT =
                    restore_row.restored_child_relfilenode
              AND namespace.nspname =
                    restore_row.child_schema
              AND child.relname =
                    restore_row.child_relation
              AND inheritance.inhparent =
                    restore_row.root_oid
              AND pg_get_expr(
                    child.relpartbound,
                    child.oid,
                    TRUE) =
                    restore_row.partition_bound;

            SELECT COUNT(*)::INTEGER
            INTO attached_index_count
            FROM pg_index child_index
            JOIN pg_inherits child_index_inheritance
              ON child_index_inheritance.inhrelid =
                    child_index.indexrelid
            JOIN pg_index root_index
              ON root_index.indexrelid =
                    child_index_inheritance.inhparent
             AND root_index.indrelid = restore_row.root_oid
            JOIN pg_inherits root_index_inheritance
              ON root_index_inheritance.inhrelid =
                    root_index.indexrelid
            JOIN pg_class top_index_relation
              ON top_index_relation.oid =
                    root_index_inheritance.inhparent
            WHERE child_index.indrelid =
                    restore_row.restored_child_oid
              AND child_index.indisvalid
              AND child_index.indisready
              AND root_index.indisvalid
              AND root_index.indisready
              AND top_index_relation.relname IN (
                    'leaderboard_entries_snapshot_pkey',
                    'ix_les_snapshot_song_score');

            current_index_inventory :=
                fst_snapshot_generation_index_inventory(
                    restore_row.restored_child_oid,
                    restore_row.root_oid,
                    TRUE);

            EXECUTE format(
                'SELECT COUNT(*)::BIGINT FROM ONLY %I.%I',
                restore_row.child_schema,
                restore_row.child_relation)
            INTO restored_row_count;

            EXECUTE format(
                'SELECT COUNT(*)::BIGINT FROM ONLY %I.%I',
                drop_row.default_partition_schema,
                drop_row.default_partition_relation)
            INTO default_row_count;

            SELECT attestation.candidate_route_manifest_sha256
            INTO latest_drop_candidate_sha256
            FROM snapshot_generation_drop_attestations
                attestation
            WHERE attestation.drop_operation_id =
                    restore_row.drop_operation_id
              AND attestation.stage IN (
                    'pre_drop',
                    'dropped',
                    'post_publication')
            ORDER BY attestation.attestation_id DESC
            LIMIT 1;

            SELECT state.*
            INTO STRICT state_row
            FROM scrape_publication_state state
            WHERE state.id = TRUE;

            IF restored_identity_count <> 1
               OR attached_index_count <> 2
               OR current_index_inventory
                    #>> '{pk,indexOid}' <>
                    restore_row.restored_index_evidence
                        #>> '{pk,indexOid}'
               OR current_index_inventory
                    #>> '{pk,indexRelfilenode}' <>
                    restore_row.restored_index_evidence
                        #>> '{pk,indexRelfilenode}'
               OR current_index_inventory
                    #>> '{pk,indexName}' <>
                    restore_row.restored_index_evidence
                        #>> '{pk,indexName}'
               OR current_index_inventory
                    #>> '{score,indexOid}' <>
                    restore_row.restored_index_evidence
                        #>> '{score,indexOid}'
               OR current_index_inventory
                    #>> '{score,indexRelfilenode}' <>
                    restore_row.restored_index_evidence
                        #>> '{score,indexRelfilenode}'
               OR current_index_inventory
                    #>> '{score,indexName}' <>
                    restore_row.restored_index_evidence
                        #>> '{score,indexName}'
               OR restored_row_count <>
                    restore_row.row_count
               OR to_regclass(
                    format(
                        '%I.%I',
                        drop_row.quarantine_schema,
                        drop_row.quarantine_relation)) IS NOT NULL
               OR EXISTS (
                    SELECT 1
                    FROM pg_class relation
                    WHERE relation.oid =
                            drop_row.child_oid)
               OR NOT EXISTS (
                    SELECT 1
                    FROM snapshot_generation_retention_holds
                        hold_row
                    WHERE hold_row.hold_id =
                            restore_row.hold_id
                      AND hold_row.released_at IS NULL)
               OR NOT EXISTS (
                    SELECT 1
                    FROM pg_trigger trigger_row
                    WHERE trigger_row.tgrelid =
                            restore_row.restored_child_oid
                      AND trigger_row.tgname =
                            'trg_sgr_' ||
                            restore_row.snapshot_id::TEXT ||
                            '_' ||
                            left(
                                restore_row.restore_operation_id,
                                12)
                      AND NOT trigger_row.tgisinternal
                      AND trigger_row.tgenabled = 'O'
                      AND trigger_row.tgfoid =
                            'public.fst_reject_snapshot_generation_quarantine_relation_mutation()'
                                ::regprocedure)
               OR EXISTS (
                    SELECT 1
                    FROM pg_constraint constraint_row
                    WHERE constraint_row.conrelid =
                            drop_row.default_partition_oid
                      AND constraint_row.conname =
                            drop_row.durable_default_exclusion_constraint)
               OR default_row_count <> 0
               OR latest_drop_candidate_sha256 IS DISTINCT FROM
                    p_baseline_route_manifest_sha256
               OR state_row.current_publication_id IS DISTINCT FROM
                    p_publication_id
               OR state_row.published_scrape_id IS DISTINCT FROM
                    p_published_scrape_id
               OR state_row.public_reads_frozen
               OR state_row.working_publication_id IS NOT NULL
               OR state_row.publication_commit_intent_started_at
                    IS NOT NULL
               OR state_row.max_score_mutation_gate_token
                    IS NOT NULL
               OR EXISTS (
                    SELECT 1
                    FROM scrape_log scrape
                    WHERE scrape.status = 'running')
               OR NOT EXISTS (
                    SELECT 1
                    FROM service_worker_status worker
                    WHERE worker.worker_key = 'scraper'
                      AND worker.status = 'offline'
                      AND worker.current_operation_json
                            IS NULL)
               OR EXISTS (
                    SELECT 1
                    FROM scrape_writer_failures failure
                    WHERE failure.instrument =
                            restore_row.instrument
                      AND failure.scrape_id =
                            restore_row.snapshot_id
                      AND failure.replayed_at IS NULL)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore continuation authorization state is unsafe.'
                    USING ERRCODE = '55000';
            END IF;

            canonical_evidence_db_sha256_value :=
                encode(
                    digest(
                        convert_to(
                            p_canonical_evidence::TEXT,
                            'UTF8'),
                        'sha256'),
                    'hex');
            continuation_authorization_id_value :=
                left(
                    encode(
                        digest(
                            convert_to(
                                'fst.snapshot-generation-restore-continuation-authorization.v1'
                                || ':confirm_attest_finalize'
                                || ':' ||
                                p_restore_operation_id
                                || ':' ||
                                p_drop_operation_id
                                || ':' ||
                                p_predecessor_authorization_id
                                || ':' ||
                                p_restore_plan_digest
                                || ':' ||
                                p_restore_plan_file_sha256
                                || ':' ||
                                p_restore_report_sha256
                                || ':' ||
                                p_predecessor_restore_tool_sha256
                                || ':' ||
                                p_predecessor_repair_package_manifest_sha256
                                || ':' ||
                                p_recovery_bundle_manifest_sha256
                                || ':' ||
                                p_authorized_continuation_tool_sha256
                                || ':' ||
                                p_authorized_evidence_assembly_sha256
                                || ':' ||
                                p_route_parity_reference_source_sha256
                                || ':' ||
                                p_authorizer_binary_sha256
                                || ':' ||
                                p_continuation_package_manifest_sha256
                                || ':fst.route-parity.canonical-zip.v1'
                                || ':' ||
                                p_route_parity_preflight_sha256
                                || ':' ||
                                p_baseline_route_manifest_sha256
                                || ':' ||
                                p_baseline_route_checksums_sha256
                                || ':' ||
                                p_candidate_route_manifest_sha256
                                || ':' ||
                                p_candidate_route_checksums_sha256
                                || ':' ||
                                p_publication_id::TEXT
                                || ':' ||
                                p_published_scrape_id::TEXT
                                || ':' ||
                                p_repository_commit
                                || ':' ||
                                p_repository_tree_id
                                || ':' ||
                                p_predecessor_to_continuation_diff_sha256
                                || ':' ||
                                p_source_manifest_sha256
                                || ':' ||
                                p_test_evidence_manifest_sha256
                                || ':' ||
                                p_evidence_sha256
                                || ':' ||
                                canonical_evidence_db_sha256_value,
                                'UTF8'),
                            'sha256'),
                        'hex'),
                    32);

            SELECT authorization_row.*
            INTO existing
            FROM
                snapshot_generation_restore_continuation_authorizations
                    authorization_row
            WHERE authorization_row.continuation_authorization_id =
                    continuation_authorization_id_value;
            IF FOUND THEN
                IF existing.restore_operation_id =
                        p_restore_operation_id
                   AND existing.drop_operation_id =
                        p_drop_operation_id
                   AND existing.predecessor_authorization_id =
                        p_predecessor_authorization_id
                   AND existing.restore_plan_digest =
                        p_restore_plan_digest
                   AND existing.restore_plan_file_sha256 =
                        p_restore_plan_file_sha256
                   AND existing.restore_report_sha256 =
                        p_restore_report_sha256
                   AND existing.predecessor_restore_tool_sha256 =
                        p_predecessor_restore_tool_sha256
                   AND existing.predecessor_repair_package_manifest_sha256 =
                        p_predecessor_repair_package_manifest_sha256
                   AND existing.recovery_bundle_manifest_sha256 =
                        p_recovery_bundle_manifest_sha256
                   AND existing.authorized_continuation_tool_sha256 =
                        p_authorized_continuation_tool_sha256
                   AND existing.authorized_evidence_assembly_sha256 =
                        p_authorized_evidence_assembly_sha256
                   AND existing.route_parity_reference_source_sha256 =
                        p_route_parity_reference_source_sha256
                   AND existing.authorizer_binary_sha256 =
                        p_authorizer_binary_sha256
                   AND existing.continuation_package_manifest_sha256 =
                        p_continuation_package_manifest_sha256
                   AND existing.route_parity_preflight_sha256 =
                        p_route_parity_preflight_sha256
                   AND existing.baseline_route_manifest_sha256 =
                        p_baseline_route_manifest_sha256
                   AND existing.baseline_route_checksums_sha256 =
                        p_baseline_route_checksums_sha256
                   AND existing.candidate_route_manifest_sha256 =
                        p_candidate_route_manifest_sha256
                   AND existing.candidate_route_checksums_sha256 =
                        p_candidate_route_checksums_sha256
                   AND existing.publication_id =
                        p_publication_id
                   AND existing.published_scrape_id =
                        p_published_scrape_id
                   AND existing.repository_commit =
                        p_repository_commit
                   AND existing.repository_tree_id =
                        p_repository_tree_id
                   AND existing.predecessor_to_continuation_diff_sha256 =
                        p_predecessor_to_continuation_diff_sha256
                   AND existing.source_manifest_sha256 =
                        p_source_manifest_sha256
                   AND existing.test_evidence_manifest_sha256 =
                        p_test_evidence_manifest_sha256
                   AND existing.reason_code =
                        p_reason_code
                   AND existing.reason_text =
                        p_reason_text
                   AND existing.approved_by =
                        p_approved_by
                   AND existing.reviewed_by =
                        p_reviewed_by
                   AND existing.approval_reference =
                        p_approval_reference
                   AND existing.canonical_evidence =
                        p_canonical_evidence
                   AND existing.evidence_sha256 =
                        p_evidence_sha256
                   AND existing.canonical_evidence_db_sha256 =
                        canonical_evidence_db_sha256_value
                   AND existing.database_user =
                        current_user
                THEN
                    RETURN
                        continuation_authorization_id_value;
                END IF;
                RAISE EXCEPTION
                    'Snapshot-generation restore continuation authorization identity conflicts with existing evidence.'
                    USING ERRCODE = '55000';
            END IF;

            IF EXISTS (
                SELECT 1
                FROM
                    snapshot_generation_restore_continuation_authorizations
                        authorization_row
                WHERE authorization_row.restore_operation_id =
                        p_restore_operation_id
                  AND authorization_row.authorized_continuation_tool_sha256 =
                        p_authorized_continuation_tool_sha256)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore continuation tool already has conflicting authorization evidence.'
                    USING ERRCODE = '55000';
            END IF;

            INSERT INTO
                snapshot_generation_restore_continuation_authorizations (
                    continuation_authorization_id,
                    schema_version,
                    tool_id,
                    authorization_scope,
                    restore_operation_id,
                    drop_operation_id,
                    predecessor_authorization_id,
                    restore_plan_digest,
                    restore_plan_file_sha256,
                    restore_report_sha256,
                    predecessor_restore_tool_sha256,
                    predecessor_repair_package_manifest_sha256,
                    recovery_bundle_manifest_sha256,
                    authorized_continuation_tool_sha256,
                    authorized_evidence_assembly_sha256,
                    route_parity_reference_source_sha256,
                    authorizer_binary_sha256,
                    continuation_package_manifest_sha256,
                    route_parity_algorithm_id,
                    route_parity_preflight_sha256,
                    baseline_route_manifest_sha256,
                    baseline_route_checksums_sha256,
                    candidate_route_manifest_sha256,
                    candidate_route_checksums_sha256,
                    publication_id,
                    published_scrape_id,
                    repository_commit,
                    repository_tree_id,
                    predecessor_to_continuation_diff_sha256,
                    source_manifest_sha256,
                    test_evidence_manifest_sha256,
                    reason_code,
                    reason_text,
                    approved_by,
                    reviewed_by,
                    approval_reference,
                    canonical_evidence,
                    evidence_sha256,
                    canonical_evidence_db_sha256,
                    database_user,
                    backend_pid,
                    transaction_id)
            VALUES (
                continuation_authorization_id_value,
                1,
                'fst.snapshot-generation-restore-continuation-authorization.v1',
                'confirm_attest_finalize',
                p_restore_operation_id,
                p_drop_operation_id,
                p_predecessor_authorization_id,
                p_restore_plan_digest,
                p_restore_plan_file_sha256,
                p_restore_report_sha256,
                p_predecessor_restore_tool_sha256,
                p_predecessor_repair_package_manifest_sha256,
                p_recovery_bundle_manifest_sha256,
                p_authorized_continuation_tool_sha256,
                p_authorized_evidence_assembly_sha256,
                p_route_parity_reference_source_sha256,
                p_authorizer_binary_sha256,
                p_continuation_package_manifest_sha256,
                'fst.route-parity.canonical-zip.v1',
                p_route_parity_preflight_sha256,
                p_baseline_route_manifest_sha256,
                p_baseline_route_checksums_sha256,
                p_candidate_route_manifest_sha256,
                p_candidate_route_checksums_sha256,
                p_publication_id,
                p_published_scrape_id,
                p_repository_commit,
                p_repository_tree_id,
                p_predecessor_to_continuation_diff_sha256,
                p_source_manifest_sha256,
                p_test_evidence_manifest_sha256,
                p_reason_code,
                p_reason_text,
                p_approved_by,
                p_reviewed_by,
                p_approval_reference,
                p_canonical_evidence,
                p_evidence_sha256,
                canonical_evidence_db_sha256_value,
                current_user,
                pg_backend_pid(),
                pg_current_xact_id()::TEXT);
            RETURN continuation_authorization_id_value;
        END
        $restore_continuation_authorize$;

        CREATE OR REPLACE FUNCTION
            fst_confirm_snapshot_generation_restore_continuation_authorization(
                p_restore_operation_id TEXT,
                p_continuation_authorization_id TEXT)
        RETURNS JSONB
        LANGUAGE sql
        SECURITY INVOKER
        STABLE
        SET search_path = pg_catalog, public
        AS $restore_continuation_confirm$
            SELECT to_jsonb(authorization_row)
            FROM
                snapshot_generation_restore_continuation_authorizations
                    authorization_row
            WHERE authorization_row.restore_operation_id =
                    p_restore_operation_id
              AND authorization_row.continuation_authorization_id =
                    p_continuation_authorization_id
        $restore_continuation_confirm$;

        CREATE OR REPLACE FUNCTION
            fst_lock_snapshot_generation_for_drop(
                p_active_quarantine_operation_id TEXT,
                p_expected_child_oid BIGINT,
                p_expected_child_relfilenode BIGINT)
        RETURNS TEXT
        LANGUAGE plpgsql
        SECURITY INVOKER
        SET search_path = pg_catalog, public
        AS $drop_lock$
        DECLARE
            operation_row
                snapshot_generation_quarantine_operations%ROWTYPE;
            current_oid BIGINT;
            current_relfilenode BIGINT;
            current_parent_count INTEGER;
            expected_root_relation TEXT;
            expected_child_relation TEXT;
            expected_quarantine_relation TEXT;
            expected_default_relation TEXT;
            instrument_key TEXT;
        BEGIN
            PERFORM set_config('lock_timeout', '5s', TRUE);
            PERFORM set_config('statement_timeout', '180s', TRUE);
            PERFORM set_config(
                'idle_in_transaction_session_timeout',
                '240s',
                TRUE);
            PERFORM set_config(
                'transaction_timeout',
                '240s',
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
               OR NOT pg_try_advisory_xact_lock(2026083002)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation drop lock chain is busy; retry in a new transaction.'
                    USING ERRCODE = '55P03';
            END IF;

            SELECT operation.*
            INTO STRICT operation_row
            FROM snapshot_generation_quarantine_operations operation
            WHERE operation.operation_id =
                    p_active_quarantine_operation_id;

            expected_root_relation := CASE operation_row.instrument
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
            instrument_key := CASE operation_row.instrument
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
            expected_child_relation :=
                expected_root_relation || '_s' ||
                operation_row.snapshot_id::TEXT;
            expected_quarantine_relation :=
                'sgq_' || instrument_key || '_' ||
                operation_row.snapshot_id::TEXT || '_' ||
                left(operation_row.operation_id, 12);
            expected_default_relation :=
                expected_root_relation || '_default';

            IF EXISTS (
                    SELECT 1
                    FROM snapshot_generation_quarantine_reattachments
                        reattach
                    WHERE reattach.operation_id =
                            operation_row.operation_id)
               OR EXISTS (
                    SELECT 1
                    FROM snapshot_generation_drop_operations drop_row
                    WHERE drop_row.active_quarantine_operation_id =
                            operation_row.operation_id)
               OR operation_row.child_oid <> p_expected_child_oid
               OR operation_row.child_relfilenode <>
                    p_expected_child_relfilenode
               OR operation_row.instrument = 'Solo_Bass'
                    AND operation_row.snapshot_id = 1308
               OR operation_row.root_schema <> 'public'
               OR operation_row.root_relation <>
                    expected_root_relation
               OR operation_row.child_schema <> 'public'
               OR operation_row.child_relation <>
                    expected_child_relation
               OR operation_row.quarantine_schema <>
                    'fst_snapshot_quarantine'
               OR operation_row.quarantine_relation <>
                    expected_quarantine_relation
               OR operation_row.default_partition_schema <>
                    'public'
               OR operation_row.default_partition_relation <>
                    expected_default_relation
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation drop target is not the exact active quarantine operation.'
                    USING ERRCODE = '55000';
            END IF;

            EXECUTE format(
                'LOCK TABLE ONLY %I.%I IN SHARE MODE',
                operation_row.default_partition_schema,
                operation_row.default_partition_relation);
            EXECUTE format(
                'LOCK TABLE ONLY %I.%I IN ACCESS EXCLUSIVE MODE',
                operation_row.quarantine_schema,
                operation_row.quarantine_relation);

            SELECT
                relation.oid::BIGINT,
                relation.relfilenode::BIGINT,
                (
                    SELECT COUNT(*)::INTEGER
                    FROM pg_inherits inheritance
                    WHERE inheritance.inhrelid =
                            relation.oid)
            INTO STRICT
                current_oid,
                current_relfilenode,
                current_parent_count
            FROM pg_class relation
            JOIN pg_namespace namespace
              ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname =
                    operation_row.quarantine_schema
              AND relation.relname =
                    operation_row.quarantine_relation
              AND relation.relkind = 'r';

            IF current_oid <> operation_row.child_oid
               OR current_relfilenode <>
                    operation_row.child_relfilenode
               OR current_parent_count <> 0
               OR to_regclass(
                    format(
                        '%I.%I',
                        operation_row.child_schema,
                        operation_row.child_relation)) IS NOT NULL
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation private relation identity changed before drop.'
                    USING ERRCODE = '55000';
            END IF;

            PERFORM set_config(
                'fst.snapshot_generation_drop_locked_operation',
                operation_row.operation_id,
                TRUE);
            RETURN operation_row.quarantine_relation;
        END
        $drop_lock$;

        CREATE OR REPLACE FUNCTION
            fst_drop_quarantined_snapshot_generation(
                p_drop_operation_id TEXT,
                p_plan_digest TEXT,
                p_rehearsal_operation_id TEXT,
                p_active_operation_id TEXT,
                p_rehearsal_quarantined_attestation_id BIGINT,
                p_rehearsal_soak_attestation_id BIGINT,
                p_rehearsal_reattached_attestation_id BIGINT,
                p_active_quarantined_attestation_id BIGINT,
                p_active_soak_attestation_id BIGINT,
                p_archive_sha256 TEXT,
                p_fresh_archive_proof_manifest_sha256 TEXT,
                p_recovery_bundle_manifest_sha256 TEXT,
                p_semantic_projection_version INTEGER,
                p_rehearsal_catalog_sha256 TEXT,
                p_catalog_sha256 TEXT,
                p_rehearsal_semantic_catalog_sha256 TEXT,
                p_semantic_catalog_sha256 TEXT,
                p_rehearsal_logical_index_shape_sha256 TEXT,
                p_logical_index_shape_sha256 TEXT,
                p_rehearsal_physical_index_inventory_sha256 TEXT,
                p_physical_index_inventory_sha256 TEXT,
                p_pre_drop_baseline_route_manifest_sha256 TEXT,
                p_pre_drop_candidate_route_manifest_sha256 TEXT,
                p_pre_drop_route_count INTEGER,
                p_pre_drop_status_parity BOOLEAN,
                p_pre_drop_semantic_json_parity BOOLEAN,
                p_pre_drop_difference_count INTEGER,
                p_pre_drop_attestation_sha256 TEXT,
                p_health_evidence_sha256 TEXT,
                p_binary_sha256 TEXT,
                p_restore_tool_sha256 TEXT,
                p_restore_image_id_sha256 TEXT,
                p_repository_commit TEXT,
                p_dependency_inventory JSONB,
                p_dependency_inventory_sha256 TEXT,
                p_topology_evidence JSONB,
                p_topology_sha256 TEXT,
                p_liveness_evidence JSONB,
                p_liveness_sha256 TEXT,
                p_database_name TEXT,
                p_database_oid BIGINT,
                p_system_identifier TEXT,
                p_server_version_num INTEGER,
                p_health_started_at TIMESTAMPTZ,
                p_health_completed_at TIMESTAMPTZ,
                p_health_sample_count INTEGER,
                p_health_sample_interval_seconds INTEGER,
                p_proof_completed_at TIMESTAMPTZ,
                p_approved_by TEXT,
                p_approval_reference TEXT,
                p_preflight_evidence JSONB,
                p_drop_evidence_sha256 TEXT,
                p_drop_evidence JSONB)
        RETURNS TEXT
        LANGUAGE plpgsql
        SECURITY INVOKER
        SET search_path = pg_catalog, public
        AS $drop_execute$
        DECLARE
            rehearsal
                snapshot_generation_quarantine_operations%ROWTYPE;
            active
                snapshot_generation_quarantine_operations%ROWTYPE;
            cycle_row
                snapshot_generation_retention_cycles%ROWTYPE;
            state_row scrape_publication_state%ROWTYPE;
            q1_quarantined
                snapshot_generation_quarantine_attestations%ROWTYPE;
            q1_soak
                snapshot_generation_quarantine_attestations%ROWTYPE;
            q1_reattached
                snapshot_generation_quarantine_attestations%ROWTYPE;
            q2_quarantined
                snapshot_generation_quarantine_attestations%ROWTYPE;
            q2_soak
                snapshot_generation_quarantine_attestations%ROWTYPE;
            q1_reattached_at TIMESTAMPTZ;
            current_oid BIGINT;
            current_relfilenode BIGINT;
            current_row_count BIGINT;
            current_total_bytes BIGINT;
            current_parent_count INTEGER;
            exact_check_count INTEGER;
            exact_check_expression TEXT;
            mutation_guard_count INTEGER;
            child_index_count INTEGER;
            attached_child_index_count INTEGER;
            default_oid BIGINT;
            default_parent_oid BIGINT;
            default_bound TEXT;
            default_constraint_valid BOOLEAN;
            default_constraint_expression TEXT;
            default_row_count BIGINT;
            target_reference_count BIGINT;
            unexpected_dependency_count BIGINT;
            current_index_inventory JSONB;
            current_index_inventory_count INTEGER;
            recorded_index_count INTEGER;
            matched_index_count INTEGER;
            durable_constraint_name TEXT;
            current_transaction_id TEXT;
        BEGIN
            PERFORM set_config('lock_timeout', '5s', TRUE);
            PERFORM set_config('statement_timeout', '180s', TRUE);
            PERFORM set_config(
                'idle_in_transaction_session_timeout',
                '240s',
                TRUE);
            PERFORM set_config(
                'transaction_timeout',
                '240s',
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
               OR NOT pg_try_advisory_xact_lock(2026083002)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation drop lock chain is busy; retry in a new transaction.'
                    USING ERRCODE = '55P03';
            END IF;

            IF p_drop_operation_id !~ '^[0-9a-f]{32}$'
               OR p_plan_digest !~ '^[0-9a-f]{64}$'
               OR p_archive_sha256 !~ '^[0-9a-f]{64}$'
               OR p_fresh_archive_proof_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_recovery_bundle_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_semantic_projection_version <> 1
               OR p_rehearsal_catalog_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_catalog_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_rehearsal_semantic_catalog_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_semantic_catalog_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_rehearsal_logical_index_shape_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_logical_index_shape_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_rehearsal_physical_index_inventory_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_physical_index_inventory_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_pre_drop_baseline_route_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_pre_drop_candidate_route_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_pre_drop_route_count <> 55
               OR p_pre_drop_status_parity IS DISTINCT FROM TRUE
               OR p_pre_drop_semantic_json_parity
                    IS DISTINCT FROM TRUE
               OR p_pre_drop_difference_count <> 0
               OR p_pre_drop_attestation_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_health_evidence_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_binary_sha256 !~ '^[0-9a-f]{64}$'
               OR p_restore_tool_sha256 !~ '^[0-9a-f]{64}$'
               OR p_restore_image_id_sha256 !~ '^[0-9a-f]{64}$'
               OR p_repository_commit !~ '^[0-9a-f]{40}$'
               OR p_dependency_inventory_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_topology_sha256 !~ '^[0-9a-f]{64}$'
               OR p_liveness_sha256 !~ '^[0-9a-f]{64}$'
               OR p_drop_evidence_sha256 !~ '^[0-9a-f]{64}$'
               OR jsonb_typeof(p_dependency_inventory) <> 'array'
               OR jsonb_typeof(p_topology_evidence) <> 'object'
               OR jsonb_typeof(p_liveness_evidence) <> 'object'
               OR jsonb_typeof(p_preflight_evidence) <> 'object'
               OR jsonb_typeof(p_drop_evidence) <> 'object'
               OR p_database_oid <= 0
               OR p_server_version_num / 10000 <> 17
               OR p_health_sample_count < 60
               OR p_health_sample_interval_seconds <> 30
               OR p_health_completed_at - p_health_started_at
                    < interval '30 minutes'
               OR p_proof_completed_at < p_health_completed_at
               OR COALESCE(p_approved_by, '') = ''
               OR COALESCE(p_approval_reference, '') = ''
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation drop arguments are invalid.'
                    USING ERRCODE = '22023';
            END IF;
            IF current_setting(
                    'fst.snapshot_generation_drop_locked_operation',
                    TRUE)
                    IS DISTINCT FROM p_active_operation_id
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation drop target was not locked by the exact preflight function.'
                    USING ERRCODE = '55000';
            END IF;

            IF EXISTS (
                SELECT 1
                FROM snapshot_generation_drop_operations drop_row
                WHERE drop_row.drop_operation_id =
                        p_drop_operation_id
                  AND drop_row.plan_digest = p_plan_digest)
            THEN
                RETURN p_drop_operation_id;
            END IF;
            IF EXISTS (
                SELECT 1
                FROM snapshot_generation_drop_operations drop_row
                WHERE drop_row.drop_operation_id =
                        p_drop_operation_id
                   OR drop_row.plan_digest = p_plan_digest
                   OR drop_row.active_quarantine_operation_id =
                        p_active_operation_id)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation drop identity conflicts with existing evidence.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT operation.*
            INTO STRICT rehearsal
            FROM snapshot_generation_quarantine_operations operation
            WHERE operation.operation_id =
                    p_rehearsal_operation_id;
            SELECT operation.*
            INTO STRICT active
            FROM snapshot_generation_quarantine_operations operation
            WHERE operation.operation_id =
                    p_active_operation_id;

            IF rehearsal.operation_id = active.operation_id
               OR rehearsal.instrument <> active.instrument
               OR rehearsal.snapshot_id <> active.snapshot_id
               OR rehearsal.root_oid <> active.root_oid
               OR rehearsal.child_oid <> active.child_oid
               OR rehearsal.child_relfilenode <>
                    active.child_relfilenode
               OR rehearsal.row_count <> active.row_count
               OR rehearsal.row_fingerprint_sha256 <>
                    active.row_fingerprint_sha256
               OR rehearsal.stable_child_identity_hash <>
                    active.stable_child_identity_hash
               OR rehearsal.total_bytes <> active.total_bytes
               OR p_rehearsal_semantic_catalog_sha256 <>
                    p_semantic_catalog_sha256
               OR p_rehearsal_logical_index_shape_sha256 <>
                    p_logical_index_shape_sha256
               OR p_rehearsal_physical_index_inventory_sha256 <>
                    p_physical_index_inventory_sha256
               OR (
                    SELECT COUNT(*)::INTEGER
                    FROM
                        snapshot_generation_quarantine_index_renames
                            rehearsal_index
                    JOIN
                        snapshot_generation_quarantine_index_renames
                            active_index
                      ON active_index.index_role =
                            rehearsal_index.index_role
                     AND active_index.operation_id =
                            active.operation_id
                     AND active_index.index_oid =
                            rehearsal_index.index_oid
                     AND active_index.index_relfilenode =
                            rehearsal_index.index_relfilenode
                     AND active_index.semantic_before =
                            rehearsal_index.semantic_after
                    WHERE rehearsal_index.operation_id =
                            rehearsal.operation_id) <> 2
               OR active.operation_id IS DISTINCT FROM (
                    SELECT latest.operation_id
                    FROM snapshot_generation_quarantine_operations
                        latest
                    LEFT JOIN
                        snapshot_generation_quarantine_reattachments
                            latest_reattach
                      ON latest_reattach.operation_id =
                            latest.operation_id
                    WHERE latest.instrument = active.instrument
                      AND latest.snapshot_id = active.snapshot_id
                      AND latest_reattach.operation_id IS NULL
                    ORDER BY
                        latest.quarantined_at DESC,
                        latest.operation_id DESC
                    LIMIT 1)
               OR active.instrument = 'Solo_Bass'
                    AND active.snapshot_id = 1308
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation rehearsal and active quarantine identities differ.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT reattach.reattached_at
            INTO STRICT q1_reattached_at
            FROM snapshot_generation_quarantine_reattachments
                reattach
            WHERE reattach.operation_id = rehearsal.operation_id;
            IF q1_reattached_at >= active.quarantined_at
               OR EXISTS (
                    SELECT 1
                    FROM snapshot_generation_quarantine_reattachments
                        reattach
                    WHERE reattach.operation_id =
                            active.operation_id)
               OR p_approved_by IN (
                    rehearsal.approved_by,
                    active.approved_by)
               OR p_approval_reference IN (
                    rehearsal.approval_reference,
                    active.approval_reference)
               OR p_approval_reference = (
                    SELECT reattach.reattach_reference
                    FROM snapshot_generation_quarantine_reattachments
                        reattach
                    WHERE reattach.operation_id =
                            rehearsal.operation_id)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation drop approval or Q1/Q2 chronology is invalid.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT attestation.*
            INTO STRICT q1_quarantined
            FROM snapshot_generation_quarantine_attestations
                attestation
            WHERE attestation.attestation_id =
                    p_rehearsal_quarantined_attestation_id
              AND attestation.operation_id =
                    rehearsal.operation_id
              AND attestation.stage = 'quarantined';
            SELECT attestation.*
            INTO STRICT q1_soak
            FROM snapshot_generation_quarantine_attestations
                attestation
            WHERE attestation.attestation_id =
                    p_rehearsal_soak_attestation_id
              AND attestation.operation_id =
                    rehearsal.operation_id
              AND attestation.stage = 'soak';
            SELECT attestation.*
            INTO STRICT q1_reattached
            FROM snapshot_generation_quarantine_attestations
                attestation
            WHERE attestation.attestation_id =
                    p_rehearsal_reattached_attestation_id
              AND attestation.operation_id =
                    rehearsal.operation_id
              AND attestation.stage = 'reattached';
            SELECT attestation.*
            INTO STRICT q2_quarantined
            FROM snapshot_generation_quarantine_attestations
                attestation
            WHERE attestation.attestation_id =
                    p_active_quarantined_attestation_id
              AND attestation.operation_id =
                    active.operation_id
              AND attestation.stage = 'quarantined';
            SELECT attestation.*
            INTO STRICT q2_soak
            FROM snapshot_generation_quarantine_attestations
                attestation
            WHERE attestation.attestation_id =
                    p_active_soak_attestation_id
              AND attestation.operation_id =
                    active.operation_id
              AND attestation.stage = 'soak';

            IF q1_quarantined.publication_id <>
                    rehearsal.trigger_publication_id
               OR q1_quarantined.published_scrape_id <>
                    rehearsal.trigger_scrape_id
               OR q1_quarantined.attested_at <
                    rehearsal.quarantined_at
               OR q1_soak.publication_id =
                    rehearsal.trigger_publication_id
               OR q1_soak.published_scrape_id =
                    rehearsal.trigger_scrape_id
               OR q1_soak.attested_at <=
                    q1_quarantined.attested_at
               OR q1_soak.attested_at >=
                    q1_reattached_at
               OR q1_reattached.publication_id <>
                    q1_soak.publication_id
               OR q1_reattached.published_scrape_id <>
                    q1_soak.published_scrape_id
               OR q1_reattached.attested_at < q1_reattached_at
               OR q1_reattached.attested_at >=
                    active.quarantined_at
               OR NOT EXISTS (
                    SELECT 1
                    FROM publication_generations generation
                    JOIN scrape_log scrape
                      ON scrape.id =
                            q1_soak.published_scrape_id
                    WHERE generation.publication_id =
                            q1_soak.publication_id
                      AND generation.scrape_id =
                            q1_soak.published_scrape_id
                      AND generation.status IN (
                            'current',
                            'retained')
                      AND scrape.status = 'completed'
                      AND scrape.completed_at IS NOT NULL
                      AND scrape.failed_at IS NULL)
               OR q2_quarantined.publication_id <>
                    active.trigger_publication_id
               OR q2_quarantined.published_scrape_id <>
                    active.trigger_scrape_id
               OR q2_quarantined.attested_at <
                    active.quarantined_at
               OR q2_soak.publication_id <>
                    active.trigger_publication_id
               OR q2_soak.published_scrape_id <>
                    active.trigger_scrape_id
               OR q2_soak.attested_at <=
                    q2_quarantined.attested_at
               OR q2_soak.attested_at - active.quarantined_at
                    < interval '30 minutes'
               OR p_health_started_at <
                    active.quarantined_at
               OR p_health_completed_at >
                    q2_soak.attested_at
               OR p_proof_completed_at <
                    q2_soak.attested_at
               OR NOT q1_quarantined.status_parity
               OR NOT q1_quarantined.semantic_json_parity
               OR q1_quarantined.difference_count <> 0
               OR NOT q1_soak.status_parity
               OR NOT q1_soak.semantic_json_parity
               OR q1_soak.difference_count <> 0
               OR NOT q1_reattached.status_parity
               OR NOT q1_reattached.semantic_json_parity
               OR q1_reattached.difference_count <> 0
               OR NOT q2_quarantined.status_parity
               OR NOT q2_quarantined.semantic_json_parity
               OR q2_quarantined.difference_count <> 0
               OR NOT q2_soak.status_parity
               OR NOT q2_soak.semantic_json_parity
               OR q2_soak.difference_count <> 0
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation Q1 rotation or Q2 soak evidence is incomplete.'
                    USING ERRCODE = '55000';
            END IF;

            -- The five-cycle gate is preserved transitively: Q2 could only
            -- commit through the quarantine function after five accepted
            -- cycles, quarantine/cycle/observation evidence is immutable, and
            -- the checks below reject any cycle or publication advancement.
            SELECT cycle.*
            INTO STRICT cycle_row
            FROM snapshot_generation_retention_cycles cycle
            WHERE cycle.cycle_id = active.cycle_id;
            IF cycle_row.cycle_id IS DISTINCT FROM (
                    SELECT latest.cycle_id
                    FROM snapshot_generation_retention_cycles latest
                    ORDER BY latest.created_at DESC, latest.cycle_id DESC
                    LIMIT 1)
               OR cycle_row.status <> 'observed'
               OR NOT cycle_row.report_only
               OR NOT cycle_row.oracle_agreement
               OR cycle_row.blocked_count <> 0
               OR cycle_row.global_blockers <> '[]'::jsonb
               OR cycle_row.planner_child_set
                    IS DISTINCT FROM cycle_row.oracle_child_set
               OR cycle_row.planner_live_set
                    IS DISTINCT FROM cycle_row.oracle_live_set
               OR cycle_row.planner_candidate_set
                    IS DISTINCT FROM cycle_row.oracle_candidate_set
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation Q2 cycle is no longer the exact latest accepted cycle.'
                    USING ERRCODE = '55000';
            END IF;
            IF NOT EXISTS (
                SELECT 1
                FROM snapshot_generation_retention_observations
                    observation
                WHERE observation.observation_id =
                        active.observation_id
                  AND observation.cycle_id = active.cycle_id
                  AND observation.report_only
                  AND observation.instrument =
                        active.instrument
                  AND observation.snapshot_id =
                        active.snapshot_id
                  AND observation.root_schema =
                        active.root_schema
                  AND observation.root_relation =
                        active.root_relation
                  AND observation.root_oid =
                        active.root_oid
                  AND observation.child_schema =
                        active.child_schema
                  AND observation.child_relation =
                        active.child_relation
                  AND observation.child_oid =
                        active.child_oid
                  AND observation.child_relfilenode =
                        active.child_relfilenode
                  AND observation.total_bytes =
                        active.total_bytes
                  AND observation
                        .stable_child_identity_hash =
                        active.stable_child_identity_hash
                  AND observation
                        .stable_config_schema_hash =
                        active.stable_config_schema_hash
                  AND observation.classification =
                        'candidate'
                  AND NOT observation.planner_live
                  AND NOT observation.oracle_live
                  AND cardinality(
                        observation.blocker_codes) = 0)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation Q2 observation is no longer the exact accepted candidate.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT state.*
            INTO STRICT state_row
            FROM scrape_publication_state state
            WHERE state.id = TRUE;
            IF state_row.current_publication_id IS DISTINCT FROM
                    active.trigger_publication_id
               OR state_row.published_scrape_id IS DISTINCT FROM
                    active.trigger_scrape_id
               OR state_row.public_reads_frozen
               OR state_row.working_publication_id IS NOT NULL
               OR state_row.publication_commit_intent_started_at
                    IS NOT NULL
               OR state_row.max_score_mutation_gate_token IS NOT NULL
               OR state_row.improvement_notifications_scrape_id
                    IS DISTINCT FROM active.trigger_scrape_id
               OR state_row.improvement_notifications_status
                    IS DISTINCT FROM 'completed'
               OR state_row.improvement_notifications_completed_at
                    IS NULL
               OR state_row.improvement_notifications_projection_ready
                    IS DISTINCT FROM TRUE
               OR state_row.improvement_notifications_projection_scrape_id
                    IS DISTINCT FROM active.trigger_scrape_id
               OR EXISTS (
                    SELECT 1
                    FROM scrape_log scrape
                    WHERE scrape.status = 'running')
               OR NOT EXISTS (
                    SELECT 1
                    FROM service_worker_status worker
                    WHERE worker.worker_key = 'scraper'
                      AND worker.status = 'offline'
                      AND worker.current_operation_json IS NULL)
            THEN
                RAISE EXCEPTION
                    'Publication state is not healthy, idle, and unchanged for snapshot-generation drop.'
                    USING ERRCODE = '55000';
            END IF;

            IF NOT EXISTS (
                    SELECT 1
                    FROM snapshot_generation_retention_holds hold_row
                    WHERE hold_row.hold_id = active.hold_id
                      AND hold_row.instrument = active.instrument
                      AND hold_row.snapshot_id = active.snapshot_id
                      AND hold_row.hold_kind =
                            'retention_in_flight'
                      AND hold_row.released_at IS NULL)
               OR EXISTS (
                    SELECT 1
                    FROM snapshot_generation_retention_holds hold_row
                    WHERE hold_row.instrument = active.instrument
                      AND hold_row.snapshot_id = active.snapshot_id
                      AND hold_row.released_at IS NULL
                      AND hold_row.hold_id <> active.hold_id)
               OR EXISTS (
                    SELECT 1
                    FROM scrape_writer_failures failure
                    WHERE failure.instrument = active.instrument
                      AND failure.scrape_id = active.snapshot_id
                      AND failure.replayed_at IS NULL)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation drop hold or writer-failure fence is invalid.'
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
                WHERE snapshot_state.instrument = active.instrument
                  AND snapshot_state.active_snapshot_id =
                        active.snapshot_id
                UNION ALL
                SELECT 1
                FROM solo_current_projection_scope projection
                WHERE projection.instrument = active.instrument
                  AND projection.source_snapshot_id =
                        active.snapshot_id
                UNION ALL
                SELECT 1
                FROM leaderboard_published_scope_source source
                WHERE source.instrument = active.instrument
                  AND source.source_snapshot_id =
                        active.snapshot_id
                  AND source.published_scrape_id IN (
                        SELECT scrape_id
                        FROM named_publication_scrapes)
            )
            SELECT COUNT(*)::BIGINT
            INTO target_reference_count
            FROM target_roots;
            IF target_reference_count <> 0 THEN
                RAISE EXCEPTION
                    'Snapshot-generation drop target gained % live reference(s).',
                    target_reference_count
                    USING ERRCODE = '55000';
            END IF;

            EXECUTE format(
                'LOCK TABLE ONLY %I.%I IN SHARE MODE',
                active.default_partition_schema,
                active.default_partition_relation);
            EXECUTE format(
                'LOCK TABLE ONLY %I.%I IN ACCESS EXCLUSIVE MODE',
                active.quarantine_schema,
                active.quarantine_relation);

            SELECT
                relation.oid::BIGINT,
                relation.relfilenode::BIGINT,
                (
                    SELECT COUNT(*)::INTEGER
                    FROM pg_inherits inheritance
                    WHERE inheritance.inhrelid =
                            relation.oid),
                pg_total_relation_size(relation.oid)::BIGINT,
                (
                    SELECT COUNT(*)::INTEGER
                    FROM pg_constraint constraint_row
                    WHERE constraint_row.conrelid = relation.oid
                      AND constraint_row.conname =
                            active.snapshot_check_constraint
                      AND constraint_row.contype = 'c'
                      AND constraint_row.convalidated),
                (
                    SELECT regexp_replace(
                        pg_get_expr(
                            constraint_row.conbin,
                            constraint_row.conrelid,
                            TRUE),
                        '[()[:space:]]',
                        '',
                        'g')
                    FROM pg_constraint constraint_row
                    WHERE constraint_row.conrelid = relation.oid
                      AND constraint_row.conname =
                            active.snapshot_check_constraint
                      AND constraint_row.contype = 'c'
                      AND constraint_row.convalidated),
                (
                    SELECT COUNT(*)::INTEGER
                    FROM pg_trigger trigger_row
                    WHERE trigger_row.tgrelid = relation.oid
                      AND trigger_row.tgname =
                            active.mutation_guard_trigger
                      AND NOT trigger_row.tgisinternal
                      AND trigger_row.tgenabled = 'O'
                      AND trigger_row.tgfoid =
                            'public.fst_reject_snapshot_generation_quarantine_relation_mutation()'
                                ::regprocedure),
                (
                    SELECT COUNT(*)::INTEGER
                    FROM pg_index index_row
                    WHERE index_row.indrelid = relation.oid
                      AND index_row.indisvalid
                      AND index_row.indisready),
                (
                    SELECT COUNT(*)::INTEGER
                    FROM pg_index index_row
                    JOIN pg_inherits inheritance
                      ON inheritance.inhrelid =
                            index_row.indexrelid
                    WHERE index_row.indrelid = relation.oid)
            INTO STRICT
                current_oid,
                current_relfilenode,
                current_parent_count,
                current_total_bytes,
                exact_check_count,
                exact_check_expression,
                mutation_guard_count,
                child_index_count,
                attached_child_index_count
            FROM pg_class relation
            JOIN pg_namespace namespace
              ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = active.quarantine_schema
              AND relation.relname =
                    active.quarantine_relation
              AND relation.relkind = 'r';
            EXECUTE format(
                'SELECT COUNT(*)::BIGINT FROM ONLY %I.%I',
                active.quarantine_schema,
                active.quarantine_relation)
            INTO current_row_count;

            IF current_oid <> active.child_oid
               OR current_relfilenode <>
                    active.child_relfilenode
               OR current_parent_count <> 0
               OR current_row_count <> active.row_count
               OR current_total_bytes <> active.total_bytes
               OR exact_check_count <> 1
               OR exact_check_expression <>
                    'snapshot_id=' ||
                    active.snapshot_id::TEXT
               OR mutation_guard_count <> 1
               OR child_index_count <> 2
               OR attached_child_index_count <> 0
               OR to_regclass(
                    format(
                        '%I.%I',
                        active.child_schema,
                        active.child_relation)) IS NOT NULL
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation private relation changed before drop.'
                    USING ERRCODE = '55000';
            END IF;

            current_index_inventory :=
                fst_snapshot_generation_index_inventory(
                    active.child_oid,
                    active.root_oid,
                    FALSE);
            SELECT COUNT(*)::INTEGER
            INTO current_index_inventory_count
            FROM jsonb_each(current_index_inventory);
            SELECT COUNT(*)::INTEGER
            INTO recorded_index_count
            FROM snapshot_generation_quarantine_index_renames
                rename_row
            WHERE rename_row.operation_id =
                    active.operation_id;
            SELECT COUNT(*)::INTEGER
            INTO matched_index_count
            FROM jsonb_each(current_index_inventory)
                AS inventory_row(index_role, index_data)
            JOIN snapshot_generation_quarantine_index_renames
                rename_row
              ON rename_row.operation_id =
                    active.operation_id
             AND rename_row.index_role =
                    inventory_row.index_role
             AND rename_row.index_oid =
                    (
                        inventory_row.index_data
                        ->> 'indexOid')::BIGINT
             AND rename_row.index_relfilenode =
                    (
                        inventory_row.index_data
                        ->> 'indexRelfilenode')::BIGINT
             AND rename_row.new_index_name =
                    inventory_row.index_data
                        ->> 'indexName';
            IF current_index_inventory_count <> 2
               OR recorded_index_count <> 2
               OR matched_index_count <> 2
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation private indexes differ from immutable quarantine rename evidence.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT
                default_child.oid::BIGINT,
                inheritance.inhparent::BIGINT,
                pg_get_expr(
                    default_child.relpartbound,
                    default_child.oid,
                    TRUE),
                constraint_row.convalidated,
                regexp_replace(
                    pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE),
                    '[()[:space:]]',
                    '',
                    'g')
            INTO STRICT
                default_oid,
                default_parent_oid,
                default_bound,
                default_constraint_valid,
                default_constraint_expression
            FROM pg_class default_child
            JOIN pg_namespace default_namespace
              ON default_namespace.oid =
                    default_child.relnamespace
            JOIN pg_inherits inheritance
              ON inheritance.inhrelid = default_child.oid
            JOIN pg_constraint constraint_row
              ON constraint_row.conrelid = default_child.oid
             AND constraint_row.conname =
                    active.default_exclusion_constraint
             AND constraint_row.contype = 'c'
            WHERE default_namespace.nspname =
                    active.default_partition_schema
              AND default_child.relname =
                    active.default_partition_relation
              AND default_child.relkind = 'r';
            EXECUTE format(
                'SELECT COUNT(*)::BIGINT FROM ONLY %I.%I',
                active.default_partition_schema,
                active.default_partition_relation)
            INTO default_row_count;
            IF default_oid <> active.default_partition_oid
               OR default_parent_oid <> active.root_oid
               OR default_bound <> 'DEFAULT'
               OR NOT default_constraint_valid
               OR default_constraint_expression <>
                    'snapshot_id<>' || active.snapshot_id::TEXT
               OR default_row_count <> 0
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation DEFAULT fence changed before drop.'
                    USING ERRCODE = '55000';
            END IF;

            SELECT COUNT(*)::BIGINT
            INTO unexpected_dependency_count
            FROM (
                SELECT constraint_row.oid
                FROM pg_constraint constraint_row
                WHERE constraint_row.confrelid = active.child_oid
                  AND constraint_row.conrelid <> active.child_oid
                UNION ALL
                SELECT publication_row.oid
                FROM pg_publication_rel publication_row
                WHERE publication_row.prrelid = active.child_oid
                UNION ALL
                SELECT policy_row.oid
                FROM pg_policy policy_row
                WHERE policy_row.polrelid = active.child_oid
                UNION ALL
                SELECT rewrite_row.oid
                FROM pg_rewrite rewrite_row
                JOIN pg_depend dependency
                  ON dependency.classid =
                        'pg_rewrite'::regclass
                 AND dependency.objid = rewrite_row.oid
                WHERE dependency.refclassid =
                        'pg_class'::regclass
                  AND dependency.refobjid = active.child_oid
                  AND rewrite_row.ev_class <> active.child_oid
                UNION ALL
                SELECT rewrite_row.oid
                FROM pg_rewrite rewrite_row
                WHERE rewrite_row.ev_class = active.child_oid
                  AND rewrite_row.rulename <> '_RETURN'
                UNION ALL
                SELECT trigger_row.oid
                FROM pg_trigger trigger_row
                WHERE trigger_row.tgrelid = active.child_oid
                  AND NOT trigger_row.tgisinternal
                  AND trigger_row.tgname <>
                        active.mutation_guard_trigger
                UNION ALL
                SELECT inheritance.inhrelid
                FROM pg_inherits inheritance
                WHERE inheritance.inhparent = active.child_oid
                   OR inheritance.inhrelid = active.child_oid
            ) unexpected;
            IF unexpected_dependency_count <> 0 THEN
                RAISE EXCEPTION
                    'Snapshot-generation private relation has % unexpected dependency row(s).',
                    unexpected_dependency_count
                    USING ERRCODE = '2BP01';
            END IF;

            IF current_database() <> p_database_name
               OR (
                    SELECT oid::BIGINT
                    FROM pg_database
                    WHERE datname = current_database()) <>
                    p_database_oid
               OR (
                    SELECT system_identifier::TEXT
                    FROM pg_control_system()) <>
                    p_system_identifier
               OR current_setting(
                    'server_version_num')::INTEGER <>
                    p_server_version_num
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation drop database identity changed.'
                    USING ERRCODE = '55000';
            END IF;

            -- Keep the already-validated Q2 exclusion name. Renaming a CHECK
            -- constraint would upgrade the DEFAULT child to ACCESS EXCLUSIVE;
            -- retaining it preserves the exact SHARE-only live-tree lock.
            durable_constraint_name :=
                active.default_exclusion_constraint;

            current_transaction_id :=
                pg_current_xact_id()::TEXT;
            INSERT INTO snapshot_generation_drop_operations (
                drop_operation_id,
                schema_version,
                tool_id,
                plan_digest,
                rehearsal_quarantine_operation_id,
                active_quarantine_operation_id,
                rehearsal_quarantined_attestation_id,
                rehearsal_soak_attestation_id,
                rehearsal_reattached_attestation_id,
                active_quarantined_attestation_id,
                active_soak_attestation_id,
                rehearsal_archive_manifest_sha256,
                rehearsal_archive_proof_manifest_sha256,
                archive_manifest_sha256,
                archive_sha256,
                archive_proof_manifest_sha256,
                fresh_archive_proof_manifest_sha256,
                source_evidence_manifest_sha256,
                recovery_bundle_manifest_sha256,
                semantic_projection_version,
                rehearsal_catalog_sha256,
                catalog_sha256,
                rehearsal_semantic_catalog_sha256,
                semantic_catalog_sha256,
                rehearsal_logical_index_shape_sha256,
                logical_index_shape_sha256,
                rehearsal_physical_index_inventory_sha256,
                physical_index_inventory_sha256,
                pre_drop_baseline_route_manifest_sha256,
                pre_drop_candidate_route_manifest_sha256,
                health_evidence_sha256,
                binary_sha256,
                restore_tool_sha256,
                restore_image_id_sha256,
                repository_commit,
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
                default_partition_schema,
                default_partition_relation,
                default_partition_oid,
                quarantine_default_exclusion_constraint,
                durable_default_exclusion_constraint,
                hold_id,
                stable_child_identity_hash,
                stable_config_schema_hash,
                row_count,
                row_fingerprint_sha256,
                logical_catalog_sha256,
                total_bytes,
                dependency_inventory,
                dependency_inventory_sha256,
                topology_evidence,
                topology_sha256,
                liveness_evidence,
                liveness_sha256,
                database_name,
                database_oid,
                system_identifier,
                server_version_num,
                health_started_at,
                health_completed_at,
                health_sample_count,
                health_sample_interval_seconds,
                proof_completed_at,
                backend_pid,
                transaction_id,
                approved_by,
                approval_reference,
                preflight_evidence,
                drop_evidence)
            VALUES (
                p_drop_operation_id,
                1,
                'fst.snapshot-generation-drop-only.v1',
                p_plan_digest,
                rehearsal.operation_id,
                active.operation_id,
                p_rehearsal_quarantined_attestation_id,
                p_rehearsal_soak_attestation_id,
                p_rehearsal_reattached_attestation_id,
                p_active_quarantined_attestation_id,
                p_active_soak_attestation_id,
                rehearsal.archive_manifest_sha256,
                rehearsal.archive_proof_manifest_sha256,
                active.archive_manifest_sha256,
                p_archive_sha256,
                active.archive_proof_manifest_sha256,
                p_fresh_archive_proof_manifest_sha256,
                active.source_evidence_manifest_sha256,
                p_recovery_bundle_manifest_sha256,
                p_semantic_projection_version,
                p_rehearsal_catalog_sha256,
                p_catalog_sha256,
                p_rehearsal_semantic_catalog_sha256,
                p_semantic_catalog_sha256,
                p_rehearsal_logical_index_shape_sha256,
                p_logical_index_shape_sha256,
                p_rehearsal_physical_index_inventory_sha256,
                p_physical_index_inventory_sha256,
                p_pre_drop_baseline_route_manifest_sha256,
                p_pre_drop_candidate_route_manifest_sha256,
                p_health_evidence_sha256,
                p_binary_sha256,
                p_restore_tool_sha256,
                p_restore_image_id_sha256,
                p_repository_commit,
                active.cycle_id,
                active.observation_id,
                active.trigger_scrape_id,
                active.trigger_publication_id,
                active.instrument,
                active.snapshot_id,
                active.root_schema,
                active.root_relation,
                active.root_oid,
                active.child_schema,
                active.child_relation,
                active.child_oid,
                active.child_relfilenode,
                active.quarantine_schema,
                active.quarantine_relation,
                active.default_partition_schema,
                active.default_partition_relation,
                active.default_partition_oid,
                active.default_exclusion_constraint,
                durable_constraint_name,
                active.hold_id,
                active.stable_child_identity_hash,
                active.stable_config_schema_hash,
                active.row_count,
                active.row_fingerprint_sha256,
                active.logical_catalog_sha256,
                active.total_bytes,
                p_dependency_inventory,
                p_dependency_inventory_sha256,
                p_topology_evidence,
                p_topology_sha256,
                p_liveness_evidence,
                p_liveness_sha256,
                p_database_name,
                p_database_oid,
                p_system_identifier,
                p_server_version_num,
                p_health_started_at,
                p_health_completed_at,
                p_health_sample_count,
                p_health_sample_interval_seconds,
                p_proof_completed_at,
                pg_backend_pid(),
                current_transaction_id,
                p_approved_by,
                p_approval_reference,
                p_preflight_evidence,
                p_drop_evidence);
            INSERT INTO snapshot_generation_drop_evidence (
                drop_operation_id,
                sequence,
                phase,
                kind,
                payload,
                previous_hash,
                current_hash)
            VALUES (
                p_drop_operation_id,
                1,
                'drop',
                'committed',
                jsonb_build_object(
                    'planDigest', p_plan_digest,
                    'preflight', p_preflight_evidence,
                    'drop', p_drop_evidence),
                NULL,
                p_drop_evidence_sha256);

            INSERT INTO snapshot_generation_drop_attestations (
                drop_operation_id,
                stage,
                publication_id,
                published_scrape_id,
                route_count,
                baseline_route_manifest_sha256,
                candidate_route_manifest_sha256,
                status_parity,
                semantic_json_parity,
                difference_count,
                database_evidence,
                evidence_sha256,
                attested_by)
            VALUES (
                p_drop_operation_id,
                'pre_drop',
                active.trigger_publication_id,
                active.trigger_scrape_id,
                p_pre_drop_route_count,
                p_pre_drop_baseline_route_manifest_sha256,
                p_pre_drop_candidate_route_manifest_sha256,
                p_pre_drop_status_parity,
                p_pre_drop_semantic_json_parity,
                p_pre_drop_difference_count,
                p_preflight_evidence,
                p_pre_drop_attestation_sha256,
                p_approved_by);
            EXECUTE format(
                'DROP TABLE %I.%I RESTRICT',
                active.quarantine_schema,
                active.quarantine_relation);

            IF to_regclass(
                    format(
                        '%I.%I',
                        active.quarantine_schema,
                        active.quarantine_relation)) IS NOT NULL
               OR EXISTS (
                    SELECT 1
                    FROM pg_class relation
                    WHERE relation.oid = active.child_oid)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation private relation remained after drop.'
                    USING ERRCODE = '55000';
            END IF;

            RETURN p_drop_operation_id;
        END
        $drop_execute$;

        CREATE OR REPLACE FUNCTION
            fst_record_snapshot_generation_drop_attestation(
                p_drop_operation_id TEXT,
                p_stage TEXT,
                p_publication_id BIGINT,
                p_published_scrape_id BIGINT,
                p_route_count INTEGER,
                p_baseline_route_manifest_sha256 TEXT,
                p_candidate_route_manifest_sha256 TEXT,
                p_database_evidence JSONB,
                p_evidence_sha256 TEXT,
                p_attested_by TEXT)
        RETURNS BIGINT
        LANGUAGE plpgsql
        SECURITY INVOKER
        SET search_path = pg_catalog, public
        AS $drop_attestation$
        DECLARE
            inserted_id BIGINT;
            drop_row snapshot_generation_drop_operations%ROWTYPE;
            state_row scrape_publication_state%ROWTYPE;
            evidence_sequence INTEGER;
            previous_evidence_hash TEXT;
            latest_candidate_sha256 TEXT;
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
               OR NOT pg_try_advisory_xact_lock(2026083002)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation drop attestation lock chain is busy; retry in a new transaction.'
                    USING ERRCODE = '55P03';
            END IF;
            IF p_stage NOT IN (
                    'dropped',
                    'post_publication')
               OR p_route_count <> 55
               OR p_baseline_route_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_candidate_route_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_evidence_sha256 !~ '^[0-9a-f]{64}$'
               OR COALESCE(p_attested_by, '') = ''
               OR jsonb_typeof(p_database_evidence) <> 'object'
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation drop attestation arguments are invalid.'
                    USING ERRCODE = '22023';
            END IF;
            SELECT operation.*
            INTO STRICT drop_row
            FROM snapshot_generation_drop_operations operation
            WHERE operation.drop_operation_id =
                    p_drop_operation_id;
            IF EXISTS (
                SELECT 1
                FROM snapshot_generation_drop_attestations
                    attestation
                WHERE attestation.drop_operation_id =
                        p_drop_operation_id
                  AND attestation.stage = p_stage
                  AND attestation.publication_id =
                        p_publication_id
                  AND attestation.published_scrape_id =
                        p_published_scrape_id
                  AND attestation.baseline_route_manifest_sha256 =
                        p_baseline_route_manifest_sha256
                  AND attestation.candidate_route_manifest_sha256 =
                        p_candidate_route_manifest_sha256
                  AND attestation.evidence_sha256 =
                        p_evidence_sha256)
            THEN
                SELECT attestation.attestation_id
                INTO STRICT inserted_id
                FROM snapshot_generation_drop_attestations
                    attestation
                WHERE attestation.drop_operation_id =
                        p_drop_operation_id
                  AND attestation.stage = p_stage
                  AND attestation.publication_id =
                        p_publication_id
                  AND attestation.candidate_route_manifest_sha256 =
                        p_candidate_route_manifest_sha256;
                RETURN inserted_id;
            END IF;
            SELECT state.*
            INTO STRICT state_row
            FROM scrape_publication_state state
            WHERE state.id = TRUE;
            SELECT attestation.candidate_route_manifest_sha256
            INTO latest_candidate_sha256
            FROM snapshot_generation_drop_attestations
                attestation
            WHERE attestation.drop_operation_id =
                    p_drop_operation_id
              AND attestation.stage IN (
                    'pre_drop',
                    'dropped',
                    'post_publication')
            ORDER BY attestation.attestation_id DESC
            LIMIT 1;
            IF state_row.current_publication_id IS DISTINCT FROM
                    p_publication_id
               OR state_row.published_scrape_id IS DISTINCT FROM
                    p_published_scrape_id
               OR state_row.public_reads_frozen
               OR state_row.working_publication_id IS NOT NULL
               OR EXISTS (
                    SELECT 1
                    FROM scrape_log scrape
                    WHERE scrape.status = 'running')
               OR to_regclass(
                    format(
                        '%I.%I',
                        drop_row.quarantine_schema,
                        drop_row.quarantine_relation)) IS NOT NULL
               OR to_regclass(
                    format(
                        '%I.%I',
                        drop_row.child_schema,
                        drop_row.child_relation)) IS NOT NULL
               OR (
                    p_stage = 'dropped'
                    AND (
                        p_publication_id <>
                            drop_row.trigger_publication_id
                        OR p_published_scrape_id <>
                            drop_row.trigger_scrape_id
                        OR p_baseline_route_manifest_sha256 <>
                            drop_row.pre_drop_candidate_route_manifest_sha256))
               OR (
                    p_stage = 'post_publication'
                    AND (
                        p_publication_id =
                            drop_row.trigger_publication_id
                        OR latest_candidate_sha256 IS NULL
                        OR p_baseline_route_manifest_sha256 <>
                            latest_candidate_sha256))
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation drop attestation state is unsafe.'
                    USING ERRCODE = '55000';
            END IF;
            INSERT INTO snapshot_generation_drop_attestations (
                drop_operation_id,
                stage,
                publication_id,
                published_scrape_id,
                route_count,
                baseline_route_manifest_sha256,
                candidate_route_manifest_sha256,
                status_parity,
                semantic_json_parity,
                difference_count,
                database_evidence,
                evidence_sha256,
                attested_by)
            VALUES (
                p_drop_operation_id,
                p_stage,
                p_publication_id,
                p_published_scrape_id,
                p_route_count,
                p_baseline_route_manifest_sha256,
                p_candidate_route_manifest_sha256,
                TRUE,
                TRUE,
                0,
                p_database_evidence,
                p_evidence_sha256,
                p_attested_by)
            RETURNING attestation_id
            INTO inserted_id;
            SELECT
                COALESCE(MAX(evidence.sequence), 0) + 1,
                (
                    SELECT latest.current_hash
                    FROM snapshot_generation_drop_evidence latest
                    WHERE latest.drop_operation_id =
                            p_drop_operation_id
                    ORDER BY latest.sequence DESC
                    LIMIT 1)
            INTO
                evidence_sequence,
                previous_evidence_hash
            FROM snapshot_generation_drop_evidence evidence
            WHERE evidence.drop_operation_id =
                    p_drop_operation_id;
            INSERT INTO snapshot_generation_drop_evidence (
                drop_operation_id,
                sequence,
                phase,
                kind,
                payload,
                previous_hash,
                current_hash)
            VALUES (
                p_drop_operation_id,
                evidence_sequence,
                p_stage,
                'route_attestation',
                jsonb_build_object(
                    'attestationId', inserted_id,
                    'publicationId', p_publication_id,
                    'publishedScrapeId',
                        p_published_scrape_id,
                    'baselineRouteManifestSha256',
                        p_baseline_route_manifest_sha256,
                    'candidateRouteManifestSha256',
                        p_candidate_route_manifest_sha256,
                    'databaseEvidence',
                        p_database_evidence),
                previous_evidence_hash,
                p_evidence_sha256);
            RETURN inserted_id;
        END
        $drop_attestation$;

        DROP FUNCTION IF EXISTS
            fst_restore_snapshot_generation(
                TEXT,
                TEXT,
                TEXT,
                BIGINT,
                BIGINT,
                BIGINT,
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                JSONB);

        DROP FUNCTION IF EXISTS
            fst_restore_snapshot_generation(
                TEXT,
                TEXT,
                TEXT,
                BIGINT,
                BIGINT,
                BIGINT,
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                JSONB,
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                JSONB);

        CREATE OR REPLACE FUNCTION
            fst_restore_snapshot_generation(
                p_restore_operation_id TEXT,
                p_plan_digest TEXT,
                p_drop_operation_id TEXT,
                p_expected_child_oid BIGINT,
                p_expected_child_relfilenode BIGINT,
                p_expected_row_count BIGINT,
                p_row_fingerprint_sha256 TEXT,
                p_logical_catalog_sha256 TEXT,
                p_semantic_catalog_sha256 TEXT,
                p_logical_index_shape_sha256 TEXT,
                p_authorization_id TEXT,
                p_executing_tool_sha256 TEXT,
                p_validator_base_tool_sha256 TEXT,
                p_authorized_archive_helper_sha256 TEXT,
                p_repair_package_manifest_sha256 TEXT,
                p_archived_index_names JSONB,
                p_temporary_check_constraint TEXT,
                p_mutation_guard_trigger TEXT,
                p_restored_by TEXT,
                p_restore_reference TEXT,
                p_restore_evidence JSONB)
        RETURNS TEXT
        LANGUAGE plpgsql
        SECURITY INVOKER
        SET search_path = pg_catalog, public
        AS $restore_execute$
        DECLARE
            drop_row snapshot_generation_drop_operations%ROWTYPE;
            state_row scrape_publication_state%ROWTYPE;
            restored_oid BIGINT;
            restored_relfilenode BIGINT;
            restored_parent_count INTEGER;
            restored_row_count BIGINT;
            exact_check_count INTEGER;
            exact_check_expression TEXT;
            mutation_guard_count INTEGER;
            attached_index_count INTEGER;
            unexpected_dependency_count BIGINT;
            target_reference_count BIGINT;
            default_oid BIGINT;
            default_parent_oid BIGINT;
            default_bound TEXT;
            default_constraint_valid BOOLEAN;
            default_constraint_expression TEXT;
            default_row_count BIGINT;
            attached_parent_oid BIGINT;
            attached_bound TEXT;
            current_transaction_id TEXT;
            restored_index_inventory JSONB;
            restore_pk_name TEXT;
            restore_score_name TEXT;
            existing_index_count INTEGER;
        BEGIN
            PERFORM set_config('lock_timeout', '5s', TRUE);
            PERFORM set_config('statement_timeout', '180s', TRUE);
            PERFORM set_config(
                'idle_in_transaction_session_timeout',
                '240s',
                TRUE);
            PERFORM set_config(
                'transaction_timeout',
                '240s',
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
               OR NOT pg_try_advisory_xact_lock(2026083002)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore lock chain is busy; retry in a new transaction.'
                    USING ERRCODE = '55P03';
            END IF;
            IF p_restore_operation_id !~ '^[0-9a-f]{32}$'
               OR p_plan_digest !~ '^[0-9a-f]{64}$'
               OR p_expected_child_oid <= 0
               OR p_expected_child_relfilenode <= 0
               OR p_expected_row_count <= 0
               OR p_row_fingerprint_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_logical_catalog_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_semantic_catalog_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_logical_index_shape_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_executing_tool_sha256
                    !~ '^[0-9a-f]{64}$'
               OR jsonb_typeof(p_archived_index_names)
                    <> 'object'
               OR p_archived_index_names ? 'pk'
                    IS DISTINCT FROM TRUE
               OR p_archived_index_names ? 'score'
                    IS DISTINCT FROM TRUE
               OR COALESCE(p_temporary_check_constraint, '') = ''
               OR COALESCE(p_mutation_guard_trigger, '') = ''
               OR COALESCE(p_restored_by, '') = ''
               OR COALESCE(p_restore_reference, '') = ''
               OR jsonb_typeof(p_restore_evidence) <> 'object'
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore arguments are invalid.'
                    USING ERRCODE = '22023';
            END IF;
            SELECT operation.*
            INTO STRICT drop_row
            FROM snapshot_generation_drop_operations operation
            WHERE operation.drop_operation_id =
                    p_drop_operation_id;
            IF p_expected_row_count <> drop_row.row_count
               OR p_row_fingerprint_sha256 <>
                    drop_row.row_fingerprint_sha256
               OR p_logical_catalog_sha256 <>
                    drop_row.logical_catalog_sha256
               OR p_semantic_catalog_sha256 <>
                    drop_row.semantic_catalog_sha256
               OR p_logical_index_shape_sha256 <>
                    drop_row.logical_index_shape_sha256
               OR p_temporary_check_constraint <>
                    'ck_sgr_' ||
                    drop_row.snapshot_id::TEXT || '_' ||
                    left(p_restore_operation_id, 12)
               OR p_mutation_guard_trigger <>
                    'trg_sgr_' ||
                    drop_row.snapshot_id::TEXT || '_' ||
                    left(p_restore_operation_id, 12)
               OR p_restore_reference =
                    drop_row.approval_reference
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore evidence differs from the committed drop.'
                    USING ERRCODE = '55000';
            END IF;
            IF p_executing_tool_sha256 =
                    drop_row.restore_tool_sha256
            THEN
                IF p_authorization_id IS NOT NULL
                   OR p_validator_base_tool_sha256
                        IS NOT NULL
                   OR p_authorized_archive_helper_sha256
                        IS NOT NULL
                   OR p_repair_package_manifest_sha256
                        IS NOT NULL
                THEN
                    RAISE EXCEPTION
                        'Pinned snapshot-generation restore cannot use repair authorization.'
                        USING ERRCODE = '55000';
                END IF;
            ELSE
                IF p_authorization_id
                        !~ '^[0-9a-f]{32}$'
                   OR p_validator_base_tool_sha256
                        !~ '^[0-9a-f]{64}$'
                   OR p_authorized_archive_helper_sha256
                        !~ '^[0-9a-f]{64}$'
                   OR p_repair_package_manifest_sha256
                        !~ '^[0-9a-f]{64}$'
                   OR NOT EXISTS (
                        SELECT 1
                        FROM
                            snapshot_generation_restore_tool_authorizations
                                authorization_row
                        WHERE authorization_row.authorization_id =
                                p_authorization_id
                          AND authorization_row.drop_operation_id =
                                drop_row.drop_operation_id
                          AND authorization_row.drop_plan_digest =
                                drop_row.plan_digest
                          AND authorization_row.original_bundle_manifest_sha256 =
                                drop_row.recovery_bundle_manifest_sha256
                          AND authorization_row.pinned_restore_tool_sha256 =
                                drop_row.restore_tool_sha256
                          AND authorization_row.validator_base_tool_sha256 =
                                p_validator_base_tool_sha256
                          AND authorization_row.authorized_restore_tool_sha256 =
                                p_executing_tool_sha256
                          AND authorization_row.authorized_archive_helper_sha256 =
                                p_authorized_archive_helper_sha256
                          AND authorization_row.repair_package_manifest_sha256 =
                                p_repair_package_manifest_sha256)
                THEN
                    RAISE EXCEPTION
                        'Snapshot-generation restore-tool authorization is invalid at attach.'
                        USING ERRCODE = '55000';
                END IF;
            END IF;
            IF EXISTS (
                SELECT 1
                FROM snapshot_generation_restore_operations restore_row
                WHERE restore_row.restore_operation_id =
                        p_restore_operation_id
                  AND restore_row.plan_digest = p_plan_digest
                  AND restore_row.drop_operation_id =
                        p_drop_operation_id
                  AND restore_row.pinned_tool_sha256 =
                        drop_row.restore_tool_sha256
                  AND restore_row.executing_tool_sha256 =
                        p_executing_tool_sha256
                  AND restore_row.authorization_id
                        IS NOT DISTINCT FROM
                        p_authorization_id)
            THEN
                RETURN p_restore_operation_id;
            END IF;
            IF EXISTS (
                SELECT 1
                FROM snapshot_generation_restore_operations restore_row
                WHERE restore_row.restore_operation_id =
                        p_restore_operation_id
                   OR restore_row.plan_digest = p_plan_digest
                   OR restore_row.drop_operation_id =
                        p_drop_operation_id)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore identity conflicts with existing evidence.'
                    USING ERRCODE = '55000';
            END IF;
            IF NOT EXISTS (
                    SELECT 1
                    FROM snapshot_generation_retention_holds hold_row
                    WHERE hold_row.hold_id = drop_row.hold_id
                      AND hold_row.hold_kind =
                            'retention_in_flight'
                      AND hold_row.released_at IS NULL)
               OR to_regclass(
                    format(
                        '%I.%I',
                        drop_row.quarantine_schema,
                        drop_row.quarantine_relation)) IS NOT NULL
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore hold or private relation state is invalid.'
                    USING ERRCODE = '55000';
            END IF;
            SELECT state.*
            INTO STRICT state_row
            FROM scrape_publication_state state
            WHERE state.id = TRUE;
            IF state_row.current_publication_id IS NULL
               OR state_row.published_scrape_id IS NULL
               OR state_row.working_publication_id IS NOT NULL
               OR state_row.publication_commit_intent_started_at
                    IS NOT NULL
               OR state_row.max_score_mutation_gate_token IS NOT NULL
               OR EXISTS (
                    SELECT 1
                    FROM scrape_log scrape
                    WHERE scrape.status = 'running')
               OR NOT EXISTS (
                    SELECT 1
                    FROM service_worker_status worker
                    WHERE worker.worker_key = 'scraper'
                      AND worker.status = 'offline'
                      AND worker.current_operation_json IS NULL)
            THEN
                RAISE EXCEPTION
                    'Publication state is not idle for snapshot-generation restore.'
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
                        drop_row.instrument
                  AND snapshot_state.active_snapshot_id =
                        drop_row.snapshot_id
                UNION ALL
                SELECT 1
                FROM solo_current_projection_scope projection
                WHERE projection.instrument =
                        drop_row.instrument
                  AND projection.source_snapshot_id =
                        drop_row.snapshot_id
                UNION ALL
                SELECT 1
                FROM leaderboard_published_scope_source source
                WHERE source.instrument = drop_row.instrument
                  AND source.source_snapshot_id =
                        drop_row.snapshot_id
                  AND source.published_scrape_id IN (
                        SELECT scrape_id
                        FROM named_publication_scrapes)
                UNION ALL
                SELECT 1
                FROM scrape_writer_failures failure
                WHERE failure.instrument = drop_row.instrument
                  AND failure.scrape_id = drop_row.snapshot_id
                  AND failure.replayed_at IS NULL
                UNION ALL
                SELECT 1
                FROM snapshot_generation_retention_holds hold_row
                WHERE hold_row.instrument = drop_row.instrument
                  AND hold_row.snapshot_id = drop_row.snapshot_id
                  AND hold_row.released_at IS NULL
                  AND hold_row.hold_id <> drop_row.hold_id
            )
            SELECT COUNT(*)::BIGINT
            INTO target_reference_count
            FROM target_roots;
            IF target_reference_count <> 0 THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore target has % conflicting reference(s).',
                    target_reference_count
                    USING ERRCODE = '55000';
            END IF;

            EXECUTE format(
                'LOCK TABLE ONLY %I.%I IN SHARE MODE',
                drop_row.default_partition_schema,
                drop_row.default_partition_relation);
            EXECUTE format(
                'LOCK TABLE ONLY %I.%I IN ACCESS EXCLUSIVE MODE',
                drop_row.child_schema,
                drop_row.child_relation);
            SELECT
                default_child.oid::BIGINT,
                inheritance.inhparent::BIGINT,
                pg_get_expr(
                    default_child.relpartbound,
                    default_child.oid,
                    TRUE),
                constraint_row.convalidated,
                regexp_replace(
                    pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE),
                    '[()[:space:]]',
                    '',
                    'g')
            INTO STRICT
                default_oid,
                default_parent_oid,
                default_bound,
                default_constraint_valid,
                default_constraint_expression
            FROM pg_class default_child
            JOIN pg_namespace default_namespace
              ON default_namespace.oid =
                    default_child.relnamespace
            JOIN pg_inherits inheritance
              ON inheritance.inhrelid = default_child.oid
            JOIN pg_constraint constraint_row
              ON constraint_row.conrelid = default_child.oid
             AND constraint_row.conname =
                    drop_row.durable_default_exclusion_constraint
             AND constraint_row.contype = 'c'
            WHERE default_namespace.nspname =
                    drop_row.default_partition_schema
              AND default_child.relname =
                    drop_row.default_partition_relation
              AND default_child.relkind = 'r';
            EXECUTE format(
                'SELECT COUNT(*)::BIGINT FROM ONLY %I.%I',
                drop_row.default_partition_schema,
                drop_row.default_partition_relation)
            INTO default_row_count;
            IF default_oid <> drop_row.default_partition_oid
               OR default_parent_oid <> drop_row.root_oid
               OR default_bound <> 'DEFAULT'
               OR NOT default_constraint_valid
               OR default_constraint_expression <>
                    'snapshot_id<>' ||
                    drop_row.snapshot_id::TEXT
               OR default_row_count <> 0
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore DEFAULT fence is invalid.'
                    USING ERRCODE = '55000';
            END IF;
            SELECT
                relation.oid::BIGINT,
                relation.relfilenode::BIGINT,
                (
                    SELECT COUNT(*)::INTEGER
                    FROM pg_inherits inheritance
                    WHERE inheritance.inhrelid =
                            relation.oid),
                (
                    SELECT COUNT(*)::INTEGER
                    FROM pg_constraint constraint_row
                    WHERE constraint_row.conrelid = relation.oid
                      AND constraint_row.conname =
                            p_temporary_check_constraint
                      AND constraint_row.contype = 'c'
                      AND constraint_row.convalidated),
                (
                    SELECT regexp_replace(
                        pg_get_expr(
                            constraint_row.conbin,
                            constraint_row.conrelid,
                            TRUE),
                        '[()[:space:]]',
                        '',
                        'g')
                    FROM pg_constraint constraint_row
                    WHERE constraint_row.conrelid = relation.oid
                      AND constraint_row.conname =
                            p_temporary_check_constraint
                      AND constraint_row.contype = 'c'
                      AND constraint_row.convalidated),
                (
                    SELECT COUNT(*)::INTEGER
                    FROM pg_trigger trigger_row
                    WHERE trigger_row.tgrelid = relation.oid
                      AND trigger_row.tgname =
                            p_mutation_guard_trigger
                      AND NOT trigger_row.tgisinternal
                      AND trigger_row.tgenabled = 'O'
                      AND trigger_row.tgfoid =
                            'public.fst_reject_snapshot_generation_quarantine_relation_mutation()'
                                ::regprocedure)
            INTO STRICT
                restored_oid,
                restored_relfilenode,
                restored_parent_count,
                exact_check_count,
                exact_check_expression,
                mutation_guard_count
            FROM pg_class relation
            JOIN pg_namespace namespace
              ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = drop_row.child_schema
              AND relation.relname = drop_row.child_relation
              AND relation.relkind = 'r';
            EXECUTE format(
                'SELECT COUNT(*)::BIGINT FROM ONLY %I.%I',
                drop_row.child_schema,
                drop_row.child_relation)
            INTO restored_row_count;
            SELECT COUNT(*)::INTEGER
            INTO existing_index_count
            FROM pg_index index_row
            WHERE index_row.indrelid = restored_oid;
            restore_pk_name :=
                'sgri_' || p_restore_operation_id ||
                '_pk';
            restore_score_name :=
                'sgri_' || p_restore_operation_id ||
                '_score';
            IF existing_index_count <> 0
               OR to_regclass(
                    format(
                        '%I.%I',
                        drop_row.child_schema,
                        restore_pk_name)) IS NOT NULL
               OR to_regclass(
                    format(
                        '%I.%I',
                        drop_row.child_schema,
                        restore_score_name)) IS NOT NULL
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore staging indexes or derived names already exist.'
                    USING ERRCODE = '55000';
            END IF;
            EXECUTE format(
                'CREATE UNIQUE INDEX %I ON %I.%I USING btree (snapshot_id, song_id, instrument, account_id)',
                restore_pk_name,
                drop_row.child_schema,
                drop_row.child_relation);
            EXECUTE format(
                'ALTER TABLE %I.%I ADD CONSTRAINT %I PRIMARY KEY USING INDEX %I',
                drop_row.child_schema,
                drop_row.child_relation,
                restore_pk_name,
                restore_pk_name);
            EXECUTE format(
                'CREATE INDEX %I ON %I.%I USING btree (snapshot_id, song_id, instrument, score DESC)',
                restore_score_name,
                drop_row.child_schema,
                drop_row.child_relation);
            restored_index_inventory :=
                fst_snapshot_generation_index_inventory(
                    restored_oid,
                    drop_row.root_oid,
                    FALSE);
            IF restored_index_inventory
                    #>> '{pk,indexName}' <>
                    restore_pk_name
               OR restored_index_inventory
                    #>> '{pk,constraintName}' <>
                    restore_pk_name
               OR restored_index_inventory
                    #>> '{score,indexName}' <>
                    restore_score_name
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore deterministic index construction is invalid.'
                    USING ERRCODE = '55000';
            END IF;
            SELECT COUNT(*)::BIGINT
            INTO unexpected_dependency_count
            FROM (
                SELECT constraint_row.oid
                FROM pg_constraint constraint_row
                WHERE constraint_row.confrelid = restored_oid
                  AND constraint_row.conrelid <> restored_oid
                UNION ALL
                SELECT publication_row.oid
                FROM pg_publication_rel publication_row
                WHERE publication_row.prrelid = restored_oid
                UNION ALL
                SELECT policy_row.oid
                FROM pg_policy policy_row
                WHERE policy_row.polrelid = restored_oid
                UNION ALL
                SELECT rewrite_row.oid
                FROM pg_rewrite rewrite_row
                JOIN pg_depend dependency
                  ON dependency.classid =
                        'pg_rewrite'::regclass
                 AND dependency.objid = rewrite_row.oid
                WHERE dependency.refclassid =
                        'pg_class'::regclass
                  AND dependency.refobjid = restored_oid
                  AND rewrite_row.ev_class <> restored_oid
                UNION ALL
                SELECT rewrite_row.oid
                FROM pg_rewrite rewrite_row
                WHERE rewrite_row.ev_class = restored_oid
                  AND rewrite_row.rulename <> '_RETURN'
                UNION ALL
                SELECT trigger_row.oid
                FROM pg_trigger trigger_row
                WHERE trigger_row.tgrelid = restored_oid
                  AND NOT trigger_row.tgisinternal
                  AND trigger_row.tgname <>
                        p_mutation_guard_trigger
                UNION ALL
                SELECT inheritance.inhrelid
                FROM pg_inherits inheritance
                WHERE inheritance.inhparent = restored_oid
            ) unexpected;
            IF restored_oid <> p_expected_child_oid
               OR restored_relfilenode <>
                    p_expected_child_relfilenode
               OR restored_oid = drop_row.child_oid
               OR restored_parent_count <> 0
               OR restored_row_count <>
                    p_expected_row_count
               OR exact_check_count <> 1
               OR exact_check_expression <>
                    'snapshot_id=' ||
                    drop_row.snapshot_id::TEXT ||
                    'ANDinstrument=' ||
                    quote_literal(drop_row.instrument) ||
                    '::text'
               OR mutation_guard_count <> 1
               OR unexpected_dependency_count <> 0
               OR NOT EXISTS (
                    SELECT 1
                    FROM pg_constraint constraint_row
                    WHERE constraint_row.conrelid =
                            drop_row.default_partition_oid
                      AND constraint_row.conname =
                            drop_row.durable_default_exclusion_constraint
                      AND constraint_row.contype = 'c'
                      AND constraint_row.convalidated)
            THEN
                RAISE EXCEPTION
                    'Restored snapshot-generation staging identity is invalid.'
                    USING ERRCODE = '55000';
            END IF;

            EXECUTE format(
                'ALTER TABLE %I.%I ATTACH PARTITION %I.%I FOR VALUES IN (%s)',
                drop_row.root_schema,
                drop_row.root_relation,
                drop_row.child_schema,
                drop_row.child_relation,
                drop_row.snapshot_id);
            SELECT
                inheritance.inhparent::BIGINT,
                pg_get_expr(
                    child.relpartbound,
                    child.oid,
                    TRUE)
            INTO STRICT
                attached_parent_oid,
                attached_bound
            FROM pg_class child
            JOIN pg_inherits inheritance
              ON inheritance.inhrelid = child.oid
            WHERE child.oid = restored_oid;
            SELECT COUNT(*)::INTEGER
            INTO attached_index_count
            FROM pg_index child_index
            JOIN pg_inherits child_index_inheritance
              ON child_index_inheritance.inhrelid =
                    child_index.indexrelid
            JOIN pg_index root_index
              ON root_index.indexrelid =
                    child_index_inheritance.inhparent
             AND root_index.indrelid = drop_row.root_oid
            JOIN pg_inherits root_index_inheritance
              ON root_index_inheritance.inhrelid =
                    root_index.indexrelid
            JOIN pg_class top_index_relation
              ON top_index_relation.oid =
                    root_index_inheritance.inhparent
            WHERE child_index.indrelid = restored_oid
              AND child_index.indisvalid
              AND child_index.indisready
              AND root_index.indisvalid
              AND root_index.indisready
              AND top_index_relation.relname IN (
                    'leaderboard_entries_snapshot_pkey',
                    'ix_les_snapshot_song_score');
            IF attached_parent_oid <> drop_row.root_oid
               OR attached_bound <>
                    format(
                        'FOR VALUES IN (%L)',
                        drop_row.snapshot_id)
               OR attached_index_count <> 2
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore did not attach both required index chains.'
                    USING ERRCODE = '55000';
            END IF;

            EXECUTE format(
                'ALTER TABLE %I.%I DROP CONSTRAINT %I',
                drop_row.child_schema,
                drop_row.child_relation,
                p_temporary_check_constraint);
            EXECUTE format(
                'ALTER TABLE %I.%I DROP CONSTRAINT %I',
                drop_row.default_partition_schema,
                drop_row.default_partition_relation,
                drop_row.durable_default_exclusion_constraint);

            current_transaction_id :=
                pg_current_xact_id()::TEXT;
            INSERT INTO snapshot_generation_restore_operations (
                restore_operation_id,
                schema_version,
                tool_id,
                plan_digest,
                drop_operation_id,
                archive_manifest_sha256,
                archive_sha256,
                recovery_bundle_manifest_sha256,
                pinned_tool_sha256,
                executing_tool_sha256,
                authorization_id,
                instrument,
                snapshot_id,
                root_schema,
                root_relation,
                root_oid,
                child_schema,
                child_relation,
                restored_child_oid,
                restored_child_relfilenode,
                partition_bound,
                row_count,
                row_fingerprint_sha256,
                logical_catalog_sha256,
                semantic_catalog_sha256,
                logical_index_shape_sha256,
                archived_index_names,
                restored_index_evidence,
                attached_index_count,
                hold_id,
                restored_by,
                restore_reference,
                restore_evidence,
                backend_pid,
                transaction_id)
            VALUES (
                p_restore_operation_id,
                1,
                'fst.snapshot-generation-restore.v1',
                p_plan_digest,
                drop_row.drop_operation_id,
                drop_row.archive_manifest_sha256,
                drop_row.archive_sha256,
                drop_row.recovery_bundle_manifest_sha256,
                drop_row.restore_tool_sha256,
                p_executing_tool_sha256,
                p_authorization_id,
                drop_row.instrument,
                drop_row.snapshot_id,
                drop_row.root_schema,
                drop_row.root_relation,
                drop_row.root_oid,
                drop_row.child_schema,
                drop_row.child_relation,
                restored_oid,
                restored_relfilenode,
                format(
                    'FOR VALUES IN (%L)',
                    drop_row.snapshot_id),
                restored_row_count,
                p_row_fingerprint_sha256,
                p_logical_catalog_sha256,
                p_semantic_catalog_sha256,
                p_logical_index_shape_sha256,
                p_archived_index_names,
                restored_index_inventory,
                attached_index_count,
                drop_row.hold_id,
                p_restored_by,
                p_restore_reference,
                p_restore_evidence,
                pg_backend_pid(),
                current_transaction_id);
            RETURN p_restore_operation_id;
        END
        $restore_execute$;

        DROP FUNCTION IF EXISTS
            fst_record_snapshot_generation_restore_attestation(
                TEXT,
                BIGINT,
                BIGINT,
                INTEGER,
                TEXT,
                TEXT,
                JSONB,
                TEXT,
                TEXT)
            RESTRICT;

        CREATE OR REPLACE FUNCTION
            fst_record_snapshot_generation_restore_attestation(
                p_restore_operation_id TEXT,
                p_publication_id BIGINT,
                p_published_scrape_id BIGINT,
                p_route_count INTEGER,
                p_baseline_route_manifest_sha256 TEXT,
                p_candidate_route_manifest_sha256 TEXT,
                p_route_semantic_evidence_sha256 TEXT,
                p_database_evidence JSONB,
                p_evidence_sha256 TEXT,
                p_attested_by TEXT,
                p_evidence_tool_sha256 TEXT,
                p_continuation_authorization_id TEXT)
        RETURNS TEXT
        LANGUAGE plpgsql
        SECURITY INVOKER
        SET search_path = pg_catalog, public
        AS $restore_attestation$
        DECLARE
            restore_row
                snapshot_generation_restore_operations%ROWTYPE;
            drop_row
                snapshot_generation_drop_operations%ROWTYPE;
            authorization_row
                snapshot_generation_restore_continuation_authorizations%ROWTYPE;
            state_row scrape_publication_state%ROWTYPE;
            latest_drop_candidate_sha256 TEXT;
            restored_identity_count INTEGER;
            attached_index_count INTEGER;
            restored_row_count BIGINT;
            default_row_count BIGINT;
            current_index_inventory JSONB;
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
               OR NOT pg_try_advisory_xact_lock(2026083002)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore attestation lock chain is busy; retry in a new transaction.'
                    USING ERRCODE = '55P03';
            END IF;
            IF p_route_count <> 55
               OR p_baseline_route_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_candidate_route_manifest_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_route_semantic_evidence_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_evidence_sha256 !~ '^[0-9a-f]{64}$'
               OR p_evidence_tool_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_continuation_authorization_id
                    !~ '^[0-9a-f]{32}$'
               OR COALESCE(p_attested_by, '') = ''
               OR jsonb_typeof(p_database_evidence) <> 'object'
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore attestation arguments are invalid.'
                    USING ERRCODE = '22023';
            END IF;
            SELECT operation.*
            INTO STRICT restore_row
            FROM snapshot_generation_restore_operations operation
            WHERE operation.restore_operation_id =
                    p_restore_operation_id;
            SELECT operation.*
            INTO STRICT drop_row
            FROM snapshot_generation_drop_operations operation
            WHERE operation.drop_operation_id =
                    restore_row.drop_operation_id;
            SELECT continuation_auth.*
            INTO STRICT authorization_row
            FROM
                snapshot_generation_restore_continuation_authorizations
                    continuation_auth
            WHERE continuation_auth.restore_operation_id =
                    restore_row.restore_operation_id
              AND continuation_auth.continuation_authorization_id =
                    p_continuation_authorization_id
              AND continuation_auth.authorized_continuation_tool_sha256 =
                    p_evidence_tool_sha256
              AND continuation_auth.authorization_scope =
                    'confirm_attest_finalize'
              AND continuation_auth.route_parity_algorithm_id =
                    'fst.route-parity.canonical-zip.v1'
              AND continuation_auth.restore_plan_digest =
                    restore_row.plan_digest
              AND continuation_auth.drop_operation_id =
                    restore_row.drop_operation_id
              AND continuation_auth.predecessor_authorization_id =
                    restore_row.authorization_id
              AND continuation_auth.predecessor_restore_tool_sha256 =
                    restore_row.executing_tool_sha256
              AND continuation_auth.recovery_bundle_manifest_sha256 =
                    restore_row.recovery_bundle_manifest_sha256
              AND continuation_auth.route_parity_preflight_sha256 =
                    p_route_semantic_evidence_sha256
              AND continuation_auth.baseline_route_manifest_sha256 =
                    p_baseline_route_manifest_sha256
              AND continuation_auth.candidate_route_manifest_sha256 =
                    p_candidate_route_manifest_sha256
              AND continuation_auth.publication_id =
                    p_publication_id
              AND continuation_auth.published_scrape_id =
                    p_published_scrape_id;
            IF EXISTS (
                SELECT 1
                FROM snapshot_generation_restore_attestations
                    attestation
                WHERE attestation.restore_operation_id =
                        p_restore_operation_id
                  AND attestation.publication_id =
                        p_publication_id
                  AND attestation.published_scrape_id =
                        p_published_scrape_id
                  AND attestation.baseline_route_manifest_sha256 =
                        p_baseline_route_manifest_sha256
                  AND attestation.candidate_route_manifest_sha256 =
                        p_candidate_route_manifest_sha256
                  AND attestation.route_semantic_evidence_sha256 =
                        p_route_semantic_evidence_sha256
                  AND attestation.evidence_sha256 =
                        p_evidence_sha256
                  AND attestation.evidence_tool_sha256 =
                        p_evidence_tool_sha256
                  AND attestation.continuation_authorization_id =
                        p_continuation_authorization_id)
            THEN
                RETURN p_restore_operation_id;
            END IF;
            IF EXISTS (
                SELECT 1
                FROM snapshot_generation_restore_attestations
                    attestation
                WHERE attestation.restore_operation_id =
                        p_restore_operation_id)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore attestation conflicts with existing evidence.'
                    USING ERRCODE = '55000';
            END IF;
            SELECT attestation.candidate_route_manifest_sha256
            INTO latest_drop_candidate_sha256
            FROM snapshot_generation_drop_attestations
                attestation
            JOIN snapshot_generation_restore_operations
                operation
              ON operation.restore_operation_id =
                    p_restore_operation_id
            WHERE attestation.drop_operation_id =
                    operation.drop_operation_id
              AND attestation.stage IN (
                    'pre_drop',
                    'dropped',
                    'post_publication')
            ORDER BY attestation.attestation_id DESC
            LIMIT 1;
            SELECT state.*
            INTO STRICT state_row
            FROM scrape_publication_state state
            WHERE state.id = TRUE;

            SELECT COUNT(*)::INTEGER
            INTO restored_identity_count
            FROM pg_class child
            JOIN pg_namespace namespace
              ON namespace.oid = child.relnamespace
            JOIN pg_inherits inheritance
              ON inheritance.inhrelid = child.oid
            WHERE child.oid =
                    restore_row.restored_child_oid
              AND child.relfilenode::BIGINT =
                    restore_row.restored_child_relfilenode
              AND namespace.nspname =
                    restore_row.child_schema
              AND child.relname =
                    restore_row.child_relation
              AND inheritance.inhparent =
                    restore_row.root_oid
              AND pg_get_expr(
                    child.relpartbound,
                    child.oid,
                    TRUE) =
                    restore_row.partition_bound;

            SELECT COUNT(*)::INTEGER
            INTO attached_index_count
            FROM pg_index child_index
            JOIN pg_inherits child_index_inheritance
              ON child_index_inheritance.inhrelid =
                    child_index.indexrelid
            JOIN pg_index root_index
              ON root_index.indexrelid =
                    child_index_inheritance.inhparent
             AND root_index.indrelid = restore_row.root_oid
            JOIN pg_inherits root_index_inheritance
              ON root_index_inheritance.inhrelid =
                    root_index.indexrelid
            JOIN pg_class top_index_relation
              ON top_index_relation.oid =
                    root_index_inheritance.inhparent
            WHERE child_index.indrelid =
                    restore_row.restored_child_oid
              AND child_index.indisvalid
              AND child_index.indisready
              AND root_index.indisvalid
              AND root_index.indisready
              AND top_index_relation.relname IN (
                    'leaderboard_entries_snapshot_pkey',
                    'ix_les_snapshot_song_score');

            current_index_inventory :=
                fst_snapshot_generation_index_inventory(
                    restore_row.restored_child_oid,
                    restore_row.root_oid,
                    TRUE);

            EXECUTE format(
                'SELECT COUNT(*)::BIGINT FROM ONLY %I.%I',
                restore_row.child_schema,
                restore_row.child_relation)
            INTO restored_row_count;
            EXECUTE format(
                'SELECT COUNT(*)::BIGINT FROM ONLY %I.%I',
                drop_row.default_partition_schema,
                drop_row.default_partition_relation)
            INTO default_row_count;

            IF state_row.current_publication_id IS DISTINCT FROM
                    p_publication_id
               OR state_row.published_scrape_id IS DISTINCT FROM
                    p_published_scrape_id
               OR state_row.public_reads_frozen
               OR state_row.working_publication_id IS NOT NULL
               OR state_row.publication_commit_intent_started_at
                    IS NOT NULL
               OR state_row.max_score_mutation_gate_token
                    IS NOT NULL
               OR latest_drop_candidate_sha256 IS NULL
               OR p_baseline_route_manifest_sha256 <>
                    latest_drop_candidate_sha256
               OR EXISTS (
                    SELECT 1
                    FROM scrape_log scrape
                    WHERE scrape.status = 'running')
               OR NOT EXISTS (
                    SELECT 1
                    FROM service_worker_status worker
                    WHERE worker.worker_key = 'scraper'
                      AND worker.status = 'offline'
                      AND worker.current_operation_json
                            IS NULL)
               OR restored_identity_count <> 1
               OR restored_row_count <>
                    restore_row.row_count
               OR attached_index_count <> 2
               OR current_index_inventory
                    #>> '{pk,indexOid}' <>
                    restore_row.restored_index_evidence
                        #>> '{pk,indexOid}'
               OR current_index_inventory
                    #>> '{pk,indexRelfilenode}' <>
                    restore_row.restored_index_evidence
                        #>> '{pk,indexRelfilenode}'
               OR current_index_inventory
                    #>> '{pk,indexName}' <>
                    restore_row.restored_index_evidence
                        #>> '{pk,indexName}'
               OR current_index_inventory
                    #>> '{score,indexOid}' <>
                    restore_row.restored_index_evidence
                        #>> '{score,indexOid}'
               OR current_index_inventory
                    #>> '{score,indexRelfilenode}' <>
                    restore_row.restored_index_evidence
                        #>> '{score,indexRelfilenode}'
               OR current_index_inventory
                    #>> '{score,indexName}' <>
                    restore_row.restored_index_evidence
                        #>> '{score,indexName}'
               OR EXISTS (
                    SELECT 1
                    FROM pg_class relation
                    WHERE relation.oid =
                            drop_row.child_oid)
               OR EXISTS (
                    SELECT 1
                    FROM pg_constraint constraint_row
                    WHERE constraint_row.conrelid =
                            drop_row.default_partition_oid
                      AND constraint_row.conname =
                            drop_row.durable_default_exclusion_constraint)
               OR default_row_count <> 0
               OR NOT EXISTS (
                    SELECT 1
                    FROM snapshot_generation_retention_holds
                        hold_row
                    WHERE hold_row.hold_id =
                            restore_row.hold_id
                      AND hold_row.released_at IS NULL)
               OR NOT EXISTS (
                    SELECT 1
                    FROM pg_trigger trigger_row
                    WHERE trigger_row.tgrelid =
                            restore_row.restored_child_oid
                      AND trigger_row.tgname =
                            'trg_sgr_' ||
                            restore_row.snapshot_id::TEXT ||
                            '_' ||
                            left(
                                restore_row.restore_operation_id,
                                12)
                      AND NOT trigger_row.tgisinternal
                      AND trigger_row.tgenabled = 'O'
                      AND trigger_row.tgfoid =
                            'public.fst_reject_snapshot_generation_quarantine_relation_mutation()'
                                ::regprocedure)
               OR EXISTS (
                    SELECT 1
                    FROM scrape_writer_failures failure
                    WHERE failure.instrument =
                            restore_row.instrument
                      AND failure.scrape_id =
                            restore_row.snapshot_id
                      AND failure.replayed_at IS NULL)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restored attestation state is unsafe.'
                    USING ERRCODE = '55000';
            END IF;
            INSERT INTO snapshot_generation_restore_attestations (
                restore_operation_id,
                publication_id,
                published_scrape_id,
                route_count,
                baseline_route_manifest_sha256,
                candidate_route_manifest_sha256,
                status_parity,
                semantic_json_parity,
                semantic_binary_parity,
                difference_count,
                route_parity_algorithm_id,
                route_semantic_evidence_sha256,
                database_evidence,
                evidence_sha256,
                evidence_tool_sha256,
                continuation_authorization_id,
                attested_by)
            VALUES (
                restore_row.restore_operation_id,
                p_publication_id,
                p_published_scrape_id,
                p_route_count,
                p_baseline_route_manifest_sha256,
                p_candidate_route_manifest_sha256,
                TRUE,
                TRUE,
                TRUE,
                0,
                'fst.route-parity.canonical-zip.v1',
                p_route_semantic_evidence_sha256,
                p_database_evidence,
                p_evidence_sha256,
                p_evidence_tool_sha256,
                p_continuation_authorization_id,
                p_attested_by);
            RETURN p_restore_operation_id;
        END
        $restore_attestation$;

        DROP FUNCTION IF EXISTS
            fst_finalize_snapshot_generation_restore(
                TEXT,
                TEXT,
                TEXT,
                JSONB)
            RESTRICT;

        CREATE OR REPLACE FUNCTION
            fst_finalize_snapshot_generation_restore(
                p_restore_operation_id TEXT,
                p_finalized_by TEXT,
                p_finalize_reference TEXT,
                p_finalization_evidence JSONB,
                p_evidence_tool_sha256 TEXT,
                p_continuation_authorization_id TEXT)
        RETURNS TEXT
        LANGUAGE plpgsql
        SECURITY INVOKER
        SET search_path = pg_catalog, public
        AS $restore_finalize$
        DECLARE
            restore_row
                snapshot_generation_restore_operations%ROWTYPE;
            attestation_row
                snapshot_generation_restore_attestations%ROWTYPE;
            authorization_row
                snapshot_generation_restore_continuation_authorizations%ROWTYPE;
            state_row scrape_publication_state%ROWTYPE;
            target_reference_count BIGINT;
            restored_row_count BIGINT;
            attached_index_count INTEGER;
            released_count INTEGER;
            current_index_inventory JSONB;
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
               OR NOT pg_try_advisory_xact_lock(2026083002)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore finalization lock chain is busy; retry in a new transaction.'
                    USING ERRCODE = '55P03';
            END IF;
            IF COALESCE(p_finalized_by, '') = ''
               OR COALESCE(p_finalize_reference, '') = ''
               OR p_evidence_tool_sha256
                    !~ '^[0-9a-f]{64}$'
               OR p_continuation_authorization_id
                    !~ '^[0-9a-f]{32}$'
               OR jsonb_typeof(p_finalization_evidence) <> 'object'
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore finalization arguments are invalid.'
                    USING ERRCODE = '22023';
            END IF;
            SELECT operation.*
            INTO STRICT restore_row
            FROM snapshot_generation_restore_operations operation
            WHERE operation.restore_operation_id =
                    p_restore_operation_id;
            SELECT attestation.*
            INTO STRICT attestation_row
            FROM snapshot_generation_restore_attestations
                attestation
            WHERE attestation.restore_operation_id =
                    restore_row.restore_operation_id
              AND attestation.evidence_tool_sha256 =
                    p_evidence_tool_sha256
              AND attestation.continuation_authorization_id =
                    p_continuation_authorization_id
              AND attestation.semantic_binary_parity
              AND attestation.route_parity_algorithm_id =
                    'fst.route-parity.canonical-zip.v1';
            SELECT continuation_auth.*
            INTO STRICT authorization_row
            FROM
                snapshot_generation_restore_continuation_authorizations
                    continuation_auth
            WHERE continuation_auth.restore_operation_id =
                    restore_row.restore_operation_id
              AND continuation_auth.continuation_authorization_id =
                    p_continuation_authorization_id
              AND continuation_auth.authorized_continuation_tool_sha256 =
                    p_evidence_tool_sha256
              AND continuation_auth.authorization_scope =
                    'confirm_attest_finalize'
              AND continuation_auth.restore_plan_digest =
                    restore_row.plan_digest
              AND continuation_auth.predecessor_authorization_id =
                    restore_row.authorization_id
              AND continuation_auth.predecessor_restore_tool_sha256 =
                    restore_row.executing_tool_sha256;
            IF p_finalize_reference IN (
                    restore_row.restore_reference,
                    authorization_row.approval_reference)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore finalization reference reuses prior evidence.'
                    USING ERRCODE = '55000';
            END IF;
            IF EXISTS (
                SELECT 1
                FROM snapshot_generation_restore_finalizations
                    finalization
                WHERE finalization.restore_operation_id =
                        restore_row.restore_operation_id
                  AND finalization.finalized_by =
                        p_finalized_by
                  AND finalization.finalize_reference =
                        p_finalize_reference
                  AND finalization.finalization_evidence =
                        p_finalization_evidence
                  AND finalization.evidence_tool_sha256 =
                        p_evidence_tool_sha256
                  AND finalization.continuation_authorization_id =
                        p_continuation_authorization_id)
            THEN
                RETURN p_restore_operation_id;
            END IF;
            IF EXISTS (
                SELECT 1
                FROM snapshot_generation_restore_finalizations
                    finalization
                WHERE finalization.restore_operation_id =
                        restore_row.restore_operation_id)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore finalization conflicts with existing evidence.'
                    USING ERRCODE = '55000';
            END IF;
            SELECT state.*
            INTO STRICT state_row
            FROM scrape_publication_state state
            WHERE state.id = TRUE;
            IF NOT EXISTS (
                    SELECT 1
                    FROM snapshot_generation_restore_attestations
                        attestation
                    WHERE attestation.restore_operation_id =
                            restore_row.restore_operation_id
                      AND attestation.publication_id =
                            state_row.current_publication_id
                      AND attestation.published_scrape_id =
                            state_row.published_scrape_id)
               OR state_row.public_reads_frozen
               OR state_row.working_publication_id IS NOT NULL
               OR state_row.publication_commit_intent_started_at
                    IS NOT NULL
               OR state_row.max_score_mutation_gate_token IS NOT NULL
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
               OR EXISTS (
                    SELECT 1
                    FROM scrape_log scrape
                    WHERE scrape.status = 'running')
               OR NOT EXISTS (
                    SELECT 1
                    FROM service_worker_status worker
                    WHERE worker.worker_key = 'scraper'
                      AND worker.status = 'offline'
                      AND worker.current_operation_json IS NULL)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore finalization publication state is unsafe.'
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
                        restore_row.instrument
                  AND snapshot_state.active_snapshot_id =
                        restore_row.snapshot_id
                UNION ALL
                SELECT 1
                FROM solo_current_projection_scope projection
                WHERE projection.instrument =
                        restore_row.instrument
                  AND projection.source_snapshot_id =
                        restore_row.snapshot_id
                UNION ALL
                SELECT 1
                FROM leaderboard_published_scope_source source
                WHERE source.instrument =
                        restore_row.instrument
                  AND source.source_snapshot_id =
                        restore_row.snapshot_id
                  AND source.published_scrape_id IN (
                        SELECT scrape_id
                        FROM named_publication_scrapes)
            )
            SELECT COUNT(*)::BIGINT
            INTO target_reference_count
            FROM target_roots;
            IF target_reference_count <> 0 THEN
                RAISE EXCEPTION
                    'Snapshot-generation restored target gained % live reference(s) before finalization.',
                    target_reference_count
                    USING ERRCODE = '55000';
            END IF;
            EXECUTE format(
                'LOCK TABLE ONLY %I.%I IN ACCESS EXCLUSIVE MODE',
                restore_row.child_schema,
                restore_row.child_relation);
            EXECUTE format(
                'SELECT COUNT(*)::BIGINT FROM ONLY %I.%I',
                restore_row.child_schema,
                restore_row.child_relation)
            INTO restored_row_count;
            SELECT COUNT(*)::INTEGER
            INTO attached_index_count
            FROM pg_index child_index
            JOIN pg_inherits child_index_inheritance
              ON child_index_inheritance.inhrelid =
                    child_index.indexrelid
            JOIN pg_index root_index
              ON root_index.indexrelid =
                    child_index_inheritance.inhparent
             AND root_index.indrelid = restore_row.root_oid
            JOIN pg_inherits root_index_inheritance
              ON root_index_inheritance.inhrelid =
                    root_index.indexrelid
            JOIN pg_class top_index_relation
              ON top_index_relation.oid =
                    root_index_inheritance.inhparent
            WHERE child_index.indrelid =
                    restore_row.restored_child_oid
              AND child_index.indisvalid
              AND child_index.indisready
              AND root_index.indisvalid
              AND root_index.indisready
              AND top_index_relation.relname IN (
                    'leaderboard_entries_snapshot_pkey',
                    'ix_les_snapshot_song_score');
            current_index_inventory :=
                fst_snapshot_generation_index_inventory(
                    restore_row.restored_child_oid,
                    restore_row.root_oid,
                    TRUE);
            IF NOT EXISTS (
                    SELECT 1
                    FROM pg_class child
                    JOIN pg_namespace namespace
                      ON namespace.oid = child.relnamespace
                    JOIN pg_inherits inheritance
                      ON inheritance.inhrelid = child.oid
                    WHERE child.oid =
                            restore_row.restored_child_oid
                      AND child.relfilenode::BIGINT =
                            restore_row.restored_child_relfilenode
                      AND namespace.nspname =
                            restore_row.child_schema
                      AND child.relname =
                            restore_row.child_relation
                      AND inheritance.inhparent =
                            restore_row.root_oid)
               OR NOT EXISTS (
                    SELECT 1
                    FROM pg_trigger trigger_row
                    WHERE trigger_row.tgrelid =
                            restore_row.restored_child_oid
                      AND trigger_row.tgname =
                            'trg_sgr_' ||
                            restore_row.snapshot_id::TEXT ||
                            '_' ||
                            left(
                                restore_row.restore_operation_id,
                                12)
                      AND NOT trigger_row.tgisinternal
                      AND trigger_row.tgenabled = 'O'
                      AND trigger_row.tgfoid =
                            'public.fst_reject_snapshot_generation_quarantine_relation_mutation()'
                                ::regprocedure)
               OR restored_row_count <> restore_row.row_count
               OR attached_index_count <> 2
               OR current_index_inventory
                    #>> '{pk,indexOid}' <>
                    restore_row.restored_index_evidence
                        #>> '{pk,indexOid}'
               OR current_index_inventory
                    #>> '{pk,indexRelfilenode}' <>
                    restore_row.restored_index_evidence
                        #>> '{pk,indexRelfilenode}'
               OR current_index_inventory
                    #>> '{pk,indexName}' <>
                    restore_row.restored_index_evidence
                        #>> '{pk,indexName}'
               OR current_index_inventory
                    #>> '{score,indexOid}' <>
                    restore_row.restored_index_evidence
                        #>> '{score,indexOid}'
               OR current_index_inventory
                    #>> '{score,indexRelfilenode}' <>
                    restore_row.restored_index_evidence
                        #>> '{score,indexRelfilenode}'
               OR current_index_inventory
                    #>> '{score,indexName}' <>
                    restore_row.restored_index_evidence
                        #>> '{score,indexName}'
               OR EXISTS (
                    SELECT 1
                    FROM scrape_writer_failures failure
                    WHERE failure.instrument =
                            restore_row.instrument
                      AND failure.scrape_id =
                            restore_row.snapshot_id
                      AND failure.replayed_at IS NULL)
            THEN
                RAISE EXCEPTION
                    'Snapshot-generation restored identity or writer-failure state changed before finalization.'
                    USING ERRCODE = '55000';
            END IF;
            EXECUTE format(
                'DROP TRIGGER %I ON %I.%I',
                'trg_sgr_' ||
                    restore_row.snapshot_id::TEXT || '_' ||
                    left(
                        restore_row.restore_operation_id,
                        12),
                restore_row.child_schema,
                restore_row.child_relation);
            UPDATE snapshot_generation_retention_holds
            SET released_by = p_finalized_by,
                released_at = clock_timestamp(),
                release_reason = p_finalize_reference
            WHERE hold_id = restore_row.hold_id
              AND released_at IS NULL;
            GET DIAGNOSTICS released_count = ROW_COUNT;
            IF released_count <> 1 THEN
                RAISE EXCEPTION
                    'Snapshot-generation restore hold release was not exact.'
                    USING ERRCODE = '55000';
            END IF;
            INSERT INTO
                snapshot_generation_restore_finalizations (
                    restore_operation_id,
                    finalized_by,
                    finalize_reference,
                    finalization_evidence,
                    evidence_tool_sha256,
                    continuation_authorization_id)
            VALUES (
                restore_row.restore_operation_id,
                p_finalized_by,
                p_finalize_reference,
                p_finalization_evidence,
                p_evidence_tool_sha256,
                p_continuation_authorization_id);
            RETURN p_restore_operation_id;
        END
        $restore_finalize$;

        DO $restore_authorization_function_acl$
        DECLARE
            function_identity TEXT;
        BEGIN
            FOR function_identity IN
            SELECT function_row.oid::regprocedure::TEXT
            FROM pg_proc function_row
            JOIN pg_namespace namespace
              ON namespace.oid = function_row.pronamespace
            WHERE namespace.nspname = 'public'
              AND function_row.proname IN (
                    'fst_authorize_snapshot_generation_restore_tool',
                    'fst_confirm_snapshot_generation_restore_tool_authorization',
                    'fst_authorize_snapshot_generation_restore_continuation',
                    'fst_confirm_snapshot_generation_restore_continuation_authorization')
            LOOP
                EXECUTE format(
                    'REVOKE ALL ON FUNCTION %s FROM PUBLIC',
                    function_identity);
            END LOOP;
        END
        $restore_authorization_function_acl$;

        REVOKE ALL ON FUNCTION
            fst_lock_snapshot_generation_for_drop(
                TEXT,
                BIGINT,
                BIGINT)
            FROM PUBLIC;
        DO $drop_function_acl$
        DECLARE
            function_identity TEXT;
        BEGIN
            FOR function_identity IN
            SELECT function_row.oid::regprocedure::TEXT
            FROM pg_proc function_row
            JOIN pg_namespace namespace
              ON namespace.oid = function_row.pronamespace
            WHERE namespace.nspname = 'public'
              AND function_row.proname =
                    'fst_drop_quarantined_snapshot_generation'
            LOOP
                EXECUTE format(
                    'REVOKE ALL ON FUNCTION %s FROM PUBLIC',
                    function_identity);
            END LOOP;
        END
        $drop_function_acl$;
        REVOKE ALL ON FUNCTION
            fst_record_snapshot_generation_drop_attestation(
                TEXT,
                TEXT,
                BIGINT,
                BIGINT,
                INTEGER,
                TEXT,
                TEXT,
                JSONB,
                TEXT,
                TEXT)
            FROM PUBLIC;
        REVOKE ALL ON FUNCTION
            fst_restore_snapshot_generation(
                TEXT,
                TEXT,
                TEXT,
                BIGINT,
                BIGINT,
                BIGINT,
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                JSONB,
                TEXT,
                TEXT,
                TEXT,
                TEXT,
                JSONB)
            FROM PUBLIC;
        REVOKE ALL ON FUNCTION
            fst_record_snapshot_generation_restore_attestation(
                TEXT,
                BIGINT,
                BIGINT,
                INTEGER,
                TEXT,
                TEXT,
                TEXT,
                JSONB,
                TEXT,
                TEXT,
                TEXT,
                TEXT)
            FROM PUBLIC;
        REVOKE ALL ON FUNCTION
            fst_finalize_snapshot_generation_restore(
                TEXT,
                TEXT,
                TEXT,
                JSONB,
                TEXT,
                TEXT)
            FROM PUBLIC;
        """;
}
