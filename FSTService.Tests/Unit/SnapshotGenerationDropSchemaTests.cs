using FSTService.Persistence;
using FSTService.Persistence.Maintenance;
using FSTService.Tests.Helpers;

namespace FSTService.Tests.Unit;

public sealed class SnapshotGenerationDropSchemaTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task SchemaIsAdditiveIdempotentAndImmutable()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);

        Assert.True(
            Scalar<bool>(
                """
                SELECT
                    to_regclass(
                        'public.snapshot_generation_drop_operations')
                        IS NOT NULL
                    AND to_regclass(
                        'public.snapshot_generation_drop_attestations')
                        IS NOT NULL
                    AND to_regclass(
                        'public.snapshot_generation_restore_tool_authorizations')
                        IS NOT NULL
                    AND to_regclass(
                        'public.snapshot_generation_restore_operations')
                        IS NOT NULL
                    AND to_regclass(
                        'public.snapshot_generation_restore_attestations')
                        IS NOT NULL
                    AND to_regclass(
                        'public.snapshot_generation_restore_finalizations')
                        IS NOT NULL
                    AND to_regclass(
                        'public.snapshot_generation_drop_evidence')
                        IS NOT NULL
                """));

        Assert.Equal(
            1,
            CountOccurrences(
                SnapshotGenerationDropSchema.Sql,
                "'DROP TABLE %I.%I RESTRICT'"));
        Assert.DoesNotContain(
            "DROP TABLE IF EXISTS",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "CASCADE",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "DROP INDEX",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            1,
            CountOccurrences(
                SnapshotGenerationDropSchema.Sql,
                "DROP FUNCTION IF EXISTS"));
        Assert.DoesNotContain(
            "'DROP SCHEMA",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "'DROP DATABASE",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            8,
            CountOccurrences(
                SnapshotGenerationDropSchema.Sql,
                "SECURITY INVOKER"));
        Assert.DoesNotContain(
            "SECURITY DEFINER",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GRANT ",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "p_pre_drop_route_count",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "p_pre_drop_status_parity",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "p_pre_drop_semantic_json_parity",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "p_pre_drop_difference_count",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ALTER TABLE %I.%I RENAME CONSTRAINT",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.Ordinal);

        Assert.Equal(
            7,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_trigger trigger_row
                WHERE trigger_row.tgrelid IN (
                        'snapshot_generation_drop_operations'
                            ::regclass,
                        'snapshot_generation_drop_attestations'
                            ::regclass,
                        'snapshot_generation_restore_tool_authorizations'
                            ::regclass,
                        'snapshot_generation_restore_operations'
                            ::regclass,
                        'snapshot_generation_restore_attestations'
                            ::regclass,
                        'snapshot_generation_restore_finalizations'
                            ::regclass,
                        'snapshot_generation_drop_evidence'
                            ::regclass)
                  AND (
                        trigger_row.tgname LIKE
                            'trg_reject_snapshot_generation_%_mutation'
                        OR trigger_row.tgname =
                            'trg_reject_sgr_tool_authorization_mutation')
                  AND NOT trigger_row.tgisinternal
                """));
        Assert.True(
            Scalar<bool>(
                """
                SELECT COUNT(*) = 8
                    AND NOT bool_or(function_row.prosecdef)
                FROM pg_proc function_row
                JOIN pg_namespace namespace
                  ON namespace.oid = function_row.pronamespace
                WHERE namespace.nspname = 'public'
                  AND function_row.proname IN (
                        'fst_lock_snapshot_generation_for_drop',
                        'fst_authorize_snapshot_generation_restore_tool',
                        'fst_confirm_snapshot_generation_restore_tool_authorization',
                        'fst_drop_quarantined_snapshot_generation',
                        'fst_record_snapshot_generation_drop_attestation',
                        'fst_restore_snapshot_generation',
                        'fst_record_snapshot_generation_restore_attestation',
                        'fst_finalize_snapshot_generation_restore')
                """));
        Assert.False(
            Scalar<bool>(
                """
                SELECT has_table_privilege(
                    'public',
                    'snapshot_generation_restore_tool_authorizations',
                    'SELECT, INSERT, UPDATE, DELETE')
                """));
        Assert.True(
            Scalar<bool>(
                """
                SELECT COUNT(*) = 8
                    AND bool_and(
                        NOT has_function_privilege(
                            'public',
                            function_row.oid,
                            'EXECUTE'))
                FROM pg_proc function_row
                JOIN pg_namespace namespace
                  ON namespace.oid =
                        function_row.pronamespace
                WHERE namespace.nspname = 'public'
                  AND function_row.proname IN (
                        'fst_lock_snapshot_generation_for_drop',
                        'fst_authorize_snapshot_generation_restore_tool',
                        'fst_confirm_snapshot_generation_restore_tool_authorization',
                        'fst_drop_quarantined_snapshot_generation',
                        'fst_record_snapshot_generation_drop_attestation',
                        'fst_restore_snapshot_generation',
                        'fst_record_snapshot_generation_restore_attestation',
                        'fst_finalize_snapshot_generation_restore')
                """));
    }

    [Fact]
    public async Task UpgradeRemovesLegacyRestoreFunctionOverload()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        Execute(
            """
            CREATE FUNCTION fst_restore_snapshot_generation(
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
                JSONB)
            RETURNS TEXT
            LANGUAGE sql
            AS 'SELECT ''legacy''::TEXT'
            """);
        Assert.Equal(
            2,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_proc function_row
                JOIN pg_namespace namespace
                  ON namespace.oid =
                        function_row.pronamespace
                WHERE namespace.nspname = 'public'
                  AND function_row.proname =
                        'fst_restore_snapshot_generation'
                """));

        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var currentOid = Scalar<long>(
            """
            SELECT function_row.oid::BIGINT
            FROM pg_proc function_row
            JOIN pg_namespace namespace
              ON namespace.oid =
                    function_row.pronamespace
            WHERE namespace.nspname = 'public'
              AND function_row.proname =
                    'fst_restore_snapshot_generation'
              AND function_row.pronargs = 21
            """);
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);

        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_proc function_row
                JOIN pg_namespace namespace
                  ON namespace.oid =
                        function_row.pronamespace
                WHERE namespace.nspname = 'public'
                  AND function_row.proname =
                        'fst_restore_snapshot_generation'
                """));
        Assert.Equal(
            currentOid,
            Scalar<long>(
                """
                SELECT function_row.oid::BIGINT
                FROM pg_proc function_row
                JOIN pg_namespace namespace
                  ON namespace.oid =
                        function_row.pronamespace
                WHERE namespace.nspname = 'public'
                  AND function_row.proname =
                        'fst_restore_snapshot_generation'
                  AND function_row.pronargs = 21
                """));
        Assert.False(
            Scalar<bool>(
                """
                SELECT has_function_privilege(
                    'public',
                    function_row.oid,
                    'EXECUTE')
                FROM pg_proc function_row
                JOIN pg_namespace namespace
                  ON namespace.oid =
                        function_row.pronamespace
                WHERE namespace.nspname = 'public'
                  AND function_row.proname =
                        'fst_restore_snapshot_generation'
                """));
    }

    [Fact]
    public async Task EmptyInitialSchemaUpgradesToCurrentShapeIdempotently()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        Execute(
            """
            ALTER TABLE snapshot_generation_drop_operations
                DROP CONSTRAINT
                    ck_snapshot_generation_drop_hashes,
                DROP CONSTRAINT
                    ck_snapshot_generation_drop_identity,
                DROP COLUMN semantic_projection_version,
                DROP COLUMN rehearsal_catalog_sha256,
                DROP COLUMN catalog_sha256,
                DROP COLUMN
                    rehearsal_semantic_catalog_sha256,
                DROP COLUMN semantic_catalog_sha256,
                DROP COLUMN
                    rehearsal_logical_index_shape_sha256,
                DROP COLUMN logical_index_shape_sha256,
                DROP COLUMN
                    rehearsal_physical_index_inventory_sha256,
                DROP COLUMN
                    physical_index_inventory_sha256;
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
                        AND server_version_num / 10000 = 17);

            ALTER TABLE snapshot_generation_restore_operations
                DROP CONSTRAINT
                    ck_snapshot_generation_restore_hashes,
                DROP CONSTRAINT
                    ck_snapshot_generation_restore_identity,
                DROP CONSTRAINT
                    fk_snapshot_generation_restore_tool_authorization,
                DROP CONSTRAINT
                    ux_snapshot_generation_restore_tool_authorization_consumption,
                DROP COLUMN semantic_catalog_sha256,
                DROP COLUMN logical_index_shape_sha256,
                DROP COLUMN archived_index_names,
                DROP COLUMN restored_index_evidence,
                DROP COLUMN pinned_tool_sha256,
                DROP COLUMN executing_tool_sha256,
                DROP COLUMN authorization_id;
            ALTER TABLE snapshot_generation_restore_operations
                ADD CONSTRAINT
                    ck_snapshot_generation_restore_hashes
                    CHECK (
                        plan_digest ~ '^[0-9a-f]{64}$'
                        AND archive_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND archive_sha256 ~ '^[0-9a-f]{64}$'
                        AND recovery_bundle_manifest_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND row_fingerprint_sha256
                            ~ '^[0-9a-f]{64}$'
                        AND logical_catalog_sha256
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
                        AND jsonb_typeof(restore_evidence) =
                            'object');
            """);

        Assert.Equal(
            16,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM (
                    SELECT unnest(ARRAY[
                        'semantic_projection_version',
                        'rehearsal_catalog_sha256',
                        'catalog_sha256',
                        'rehearsal_semantic_catalog_sha256',
                        'semantic_catalog_sha256',
                        'rehearsal_logical_index_shape_sha256',
                        'logical_index_shape_sha256',
                        'rehearsal_physical_index_inventory_sha256',
                        'physical_index_inventory_sha256'
                    ]) AS column_name,
                    'snapshot_generation_drop_operations'
                        ::regclass AS relation_oid
                    UNION ALL
                    SELECT unnest(ARRAY[
                        'semantic_catalog_sha256',
                        'logical_index_shape_sha256',
                        'archived_index_names',
                        'restored_index_evidence',
                        'pinned_tool_sha256',
                        'executing_tool_sha256',
                        'authorization_id'
                    ]),
                    'snapshot_generation_restore_operations'
                        ::regclass
                ) expected
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM pg_attribute attribute
                    WHERE attribute.attrelid =
                            expected.relation_oid
                      AND attribute.attname =
                            expected.column_name
                      AND attribute.attnum > 0
                      AND NOT attribute.attisdropped)
                """));

        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var upgradedConstraintOids =
            Scalar<string>(
                """
                SELECT string_agg(
                    constraint_row.oid::TEXT,
                    ','
                    ORDER BY
                        constraint_row.conrelid,
                        constraint_row.conname)
                FROM pg_constraint constraint_row
                WHERE (
                        constraint_row.conrelid =
                            'snapshot_generation_drop_operations'
                                ::regclass
                        AND constraint_row.conname IN (
                            'ck_snapshot_generation_drop_hashes',
                            'ck_snapshot_generation_drop_identity'))
                   OR (
                        constraint_row.conrelid =
                            'snapshot_generation_restore_operations'
                                ::regclass
                        AND constraint_row.conname IN (
                            'ck_snapshot_generation_restore_hashes',
                            'ck_snapshot_generation_restore_identity',
                            'fk_snapshot_generation_restore_tool_authorization',
                            'ux_snapshot_generation_restore_tool_authorization_consumption'))
                """);
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        Assert.Equal(
            upgradedConstraintOids,
            Scalar<string>(
                """
                SELECT string_agg(
                    constraint_row.oid::TEXT,
                    ','
                    ORDER BY
                        constraint_row.conrelid,
                        constraint_row.conname)
                FROM pg_constraint constraint_row
                WHERE (
                        constraint_row.conrelid =
                            'snapshot_generation_drop_operations'
                                ::regclass
                        AND constraint_row.conname IN (
                            'ck_snapshot_generation_drop_hashes',
                            'ck_snapshot_generation_drop_identity'))
                   OR (
                        constraint_row.conrelid =
                            'snapshot_generation_restore_operations'
                                ::regclass
                        AND constraint_row.conname IN (
                            'ck_snapshot_generation_restore_hashes',
                            'ck_snapshot_generation_restore_identity',
                            'fk_snapshot_generation_restore_tool_authorization',
                            'ux_snapshot_generation_restore_tool_authorization_consumption'))
                """));

        Assert.Equal(
            15,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM (
                    VALUES
                        ('snapshot_generation_drop_operations'
                            ::regclass,
                         'semantic_projection_version',
                         'integer'::regtype),
                        ('snapshot_generation_drop_operations'
                            ::regclass,
                         'rehearsal_catalog_sha256',
                         'text'::regtype),
                        ('snapshot_generation_drop_operations'
                            ::regclass,
                         'catalog_sha256',
                         'text'::regtype),
                        ('snapshot_generation_drop_operations'
                            ::regclass,
                         'rehearsal_semantic_catalog_sha256',
                         'text'::regtype),
                        ('snapshot_generation_drop_operations'
                            ::regclass,
                         'semantic_catalog_sha256',
                         'text'::regtype),
                        ('snapshot_generation_drop_operations'
                            ::regclass,
                         'rehearsal_logical_index_shape_sha256',
                         'text'::regtype),
                        ('snapshot_generation_drop_operations'
                            ::regclass,
                         'logical_index_shape_sha256',
                         'text'::regtype),
                        ('snapshot_generation_drop_operations'
                            ::regclass,
                         'rehearsal_physical_index_inventory_sha256',
                         'text'::regtype),
                        ('snapshot_generation_drop_operations'
                            ::regclass,
                         'physical_index_inventory_sha256',
                         'text'::regtype),
                        ('snapshot_generation_restore_operations'
                            ::regclass,
                         'semantic_catalog_sha256',
                         'text'::regtype),
                        ('snapshot_generation_restore_operations'
                            ::regclass,
                         'logical_index_shape_sha256',
                         'text'::regtype),
                        ('snapshot_generation_restore_operations'
                            ::regclass,
                         'archived_index_names',
                         'jsonb'::regtype),
                        ('snapshot_generation_restore_operations'
                            ::regclass,
                         'restored_index_evidence',
                         'jsonb'::regtype),
                        ('snapshot_generation_restore_operations'
                            ::regclass,
                         'pinned_tool_sha256',
                         'text'::regtype),
                        ('snapshot_generation_restore_operations'
                            ::regclass,
                         'executing_tool_sha256',
                         'text'::regtype)
                ) expected(
                    relation_oid,
                    column_name,
                    type_oid)
                JOIN pg_attribute attribute
                  ON attribute.attrelid =
                        expected.relation_oid
                 AND attribute.attname =
                        expected.column_name
                 AND attribute.attnum > 0
                 AND NOT attribute.attisdropped
                 AND attribute.atttypid =
                        expected.type_oid
                 AND attribute.attnotnull
                """));
        Assert.True(
            Scalar<bool>(
                """
                SELECT
                    attribute.atttypid =
                        'text'::regtype
                    AND NOT attribute.attnotnull
                FROM pg_attribute attribute
                WHERE attribute.attrelid =
                        'snapshot_generation_restore_operations'
                            ::regclass
                  AND attribute.attname =
                        'authorization_id'
                  AND attribute.attnum > 0
                  AND NOT attribute.attisdropped
                """));
        Assert.True(
            Scalar<bool>(
                """
                WITH expected(table_name, column_name) AS (
                    SELECT
                        'snapshot_generation_drop_attestations',
                        unnest(ARRAY[
                            'attestation_id',
                            'drop_operation_id',
                            'stage',
                            'publication_id',
                            'published_scrape_id',
                            'route_count',
                            'baseline_route_manifest_sha256',
                            'candidate_route_manifest_sha256',
                            'status_parity',
                            'semantic_json_parity',
                            'difference_count',
                            'database_evidence',
                            'evidence_sha256',
                            'attested_by',
                            'attested_at'
                        ])
                    UNION ALL
                    SELECT
                        'snapshot_generation_restore_attestations',
                        unnest(ARRAY[
                            'restore_operation_id',
                            'publication_id',
                            'published_scrape_id',
                            'route_count',
                            'baseline_route_manifest_sha256',
                            'candidate_route_manifest_sha256',
                            'status_parity',
                            'semantic_json_parity',
                            'difference_count',
                            'database_evidence',
                            'evidence_sha256',
                            'attested_by',
                            'attested_at',
                            'finalized_at'
                        ])
                    UNION ALL
                    SELECT
                        'snapshot_generation_restore_finalizations',
                        unnest(ARRAY[
                            'restore_operation_id',
                            'finalized_by',
                            'finalize_reference',
                            'finalization_evidence',
                            'finalized_at'
                        ])
                    UNION ALL
                    SELECT
                        'snapshot_generation_drop_evidence',
                        unnest(ARRAY[
                            'evidence_id',
                            'drop_operation_id',
                            'sequence',
                            'phase',
                            'kind',
                            'payload',
                            'previous_hash',
                            'current_hash',
                            'created_at'
                        ])
                ),
                actual AS (
                    SELECT
                        columns.table_name,
                        columns.column_name
                    FROM information_schema.columns
                        columns
                    WHERE columns.table_schema = 'public'
                      AND columns.table_name IN (
                            'snapshot_generation_drop_attestations',
                            'snapshot_generation_restore_attestations',
                            'snapshot_generation_restore_finalizations',
                            'snapshot_generation_drop_evidence')
                )
                SELECT
                    NOT EXISTS (
                        SELECT * FROM expected
                        EXCEPT
                        SELECT * FROM actual)
                    AND NOT EXISTS (
                        SELECT * FROM actual
                        EXCEPT
                        SELECT * FROM expected)
                """));
        Assert.True(
            Scalar<bool>(
                """
                SELECT COUNT(*) = 6
                    AND bool_and(
                        constraint_row.convalidated)
                FROM pg_constraint constraint_row
                WHERE (
                        constraint_row.conrelid =
                            'snapshot_generation_drop_operations'
                                ::regclass
                        AND constraint_row.conname IN (
                            'ck_snapshot_generation_drop_hashes',
                            'ck_snapshot_generation_drop_identity'))
                   OR (
                        constraint_row.conrelid =
                            'snapshot_generation_restore_operations'
                                ::regclass
                        AND constraint_row.conname IN (
                            'ck_snapshot_generation_restore_hashes',
                            'ck_snapshot_generation_restore_identity',
                            'fk_snapshot_generation_restore_tool_authorization',
                            'ux_snapshot_generation_restore_tool_authorization_consumption'))
                """));
        Assert.Equal(
            9,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_constraint constraint_row
                WHERE constraint_row.convalidated
                  AND (
                        (
                            constraint_row.conrelid =
                                'snapshot_generation_drop_attestations'
                                    ::regclass
                            AND constraint_row.conname IN (
                                'ck_snapshot_generation_drop_attestation_stage',
                                'ck_snapshot_generation_drop_attestation_values',
                                'ck_snapshot_generation_drop_attestation_hashes',
                                'ux_snapshot_generation_drop_attestation'))
                        OR (
                            constraint_row.conrelid =
                                'snapshot_generation_restore_attestations'
                                    ::regclass
                            AND constraint_row.conname =
                                'ck_snapshot_generation_restore_attestation')
                        OR (
                            constraint_row.conrelid =
                                'snapshot_generation_restore_finalizations'
                                    ::regclass
                            AND constraint_row.conname =
                                'ck_snapshot_generation_restore_finalize')
                        OR (
                            constraint_row.conrelid =
                                'snapshot_generation_drop_evidence'
                                    ::regclass
                            AND constraint_row.conname IN (
                                'ux_snapshot_generation_drop_evidence_sequence',
                                'ck_snapshot_generation_drop_evidence_values',
                                'ck_snapshot_generation_drop_evidence_hashes')))
                """));
        Assert.True(
            Scalar<bool>(
                """
                SELECT
                    pg_get_expr(
                        hashes.conbin,
                        hashes.conrelid,
                        TRUE)
                        ~ '\mrehearsal_catalog_sha256\M'
                    AND pg_get_expr(
                        hashes.conbin,
                        hashes.conrelid,
                        TRUE)
                        ~ '\mphysical_index_inventory_sha256\M'
                    AND pg_get_expr(
                        identity_row.conbin,
                        identity_row.conrelid,
                        TRUE)
                        ~ '\msemantic_projection_version\M'
                    AND pg_get_expr(
                        identity_row.conbin,
                        identity_row.conrelid,
                        TRUE)
                        LIKE
                            '%semantic_projection_version = 1%'
                FROM pg_constraint hashes
                CROSS JOIN pg_constraint identity_row
                WHERE hashes.conrelid =
                        'snapshot_generation_drop_operations'
                            ::regclass
                  AND hashes.conname =
                        'ck_snapshot_generation_drop_hashes'
                  AND identity_row.conrelid =
                        hashes.conrelid
                  AND identity_row.conname =
                        'ck_snapshot_generation_drop_identity'
                """));
        Assert.Equal(
            8,
            Scalar<int>(
                """
                WITH expression AS (
                    SELECT pg_get_expr(
                        constraint_row.conbin,
                        constraint_row.conrelid,
                        TRUE) AS value
                    FROM pg_constraint constraint_row
                    WHERE constraint_row.conrelid =
                            'snapshot_generation_drop_operations'
                                ::regclass
                      AND constraint_row.conname =
                            'ck_snapshot_generation_drop_hashes'
                )
                SELECT COUNT(*)::INTEGER
                FROM unnest(ARRAY[
                    'rehearsal_catalog_sha256',
                    'catalog_sha256',
                    'rehearsal_semantic_catalog_sha256',
                    'semantic_catalog_sha256',
                    'rehearsal_logical_index_shape_sha256',
                    'logical_index_shape_sha256',
                    'rehearsal_physical_index_inventory_sha256',
                    'physical_index_inventory_sha256'
                ]) required(column_name)
                CROSS JOIN expression
                WHERE expression.value ~ (
                    '\m' || required.column_name || '\M')
                """));
        Assert.True(
            Scalar<bool>(
                """
                SELECT
                    pg_get_expr(
                        hashes.conbin,
                        hashes.conrelid,
                        TRUE)
                        ~ '\msemantic_catalog_sha256\M'
                    AND pg_get_expr(
                        hashes.conbin,
                        hashes.conrelid,
                        TRUE)
                        ~ '\mlogical_index_shape_sha256\M'
                    AND pg_get_expr(
                        identity_row.conbin,
                        identity_row.conrelid,
                        TRUE)
                        ~ '\marchived_index_names\M'
                    AND pg_get_expr(
                        identity_row.conbin,
                        identity_row.conrelid,
                        TRUE)
                        ~ '\mrestored_index_evidence\M'
                FROM pg_constraint hashes
                CROSS JOIN pg_constraint identity_row
                WHERE hashes.conrelid =
                        'snapshot_generation_restore_operations'
                            ::regclass
                  AND hashes.conname =
                        'ck_snapshot_generation_restore_hashes'
                  AND identity_row.conrelid =
                        hashes.conrelid
                  AND identity_row.conname =
                        'ck_snapshot_generation_restore_identity'
                """));
        Assert.Equal(
            8,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_proc function_row
                JOIN pg_namespace namespace
                  ON namespace.oid =
                        function_row.pronamespace
                WHERE namespace.nspname = 'public'
                  AND function_row.proname IN (
                        'fst_lock_snapshot_generation_for_drop',
                        'fst_authorize_snapshot_generation_restore_tool',
                        'fst_confirm_snapshot_generation_restore_tool_authorization',
                        'fst_drop_quarantined_snapshot_generation',
                        'fst_record_snapshot_generation_drop_attestation',
                        'fst_restore_snapshot_generation',
                        'fst_record_snapshot_generation_restore_attestation',
                        'fst_finalize_snapshot_generation_restore')
                """));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT (
                    (SELECT COUNT(*)
                     FROM snapshot_generation_drop_operations)
                    + (SELECT COUNT(*)
                       FROM snapshot_generation_drop_attestations)
                    + (SELECT COUNT(*)
                       FROM snapshot_generation_restore_tool_authorizations)
                    + (SELECT COUNT(*)
                       FROM snapshot_generation_restore_operations)
                    + (SELECT COUNT(*)
                       FROM snapshot_generation_restore_attestations)
                    + (SELECT COUNT(*)
                       FROM snapshot_generation_restore_finalizations)
                    + (SELECT COUNT(*)
                       FROM snapshot_generation_drop_evidence)
                    )::INTEGER
                """));
    }

    [Fact]
    public async Task FiveCycleGateIsTransitivelyPreservedForDrop()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);

        Assert.Contains(
            "accepted_cycle_count <> 5",
            SnapshotGenerationQuarantineSchema.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "The five-cycle gate is preserved transitively",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "cycle_row.cycle_id IS DISTINCT FROM",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_trigger trigger_row
                WHERE trigger_row.tgrelid IN (
                        'snapshot_generation_retention_cycles'
                            ::regclass,
                        'snapshot_generation_retention_observations'
                            ::regclass)
                  AND trigger_row.tgname IN (
                        'trg_reject_snapshot_generation_retention_cycles_mutation',
                        'trg_reject_snapshot_generation_retention_observations_mutation')
                  AND NOT trigger_row.tgisinternal
                """));
    }

    private static int CountOccurrences(
        string value,
        string token)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(
                   token,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }
        return count;
    }

    private T Scalar<T>(string sql)
    {
        using var connection = _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private void Execute(string sql)
    {
        using var connection =
            _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
