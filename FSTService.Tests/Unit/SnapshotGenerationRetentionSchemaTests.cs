using FSTService.Persistence.Maintenance;
using FSTService.Tests.Helpers;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class SnapshotGenerationRetentionSchemaTests
    : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Schema_IsIdempotentConstrainedAndEvidenceIsAppendOnly()
    {
        await FSTService.Persistence.DatabaseInitializer
            .EnsureSchemaAsync(_fixture.DataSource);
        await FSTService.Persistence.DatabaseInitializer
            .EnsureSchemaAsync(_fixture.DataSource);

        Execute(
            """
            INSERT INTO scrape_log (
                id, started_at, completed_at, status)
            VALUES (901, now(), now(), 'completed');

            INSERT INTO publication_generations (
                publication_id,
                scrape_id,
                status,
                created_at,
                ready_at,
                published_at)
            VALUES (
                9901,
                901,
                'current',
                now(),
                now(),
                now());

            INSERT INTO snapshot_generation_retention_cycles (
                trigger_scrape_id,
                trigger_publication_id,
                safe_point_kind,
                safe_point_at,
                planner_version,
                config_version,
                report_only,
                plan_digest,
                status,
                completed_at)
            VALUES (
                901,
                9901,
                'post_publication',
                now(),
                1,
                1,
                TRUE,
                repeat('a', 64),
                'blocked',
                now());

            INSERT INTO snapshot_generation_retention_jobs (
                cycle_id,
                report_only,
                operation_kind,
                instrument,
                root_relation,
                child_relation,
                snapshot_id,
                child_oid,
                child_relfilenode,
                partition_bound,
                tablespace_name,
                row_estimate,
                total_bytes,
                status)
            VALUES (
                currval(
                    'snapshot_generation_retention_cycles_cycle_id_seq'),
                TRUE,
                'drop_whole_child',
                'Solo_Guitar',
                'leaderboard_entries_snapshot_solo_guitar',
                'leaderboard_entries_snapshot_solo_guitar_s1',
                1,
                1,
                1,
                'FOR VALUES IN (''1'')',
                'pg_default',
                0,
                0,
                'blocked');

            INSERT INTO snapshot_generation_retention_evidence (
                cycle_id,
                job_id,
                sequence,
                phase,
                kind,
                payload,
                previous_hash,
                current_hash)
            SELECT
                cycle_id,
                job_id,
                1,
                'test',
                'created',
                '{"ok":true}'::jsonb,
                NULL,
                repeat('b', 64)
            FROM snapshot_generation_retention_jobs
            ORDER BY job_id DESC
            LIMIT 1;
            """);

        var updateError = Assert.Throws<PostgresException>(
            () => Execute(
                """
                UPDATE snapshot_generation_retention_evidence
                SET payload = '{}'::jsonb
                """));
        Assert.Equal("55000", updateError.SqlState);

        var deleteError = Assert.Throws<PostgresException>(
            () => Execute(
                "DELETE FROM snapshot_generation_retention_evidence"));
        Assert.Equal("55000", deleteError.SqlState);

        var statusError = Assert.Throws<PostgresException>(
            () => Execute(
                """
                UPDATE snapshot_generation_retention_jobs
                SET status = 'unknown'
                """));
        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            statusError.SqlState);

        var reportOnlyExecutableError =
            Assert.Throws<PostgresException>(
            () => Execute(
                """
                UPDATE snapshot_generation_retention_jobs
                SET status = 'planned'
                WHERE report_only;
                """));
        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            reportOnlyExecutableError.SqlState);
        var reportOnlyCycleError =
            Assert.Throws<PostgresException>(
                () => Execute(
                    """
                    UPDATE snapshot_generation_retention_cycles
                    SET status = 'planned'
                    WHERE report_only;
                    """));
        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            reportOnlyCycleError.SqlState);

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
                    plan_digest,
                    status,
                    completed_at)
                VALUES (
                    902,
                    9902,
                    'post_publication',
                    now(),
                    1,
                    1,
                    FALSE,
                    repeat('c', 64),
                    'planned',
                    now());

                INSERT INTO snapshot_generation_retention_jobs (
                    cycle_id,
                    report_only,
                    operation_kind,
                    instrument,
                    root_relation,
                    child_relation,
                    snapshot_id,
                    child_oid,
                    child_relfilenode,
                    partition_bound,
                    tablespace_name,
                    row_estimate,
                    total_bytes,
                    lease_owner,
                    lease_token,
                    lease_acquired_at,
                    lease_expires_at,
                    status)
                VALUES (
                    currval(
                        'snapshot_generation_retention_cycles_cycle_id_seq'),
                    FALSE,
                    'drop_whole_child',
                    'Solo_Bass',
                    'leaderboard_entries_snapshot_solo_bass',
                    'leaderboard_entries_snapshot_solo_bass_s2',
                    2,
                    2,
                    2,
                    'FOR VALUES IN (''2'')',
                    'pg_default',
                    0,
                    0,
                    'second',
                    '22222222-2222-2222-2222-222222222222',
                    now(),
                    now() + interval '5 minutes',
                    'leased');
                """);

        var activeError = Assert.Throws<PostgresException>(
            () => Execute(
                """
                INSERT INTO snapshot_generation_retention_cycles (
                    trigger_scrape_id,
                    trigger_publication_id,
                    safe_point_kind,
                    safe_point_at,
                    planner_version,
                    config_version,
                    report_only,
                    plan_digest,
                    status,
                    completed_at)
                VALUES (
                    903,
                    9903,
                    'post_publication',
                    now(),
                    1,
                    1,
                    FALSE,
                    repeat('d', 64),
                    'planned',
                    now());

                INSERT INTO snapshot_generation_retention_jobs (
                    cycle_id,
                    report_only,
                    operation_kind,
                    instrument,
                    root_relation,
                    child_relation,
                    snapshot_id,
                    child_oid,
                    child_relfilenode,
                    partition_bound,
                    tablespace_name,
                    row_estimate,
                    total_bytes,
                    lease_owner,
                    lease_token,
                    lease_acquired_at,
                    lease_expires_at,
                    status)
                VALUES (
                    currval(
                        'snapshot_generation_retention_cycles_cycle_id_seq'),
                    FALSE,
                    'drop_whole_child',
                    'Solo_Bass',
                    'leaderboard_entries_snapshot_solo_bass',
                    'leaderboard_entries_snapshot_solo_bass_s3',
                    3,
                    3,
                    3,
                    'FOR VALUES IN (''3'')',
                    'pg_default',
                    0,
                    0,
                    'second',
                    '22222222-2222-2222-2222-222222222222',
                    now(),
                    now() + interval '5 minutes',
                    'leased');
                """));
        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            activeError.SqlState);

        var duplicateChildError =
            Assert.Throws<PostgresException>(
                () => Execute(
                    """
                    INSERT INTO snapshot_generation_retention_cycles (
                        trigger_scrape_id,
                        trigger_publication_id,
                        safe_point_kind,
                        safe_point_at,
                        planner_version,
                        config_version,
                        report_only,
                        plan_digest,
                        status,
                        completed_at)
                    VALUES (
                        904,
                        9904,
                        'post_publication',
                        now(),
                        1,
                        1,
                        FALSE,
                        repeat('e', 64),
                        'planned',
                        now());

                    INSERT INTO snapshot_generation_retention_jobs (
                        cycle_id,
                        report_only,
                        operation_kind,
                        instrument,
                        root_relation,
                        child_relation,
                        snapshot_id,
                        child_oid,
                        child_relfilenode,
                        partition_bound,
                        tablespace_name,
                        row_estimate,
                        total_bytes,
                        status)
                    VALUES (
                        currval(
                            'snapshot_generation_retention_cycles_cycle_id_seq'),
                        FALSE,
                        'drop_whole_child',
                        'Solo_Bass',
                        'leaderboard_entries_snapshot_solo_bass',
                        'leaderboard_entries_snapshot_solo_bass_s2',
                        2,
                        2,
                        2,
                        'FOR VALUES IN (''2'')',
                        'pg_default',
                        0,
                        0,
                        'planned');
                    """));
        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            duplicateChildError.SqlState);

        var executorIndex = Scalar<string>(
            """
            SELECT pg_get_indexdef(
                'ix_snapshot_generation_retention_jobs_executor'::regclass)
            """);
        Assert.Contains(
            "NOT report_only",
            executorIndex,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "observed",
            executorIndex,
            StringComparison.OrdinalIgnoreCase);
        var nonterminalChildIndex = Scalar<string>(
            """
            SELECT pg_get_indexdef(
                'ux_snapshot_generation_retention_nonterminal_child'::regclass)
            """);
        Assert.Contains(
            "instrument, child_oid, child_relfilenode",
            nonterminalChildIndex,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "safety_failed",
            nonterminalChildIndex,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublishedSourceMap_PrimaryKeyRejectsDuplicateLogicalKey()
    {
        Execute(
            """
            INSERT INTO scrape_log (
                id, started_at, completed_at, status)
            VALUES (902, now(), now(), 'completed');

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
                902,
                'song',
                'Solo_Guitar',
                'alltime',
                'snapshot',
                902,
                902,
                1,
                'content',
                'coverage',
                1,
                1,
                TRUE,
                now(),
                now());
            """);

        var duplicateError = Assert.Throws<PostgresException>(
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
                VALUES (
                    902,
                    'song',
                    'Solo_Guitar',
                    'alltime',
                    'snapshot',
                    902,
                    902,
                    1,
                    'content-2',
                    'coverage-2',
                    1,
                    1,
                    TRUE,
                    now(),
                    now());
                """));
        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            duplicateError.SqlState);
    }

    [Fact]
    public void SchemaPlan_OwnsControlPlaneAfterMainPublication()
    {
        var plan = FSTService.Persistence.DatabaseInitializer
            .GetSchemaInitializationPlan();
        var mainIndex = plan
            .Select((step, index) => (step, index))
            .Single(item => item.step.Name == "main-publication")
            .index;
        var retention = plan.Single(step =>
            step.Name ==
            "snapshot-generation-retention-control-plane");

        Assert.True(retention.UseShortTransaction);
        Assert.Equal("2s", retention.LockTimeout);
        Assert.Equal("15s", retention.StatementTimeout);
        Assert.True(
            plan.Select((step, index) => (step, index))
                .Single(item => item.step == retention)
                .index > mainIndex);
    }

    private void Execute(string sql)
    {
        using var connection =
            _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var connection =
            _fixture.DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(
            command.ExecuteScalar()!,
            typeof(T));
    }
}
