using FSTService.Persistence;
using FSTService.Persistence.Maintenance;
using FSTService.Tests.Helpers;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class SnapshotGenerationRetentionPlannerTests
    : IDisposable
{
    private const long CurrentScrapeId = 2000;
    private const long CurrentPublicationId = 9000;
    private readonly InMemoryMetaDatabase _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task DisabledPlannerWritesNothing()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        var planner = CreatePlanner(enabled: false);

        var result = await planner.PlanAsync(
            CreateRequest());

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Disabled,
            result.Disposition);
        Assert.Equal(
            0,
            Scalar<long>(
                "SELECT COUNT(*) FROM snapshot_generation_retention_cycles"));
        Assert.Equal(
            0,
            Scalar<long>(
                "SELECT COUNT(*) FROM snapshot_generation_retention_deferrals"));
    }

    [Fact]
    public async Task PlannerPersistsExactCandidateIdentityAndIsIdempotent()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        var planner = CreatePlanner();

        var first = await planner.PlanAsync(CreateRequest());
        var second = await planner.PlanAsync(
            CreateRequest(broadcastScrapeId: null));

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Observed,
            first.Disposition);
        Assert.Equal(1, first.CandidateCount);
        Assert.True(first.OracleAgreement);
        Assert.NotNull(first.CycleId);
        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Existing,
            second.Disposition);
        Assert.Equal(first.CycleId, second.CycleId);

        var repository =
            new SnapshotGenerationRetentionRepository(
                _fixture.DataSource);
        var observation = Assert.Single(
            await repository.GetObservationsAsync(
                first.CycleId!.Value));
        Assert.Equal(
            SnapshotGenerationRetentionClassification.Candidate,
            observation.Classification);
        Assert.Equal("public", observation.RootSchema);
        Assert.Equal("public", observation.ChildSchema);
        Assert.True(observation.SnapshotParentOid > 0);
        Assert.True(observation.RootOid > 0);
        Assert.Equal(
            "LIST (snapshot_id)",
            observation.RootPartitionKey);
        Assert.Equal(
            "FOR VALUES IN ('Solo_Guitar')",
            observation.RootPartitionBound);
        Assert.Equal(
            "leaderboard_entries_snapshot_solo_guitar_s1307",
            observation.ChildRelation);
        Assert.Equal(1307, observation.SnapshotId);
        Assert.True(observation.ChildOid > 0);
        Assert.True(observation.ChildRelfilenode > 0);
        Assert.Equal(
            "FOR VALUES IN ('1307')",
            observation.PartitionBound);
        Assert.Matches(
            "^[0-9a-f]{64}$",
            observation.StableChildIdentityHash);
        Assert.Matches(
            "^[0-9a-f]{64}$",
            observation.StableConfigSchemaHash);
        Assert.Matches(
            "^[0-9a-f]{64}$",
            observation.ObservationMetricsHash);
        Assert.Empty(observation.RootReasons);
        var evidence = ReadEvidence(first.CycleId.Value);
        Assert.Equal(2, evidence.Count);
        Assert.Null(evidence[0].PreviousHash);
        Assert.Equal(
            evidence[0].CurrentHash,
            evidence[1].PreviousHash);
        var summaryEvidence = Scalar<string>(
            """
            SELECT payload::TEXT
            FROM snapshot_generation_retention_evidence
            WHERE cycle_id = (
                    SELECT cycle_id
                    FROM snapshot_generation_retention_cycles
                    WHERE trigger_scrape_id =
                        2000
                      AND trigger_publication_id =
                        9000
                )
              AND kind = 'summary'
            """);
        Assert.Contains(
            "plannerPublicationSourceValidations",
            summaryEvidence,
            StringComparison.Ordinal);
        Assert.Contains(
            "oraclePublicationSourceValidations",
            summaryEvidence,
            StringComparison.Ordinal);
        Assert.Contains(
            "plannerIndexTopologyValidations",
            summaryEvidence,
            StringComparison.Ordinal);
        Assert.Contains(
            "oracleIndexTopologyValidations",
            summaryEvidence,
            StringComparison.Ordinal);
        var mutation = Assert.Throws<PostgresException>(
            () => Execute(
                """
                UPDATE snapshot_generation_retention_evidence
                SET payload = '{}'::jsonb
                WHERE cycle_id = @cycleId
                """,
                command =>
                    command.Parameters.AddWithValue(
                        "cycleId",
                        first.CycleId.Value)));
        Assert.Equal("55000", mutation.SqlState);
    }

    [Fact]
    public async Task PlannerVersionThreePreservesExistingVersionTwoCycle()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        Execute(
            """
            INSERT INTO snapshot_generation_retention_cycles (
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
                anomalies)
            VALUES (
                2000,
                9000,
                'terminal_worker_post_publication',
                now(),
                2,
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
                '[{"code":"version_two_evidence"}]')
            """);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var cycle = await CycleAsync(result);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Existing,
            result.Disposition);
        Assert.Equal(2, cycle.PlannerVersion);
        Assert.Equal(
            "[{\"code\": \"version_two_evidence\"}]",
            cycle.AnomaliesJson);
        Assert.Equal(
            1,
            Scalar<long>(
                "SELECT COUNT(*) FROM snapshot_generation_retention_cycles"));
    }

    [Fact]
    public async Task FourPublicationRotationRetiresOldSurfacesWithoutDiscardingImmutableCycles()
    {
        var planner = CreatePlanner();
        var publications =
            new List<(long ScrapeId, long PublicationId, long CycleId)>();

        for (var index = 0; index < 4; index++)
        {
            var scrapeId = _fixture.Db.StartScrapeRun();
            _fixture.Db.CompleteScrapeRun(
                scrapeId,
                songsScraped: 1,
                totalEntries: index,
                totalRequests: 1,
                totalBytes: 1);
            _fixture.Db.PublishScrapeRun(
                scrapeId,
                promoteCachedResponses: false);
            var publicationId = _fixture.Db
                .GetPublicationPointerState()
                .CurrentPublicationId!.Value;
            SetReadyPublicationSourceBinding(
                scrapeId,
                publicationId,
                $"rotation-{index}");

            var result = await planner.PlanAsync(
                new SnapshotGenerationRetentionPlanRequest(
                    scrapeId,
                    publicationId,
                    DateTime.UtcNow,
                    scrapeId,
                    BackgroundWorkQuiesced: true));

            Assert.Equal(
                SnapshotGenerationRetentionPlanDisposition.Observed,
                result.Disposition);
            Assert.True(result.OracleAgreement);
            publications.Add(
                (scrapeId, publicationId, result.CycleId!.Value));
        }

        var pointers = _fixture.Db.GetPublicationPointerState();
        Assert.Equal(
            publications[3].PublicationId,
            pointers.CurrentPublicationId);
        Assert.Equal(
            publications[2].PublicationId,
            pointers.PreviousPublicationId);
        Assert.Equal(
            PublicationGenerationStatus.Retired,
            _fixture.Db.GetPublicationGeneration(
                publications[0].PublicationId)!.Status);
        Assert.Equal(
            PublicationGenerationStatus.Retired,
            _fixture.Db.GetPublicationGeneration(
                publications[1].PublicationId)!.Status);
        Assert.Equal(
            PublicationGenerationStatus.Retained,
            _fixture.Db.GetPublicationGeneration(
                publications[2].PublicationId)!.Status);
        Assert.Equal(
            PublicationGenerationStatus.Current,
            _fixture.Db.GetPublicationGeneration(
                publications[3].PublicationId)!.Status);
        Assert.Equal(
            0,
            Scalar<long>(
                """
                SELECT COUNT(*)
                FROM leaderboard_published_scope_source
                WHERE published_scrape_id = ANY(@scrapeIds)
                """,
                command => command.Parameters.AddWithValue(
                    "scrapeIds",
                    new[]
                    {
                        publications[0].ScrapeId,
                        publications[1].ScrapeId,
                    })));
        Assert.Equal(
            2,
            Scalar<long>(
                """
                SELECT COUNT(*)
                FROM leaderboard_published_scope_source
                WHERE published_scrape_id = ANY(@scrapeIds)
                """,
                command => command.Parameters.AddWithValue(
                    "scrapeIds",
                    new[]
                    {
                        publications[2].ScrapeId,
                        publications[3].ScrapeId,
                    })));
        Assert.Equal(
            4,
            Scalar<long>(
                "SELECT COUNT(*) FROM snapshot_generation_retention_cycles"));
        Assert.NotEmpty(ReadEvidence(publications[0].CycleId));
        var mutation = Assert.Throws<PostgresException>(
            () => Execute(
                """
                UPDATE snapshot_generation_retention_cycles
                SET status = 'failed'
                WHERE cycle_id = @cycleId
                """,
                command => command.Parameters.AddWithValue(
                    "cycleId",
                    publications[0].CycleId)));
        Assert.Equal("55000", mutation.SqlState);
    }

    [Fact]
    public async Task ActiveAndProjectionRootsAreChildScoped()
    {
        SeedBaseline(
            ("Solo_Guitar", 1308),
            ("Solo_Bass", 1308),
            ("Solo_Vocals", 1308));
        Execute(
            """
            INSERT INTO leaderboard_snapshot_state (
                song_id,
                instrument,
                active_snapshot_id,
                scrape_id,
                is_finalized,
                updated_at)
            VALUES (
                'active-song',
                'Solo_Guitar',
                1308,
                1308,
                TRUE,
                now());

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
                'projection-song',
                'Solo_Bass',
                1,
                0,
                1308,
                'snapshot',
                'ready',
                now());
            """);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var observations = await ObservationsAsync(result);

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(2, result.ProtectedCount);
        Assert.Equal(
            SnapshotGenerationRetentionClassification.Protected,
            ByInstrument(observations, "Solo_Guitar")
                .Classification);
        Assert.Contains(
            "active_snapshot",
            ByInstrument(observations, "Solo_Guitar")
                .RootReasons);
        Assert.Equal(
            SnapshotGenerationRetentionClassification.Protected,
            ByInstrument(observations, "Solo_Bass")
                .Classification);
        Assert.Contains(
            "projection_source",
            ByInstrument(observations, "Solo_Bass")
                .RootReasons);
        Assert.Equal(
            SnapshotGenerationRetentionClassification.Candidate,
            ByInstrument(observations, "Solo_Vocals")
                .Classification);
    }

    [Fact]
    public async Task NamedPublicationSourceRootsOnlyItsPhysicalChild()
    {
        SeedBaseline(
            ("Solo_Guitar", 1308),
            ("Solo_Bass", 1308));
        InsertSnapshotPublicationSource(
            CurrentScrapeId,
            "source-song",
            "Solo_Guitar",
            1308);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var observations = await ObservationsAsync(result);

        Assert.Equal(
            SnapshotGenerationRetentionClassification.Protected,
            ByInstrument(observations, "Solo_Guitar")
                .Classification);
        Assert.Contains(
            "named_publication_source",
            ByInstrument(observations, "Solo_Guitar")
                .RootReasons);
        Assert.Equal(
            SnapshotGenerationRetentionClassification.Candidate,
            ByInstrument(observations, "Solo_Bass")
                .Classification);
    }

    [Fact]
    public async Task PartialNamedPublicationSourceSetFailsClosed()
    {
        SeedBaseline(("Solo_Guitar", 1308));
        InsertSnapshotPublicationSource(
            CurrentScrapeId,
            "second-source",
            "Solo_Guitar",
            1308);
        SetCurrentPublicationSourceBinding(
            expectedCount: 2,
            keyHash: new string('a', 64));
        Execute(
            """
            DELETE FROM leaderboard_published_scope_source
            WHERE published_scrape_id = @scrapeId
              AND song_id = 'baseline-empty'
            """,
            command => command.Parameters.AddWithValue(
                "scrapeId",
                CurrentScrapeId));

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        var cycle = await new SnapshotGenerationRetentionRepository(
                _fixture.DataSource)
            .GetCycleForSafePointAsync(
                CurrentScrapeId,
                CurrentPublicationId);
        Assert.NotNull(cycle);
        Assert.Contains(
            "named_publication_source_set_invalid",
            cycle!.GlobalBlockersJson,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-binding")]
    [InlineData("row-count")]
    [InlineData("key-hash")]
    [InlineData("binding-identity")]
    [InlineData("malformed-source")]
    [InlineData("extra-source")]
    public async Task NamedPublicationSourceBindingFailuresFailClosed(
        string mutation)
    {
        SeedBaseline(("Solo_Guitar", 1307));
        switch (mutation)
        {
            case "missing-binding":
                Execute(
                    """
                    DELETE FROM publication_surface_bindings
                    WHERE publication_id = @publicationId
                      AND surface_name = 'solo_scope_sources'
                    """,
                    command => command.Parameters.AddWithValue(
                        "publicationId",
                        CurrentPublicationId));
                break;
            case "row-count":
                Execute(
                    """
                    UPDATE publication_surface_bindings
                    SET row_count = row_count + 1
                    WHERE publication_id = @publicationId
                      AND surface_name = 'solo_scope_sources'
                    """,
                    command => command.Parameters.AddWithValue(
                        "publicationId",
                        CurrentPublicationId));
                break;
            case "key-hash":
                Execute(
                    """
                    UPDATE publication_surface_bindings
                    SET content_hash = repeat('f', 64)
                    WHERE publication_id = @publicationId
                      AND surface_name = 'solo_scope_sources'
                    """,
                    command => command.Parameters.AddWithValue(
                        "publicationId",
                        CurrentPublicationId));
                break;
            case "binding-identity":
                Execute(
                    """
                    UPDATE publication_surface_bindings
                    SET binding_json = binding_json
                        || jsonb_build_object(
                            'publishedScrapeId',
                            @scrapeId + 1)
                    WHERE publication_id = @publicationId
                      AND surface_name = 'solo_scope_sources'
                    """,
                    command =>
                    {
                        command.Parameters.AddWithValue(
                            "publicationId",
                            CurrentPublicationId);
                        command.Parameters.AddWithValue(
                            "scrapeId",
                            CurrentScrapeId);
                    });
                break;
            case "malformed-source":
                Execute(
                    """
                    UPDATE leaderboard_published_scope_source
                    SET is_complete = FALSE
                    WHERE published_scrape_id = @scrapeId
                    """,
                    command => command.Parameters.AddWithValue(
                        "scrapeId",
                        CurrentScrapeId));
                break;
            case "extra-source":
                Execute(
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
                        @scrapeId,
                        'unexpected-extra',
                        'Solo_Bass',
                        'alltime',
                        'empty',
                        NULL,
                        @scrapeId,
                        0,
                        'extra-content',
                        'extra-coverage',
                        0,
                        0,
                        TRUE,
                        now(),
                        now())
                    """,
                    command => command.Parameters.AddWithValue(
                        "scrapeId",
                        CurrentScrapeId));
                break;
        }

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var cycle = await new SnapshotGenerationRetentionRepository(
                _fixture.DataSource)
            .GetCycleForSafePointAsync(
                CurrentScrapeId,
                CurrentPublicationId);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.NotNull(cycle);
        Assert.Contains(
            "named_publication_source_set_invalid",
            cycle!.GlobalBlockersJson,
            StringComparison.Ordinal);
        Assert.Contains(
            "oracle_named_publication_source_set_invalid",
            cycle.GlobalBlockersJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PublicationSourceSchemaRejectsDuplicateExactKey()
    {
        SeedBaseline(("Solo_Guitar", 1307));

        var error = Assert.Throws<PostgresException>(
            () => Execute(
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
                SELECT
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
                    validated_at
                FROM leaderboard_published_scope_source
                WHERE published_scrape_id = @scrapeId
                """,
                command => command.Parameters.AddWithValue(
                    "scrapeId",
                    CurrentScrapeId)));

        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            error.SqlState);
    }

    [Fact]
    public async Task MissingDefaultOnChildlessInstrumentIsGlobalBlocker()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        Execute(
            """
            DROP TABLE
                leaderboard_entries_snapshot_solo_guitar_s1307;
            DROP TABLE
                leaderboard_entries_snapshot_solo_guitar_default;
            """);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        var cycle = await new SnapshotGenerationRetentionRepository(
                _fixture.DataSource)
            .GetCycleForSafePointAsync(
                CurrentScrapeId,
                CurrentPublicationId);
        Assert.NotNull(cycle);
        Assert.Contains(
            "default_child_shape_invalid",
            cycle!.GlobalBlockersJson,
            StringComparison.Ordinal);
        Assert.Contains(
            "default_child_index_missing",
            cycle.GlobalBlockersJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRootWithNoChildrenIsGlobalBlocker()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        Execute(
            """
            DROP TABLE
                leaderboard_entries_snapshot_solo_guitar
                CASCADE
            """);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var cycle = await new SnapshotGenerationRetentionRepository(
                _fixture.DataSource)
            .GetCycleForSafePointAsync(
                CurrentScrapeId,
                CurrentPublicationId);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.NotNull(cycle);
        Assert.Contains(
            "instrument_root_shape_invalid",
            cycle!.GlobalBlockersJson,
            StringComparison.Ordinal);
        Assert.Contains(
            "instrument_generation_children_missing",
            cycle.GlobalBlockersJson,
            StringComparison.Ordinal);
        Assert.Contains(
            "instrument_root_index_missing",
            cycle.GlobalBlockersJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidPartitionStrategyWithoutChildrenIsGlobalBlocker()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        Execute(
            """
            ALTER TABLE leaderboard_entries_snapshot
                DETACH PARTITION
                    leaderboard_entries_snapshot_solo_guitar;
            DROP TABLE
                leaderboard_entries_snapshot_solo_guitar
                CASCADE;
            CREATE TABLE
                leaderboard_entries_snapshot_solo_guitar
                (
                    LIKE leaderboard_entries_snapshot
                    INCLUDING DEFAULTS
                    INCLUDING CONSTRAINTS
                )
                PARTITION BY RANGE (snapshot_id);
            ALTER TABLE leaderboard_entries_snapshot
                ATTACH PARTITION
                    leaderboard_entries_snapshot_solo_guitar
                FOR VALUES IN ('Solo_Guitar');
            CREATE TABLE
                leaderboard_entries_snapshot_solo_guitar_default
                PARTITION OF
                    leaderboard_entries_snapshot_solo_guitar
                DEFAULT;
            """);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var cycle = await new SnapshotGenerationRetentionRepository(
                _fixture.DataSource)
            .GetCycleForSafePointAsync(
                CurrentScrapeId,
                CurrentPublicationId);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.NotNull(cycle);
        Assert.Contains(
            "instrument_root_shape_invalid",
            cycle!.GlobalBlockersJson,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "DROP INDEX public.ix_les_snapshot_song_score",
        "snapshot_parent_index_missing")]
    [InlineData(
        """
        UPDATE pg_index
        SET indisvalid = FALSE
        WHERE indexrelid =
            'public.ix_les_snapshot_song_score'::regclass
        """,
        "snapshot_parent_index_invalid")]
    [InlineData(
        """
        UPDATE pg_index
        SET indisready = FALSE
        WHERE indexrelid =
            'public.ix_les_snapshot_song_score'::regclass
        """,
        "snapshot_parent_index_unready")]
    public async Task InvalidTopSnapshotIndexBlocksChildlessCycle(
        string mutationSql,
        string blockerCode)
    {
        SeedBaseline();
        Execute(mutationSql);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var cycle = await new SnapshotGenerationRetentionRepository(
                _fixture.DataSource)
            .GetCycleForSafePointAsync(
                CurrentScrapeId,
                CurrentPublicationId);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.True(result.OracleAgreement);
        Assert.Contains(
            blockerCode,
            cycle!.GlobalBlockersJson,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        """
        UPDATE pg_index
        SET indisvalid = FALSE
        WHERE indexrelid = (
            SELECT index_row.indexrelid
            FROM pg_index index_row
            JOIN pg_class parent_index
              ON parent_index.oid = index_row.indexrelid
            JOIN pg_inherits inheritance
              ON inheritance.inhrelid = parent_index.oid
            WHERE index_row.indrelid =
                    'public.leaderboard_entries_snapshot_solo_guitar'
                        ::regclass
            ORDER BY parent_index.relname
            LIMIT 1)
        """,
        "instrument_root_index_invalid")]
    [InlineData(
        """
        UPDATE pg_index
        SET indisready = FALSE
        WHERE indexrelid = (
            SELECT index_row.indexrelid
            FROM pg_index index_row
            JOIN pg_class parent_index
              ON parent_index.oid = index_row.indexrelid
            JOIN pg_inherits inheritance
              ON inheritance.inhrelid = parent_index.oid
            WHERE index_row.indrelid =
                    'public.leaderboard_entries_snapshot_solo_guitar'
                        ::regclass
            ORDER BY parent_index.relname
            LIMIT 1)
        """,
        "instrument_root_index_unready")]
    [InlineData(
        """
        CREATE INDEX ix_retention_detached_root
        ON ONLY
            public.leaderboard_entries_snapshot_solo_guitar
            (snapshot_id)
        """,
        "instrument_root_index_detached")]
    public async Task InvalidInstrumentRootIndexBlocksChildlessCycle(
        string mutationSql,
        string blockerCode)
    {
        SeedBaseline();
        Execute(mutationSql);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var cycle = await new SnapshotGenerationRetentionRepository(
                _fixture.DataSource)
            .GetCycleForSafePointAsync(
                CurrentScrapeId,
                CurrentPublicationId);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.True(result.OracleAgreement);
        Assert.Contains(
            blockerCode,
            cycle!.GlobalBlockersJson,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        """
        UPDATE pg_index
        SET indisvalid = FALSE
        WHERE indexrelid = (
            SELECT index_row.indexrelid
            FROM pg_index index_row
            JOIN pg_class index_relation
              ON index_relation.oid = index_row.indexrelid
            WHERE index_row.indrelid =
                    'public.leaderboard_entries_snapshot_solo_guitar_default'
                        ::regclass
            ORDER BY index_relation.relname
            LIMIT 1)
        """,
        "default_child_index_invalid")]
    [InlineData(
        """
        UPDATE pg_index
        SET indisready = FALSE
        WHERE indexrelid = (
            SELECT index_row.indexrelid
            FROM pg_index index_row
            JOIN pg_class index_relation
              ON index_relation.oid = index_row.indexrelid
            WHERE index_row.indrelid =
                    'public.leaderboard_entries_snapshot_solo_guitar_default'
                        ::regclass
            ORDER BY index_relation.relname
            LIMIT 1)
        """,
        "default_child_index_unready")]
    [InlineData(
        """
        CREATE INDEX ix_retention_detached_default
        ON public.leaderboard_entries_snapshot_solo_guitar_default
            (snapshot_id)
        """,
        "default_child_index_detached")]
    public async Task InvalidDefaultChildIndexBlocksChildlessCycle(
        string mutationSql,
        string blockerCode)
    {
        SeedBaseline();
        Execute(mutationSql);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var cycle = await new SnapshotGenerationRetentionRepository(
                _fixture.DataSource)
            .GetCycleForSafePointAsync(
                CurrentScrapeId,
                CurrentPublicationId);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.True(result.OracleAgreement);
        Assert.Contains(
            blockerCode,
            cycle!.GlobalBlockersJson,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "missing",
        "\"missingParentIndexCount\":1")]
    [InlineData(
        "invalid",
        "\"invalidIndexCount\":1")]
    [InlineData(
        "unready",
        "\"unreadyIndexCount\":1")]
    [InlineData(
        "detached",
        "\"detachedIndexCount\":1")]
    [InlineData(
        "attributes",
        "\"attributeMismatchIndexCount\":1")]
    public async Task IndependentOracleValidatesEveryNumericChildIndex(
        string mutation,
        string expectedOracleFact)
    {
        SeedBaseline(("Solo_Guitar", 1307));
        switch (mutation)
        {
            case "missing":
                Execute(
                    """
                    DELETE FROM pg_inherits
                    WHERE inhrelid = (
                        SELECT index_relation.oid
                        FROM pg_index index_row
                        JOIN pg_class index_relation
                          ON index_relation.oid =
                                index_row.indexrelid
                        JOIN pg_inherits attachment
                          ON attachment.inhrelid =
                                index_relation.oid
                        WHERE index_row.indrelid =
                                'public.leaderboard_entries_snapshot_solo_guitar_s1307'
                                    ::regclass
                        ORDER BY index_relation.relname
                        LIMIT 1)
                    """);
                break;
            case "invalid":
                Execute(
                    """
                    UPDATE pg_index
                    SET indisvalid = FALSE
                    WHERE indexrelid = (
                        SELECT index_relation.oid
                        FROM pg_index index_row
                        JOIN pg_class index_relation
                          ON index_relation.oid =
                                index_row.indexrelid
                        JOIN pg_inherits attachment
                          ON attachment.inhrelid =
                                index_relation.oid
                        WHERE index_row.indrelid =
                                'public.leaderboard_entries_snapshot_solo_guitar_s1307'
                                    ::regclass
                        ORDER BY index_relation.relname
                        LIMIT 1)
                    """);
                break;
            case "unready":
                Execute(
                    """
                    UPDATE pg_index
                    SET indisready = FALSE
                    WHERE indexrelid = (
                        SELECT index_relation.oid
                        FROM pg_index index_row
                        JOIN pg_class index_relation
                          ON index_relation.oid =
                                index_row.indexrelid
                        JOIN pg_inherits attachment
                          ON attachment.inhrelid =
                                index_relation.oid
                        WHERE index_row.indrelid =
                                'public.leaderboard_entries_snapshot_solo_guitar_s1307'
                                    ::regclass
                        ORDER BY index_relation.relname
                        LIMIT 1)
                    """);
                break;
            case "detached":
                Execute(
                    """
                    CREATE INDEX
                        ix_retention_detached_numeric_child
                    ON public
                        .leaderboard_entries_snapshot_solo_guitar_s1307
                        (snapshot_id)
                    """);
                break;
            case "attributes":
                Execute(
                    """
                    UPDATE pg_index
                    SET indisunique = NOT indisunique
                    WHERE indexrelid = (
                        SELECT index_relation.oid
                        FROM pg_index index_row
                        JOIN pg_class index_relation
                          ON index_relation.oid =
                                index_row.indexrelid
                        JOIN pg_inherits attachment
                          ON attachment.inhrelid =
                                index_relation.oid
                        WHERE index_row.indrelid =
                                'public.leaderboard_entries_snapshot_solo_guitar_s1307'
                                    ::regclass
                        ORDER BY index_relation.relname
                        LIMIT 1)
                    """);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(mutation));
        }

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var cycle = await new SnapshotGenerationRetentionRepository(
                _fixture.DataSource)
            .GetCycleForSafePointAsync(
                CurrentScrapeId,
                CurrentPublicationId);
        var observation = Assert.Single(
            await ObservationsAsync(result));

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.True(result.OracleAgreement);
        Assert.Contains(
            "generation_child_index_shape_invalid",
            observation.BlockerCodes);
        Assert.Equal(0, result.CandidateCount);
        Assert.Contains(
            "oracle_index_topology_invalid",
            cycle!.GlobalBlockersJson,
            StringComparison.Ordinal);
        using var blockerDocument =
            JsonDocument.Parse(
                cycle.GlobalBlockersJson);
        var oracleDetail = blockerDocument.RootElement
            .EnumerateArray()
            .Single(blocker =>
                blocker.GetProperty("code")
                    .GetString()
                == "oracle_index_topology_invalid")
            .GetProperty("detail")
            .GetString()!;
        var comparisonStart =
            oracleDetail.IndexOf(
                '{',
                StringComparison.Ordinal);
        var comparisonEnd =
            oracleDetail.LastIndexOf(
                '}');
        Assert.True(comparisonStart >= 0);
        Assert.True(comparisonEnd > comparisonStart);
        using var comparisonDocument =
            JsonDocument.Parse(
                oracleDetail[
                    comparisonStart
                    ..(comparisonEnd + 1)]);
        var numericFacts = comparisonDocument.RootElement
            .GetProperty(
                "numericChildIndexValidations")
            .EnumerateArray()
            .Select(static fact =>
                fact.GetString()!)
            .ToArray();
        Assert.Contains(
            numericFacts,
            fact => fact.Contains(
                expectedOracleFact,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task RootedRunningChildStillBlocksCleanCycle()
    {
        SeedBaseline(("Solo_Guitar", 1308));
        Execute(
            """
            UPDATE scrape_log
            SET status = 'running',
                completed_at = NULL
            WHERE id = 1308;

            INSERT INTO leaderboard_snapshot_state (
                song_id,
                instrument,
                active_snapshot_id,
                scrape_id,
                is_finalized,
                updated_at)
            VALUES (
                'running-root',
                'Solo_Guitar',
                1308,
                1308,
                TRUE,
                now());
            """);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var observation = Assert.Single(
            await ObservationsAsync(result));

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.Equal(
            SnapshotGenerationRetentionClassification.Protected,
            observation.Classification);
        Assert.Contains(
            "scrape_not_terminal",
            observation.BlockerCodes);
    }

    [Fact]
    public async Task InvalidTriggerScrapeIdentityBlocksCycle()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        Execute(
            """
            UPDATE scrape_log
            SET status = 'running',
                completed_at = NULL
            WHERE id = @scrapeId
            """,
            command => command.Parameters.AddWithValue(
                "scrapeId",
                CurrentScrapeId));

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var observation = Assert.Single(
            await ObservationsAsync(result));

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.Contains(
            "trigger_scrape_not_terminal",
            observation.BlockerCodes);
    }

    [Fact]
    public async Task InvalidPreviousPublicationScrapeIdentityBlocksCycle()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        AddPreviousPublication(
            publicationId: 8999,
            scrapeId: 1999);
        Execute(
            """
            UPDATE scrape_log
            SET status = 'running',
                completed_at = NULL
            WHERE id = 1999
            """);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var cycle = await new SnapshotGenerationRetentionRepository(
                _fixture.DataSource)
            .GetCycleForSafePointAsync(
                CurrentScrapeId,
                CurrentPublicationId);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.NotNull(cycle);
        Assert.Contains(
            "named_publication_scrape_not_terminal",
            cycle!.GlobalBlockersJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidWorkingPublicationScrapeIdentityIsInDeferralEvidence()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        var keyHash =
            PublishedScopeSourceBindingContract.ComputeKeyHash(
            [
                new PublishedScopeSourceKey(
                    "Solo_Guitar",
                    "working-empty",
                    "alltime"),
            ]);
        Execute(
            """
            INSERT INTO scrape_log (
                id,
                started_at,
                status)
            VALUES (
                2001,
                now(),
                'running');

            INSERT INTO publication_generations (
                publication_id,
                scrape_id,
                status,
                previous_publication_id,
                created_at,
                ready_at,
                metadata)
            VALUES (
                9001,
                2001,
                'ready',
                @currentPublicationId,
                now(),
                now(),
                jsonb_build_object(
                    'publicationPreparation',
                    jsonb_build_object(
                        'scrapeId', 2001,
                        'publicationId', 9001,
                        'expectedPublishedScopeCount', 1)));

            UPDATE scrape_publication_state
            SET working_publication_id = 9001,
                updated_at = now()
            WHERE id = TRUE;

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
                2001,
                'working-empty',
                'Solo_Guitar',
                'alltime',
                'empty',
                NULL,
                2001,
                0,
                'working-content',
                'working-coverage',
                0,
                0,
                TRUE,
                now(),
                now());

            INSERT INTO publication_surface_bindings (
                publication_id,
                surface_name,
                binding_kind,
                binding_json,
                row_count,
                content_hash,
                status,
                built_at)
            VALUES (
                9001,
                'solo_scope_sources',
                'scrape_id',
                jsonb_build_object(
                    'publicationId', 9001,
                    'table',
                        'leaderboard_published_scope_source',
                    'publishedScrapeId', 2001,
                    'keyHashVersion', 1),
                1,
                @keyHash,
                'ready',
                now());
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "currentPublicationId",
                    CurrentPublicationId);
                command.Parameters.AddWithValue(
                    "keyHash",
                    keyHash);
            });

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var deferral = Assert.Single(
            await new SnapshotGenerationRetentionRepository(
                    _fixture.DataSource)
                .GetDeferralsAsync(CurrentPublicationId));

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Deferred,
            result.Disposition);
        Assert.Contains(
            "named_publication_scrape_not_terminal",
            deferral.EvidenceJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnnamedRetiredPublicationSourceDoesNotRemainALivenessRoot()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        Execute(
            """
            INSERT INTO scrape_log (
                id,
                started_at,
                completed_at,
                status)
            VALUES (
                1990,
                now() - interval '3 days',
                now() - interval '2 days',
                'completed');

            INSERT INTO publication_generations (
                publication_id,
                scrape_id,
                status,
                created_at,
                ready_at,
                published_at)
            VALUES (
                8990,
                1990,
                'retired',
                now(),
                now(),
                now());
            """);
        InsertSnapshotPublicationSource(
            1990,
            "retired-source",
            "Solo_Guitar",
            1307);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var observation = Assert.Single(
            await ObservationsAsync(result));

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Observed,
            result.Disposition);
        Assert.Equal(
            SnapshotGenerationRetentionClassification.Candidate,
            observation.Classification);
        Assert.Empty(observation.RootReasons);
    }

    [Fact]
    public async Task UnreplayedWriterFailureForScrape1308IsMandatoryAndChildScoped()
    {
        SeedBaseline(
            ("Solo_Guitar", 1308),
            ("Solo_Bass", 1308));
        Execute(
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
                1308,
                'online',
                'Solo_Guitar',
                'failed-song',
                1,
                0,
                'InjectedFailure',
                'retained evidence',
                now())
            """);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var observations = await ObservationsAsync(result);
        var guitar = ByInstrument(
            observations,
            "Solo_Guitar");

        Assert.Equal(
            SnapshotGenerationRetentionClassification.Protected,
            guitar.Classification);
        Assert.Contains(
            "unreplayed_writer_failure",
            guitar.RootReasons);
        Assert.Equal(
            SnapshotGenerationRetentionClassification.Candidate,
            ByInstrument(observations, "Solo_Bass")
                .Classification);
        Assert.DoesNotContain(
            typeof(DatabaseMaintenanceOptions).GetProperties(),
            property =>
                property.Name.Contains(
                    "WriterFailure",
                    StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunningResumeAndExplicitHoldRootsArePersisted()
    {
        SeedBaseline(
            ("Solo_Guitar", 1305),
            ("Solo_Bass", 1306),
            ("Solo_Vocals", 1307));
        Execute(
            """
            UPDATE scrape_log
            SET status = 'running',
                completed_at = NULL
            WHERE id = 1305;

            INSERT INTO snapshot_generation_retention_holds (
                instrument,
                snapshot_id,
                hold_kind,
                reason,
                created_by)
            VALUES (
                'Solo_Vocals',
                1307,
                'restore_in_flight',
                'isolated restore proof',
                'test');
            """);

        var result = await CreatePlanner(
                resumeScrapeId: 1306)
            .PlanAsync(CreateRequest());
        var observations = await ObservationsAsync(result);

        Assert.Contains(
            "running_scrape",
            ByInstrument(observations, "Solo_Guitar")
                .RootReasons);
        Assert.Contains(
            "configured_resume_scrape",
            ByInstrument(observations, "Solo_Bass")
                .RootReasons);
        Assert.Contains(
            ByInstrument(observations, "Solo_Vocals")
                .RootReasons,
            reason => reason.StartsWith(
                "hold:restore_in_flight:",
                StringComparison.Ordinal));
        Assert.Equal(0, result.CandidateCount);
        Assert.Equal(3, result.ProtectedCount);
    }

    [Fact]
    public async Task LegacyUnpointedRetainedPublicationsAreDurableAnomaliesWithoutBlockingCandidates()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        for (var index = 0; index < 39; index++)
        {
            Execute(
                """
                INSERT INTO scrape_log (
                    id,
                    started_at,
                    completed_at,
                    status)
                VALUES (
                    @scrapeId,
                    now() - interval '3 days',
                    now() - interval '2 days',
                    'completed');

                INSERT INTO publication_generations (
                    publication_id,
                    scrape_id,
                    status,
                    created_at,
                    ready_at,
                    published_at)
                VALUES (
                    @publicationId,
                    @scrapeId,
                    'retained',
                    now(),
                    now(),
                    now());

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
                    @scrapeId,
                    @songId,
                    'Solo_Guitar',
                    'alltime',
                    'empty',
                    NULL,
                    @scrapeId,
                    0,
                    'legacy-content',
                    'legacy-coverage',
                    0,
                    0,
                    TRUE,
                    now(),
                    now());

                INSERT INTO publication_surface_bindings (
                    publication_id,
                    surface_name,
                    binding_kind,
                    binding_json,
                    row_count,
                    content_hash,
                    status,
                    built_at)
                VALUES (
                    @publicationId,
                    'solo_scope_sources',
                    'legacy_scrape_id',
                    jsonb_build_object(
                        'table',
                            'leaderboard_published_scope_source'),
                    1,
                    NULL,
                    'ready',
                    now());
                """,
                command =>
                {
                    command.Parameters.AddWithValue(
                        "scrapeId",
                        18_000L + index);
                    command.Parameters.AddWithValue(
                        "publicationId",
                        10_000L + index);
                    command.Parameters.AddWithValue(
                        "songId",
                        $"legacy-{index}");
                });
        }

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var cycleId = result.CycleId
            ?? throw new InvalidOperationException(
                "Expected a persisted retention cycle.");
        var observation = Assert.Single(
            await ObservationsAsync(result));

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Observed,
            result.Disposition);
        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(0, result.BlockedCount);
        Assert.Empty(observation.RootReasons);
        Assert.Equal(
            SnapshotGenerationRetentionClassification.Candidate,
            observation.Classification);
        Assert.DoesNotContain(
            "unpointed_retained_publication",
            observation.BlockerCodes);
        var cycle =
            await new SnapshotGenerationRetentionRepository(
                    _fixture.DataSource)
                .GetCycleForSafePointAsync(
                    CurrentScrapeId,
                    CurrentPublicationId);
        Assert.NotNull(cycle);
        Assert.Equal(3, cycle!.PlannerVersion);
        var anomaliesJson = Scalar<string>(
            """
            SELECT anomalies::TEXT
            FROM snapshot_generation_retention_cycles
            WHERE cycle_id = @cycleId
            """,
            command => command.Parameters.AddWithValue(
                "cycleId",
                cycleId));
        Assert.Equal(anomaliesJson, cycle.AnomaliesJson);
        using var anomalies =
            JsonDocument.Parse(anomaliesJson);
        Assert.Equal(
            39,
            anomalies.RootElement.GetArrayLength());
        Assert.All(
            anomalies.RootElement.EnumerateArray(),
            anomaly =>
            {
                Assert.Equal(
                    "unpointed_retained_publication",
                    anomaly.GetProperty("code").GetString());
                Assert.Equal(
                    PublicationGenerationStatus.Retained,
                    anomaly.GetProperty(
                            "publicationStatus")
                        .GetString());
                Assert.True(
                    anomaly.GetProperty("publicationId")
                        .GetInt64() >= 10_000);
            });
        var summaryEvidence = Scalar<string>(
            """
            SELECT payload::TEXT
            FROM snapshot_generation_retention_evidence
            WHERE cycle_id = @cycleId
              AND kind = 'summary'
            """,
            command => command.Parameters.AddWithValue(
                "cycleId",
                cycleId));
        Assert.Contains(
            "\"anomalies\"",
            summaryEvidence,
            StringComparison.Ordinal);
        Assert.Contains(
            "unpointed_retained_publication",
            summaryEvidence,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "unpointed_retained_publication",
            Scalar<string>(
                """
                SELECT global_blockers::TEXT
                FROM snapshot_generation_retention_cycles
                WHERE cycle_id = @cycleId
                """,
                command => command.Parameters.AddWithValue(
                    "cycleId",
                    cycleId)),
            StringComparison.Ordinal);
        Assert.Equal(
            39,
            Scalar<long>(
                """
                SELECT COUNT(*)
                FROM publication_generations
                WHERE publication_id >= 10000
                  AND publication_id < 10039
                  AND status = 'retained'
                """));
        Assert.Equal(
            39,
            Scalar<long>(
                """
                SELECT COUNT(*)
                FROM publication_surface_bindings
                WHERE publication_id >= 10000
                  AND publication_id < 10039
                  AND binding_kind = 'legacy_scrape_id'
                  AND status = 'ready'
                """));
        Assert.Equal(
            39,
            Scalar<long>(
                """
                SELECT COUNT(*)
                FROM leaderboard_published_scope_source
                WHERE published_scrape_id >= 18000
                  AND published_scrape_id < 18039
                """));
        var anomalyMutation =
            Assert.Throws<PostgresException>(
                () => Execute(
                    """
                    UPDATE snapshot_generation_retention_cycles
                    SET anomalies = '[]'::jsonb
                    WHERE cycle_id = @cycleId
                    """,
                    command => command.Parameters.AddWithValue(
                        "cycleId",
                        cycleId)));
        Assert.Equal("55000", anomalyMutation.SqlState);
    }

    [Theory]
    [InlineData(PublicationGenerationStatus.Building)]
    [InlineData(PublicationGenerationStatus.Ready)]
    [InlineData(PublicationGenerationStatus.Current)]
    public async Task UnpointedNonterminalPublicationFailsClosed(
        string status)
    {
        SeedBaseline(("Solo_Guitar", 1307));
        Execute(
            """
            INSERT INTO scrape_log (
                id,
                started_at,
                completed_at,
                status)
            VALUES (
                1801,
                now() - interval '3 days',
                now() - interval '2 days',
                'completed');

            INSERT INTO publication_generations (
                publication_id,
                scrape_id,
                status,
                created_at)
            VALUES (
                8998,
                1801,
                @status,
                now());
            """,
            command => command.Parameters.AddWithValue(
                "status",
                status));

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var observation = Assert.Single(
            await ObservationsAsync(result));

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.Contains(
            "unpointed_nonterminal_publication",
            observation.BlockerCodes);
        Assert.Empty(observation.RootReasons);
    }

    [Fact]
    public async Task TerminalUnpointedFailedPublicationInventoryIsDurableAnomalyEvidence()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        foreach (var publicationId in new long[]
                 {
                     5,
                     9,
                     10,
                     34,
                     37,
                     41,
                     48,
                     53,
                 })
        {
            SeedFailedPublication(
                publicationId,
                10_000 + publicationId);
        }
        Execute(
            """
            INSERT INTO publication_surface_bindings (
                publication_id,
                surface_name,
                binding_kind,
                binding_json,
                row_count,
                content_hash,
                status,
                built_at)
            VALUES (
                53,
                'history',
                'failed_generation_history',
                '{"failed":true}'::jsonb,
                0,
                NULL,
                'failed',
                now())
            """);
        SeedFailedPublication(17, 10_017);
        Execute(
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
            SELECT
                10017,
                'orphan-' || value::TEXT,
                'Solo_Guitar',
                'alltime',
                'empty',
                NULL,
                10017,
                0,
                'orphan-content',
                'orphan-coverage',
                0,
                0,
                TRUE,
                now(),
                now()
            FROM generate_series(1, 6273) value
            """);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var observation = Assert.Single(
            await ObservationsAsync(result));
        var cycle = await CycleAsync(result);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Observed,
            result.Disposition);
        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(0, result.BlockedCount);
        Assert.Equal(
            SnapshotGenerationRetentionClassification.Candidate,
            observation.Classification);
        Assert.Equal(3, cycle.PlannerVersion);
        var anomalies = JsonEntries(cycle.AnomaliesJson);
        Assert.Equal(9, anomalies.Count);
        Assert.All(
            anomalies,
            anomaly =>
            {
                Assert.Equal(
                    "unpointed_terminal_failed_publication",
                    anomaly.GetProperty("code").GetString());
                Assert.Empty(
                    anomaly.GetProperty("publicationFailure")
                        .GetProperty("recoveryReasons")
                        .EnumerateArray());
            });
        var orphaned = Assert.Single(
            anomalies,
            anomaly =>
                anomaly.GetProperty("publicationId")
                    .GetInt64() == 17);
        var orphanedFailure =
            orphaned.GetProperty("publicationFailure");
        Assert.Equal(
            PublicationGenerationStatus.Failed,
            orphanedFailure.GetProperty(
                    "publicationStatus")
                .GetString());
        Assert.Equal(
            "failed",
            orphanedFailure.GetProperty("scrapeStatus")
                .GetString());
        Assert.Equal(
            6_273,
            orphanedFailure.GetProperty(
                    "publishedSourceRowCount")
                .GetInt64());
        Assert.Equal(
            0,
            orphanedFailure.GetProperty(
                    "liveSurfaceBindingRowCount")
                .GetInt64());
        var failedBinding = Assert.Single(
            anomalies,
            anomaly =>
                anomaly.GetProperty("publicationId")
                    .GetInt64() == 53);
        Assert.Equal(
            1,
            failedBinding.GetProperty("publicationFailure")
                .GetProperty("failedSurfaceBindingRowCount")
                .GetInt64());
        Assert.DoesNotContain(
            "unpointed_failed_publication",
            cycle.GlobalBlockersJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreplayedWriterFailureBlocksOnlyItsExactFailedGeneration()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        SeedFailedPublication(100, 1308);
        Execute(
            """
            SELECT ensure_leaderboard_snapshot_generation_partition(
                'Solo_Guitar',
                1308);

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
                1308,
                'snapshot',
                'Solo_Guitar',
                'writer-failure',
                1,
                1,
                'InjectedException',
                'retained writer failure evidence',
                now())
            """);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var observations = await ObservationsAsync(result);
        var cycle = await CycleAsync(result);
        var candidate = ByInstrument(
            observations,
            "Solo_Guitar",
            1307);
        var blockedGeneration = ByInstrument(
            observations,
            "Solo_Guitar",
            1308);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Observed,
            result.Disposition);
        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(
            SnapshotGenerationRetentionClassification.Candidate,
            candidate.Classification);
        Assert.DoesNotContain(
            "unreplayed_writer_failure",
            candidate.RootReasons);
        Assert.Contains(
            "unreplayed_writer_failure",
            blockedGeneration.RootReasons);
        Assert.Empty(blockedGeneration.BlockerCodes);
        Assert.Equal(
            SnapshotGenerationRetentionClassification.Protected,
            blockedGeneration.Classification);
        var anomaly = Assert.Single(
            JsonEntries(cycle.AnomaliesJson),
            item =>
                item.GetProperty("publicationId")
                    .GetInt64() == 100);
        Assert.Equal(
            1,
            anomaly.GetProperty("publicationFailure")
                .GetProperty("unreplayedWriterFailureCount")
                .GetInt64());
        Assert.DoesNotContain(
            "unpointed_failed_publication",
            cycle.GlobalBlockersJson,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("surface-ready")]
    [InlineData("surface-building")]
    [InlineData("cache")]
    [InlineData("cache-staging")]
    [InlineData("catalog")]
    [InlineData("path")]
    [InlineData("prepared-band")]
    [InlineData("retained-band")]
    [InlineData("leaderboard-staging")]
    [InlineData("staging-metadata")]
    [InlineData("deep-scrape")]
    public async Task FailedPublicationLiveRecoveryArtifactFailsClosedWithCounts(
        string artifact)
    {
        SeedBaseline(("Solo_Guitar", 1307));
        SeedFailedPublication(8997, 1802);
        AddFailedPublicationArtifact(
            8997,
            1802,
            artifact);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var cycle = await CycleAsync(result);
        var blocker = Assert.Single(
            JsonEntries(cycle.GlobalBlockersJson),
            item => item.GetProperty("code").GetString()
                == "unpointed_failed_publication");
        var failure =
            blocker.GetProperty("publicationFailure");

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.Equal(0, result.CandidateCount);
        Assert.NotEmpty(
            failure.GetProperty("recoveryReasons")
                .EnumerateArray());
        var expectedCountProperty = artifact switch
        {
            "surface-ready" =>
                "readySurfaceBindingRowCount",
            "surface-building" =>
                "buildingSurfaceBindingRowCount",
            "cache" => "apiResponseCacheRowCount",
            "cache-staging" =>
                "apiResponseCacheStagingRowCount",
            "catalog" => "songCatalogRowCount",
            "path" => "pathArtifactRowCount",
            "prepared-band" =>
                "preparedBandRelationCount",
            "retained-band" =>
                "retainedBandRelationCount",
            "leaderboard-staging" =>
                "leaderboardStagingRowCount",
            "staging-metadata" =>
                "leaderboardStagingMetadataRowCount",
            "deep-scrape" =>
                "deepScrapeQueueRowCount",
            _ => throw new InvalidOperationException(
                $"Unexpected test artifact {artifact}."),
        };
        Assert.True(
            failure.GetProperty(expectedCountProperty)
                .GetInt64() > 0);
        Assert.True(
            failure.GetProperty("liveArtifactRowCount")
                .GetInt64() > 0);
        Assert.DoesNotContain(
            JsonEntries(cycle.AnomaliesJson),
            item =>
                item.GetProperty("publicationId")
                    .GetInt64() == 8997);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("resume")]
    public async Task RunningOrResumableFailedPublicationFailsClosedWithIdentity(
        string state)
    {
        SeedBaseline(("Solo_Guitar", 1307));
        SeedFailedPublication(
            8997,
            1802,
            scrapeStatus: state == "running"
                ? "running"
                : "failed");

        var result = await CreatePlanner(
                resumeScrapeId: state == "resume"
                    ? 1802
                    : 0)
            .PlanAsync(CreateRequest());
        var blocker = Assert.Single(
            JsonEntries(
                (await CycleAsync(result))
                    .GlobalBlockersJson),
            item => item.GetProperty("code").GetString()
                == "unpointed_failed_publication");
        var failure =
            blocker.GetProperty("publicationFailure");

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.Contains(
            state == "running"
                ? "running_scrape"
                : "configured_resume_scrape",
            failure.GetProperty("recoveryReasons")
                .EnumerateArray()
                .Select(static item => item.GetString()));
        Assert.Equal(
            1802,
            failure.GetProperty("scrapeId").GetInt64());
    }

    [Fact]
    public async Task NamedFailedPublicationFailsClosedWithRecoveryEvidence()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        SeedFailedPublication(8997, 1802);
        Execute(
            """
            UPDATE scrape_publication_state
            SET previous_publication_id = 8997,
                updated_at = now()
            WHERE id = TRUE;

            UPDATE publication_generations
            SET previous_publication_id = 8997
            WHERE publication_id = 9000
            """);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var blockers = JsonEntries(
            (await CycleAsync(result)).GlobalBlockersJson);
        var failed = Assert.Single(
            blockers,
            item => item.GetProperty("code").GetString()
                == "named_failed_publication");

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.OracleMismatch,
            result.Disposition);
        Assert.Contains(
            "named_pointer:previous",
            failed.GetProperty("publicationFailure")
                .GetProperty("recoveryReasons")
                .EnumerateArray()
                .Select(static item => item.GetString()));
    }

    [Theory]
    [InlineData("freeze")]
    [InlineData("commit")]
    public async Task ActiveFailedPublicationRecoveryIntentIsPersistedInDeferral(
        string intent)
    {
        SeedBaseline(("Solo_Guitar", 1307));
        SeedFailedPublication(8997, 1802);
        if (intent == "freeze")
        {
            Execute(
                """
                UPDATE scrape_publication_state
                SET public_reads_frozen = TRUE,
                    public_reads_frozen_at = now(),
                    public_reads_frozen_scrape_id = 1802,
                    public_reads_frozen_reason =
                        'failed publication recovery',
                    updated_at = now()
                WHERE id = TRUE
                """);
        }
        else
        {
            Execute(
                """
                UPDATE scrape_publication_state
                SET working_publication_id = 8997,
                    publication_commit_intent_started_at =
                        now(),
                    publication_commit_intent_heartbeat_at =
                        now(),
                    publication_commit_intent_owner =
                        'failed-publication-recovery',
                    updated_at = now()
                WHERE id = TRUE
                """);
        }

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var evidence = Scalar<string>(
            """
            SELECT evidence::TEXT
            FROM snapshot_generation_retention_deferrals
            ORDER BY deferral_id DESC
            LIMIT 1
            """);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Deferred,
            result.Disposition);
        Assert.Contains(
            "\"publicationFailure\"",
            evidence,
            StringComparison.Ordinal);
        Assert.Contains(
            intent == "freeze"
                ? "publication_freeze_reference"
                : "publication_commit_intent_reference",
            evidence,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-scrape")]
    [InlineData("missing-publication-failed-at")]
    [InlineData("completed-scrape")]
    public async Task MalformedFailedPublicationIdentityFailsClosed(
        string malformedState)
    {
        SeedBaseline(("Solo_Guitar", 1307));
        SeedFailedPublication(
            8997,
            1802,
            scrapeStatus:
                malformedState == "completed-scrape"
                    ? "completed"
                    : "failed",
            publicationFailedAt:
                malformedState !=
                    "missing-publication-failed-at");
        if (malformedState == "missing-scrape")
        {
            Execute(
                """
                UPDATE publication_generations
                SET scrape_id = NULL
                WHERE publication_id = 8997;

                DELETE FROM scrape_log
                WHERE id = 1802
                """);
        }

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var blocker = Assert.Single(
            JsonEntries(
                (await CycleAsync(result))
                    .GlobalBlockersJson),
            item => item.GetProperty("code").GetString()
                == "unpointed_failed_publication");

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.Contains(
            "failed_publication_identity_invalid",
            blocker.GetProperty("publicationFailure")
                .GetProperty("recoveryReasons")
                .EnumerateArray()
                .Select(static item => item.GetString()));
    }

    [Theory]
    [InlineData("background")]
    [InlineData("broadcast")]
    [InlineData("freeze")]
    [InlineData("commit-intent")]
    [InlineData("notification")]
    [InlineData("registration")]
    [InlineData("max-score")]
    public async Task IncompleteTerminalStatePersistsExplicitDeferral(
        string blocker)
    {
        SeedBaseline(("Solo_Guitar", 1307));
        var request = CreateRequest();
        switch (blocker)
        {
            case "background":
                request = CreateRequest(
                    backgroundQuiesced: false);
                break;
            case "broadcast":
                request = CreateRequest(
                    broadcastScrapeId: null);
                break;
            case "freeze":
                Execute(
                    """
                    UPDATE scrape_publication_state
                    SET public_reads_frozen = TRUE,
                        public_reads_frozen_at = now(),
                        public_reads_frozen_scrape_id =
                            published_scrape_id,
                        public_reads_frozen_reason =
                            'scrape',
                        updated_at = now()
                    WHERE id = TRUE
                    """);
                break;
            case "commit-intent":
                Execute(
                    """
                    UPDATE scrape_publication_state
                    SET publication_commit_intent_started_at =
                            now(),
                        publication_commit_intent_heartbeat_at =
                            now(),
                        publication_commit_intent_owner =
                            'test',
                        updated_at = now()
                    WHERE id = TRUE
                    """);
                break;
            case "notification":
                Execute(
                    """
                    UPDATE scrape_publication_state
                    SET improvement_notifications_scrape_id =
                            published_scrape_id,
                        improvement_notifications_status =
                            'pending',
                        improvement_notifications_projection_ready =
                            TRUE,
                        improvement_notifications_projection_scrape_id =
                            published_scrape_id,
                        updated_at = now()
                    WHERE id = TRUE
                    """);
                break;
            case "registration":
                Execute(
                    """
                    INSERT INTO registered_users (
                        device_id,
                        account_id,
                        registered_at)
                    VALUES (
                        'test-device',
                        'registration-pending',
                        now());

                    INSERT INTO backfill_status (
                        account_id,
                        status,
                        total_songs_to_check)
                    VALUES (
                        'registration-pending',
                        'pending',
                        1);
                    """);
                break;
            case "max-score":
                Execute(
                    """
                    UPDATE scrape_publication_state
                    SET max_score_mutation_gate_token =
                            'test-gate',
                        max_score_mutation_gate_publication_id =
                            current_publication_id,
                        max_score_mutation_gate_backend_pid =
                            pg_backend_pid(),
                        max_score_mutation_gate_backend_start =
                            (
                                SELECT backend_start
                                FROM pg_stat_activity
                                WHERE pid = pg_backend_pid()
                            ),
                        max_score_mutation_gate_acquired_at =
                            now(),
                        updated_at = now()
                    WHERE id = TRUE
                    """);
                break;
        }

        var result = await CreatePlanner().PlanAsync(request);
        var deferrals =
            await new SnapshotGenerationRetentionRepository(
                    _fixture.DataSource)
                .GetDeferralsAsync(CurrentPublicationId);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Deferred,
            result.Disposition);
        Assert.Single(deferrals);
        Assert.Equal(
            0,
            Scalar<long>(
                "SELECT COUNT(*) FROM snapshot_generation_retention_cycles"));
    }

    [Fact]
    public async Task RegistrationBackfillErrorPersistsTerminalCycleBlocker()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        Execute(
            """
            INSERT INTO registered_users (
                device_id,
                account_id,
                registered_at)
            VALUES (
                'registration-error-device',
                'registration-error',
                now());

            INSERT INTO backfill_status (
                account_id,
                status,
                total_songs_to_check,
                error_message)
            VALUES (
                'registration-error',
                'error',
                1,
                'injected terminal failure');
            """);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var observation = Assert.Single(
            await ObservationsAsync(result));
        var cycle =
            await new SnapshotGenerationRetentionRepository(
                    _fixture.DataSource)
                .GetCycleForSafePointAsync(
                    CurrentScrapeId,
                    CurrentPublicationId);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.False(result.Retryable);
        Assert.Contains(
            "registration_backfill_terminal_error",
            observation.BlockerCodes);
        Assert.NotNull(cycle);
        Assert.Contains(
            "found 1 backfill account",
            cycle!.GlobalBlockersJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "registration-error",
            cycle.GlobalBlockersJson,
            StringComparison.Ordinal);
        Assert.Empty(
            await new SnapshotGenerationRetentionRepository(
                    _fixture.DataSource)
                .GetDeferralsAsync(CurrentPublicationId));
    }

    [Fact]
    public async Task UnknownRegisteredBackfillStatePersistsTerminalCycleBlocker()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        Execute(
            """
            INSERT INTO registered_users (
                device_id,
                account_id,
                registered_at)
            VALUES (
                'registration-invalid-device',
                'registration-invalid',
                now());

            INSERT INTO backfill_status (
                account_id,
                status,
                total_songs_to_check)
            VALUES (
                'registration-invalid',
                'wedged',
                1);
            """);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var observation = Assert.Single(
            await ObservationsAsync(result));

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.Contains(
            "registration_backfill_state_invalid",
            observation.BlockerCodes);
    }

    [Fact]
    public async Task RetiredRegistrationErrorWithoutRegisteredUserIsNotDrainWork()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        Execute(
            """
            INSERT INTO backfill_status (
                account_id,
                status,
                total_songs_to_check,
                error_message)
            VALUES (
                'retired-registration',
                'error',
                1,
                'retired account evidence');
            """);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Observed,
            result.Disposition);
        Assert.False(result.Retryable);
    }

    [Fact]
    public async Task MissingRegistrationBackfillPersistsTerminalCycleBlocker()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        Execute(
            """
            INSERT INTO registered_users (
                device_id,
                account_id,
                registered_at)
            VALUES (
                'registration-missing-device',
                'registration-missing',
                now());
            """);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var observation = Assert.Single(
            await ObservationsAsync(result));

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.False(result.Retryable);
        Assert.Contains(
            "registration_backfill_state_missing",
            observation.BlockerCodes);
        Assert.Empty(
            await new SnapshotGenerationRetentionRepository(
                    _fixture.DataSource)
                .GetDeferralsAsync(CurrentPublicationId));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("deferred")]
    public async Task RunnableRegistrationBackfillRemainsRetryable(
        string status)
    {
        SeedBaseline(("Solo_Guitar", 1307));
        Execute(
            """
            INSERT INTO registered_users (
                device_id,
                account_id,
                registered_at)
            VALUES (
                'registration-runnable-device',
                'registration-runnable',
                now());

            INSERT INTO backfill_status (
                account_id,
                status,
                total_songs_to_check)
            VALUES (
                'registration-runnable',
                @status,
                1);
            """,
            command => command.Parameters.AddWithValue(
                "status",
                status));

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var deferral = Assert.Single(
            await new SnapshotGenerationRetentionRepository(
                    _fixture.DataSource)
                .GetDeferralsAsync(CurrentPublicationId));

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Deferred,
            result.Disposition);
        Assert.True(result.Retryable);
        Assert.Equal(
            "registration_drain_incomplete",
            deferral.Code);
        Assert.True(deferral.Retryable);
        Assert.Equal(
            0,
            Scalar<long>(
                "SELECT COUNT(*) FROM snapshot_generation_retention_cycles"));
    }

    [Fact]
    public async Task ExplicitBackfillRequeueRestoresRetryableDrainState()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        Execute(
            """
            INSERT INTO registered_users (
                device_id,
                account_id,
                registered_at)
            VALUES (
                'registration-requeue-device',
                'registration-requeue',
                now());

            INSERT INTO backfill_status (
                account_id,
                status,
                total_songs_to_check,
                error_message)
            VALUES (
                'registration-requeue',
                'error',
                1,
                'retry through supported admission');
            """);
        _fixture.Db.EnqueueBackfill(
            "registration-requeue",
            totalSongsToCheck: 1);

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var deferral = Assert.Single(
            await new SnapshotGenerationRetentionRepository(
                    _fixture.DataSource)
                .GetDeferralsAsync(CurrentPublicationId));

        Assert.Equal(
            "pending",
            _fixture.Db.GetBackfillStatus(
                "registration-requeue")?.Status);
        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Deferred,
            result.Disposition);
        Assert.True(result.Retryable);
        Assert.Equal(
            "registration_drain_incomplete",
            deferral.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("error")]
    public async Task RepairableHistoryStateRemainsRetryable(
        string? historyStatus)
    {
        SeedBaseline(("Solo_Guitar", 1307));
        Execute(
            """
            INSERT INTO registered_users (
                device_id,
                account_id,
                registered_at)
            VALUES (
                'history-repair-device',
                'history-repair',
                now());

            INSERT INTO backfill_status (
                account_id,
                status,
                total_songs_to_check,
                completed_at)
            VALUES (
                'history-repair',
                'complete',
                1,
                now());
            """);
        if (historyStatus is not null)
        {
            Execute(
                """
                INSERT INTO history_recon_status (
                    account_id,
                    status,
                    total_songs_to_process,
                    error_message)
                VALUES (
                    'history-repair',
                    @status,
                    1,
                    'repairable history state');
                """,
                command => command.Parameters.AddWithValue(
                    "status",
                    historyStatus));
        }

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var deferral = Assert.Single(
            await new SnapshotGenerationRetentionRepository(
                    _fixture.DataSource)
                .GetDeferralsAsync(CurrentPublicationId));

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Deferred,
            result.Disposition);
        Assert.True(result.Retryable);
        Assert.Equal(
            "registration_drain_incomplete",
            deferral.Code);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("disabled")]
    [InlineData("missing")]
    public async Task NonRunnableNotificationStatePersistsTerminalBlocker(
        string status)
    {
        SeedBaseline(("Solo_Guitar", 1307));
        switch (status)
        {
            case "completed":
                Execute(
                    """
                    UPDATE scrape_publication_state
                    SET improvement_notifications_scrape_id =
                            published_scrape_id,
                        improvement_notifications_status =
                            'completed',
                        improvement_notifications_completed_at =
                            NULL,
                        improvement_notifications_projection_ready =
                            TRUE,
                        improvement_notifications_projection_scrape_id =
                            published_scrape_id,
                        updated_at = now()
                    WHERE id = TRUE
                    """);
                break;
            case "disabled":
                Execute(
                    """
                    UPDATE scrape_publication_state
                    SET improvement_notifications_scrape_id =
                            NULL,
                        improvement_notifications_status =
                            'disabled',
                        improvement_notifications_completed_at =
                            now(),
                        improvement_notifications_projection_ready =
                            FALSE,
                        improvement_notifications_projection_scrape_id =
                            NULL,
                        updated_at = now()
                    WHERE id = TRUE
                    """);
                break;
            case "missing":
                Execute(
                    """
                    UPDATE scrape_publication_state
                    SET improvement_notifications_scrape_id =
                            NULL,
                        improvement_notifications_status =
                            NULL,
                        improvement_notifications_completed_at =
                            NULL,
                        improvement_notifications_projection_ready =
                            FALSE,
                        improvement_notifications_projection_scrape_id =
                            NULL,
                        updated_at = now()
                    WHERE id = TRUE
                    """);
                break;
        }

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var observation = Assert.Single(
            await ObservationsAsync(result));

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Blocked,
            result.Disposition);
        Assert.False(result.Retryable);
        Assert.Contains(
            "improvement_notifications_terminal_state_invalid",
            observation.BlockerCodes);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("running")]
    [InlineData("failed")]
    public async Task RecoverableNotificationStateRemainsRetryable(
        string status)
    {
        SeedBaseline(("Solo_Guitar", 1307));
        Execute(
            """
            UPDATE scrape_publication_state
            SET improvement_notifications_scrape_id =
                    published_scrape_id,
                improvement_notifications_status = @status,
                improvement_notifications_completed_at = NULL,
                improvement_notifications_projection_ready = TRUE,
                improvement_notifications_projection_scrape_id =
                    published_scrape_id,
                updated_at = now()
            WHERE id = TRUE
            """,
            command => command.Parameters.AddWithValue(
                "status",
                status));

        var result = await CreatePlanner().PlanAsync(
            CreateRequest());
        var deferral = Assert.Single(
            await new SnapshotGenerationRetentionRepository(
                    _fixture.DataSource)
                .GetDeferralsAsync(CurrentPublicationId));

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Deferred,
            result.Disposition);
        Assert.True(result.Retryable);
        Assert.Equal(
            "improvement_notifications_incomplete",
            deferral.Code);
    }

    [Fact]
    public async Task IndependentOracleMismatchPersistsExactSetsAndZeroCandidates()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        var oracle = new TransformingOracle(
            result => new SnapshotGenerationRetentionOracleResult(
                result.ChildKeys,
                result.ChildKeys,
                result.EffectivePublicationSourceValidations,
                result.EffectiveIndexTopologyValidations));

        var result = await CreatePlanner(oracle: oracle)
            .PlanAsync(CreateRequest());
        var cycle =
            await new SnapshotGenerationRetentionRepository(
                    _fixture.DataSource)
                .GetCycleForSafePointAsync(
                    CurrentScrapeId,
                    CurrentPublicationId);
        var observation = Assert.Single(
            await ObservationsAsync(result));

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.OracleMismatch,
            result.Disposition);
        Assert.NotNull(cycle);
        Assert.False(cycle!.OracleAgreement);
        Assert.Equal(0, cycle.CandidateCount);
        Assert.NotEqual(
            cycle.PlannerLiveSetJson,
            cycle.OracleLiveSetJson);
        Assert.Equal(
            SnapshotGenerationRetentionClassification.OracleMismatch,
            observation.Classification);
        Assert.Contains(
            "liveness_oracle_mismatch",
            observation.BlockerCodes);
    }

    [Fact]
    public async Task IndependentOracleSourceValidationMismatchFailsClosed()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        var oracle = new TransformingOracle(
            result => new SnapshotGenerationRetentionOracleResult(
                result.ChildKeys,
                result.LiveKeys,
                result.EffectivePublicationSourceValidations
                    .Select(static validation =>
                        validation with
                        {
                            ActualRowCount =
                                validation.ActualRowCount + 1,
                        })
                    .ToArray(),
                result.EffectiveIndexTopologyValidations));

        var result = await CreatePlanner(oracle: oracle)
            .PlanAsync(CreateRequest());
        var cycle =
            await new SnapshotGenerationRetentionRepository(
                    _fixture.DataSource)
                .GetCycleForSafePointAsync(
                    CurrentScrapeId,
                    CurrentPublicationId);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.OracleMismatch,
            result.Disposition);
        Assert.NotNull(cycle);
        Assert.False(cycle!.OracleAgreement);
        Assert.Contains(
            "independent SQL oracle disagreed",
            cycle.GlobalBlockersJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndependentOracleIndexTopologyMismatchFailsClosed()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        var oracle = new TransformingOracle(
            result => new SnapshotGenerationRetentionOracleResult(
                result.ChildKeys,
                result.LiveKeys,
                result.EffectivePublicationSourceValidations,
                result.EffectiveIndexTopologyValidations
                    .Select((validation, index) =>
                        index == 0
                            ? validation with
                            {
                                MissingRootIndexCount =
                                    validation
                                        .MissingRootIndexCount + 1,
                            }
                            : validation)
                    .ToArray()));

        var result = await CreatePlanner(oracle: oracle)
            .PlanAsync(CreateRequest());
        var cycle =
            await new SnapshotGenerationRetentionRepository(
                    _fixture.DataSource)
                .GetCycleForSafePointAsync(
                    CurrentScrapeId,
                    CurrentPublicationId);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.OracleMismatch,
            result.Disposition);
        Assert.NotNull(cycle);
        Assert.False(cycle!.OracleAgreement);
        Assert.Contains(
            "index-topology validation",
            cycle.GlobalBlockersJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndependentOracleNumericChildIndexMismatchFailsClosed()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        var oracle = new TransformingOracle(
            result => new SnapshotGenerationRetentionOracleResult(
                result.ChildKeys,
                result.LiveKeys,
                result.EffectivePublicationSourceValidations,
                result.EffectiveIndexTopologyValidations
                    .Select(validation =>
                        validation.Instrument ==
                            "Solo_Guitar"
                        ? validation with
                        {
                            NumericChildIndexValidations =
                                validation
                                    .EffectiveNumericChildIndexValidations
                                    .Select((numeric, index) =>
                                        index == 0
                                            ? numeric with
                                            {
                                                MissingParentIndexCount =
                                                    numeric
                                                        .MissingParentIndexCount
                                                    + 1,
                                            }
                                            : numeric)
                                    .ToArray(),
                        }
                        : validation)
                    .ToArray()));

        var result = await CreatePlanner(oracle: oracle)
            .PlanAsync(CreateRequest());
        var cycle =
            await new SnapshotGenerationRetentionRepository(
                    _fixture.DataSource)
                .GetCycleForSafePointAsync(
                    CurrentScrapeId,
                    CurrentPublicationId);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.OracleMismatch,
            result.Disposition);
        Assert.Equal(0, result.CandidateCount);
        Assert.NotNull(cycle);
        Assert.False(cycle!.OracleAgreement);
        Assert.Contains(
            "index-topology validation",
            cycle.GlobalBlockersJson,
            StringComparison.Ordinal);
        Assert.Contains(
            "missingParentIndexCount",
            cycle.GlobalBlockersJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SetComparisonRejectsPerturbationOnEitherSide()
    {
        var primaryPerturbed =
            SnapshotGenerationRetentionPlanner.CompareSets(
                ["a", "b"],
                ["a"],
                ["b"],
                ["a"],
                ["a"],
                []);
        var oraclePerturbed =
            SnapshotGenerationRetentionPlanner.CompareSets(
                ["a"],
                [],
                ["a"],
                ["a", "b"],
                ["b"],
                ["a"]);

        Assert.False(primaryPerturbed.Agrees);
        Assert.Equal(
            ["b"],
            primaryPerturbed.PlannerOnlyChildren);
        Assert.False(oraclePerturbed.Agrees);
        Assert.Equal(
            ["b"],
            oraclePerturbed.OracleOnlyChildren);
        Assert.Equal(
            ["b"],
            oraclePerturbed.OracleOnlyLive);
    }

    [Fact]
    public async Task PlannerUsesCentralizedLockAfterRegistrationLock()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        Assert.Equal(
            [
                RegistrationMutationGate.AdvisoryLockKey,
                ServiceMaintenanceLock.AdvisoryLockKey,
                PublicationGenerationSchema.AdvisoryLockKey,
                SnapshotGenerationRetentionContract
                    .PlannerAdvisoryLockKey,
            ],
            SnapshotGenerationRetentionLockOrder.OrderedKeys);

        await using var lockConnection =
            await _fixture.DataSource.OpenConnectionAsync();
        await using var maintenanceLease =
            await new ServiceMaintenanceLock().TryAcquireAsync(
                lockConnection,
                TimeSpan.Zero)
            ?? throw new InvalidOperationException(
                "Test could not acquire centralized maintenance lock.");

        var result = await CreatePlanner(
                lockWaitMilliseconds: 25)
            .PlanAsync(CreateRequest());
        var deferral = Assert.Single(
            await new SnapshotGenerationRetentionRepository(
                    _fixture.DataSource)
                .GetDeferralsAsync(CurrentPublicationId));

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Deferred,
            result.Disposition);
        Assert.Equal(
            "service_maintenance_lock_busy",
            deferral.Code);
        Assert.True(deferral.Retryable);
    }

    [Fact]
    public async Task RegistrationContentionDefersBeforeMaintenance()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        await using var lockConnection =
            await _fixture.DataSource.OpenConnectionAsync();
        await using var registrationLease =
            await PostgresSessionAdvisoryLock.TryAcquireAsync(
                lockConnection,
                RegistrationMutationGate.AdvisoryLockKey,
                shared: true,
                TimeSpan.Zero,
                CancellationToken.None)
            ?? throw new InvalidOperationException(
                "Test could not acquire registration mutation lock.");

        var result = await CreatePlanner(
                lockWaitMilliseconds: 25)
            .PlanAsync(CreateRequest());
        var deferral = Assert.Single(
            await new SnapshotGenerationRetentionRepository(
                    _fixture.DataSource)
                .GetDeferralsAsync(CurrentPublicationId));

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Deferred,
            result.Disposition);
        Assert.Equal(
            "registration_mutation_lock_busy",
            deferral.Code);
    }

    [Fact]
    public async Task ObservationUsesOneRepeatableReadReadOnlyTransaction()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        var oracle = new TransactionRecordingOracle();

        var result = await CreatePlanner(oracle: oracle)
            .PlanAsync(CreateRequest());

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Observed,
            result.Disposition);
        Assert.Equal("repeatable read", oracle.IsolationLevel);
        Assert.Equal("on", oracle.ReadOnly);
        Assert.Equal("15s", oracle.StatementTimeout);
        Assert.Equal("500ms", oracle.LockTimeout);
        Assert.Equal("20s", oracle.IdleTimeout);
        Assert.False(string.IsNullOrWhiteSpace(
            oracle.SnapshotIdentity));
        Assert.Equal(1, oracle.InvocationCount);
    }

    [Fact]
    public async Task RequestedCancellationPropagatesFromOracle()
    {
        SeedBaseline(("Solo_Guitar", 1307));
        using var cancellation =
            new CancellationTokenSource(
                TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreatePlanner(
                    oracle: new BlockingOracle())
                .PlanAsync(
                    CreateRequest(),
                    cancellation.Token));
        Assert.Equal(
            0,
            Scalar<long>(
                "SELECT COUNT(*) FROM snapshot_generation_retention_cycles"));
    }

    [Fact]
    public async Task InvalidObservationStatePersistsFailureAndNeverSucceedsSilently()
    {
        SeedBaseline(("Solo_Guitar", 1307));

        var result = await CreatePlanner(
                oracle: new ThrowingOracle())
            .PlanAsync(CreateRequest());
        var cycle =
            await new SnapshotGenerationRetentionRepository(
                    _fixture.DataSource)
                .GetCycleForSafePointAsync(
                    CurrentScrapeId,
                    CurrentPublicationId);

        Assert.Equal(
            SnapshotGenerationRetentionPlanDisposition.Failed,
            result.Disposition);
        Assert.NotNull(cycle);
        Assert.Equal(
            SnapshotGenerationRetentionCycleStatus.Failed,
            cycle!.Status);
        Assert.Contains(
            "injected oracle failure",
            cycle.ErrorMessage,
            StringComparison.Ordinal);
        Assert.Equal(0, cycle.CandidateCount);
    }

    [Fact]
    public void StableCandidateHashExcludesMetricsAndIsCanonical()
    {
        var child = CreateHashChild(
            "leaderboard_entries_snapshot_solo_guitar_s1307",
            1307,
            oid: 100,
            relfilenode: 200,
            rowEstimate: 10,
            totalBytes: 20);
        var metricMutation = child with
        {
            RowEstimate = 999,
            TotalBytes = 888,
        };
        var second = CreateHashChild(
            "leaderboard_entries_snapshot_solo_guitar_s1306",
            1306,
            oid: 101,
            relfilenode: 201,
            rowEstimate: 30,
            totalBytes: 40);
        var firstEvaluations = new[]
        {
            Candidate(child),
            Candidate(second),
        };
        var reversedMetricMutation = new[]
        {
            Candidate(second),
            Candidate(metricMutation),
        };

        var firstIdentity =
            SnapshotGenerationRetentionPlanner
                .ComputeCandidateIdentityHash(
                    firstEvaluations);
        var secondIdentity =
            SnapshotGenerationRetentionPlanner
                .ComputeCandidateIdentityHash(
                    reversedMetricMutation);
        var comparison =
            SnapshotGenerationRetentionPlanner.CompareSets(
                [],
                [],
                [],
                [],
                [],
                []);
        var firstObservation =
            SnapshotGenerationRetentionPlanner
                .ComputeObservationHash(
                    CreateRequest(),
                    firstEvaluations,
                    [],
                    comparison);
        var secondObservation =
            SnapshotGenerationRetentionPlanner
                .ComputeObservationHash(
                    CreateRequest(),
                    reversedMetricMutation,
                    [],
                    comparison);
        var firstAnomalyOrder =
            new[]
            {
                new SnapshotGenerationRetentionAnomaly(
                    "unpointed_retained_publication",
                    "publication 2",
                    PublicationId: 2,
                    ScrapeId: 12,
                    PublicationStatus:
                        PublicationGenerationStatus.Retained),
                new SnapshotGenerationRetentionAnomaly(
                    "unpointed_retained_publication",
                    "publication 1",
                    PublicationId: 1,
                    ScrapeId: 11,
                    PublicationStatus:
                        PublicationGenerationStatus.Retained),
            };
        var anomalyObservation =
            SnapshotGenerationRetentionPlanner
                .ComputeObservationHash(
                    CreateRequest(),
                    firstEvaluations,
                    [],
                    comparison,
                    firstAnomalyOrder);
        var reversedAnomalyObservation =
            SnapshotGenerationRetentionPlanner
                .ComputeObservationHash(
                    CreateRequest(),
                    firstEvaluations,
                    [],
                    comparison,
                    firstAnomalyOrder.Reverse());
        var failureEvidence =
            CreatePublicationFailureEvidence(
                publishedSourceRowCount: 0,
                unreplayedWriterFailureCount: 0);
        var failureCountMutation = failureEvidence with
        {
            PublishedSourceRowCount = 6_273,
            UnreplayedWriterFailureCount = 1,
        };
        var failedPublicationObservation =
            SnapshotGenerationRetentionPlanner
                .ComputeObservationHash(
                    CreateRequest(),
                    firstEvaluations,
                    [],
                    comparison,
                    [
                        new SnapshotGenerationRetentionAnomaly(
                            "unpointed_terminal_failed_publication",
                            "terminal failed publication",
                            PublicationId: 17,
                            ScrapeId: 10_017,
                            PublicationStatus:
                                PublicationGenerationStatus.Failed,
                            PublicationFailure:
                                failureEvidence),
                    ]);
        var failedPublicationCountMutation =
            SnapshotGenerationRetentionPlanner
                .ComputeObservationHash(
                    CreateRequest(),
                    firstEvaluations,
                    [],
                    comparison,
                    [
                        new SnapshotGenerationRetentionAnomaly(
                            "unpointed_terminal_failed_publication",
                            "terminal failed publication",
                            PublicationId: 17,
                            ScrapeId: 10_017,
                            PublicationStatus:
                                PublicationGenerationStatus.Failed,
                            PublicationFailure:
                                failureCountMutation),
                    ]);
        var failedPublicationBlocker =
            SnapshotGenerationRetentionPlanner
                .ComputeObservationHash(
                    CreateRequest(),
                    firstEvaluations,
                    [
                        new SnapshotGenerationRetentionBlocker(
                            "unpointed_failed_publication",
                            "recoverable failed publication",
                            failureEvidence),
                    ],
                    comparison);
        var failedPublicationBlockerCountMutation =
            SnapshotGenerationRetentionPlanner
                .ComputeObservationHash(
                    CreateRequest(),
                    firstEvaluations,
                    [
                        new SnapshotGenerationRetentionBlocker(
                            "unpointed_failed_publication",
                            "recoverable failed publication",
                            failureCountMutation),
                    ],
                    comparison);

        Assert.Equal(firstIdentity, secondIdentity);
        Assert.NotEqual(
            firstObservation,
            secondObservation);
        Assert.NotEqual(
            firstObservation,
            anomalyObservation);
        Assert.Equal(
            anomalyObservation,
            reversedAnomalyObservation);
        Assert.NotEqual(
            failedPublicationObservation,
            failedPublicationCountMutation);
        Assert.NotEqual(
            failedPublicationBlocker,
            failedPublicationBlockerCountMutation);
        Assert.Equal(
            child.StableChildIdentityHash,
            metricMutation.StableChildIdentityHash);
        Assert.Equal(
            child.StableConfigSchemaHash,
            metricMutation.StableConfigSchemaHash);
        Assert.NotEqual(
            child.ObservationMetricsHash,
            metricMutation.ObservationMetricsHash);
        Assert.NotEqual(
            child.StableChildIdentityHash,
            (child with
            {
                ChildRelfilenode =
                    child.ChildRelfilenode + 1,
            }).StableChildIdentityHash);
        Assert.NotEqual(
            child.StableChildIdentityHash,
            (child with
            {
                RootOid = child.RootOid + 1,
            }).StableChildIdentityHash);
    }

    private async Task<IReadOnlyList<
        SnapshotGenerationRetentionObservation>>
        ObservationsAsync(
            SnapshotGenerationRetentionPlanResult result)
    {
        Assert.NotNull(result.CycleId);
        return await new SnapshotGenerationRetentionRepository(
                _fixture.DataSource)
            .GetObservationsAsync(result.CycleId!.Value);
    }

    private async Task<SnapshotGenerationRetentionCycle>
        CycleAsync(
            SnapshotGenerationRetentionPlanResult result)
    {
        Assert.NotNull(result.CycleId);
        return await new SnapshotGenerationRetentionRepository(
                _fixture.DataSource)
            .GetCycleForSafePointAsync(
                CurrentScrapeId,
                CurrentPublicationId)
            ?? throw new InvalidOperationException(
                "Expected a persisted retention cycle.");
    }

    private static IReadOnlyList<JsonElement> JsonEntries(
        string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .EnumerateArray()
            .Select(static element => element.Clone())
            .ToArray();
    }

    private static SnapshotGenerationRetentionObservation
        ByInstrument(
            IReadOnlyList<
                SnapshotGenerationRetentionObservation>
                observations,
            string instrument) =>
        Assert.Single(
                    observations,
                    observation =>
                        observation.Instrument == instrument);

    private static SnapshotGenerationRetentionObservation
        ByInstrument(
            IReadOnlyList<
                SnapshotGenerationRetentionObservation>
                observations,
            string instrument,
            long snapshotId) =>
        Assert.Single(
            observations,
            observation =>
                observation.Instrument == instrument
                && observation.SnapshotId == snapshotId);

    private void SeedFailedPublication(
        long publicationId,
        long scrapeId,
        string scrapeStatus = "failed",
        bool publicationFailedAt = true)
    {
        Execute(
            """
            INSERT INTO scrape_log (
                id,
                started_at,
                completed_at,
                failed_at,
                status,
                failure_phase,
                failure_message)
            VALUES (
                @scrapeId,
                now() - interval '3 days',
                CASE
                    WHEN @scrapeStatus = 'completed'
                    THEN now() - interval '2 days'
                    ELSE NULL
                END,
                CASE
                    WHEN @scrapeStatus = 'failed'
                    THEN now() - interval '2 days'
                    ELSE NULL
                END,
                @scrapeStatus,
                CASE
                    WHEN @scrapeStatus = 'failed'
                    THEN 'publication'
                    ELSE NULL
                END,
                CASE
                    WHEN @scrapeStatus = 'failed'
                    THEN 'terminal failed publication'
                    ELSE NULL
                END);

            INSERT INTO publication_generations (
                publication_id,
                scrape_id,
                status,
                created_at,
                failed_at,
                failure_phase,
                failure_message)
            VALUES (
                @publicationId,
                @scrapeId,
                'failed',
                now() - interval '3 days',
                CASE
                    WHEN @publicationFailedAt
                    THEN now() - interval '2 days'
                    ELSE NULL
                END,
                'publication',
                'terminal failed publication')
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "publicationId",
                    publicationId);
                command.Parameters.AddWithValue(
                    "scrapeId",
                    scrapeId);
                command.Parameters.AddWithValue(
                    "scrapeStatus",
                    scrapeStatus);
                command.Parameters.AddWithValue(
                    "publicationFailedAt",
                    publicationFailedAt);
            });
    }

    private void AddFailedPublicationArtifact(
        long publicationId,
        long scrapeId,
        string artifact)
    {
        switch (artifact)
        {
            case "surface-ready":
            case "surface-building":
                Execute(
                    """
                    INSERT INTO publication_surface_bindings (
                        publication_id,
                        surface_name,
                        binding_kind,
                        binding_json,
                        row_count,
                        content_hash,
                        status,
                        built_at)
                    VALUES (
                        @publicationId,
                        'history',
                        'generation_history',
                        '{"table":"history"}'::jsonb,
                        1,
                        repeat('a', 64),
                        @status,
                        now())
                    """,
                    command =>
                    {
                        command.Parameters.AddWithValue(
                            "publicationId",
                            publicationId);
                        command.Parameters.AddWithValue(
                            "status",
                            artifact == "surface-ready"
                                ? "ready"
                                : "building");
                    });
                break;
            case "cache":
                Execute(
                    """
                    INSERT INTO publication_api_response_cache (
                        publication_id,
                        cache_key,
                        json_data,
                        etag)
                    VALUES (
                        @publicationId,
                        '/api/test',
                        decode('', 'hex'),
                        '"failed-publication-cache"')
                    """,
                    command => command.Parameters.AddWithValue(
                        "publicationId",
                        publicationId));
                break;
            case "cache-staging":
                Execute(
                    """
                    INSERT INTO
                        publication_api_response_cache_staging (
                            publication_id,
                            cache_key,
                            json_data,
                            etag)
                    VALUES (
                        @publicationId,
                        '/api/test',
                        decode('', 'hex'),
                        '"failed-publication-staging"')
                    """,
                    command => command.Parameters.AddWithValue(
                        "publicationId",
                        publicationId));
                break;
            case "catalog":
                Execute(
                    """
                    INSERT INTO publication_song_catalog (
                        publication_id,
                        catalog_json,
                        content_hash,
                        song_count,
                        source_captured_at)
                    VALUES (
                        @publicationId,
                        '{"songs":[]}'::jsonb,
                        repeat('b', 64),
                        0,
                        now())
                    """,
                    command => command.Parameters.AddWithValue(
                        "publicationId",
                        publicationId));
                break;
            case "path":
                Execute(
                    """
                    INSERT INTO publication_path_artifacts (
                        publication_id,
                        song_id)
                    VALUES (
                        @publicationId,
                        'failed-publication-path')
                    """,
                    command => command.Parameters.AddWithValue(
                        "publicationId",
                        publicationId));
                break;
            case "prepared-band":
                Execute(
                    """
                    CREATE TABLE btr_pubprep_8997_duets (
                        id BIGINT PRIMARY KEY)
                    """);
                break;
            case "retained-band":
                Execute(
                    """
                    CREATE TABLE btrs_retained_8997_duets (
                        id BIGINT PRIMARY KEY)
                    """);
                break;
            case "leaderboard-staging":
                Execute(
                    """
                    INSERT INTO leaderboard_staging (
                        scrape_id,
                        song_id,
                        instrument,
                        page_num,
                        account_id,
                        score)
                    VALUES (
                        @scrapeId,
                        'failed-staging',
                        'Solo_Guitar',
                        1,
                        'failed-account',
                        1)
                    """,
                    command => command.Parameters.AddWithValue(
                        "scrapeId",
                        checked((int)scrapeId)));
                break;
            case "staging-metadata":
                Execute(
                    """
                    INSERT INTO leaderboard_staging_meta (
                        scrape_id,
                        song_id,
                        instrument,
                        reported_pages,
                        pages_scraped,
                        entries_staged,
                        requests,
                        bytes_received)
                    VALUES (
                        @scrapeId,
                        'failed-staging-meta',
                        'Solo_Guitar',
                        1,
                        1,
                        1,
                        1,
                        1)
                    """,
                    command => command.Parameters.AddWithValue(
                        "scrapeId",
                        checked((int)scrapeId)));
                break;
            case "deep-scrape":
                Execute(
                    """
                    INSERT INTO deep_scrape_queue (
                        scrape_id,
                        song_id,
                        instrument,
                        valid_cutoff,
                        valid_entry_target,
                        wave2_start_page,
                        reported_pages,
                        initial_valid_count)
                    VALUES (
                        @scrapeId,
                        'failed-deep-scrape',
                        'Solo_Guitar',
                        1,
                        1,
                        1,
                        1,
                        1)
                    """,
                    command => command.Parameters.AddWithValue(
                        "scrapeId",
                        checked((int)scrapeId)));
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(artifact),
                    artifact,
                    "Unknown failed-publication artifact shape.");
        }
    }

    private void InsertSnapshotPublicationSource(
        long publishedScrapeId,
        string songId,
        string instrument,
        long sourceSnapshotId)
    {
        Execute(
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
                @publishedScrapeId,
                @songId,
                @instrument,
                'alltime',
                'snapshot',
                @sourceSnapshotId,
                @sourceSnapshotId,
                1,
                'content',
                'coverage',
                1,
                1,
                TRUE,
                now(),
                now())
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "publishedScrapeId",
                    publishedScrapeId);
                command.Parameters.AddWithValue(
                    "songId",
                    songId);
                command.Parameters.AddWithValue(
                    "instrument",
                    instrument);
                command.Parameters.AddWithValue(
                    "sourceSnapshotId",
                    sourceSnapshotId);
            });
        if (publishedScrapeId == CurrentScrapeId)
            RefreshCurrentPublicationSourceBinding();
    }

    private void SetCurrentPublicationSourceBinding(
        int expectedCount,
        string keyHash)
    {
        Execute(
            """
            UPDATE publication_generations
            SET metadata = metadata || jsonb_build_object(
                'publicationPreparation',
                jsonb_build_object(
                    'scrapeId', @scrapeId,
                    'publicationId', @publicationId,
                    'expectedPublishedScopeCount',
                        @expectedCount))
            WHERE publication_id = @publicationId;

            INSERT INTO publication_surface_bindings (
                publication_id,
                surface_name,
                binding_kind,
                binding_json,
                row_count,
                content_hash,
                status,
                built_at)
            VALUES (
                @publicationId,
                'solo_scope_sources',
                'scrape_id',
                jsonb_build_object(
                    'publicationId', @publicationId,
                    'table',
                        'leaderboard_published_scope_source',
                    'publishedScrapeId', @scrapeId,
                    'keyHashVersion', 1),
                @expectedCount,
                @keyHash,
                'ready',
                now())
            ON CONFLICT (publication_id, surface_name)
            DO UPDATE SET
                binding_kind = EXCLUDED.binding_kind,
                binding_json = EXCLUDED.binding_json,
                row_count = EXCLUDED.row_count,
                content_hash = EXCLUDED.content_hash,
                status = EXCLUDED.status,
                built_at = EXCLUDED.built_at
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "scrapeId",
                    CurrentScrapeId);
                command.Parameters.AddWithValue(
                    "publicationId",
                    CurrentPublicationId);
                command.Parameters.AddWithValue(
                    "expectedCount",
                    expectedCount);
                command.Parameters.AddWithValue(
                    "keyHash",
                    keyHash);
            });
    }

    private void RefreshCurrentPublicationSourceBinding()
    {
        using var connection =
            _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT instrument, song_id, scope_kind
            FROM leaderboard_published_scope_source
            WHERE published_scrape_id = @scrapeId
            ORDER BY instrument, song_id, scope_kind
            """;
        command.Parameters.AddWithValue(
            "scrapeId",
            CurrentScrapeId);
        using var reader = command.ExecuteReader();
        var keys = new List<PublishedScopeSourceKey>();
        while (reader.Read())
        {
            keys.Add(
                new PublishedScopeSourceKey(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2)));
        }

        SetCurrentPublicationSourceBinding(
            keys.Count,
            PublishedScopeSourceBindingContract
                .ComputeKeyHash(keys));
    }

    private void SetReadyPublicationSourceBinding(
        long scrapeId,
        long publicationId,
        string songId)
    {
        var key = new PublishedScopeSourceKey(
            "Solo_Guitar",
            songId,
            "alltime");
        Execute(
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
                @scrapeId,
                @songId,
                'Solo_Guitar',
                'alltime',
                'empty',
                NULL,
                @scrapeId,
                0,
                'empty-content',
                'empty-coverage',
                0,
                0,
                TRUE,
                now(),
                now());

            UPDATE publication_generations
            SET metadata = metadata || jsonb_build_object(
                'publicationPreparation',
                jsonb_build_object(
                    'scrapeId', @scrapeId,
                    'publicationId', @publicationId,
                    'expectedPublishedScopeCount', 1))
            WHERE publication_id = @publicationId;

            UPDATE publication_surface_bindings
            SET binding_kind = 'scrape_id',
                binding_json = jsonb_build_object(
                    'publicationId', @publicationId,
                    'table',
                        'leaderboard_published_scope_source',
                    'publishedScrapeId', @scrapeId,
                    'keyHashVersion', 1),
                row_count = 1,
                content_hash = @keyHash,
                status = 'ready',
                built_at = now()
            WHERE publication_id = @publicationId
              AND surface_name = 'solo_scope_sources'
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "scrapeId",
                    scrapeId);
                command.Parameters.AddWithValue(
                    "publicationId",
                    publicationId);
                command.Parameters.AddWithValue(
                    "songId",
                    songId);
                command.Parameters.AddWithValue(
                    "keyHash",
                    PublishedScopeSourceBindingContract
                        .ComputeKeyHash([key]));
            });
    }

    private void AddPreviousPublication(
        long publicationId,
        long scrapeId)
    {
        var key =
            new PublishedScopeSourceKey(
                "Solo_Guitar",
                "previous-empty",
                "alltime");
        var hash =
            PublishedScopeSourceBindingContract
                .ComputeKeyHash([key]);
        Execute(
            """
            INSERT INTO scrape_log (
                id,
                started_at,
                completed_at,
                status)
            VALUES (
                @scrapeId,
                now() - interval '2 days',
                now() - interval '1 day',
                'completed');

            INSERT INTO publication_generations (
                publication_id,
                scrape_id,
                status,
                previous_publication_id,
                created_at,
                ready_at,
                published_at,
                metadata)
            VALUES (
                @publicationId,
                @scrapeId,
                'retained',
                NULL,
                now() - interval '2 days',
                now() - interval '1 day',
                now() - interval '1 day',
                jsonb_build_object(
                    'publicationPreparation',
                    jsonb_build_object(
                        'scrapeId', @scrapeId,
                        'publicationId', @publicationId,
                        'expectedPublishedScopeCount', 1)));

            UPDATE publication_generations
            SET previous_publication_id = @publicationId
            WHERE publication_id = @currentPublicationId;

            UPDATE scrape_publication_state
            SET previous_publication_id = @publicationId,
                updated_at = now()
            WHERE id = TRUE;

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
                @scrapeId,
                'previous-empty',
                'Solo_Guitar',
                'alltime',
                'empty',
                NULL,
                @scrapeId,
                0,
                'previous-content',
                'previous-coverage',
                0,
                0,
                TRUE,
                now(),
                now());

            INSERT INTO publication_surface_bindings (
                publication_id,
                surface_name,
                binding_kind,
                binding_json,
                row_count,
                content_hash,
                status,
                built_at)
            VALUES (
                @publicationId,
                'solo_scope_sources',
                'scrape_id',
                jsonb_build_object(
                    'publicationId', @publicationId,
                    'table',
                        'leaderboard_published_scope_source',
                    'publishedScrapeId', @scrapeId,
                    'keyHashVersion', 1),
                1,
                @keyHash,
                'ready',
                now());
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "publicationId",
                    publicationId);
                command.Parameters.AddWithValue(
                    "scrapeId",
                    scrapeId);
                command.Parameters.AddWithValue(
                    "currentPublicationId",
                    CurrentPublicationId);
                command.Parameters.AddWithValue(
                    "keyHash",
                    hash);
            });
    }

    private static SnapshotGenerationRetentionChild
        CreateHashChild(
            string relationName,
            long snapshotId,
            long oid,
            long relfilenode,
            long rowEstimate,
            long totalBytes) =>
        new(
            SnapshotGenerationRetentionContract.Instruments[0],
            "public",
            50,
            60,
            "LIST (snapshot_id)",
            "FOR VALUES IN ('Solo_Guitar')",
            "pg_default",
            [],
            [
                new SnapshotGenerationRetentionIndex(
                    60,
                    61,
                    62,
                    "root_pkey",
                    "I",
                    IsValid: true,
                    IsReady: true,
                    IsPrimary: true,
                    IsUnique: true,
                    "btree",
                    "pg_default",
                    70,
                    "CREATE UNIQUE INDEX"),
            ],
            "public",
            relationName,
            snapshotId,
            oid,
            relfilenode,
            $"FOR VALUES IN ('{snapshotId}')",
            "pg_default",
            "r",
            "p",
            "heap",
            [],
            [
                new SnapshotGenerationRetentionIndex(
                    oid,
                    oid + 1_000,
                    relfilenode + 1_000,
                    $"{relationName}_pkey",
                    "i",
                    IsValid: true,
                    IsReady: true,
                    IsPrimary: true,
                    IsUnique: true,
                    "btree",
                    "pg_default",
                    oid + 2_000,
                    "CREATE UNIQUE INDEX"),
            ],
            rowEstimate,
            totalBytes,
            []);

    private static
        SnapshotGenerationRetentionPublicationFailureEvidence
        CreatePublicationFailureEvidence(
            long publishedSourceRowCount,
            long unreplayedWriterFailureCount) =>
        new(
            PublicationId: 17,
            ScrapeId: 10_017,
            PublicationStatus:
                PublicationGenerationStatus.Failed,
            PublicationFailedAtUtc: DateTime.UnixEpoch,
            PublicationFailurePhase: "publication",
            ScrapeStatus: "failed",
            ScrapeCompletedAtUtc: null,
            ScrapeFailedAtUtc: DateTime.UnixEpoch,
            TerminalFailureIdentityValid: true,
            NamedPointerSlots: [],
            ConfiguredResumeScrape: false,
            PublishedScrapeReference: false,
            PublicationFreezeReference: false,
            PublicationCommitIntentReference: false,
            MaxScoreMutationGateReference: false,
            NotificationStateReference: false,
            SurfaceBindingRowCount: 0,
            LiveSurfaceBindingRowCount: 0,
            BuildingSurfaceBindingRowCount: 0,
            ReadySurfaceBindingRowCount: 0,
            FailedSurfaceBindingRowCount: 0,
            RetiredSurfaceBindingRowCount: 0,
            InvalidSurfaceBindingRowCount: 0,
            PublishedSourceRowCount:
                publishedSourceRowCount,
            ApiResponseCacheRowCount: 0,
            ApiResponseCacheStagingRowCount: 0,
            SongCatalogRowCount: 0,
            PathArtifactRowCount: 0,
            PreparedBandRelationCount: 0,
            RetainedBandRelationCount: 0,
            LeaderboardStagingRowCount: 0,
            LeaderboardStagingMetadataRowCount: 0,
            DeepScrapeQueueRowCount: 0,
            UnreplayedWriterFailureCount:
                unreplayedWriterFailureCount,
            RecoveryReasons: []);

    private static SnapshotGenerationRetentionEvaluation
        Candidate(
            SnapshotGenerationRetentionChild child) =>
        new(
            child,
            PlannerLive: false,
            OracleLive: false,
            RootReasons: [],
            Blockers: [],
            SnapshotGenerationRetentionClassification.Candidate);

    private SnapshotGenerationRetentionPlanner CreatePlanner(
        bool enabled = true,
        long resumeScrapeId = 0,
        ISnapshotGenerationRetentionOracle? oracle = null,
        int lockWaitMilliseconds = 100) =>
        new(
            _fixture.DataSource,
            new SnapshotGenerationRetentionRepository(
                _fixture.DataSource),
            oracle ?? new SnapshotGenerationRetentionOracle(),
            new ServiceMaintenanceLock(),
            Options.Create(
                new DatabaseMaintenanceOptions
                {
                    SnapshotGenerationRetentionReportOnlyEnabled =
                        enabled,
                    SnapshotGenerationRetentionCommandTimeoutSeconds =
                        15,
                    ServiceMaintenanceLockWaitMilliseconds =
                        lockWaitMilliseconds,
                }),
            Options.Create(
                new ScraperOptions
                {
                    ResumeScrapeId = resumeScrapeId,
                }),
            NullLogger<
                SnapshotGenerationRetentionPlanner>.Instance);

    private static SnapshotGenerationRetentionPlanRequest
        CreateRequest(
            long? broadcastScrapeId = CurrentScrapeId,
            bool backgroundQuiesced = true) =>
        new(
            CurrentScrapeId,
            CurrentPublicationId,
            DateTime.UtcNow,
            broadcastScrapeId,
            backgroundQuiesced);

    private void SeedBaseline(
        params (string Instrument, long SnapshotId)[] children)
    {
        var scrapeIds = children
            .Select(static child => child.SnapshotId)
            .Append(CurrentScrapeId)
            .Distinct()
            .OrderBy(static id => id)
            .ToArray();
        foreach (var scrapeId in scrapeIds)
        {
            Execute(
                """
                INSERT INTO scrape_log (
                    id,
                    started_at,
                    completed_at,
                    status)
                VALUES (
                    @scrapeId,
                    now() - interval '10 days',
                    now() - interval '9 days',
                    'completed')
                """,
                command => command.Parameters.AddWithValue(
                    "scrapeId",
                    scrapeId));
        }

        Execute(
            """
            INSERT INTO publication_generations (
                publication_id,
                scrape_id,
                status,
                created_at,
                ready_at,
                published_at)
            VALUES (
                @publicationId,
                @scrapeId,
                'current',
                now(),
                now(),
                now());

            UPDATE scrape_publication_state
            SET published_scrape_id = @scrapeId,
                published_at = now(),
                public_reads_frozen = FALSE,
                public_reads_frozen_at = NULL,
                public_reads_frozen_scrape_id = NULL,
                public_reads_frozen_reason = NULL,
                publication_commit_intent_started_at = NULL,
                publication_commit_intent_heartbeat_at = NULL,
                publication_commit_intent_owner = NULL,
                current_publication_id = @publicationId,
                previous_publication_id = NULL,
                working_publication_id = NULL,
                max_score_mutation_gate_token = NULL,
                max_score_mutation_gate_publication_id = NULL,
                max_score_mutation_gate_backend_pid = NULL,
                max_score_mutation_gate_backend_start = NULL,
                max_score_mutation_gate_acquired_at = NULL,
                improvement_notifications_scrape_id = NULL,
                improvement_notifications_status = 'disabled',
                improvement_notifications_attempt_count = 0,
                improvement_notifications_started_at = NULL,
                improvement_notifications_completed_at = NULL,
                improvement_notifications_error = NULL,
                improvement_notifications_projection_scopes =
                    '[]'::jsonb,
                improvement_notifications_projection_ready = FALSE,
                improvement_notifications_projection_scrape_id = NULL,
                updated_at = now()
            WHERE id = TRUE;

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
                @scrapeId,
                'baseline-empty',
                'Solo_Guitar',
                'alltime',
                'empty',
                NULL,
                @scrapeId,
                0,
                'empty-content',
                'empty-coverage',
                0,
                0,
                TRUE,
                now(),
                now());
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "publicationId",
                    CurrentPublicationId);
                command.Parameters.AddWithValue(
                    "scrapeId",
                    CurrentScrapeId);
            });
        RefreshCurrentPublicationSourceBinding();

        foreach (var child in children)
        {
            Execute(
                """
                SELECT
                    ensure_leaderboard_snapshot_generation_partition(
                        @instrument,
                        @snapshotId)
                """,
                command =>
                {
                    command.Parameters.AddWithValue(
                        "instrument",
                        child.Instrument);
                    command.Parameters.AddWithValue(
                        "snapshotId",
                        child.SnapshotId);
                });
        }
    }

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

    private IReadOnlyList<(
        string? PreviousHash,
        string CurrentHash)> ReadEvidence(
            long cycleId)
    {
        using var connection =
            _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT previous_hash, current_hash
            FROM snapshot_generation_retention_evidence
            WHERE cycle_id = @cycleId
            ORDER BY sequence
            """;
        command.Parameters.AddWithValue("cycleId", cycleId);
        using var reader = command.ExecuteReader();
        var rows = new List<(string?, string)>();
        while (reader.Read())
        {
            rows.Add((
                reader.IsDBNull(0)
                    ? null
                    : reader.GetString(0),
                reader.GetString(1)));
        }
        return rows;
    }

    private sealed class TransformingOracle(
        Func<
            SnapshotGenerationRetentionOracleResult,
            SnapshotGenerationRetentionOracleResult> transform)
        : ISnapshotGenerationRetentionOracle
    {
        private readonly SnapshotGenerationRetentionOracle
            _inner = new();

        public async Task<
            SnapshotGenerationRetentionOracleResult> LoadAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long configuredResumeScrapeId,
            int commandTimeoutSeconds,
            CancellationToken ct = default) =>
            transform(
                await _inner.LoadAsync(
                    connection,
                    transaction,
                    configuredResumeScrapeId,
                    commandTimeoutSeconds,
                    ct));
    }

    private sealed class TransactionRecordingOracle
        : ISnapshotGenerationRetentionOracle
    {
        private readonly SnapshotGenerationRetentionOracle
            _inner = new();

        public string? IsolationLevel { get; private set; }
        public string? ReadOnly { get; private set; }
        public string? SnapshotIdentity { get; private set; }
        public string? StatementTimeout { get; private set; }
        public string? LockTimeout { get; private set; }
        public string? IdleTimeout { get; private set; }
        public int InvocationCount { get; private set; }

        public async Task<
            SnapshotGenerationRetentionOracleResult> LoadAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long configuredResumeScrapeId,
            int commandTimeoutSeconds,
            CancellationToken ct = default)
        {
            InvocationCount++;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandTimeout = commandTimeoutSeconds;
                command.CommandText = """
                    SELECT
                        current_setting(
                            'transaction_isolation'),
                        current_setting(
                            'transaction_read_only'),
                        txid_current_snapshot()::TEXT,
                        current_setting(
                            'statement_timeout'),
                        current_setting(
                            'lock_timeout'),
                        current_setting(
                            'idle_in_transaction_session_timeout')
                    """;
                await using var reader =
                    await command.ExecuteReaderAsync(ct);
                Assert.True(await reader.ReadAsync(ct));
                IsolationLevel = reader.GetString(0);
                ReadOnly = reader.GetString(1);
                SnapshotIdentity = reader.GetString(2);
                StatementTimeout = reader.GetString(3);
                LockTimeout = reader.GetString(4);
                IdleTimeout = reader.GetString(5);
            }

            return await _inner.LoadAsync(
                connection,
                transaction,
                configuredResumeScrapeId,
                commandTimeoutSeconds,
                ct);
        }
    }

    private sealed class BlockingOracle
        : ISnapshotGenerationRetentionOracle
    {
        public async Task<
            SnapshotGenerationRetentionOracleResult> LoadAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long configuredResumeScrapeId,
            int commandTimeoutSeconds,
            CancellationToken ct = default)
        {
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                ct);
            throw new InvalidOperationException(
                "Unreachable.");
        }
    }

    private sealed class ThrowingOracle
        : ISnapshotGenerationRetentionOracle
    {
        public Task<SnapshotGenerationRetentionOracleResult>
            LoadAsync(
                NpgsqlConnection connection,
                NpgsqlTransaction transaction,
                long configuredResumeScrapeId,
                int commandTimeoutSeconds,
                CancellationToken ct = default) =>
            throw new InvalidOperationException(
                "injected oracle failure");
    }
}
