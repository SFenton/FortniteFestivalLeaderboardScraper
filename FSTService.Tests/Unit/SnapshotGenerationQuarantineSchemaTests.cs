using System.Diagnostics;
using FSTService.Persistence;
using FSTService.Persistence.Maintenance;
using FSTService.Tests.Helpers;
using FstSnapshotGenerationQuarantine;
using FstSnapshotGenerationRestoreAuthorization;
using FstSnapshotGenerationRestoreContinuation;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

namespace FSTService.Tests.Unit;

public sealed class SnapshotGenerationQuarantineSchemaTests
    : IDisposable
{
    private const string OperationId =
        "0123456789abcdef0123456789abcdef";
    private const string PlanDigest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string QuarantineRelation =
        "sgq_pc_1005_0123456789ab";
    private const string OriginalRelation =
        "leaderboard_entries_snapshot_pro_cymbals_s1005";

    private readonly InMemoryMetaDatabase _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task SchemaIsIdempotentAndHasNoDropPath()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);

        Assert.DoesNotContain(
            "DROP TABLE",
            SnapshotGenerationQuarantineSchema.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "DROP INDEX",
            SnapshotGenerationQuarantineSchema.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "CASCADE",
            SnapshotGenerationQuarantineSchema.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "DETACH PARTITION",
            SnapshotGenerationQuarantineSchema.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ATTACH PARTITION",
            SnapshotGenerationQuarantineSchema.Sql,
            StringComparison.Ordinal);
        Assert.True(
            Scalar<bool>(
                """
                SELECT
                    to_regnamespace(
                        'fst_snapshot_quarantine') IS NOT NULL
                    AND to_regclass(
                        'public.snapshot_generation_quarantine_operations')
                        IS NOT NULL
                    AND to_regclass(
                        'public.snapshot_generation_quarantine_reattachments')
                        IS NOT NULL
                    AND to_regclass(
                        'public.snapshot_generation_quarantine_attestations')
                        IS NOT NULL
                """));
    }

    [Fact]
    public async Task QuarantineAndReattachAreAtomicAndPreserveIdentity()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        var indexIdentityBefore =
            LoadIndexIdentity(
                "public",
                OriginalRelation);

        var result = ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));

        Assert.Equal(OperationId, result);
        Assert.False(RelationExists("public", OriginalRelation));
        Assert.True(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
        Assert.Equal(
            identity.ChildOid,
            RelationOid(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
        Assert.Equal(
            identity.ChildRelfilenode,
            RelationRelfilenode(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
        var privateIndexIdentity =
            LoadIndexIdentity(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation);
        Assert.Equal(
            indexIdentityBefore
                .OrderBy(item => item.Role)
                .Select(item =>
                    (item.Role,
                     item.Oid,
                     item.Relfilenode)),
            privateIndexIdentity
                .OrderBy(item => item.Role)
                .Select(item =>
                    (item.Role,
                     item.Oid,
                     item.Relfilenode)));
        Assert.Equal(
            [
                "sgqi_0123456789abcdef0123456789abcdef_pk",
                "sgqi_0123456789abcdef0123456789abcdef_score",
            ],
            privateIndexIdentity
                .OrderBy(item => item.Role)
                .Select(item => item.Name)
                .ToArray());
        Assert.Equal(
            2,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_quarantine_index_renames
                WHERE operation_id =
                    '0123456789abcdef0123456789abcdef'
                  AND source_phase = 'quarantine'
                  AND semantic_before =
                        semantic_after
                  AND semantic_before_sha256 =
                        semantic_after_sha256
                """));
        Assert.Equal(
            "sgqi_0123456789abcdef0123456789abcdef_pk",
            Scalar<string>(
                """
                SELECT conname
                FROM pg_constraint
                WHERE conrelid =
                        'fst_snapshot_quarantine.sgq_pc_1005_0123456789ab'
                            ::regclass
                  AND contype = 'p'
                """));
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_quarantine_operations
                WHERE operation_id =
                    '0123456789abcdef0123456789abcdef'
                """));
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_retention_holds
                WHERE instrument = 'Solo_PeripheralCymbals'
                  AND snapshot_id = 1005
                  AND hold_kind = 'retention_in_flight'
                  AND released_at IS NULL
                """));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_inherits
                WHERE inhrelid =
                    'fst_snapshot_quarantine.sgq_pc_1005_0123456789ab'
                        ::regclass
                """));
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_constraint
                WHERE conrelid =
                        'public.leaderboard_entries_snapshot_pro_cymbals_default'
                            ::regclass
                  AND conname =
                        'ck_sgq_default_1005_0123456789ab'
                """));
        Assert.True(
            Scalar<bool>(
                """
                SELECT
                    constraint_row.convalidated
                    AND regexp_replace(
                        pg_get_expr(
                            constraint_row.conbin,
                            constraint_row.conrelid,
                            TRUE),
                        '[()[:space:]]',
                        '',
                        'g') = 'snapshot_id=1005'
                FROM pg_constraint constraint_row
                WHERE constraint_row.conrelid =
                        'fst_snapshot_quarantine.sgq_pc_1005_0123456789ab'
                            ::regclass
                  AND constraint_row.conname =
                        'ck_sgq_1005_0123456789ab'
                """));
        var defaultWrite = Assert.Throws<PostgresException>(
            () => Execute(
                """
                INSERT INTO leaderboard_entries_snapshot (
                    snapshot_id,
                    song_id,
                    instrument,
                    account_id,
                    score,
                    source,
                    first_seen_at,
                    last_updated_at)
                VALUES (
                    1005,
                    'song-default-write',
                    'Solo_PeripheralCymbals',
                    'account-default-write',
                    1,
                    'scrape',
                    now(),
                    now())
                """));
        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            defaultWrite.SqlState);

        var rowMutation = Assert.Throws<PostgresException>(
            () => Execute(
                """
                UPDATE
                    fst_snapshot_quarantine.sgq_pc_1005_0123456789ab
                SET score = score
                """));
        Assert.Equal("55000", rowMutation.SqlState);
        var evidenceMutation = Assert.Throws<PostgresException>(
            () => Execute(
                """
                UPDATE snapshot_generation_quarantine_operations
                SET approved_by = 'changed'
                WHERE operation_id =
                    '0123456789abcdef0123456789abcdef'
                """));
        Assert.Equal("55000", evidenceMutation.SqlState);
        var renameEvidenceMutation =
            Assert.Throws<PostgresException>(
                () => Execute(
                    """
                    UPDATE snapshot_generation_quarantine_index_renames
                    SET new_index_name =
                            new_index_name || '_tampered'
                    WHERE operation_id =
                            '0123456789abcdef0123456789abcdef'
                      AND index_role = 'pk'
                    """));
        Assert.Equal(
            "55000",
            renameEvidenceMutation.SqlState);

        RecordAttestation(
            "quarantined",
            baselineHashCharacter: '5',
            candidateHashCharacter: '8');
        RecordAttestation(
            "soak",
            baselineHashCharacter: '8',
            candidateHashCharacter: '9');

        var reattached = ExecuteScalar<string>(
            """
            SELECT fst_reattach_snapshot_generation(
                @operationId,
                @planDigest,
                'test-operator',
                'test-rollback',
                '{}'::jsonb)
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "operationId",
                    OperationId);
                command.Parameters.AddWithValue(
                    "planDigest",
                    PlanDigest);
            });

        Assert.Equal(OperationId, reattached);
        Assert.True(RelationExists("public", OriginalRelation));
        Assert.False(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
        Assert.Equal(
            identity.ChildOid,
            RelationOid("public", OriginalRelation));
        Assert.Equal(
            identity.ChildRelfilenode,
            RelationRelfilenode("public", OriginalRelation));
        var reattachedIndexIdentity =
            LoadIndexIdentity(
                "public",
                OriginalRelation);
        Assert.Equal(
            privateIndexIdentity
                .OrderBy(item => item.Role)
                .Select(item =>
                    (item.Role,
                     item.Oid,
                     item.Relfilenode,
                     item.Name)),
            reattachedIndexIdentity
                .OrderBy(item => item.Role)
                .Select(item =>
                    (item.Role,
                     item.Oid,
                     item.Relfilenode,
                     item.Name)));
        Assert.Equal(
            identity.RootOid,
            Scalar<long>(
                """
                SELECT inhparent::BIGINT
                FROM pg_inherits
                WHERE inhrelid =
                    'public.leaderboard_entries_snapshot_pro_cymbals_s1005'
                        ::regclass
                """));
        Assert.Equal(
            2,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_index child_index
                JOIN pg_inherits child_inheritance
                  ON child_inheritance.inhrelid =
                        child_index.indexrelid
                JOIN pg_index root_index
                  ON root_index.indexrelid =
                        child_inheritance.inhparent
                 AND root_index.indrelid =
                        'public.leaderboard_entries_snapshot_pro_cymbals'
                            ::regclass
                JOIN pg_inherits root_inheritance
                  ON root_inheritance.inhrelid =
                        root_index.indexrelid
                JOIN pg_class top_index
                  ON top_index.oid =
                        root_inheritance.inhparent
                WHERE child_index.indrelid =
                        'public.leaderboard_entries_snapshot_pro_cymbals_s1005'
                            ::regclass
                  AND child_index.indisvalid
                  AND child_index.indisready
                  AND root_index.indisvalid
                  AND root_index.indisready
                  AND top_index.relname IN (
                        'leaderboard_entries_snapshot_pkey',
                        'ix_les_snapshot_song_score')
                """));
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_quarantine_reattachments
                WHERE operation_id =
                    '0123456789abcdef0123456789abcdef'
                """));
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_retention_holds
                WHERE instrument = 'Solo_PeripheralCymbals'
                  AND snapshot_id = 1005
                  AND hold_kind = 'retention_in_flight'
                  AND released_at IS NOT NULL
                """));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_constraint
                WHERE conrelid =
                        'public.leaderboard_entries_snapshot_pro_cymbals_s1005'
                            ::regclass
                  AND conname =
                        'ck_sgq_1005_0123456789ab'
                """));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_constraint
                WHERE conrelid =
                        'public.leaderboard_entries_snapshot_pro_cymbals_default'
                            ::regclass
                  AND conname =
                        'ck_sgq_default_1005_0123456789ab'
                """));
    }

    [Fact]
    public async Task ReattachSurvivesFreedIndexNameReuse()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        var originalScoreName =
            Assert.Single(
                LoadIndexIdentity(
                    "public",
                    OriginalRelation),
                index => index.Role == "score")
                .Name;
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));

        ExecuteScalar<string>(
            """
            SELECT ensure_leaderboard_snapshot_generation_partition(
                'Solo_Guitar',
                1006)
            """);
        var newScoreName =
            Assert.Single(
                LoadIndexIdentity(
                    "public",
                    "leaderboard_entries_snapshot_solo_guitar_s1006"),
                index => index.Role == "score")
                .Name;
        Assert.Equal(originalScoreName, newScoreName);

        RecordAttestation(
            "quarantined",
            baselineHashCharacter: '5',
            candidateHashCharacter: '8');
        RecordAttestation(
            "soak",
            baselineHashCharacter: '8',
            candidateHashCharacter: '9');
        ExecuteScalar<string>(
            """
            SELECT fst_reattach_snapshot_generation(
                @operationId,
                @planDigest,
                'test-operator',
                'rotation-collision-recovery',
                '{}'::jsonb)
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "operationId",
                    OperationId);
                command.Parameters.AddWithValue(
                    "planDigest",
                    PlanDigest);
            });

        Assert.Equal(
            identity.ChildOid,
            RelationOid("public", OriginalRelation));
        Assert.Equal(
            2,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_inherits inheritance
                JOIN pg_index child_index
                  ON child_index.indexrelid =
                        inheritance.inhrelid
                WHERE child_index.indrelid =
                        'public.leaderboard_entries_snapshot_pro_cymbals_s1005'
                            ::regclass
                """));
    }

    [Fact]
    public async Task ReattachRepairsPrePatchIndexNames()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        var oldPkName = Scalar<string>(
            """
            SELECT old_index_name
            FROM snapshot_generation_quarantine_index_renames
            WHERE operation_id =
                    '0123456789abcdef0123456789abcdef'
              AND index_role = 'pk'
            """);
        var oldScoreName = Scalar<string>(
            """
            SELECT old_index_name
            FROM snapshot_generation_quarantine_index_renames
            WHERE operation_id =
                    '0123456789abcdef0123456789abcdef'
              AND index_role = 'score'
            """);
        Execute(
            $"""
            ALTER INDEX
                fst_snapshot_quarantine.sgqi_0123456789abcdef0123456789abcdef_pk
                RENAME TO {oldPkName};
            ALTER INDEX
                fst_snapshot_quarantine.sgqi_0123456789abcdef0123456789abcdef_score
                RENAME TO {oldScoreName};
            SET session_replication_role = replica;
            DELETE FROM snapshot_generation_quarantine_index_renames
            WHERE operation_id =
                    '0123456789abcdef0123456789abcdef';
            SET session_replication_role = origin;
            CREATE TABLE public.reattach_name_collision (
                value INTEGER NOT NULL);
            CREATE INDEX {oldScoreName}
                ON public.reattach_name_collision (value);
            """);
        var unrelatedOid =
            RelationOid("public", oldScoreName);
        RecordAttestation(
            "quarantined",
            baselineHashCharacter: '5',
            candidateHashCharacter: '8');
        RecordAttestation(
            "soak",
            baselineHashCharacter: '8',
            candidateHashCharacter: '9');

        ExecuteScalar<string>(
            """
            SELECT fst_reattach_snapshot_generation(
                @operationId,
                @planDigest,
                'test-operator',
                'pre-patch-repair',
                '{}'::jsonb)
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "operationId",
                    OperationId);
                command.Parameters.AddWithValue(
                    "planDigest",
                    PlanDigest);
            });

        Assert.Equal(
            unrelatedOid,
            RelationOid("public", oldScoreName));
        Assert.Equal(
            2,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_quarantine_index_renames
                WHERE operation_id =
                    '0123456789abcdef0123456789abcdef'
                  AND source_phase = 'reattach_repair'
                """));
    }

    [Fact]
    public async Task QuarantineSurvivesPrivateDestinationIndexCollisions()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        var originalIndexes =
            LoadIndexIdentity(
                "public",
                OriginalRelation);
        var oldPkName = Assert.Single(
            originalIndexes,
            index => index.Role == "pk").Name;
        var oldScoreName = Assert.Single(
            originalIndexes,
            index => index.Role == "score").Name;
        Execute(
            $"""
            CREATE TABLE
                fst_snapshot_quarantine.destination_collision (
                    id INTEGER NOT NULL,
                    score INTEGER NOT NULL);
            CREATE UNIQUE INDEX {oldPkName}
                ON fst_snapshot_quarantine.destination_collision (id);
            CREATE INDEX {oldScoreName}
                ON fst_snapshot_quarantine.destination_collision (score);
            """);

        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));

        Assert.Equal(
            identity.ChildOid,
            RelationOid(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
        Assert.Equal(
            [
                "sgqi_0123456789abcdef0123456789abcdef_pk",
                "sgqi_0123456789abcdef0123456789abcdef_score",
            ],
            LoadIndexIdentity(
                    SnapshotGenerationQuarantineContract
                        .QuarantineSchema,
                    QuarantineRelation)
                .OrderBy(index => index.Role)
                .Select(index => index.Name)
                .ToArray());
    }

    [Fact]
    public async Task QuarantineIndexNameCollisionRollsBackEarlierRename()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        var originalIndexes =
            LoadIndexIdentity(
                "public",
                OriginalRelation);
        Execute(
            """
            CREATE TABLE public.derived_name_collision (
                value INTEGER NOT NULL);
            CREATE INDEX
                sgqi_0123456789abcdef0123456789abcdef_score
                ON public.derived_name_collision (value);
            """);

        var failure = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                QuarantineSql,
                command => ConfigureQuarantine(
                    command,
                    identity,
                    expectedRowCount: 1)));

        Assert.Equal("42P07", failure.SqlState);
        Assert.Equal(
            identity.ChildOid,
            RelationOid("public", OriginalRelation));
        Assert.Equal(
            originalIndexes.OrderBy(index => index.Role),
            LoadIndexIdentity(
                    "public",
                    OriginalRelation)
                .OrderBy(index => index.Role));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_quarantine_operations
                """));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_quarantine_index_renames
                """));
    }

    [Fact]
    public async Task ReattachRejectsIncompleteNormalizedEvidence()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        Execute(
            """
            SET session_replication_role = replica;
            DELETE FROM snapshot_generation_quarantine_index_renames
            WHERE operation_id =
                    '0123456789abcdef0123456789abcdef'
              AND index_role = 'score';
            SET session_replication_role = origin;
            """);
        RecordAttestation(
            "quarantined",
            baselineHashCharacter: '5',
            candidateHashCharacter: '8');
        RecordAttestation(
            "soak",
            baselineHashCharacter: '8',
            candidateHashCharacter: '9');

        var failure = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                """
                SELECT fst_reattach_snapshot_generation(
                    @operationId,
                    @planDigest,
                    'test-operator',
                    'inconsistent-evidence',
                    '{}'::jsonb)
                """,
                command =>
                {
                    command.Parameters.AddWithValue(
                        "operationId",
                        OperationId);
                    command.Parameters.AddWithValue(
                        "planDigest",
                        PlanDigest);
                }));

        Assert.Equal("55000", failure.SqlState);
        Assert.True(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_quarantine_reattachments
                """));
    }

    [Fact]
    public async Task ActiveRetentionHoldPreventsQuarantinedChildRecreation()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();

        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));

        var failure = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                """
                SELECT ensure_leaderboard_snapshot_generation_partition(
                    'Solo_PeripheralCymbals',
                    1005)
                """));

        Assert.Equal("55000", failure.SqlState);
        Assert.False(RelationExists("public", OriginalRelation));
        Assert.True(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
    }

    [Fact]
    public async Task ExactPrivateChildDropIsAtomicAndRetainsFenceAndHold()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        SeedDropPrerequisites();

        var result = ExecuteScalar<string>(
            DropSql,
            command => ConfigureDrop(command, identity));

        Assert.Equal(
            "fedcba9876543210fedcba9876543210",
            result);
        Assert.False(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
        Assert.False(RelationExists("public", OriginalRelation));
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_drop_operations
                WHERE drop_operation_id =
                    'fedcba9876543210fedcba9876543210'
                """));
        Assert.Equal(
            "55|true|true|0",
            Scalar<string>(
                """
                SELECT
                    route_count::TEXT || '|' ||
                    status_parity::TEXT || '|' ||
                    semantic_json_parity::TEXT || '|' ||
                    difference_count::TEXT
                FROM snapshot_generation_drop_attestations
                WHERE drop_operation_id =
                        'fedcba9876543210fedcba9876543210'
                  AND stage = 'pre_drop'
                """));
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_retention_holds
                WHERE instrument = 'Solo_PeripheralCymbals'
                  AND snapshot_id = 1005
                  AND hold_kind = 'retention_in_flight'
                  AND released_at IS NULL
                """));
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_constraint
                WHERE conrelid =
                        'public.leaderboard_entries_snapshot_pro_cymbals_default'
                            ::regclass
                  AND conname =
                        'ck_sgq_default_1005_0123456789ab'
                  AND convalidated
                """));
        var mutation = Assert.Throws<PostgresException>(
            () => Execute(
                """
                UPDATE snapshot_generation_drop_operations
                SET approved_by = 'changed'
                WHERE drop_operation_id =
                    'fedcba9876543210fedcba9876543210'
                """));
        Assert.Equal("55000", mutation.SqlState);
        var lateAttestation =
            Assert.Throws<PostgresException>(
                () => RecordAttestation(
                    "soak",
                    baselineHashCharacter: '8',
                    candidateHashCharacter: '9'));
        Assert.Equal("55000", lateAttestation.SqlState);
        var lateReattach =
            Assert.Throws<PostgresException>(
                () => Execute(
                    """
                    INSERT INTO
                        snapshot_generation_quarantine_reattachments (
                            operation_id,
                            reattached_by,
                            reattach_reference,
                            reattach_evidence)
                    VALUES (
                        '0123456789abcdef0123456789abcdef',
                        'test-operator',
                        'late-reattach',
                        '{}'::jsonb)
                    """));
        Assert.Equal("55000", lateReattach.SqlState);
        Execute(
            """
            UPDATE snapshot_generation_retention_holds
            SET released_at = now(),
                released_by = 'fault-injection',
                release_reason = 'prove committed-drop tombstone'
            WHERE instrument = 'Solo_PeripheralCymbals'
              AND snapshot_id = 1005
              AND released_at IS NULL
            """);
        var recreation = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                """
                SELECT ensure_leaderboard_snapshot_generation_partition(
                    'Solo_PeripheralCymbals',
                    1005)
                """));
        Assert.Equal("55000", recreation.SqlState);
        Assert.Contains(
            "committed DROP tombstone",
            recreation.MessageText,
            StringComparison.Ordinal);
        Assert.False(RelationExists("public", OriginalRelation));
    }

    [Fact]
    public async Task RestoreToolAuthorizationIsExactImmutableAndIdempotent()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        ExecuteScalar<string>(
            DropSql,
            command => ConfigureDrop(command, identity));
        var request = BuildAuthorizationRequest();
        await using var database =
            AuthorizationDatabase.FromConnectionString(
                _fixture.DataSource.ConnectionString);

        var first = await database.AuthorizeAsync(request);
        var second = await database.AuthorizeAsync(request);
        var confirmed = await database.ReadAsync(
            request.DropOperationId,
            first.AuthorizationId);

        Assert.Equal(
            RestoreToolAuthorizationContract
                .DeriveAuthorizationId(
                    request,
                    first.CanonicalEvidenceDbSha256),
            first.AuthorizationId);
        Assert.Equal(
            first.CanonicalEvidenceDbSha256,
            DbCanonicalEvidenceSha256(
                request.CanonicalEvidence));
        Assert.Equal(
            first.AuthorizationId,
            second.AuthorizationId);
        Assert.Equal(
            first.EvidenceSha256,
            second.EvidenceSha256);
        Assert.Equal(
            first.AuthorizationId,
            confirmed.AuthorizationId);
        Assert.Equal(
            first.CanonicalEvidence.GetRawText(),
            confirmed.CanonicalEvidence.GetRawText());
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM
                    snapshot_generation_restore_tool_authorizations
                """));
        var mutation = Assert.Throws<PostgresException>(
            () => Execute(
                """
                UPDATE
                    snapshot_generation_restore_tool_authorizations
                SET reason_text = 'tampered'
                """));
        Assert.Equal("55000", mutation.SqlState);
        var conflict = request with
        {
            ReasonText = "different reviewed reason",
        };
        Assert.NotEqual(
            first.AuthorizationId,
            RestoreToolAuthorizationContract
                .DeriveAuthorizationId(
                    conflict,
                    first.CanonicalEvidenceDbSha256));
        var conflictError =
            await Assert.ThrowsAsync<PostgresException>(
                () => database.AuthorizeAsync(conflict));
        Assert.Equal("55000", conflictError.SqlState);
        var wrongPinned = request with
        {
            PinnedRestoreToolSha256 =
                new string('8', 64),
        };
        var pinnedError =
            await Assert.ThrowsAsync<PostgresException>(
                () => database.AuthorizeAsync(
                    wrongPinned));
        Assert.Equal("55000", pinnedError.SqlState);
        var sameApprover = request with
        {
            ApprovedBy = "drop-operator",
        };
        var actorError =
            await Assert.ThrowsAsync<PostgresException>(
                () => database.AuthorizeAsync(
                    sameApprover));
        Assert.Equal("55000", actorError.SqlState);
        var changedContent = request with
        {
            CanonicalEvidence =
                JsonDocument.Parse(
                    """{"packageValidated":false}""")
                    .RootElement.Clone(),
        };
        Assert.NotEqual(
            first.AuthorizationId,
            RestoreToolAuthorizationContract
                .DeriveAuthorizationId(
                    changedContent,
                    DbCanonicalEvidenceSha256(
                        changedContent
                            .CanonicalEvidence)));
        var contentError =
            await Assert.ThrowsAsync<PostgresException>(
                () => database.AuthorizeAsync(
                    changedContent));
        Assert.Equal("55000", contentError.SqlState);
        var nextTool = request with
        {
            AuthorizedRestoreToolSha256 =
                new string('9', 64),
            RepairPackageManifestSha256 =
                new string('a', 64),
            BaseToFinalDiffSha256 =
                new string('b', 64),
            ReasonText =
                "Reviewed replacement after failed H3 planning",
            CanonicalEvidence =
                JsonDocument.Parse(
                    """
                    {
                      "packageValidated": true,
                      "toolGeneration": "H4"
                    }
                    """).RootElement.Clone(),
        };
        var nextAuthorization =
            await database.AuthorizeAsync(nextTool);
        Assert.NotEqual(
            first.AuthorizationId,
            nextAuthorization.AuthorizationId);
        Assert.Equal(
            nextTool.AuthorizedRestoreToolSha256,
            nextAuthorization.AuthorizedRestoreToolSha256);
        var thirdTool = nextTool with
        {
            AuthorizedRestoreToolSha256 =
                new string('c', 64),
            RepairPackageManifestSha256 =
                new string('d', 64),
            BaseToFinalDiffSha256 =
                new string('e', 64),
            ReasonText =
                "Reviewed replacement after failed H4 planning",
            CanonicalEvidence =
                JsonDocument.Parse(
                    """
                    {
                      "packageValidated": true,
                      "toolGeneration": "H5"
                    }
                    """).RootElement.Clone(),
        };
        var thirdAuthorization =
            await database.AuthorizeAsync(thirdTool);
        Assert.NotEqual(
            first.AuthorizationId,
            thirdAuthorization.AuthorizationId);
        Assert.NotEqual(
            nextAuthorization.AuthorizationId,
            thirdAuthorization.AuthorizationId);
        Assert.Equal(
            thirdTool.AuthorizedRestoreToolSha256,
            thirdAuthorization.AuthorizedRestoreToolSha256);
        Assert.Equal(
            3,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM
                    snapshot_generation_restore_tool_authorizations
                """));
    }

    [Fact]
    public async Task PythonAuthorizationLookupSqlExecutesAgainstPostgres()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        ExecuteScalar<string>(
            DropSql,
            command => ConfigureDrop(command, identity));
        var request = BuildAuthorizationRequest();
        await using var database =
            AuthorizationDatabase.FromConnectionString(
                _fixture.DataSource.ConnectionString);
        var authorization =
            await database.AuthorizeAsync(request);
        var repository = FindRepositoryRoot();
        var start = new ProcessStartInfo("python3")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment[
            "PYTHONDONTWRITEBYTECODE"] = "1";
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(
            """
            import importlib.util
            import sys
            spec = importlib.util.spec_from_file_location(
                "restore_tool", sys.argv[1])
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            print(module.restore_tool_authorization_lookup_sql(
                {
                    "dropOperationId": sys.argv[2],
                    "planDigest": sys.argv[3],
                },
                sys.argv[4]))
            """);
        start.ArgumentList.Add(
            Path.Combine(
                repository,
                "tools",
                "postgres-snapshot-generation-restore.py"));
        start.ArgumentList.Add(request.DropOperationId);
        start.ArgumentList.Add(request.DropPlanDigest);
        start.ArgumentList.Add(
            authorization.AuthorizationId);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Python authorization SQL generator did not start.");
        var sql =
            await process.StandardOutput.ReadToEndAsync();
        var error =
            await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);

        var result = ExecuteScalar<string>(sql);
        using var document = JsonDocument.Parse(result);
        Assert.Equal(
            authorization.AuthorizationId,
            document.RootElement
                .GetProperty("authorizationId")
                .GetString());
        Assert.Equal(
            request.DropOperationId,
            document.RootElement
                .GetProperty("dropOperationId")
                .GetString());
        Assert.Equal(
            request.DropPlanDigest,
            document.RootElement
                .GetProperty("dropPlanDigest")
                .GetString());
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("fence")]
    [InlineData("original-name")]
    public async Task RestoreToolAuthorizationRejectsUnsafeDropState(
        string drift)
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        ExecuteScalar<string>(
            DropSql,
            command => ConfigureDrop(command, identity));
        Execute(
            drift switch
            {
                "hold" =>
                    """
                    UPDATE snapshot_generation_retention_holds
                    SET released_at = now(),
                        released_by = 'fault-injection',
                        release_reason = 'fault-injection'
                    WHERE released_at IS NULL
                    """,
                "fence" =>
                    """
                    ALTER TABLE
                        public.leaderboard_entries_snapshot_pro_cymbals_default
                        DROP CONSTRAINT
                            ck_sgq_default_1005_0123456789ab
                    """,
                _ =>
                    """
                    CREATE TABLE
                        public.leaderboard_entries_snapshot_pro_cymbals_s1005 (
                            value INTEGER)
                    """,
            });
        await using var database =
            AuthorizationDatabase.FromConnectionString(
                _fixture.DataSource.ConnectionString);

        var failure =
            await Assert.ThrowsAsync<PostgresException>(
                () => database.AuthorizeAsync(
                    BuildAuthorizationRequest()));

        Assert.Equal("55000", failure.SqlState);
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM
                    snapshot_generation_restore_tool_authorizations
                """));
    }

    [Fact]
    public async Task AuthorizedRestoreConsumesExactToolAuthorization()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var original = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                original,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        ExecuteScalar<string>(
            DropSql,
            command => ConfigureDrop(command, original));
        var request = BuildAuthorizationRequest();
        await using var database =
            AuthorizationDatabase.FromConnectionString(
                _fixture.DataSource.ConnectionString);
        var authorization =
            await database.AuthorizeAsync(request);
        CreateRestoreStagingRelation();
        var restoredOid =
            RelationOid("public", OriginalRelation);
        var restoredRelfilenode =
            RelationRelfilenode(
                "public",
                OriginalRelation);

        var wrongPackage =
            Assert.Throws<PostgresException>(
                () => ExecuteScalar<string>(
                    RestoreSql,
                    command => ConfigureRestore(
                        command,
                        restoredOid,
                        restoredRelfilenode,
                        authorization.AuthorizationId,
                        request.AuthorizedRestoreToolSha256,
                        request.ValidatorBaseToolSha256,
                        request.AuthorizedArchiveHelperSha256,
                        new string('8', 64))));
        Assert.Equal("55000", wrongPackage.SqlState);
        var result = ExecuteScalar<string>(
            RestoreSql,
            command => ConfigureRestore(
                command,
                restoredOid,
                restoredRelfilenode,
                authorization.AuthorizationId,
                request.AuthorizedRestoreToolSha256,
                request.ValidatorBaseToolSha256,
                request.AuthorizedArchiveHelperSha256,
                request.RepairPackageManifestSha256));

        Assert.Equal(new string('a', 32), result);
        Assert.Equal(
            $"{new string('f', 64)}|"
            + $"{request.AuthorizedRestoreToolSha256}|"
            + authorization.AuthorizationId,
            Scalar<string>(
                """
                SELECT
                    pinned_tool_sha256 || '|' ||
                    executing_tool_sha256 || '|' ||
                    authorization_id
                FROM snapshot_generation_restore_operations
                """));
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_restore_operations
                    restore_row
                JOIN
                    snapshot_generation_restore_tool_authorizations
                        authorization_row
                  ON authorization_row.drop_operation_id =
                        restore_row.drop_operation_id
                 AND authorization_row.authorization_id =
                        restore_row.authorization_id
                """));
        var continuationRequest =
            BuildContinuationAuthorizationRequest(
                authorization.AuthorizationId,
                request);
        await using var continuationDatabase =
            ContinuationAuthorizationDatabase
                .FromConnectionString(
                    _fixture.DataSource.ConnectionString);
        var continuationAuthorization =
            await continuationDatabase.AuthorizeAsync(
                continuationRequest);
        ExecuteScalar<string>(
            $$"""
            SELECT
                fst_record_snapshot_generation_restore_attestation(
                    repeat('a', 32),
                    2005,
                    1005,
                    55,
                    repeat('b', 64),
                    repeat('2', 64),
                    '{{continuationRequest.RouteParityPreflightSha256}}',
                    '{}'::jsonb,
                    repeat('3', 64),
                    'restore-attestor',
                    '{{continuationRequest.AuthorizedContinuationToolSha256}}',
                    '{{continuationAuthorization.ContinuationAuthorizationId}}')
            """);
        ExecuteScalar<string>(
            $$"""
            SELECT fst_finalize_snapshot_generation_restore(
                repeat('a', 32),
                'restore-operator',
                'authorized-restore-complete',
                '{}'::jsonb,
                '{{continuationRequest.AuthorizedContinuationToolSha256}}',
                '{{continuationAuthorization.ContinuationAuthorizationId}}')
            """);
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_retention_holds
                    hold_row
                JOIN snapshot_generation_restore_operations
                    restore_row
                  ON restore_row.hold_id =
                        hold_row.hold_id
                WHERE hold_row.released_at IS NOT NULL
                """));
        Assert.Equal(
            $"{continuationRequest.AuthorizedContinuationToolSha256}|"
            + continuationAuthorization
                .ContinuationAuthorizationId,
            Scalar<string>(
                """
                SELECT
                    attestation.evidence_tool_sha256
                    || '|' ||
                    attestation.continuation_authorization_id
                FROM snapshot_generation_restore_attestations
                    attestation
                """));
        Assert.Equal(
            $"{continuationRequest.AuthorizedContinuationToolSha256}|"
            + continuationAuthorization
                .ContinuationAuthorizationId,
            Scalar<string>(
                """
                SELECT
                    finalization.evidence_tool_sha256
                    || '|' ||
                    finalization.continuation_authorization_id
                FROM snapshot_generation_restore_finalizations
                    finalization
                """));
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM
                    snapshot_generation_restore_tool_authorizations
                """));
        var afterRestore = request with
        {
            AuthorizedRestoreToolSha256 =
                new string('6', 64),
            RepairPackageManifestSha256 =
                new string('7', 64),
        };
        var afterRestoreError =
            await Assert.ThrowsAsync<PostgresException>(
                () => database.AuthorizeAsync(
                    afterRestore));
        Assert.Equal(
            "55000",
            afterRestoreError.SqlState);
    }

    [Fact]
    public async Task ReplacementRestoreToolWithoutExactAuthorizationRejects()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var original = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                original,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        ExecuteScalar<string>(
            DropSql,
            command => ConfigureDrop(command, original));
        CreateRestoreStagingRelation();
        var restoredOid =
            RelationOid("public", OriginalRelation);
        var restoredRelfilenode =
            RelationRelfilenode(
                "public",
                OriginalRelation);

        var missing = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                RestoreSql,
                command => ConfigureRestore(
                    command,
                    restoredOid,
                    restoredRelfilenode,
                    authorizationId: null,
                    executingToolSha256:
                        new string('a', 64),
                    validatorBaseToolSha256:
                        RestoreToolAuthorizationContract
                            .ValidatorBaseToolSha256,
                    archiveHelperSha256:
                        new string('b', 64),
                    repairPackageManifestSha256:
                        new string('d', 64))));
        Assert.Equal("55000", missing.SqlState);
        var wrong = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                RestoreSql,
                command => ConfigureRestore(
                    command,
                    restoredOid,
                    restoredRelfilenode,
                    new string('9', 32),
                    new string('a', 64),
                    RestoreToolAuthorizationContract
                        .ValidatorBaseToolSha256,
                    new string('b', 64),
                    new string('d', 64))));
        Assert.Equal("55000", wrong.SqlState);
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_restore_operations
                """));
        Assert.True(
            RelationExists("public", OriginalRelation));
    }

    [Fact]
    public async Task EmptyRestoreIdentityUpgradeAllowsCommittedDrop()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        ExecuteScalar<string>(
            DropSql,
            command => ConfigureDrop(command, identity));
        Execute(DowngradeRestoreOperationsToPreSemanticSql);
        Execute(
            """
            DROP TABLE
                snapshot_generation_restore_tool_authorizations
                CASCADE
            """);
        var dropHash = Scalar<string>(
            """
            SELECT md5(to_jsonb(operation_row)::TEXT)
            FROM snapshot_generation_drop_operations
                operation_row
            """);

        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);

        Assert.Equal(0, MissingRestoreSemanticColumnCount());
        Assert.Equal(
            dropHash,
            Scalar<string>(
                """
                SELECT md5(to_jsonb(operation_row)::TEXT)
                FROM snapshot_generation_drop_operations
                    operation_row
                """));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_restore_operations
                """));
    }

    [Fact]
    public async Task NonemptyPreSemanticDropEvidenceBlocksSchemaUpgrade()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        ExecuteScalar<string>(
            DropSql,
            command => ConfigureDrop(command, identity));
        Execute(DowngradeDropOperationsToPreSemanticSql);
        var before = Scalar<string>(
            """
            SELECT md5(to_jsonb(operation_row)::TEXT)
            FROM snapshot_generation_drop_operations
                operation_row
            """);

        var failure = await Assert.ThrowsAsync<PostgresException>(
            () => DatabaseInitializer.EnsureSchemaAsync(
                _fixture.DataSource));

        Assert.Equal("55000", failure.SqlState);
        Assert.Contains(
            "nonempty pre-semantic committed evidence",
            failure.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            before,
            Scalar<string>(
                """
                SELECT md5(to_jsonb(operation_row)::TEXT)
                FROM snapshot_generation_drop_operations
                    operation_row
                """));
        Assert.Equal(
            9,
            MissingDropSemanticColumnCount());
        Assert.DoesNotContain(
            "semantic_projection_version",
            Scalar<string>(
                """
                SELECT pg_get_expr(
                    constraint_row.conbin,
                    constraint_row.conrelid,
                    TRUE)
                FROM pg_constraint constraint_row
                WHERE constraint_row.conrelid =
                        'snapshot_generation_drop_operations'
                            ::regclass
                  AND constraint_row.conname =
                        'ck_snapshot_generation_drop_identity'
                """),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonemptyPreSemanticRestoreEvidenceBlocksSchemaUpgrade()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var original = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                original,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        ExecuteScalar<string>(
            DropSql,
            command => ConfigureDrop(command, original));
        Execute(
            """
            CREATE TABLE
                public.leaderboard_entries_snapshot_pro_cymbals_s1005
                (
                    LIKE
                        public.leaderboard_entries_snapshot_pro_cymbals
                    INCLUDING DEFAULTS
                    INCLUDING STORAGE
                    INCLUDING COMPRESSION
                );
            INSERT INTO
                public.leaderboard_entries_snapshot_pro_cymbals_s1005 (
                    snapshot_id,
                    song_id,
                    instrument,
                    account_id,
                    score,
                    source,
                    first_seen_at,
                    last_updated_at)
            VALUES (
                1005,
                'song-test',
                'Solo_PeripheralCymbals',
                'account-test',
                123456,
                'scrape',
                now(),
                now());
            ALTER TABLE
                public.leaderboard_entries_snapshot_pro_cymbals_s1005
                ADD CONSTRAINT ck_sgr_1005_aaaaaaaaaaaa
                CHECK (
                    snapshot_id = 1005
                    AND instrument =
                        'Solo_PeripheralCymbals');
            CREATE TRIGGER trg_sgr_1005_aaaaaaaaaaaa
                BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
                ON
                    public.leaderboard_entries_snapshot_pro_cymbals_s1005
                FOR EACH STATEMENT EXECUTE FUNCTION
                    fst_reject_snapshot_generation_quarantine_relation_mutation();
            """);
        var restoredOid =
            RelationOid("public", OriginalRelation);
        var restoredRelfilenode =
            RelationRelfilenode("public", OriginalRelation);
        ExecuteScalar<string>(
            RestoreSql,
            command => ConfigureRestore(
                command,
                restoredOid,
                restoredRelfilenode));
        Execute(DowngradeRestoreOperationsToPreSemanticSql);
        var before = Scalar<string>(
            """
            SELECT md5(to_jsonb(operation_row)::TEXT)
            FROM snapshot_generation_restore_operations
                operation_row
            """);

        var failure = await Assert.ThrowsAsync<PostgresException>(
            () => DatabaseInitializer.EnsureSchemaAsync(
                _fixture.DataSource));

        Assert.Equal("55000", failure.SqlState);
        Assert.Contains(
            "nonempty pre-semantic committed evidence",
            failure.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            before,
            Scalar<string>(
                """
                SELECT md5(to_jsonb(operation_row)::TEXT)
                FROM snapshot_generation_restore_operations
                    operation_row
                """));
        Assert.Equal(
            7,
            MissingRestoreSemanticColumnCount());
        Assert.DoesNotContain(
            "archived_index_names",
            Scalar<string>(
                """
                SELECT pg_get_expr(
                    constraint_row.conbin,
                    constraint_row.conrelid,
                    TRUE)
                FROM pg_constraint constraint_row
                WHERE constraint_row.conrelid =
                        'snapshot_generation_restore_operations'
                            ::regclass
                  AND constraint_row.conname =
                        'ck_snapshot_generation_restore_identity'
                """),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("attestation")]
    [InlineData("finalization")]
    public async Task NonemptyLegacyRestoreEvidenceBlocksContinuationUpgrade(
        string relation)
    {
        await SeedAuthorizedRestoreAsync();
        if (relation == "attestation")
        {
            Execute(
                """
                ALTER TABLE
                    snapshot_generation_restore_attestations
                    DROP CONSTRAINT
                        fk_snapshot_generation_restore_attestation_continuation,
                    DROP CONSTRAINT
                        ck_snapshot_generation_restore_attestation,
                    DROP COLUMN semantic_binary_parity,
                    DROP COLUMN route_parity_algorithm_id,
                    DROP COLUMN route_semantic_evidence_sha256,
                    DROP COLUMN evidence_tool_sha256,
                    DROP COLUMN continuation_authorization_id;
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
                            AND difference_count = 0
                            AND baseline_route_manifest_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND candidate_route_manifest_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND evidence_sha256
                                ~ '^[0-9a-f]{64}$'
                            AND attested_by <> ''
                            AND jsonb_typeof(
                                database_evidence) =
                                'object');
                INSERT INTO
                    snapshot_generation_restore_attestations (
                        restore_operation_id,
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
                    repeat('a', 32),
                    2005,
                    1005,
                    55,
                    repeat('b', 64),
                    repeat('2', 64),
                    TRUE,
                    TRUE,
                    0,
                    '{}'::jsonb,
                    repeat('3', 64),
                    'legacy-attestor')
                """);
        }
        else
        {
            Execute(
                """
                ALTER TABLE
                    snapshot_generation_restore_finalizations
                    DROP CONSTRAINT
                        fk_snapshot_generation_restore_finalization_continuation,
                    DROP CONSTRAINT
                        ck_snapshot_generation_restore_finalize,
                    DROP COLUMN evidence_tool_sha256,
                    DROP COLUMN continuation_authorization_id;
                ALTER TABLE
                    snapshot_generation_restore_finalizations
                    ADD CONSTRAINT
                        ck_snapshot_generation_restore_finalize
                        CHECK (
                            finalized_by <> ''
                            AND finalize_reference <> ''
                            AND jsonb_typeof(
                                finalization_evidence) =
                                'object');
                INSERT INTO
                    snapshot_generation_restore_finalizations (
                        restore_operation_id,
                        finalized_by,
                        finalize_reference,
                        finalization_evidence)
                VALUES (
                    repeat('a', 32),
                    'legacy-finalizer',
                    'legacy-reference',
                    '{}'::jsonb)
                """);
        }

        var error =
            await Assert.ThrowsAsync<PostgresException>(
                () => DatabaseInitializer
                    .EnsureSchemaAsync(
                        _fixture.DataSource));

        Assert.Equal("55000", error.SqlState);
        Assert.Equal(
            1,
            Scalar<int>(
                $"""
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_restore_{relation}s
                """));
        Assert.False(
            Scalar<bool>(
                $"""
                SELECT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name =
                            'snapshot_generation_restore_{relation}s'
                      AND column_name =
                            'continuation_authorization_id')
                """));
    }

    [Fact]
    public async Task ContinuationAuthorizationIsExactImmutableAndIdempotent()
    {
        var seeded =
            await SeedAuthorizedRestoreAsync();
        var request =
            BuildContinuationAuthorizationRequest(
                seeded.Authorization.AuthorizationId,
                seeded.Request);
        await using var database =
            ContinuationAuthorizationDatabase
                .FromConnectionString(
                    _fixture.DataSource.ConnectionString);

        var first =
            await database.AuthorizeAsync(request);
        var repeated =
            await database.AuthorizeAsync(request);
        var confirmed = await database.ReadAsync(
            request.RestoreOperationId,
            first.ContinuationAuthorizationId);

        Assert.Equal(
            first.ContinuationAuthorizationId,
            repeated.ContinuationAuthorizationId);
        Assert.Equal(
            first.ContinuationAuthorizationId,
            confirmed.ContinuationAuthorizationId);
        Assert.Equal(
            RestoreContinuationContract
                .DeriveAuthorizationId(
                    request,
                    first.CanonicalEvidenceDbSha256),
            first.ContinuationAuthorizationId);
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM
                    snapshot_generation_restore_continuation_authorizations
                """));
        var mutation =
            Assert.Throws<PostgresException>(
                () => Execute(
                    """
                    UPDATE
                        snapshot_generation_restore_continuation_authorizations
                    SET reason_text = 'tampered'
                    """));
        Assert.Equal("55000", mutation.SqlState);
        var conflict = request with
        {
            CandidateRouteManifestSha256 =
                new string('f', 64),
        };
        var conflictError =
            await Assert.ThrowsAsync<PostgresException>(
                () => database.AuthorizeAsync(
                    conflict));
        Assert.Equal("55000", conflictError.SqlState);
        var wrongPredecessor = request with
        {
            PredecessorAuthorizationId =
                new string('1', 32),
        };
        var predecessorError =
            await Assert.ThrowsAsync<PostgresException>(
                () => database.AuthorizeAsync(
                    wrongPredecessor));
        Assert.Equal("P0002", predecessorError.SqlState);
        var sameActor = request with
        {
            ApprovedBy = "restore-operator",
            AuthorizedContinuationToolSha256 =
                new string('5', 64),
        };
        var actorError =
            await Assert.ThrowsAsync<PostgresException>(
                () => database.AuthorizeAsync(
                    sameActor));
        Assert.Equal("55000", actorError.SqlState);
    }

    [Fact]
    public async Task ContinuationDatabaseAttestsAndFinalizesExactRestore()
    {
        var seeded =
            await SeedAuthorizedRestoreAsync();
        var request =
            BuildContinuationAuthorizationRequest(
                seeded.Authorization.AuthorizationId,
                seeded.Request);
        await using var authorizationDatabase =
            ContinuationAuthorizationDatabase
                .FromConnectionString(
                    _fixture.DataSource.ConnectionString);
        var authorization =
            await authorizationDatabase.AuthorizeAsync(
                request);
        var otherRequest = request with
        {
            AuthorizedContinuationToolSha256 =
                new string('5', 64),
            ContinuationPackageManifestSha256 =
                new string('4', 64),
            ReasonText =
                "Authorize another reviewed continuation tool.",
            ApprovalReference =
                "other-continuation-authorization-reference",
            CanonicalEvidence =
                JsonDocument.Parse(
                    """{"packageValidated":true,"tool":"other"}""")
                    .RootElement.Clone(),
        };
        var otherAuthorization =
            await authorizationDatabase.AuthorizeAsync(
                otherRequest);
        var manifest =
            BuildContinuationManifest(request);
        var otherManifest =
            BuildContinuationManifest(otherRequest);
        var parity =
            new DetailedRouteParityEvidence(
                new RouteParityEvidence(
                    "/evidence/baseline/manifest.json",
                    request.BaselineRouteManifestSha256,
                    "/evidence/candidate/manifest.json",
                    request.CandidateRouteManifestSha256,
                    request.PublicationId,
                    request.PublishedScrapeId,
                    55,
                    true,
                    true,
                    0),
                QuarantineEvidenceValidator
                    .RouteParityAlgorithmId,
                true,
                new string('f', 64),
                []);
        await using var database =
            ContinuationDatabase.FromConnectionString(
                _fixture.DataSource.ConnectionString);

        var attestation = await database.AttestAsync(
            manifest,
            authorization.ContinuationAuthorizationId,
            parity,
            "restore-attestor");
        var before = await database.ReadStateAsync(
            manifest.RestoreOperationId,
            authorization.ContinuationAuthorizationId);
        Assert.Equal(1, attestation.Fingerprint.RowCount);
        Assert.True(
            before.GetProperty("attested")
                .GetBoolean());
        Assert.True(
            before.GetProperty("holdActive")
                .GetBoolean());

        var wrongAuthorization =
            await Assert.ThrowsAsync<PostgresException>(
                () => database.FinalizeAsync(
                    otherManifest,
                    otherAuthorization
                        .ContinuationAuthorizationId,
                    "restore-finalizer",
                    "wrong-continuation",
                    JsonDocument.Parse(
                        """{"confirmed":true}""")
                        .RootElement.Clone()));
        Assert.Equal("P0002", wrongAuthorization.SqlState);
        Assert.True(
            Scalar<bool>(
                """
                SELECT hold_row.released_at IS NULL
                FROM snapshot_generation_retention_holds
                    hold_row
                JOIN snapshot_generation_restore_operations
                    restore_row
                  ON restore_row.hold_id =
                        hold_row.hold_id
                WHERE restore_row.restore_operation_id =
                        repeat('a', 32)
                """));
        await database.FinalizeAsync(
            manifest,
            authorization.ContinuationAuthorizationId,
            "restore-finalizer",
            "restore-finalized",
            JsonDocument.Parse(
                """{"confirmed":true}""")
                .RootElement.Clone());
        var after = await database.ReadStateAsync(
            manifest.RestoreOperationId,
            authorization.ContinuationAuthorizationId);

        Assert.True(
            after.GetProperty("finalized")
                .GetBoolean());
        Assert.False(
            after.GetProperty("holdActive")
                .GetBoolean());
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_trigger
                WHERE tgrelid =
                        'public.leaderboard_entries_snapshot_pro_cymbals_s1005'
                            ::regclass
                  AND tgname =
                        'trg_sgr_1005_aaaaaaaaaaaa'
                  AND NOT tgisinternal
                """));
    }

    [Theory]
    [InlineData(
        "wrong-name",
        """
        ALTER INDEX
            fst_snapshot_quarantine.sgqi_0123456789abcdef0123456789abcdef_score
            RENAME TO sgqi_wrong_name_score
        """)]
    [InlineData(
        "wrong-oid",
        """
        DROP INDEX
            fst_snapshot_quarantine.sgqi_0123456789abcdef0123456789abcdef_score;
        CREATE INDEX
            sgqi_0123456789abcdef0123456789abcdef_score
            ON
                fst_snapshot_quarantine.sgq_pc_1005_0123456789ab
                USING btree (
                    snapshot_id,
                    song_id,
                    instrument,
                    score DESC)
        """)]
    [InlineData(
        "wrong-relfilenode",
        """
        REINDEX INDEX
            fst_snapshot_quarantine.sgqi_0123456789abcdef0123456789abcdef_score
        """)]
    [InlineData(
        "wrong-role",
        """
        ALTER INDEX
            fst_snapshot_quarantine.sgqi_0123456789abcdef0123456789abcdef_pk
            RENAME TO sgqi_swap_temp;
        ALTER INDEX
            fst_snapshot_quarantine.sgqi_0123456789abcdef0123456789abcdef_score
            RENAME TO sgqi_0123456789abcdef0123456789abcdef_pk;
        ALTER INDEX
            fst_snapshot_quarantine.sgqi_swap_temp
            RENAME TO sgqi_0123456789abcdef0123456789abcdef_score
        """)]
    [InlineData(
        "extra-index",
        """
        CREATE INDEX sgqi_unexpected_extra
            ON
                fst_snapshot_quarantine.sgq_pc_1005_0123456789ab
                (account_id)
        """)]
    [InlineData(
        "missing-index",
        """
        DROP INDEX
            fst_snapshot_quarantine.sgqi_0123456789abcdef0123456789abcdef_score
        """)]
    public async Task DropRejectsCurrentPrivateIndexDriftWithoutResidue(
        string scenario,
        string mutationSql)
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        Execute(mutationSql);
        AssertPrivateIndexDrift(
            scenario,
            identity);

        var failure = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                DropSql,
                command => ConfigureDrop(command, identity)));

        Assert.Equal("55000", failure.SqlState);
        Assert.True(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
        Assert.Equal(
            identity.ChildOid,
            RelationOid(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_drop_operations
                """));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_drop_attestations
                """));
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_retention_holds
                WHERE instrument =
                        'Solo_PeripheralCymbals'
                  AND snapshot_id = 1005
                  AND hold_kind =
                        'retention_in_flight'
                  AND released_at IS NULL
                """));
        Assert.True(
            RelationExists(
                "public",
                "leaderboard_entries_snapshot_pro_cymbals_default"));
    }

    [Fact]
    public async Task ExternalDependencyRejectsDropWithoutResidue()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        Execute(
            """
            CREATE VIEW public.sgq_external_dependency AS
            SELECT *
            FROM fst_snapshot_quarantine.sgq_pc_1005_0123456789ab
            """);

        var failure = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                DropSql,
                command => ConfigureDrop(command, identity)));

        Assert.Equal("2BP01", failure.SqlState);
        Assert.True(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_drop_operations
                """));
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_constraint
                WHERE conrelid =
                        'public.leaderboard_entries_snapshot_pro_cymbals_default'
                            ::regclass
                  AND conname =
                        'ck_sgq_default_1005_0123456789ab'
                """));
    }

    [Theory]
    [InlineData(
        """
        CREATE TABLE public.sgq_inbound_fk (
            snapshot_id BIGINT NOT NULL,
            song_id TEXT NOT NULL,
            instrument TEXT NOT NULL,
            account_id TEXT NOT NULL,
            CONSTRAINT sgq_inbound_fk_target
                FOREIGN KEY (
                    snapshot_id,
                    song_id,
                    instrument,
                    account_id)
                REFERENCES
                    fst_snapshot_quarantine.sgq_pc_1005_0123456789ab (
                        snapshot_id,
                        song_id,
                        instrument,
                        account_id)
        )
        """)]
    [InlineData(
        """
        ALTER TABLE
            fst_snapshot_quarantine.sgq_pc_1005_0123456789ab
            ENABLE ROW LEVEL SECURITY;
        CREATE POLICY sgq_policy
            ON fst_snapshot_quarantine.sgq_pc_1005_0123456789ab
            USING (TRUE)
        """)]
    [InlineData(
        """
        CREATE TRIGGER sgq_unexpected_trigger
            BEFORE INSERT ON
                fst_snapshot_quarantine.sgq_pc_1005_0123456789ab
            FOR EACH STATEMENT EXECUTE FUNCTION
                fst_reject_snapshot_generation_quarantine_relation_mutation()
        """)]
    [InlineData(
        """
        CREATE RULE sgq_unexpected_rule AS
            ON UPDATE TO
                fst_snapshot_quarantine.sgq_pc_1005_0123456789ab
            DO ALSO NOTHING
        """)]
    [InlineData(
        """
        CREATE PUBLICATION sgq_unexpected_publication
            FOR TABLE
                fst_snapshot_quarantine.sgq_pc_1005_0123456789ab
        """)]
    public async Task HiddenDependencyOrPolicyRejectsDrop(
        string dependencySql)
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        Execute(dependencySql);

        var failure = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                DropSql,
                command => ConfigureDrop(command, identity)));

        Assert.Equal("2BP01", failure.SqlState);
        Assert.True(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_drop_operations
                """));
    }

    [Fact]
    public async Task DropLockRejectsConcurrentQuarantineExecutor()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));

        await using var blocker =
            await _fixture.DataSource.OpenConnectionAsync();
        await using (var acquire = blocker.CreateCommand())
        {
            acquire.CommandText =
                "SELECT pg_advisory_lock(@key)";
            acquire.Parameters.AddWithValue(
                "key",
                SnapshotGenerationQuarantineContract
                    .ExecutorAdvisoryLockKey);
            await acquire.ExecuteNonQueryAsync();
        }

        var failure = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                """
                SELECT fst_lock_snapshot_generation_for_drop(
                    @operationId,
                    @childOid,
                    @childRelfilenode)
                """,
                command =>
                {
                    command.Parameters.AddWithValue(
                        "operationId",
                        OperationId);
                    command.Parameters.AddWithValue(
                        "childOid",
                        identity.ChildOid);
                    command.Parameters.AddWithValue(
                        "childRelfilenode",
                        identity.ChildRelfilenode);
                }));

        Assert.Equal("55P03", failure.SqlState);
    }

    [Fact]
    public async Task DropFunctionRejectsBypassingLockedPreflight()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        var start = DropSql.IndexOf(
            "SELECT fst_drop_quarantined",
            StringComparison.Ordinal);
        var end = DropSql.LastIndexOf(
            "FROM locked",
            StringComparison.Ordinal);
        var unlockedSql = DropSql[start..end];

        var failure = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                unlockedSql,
                command => ConfigureDrop(command, identity)));

        Assert.Equal("55000", failure.SqlState);
        Assert.Contains(
            "not locked",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DropLockTimeoutLeavesPrivateChildUnchanged()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        await using var blocker =
            await _fixture.DataSource.OpenConnectionAsync();
        await using var blockerTransaction =
            await blocker.BeginTransactionAsync();
        await using (var acquire = blocker.CreateCommand())
        {
            acquire.Transaction = blockerTransaction;
            acquire.CommandText = """
                LOCK TABLE
                    fst_snapshot_quarantine.sgq_pc_1005_0123456789ab
                    IN ACCESS SHARE MODE
                """;
            await acquire.ExecuteNonQueryAsync();
        }

        var failure = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                """
                SELECT fst_lock_snapshot_generation_for_drop(
                    @operationId,
                    @childOid,
                    @childRelfilenode)
                """,
                command =>
                {
                    command.Parameters.AddWithValue(
                        "operationId",
                        OperationId);
                    command.Parameters.AddWithValue(
                        "childOid",
                        identity.ChildOid);
                    command.Parameters.AddWithValue(
                        "childRelfilenode",
                        identity.ChildRelfilenode);
                }));

        Assert.Equal(
            PostgresErrorCodes.LockNotAvailable,
            failure.SqlState);
        Assert.True(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
        await blockerTransaction.RollbackAsync();
    }

    [Fact]
    public async Task RecreatedPrivateRelationOidRejectsDrop()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        Execute(
            """
            DROP TABLE
                fst_snapshot_quarantine.sgq_pc_1005_0123456789ab;
            CREATE TABLE
                fst_snapshot_quarantine.sgq_pc_1005_0123456789ab
                (
                    LIKE
                        public.leaderboard_entries_snapshot_pro_cymbals
                    INCLUDING ALL
                );
            """);

        var failure = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                DropSql,
                command => ConfigureDrop(command, identity)));

        Assert.Equal("55000", failure.SqlState);
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_drop_operations
                """));
    }

    [Theory]
    [InlineData(
        """
        INSERT INTO scrape_writer_failures (
            scrape_id,
            writer_kind,
            instrument,
            song_id,
            page_count,
            row_count,
            exception_type,
            error_message,
            occurred_at)
        VALUES (
            1005,
            'online',
            'Solo_PeripheralCymbals',
            'failed-song',
            1,
            0,
            'InjectedFailure',
            'drop must fail closed',
            now())
        """)]
    [InlineData(
        """
        INSERT INTO snapshot_generation_retention_holds (
            instrument,
            snapshot_id,
            hold_kind,
            reason,
            created_by)
        VALUES (
            'Solo_PeripheralCymbals',
            1005,
            'operator',
            'additional hold',
            'test')
        """)]
    [InlineData(
        """
        UPDATE snapshot_generation_retention_holds
        SET released_at = now(),
            released_by = 'test',
            release_reason = 'test'
        WHERE instrument = 'Solo_PeripheralCymbals'
          AND snapshot_id = 1005
          AND hold_kind = 'retention_in_flight'
          AND released_at IS NULL
        """)]
    [InlineData(
        """
        UPDATE scrape_publication_state
        SET public_reads_frozen = TRUE,
            public_reads_frozen_at = now(),
            public_reads_frozen_scrape_id = 1005,
            public_reads_frozen_reason = 'test'
        WHERE id = TRUE
        """)]
    [InlineData(
        """
        UPDATE scrape_publication_state
        SET working_publication_id = 2004
        WHERE id = TRUE
        """)]
    [InlineData(
        """
        UPDATE scrape_publication_state
        SET current_publication_id = 2004,
            published_scrape_id = 1004,
            improvement_notifications_scrape_id = 1004,
            improvement_notifications_projection_scrape_id = 1004
        WHERE id = TRUE
        """)]
    [InlineData(
        """
        UPDATE scrape_publication_state
        SET publication_commit_intent_started_at = now(),
            publication_commit_intent_heartbeat_at = now(),
            publication_commit_intent_owner = 'test'
        WHERE id = TRUE
        """)]
    [InlineData(
        """
        UPDATE scrape_publication_state
        SET max_score_mutation_gate_token = 'test',
            max_score_mutation_gate_publication_id = 2005,
            max_score_mutation_gate_backend_pid = pg_backend_pid(),
            max_score_mutation_gate_backend_start = now(),
            max_score_mutation_gate_acquired_at = now()
        WHERE id = TRUE
        """)]
    [InlineData(
        """
        UPDATE scrape_publication_state
        SET improvement_notifications_status = 'pending',
            improvement_notifications_completed_at = NULL
        WHERE id = TRUE
        """)]
    [InlineData(
        """
        INSERT INTO scrape_log (
            id,
            started_at,
            status)
        VALUES (
            1010,
            now(),
            'running')
        """)]
    [InlineData(
        """
        UPDATE service_worker_status
        SET status = 'running',
            current_operation_json = '{}'::jsonb
        WHERE worker_key = 'scraper'
        """)]
    [InlineData(
        """
        ALTER TABLE
            public.leaderboard_entries_snapshot_pro_cymbals_default
            DROP CONSTRAINT
                ck_sgq_default_1005_0123456789ab
        """)]
    [InlineData(
        """
        INSERT INTO scrape_log (
            id,
            started_at,
            completed_at,
            status)
        VALUES (
            9999,
            now() - interval '2 minutes',
            now() - interval '1 minute',
            'completed');
        INSERT INTO
            public.leaderboard_entries_snapshot_pro_cymbals_default (
                snapshot_id,
                song_id,
                instrument,
                account_id,
                score,
                source,
                first_seen_at,
                last_updated_at)
        VALUES (
            9999,
            'default-row',
            'Solo_PeripheralCymbals',
            'default-row',
            1,
            'scrape',
            now(),
            now())
        """)]
    [InlineData(
        """
        UPDATE pg_index
        SET indisvalid = FALSE
        WHERE indexrelid = (
            SELECT index_row.indexrelid
            FROM pg_index index_row
            WHERE index_row.indrelid =
                    'fst_snapshot_quarantine.sgq_pc_1005_0123456789ab'
                        ::regclass
            ORDER BY index_row.indisprimary DESC
            LIMIT 1)
        """)]
    [InlineData(
        """
        INSERT INTO leaderboard_snapshot_state (
            song_id,
            instrument,
            active_snapshot_id,
            scrape_id,
            is_finalized,
            updated_at)
        VALUES (
            'drop-live-root',
            'Solo_PeripheralCymbals',
            1005,
            1005,
            TRUE,
            now())
        """)]
    [InlineData(
        """
        INSERT INTO solo_current_projection_scope (
            song_id,
            instrument,
            projection_generation,
            row_count,
            source_snapshot_id,
            source_kind,
            status,
            updated_at)
        VALUES (
            'drop-live-projection',
            'Solo_PeripheralCymbals',
            1,
            1,
            1005,
            'snapshot',
            'ready',
            now())
        """)]
    [InlineData(
        """
        INSERT INTO leaderboard_published_scope_source (
            published_scrape_id,
            song_id,
            instrument,
            scope_kind,
            source_kind,
            source_snapshot_id,
            source_scrape_id,
            row_count,
            content_fingerprint,
            coverage_fingerprint,
            reported_total_entries,
            reported_total_pages,
            is_complete,
            created_at,
            validated_at)
        VALUES (
            1005,
            'drop-live-published',
            'Solo_PeripheralCymbals',
            'alltime',
            'snapshot',
            1005,
            1005,
            1,
            repeat('a', 64),
            repeat('b', 64),
            1,
            1,
            TRUE,
            now(),
            now())
        """)]
    [InlineData(
        """
        SET session_replication_role = replica;
        UPDATE snapshot_generation_retention_observations
        SET classification = 'protected',
            blocker_codes = ARRAY['tampered_candidate']
        WHERE observation_id = 3005;
        SET session_replication_role = origin
        """)]
    public async Task CurrentSafetyFenceRejectsDrop(
        string mutationSql)
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        Execute(mutationSql);

        Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                DropSql,
                command => ConfigureDrop(command, identity)));
        Assert.True(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_drop_operations
                """));
    }

    [Fact]
    public async Task AdvancedPlannerCycleRejectsDropWithoutResidue()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        Execute(
            """
            INSERT INTO snapshot_generation_retention_cycles (
                cycle_id,
                trigger_scrape_id,
                trigger_publication_id,
                safe_point_kind,
                safe_point_at,
                planner_version,
                config_version,
                report_only,
                status,
                oracle_agreement,
                candidate_identity_hash,
                observation_hash,
                planner_child_set,
                planner_live_set,
                planner_candidate_set,
                oracle_child_set,
                oracle_live_set,
                oracle_candidate_set,
                candidate_count,
                protected_count,
                blocked_count,
                candidate_bytes,
                global_blockers,
                anomalies,
                created_at)
            VALUES (
                3006,
                1006,
                2006,
                'terminal_worker_post_publication',
                now(),
                3,
                1,
                TRUE,
                'observed',
                TRUE,
                repeat('a', 64),
                repeat('b', 64),
                '[]',
                '[]',
                '[]',
                '[]',
                '[]',
                '[]',
                0,
                0,
                0,
                0,
                '[]',
                '[]',
                now());
            """);

        var failure = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                DropSql,
                command => ConfigureDrop(command, identity)));

        Assert.Equal("55000", failure.SqlState);
        Assert.Contains(
            "latest accepted cycle",
            failure.Message,
            StringComparison.Ordinal);
        Assert.True(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_drop_operations
                """));
    }

    [Theory]
    [InlineData("q1-no-rotation")]
    [InlineData("q2-short-soak")]
    [InlineData("q1-identity-drift")]
    public async Task DropRejectsInvalidRehearsalAndSoakEvidence(
        string mutation)
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        var mutationStatement = mutation switch
        {
            "q1-no-rotation" => """
                UPDATE
                    snapshot_generation_quarantine_attestations
                SET publication_id = 2005,
                    published_scrape_id = 1005
                WHERE attestation_id = 9102
                """,
            "q2-short-soak" => """
                UPDATE
                    snapshot_generation_quarantine_operations
                SET quarantined_at =
                        now() - interval '5 minutes'
                WHERE operation_id =
                    '0123456789abcdef0123456789abcdef'
                """,
            _ => """
                UPDATE
                    snapshot_generation_quarantine_operations
                SET child_oid = child_oid + 1
                WHERE operation_id = repeat('b', 32)
                """,
        };
        Execute(
            $"""
            SET session_replication_role = replica;
            {mutationStatement};
            SET session_replication_role = origin;
            """);

        var failure = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                DropSql,
                command => ConfigureDrop(command, identity)));

        Assert.Equal("55000", failure.SqlState);
        Assert.True(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
    }

    [Fact]
    public async Task DropRejectsInsufficientHealthSamples()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        SeedDropPrerequisites();

        var failure = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                DropSql,
                command => ConfigureDrop(
                    command,
                    identity,
                    healthSampleCount: 59)));

        Assert.Equal("22023", failure.SqlState);
        Assert.True(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
    }

    [Theory]
    [InlineData(54, true, true, 0)]
    [InlineData(55, false, true, 0)]
    [InlineData(55, true, false, 0)]
    [InlineData(55, true, true, 1)]
    public async Task DropRejectsAssertedPreDropParity(
        int routeCount,
        bool statusParity,
        bool semanticJsonParity,
        int differenceCount)
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        SeedDropPrerequisites();

        var failure = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                DropSql,
                command => ConfigureDrop(
                    command,
                    identity,
                    preDropRouteCount: routeCount,
                    preDropStatusParity: statusParity,
                    preDropSemanticJsonParity:
                        semanticJsonParity,
                    preDropDifferenceCount:
                        differenceCount)));

        Assert.Equal("22023", failure.SqlState);
        Assert.True(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_drop_operations
                """));
    }

    [Fact]
    public async Task DropRejectsHealthWindowThatPredatesQ2()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                identity,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        var staleHealthSql = DropSql.Replace(
            "now() - interval '31 minutes'",
            "now() - interval '40 minutes'",
            StringComparison.Ordinal);

        var failure = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                staleHealthSql,
                command => ConfigureDrop(
                    command,
                    identity)));

        Assert.Equal("55000", failure.SqlState);
        Assert.True(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
    }

    [Fact]
    public async Task DroppedChildCanBeLogicallyRestoredWithNewIdentity()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var original = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            QuarantineSql,
            command => ConfigureQuarantine(
                command,
                original,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        ExecuteScalar<string>(
            DropSql,
            command => ConfigureDrop(command, original));
        ExecuteScalar<long>(
            """
            SELECT fst_record_snapshot_generation_drop_attestation(
                'fedcba9876543210fedcba9876543210',
                'dropped',
                2005,
                1005,
                55,
                repeat('b', 64),
                repeat('c', 64),
                '{}'::jsonb,
                repeat('d', 64),
                'drop-operator')
            """);
        ExecuteScalar<long>(
            """
            SELECT fst_record_snapshot_generation_drop_attestation(
                'fedcba9876543210fedcba9876543210',
                'dropped',
                2005,
                1005,
                55,
                repeat('b', 64),
                repeat('c', 64),
                '{}'::jsonb,
                repeat('d', 64),
                'drop-operator')
            """);
        Assert.Equal(
            2,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_drop_attestations
                WHERE drop_operation_id =
                    'fedcba9876543210fedcba9876543210'
                """));
        Assert.Equal(
            2,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_drop_evidence
                WHERE drop_operation_id =
                    'fedcba9876543210fedcba9876543210'
                """));
        var predecessorRequest =
            BuildAuthorizationRequest();
        await using var predecessorDatabase =
            AuthorizationDatabase.FromConnectionString(
                _fixture.DataSource.ConnectionString);
        var predecessorAuthorization =
            await predecessorDatabase.AuthorizeAsync(
                predecessorRequest);

        Execute(
            """
            CREATE TABLE public.restore_name_collision (
                id INTEGER NOT NULL,
                score INTEGER NOT NULL);
            CREATE UNIQUE INDEX archived_pk
                ON public.restore_name_collision (id);
            CREATE INDEX archived_score
                ON public.restore_name_collision (score);
            """);
        var unrelatedPkOid =
            RelationOid("public", "archived_pk");
        var unrelatedScoreOid =
            RelationOid("public", "archived_score");
        Execute(
            """
            CREATE TABLE
                public.leaderboard_entries_snapshot_pro_cymbals_s1005
                (
                    LIKE
                        public.leaderboard_entries_snapshot_pro_cymbals
                    INCLUDING DEFAULTS
                    INCLUDING STORAGE
                    INCLUDING COMPRESSION
                );
            INSERT INTO
                public.leaderboard_entries_snapshot_pro_cymbals_s1005 (
                    snapshot_id,
                    song_id,
                    instrument,
                    account_id,
                    score,
                    source,
                    first_seen_at,
                    last_updated_at)
            VALUES (
                1005,
                'song-test',
                'Solo_PeripheralCymbals',
                'account-test',
                123456,
                'scrape',
                now(),
                now());
            ALTER TABLE
                public.leaderboard_entries_snapshot_pro_cymbals_s1005
                ADD CONSTRAINT ck_sgr_1005_aaaaaaaaaaaa
                CHECK (
                    snapshot_id = 1005
                    AND instrument =
                        'Solo_PeripheralCymbals');
            CREATE TRIGGER trg_sgr_1005_aaaaaaaaaaaa
                BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
                ON
                    public.leaderboard_entries_snapshot_pro_cymbals_s1005
                FOR EACH STATEMENT EXECUTE FUNCTION
                    fst_reject_snapshot_generation_quarantine_relation_mutation();
            """);
        var restoredOid =
            RelationOid("public", OriginalRelation);
        var restoredRelfilenode =
            RelationRelfilenode("public", OriginalRelation);
        Assert.NotEqual(original.ChildOid, restoredOid);

        var reusedApproval = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                RestoreSql.Replace(
                    "'restore-approval'",
                    "'drop-approval'",
                    StringComparison.Ordinal),
                command => ConfigureRestore(
                    command,
                    restoredOid,
                    restoredRelfilenode,
                    predecessorAuthorization
                        .AuthorizationId,
                    predecessorRequest
                        .AuthorizedRestoreToolSha256,
                    predecessorRequest
                        .ValidatorBaseToolSha256,
                    predecessorRequest
                        .AuthorizedArchiveHelperSha256,
                    predecessorRequest
                        .RepairPackageManifestSha256)));
        Assert.Equal("55000", reusedApproval.SqlState);

        Execute(
            """
            CREATE VIEW public.restore_staging_dependency AS
            SELECT *
            FROM
                public.leaderboard_entries_snapshot_pro_cymbals_s1005
            """);
        var dependencyFailure =
            Assert.Throws<PostgresException>(
                () => ExecuteScalar<string>(
                    RestoreSql,
                    command => ConfigureRestore(
                        command,
                        restoredOid,
                        restoredRelfilenode,
                        predecessorAuthorization
                            .AuthorizationId,
                        predecessorRequest
                            .AuthorizedRestoreToolSha256,
                        predecessorRequest
                            .ValidatorBaseToolSha256,
                        predecessorRequest
                            .AuthorizedArchiveHelperSha256,
                        predecessorRequest
                            .RepairPackageManifestSha256)));
        Assert.Equal("55000", dependencyFailure.SqlState);
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_restore_operations
                """));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_inherits
                WHERE inhrelid =
                    'public.leaderboard_entries_snapshot_pro_cymbals_s1005'
                        ::regclass
                """));
        Execute("DROP VIEW public.restore_staging_dependency");

        var result = ExecuteScalar<string>(
            RestoreSql,
            command => ConfigureRestore(
                command,
                restoredOid,
                restoredRelfilenode,
                predecessorAuthorization
                    .AuthorizationId,
                predecessorRequest
                    .AuthorizedRestoreToolSha256,
                predecessorRequest
                    .ValidatorBaseToolSha256,
                predecessorRequest
                    .AuthorizedArchiveHelperSha256,
                predecessorRequest
                    .RepairPackageManifestSha256));

        Assert.Equal(new string('a', 32), result);
        Assert.Equal(
            unrelatedPkOid,
            RelationOid("public", "archived_pk"));
        Assert.Equal(
            unrelatedScoreOid,
            RelationOid("public", "archived_score"));
        Assert.Equal(
            2,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_class
                WHERE oid IN (
                    @pkOid::OID,
                    @scoreOid::OID)
                  AND relname IN (
                    'archived_pk',
                    'archived_score')
                """,
                command =>
                {
                    command.Parameters.AddWithValue(
                        "pkOid",
                        unrelatedPkOid);
                    command.Parameters.AddWithValue(
                        "scoreOid",
                        unrelatedScoreOid);
                }));
        Assert.Equal(
            Scalar<long>(
                """
                SELECT
                    'public.leaderboard_entries_snapshot_pro_cymbals'
                        ::regclass::OID::BIGINT
                """),
            Scalar<long>(
                """
                SELECT inhparent::BIGINT
                FROM pg_inherits
                WHERE inhrelid =
                    'public.leaderboard_entries_snapshot_pro_cymbals_s1005'
                        ::regclass
                """));
        Assert.Equal(
            2,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_index child_index
                JOIN pg_inherits child_inheritance
                  ON child_inheritance.inhrelid =
                        child_index.indexrelid
                WHERE child_index.indrelid =
                        'public.leaderboard_entries_snapshot_pro_cymbals_s1005'
                            ::regclass
                """));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_constraint
                WHERE conrelid =
                        'public.leaderboard_entries_snapshot_pro_cymbals_default'
                            ::regclass
                  AND conname =
                        'ck_sgq_default_1005_0123456789ab'
                """));
        var protectedRestore =
            Assert.Throws<PostgresException>(
                () => ExecuteScalar<string>(
                    """
                    SELECT
                        ensure_leaderboard_snapshot_generation_partition(
                            'Solo_PeripheralCymbals',
                            1005)
                    """));
        Assert.Equal("55000", protectedRestore.SqlState);
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_trigger
                WHERE tgrelid =
                        'public.leaderboard_entries_snapshot_pro_cymbals_s1005'
                            ::regclass
                  AND tgname = 'trg_sgr_1005_aaaaaaaaaaaa'
                  AND NOT tgisinternal
                  AND tgenabled = 'O'
                """));
        var directWrite =
            Assert.Throws<PostgresException>(
                () => Execute(
                    """
                    INSERT INTO
                        public.leaderboard_entries_snapshot_pro_cymbals_s1005 (
                            snapshot_id,
                            song_id,
                            instrument,
                            account_id,
                            score,
                            source,
                            first_seen_at,
                            last_updated_at)
                    VALUES (
                        1005,
                        'guard-test',
                        'Solo_PeripheralCymbals',
                        'guard-test',
                        1,
                        'scrape',
                        now(),
                        now())
                    """));
        Assert.Equal("55000", directWrite.SqlState);

        var continuationRequest =
            BuildContinuationAuthorizationRequest(
                predecessorAuthorization.AuthorizationId,
                predecessorRequest) with
            {
                BaselineRouteManifestSha256 =
                    new string('c', 64),
            };
        await using var continuationDatabase =
            ContinuationAuthorizationDatabase
                .FromConnectionString(
                    _fixture.DataSource.ConnectionString);
        var continuationAuthorization =
            await continuationDatabase.AuthorizeAsync(
                continuationRequest);
        ExecuteScalar<string>(
            $$"""
            SELECT
                fst_record_snapshot_generation_restore_attestation(
                    repeat('a', 32),
                    2005,
                    1005,
                    55,
                    repeat('c', 64),
                    repeat('2', 64),
                    '{{continuationRequest.RouteParityPreflightSha256}}',
                    '{}'::jsonb,
                    repeat('3', 64),
                    'restore-attestor',
                    '{{continuationRequest.AuthorizedContinuationToolSha256}}',
                    '{{continuationAuthorization.ContinuationAuthorizationId}}')
            """);
        ExecuteScalar<string>(
            $$"""
            SELECT
                fst_record_snapshot_generation_restore_attestation(
                    repeat('a', 32),
                    2005,
                    1005,
                    55,
                    repeat('c', 64),
                    repeat('2', 64),
                    '{{continuationRequest.RouteParityPreflightSha256}}',
                    '{}'::jsonb,
                    repeat('3', 64),
                    'restore-attestor',
                    '{{continuationRequest.AuthorizedContinuationToolSha256}}',
                    '{{continuationAuthorization.ContinuationAuthorizationId}}')
            """);
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_restore_attestations
                WHERE restore_operation_id = repeat('a', 32)
                """));
        Execute(
            """
            UPDATE service_worker_status
            SET status = 'running',
                current_operation_json = '{}'::jsonb
            WHERE worker_key = 'scraper'
            """);
        var unsafeFinalize =
            Assert.Throws<PostgresException>(
                () => ExecuteScalar<string>(
                    $$"""
                    SELECT fst_finalize_snapshot_generation_restore(
                        repeat('a', 32),
                        'restore-operator',
                        'restore-complete',
                        '{}'::jsonb,
                        '{{continuationRequest.AuthorizedContinuationToolSha256}}',
                        '{{continuationAuthorization.ContinuationAuthorizationId}}')
                    """));
        Assert.Equal("55000", unsafeFinalize.SqlState);
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_retention_holds hold_row
                JOIN snapshot_generation_restore_operations
                    restore_row
                  ON restore_row.hold_id = hold_row.hold_id
                WHERE restore_row.restore_operation_id =
                        repeat('a', 32)
                  AND hold_row.released_at IS NULL
                """));
        Execute(
            """
            UPDATE service_worker_status
            SET status = 'offline',
                current_operation_json = NULL
            WHERE worker_key = 'scraper'
            """);
        ExecuteScalar<string>(
            $$"""
            SELECT fst_finalize_snapshot_generation_restore(
                repeat('a', 32),
                'restore-operator',
                'restore-complete',
                '{}'::jsonb,
                '{{continuationRequest.AuthorizedContinuationToolSha256}}',
                '{{continuationAuthorization.ContinuationAuthorizationId}}')
            """);
        ExecuteScalar<string>(
            $$"""
            SELECT fst_finalize_snapshot_generation_restore(
                repeat('a', 32),
                'restore-operator',
                'restore-complete',
                '{}'::jsonb,
                '{{continuationRequest.AuthorizedContinuationToolSha256}}',
                '{{continuationAuthorization.ContinuationAuthorizationId}}')
            """);
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_retention_holds hold_row
                JOIN snapshot_generation_drop_operations drop_row
                  ON drop_row.hold_id = hold_row.hold_id
                WHERE drop_row.drop_operation_id =
                        'fedcba9876543210fedcba9876543210'
                  AND hold_row.released_at IS NOT NULL
                """));
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_restore_finalizations
                WHERE restore_operation_id = repeat('a', 32)
                """));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM pg_trigger
                WHERE tgrelid =
                        'public.leaderboard_entries_snapshot_pro_cymbals_s1005'
                            ::regclass
                  AND tgname = 'trg_sgr_1005_aaaaaaaaaaaa'
                  AND NOT tgisinternal
                """));
        Assert.Equal(
            OriginalRelation,
            ExecuteScalar<string>(
                """
                SELECT
                    ensure_leaderboard_snapshot_generation_partition(
                        'Solo_PeripheralCymbals',
                        1005)
                """));
        Execute(
            """
            SET session_replication_role = replica;
            UPDATE snapshot_generation_restore_operations
            SET restored_child_relfilenode =
                    restored_child_relfilenode + 1
            WHERE restore_operation_id = repeat('a', 32);
            SET session_replication_role = origin
            """);
        Assert.Equal(
            restoredOid,
            RelationOid("public", OriginalRelation));
        Assert.Equal(
            OriginalRelation,
            ExecuteScalar<string>(
                """
                SELECT
                    ensure_leaderboard_snapshot_generation_partition(
                        'Solo_PeripheralCymbals',
                        1005)
                """));

        Execute(
            """
            SET session_replication_role = replica;
            UPDATE snapshot_generation_restore_operations
            SET restored_child_oid = restored_child_oid + 1
            WHERE restore_operation_id = repeat('a', 32);
            SET session_replication_role = origin
            """);
        var wrongOid = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                """
                SELECT
                    ensure_leaderboard_snapshot_generation_partition(
                        'Solo_PeripheralCymbals',
                        1005)
                """));
        Assert.Equal("55000", wrongOid.SqlState);
    }

    [Fact]
    public async Task FailedQuarantineRollsBackHoldAndDdl()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();

        var failure = Assert.Throws<PostgresException>(
            () => ExecuteScalar<string>(
                QuarantineSql,
                command => ConfigureQuarantine(
                    command,
                    identity,
                    expectedRowCount: 2)));

        Assert.Equal("55000", failure.SqlState);
        Assert.True(RelationExists("public", OriginalRelation));
        Assert.False(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelation));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_quarantine_operations
                """));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_retention_holds
                WHERE hold_kind = 'retention_in_flight'
                """));
    }

    [Fact]
    public async Task ExecutorRunsLockedFingerprintQuarantineAndRollback()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        await using var database =
            QuarantineDatabase.FromConnectionString(
                _fixture.DataSource.ConnectionString);
        var (plan, parity) =
            await BuildExecutorPlanAsync(
                database,
                identity);

        var quarantine = await database.QuarantineAsync(
            plan,
            "test-operator",
            "test-approval");
        Assert.Equal("quarantined", quarantine.Status);
        var quarantinedState =
            await database.ReadOperationStateAsync(
                plan.OperationId!);
        Assert.False(quarantinedState.Reattached);
        Assert.True(quarantinedState.ExactCheckPresent);
        Assert.True(
            quarantinedState.MutationGuardPresent);
        Assert.True(
            quarantinedState.DefaultExclusionPresent);

        var attestationParity = parity with
        {
            BaselineManifestPath =
                parity.CandidateManifestPath,
            BaselineManifestSha256 =
                parity.CandidateManifestSha256,
            CandidateManifestPath =
                "/evidence/routes/post-quarantine/manifest.json",
            CandidateManifestSha256 = new('8', 64),
        };
        await database.RecordAttestationAsync(
            plan,
            "quarantined",
            "test-operator",
            attestationParity);
        RotatePublicationForRollbackTest();
        await database.RecordAttestationAsync(
            plan,
            "soak",
            "test-operator",
            attestationParity with
            {
                PublicationId = 2006,
                PublishedScrapeId = 1006,
                CandidateManifestPath =
                    "/evidence/routes/soak/manifest.json",
                CandidateManifestSha256 =
                    new('9', 64),
            });
        var reattach = await database.ReattachAsync(
            plan,
            "test-operator",
            "test-rollback");

        Assert.Equal("reattached", reattach.Status);
        var reattachedState =
            await database.ReadOperationStateAsync(
                plan.OperationId!);
        Assert.True(reattachedState.Reattached);
        Assert.False(reattachedState.ExactCheckPresent);
        Assert.False(
            reattachedState.MutationGuardPresent);
        Assert.False(
            reattachedState.DefaultExclusionPresent);
        Assert.Equal(
            identity.ChildOid,
            reattachedState.CurrentOid);
        Assert.Equal(
            identity.RootOid,
            reattachedState.CurrentParentOid);
        await database.RecordAttestationAsync(
            plan,
            "reattached",
            "test-operator",
            attestationParity with
            {
                PublicationId = 2006,
                PublishedScrapeId = 1006,
                BaselineManifestPath =
                    "/evidence/routes/soak/manifest.json",
                BaselineManifestSha256 =
                    new('9', 64),
                CandidateManifestPath =
                    "/evidence/routes/reattached/manifest.json",
                CandidateManifestSha256 =
                    new('a', 64),
            });
    }

    [Fact]
    public async Task ReattachIndexRenamesDoNotStrongLockUnrelatedObjects()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        await using var database =
            QuarantineDatabase.FromConnectionString(
                _fixture.DataSource.ConnectionString);
        var (plan, parity) =
            await BuildExecutorPlanAsync(
                database,
                identity);
        await database.QuarantineAsync(
            plan,
            "test-operator",
            "test-approval");
        var attestationParity = parity with
        {
            BaselineManifestPath =
                parity.CandidateManifestPath,
            BaselineManifestSha256 =
                parity.CandidateManifestSha256,
            CandidateManifestPath =
                "/evidence/routes/post-quarantine/manifest.json",
            CandidateManifestSha256 = new('8', 64),
        };
        await database.RecordAttestationAsync(
            plan,
            "quarantined",
            "test-operator",
            attestationParity);
        await database.RecordAttestationAsync(
            plan,
            "soak",
            "test-operator",
            attestationParity with
            {
                BaselineManifestSha256 =
                    new('8', 64),
                CandidateManifestSha256 =
                    new('9', 64),
            });
        Execute(
            """
            CREATE TABLE public.unrelated_reattach_lock (
                value INTEGER NOT NULL);
            CREATE INDEX unrelated_reattach_lock_idx
                ON public.unrelated_reattach_lock (value);
            """);
        var unrelatedTableOid = RelationOid(
            "public",
            "unrelated_reattach_lock");
        var unrelatedIndexOid = RelationOid(
            "public",
            "unrelated_reattach_lock_idx");
        var targetIndexOids = new[]
        {
            Scalar<long>(
                $"""
                SELECT index_oid
                FROM snapshot_generation_quarantine_index_renames
                WHERE operation_id =
                        '{plan.OperationId}'
                  AND index_role = 'pk'
                """),
            Scalar<long>(
                $"""
                SELECT index_oid
                FROM snapshot_generation_quarantine_index_renames
                WHERE operation_id =
                        '{plan.OperationId}'
                  AND index_role = 'score'
                """),
        };
        var reached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        QuarantineDatabase.ReattachTestHook =
            point =>
            {
                if (point !=
                    "after-reattach-before-commit")
                {
                    return;
                }
                reached.TrySetResult();
                if (!release.Wait(
                        TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException(
                        "Reattach lock inspection was not released.");
                }
            };
        IReadOnlyList<(long Oid, string Mode)> locks =
            [];
        var reattachTask = database.ReattachAsync(
            plan,
            "test-operator",
            "lock-observation");
        try
        {
            await reached.Task.WaitAsync(
                TimeSpan.FromSeconds(30));
            await using var connection =
                await _fixture.DataSource
                    .OpenConnectionAsync();
            await using var command =
                connection.CreateCommand();
            command.CommandText = """
                SELECT
                    lock_row.relation::BIGINT,
                    lock_row.mode
                FROM pg_locks lock_row
                JOIN pg_stat_activity activity
                  ON activity.pid = lock_row.pid
                WHERE lock_row.locktype = 'relation'
                  AND lock_row.granted
                  AND activity.datname =
                        current_database()
                  AND activity.application_name =
                        'fst-snapshot-generation-quarantine'
                  AND activity.state =
                        'idle in transaction'
                  AND lock_row.relation =
                        ANY(@relationOids)
                ORDER BY
                    lock_row.relation,
                    lock_row.mode
                """;
            command.Parameters.AddWithValue(
                "relationOids",
                targetIndexOids
                    .Concat(
                    [
                        unrelatedTableOid,
                        unrelatedIndexOid,
                    ])
                    .ToArray());
            var observed =
                new List<(long Oid, string Mode)>();
            await using var reader =
                await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                observed.Add(
                    (reader.GetInt64(0),
                     reader.GetString(1)));
            }
            locks = observed;
            release.Set();
            await reattachTask;
        }
        finally
        {
            release.Set();
            QuarantineDatabase.ReattachTestHook =
                null;
        }

        Assert.All(
            targetIndexOids,
            oid => Assert.Contains(
                locks,
                item => item.Oid == oid));
        Assert.DoesNotContain(
            locks,
            item =>
                item.Oid is var oid
                && (oid == unrelatedTableOid
                    || oid == unrelatedIndexOid)
                && item.Mode is
                    "ShareUpdateExclusiveLock"
                    or "ShareLock"
                    or "AccessExclusiveLock");
    }

    [Fact]
    public async Task PythonAcceptsCSharpCanonicalDropPlanBytes()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        var plan = await PrepareDropPlanAsync(identity);
        var fixture = (plan with
        {
            GeneratedAtUtc = new DateTimeOffset(
                2026,
                8,
                31,
                14,
                0,
                0,
                TimeSpan.Zero),
            RecoveryBundlePath =
                "/evidence/C++/operator's <bundle>&",
            BinaryPath =
                "/evidence/drop+operator's.dll",
            ActiveQuarantineReport =
                plan.ActiveQuarantineReport with
                {
                    Reference =
                        "approval+operator's <reference>&",
                    ReportSha256 = null,
                },
            PlanDigest = null,
            DropOperationId = null,
        }).Seal();
        fixture.Validate();
        var planPath = Path.Combine(
            AppContext.BaseDirectory,
            $"drop-plan-canonical-{Guid.NewGuid():N}.json");
        var repository = FindRepositoryRoot();
        try
        {
            FstSnapshotGenerationDrop
                .DropEvidenceValidator.WriteNewCanonical(
                    planPath,
                    fixture);
            var raw = File.ReadAllText(planPath);
            Assert.Contains("\\u002B", raw);
            Assert.Contains("\\u0027", raw);
            Assert.Contains(
                "14:00:00\\u002B00:00",
                raw);
            Assert.DoesNotContain(
                "\"reportSha256\":null",
                raw,
                StringComparison.Ordinal);

            var start = new ProcessStartInfo(
                "python3")
            {
                WorkingDirectory = repository,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.Environment[
                "PYTHONDONTWRITEBYTECODE"] = "1";
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(
                """
                import importlib.util
                import pathlib
                import sys
                spec = importlib.util.spec_from_file_location(
                    "restore_tool", sys.argv[1])
                module = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(module)
                plan = module.validate_drop_plan(
                    pathlib.Path(sys.argv[2]),
                    sys.argv[3],
                    sys.argv[4])
                print(plan["dropOperationId"])
                """);
            start.ArgumentList.Add(
                Path.Combine(
                    repository,
                    "tools",
                    "postgres-snapshot-generation-restore.py"));
            start.ArgumentList.Add(planPath);
            start.ArgumentList.Add(fixture.PlanDigest!);
            start.ArgumentList.Add(
                fixture.DropOperationId!);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException(
                    "Python restore validator did not start.");
            var standardOutput =
                await process.StandardOutput.ReadToEndAsync();
            var standardError =
                await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.True(
                process.ExitCode == 0,
                standardError);
            Assert.Equal(
                fixture.DropOperationId,
                standardOutput.Trim());
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
        }
    }

    [Fact]
    public async Task DropExecutorReadsExactPrivateCandidateState()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        await using var quarantineDatabase =
            QuarantineDatabase.FromConnectionString(
                _fixture.DataSource.ConnectionString);
        var (quarantinePlan, _) =
            await BuildExecutorPlanAsync(
                quarantineDatabase,
                identity);
        await quarantineDatabase.QuarantineAsync(
            quarantinePlan,
            "test-operator",
            "test-approval");
        await using var dropDatabase =
            FstSnapshotGenerationDrop.DropDatabase
                .FromConnectionString(
                    _fixture.DataSource.ConnectionString);

        var snapshot = await dropDatabase.ReadSnapshotAsync(
            quarantinePlan);

        Assert.True(snapshot.PrivateRelationExists);
        Assert.True(snapshot.OriginalRelationAbsent);
        Assert.True(snapshot.Detached);
        Assert.True(snapshot.ExactHoldActive);
        Assert.True(snapshot.DefaultIdentityValid);
        Assert.True(snapshot.DefaultExclusionPresent);
        Assert.Equal(0, snapshot.DefaultRowCount);
        Assert.Equal(identity.ChildOid, snapshot.CurrentChildOid);
        Assert.Equal(
            identity.ChildRelfilenode,
            snapshot.CurrentChildRelfilenode);
        Assert.Equal(1, snapshot.CurrentRowCount);
        Assert.Equal(2, snapshot.ChildIndexCount);
    }

    [Fact]
    public async Task DropCanarySelectionUsesCurrentPhysicalBytes()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        await using var database =
            FstSnapshotGenerationDrop.DropDatabase
                .FromConnectionString(
                    _fixture.DataSource.ConnectionString);

        var candidate = await database.SelectCanaryAsync();

        Assert.Equal(
            "Solo_PeripheralCymbals",
            candidate.Instrument);
        Assert.Equal(1005, candidate.SnapshotId);
        Assert.Equal(identity.ChildOid, candidate.ChildOid);
        Assert.True(candidate.TotalBytes > 0);
        Assert.Equal(1, candidate.RowCount);
    }

    [Fact]
    public async Task DropExecutorCommitsAndConfirmsExactOperation()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        await using var quarantineDatabase =
            QuarantineDatabase.FromConnectionString(
                _fixture.DataSource.ConnectionString);
        var (activePlan, parity) =
            await BuildExecutorPlanAsync(
                quarantineDatabase,
                identity);
        var activeReport =
            await quarantineDatabase.QuarantineAsync(
                activePlan,
                "q2-operator",
                "q2-approval");
        var rehearsalPlan = (activePlan with
        {
            GeneratedAtUtc =
                activePlan.GeneratedAtUtc.AddMinutes(-60),
            PlanDigest = null,
            OperationId = null,
        }).Seal();
        SeedDropPrerequisites(
            activePlan.OperationId!,
            rehearsalPlan.OperationId!,
            rehearsalPlan.PlanDigest!);

        await using var dropDatabase =
            FstSnapshotGenerationDrop.DropDatabase
                .FromConnectionString(
                    _fixture.DataSource.ConnectionString);
        var snapshot = await dropDatabase.ReadSnapshotAsync(
            activePlan);
        var started =
            DateTimeOffset.UtcNow.AddMinutes(-31);
        var health =
            new FstSnapshotGenerationDrop
                .SnapshotGenerationHealthEvidence(
                    1,
                    "fst.snapshot-generation-drop-health.v1",
                    started,
                    started.AddMinutes(30),
                    30,
                    60,
                    2005,
                    1005,
                    true,
                    Enumerable.Range(0, 60)
                        .Select(index =>
                            new FstSnapshotGenerationDrop
                                .SnapshotGenerationHealthSample(
                                    started.AddSeconds(
                                        index * 30),
                                    2005,
                                    1005,
                                    true,
                                    true,
                                    false,
                                    0,
                                    0))
                        .ToArray(),
                    null).Seal();
        var q1Quarantined = BuildAttestationReport(
            rehearsalPlan,
            9101,
            "quarantined",
            parity);
        var q1Soak = BuildAttestationReport(
            rehearsalPlan,
            9102,
            "soak",
            parity with
            {
                PublicationId = 2006,
                PublishedScrapeId = 1006,
            });
        var q1Reattached = BuildAttestationReport(
            rehearsalPlan,
            9103,
            "reattached",
            parity with
            {
                PublicationId = 2006,
                PublishedScrapeId = 1006,
            });
        var q2Quarantined = BuildAttestationReport(
            activePlan,
            9201,
            "quarantined",
            parity);
        var q2Soak = BuildAttestationReport(
            activePlan,
            9202,
            "soak",
            parity);
        var rehearsalQuarantine = BuildExecutionReport(
            rehearsalPlan,
            "quarantine",
            "quarantined",
            "q1-operator",
            "q1-approval");
        var rehearsalReattach = BuildExecutionReport(
            rehearsalPlan,
            "reattach",
            "reattached",
            "q1-operator",
            "q1-reattach");
        var semanticEvidence =
            BuildSemanticEvidence(
                identity,
                activePlan.OperationId!);
        var plan =
            new FstSnapshotGenerationDrop
                .SnapshotGenerationDropPlan(
                    1,
                    SnapshotGenerationDropContract.ToolId,
                    DateTimeOffset.UtcNow,
                    true,
                    rehearsalPlan,
                    activePlan,
                    rehearsalQuarantine,
                    rehearsalReattach,
                    activeReport,
                    q1Quarantined,
                    q1Soak,
                    q1Reattached,
                    q2Quarantined,
                    q2Soak,
                    semanticEvidence,
                    semanticEvidence,
                    parity,
                    health,
                    snapshot,
                    "/evidence/recovery",
                    new('1', 64),
                    2L * 1024 * 1024 * 1024,
                    0,
                    "/evidence/drop.dll",
                    new('2', 64),
                    "/evidence/restore.py",
                    new('3', 64),
                    new('4', 64),
                    new('5', 40),
                    "/evidence/archive/fresh-proof.json",
                    new('6', 64),
                    DateTimeOffset.UtcNow,
                    null,
                    null).Seal();
        Assert.Throws<InvalidDataException>(
            () => (plan with
            {
                CapacityReserveBytes = 1,
            }).Validate());

        await dropDatabase.ValidateQuarantineChainAsync(
            plan);
        var report = await dropDatabase.DropAsync(
            plan,
            "drop-operator",
            "drop-approval");
        var repeated = await dropDatabase.DropAsync(
            plan,
            "confirming-operator",
            "confirming-reference");

        Assert.Equal("dropped", report.Status);
        Assert.Equal("committed", report.CommitOutcome);
        Assert.Equal(
            "already-committed",
            repeated.CommitOutcome);
        Assert.Equal("drop-operator", repeated.Actor);
        Assert.Equal("drop-approval", repeated.Reference);
        var attestation =
            await dropDatabase.RecordAttestationAsync(
                plan,
                "dropped",
                "drop-operator",
                parity with
                {
                    BaselineManifestSha256 =
                        plan.PreDropParity
                            .CandidateManifestSha256,
                    CandidateManifestSha256 =
                        new('9', 64),
                });
        Assert.True(attestation.AttestationId > 0);
        Assert.False(RelationExists(
            SnapshotGenerationQuarantineContract
                .QuarantineSchema,
            QuarantineRelationFor(
                activePlan.OperationId!)));
    }

    [Fact]
    public async Task DropExecutorRejectsCatalogDriftAfterPlan()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        var plan = await PrepareDropPlanAsync(identity);
        var privateRelation =
            QuarantineRelationFor(
                plan.ActivePlan.OperationId!);
        Execute(
            $"""
            ALTER TABLE
                fst_snapshot_quarantine.{privateRelation}
                ADD COLUMN unexpected_catalog_drift INTEGER
            """);
        await using var dropDatabase =
            FstSnapshotGenerationDrop.DropDatabase
                .FromConnectionString(
                    _fixture.DataSource.ConnectionString);

        var failure =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => dropDatabase.DropAsync(
                    plan,
                    "drop-operator",
                    "drop-approval"));

        Assert.Contains(
            "topology",
            failure.Message,
            StringComparison.Ordinal);
        Assert.True(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                privateRelation));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_drop_operations
                """));
    }

    [Theory]
    [InlineData("after-drop-before-commit", false)]
    [InlineData("after-commit", true)]
    public async Task DropExecutorReconcilesCommitBoundaryFailure(
        string failurePoint,
        bool committed)
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        var plan = await PrepareDropPlanAsync(identity);
        await using var dropDatabase =
            FstSnapshotGenerationDrop.DropDatabase
                .FromConnectionString(
                    _fixture.DataSource.ConnectionString);
        FstSnapshotGenerationDrop.DropDatabase.DropTestHook =
            point =>
            {
                if (point == failurePoint)
                    throw new IOException(
                        $"Injected {failurePoint} failure.");
            };
        try
        {
            if (committed)
            {
                var report = await dropDatabase.DropAsync(
                    plan,
                    "drop-operator",
                    "drop-approval");
                Assert.Equal(
                    "reconciled-committed",
                    report.CommitOutcome);
                Assert.False(
                    RelationExists(
                        SnapshotGenerationQuarantineContract
                            .QuarantineSchema,
                        QuarantineRelationFor(
                            plan.ActivePlan.OperationId!)));
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(
                    () => dropDatabase.DropAsync(
                        plan,
                        "drop-operator",
                        "drop-approval"));
                Assert.True(
                    RelationExists(
                        SnapshotGenerationQuarantineContract
                            .QuarantineSchema,
                        QuarantineRelationFor(
                            plan.ActivePlan.OperationId!)));
                Assert.Equal(
                    0,
                    Scalar<int>(
                        """
                        SELECT COUNT(*)::INTEGER
                        FROM snapshot_generation_drop_operations
                        """));
            }
        }
        finally
        {
            FstSnapshotGenerationDrop.DropDatabase
                .DropTestHook = null;
        }
    }

    [Fact]
    public async Task DropExecutorHoldsOnlyExactPrivateAndDefaultRelations()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        ExecuteScalar<string>(
            """
            SELECT ensure_leaderboard_snapshot_generation_partition(
                'Solo_PeripheralCymbals',
                1004)
            """);
        var topOid = Scalar<long>(
            """
            SELECT
                'public.leaderboard_entries_snapshot'
                    ::regclass::OID::BIGINT
            """);
        var defaultOid = RelationOid(
            "public",
            "leaderboard_entries_snapshot_pro_cymbals_default");
        var siblingOid = RelationOid(
            "public",
            "leaderboard_entries_snapshot_pro_cymbals_s1004");
        var plan = await PrepareDropPlanAsync(identity);
        await using var dropDatabase =
            FstSnapshotGenerationDrop.DropDatabase
                .FromConnectionString(
                    _fixture.DataSource.ConnectionString);
        var reached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        FstSnapshotGenerationDrop.DropDatabase.DropTestHook =
            point =>
            {
                if (point != "after-drop-before-commit")
                    return;
                reached.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException(
                        "DROP lock inspection was not released.");
                }
            };
        IReadOnlyList<(long RelationOid, string Mode)> locks =
            [];
        var dropTask = dropDatabase.DropAsync(
            plan,
            "drop-operator",
            "drop-approval");
        try
        {
            await reached.Task.WaitAsync(
                TimeSpan.FromSeconds(30));
            await using var connection =
                await _fixture.DataSource
                    .OpenConnectionAsync();
            await using var command =
                connection.CreateCommand();
            command.CommandText = """
                SELECT
                    lock_row.relation::BIGINT,
                    lock_row.mode
                FROM pg_locks lock_row
                JOIN pg_stat_activity activity
                  ON activity.pid = lock_row.pid
                WHERE lock_row.locktype = 'relation'
                  AND lock_row.granted
                  AND activity.datname = current_database()
                  AND activity.application_name =
                        'fst-snapshot-generation-drop'
                  AND activity.state = 'idle in transaction'
                  AND lock_row.relation = ANY(@relationOids)
                ORDER BY
                    lock_row.relation,
                    lock_row.mode
                """;
            command.Parameters.AddWithValue(
                "relationOids",
                new[]
                {
                    topOid,
                    identity.RootOid,
                    defaultOid,
                    siblingOid,
                    identity.ChildOid,
                });
            var observed =
                new List<(long RelationOid, string Mode)>();
            await using var reader =
                await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                observed.Add(
                    (reader.GetInt64(0),
                     reader.GetString(1)));
            }
            locks = observed;
            release.Set();
            await dropTask;
        }
        finally
        {
            release.Set();
            FstSnapshotGenerationDrop.DropDatabase
                .DropTestHook = null;
        }

        Assert.Contains(
            (identity.ChildOid, "AccessExclusiveLock"),
            locks);
        Assert.Contains(
            (defaultOid, "ShareLock"),
            locks);
        Assert.DoesNotContain(
            (defaultOid, "AccessExclusiveLock"),
            locks);
        Assert.DoesNotContain(
            locks,
            item => item.RelationOid == topOid
                && item.Mode is
                    "ShareLock"
                    or "AccessExclusiveLock");
        Assert.DoesNotContain(
            locks,
            item => item.RelationOid == identity.RootOid
                && item.Mode is
                    "ShareLock"
                    or "AccessExclusiveLock");
        Assert.DoesNotContain(
            locks,
            item => item.RelationOid == siblingOid
                && item.Mode is
                    "ShareLock"
                    or "AccessExclusiveLock");
        Assert.Equal(
            [identity.ChildOid],
            locks
                .Where(item =>
                    item.Mode == "AccessExclusiveLock")
                .Select(item => item.RelationOid)
                .Distinct()
                .ToArray());
    }

    [Fact]
    public async Task DropExecutorTakesSnapshotAfterWaitingForLockChain()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        var plan = await PrepareDropPlanAsync(identity);
        await using var blocker =
            await _fixture.DataSource.OpenConnectionAsync();
        await using (var acquire = blocker.CreateCommand())
        {
            acquire.CommandText =
                "SELECT pg_advisory_lock(@key)";
            acquire.Parameters.AddWithValue(
                "key",
                SnapshotGenerationQuarantineContract
                    .PublicationAdvisoryLockKey);
            await acquire.ExecuteNonQueryAsync();
        }
        await using var database =
            FstSnapshotGenerationDrop.DropDatabase
                .FromConnectionString(
                    _fixture.DataSource.ConnectionString);
        var task = database.DropAsync(
            plan,
            "drop-operator",
            "drop-approval");
        await Task.Delay(250);
        await using (var mutate = blocker.CreateCommand())
        {
            mutate.CommandText = """
                UPDATE scrape_publication_state
                SET public_reads_frozen = TRUE,
                    public_reads_frozen_at = now(),
                    public_reads_frozen_scrape_id = 1005,
                    public_reads_frozen_reason = 'test'
                WHERE id = TRUE
                """;
            await mutate.ExecuteNonQueryAsync();
        }
        await using (var release = blocker.CreateCommand())
        {
            release.CommandText =
                "SELECT pg_advisory_unlock(@key)";
            release.Parameters.AddWithValue(
                "key",
                SnapshotGenerationQuarantineContract
                    .PublicationAdvisoryLockKey);
            Assert.True(
                (bool)(await release.ExecuteScalarAsync())!);
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => task);
        Assert.True(
            RelationExists(
                SnapshotGenerationQuarantineContract
                    .QuarantineSchema,
                QuarantineRelationFor(
                    plan.ActivePlan.OperationId!)));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_drop_operations
                """));
    }

    [Fact]
    public async Task DropConfirmRejectsMixedCommittedState()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        var plan = await PrepareDropPlanAsync(identity);
        await using var database =
            FstSnapshotGenerationDrop.DropDatabase
                .FromConnectionString(
                    _fixture.DataSource.ConnectionString);
        await database.DropAsync(
            plan,
            "drop-operator",
            "drop-approval");
        Execute(
            """
            CREATE TABLE
                public.leaderboard_entries_snapshot_pro_cymbals_s1005
                (
                    LIKE
                        public.leaderboard_entries_snapshot_pro_cymbals
                    INCLUDING ALL
                )
            """);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => database.DropAsync(
                plan,
                "drop-operator",
                "drop-approval"));
        Assert.Equal(
            1,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_drop_operations
                """));
    }

    [Fact]
    public async Task ExecutorTakesSnapshotAfterWaitingForLockChain()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var identity = SeedAcceptedCandidate();
        await using var database =
            QuarantineDatabase.FromConnectionString(
                _fixture.DataSource.ConnectionString);
        var (plan, _) = await BuildExecutorPlanAsync(
            database,
            identity);
        await using var blocker =
            await _fixture.DataSource.OpenConnectionAsync();
        await using (var acquire = blocker.CreateCommand())
        {
            acquire.CommandText =
                "SELECT pg_advisory_lock(@lockKey)";
            acquire.Parameters.AddWithValue(
                "lockKey",
                SnapshotGenerationQuarantineContract
                    .PublicationAdvisoryLockKey);
            await acquire.ExecuteNonQueryAsync();
        }

        var quarantineTask = database.QuarantineAsync(
            plan,
            "test-operator",
            "test-approval");
        await Task.Delay(250);
        RotatePublicationForRollbackTest();
        await using (var release = blocker.CreateCommand())
        {
            release.CommandText =
                "SELECT pg_advisory_unlock(@lockKey)";
            release.Parameters.AddWithValue(
                "lockKey",
                SnapshotGenerationQuarantineContract
                    .PublicationAdvisoryLockKey);
            Assert.True(
                (bool)(await release.ExecuteScalarAsync())!);
        }

        var failure =
            await Assert.ThrowsAsync<PostgresException>(
                () => quarantineTask);
        Assert.Contains(
            "Publication state changed before fingerprint locking",
            failure.Message,
            StringComparison.Ordinal);
        Assert.True(RelationExists("public", OriginalRelation));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_quarantine_operations
                """));
        Assert.Equal(
            0,
            Scalar<int>(
                """
                SELECT COUNT(*)::INTEGER
                FROM snapshot_generation_retention_holds
                WHERE hold_kind = 'retention_in_flight'
                """));
    }

    private static async Task<(
        SnapshotGenerationQuarantinePlan Plan,
        RouteParityEvidence Parity)> BuildExecutorPlanAsync(
        QuarantineDatabase database,
        CandidateIdentity identity)
    {
        var archive = new ArchivePackageEvidence(
            PackagePath: "/evidence/archive",
            PackageManifestSha256: new('1', 64),
            ArchiveSha256: new('2', 64),
            ProofManifestPath: "/evidence/archive/proof.json",
            ProofManifestSha256: new('3', 64),
            CycleId: 3005,
            TriggerScrapeId: 1005,
            TriggerPublicationId: 2005,
            CandidateIdentityHash: new('b', 64),
            ObservationHash: new('c', 64),
            ObservationId: 3005,
            Instrument: "Solo_PeripheralCymbals",
            SnapshotId: 1005,
            RootSchema: "public",
            RootRelation:
                "leaderboard_entries_snapshot_pro_cymbals",
            RootOid: identity.RootOid,
            ChildSchema: "public",
            ChildRelation: OriginalRelation,
            ChildOid: identity.ChildOid,
            ChildRelfilenode: identity.ChildRelfilenode,
            StableChildIdentityHash: new('d', 64),
            StableConfigSchemaHash: new('e', 64),
            RowCount: 0,
            RowFingerprintSha256: new('0', 64),
            LogicalCatalogSha256: new('7', 64),
            TotalBytes: 0,
            DatabaseName: "",
            DatabaseOid: 0,
            SystemIdentifier: "",
            ServerVersionNum: 0);
        var snapshot = await database.ReadSnapshotAsync(
            archive);
        var fingerprint =
            await database.ComputeFingerprintAsync(
                archive);
        archive = archive with
        {
            RowCount = fingerprint.RowCount,
            RowFingerprintSha256 = fingerprint.Sha256,
            TotalBytes = snapshot.CurrentTotalBytes,
            DatabaseName = snapshot.DatabaseName,
            DatabaseOid = snapshot.DatabaseOid,
            SystemIdentifier = snapshot.SystemIdentifier,
            ServerVersionNum = snapshot.ServerVersionNum,
        };
        var source = new SourceScrapeEvidence(
            ManifestPath: "/evidence/source/manifest.json",
            ManifestSha256: new('4', 64),
            ScrapeId: 1005,
            PublishedScrapeId: 1005,
            SongCount: 1,
            TotalEntries: 1,
            ScopeCount: 1,
            PublishedScopeCount: 1,
            PublishedRowCount: 1);
        var parity = new RouteParityEvidence(
            BaselineManifestPath:
                "/evidence/routes/baseline/manifest.json",
            BaselineManifestSha256: new('5', 64),
            CandidateManifestPath:
                "/evidence/routes/candidate/manifest.json",
            CandidateManifestSha256: new('6', 64),
            PublicationId: 2005,
            PublishedScrapeId: 1005,
            RouteCount: 55,
            StatusParity: true,
            SemanticJsonParity: true,
            DifferenceCount: 0);
        QuarantineDatabase.ValidateSnapshot(
            snapshot,
            archive,
            source,
            parity);
        return (
            new SnapshotGenerationQuarantinePlan(
                SchemaVersion: 1,
                ToolId:
                    SnapshotGenerationQuarantineContract.ToolId,
                GeneratedAtUtc: DateTimeOffset.UtcNow,
                Archive: archive,
                SourceScrape: source,
                PreQuarantineParity: parity,
                Database: snapshot,
                ExplicitApprovalRequired: true,
                PlanDigest: null,
                OperationId: null).Seal(),
            parity);
    }

    private static SnapshotGenerationQuarantineExecutionReport
        BuildExecutionReport(
            SnapshotGenerationQuarantinePlan plan,
            string action,
            string status,
            string actor,
            string reference) =>
        new SnapshotGenerationQuarantineExecutionReport(
            1,
            SnapshotGenerationQuarantineContract.ToolId,
            action,
            plan.OperationId!,
            plan.PlanDigest!,
            status,
            DateTimeOffset.UtcNow,
            actor,
            reference,
            plan.Database.DatabaseName,
            plan.Database.SystemIdentifier,
            plan.Archive.TriggerPublicationId,
            plan.Archive.TriggerScrapeId,
            plan.Archive.Instrument,
            plan.Archive.SnapshotId,
            plan.Archive.ChildRelation,
            status == "quarantined"
                ? $"{SnapshotGenerationQuarantineContract.QuarantineSchema}.{QuarantineRelationFor(plan.OperationId!)}"
                : null,
            plan.Archive.ChildOid,
            plan.Archive.ChildRelfilenode,
            plan.Archive.RowCount,
            plan.Archive.RowFingerprintSha256,
            JsonDocument.Parse("{}").RootElement.Clone())
        .Seal();

    private FstSnapshotGenerationDrop
        .SnapshotGenerationArchiveSemanticEvidence
        BuildSemanticEvidence(
            CandidateIdentity identity,
            string operationId)
    {
        var physicalIndexes = new Dictionary<
            string,
            (long Oid,
             long Relfilenode,
             long ParentRootOid,
             long ParentTopOid)>(
                StringComparer.Ordinal);
        using (var connection =
               _fixture.DataSource.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    index_role,
                    index_oid,
                    index_relfilenode,
                    (
                        semantic_before #>>
                            '{expectedParentIndexOid}'
                        )::BIGINT,
                    (
                        semantic_before #>>
                            '{expectedTopIndexOid}'
                        )::BIGINT
                FROM
                    snapshot_generation_quarantine_index_renames
                WHERE operation_id = @operationId
                ORDER BY index_role
                """;
            command.Parameters.AddWithValue(
                "operationId",
                operationId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                physicalIndexes[reader.GetString(0)] =
                    (reader.GetInt64(1),
                     reader.GetInt64(2),
                     reader.GetInt64(3),
                     reader.GetInt64(4));
            }
        }
        var indexes = new[]
        {
            new FstSnapshotGenerationDrop
                .SnapshotGenerationSemanticIndexEvidence(
                    "pk",
                    physicalIndexes["pk"].Oid,
                    physicalIndexes["pk"]
                        .Relfilenode,
                    true,
                    true,
                    true,
                    true,
                    "btree",
                    "pg_default",
                    [
                        "snapshot_id",
                        "song_id",
                        "instrument",
                        "account_id",
                    ],
                    ["asc", "asc", "asc", "asc"],
                    ["last", "last", "last", "last"],
                    ["default", "default", "default", "default"],
                    ["default", "default", "default", "default"],
                    null,
                    null,
                    physicalIndexes["pk"]
                        .ParentRootOid,
                    physicalIndexes["pk"]
                        .ParentTopOid,
                    "pk"),
            new FstSnapshotGenerationDrop
                .SnapshotGenerationSemanticIndexEvidence(
                    "score",
                    physicalIndexes["score"].Oid,
                    physicalIndexes["score"]
                        .Relfilenode,
                    false,
                    false,
                    true,
                    true,
                    "btree",
                    "pg_default",
                    [
                        "snapshot_id",
                        "song_id",
                        "instrument",
                        "score",
                    ],
                    ["asc", "asc", "asc", "desc"],
                    ["last", "last", "last", "first"],
                    ["default", "default", "default", "default"],
                    ["default", "default", "default", "default"],
                    null,
                    null,
                    physicalIndexes["score"]
                        .ParentRootOid,
                    physicalIndexes["score"]
                        .ParentTopOid,
                    "score"),
        };
        return new FstSnapshotGenerationDrop
            .SnapshotGenerationArchiveSemanticEvidence(
                1,
                new string('a', 64),
                new string('b', 64),
                new string('c', 64),
                new string('d', 64),
                indexes);
    }

    private async Task<
        FstSnapshotGenerationDrop.SnapshotGenerationDropPlan>
        PrepareDropPlanAsync(CandidateIdentity identity)
    {
        await using var quarantineDatabase =
            QuarantineDatabase.FromConnectionString(
                _fixture.DataSource.ConnectionString);
        var (activePlan, parity) =
            await BuildExecutorPlanAsync(
                quarantineDatabase,
                identity);
        var activeReport =
            await quarantineDatabase.QuarantineAsync(
                activePlan,
                "q2-operator",
                "q2-approval");
        var rehearsalPlan = (activePlan with
        {
            GeneratedAtUtc =
                activePlan.GeneratedAtUtc.AddMinutes(-60),
            PlanDigest = null,
            OperationId = null,
        }).Seal();
        SeedDropPrerequisites(
            activePlan.OperationId!,
            rehearsalPlan.OperationId!,
            rehearsalPlan.PlanDigest!);
        await using var dropDatabase =
            FstSnapshotGenerationDrop.DropDatabase
                .FromConnectionString(
                    _fixture.DataSource.ConnectionString);
        var snapshot = await dropDatabase.ReadSnapshotAsync(
            activePlan);
        var started =
            DateTimeOffset.UtcNow.AddMinutes(-31);
        var health =
            new FstSnapshotGenerationDrop
                .SnapshotGenerationHealthEvidence(
                    1,
                    "fst.snapshot-generation-drop-health.v1",
                    started,
                    started.AddMinutes(30),
                    30,
                    60,
                    2005,
                    1005,
                    true,
                    Enumerable.Range(0, 60)
                        .Select(index =>
                            new FstSnapshotGenerationDrop
                                .SnapshotGenerationHealthSample(
                                    started.AddSeconds(
                                        index * 30),
                                    2005,
                                    1005,
                                    true,
                                    true,
                                    false,
                                    0,
                                    0))
                        .ToArray(),
                    null).Seal();
        var semanticEvidence =
            BuildSemanticEvidence(
                identity,
                activePlan.OperationId!);
        return new FstSnapshotGenerationDrop
            .SnapshotGenerationDropPlan(
                1,
                SnapshotGenerationDropContract.ToolId,
                DateTimeOffset.UtcNow,
                true,
                rehearsalPlan,
                activePlan,
                BuildExecutionReport(
                    rehearsalPlan,
                    "quarantine",
                    "quarantined",
                    "q1-operator",
                    "q1-approval"),
                BuildExecutionReport(
                    rehearsalPlan,
                    "reattach",
                    "reattached",
                    "q1-operator",
                    "q1-reattach"),
                activeReport,
                BuildAttestationReport(
                    rehearsalPlan,
                    9101,
                    "quarantined",
                    parity),
                BuildAttestationReport(
                    rehearsalPlan,
                    9102,
                    "soak",
                    parity with
                    {
                        PublicationId = 2006,
                        PublishedScrapeId = 1006,
                    }),
                BuildAttestationReport(
                    rehearsalPlan,
                    9103,
                    "reattached",
                    parity with
                    {
                        PublicationId = 2006,
                        PublishedScrapeId = 1006,
                    }),
                BuildAttestationReport(
                    activePlan,
                    9201,
                    "quarantined",
                    parity),
                BuildAttestationReport(
                    activePlan,
                    9202,
                    "soak",
                    parity),
                semanticEvidence,
                semanticEvidence,
                parity,
                health,
                snapshot,
                "/evidence/recovery",
                new('1', 64),
                2L * 1024 * 1024 * 1024,
                0,
                "/evidence/drop.dll",
                new('2', 64),
                "/evidence/restore.py",
                new('3', 64),
                new('4', 64),
                new('5', 40),
                "/evidence/archive/fresh-proof.json",
                new('6', 64),
                DateTimeOffset.UtcNow,
                null,
                null).Seal();
    }

    private static SnapshotGenerationQuarantineAttestationReport
        BuildAttestationReport(
            SnapshotGenerationQuarantinePlan plan,
            long id,
            string stage,
            RouteParityEvidence parity)
    {
        var evidenceCharacter = id switch
        {
            9101 => '3',
            9102 => '4',
            9103 => '5',
            9201 => '7',
            9202 => '8',
            _ => 'f',
        };
        var effectiveParity = id switch
        {
            9101 => parity with
            {
                BaselineManifestSha256 = new('1', 64),
                CandidateManifestSha256 = new('2', 64),
            },
            9102 => parity with
            {
                PublicationId = 2006,
                PublishedScrapeId = 1006,
                BaselineManifestSha256 = new('2', 64),
                CandidateManifestSha256 = new('3', 64),
            },
            9103 => parity with
            {
                PublicationId = 2006,
                PublishedScrapeId = 1006,
                BaselineManifestSha256 = new('3', 64),
                CandidateManifestSha256 = new('4', 64),
            },
            9201 => parity with
            {
                BaselineManifestSha256 = new('5', 64),
                CandidateManifestSha256 = new('6', 64),
            },
            9202 => parity with
            {
                BaselineManifestSha256 = new('6', 64),
                CandidateManifestSha256 = new('7', 64),
            },
            _ => parity,
        };
        return new SnapshotGenerationQuarantineAttestationReport(
                1,
                SnapshotGenerationQuarantineContract.ToolId,
                plan.OperationId!,
                plan.PlanDigest!,
                stage,
                id,
                DateTimeOffset.UtcNow,
                "test-operator",
                effectiveParity,
                JsonDocument.Parse("{}")
                    .RootElement.Clone(),
                new string(evidenceCharacter, 64))
            .Seal();
    }

    private static string QuarantineRelationFor(
        string operationId) =>
        $"sgq_pc_1005_{operationId[..12]}";

    private void RotatePublicationForRollbackTest()
    {
        Execute(
            """
            INSERT INTO scrape_log (
                id,
                started_at,
                completed_at,
                status)
            VALUES (
                1006,
                now() - interval '10 minutes',
                now() - interval '5 minutes',
                'completed');

            UPDATE publication_generations
            SET status = 'retained'
            WHERE publication_id = 2005;

            INSERT INTO publication_generations (
                publication_id,
                scrape_id,
                status,
                created_at,
                source_cut_at,
                ready_at,
                published_at)
            VALUES (
                2006,
                1006,
                'current',
                now() - interval '9 minutes',
                now() - interval '8 minutes',
                now() - interval '7 minutes',
                now() - interval '6 minutes');

            UPDATE scrape_publication_state
            SET current_publication_id = 2006,
                previous_publication_id = 2005,
                working_publication_id = NULL,
                published_scrape_id = 1006,
                published_at = now() - interval '6 minutes',
                public_reads_frozen = FALSE,
                improvement_notifications_scrape_id = 1006,
                improvement_notifications_status = 'completed',
                improvement_notifications_started_at =
                    now() - interval '5 minutes',
                improvement_notifications_completed_at =
                    now() - interval '4 minutes',
                improvement_notifications_projection_ready = TRUE,
                improvement_notifications_projection_scrape_id = 1006,
                updated_at = now()
            WHERE id = TRUE;
            """);
    }

    private CandidateIdentity SeedAcceptedCandidate()
    {
        Execute(
            """
            INSERT INTO scrape_log (
                id,
                started_at,
                completed_at,
                status)
            SELECT
                scrape_id,
                now() - interval '1 hour',
                now() - interval '30 minutes',
                'completed'
            FROM generate_series(1001, 1005) scrape_id;

            INSERT INTO publication_generations (
                publication_id,
                scrape_id,
                status,
                created_at,
                source_cut_at,
                ready_at,
                published_at)
            SELECT
                1000 + scrape_id,
                scrape_id,
                CASE
                    WHEN scrape_id = 1005 THEN 'current'
                    ELSE 'retained'
                END,
                now() - interval '20 minutes',
                now() - interval '19 minutes',
                now() - interval '18 minutes',
                now() - interval '17 minutes'
            FROM generate_series(1001, 1005) scrape_id;

            UPDATE scrape_publication_state
            SET current_publication_id = 2005,
                previous_publication_id = 2004,
                working_publication_id = NULL,
                published_scrape_id = 1005,
                published_at = now() - interval '17 minutes',
                public_reads_frozen = FALSE,
                public_reads_frozen_at = NULL,
                public_reads_frozen_scrape_id = NULL,
                public_reads_frozen_reason = NULL,
                publication_commit_intent_started_at = NULL,
                publication_commit_intent_heartbeat_at = NULL,
                publication_commit_intent_owner = NULL,
                max_score_mutation_gate_token = NULL,
                max_score_mutation_gate_publication_id = NULL,
                max_score_mutation_gate_backend_pid = NULL,
                max_score_mutation_gate_backend_start = NULL,
                max_score_mutation_gate_acquired_at = NULL,
                improvement_notifications_scrape_id = 1005,
                improvement_notifications_status = 'completed',
                improvement_notifications_attempt_count = 1,
                improvement_notifications_started_at =
                    now() - interval '16 minutes',
                improvement_notifications_completed_at =
                    now() - interval '15 minutes',
                improvement_notifications_error = NULL,
                improvement_notifications_projection_scopes =
                    '[]'::jsonb,
                improvement_notifications_projection_ready = TRUE,
                improvement_notifications_projection_scrape_id = 1005,
                updated_at = now()
            WHERE id = TRUE;

            INSERT INTO service_worker_status (
                worker_key,
                status,
                last_status_change_at,
                current_operation_json,
                updated_at)
            VALUES (
                'scraper',
                'offline',
                now(),
                NULL,
                now())
            ON CONFLICT (worker_key) DO UPDATE
            SET status = 'offline',
                current_operation_json = NULL,
                last_status_change_at = now(),
                updated_at = now();

            INSERT INTO snapshot_generation_retention_cycles (
                cycle_id,
                trigger_scrape_id,
                trigger_publication_id,
                safe_point_kind,
                safe_point_at,
                planner_version,
                config_version,
                report_only,
                status,
                oracle_agreement,
                candidate_identity_hash,
                observation_hash,
                planner_child_set,
                planner_live_set,
                planner_candidate_set,
                oracle_child_set,
                oracle_live_set,
                oracle_candidate_set,
                candidate_count,
                protected_count,
                blocked_count,
                candidate_bytes,
                global_blockers,
                anomalies,
                created_at)
            SELECT
                2000 + scrape_id,
                scrape_id,
                1000 + scrape_id,
                'terminal_worker_post_publication',
                now() -
                    ((1006 - scrape_id)::TEXT || ' minutes')::INTERVAL,
                3,
                1,
                TRUE,
                'observed',
                TRUE,
                CASE
                    WHEN scrape_id = 1005
                        THEN repeat('b', 64)
                    ELSE repeat('a', 64)
                END,
                repeat('c', 64),
                '["candidate"]'::jsonb,
                '[]'::jsonb,
                '["candidate"]'::jsonb,
                '["candidate"]'::jsonb,
                '[]'::jsonb,
                '["candidate"]'::jsonb,
                1,
                0,
                0,
                1,
                '[]'::jsonb,
                '[]'::jsonb,
                now() -
                    ((1006 - scrape_id)::TEXT || ' minutes')::INTERVAL
            FROM generate_series(1001, 1005) scrape_id;

            SELECT ensure_leaderboard_snapshot_generation_partition(
                'Solo_PeripheralCymbals',
                1005);

            INSERT INTO leaderboard_entries_snapshot (
                snapshot_id,
                song_id,
                instrument,
                account_id,
                score,
                source,
                first_seen_at,
                last_updated_at)
            VALUES (
                1005,
                'song-test',
                'Solo_PeripheralCymbals',
                'account-test',
                123456,
                'scrape',
                '2026-01-01T00:00:00Z',
                '2026-01-01T00:00:00Z');

            INSERT INTO snapshot_generation_retention_observations (
                observation_id,
                cycle_id,
                report_only,
                instrument,
                root_schema,
                root_relation,
                snapshot_parent_oid,
                root_oid,
                root_partition_key,
                root_partition_bound,
                root_tablespace_name,
                root_relation_options,
                root_index_configuration,
                child_schema,
                child_relation,
                snapshot_id,
                child_oid,
                child_relfilenode,
                partition_bound,
                tablespace_name,
                relation_kind,
                persistence_kind,
                access_method,
                relation_options,
                index_configuration,
                stable_child_identity_hash,
                stable_config_schema_hash,
                row_estimate,
                total_bytes,
                observation_metrics_hash,
                planner_live,
                oracle_live,
                classification,
                root_reasons,
                blocker_codes,
                details)
            SELECT
                3005,
                3005,
                TRUE,
                'Solo_PeripheralCymbals',
                'public',
                root.relname,
                snapshot_parent.oid::BIGINT,
                root.oid::BIGINT,
                'LIST (snapshot_id)',
                pg_get_expr(
                    root.relpartbound,
                    root.oid,
                    TRUE),
                'pg_default',
                '[]'::jsonb,
                '[]'::jsonb,
                'public',
                child.relname,
                1005,
                child.oid::BIGINT,
                child.relfilenode::BIGINT,
                pg_get_expr(
                    child.relpartbound,
                    child.oid,
                    TRUE),
                'pg_default',
                child.relkind::TEXT,
                child.relpersistence::TEXT,
                access_method.amname,
                '[]'::jsonb,
                '[]'::jsonb,
                repeat('d', 64),
                repeat('e', 64),
                1,
                pg_total_relation_size(child.oid)::BIGINT,
                repeat('f', 64),
                FALSE,
                FALSE,
                'candidate',
                ARRAY[]::TEXT[],
                ARRAY[]::TEXT[],
                '{}'::jsonb
            FROM pg_class child
            JOIN pg_namespace child_namespace
              ON child_namespace.oid = child.relnamespace
            JOIN pg_am access_method
              ON access_method.oid = child.relam
            JOIN pg_inherits child_inheritance
              ON child_inheritance.inhrelid = child.oid
            JOIN pg_class root
              ON root.oid = child_inheritance.inhparent
            JOIN pg_inherits root_inheritance
              ON root_inheritance.inhrelid = root.oid
            JOIN pg_class snapshot_parent
              ON snapshot_parent.oid =
                    root_inheritance.inhparent
            WHERE child_namespace.nspname = 'public'
              AND child.relname =
                    'leaderboard_entries_snapshot_pro_cymbals_s1005';
            """);

        return new CandidateIdentity(
            RootOid: Scalar<long>(
                """
                SELECT
                    'public.leaderboard_entries_snapshot_pro_cymbals'
                        ::regclass::OID::BIGINT
                """),
            ChildOid: RelationOid("public", OriginalRelation),
            ChildRelfilenode:
                RelationRelfilenode("public", OriginalRelation));
    }

    private static void ConfigureQuarantine(
        NpgsqlCommand command,
        CandidateIdentity identity,
        long expectedRowCount)
    {
        command.Parameters.AddWithValue(
            "operationId",
            OperationId);
        command.Parameters.AddWithValue(
            "planDigest",
            PlanDigest);
        command.Parameters.AddWithValue(
            "cycleId",
            3005L);
        command.Parameters.AddWithValue(
            "observationId",
            3005L);
        command.Parameters.AddWithValue(
            "childOid",
            identity.ChildOid);
        command.Parameters.AddWithValue(
            "childRelfilenode",
            identity.ChildRelfilenode);
        command.Parameters.AddWithValue(
            "rowCount",
            expectedRowCount);
    }

    private static void ConfigureDrop(
        NpgsqlCommand command,
        CandidateIdentity identity,
        int healthSampleCount = 60,
        int preDropRouteCount = 55,
        bool preDropStatusParity = true,
        bool preDropSemanticJsonParity = true,
        int preDropDifferenceCount = 0)
    {
        command.Parameters.AddWithValue(
            "childOid",
            identity.ChildOid);
        command.Parameters.AddWithValue(
            "childRelfilenode",
            identity.ChildRelfilenode);
        command.Parameters.AddWithValue(
            "healthSampleCount",
            healthSampleCount);
        command.Parameters.AddWithValue(
            "preDropRouteCount",
            preDropRouteCount);
        command.Parameters.AddWithValue(
            "preDropStatusParity",
            preDropStatusParity);
        command.Parameters.AddWithValue(
            "preDropSemanticJsonParity",
            preDropSemanticJsonParity);
        command.Parameters.AddWithValue(
            "preDropDifferenceCount",
            preDropDifferenceCount);
    }

    private void AssertPrivateIndexDrift(
        string scenario,
        CandidateIdentity identity)
    {
        if (scenario is "extra-index" or "missing-index")
        {
            Assert.Equal(
                scenario == "extra-index" ? 3 : 1,
                Scalar<int>(
                    $"""
                    SELECT COUNT(*)::INTEGER
                    FROM pg_index
                    WHERE indrelid =
                        'fst_snapshot_quarantine.{QuarantineRelation}'
                            ::regclass
                    """));
            return;
        }

        var mismatchCount = ExecuteScalar<int>(
            """
            WITH inventory AS (
                SELECT
                    item.key AS index_role,
                    item.value AS index_data
                FROM jsonb_each(
                    fst_snapshot_generation_index_inventory(
                        @childOid,
                        @rootOid,
                        FALSE))
                    item
            )
            SELECT COUNT(*)::INTEGER
            FROM inventory
            JOIN snapshot_generation_quarantine_index_renames
                rename_row
              ON rename_row.operation_id =
                    '0123456789abcdef0123456789abcdef'
             AND rename_row.index_role =
                    inventory.index_role
            WHERE rename_row.index_oid <>
                    (
                        inventory.index_data
                        ->> 'indexOid')::BIGINT
               OR rename_row.index_relfilenode <>
                    (
                        inventory.index_data
                        ->> 'indexRelfilenode')::BIGINT
               OR rename_row.new_index_name <>
                    inventory.index_data
                        ->> 'indexName'
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "childOid",
                    identity.ChildOid);
                command.Parameters.AddWithValue(
                    "rootOid",
                    identity.RootOid);
            });
        Assert.Equal(
            scenario == "wrong-role" ? 2 : 1,
            mismatchCount);
    }

    private static void ConfigureRestore(
        NpgsqlCommand command,
        long restoredOid,
        long restoredRelfilenode,
        string? authorizationId = null,
        string? executingToolSha256 = null,
        string? validatorBaseToolSha256 = null,
        string? archiveHelperSha256 = null,
        string? repairPackageManifestSha256 = null)
    {
        command.Parameters.AddWithValue(
            "childOid",
            restoredOid);
        command.Parameters.AddWithValue(
            "childRelfilenode",
            restoredRelfilenode);
        command.Parameters.Add(
            "authorizationId",
            NpgsqlDbType.Text).Value =
            (object?)authorizationId
            ?? DBNull.Value;
        command.Parameters.AddWithValue(
            "executingToolSha256",
            executingToolSha256
            ?? new string('f', 64));
        command.Parameters.Add(
            "validatorBaseToolSha256",
            NpgsqlDbType.Text).Value =
            (object?)validatorBaseToolSha256
            ?? DBNull.Value;
        command.Parameters.Add(
            "archiveHelperSha256",
            NpgsqlDbType.Text).Value =
            (object?)archiveHelperSha256
            ?? DBNull.Value;
        command.Parameters.Add(
            "repairPackageManifestSha256",
            NpgsqlDbType.Text).Value =
            (object?)repairPackageManifestSha256
            ?? DBNull.Value;
    }

    private async Task<(
        RestoreToolAuthorizationRequest Request,
        RestoreToolAuthorizationRecord Authorization)>
        SeedAuthorizedRestoreAsync()
    {
        await DatabaseInitializer.EnsureSchemaAsync(
            _fixture.DataSource);
        var original = SeedAcceptedCandidate();
        var fingerprint =
            Scalar<string>(
                """
                SELECT encode(
                    digest(
                        convert_to(
                            to_jsonb(row_value)::TEXT
                            || E'\n',
                            'UTF8'),
                        'sha256'),
                    'hex')
                FROM ONLY
                    public.leaderboard_entries_snapshot_pro_cymbals_s1005
                        row_value
                """);
        ExecuteScalar<string>(
            QuarantineSql.Replace(
                "repeat('6', 64)",
                $"'{fingerprint}'",
                StringComparison.Ordinal),
            command => ConfigureQuarantine(
                command,
                original,
                expectedRowCount: 1));
        SeedDropPrerequisites();
        ExecuteScalar<string>(
            DropSql,
            command => ConfigureDrop(command, original));
        var request = BuildAuthorizationRequest();
        await using var database =
            AuthorizationDatabase.FromConnectionString(
                _fixture.DataSource.ConnectionString);
        var authorization =
            await database.AuthorizeAsync(request);
        CreateRestoreStagingRelation();
        var restoredOid =
            RelationOid("public", OriginalRelation);
        var restoredRelfilenode =
            RelationRelfilenode(
                "public",
                OriginalRelation);
        ExecuteScalar<string>(
            RestoreSql.Replace(
                "repeat('6', 64)",
                $"'{fingerprint}'",
                StringComparison.Ordinal),
            command => ConfigureRestore(
                command,
                restoredOid,
                restoredRelfilenode,
                authorization.AuthorizationId,
                request.AuthorizedRestoreToolSha256,
                request.ValidatorBaseToolSha256,
                request.AuthorizedArchiveHelperSha256,
                request.RepairPackageManifestSha256));
        return (request, authorization);
    }

    private static RestoreToolAuthorizationRequest
        BuildAuthorizationRequest() =>
        new(
            "fedcba9876543210fedcba9876543210",
            new string('0', 64),
            new string('9', 64),
            new string('f', 64),
            RestoreToolAuthorizationContract
                .ValidatorBaseToolSha256,
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            new string('1', 40),
            new string('2', 40),
            new string('e', 64),
            new string('3', 64),
            new string('4', 64),
            new string('5', 64),
            "pinned_restore_validator_defect",
            "Authorize the reviewed canonical-byte restore validator.",
            "repair-operator",
            "independent-reviewer",
            "repair-authorization-reference",
            JsonDocument.Parse(
                """
                {
                  "packageValidated": true,
                  "testsValidated": true
                }
                """).RootElement.Clone());

    private static RestoreContinuationAuthorizationRequest
        BuildContinuationAuthorizationRequest(
            string predecessorAuthorizationId,
            RestoreToolAuthorizationRequest predecessor) =>
        new(
            new string('a', 32),
            predecessor.DropOperationId,
            predecessorAuthorizationId,
            new string('b', 64),
            new string('8', 64),
            new string('9', 64),
            predecessor.AuthorizedRestoreToolSha256,
            predecessor.RepairPackageManifestSha256,
            predecessor.OriginalBundleManifestSha256,
            new string('6', 64),
            new string('7', 64),
            new string('8', 64),
            new string('9', 64),
            new string('a', 64),
            QuarantineEvidenceValidator
                .RouteParityAlgorithmId,
            new string('c', 64),
            new string('b', 64),
            new string('d', 64),
            new string('2', 64),
            new string('e', 64),
            2005,
            1005,
            new string('4', 40),
            new string('5', 40),
            new string('f', 64),
            new string('6', 64),
            new string('7', 64),
            "post_restore_route_parity",
            "Authorize the reviewed continuation-only evidence tool.",
            "continuation-operator",
            "continuation-reviewer",
            "continuation-authorization-reference",
            JsonDocument.Parse(
                """
                {
                  "packageValidated": true,
                  "routeParityValidated": true
                }
                """).RootElement.Clone());

    private static RestoreContinuationPackageManifest
        BuildContinuationManifest(
            RestoreContinuationAuthorizationRequest request) =>
        new(
            RestoreContinuationContract.SchemaVersion,
            RestoreContinuationContract.PackageToolId,
            "accepted",
            DateTimeOffset.UtcNow,
            request.RestoreOperationId,
            request.DropOperationId,
            request.RestorePlanDigest,
            "/evidence/restore-plan.json",
            request.RestorePlanFileSha256,
            "/evidence/restore-report.json",
            request.RestoreReportSha256,
            request.PredecessorAuthorizationId,
            request.PredecessorRestoreToolSha256,
            "/evidence/repair-package-v5",
            request
                .PredecessorRepairPackageManifestSha256,
            "/evidence/recovery-bundle-v2",
            request.RecoveryBundleManifestSha256,
            request.AuthorizedContinuationToolSha256,
            request.AuthorizedEvidenceAssemblySha256,
            request.RouteParityReferenceSourceSha256,
            request.AuthorizerBinarySha256,
            request.RepositoryCommit,
            request.RepositoryTreeId,
            request
                .PredecessorToContinuationDiffSha256,
            request.SourceManifestSha256,
            request.TestEvidenceManifestSha256,
            request.RouteParityAlgorithmId,
            request.RouteParityPreflightSha256,
            "/evidence/baseline/manifest.json",
            request.BaselineRouteManifestSha256,
            request.BaselineRouteChecksumsSha256,
            "/evidence/candidate/manifest.json",
            request.CandidateRouteManifestSha256,
            request.CandidateRouteChecksumsSha256,
            request.PublicationId,
            request.PublishedScrapeId,
            []);

    private string DbCanonicalEvidenceSha256(
        JsonElement evidence) =>
        ExecuteScalar<string>(
            """
            SELECT encode(
                digest(
                    convert_to(
                        @evidence::JSONB::TEXT,
                        'UTF8'),
                    'sha256'),
                'hex')
            """,
            command =>
            {
                command.Parameters.Add(
                    "evidence",
                    NpgsqlDbType.Jsonb).Value =
                    evidence.GetRawText();
            });

    private void CreateRestoreStagingRelation()
    {
        Execute(
            """
            CREATE TABLE
                public.leaderboard_entries_snapshot_pro_cymbals_s1005
                (
                    LIKE
                        public.leaderboard_entries_snapshot_pro_cymbals
                    INCLUDING DEFAULTS
                    INCLUDING STORAGE
                    INCLUDING COMPRESSION
                );
            INSERT INTO
                public.leaderboard_entries_snapshot_pro_cymbals_s1005 (
                    snapshot_id,
                    song_id,
                    instrument,
                    account_id,
                    score,
                    source,
                    first_seen_at,
                    last_updated_at)
            VALUES (
                1005,
                'song-test',
                'Solo_PeripheralCymbals',
                'account-test',
                123456,
                'scrape',
                '2026-01-01T00:00:00Z',
                '2026-01-01T00:00:00Z');
            ALTER TABLE
                public.leaderboard_entries_snapshot_pro_cymbals_s1005
                ADD CONSTRAINT ck_sgr_1005_aaaaaaaaaaaa
                CHECK (
                    snapshot_id = 1005
                    AND instrument =
                        'Solo_PeripheralCymbals');
            CREATE TRIGGER trg_sgr_1005_aaaaaaaaaaaa
                BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE
                ON
                    public.leaderboard_entries_snapshot_pro_cymbals_s1005
                FOR EACH STATEMENT EXECUTE FUNCTION
                    fst_reject_snapshot_generation_quarantine_relation_mutation();
            """);
    }

    private long RecordAttestation(
        string stage,
        char baselineHashCharacter,
        char candidateHashCharacter)
    {
        return ExecuteScalar<long>(
            """
            SELECT
                fst_record_snapshot_generation_quarantine_attestation(
                    @operationId,
                    @stage,
                    2005,
                    1005,
                    55,
                    TRUE,
                    TRUE,
                    0,
                    repeat(@baselineHashCharacter, 64),
                    repeat(@candidateHashCharacter, 64),
                    '{}'::jsonb,
                    repeat(@candidateHashCharacter, 64),
                    'test-operator')
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "operationId",
                    OperationId);
                command.Parameters.AddWithValue("stage", stage);
                command.Parameters.AddWithValue(
                    "baselineHashCharacter",
                    baselineHashCharacter.ToString());
                command.Parameters.AddWithValue(
                    "candidateHashCharacter",
                    candidateHashCharacter.ToString());
            });
    }

    private void SeedDropPrerequisites(
        string activeOperationId = OperationId,
        string rehearsalOperationId =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        string rehearsalPlanDigest =
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")
    {
        Execute(
            """
            INSERT INTO scrape_log (
                id,
                started_at,
                completed_at,
                status)
            VALUES (
                1006,
                now() - interval '70 minutes',
                now() - interval '60 minutes',
                'completed')
            ON CONFLICT (id) DO NOTHING;
            INSERT INTO publication_generations (
                publication_id,
                scrape_id,
                status,
                created_at)
            VALUES (
                2006,
                1006,
                'retained',
                now() - interval '60 minutes')
            ON CONFLICT (publication_id) DO NOTHING;

            SET session_replication_role = replica;
            UPDATE snapshot_generation_quarantine_operations
            SET quarantined_at =
                    now() - interval '35 minutes'
            WHERE operation_id = @activeOperationId;
            SET session_replication_role = origin;

            WITH rehearsal_hold AS (
                INSERT INTO snapshot_generation_retention_holds (
                    instrument,
                    snapshot_id,
                    hold_kind,
                    reason,
                    created_by,
                    released_by,
                    released_at,
                    release_reason)
                VALUES (
                    'Solo_PeripheralCymbals',
                    1005,
                    'retention_in_flight',
                    'Q1 rehearsal',
                    'q1-operator',
                    'q1-operator',
                    now() - interval '40 minutes',
                    'reattached')
                RETURNING hold_id
            )
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
                quarantine_evidence,
                quarantined_at)
            SELECT
                @rehearsalOperationId,
                operation.schema_version,
                operation.tool_id,
                @rehearsalPlanDigest,
                operation.archive_manifest_sha256,
                operation.archive_proof_manifest_sha256,
                operation.source_evidence_manifest_sha256,
                operation.baseline_route_manifest_sha256,
                operation.candidate_route_manifest_sha256,
                operation.cycle_id,
                operation.observation_id,
                operation.trigger_scrape_id,
                operation.trigger_publication_id,
                operation.instrument,
                operation.snapshot_id,
                operation.root_schema,
                operation.root_relation,
                operation.root_oid,
                operation.child_schema,
                operation.child_relation,
                operation.child_oid,
                operation.child_relfilenode,
                operation.quarantine_schema,
                'sgq_pc_1005_bbbbbbbbbbbb',
                'ck_sgq_1005_bbbbbbbbbbbb',
                'trg_sgq_1005_bbbbbbbbbbbb',
                operation.default_partition_schema,
                operation.default_partition_relation,
                operation.default_partition_oid,
                'ck_sgq_default_1005_bbbbbbbbbbbb',
                operation.stable_child_identity_hash,
                operation.stable_config_schema_hash,
                operation.row_count,
                operation.row_fingerprint_sha256,
                operation.logical_catalog_sha256,
                operation.total_bytes,
                rehearsal_hold.hold_id,
                'q1-operator',
                'q1-approval',
                '{}',
                '{}',
                now() - interval '2 hours'
            FROM snapshot_generation_quarantine_operations
                operation,
                rehearsal_hold
            WHERE operation.operation_id =
                    @activeOperationId;

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
                @rehearsalOperationId,
                rename_row.index_role,
                rename_row.index_oid,
                rename_row.index_relfilenode,
                rename_row.old_index_name,
                'sgqi_' || @rehearsalOperationId || '_' ||
                    rename_row.index_role,
                rename_row.old_constraint_name,
                CASE
                    WHEN rename_row.index_role = 'pk'
                        THEN 'sgqi_' ||
                            @rehearsalOperationId ||
                            '_pk'
                    ELSE NULL
                END,
                'quarantine',
                rename_row.semantic_before,
                rename_row.semantic_after,
                rename_row.semantic_before_sha256,
                rename_row.semantic_after_sha256,
                pg_backend_pid(),
                pg_current_xact_id()::TEXT
            FROM
                snapshot_generation_quarantine_index_renames
                    rename_row
            WHERE rename_row.operation_id =
                    @activeOperationId;

            INSERT INTO
                snapshot_generation_quarantine_reattachments (
                    operation_id,
                    reattached_by,
                    reattach_reference,
                    reattach_evidence,
                    reattached_at)
            VALUES (
                @rehearsalOperationId,
                'q1-operator',
                'q1-reattach',
                '{}',
                now() - interval '40 minutes');

            INSERT INTO snapshot_generation_quarantine_attestations (
                attestation_id,
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
                attested_by,
                attested_at)
            VALUES
                (
                    9101,
                    @rehearsalOperationId,
                    'quarantined',
                    2005,
                    1005,
                    55,
                    TRUE,
                    TRUE,
                    0,
                    repeat('1', 64),
                    repeat('2', 64),
                    '{}',
                    repeat('3', 64),
                    'q1-operator',
                    now() - interval '110 minutes'),
                (
                    9102,
                    @rehearsalOperationId,
                    'soak',
                    2006,
                    1006,
                    55,
                    TRUE,
                    TRUE,
                    0,
                    repeat('2', 64),
                    repeat('3', 64),
                    '{}',
                    repeat('4', 64),
                    'q1-operator',
                    now() - interval '50 minutes'),
                (
                    9103,
                    @rehearsalOperationId,
                    'reattached',
                    2006,
                    1006,
                    55,
                    TRUE,
                    TRUE,
                    0,
                    repeat('3', 64),
                    repeat('4', 64),
                    '{}',
                    repeat('5', 64),
                    'q1-operator',
                    now() - interval '39 minutes'),
                (
                    9201,
                    @activeOperationId,
                    'quarantined',
                    2005,
                    1005,
                    55,
                    TRUE,
                    TRUE,
                    0,
                    repeat('5', 64),
                    repeat('6', 64),
                    '{}',
                    repeat('7', 64),
                    'q2-operator',
                    now() - interval '34 minutes'),
                (
                    9202,
                    @activeOperationId,
                    'soak',
                    2005,
                    1005,
                    55,
                    TRUE,
                    TRUE,
                    0,
                    repeat('6', 64),
                    repeat('7', 64),
                    '{}',
                    repeat('8', 64),
                    'q2-operator',
                    now());
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "activeOperationId",
                    activeOperationId);
                command.Parameters.AddWithValue(
                    "rehearsalOperationId",
                    rehearsalOperationId);
                command.Parameters.AddWithValue(
                    "rehearsalPlanDigest",
                    rehearsalPlanDigest);
            });
    }

    private bool RelationExists(
        string schema,
        string relation) =>
        Scalar<bool>(
            """
            SELECT to_regclass(format('%I.%I', @schema, @relation))
                IS NOT NULL
            """,
            command =>
            {
                command.Parameters.AddWithValue("schema", schema);
                command.Parameters.AddWithValue(
                    "relation",
                    relation);
            });

    private long RelationOid(
        string schema,
        string relation) =>
        Scalar<long>(
            """
            SELECT to_regclass(
                format('%I.%I', @schema, @relation))::OID::BIGINT
            """,
            command =>
            {
                command.Parameters.AddWithValue("schema", schema);
                command.Parameters.AddWithValue(
                    "relation",
                    relation);
            });

    private long RelationRelfilenode(
        string schema,
        string relation) =>
        Scalar<long>(
            """
            SELECT relation.relfilenode::BIGINT
            FROM pg_class relation
            JOIN pg_namespace namespace
              ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = @schema
              AND relation.relname = @relation
            """,
            command =>
            {
                command.Parameters.AddWithValue("schema", schema);
                command.Parameters.AddWithValue(
                    "relation",
                    relation);
            });

    private void Execute(
        string sql,
        Action<NpgsqlCommand>? configure = null)
    {
        using var connection =
            _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        command.ExecuteNonQuery();
    }

    private T ExecuteScalar<T>(
        string sql,
        Action<NpgsqlCommand>? configure = null) =>
        Scalar<T>(sql, configure);

    private T Scalar<T>(
        string sql,
        Action<NpgsqlCommand>? configure = null)
    {
        using var connection =
            _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        return (T)Convert.ChangeType(
            command.ExecuteScalar()!,
            typeof(T));
    }

    private int MissingDropSemanticColumnCount() =>
        Scalar<int>(
            """
            SELECT COUNT(*)::INTEGER
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
                  AND NOT attribute.attisdropped)
            """);

    private int MissingRestoreSemanticColumnCount() =>
        Scalar<int>(
            """
            SELECT COUNT(*)::INTEGER
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
                  AND NOT attribute.attisdropped)
            """);

    private sealed record CandidateIdentity(
        long RootOid,
        long ChildOid,
        long ChildRelfilenode);

    private sealed record IndexIdentity(
        string Role,
        long Oid,
        long Relfilenode,
        string Name);

    private IReadOnlyList<IndexIdentity>
        LoadIndexIdentity(
            string schema,
            string relation)
    {
        using var connection =
            _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                CASE
                    WHEN index_row.indisprimary
                        THEN 'pk'
                    ELSE 'score'
                END,
                index_relation.oid::BIGINT,
                index_relation.relfilenode::BIGINT,
                index_relation.relname
            FROM pg_index index_row
            JOIN pg_class index_relation
              ON index_relation.oid =
                    index_row.indexrelid
            JOIN pg_class table_relation
              ON table_relation.oid =
                    index_row.indrelid
            JOIN pg_namespace namespace
              ON namespace.oid =
                    table_relation.relnamespace
            WHERE namespace.nspname = @schema
              AND table_relation.relname = @relation
            ORDER BY 1
            """;
        command.Parameters.AddWithValue(
            "schema",
            schema);
        command.Parameters.AddWithValue(
            "relation",
            relation);
        using var reader = command.ExecuteReader();
        var result = new List<IndexIdentity>();
        while (reader.Read())
        {
            result.Add(
                new IndexIdentity(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetString(3)));
        }
        return result;
    }

    private static string FindRepositoryRoot()
    {
        var current =
            new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        current.FullName,
                        "FortniteFestivalLeaderboardScraper.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException(
            "Repository root was not found.");
    }

    private const string
        DowngradeDropOperationsToPreSemanticSql =
        """
        ALTER TABLE snapshot_generation_drop_operations
            DROP CONSTRAINT
                ck_snapshot_generation_drop_hashes,
            DROP CONSTRAINT
                ck_snapshot_generation_drop_identity,
            DROP COLUMN semantic_projection_version,
            DROP COLUMN rehearsal_catalog_sha256,
            DROP COLUMN catalog_sha256,
            DROP COLUMN rehearsal_semantic_catalog_sha256,
            DROP COLUMN semantic_catalog_sha256,
            DROP COLUMN rehearsal_logical_index_shape_sha256,
            DROP COLUMN logical_index_shape_sha256,
            DROP COLUMN
                rehearsal_physical_index_inventory_sha256,
            DROP COLUMN physical_index_inventory_sha256;
        ALTER TABLE snapshot_generation_drop_operations
            ADD CONSTRAINT
                ck_snapshot_generation_drop_hashes
                CHECK (
                    plan_digest ~ '^[0-9a-f]{64}$'
                    AND archive_sha256
                        ~ '^[0-9a-f]{64}$'),
            ADD CONSTRAINT
                ck_snapshot_generation_drop_identity
                CHECK (
                    cycle_id > 0
                    AND child_oid > 0
                    AND child_relfilenode > 0);
        """;

    private const string
        DowngradeRestoreOperationsToPreSemanticSql =
        """
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
                    AND archive_sha256
                        ~ '^[0-9a-f]{64}$'),
            ADD CONSTRAINT
                ck_snapshot_generation_restore_identity
                CHECK (
                    snapshot_id > 0
                    AND restored_child_oid > 0
                    AND restored_child_relfilenode > 0
                    AND jsonb_typeof(restore_evidence) =
                        'object');
        """;

    private const string QuarantineSql = """
        SELECT fst_quarantine_snapshot_generation(
            @operationId,
            @planDigest,
            repeat('1', 64),
            repeat('2', 64),
            repeat('3', 64),
            repeat('4', 64),
            repeat('5', 64),
            @cycleId,
            @observationId,
            @childOid,
            @childRelfilenode,
            @rowCount,
            repeat('6', 64),
            repeat('7', 64),
            'test-operator',
            'test-approval',
            '{}'::jsonb)
        """;

    private const string DropSql = """
        WITH locked AS MATERIALIZED (
            SELECT fst_lock_snapshot_generation_for_drop(
                '0123456789abcdef0123456789abcdef',
                @childOid,
                @childRelfilenode) AS relation_name
        )
        SELECT fst_drop_quarantined_snapshot_generation(
            'fedcba9876543210fedcba9876543210',
            repeat('0', 64),
            repeat('b', 32),
            '0123456789abcdef0123456789abcdef',
            9101,
            9102,
            9103,
            9201,
            9202,
            repeat('8', 64),
            repeat('7', 64),
            repeat('9', 64),
            1,
            repeat('a', 64),
            repeat('b', 64),
            repeat('c', 64),
            repeat('c', 64),
            repeat('d', 64),
            repeat('d', 64),
            repeat('e', 64),
            repeat('e', 64),
            repeat('a', 64),
            repeat('b', 64),
            @preDropRouteCount,
            @preDropStatusParity,
            @preDropSemanticJsonParity,
            @preDropDifferenceCount,
            repeat('c', 64),
            repeat('d', 64),
            repeat('e', 64),
            repeat('f', 64),
            repeat('0', 64),
            repeat('1', 40),
            '[]'::jsonb,
            repeat('2', 64),
            '{}'::jsonb,
            repeat('3', 64),
            '{}'::jsonb,
            repeat('4', 64),
            current_database(),
            (
                SELECT oid::BIGINT
                FROM pg_database
                WHERE datname = current_database()),
            (
                SELECT system_identifier::TEXT
                FROM pg_control_system()),
            current_setting('server_version_num')::INTEGER,
            now() - interval '31 minutes',
            now() - interval '1 minute',
            @healthSampleCount,
            30,
            now(),
            'drop-operator',
            'drop-approval',
            '{}'::jsonb,
            repeat('5', 64),
            '{}'::jsonb)
        FROM locked
        WHERE relation_name =
            'sgq_pc_1005_0123456789ab'
        """;

    private const string RestoreSql = """
        SELECT fst_restore_snapshot_generation(
            repeat('a', 32),
            repeat('b', 64),
            'fedcba9876543210fedcba9876543210',
            @childOid,
            @childRelfilenode,
            1,
            repeat('6', 64),
            repeat('7', 64),
            repeat('c', 64),
            repeat('d', 64),
            @authorizationId,
            @executingToolSha256,
            @validatorBaseToolSha256,
            @archiveHelperSha256,
            @repairPackageManifestSha256,
            '{"pk":"archived_pk","score":"archived_score"}'
                ::jsonb,
            'ck_sgr_1005_aaaaaaaaaaaa',
            'trg_sgr_1005_aaaaaaaaaaaa',
            'restore-operator',
            'restore-approval',
            '{}'::jsonb)
        """;
}
