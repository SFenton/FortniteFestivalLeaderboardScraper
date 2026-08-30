using FSTService.Persistence;
using FSTService.Persistence.Maintenance;
using FSTService.Tests.Helpers;
using FstSnapshotGenerationQuarantine;
using Npgsql;

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
                now(),
                now());

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

    private void RecordAttestation(
        string stage,
        char baselineHashCharacter,
        char candidateHashCharacter)
    {
        ExecuteScalar<long>(
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

    private sealed record CandidateIdentity(
        long RootOid,
        long ChildOid,
        long ChildRelfilenode);

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
}
