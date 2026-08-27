using FortniteFestival.Core.Persistence;
using FSTService.Persistence;
using FSTService.Persistence.Maintenance;
using FSTService.Tests.Helpers;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class SnapshotGenerationRetentionAdmissionTests
    : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ScrapeAllocation_AllowsObservedReportOnlyJobButBlocksActiveDestructiveJob()
    {
        var catalog = (await new FestivalPersistence(
                _fixture.DataSource)
            .GetExactSongCatalogTokenAsync())!;
        var publishedScrapeId =
            _fixture.Db.StartScrapeRun(catalog);
        _fixture.Db.CompleteScrapeRun(
            publishedScrapeId,
            0,
            0,
            0,
            0);
        _fixture.Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var publication =
            _fixture.Db.GetPublicationPointerState();

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
                @scrapeId,
                @publicationId,
                'post_publication',
                now(),
                1,
                1,
                TRUE,
                repeat('a', 64),
                'observed',
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
                'observed');
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "scrapeId",
                    publishedScrapeId);
                command.Parameters.AddWithValue(
                    "publicationId",
                    publication.CurrentPublicationId!.Value);
            });

        var nextScrapeId =
            _fixture.Db.StartScrapeRun(catalog);
        Assert.True(nextScrapeId > publishedScrapeId);
        var repository =
            new SnapshotGenerationRetentionRepository(
                _fixture.DataSource);
        Assert.False(
            await repository.HasActiveDestructiveStateAsync());

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
                @scrapeId,
                @publicationId + 1,
                'post_publication',
                now(),
                1,
                1,
                FALSE,
                repeat('b', 64),
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
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "scrapeId",
                    publishedScrapeId);
                command.Parameters.AddWithValue(
                    "publicationId",
                    publication.CurrentPublicationId!.Value);
            });

        var plannedNextScrapeId =
            _fixture.Db.StartScrapeRun(catalog);
        Assert.True(plannedNextScrapeId > nextScrapeId);
        Assert.False(
            await repository.HasActiveDestructiveStateAsync());

        Execute(
            """
            UPDATE snapshot_generation_retention_jobs
            SET status = 'leased',
                lease_owner = 'test',
                lease_token =
                    '11111111-1111-1111-1111-111111111111',
                lease_acquired_at = now(),
                lease_expires_at = now() + interval '5 minutes',
                updated_at = now()
            WHERE NOT report_only;
            """);

        var error = Assert.Throws<PublicationCommitBusyException>(
            () => _fixture.Db.StartScrapeRun(catalog));
        Assert.Contains(
            "snapshot-generation retention",
            error.Message,
            StringComparison.Ordinal);
        Assert.True(
            await repository.HasActiveDestructiveStateAsync());

        Execute(
            """
            UPDATE snapshot_generation_retention_jobs
            SET status = 'planned',
                lease_owner = NULL,
                lease_token = NULL,
                lease_acquired_at = NULL,
                lease_expires_at = NULL,
                updated_at = now()
            WHERE NOT report_only;

            UPDATE snapshot_generation_retention_cycles
            SET status = 'safety_failed',
                updated_at = now()
            WHERE NOT report_only;
            """);

        Assert.Throws<PublicationCommitBusyException>(
            () => _fixture.Db.StartScrapeRun(catalog));
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
}
