using FSTService.Persistence.Maintenance;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class DatabaseRetentionMaintenanceServiceTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    private readonly IDatabasePressureMonitor _pressureMonitor = Substitute.For<IDatabasePressureMonitor>();

    public DatabaseRetentionMaintenanceServiceTests()
    {
        _pressureMonitor.GetPressureSnapshotAsync(Arg.Any<DatabaseMaintenanceOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DatabasePressureSnapshot.None));
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    [Fact]
    public async Task RunAsync_WithSnapshotRewriteDisabled_ReportsCandidateWithoutDeletingRows()
    {
        SeedSnapshotRetentionCandidate();
        var sut = CreateSut(new DatabaseMaintenanceOptions
        {
            SnapshotRetentionRewriteEnabled = false,
            SnapshotRetentionReportOnlyWhenDisabled = true,
            SnapshotRetentionMinimumEstimatedPurgeBytes = 0,
            MetadataTtlCleanupEnabled = false,
        });

        var result = await sut.RunAsync(CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.False(result.SnapshotRetention.Enabled);
        Assert.Equal(1, result.SnapshotRetention.CandidateCount);
        Assert.Contains(result.SnapshotRetention.Candidates, plan => plan.PartitionName == "leaderboard_entries_snapshot_solo_guitar");
        Assert.Equal(50, CountSnapshotRows("Solo_Guitar"));
        Assert.Equal(30, CountSnapshotRows("Solo_Guitar", 100));
    }

    [Fact]
    public async Task RunAsync_WithSnapshotRewriteEnabled_RewritesOnePartitionAndPurgesOldSnapshot()
    {
        SeedSnapshotRetentionCandidate();
        var sut = CreateSut(new DatabaseMaintenanceOptions
        {
            SnapshotRetentionRewriteEnabled = true,
            SnapshotRetentionReportOnlyWhenDisabled = true,
            SnapshotRetentionMinimumEstimatedPurgeBytes = 0,
            SnapshotRetentionMaxPartitionsPerRun = 1,
            MetadataTtlCleanupEnabled = false,
        });

        var result = await sut.RunAsync(CancellationToken.None);

        var rewrite = Assert.Single(result.SnapshotRetention.RewriteResults);
        Assert.True(rewrite.Executed, rewrite.Reason);
        Assert.Equal(0, CountSnapshotRows("Solo_Guitar", 100));
        Assert.Equal(10, CountSnapshotRows("Solo_Guitar", 101));
        Assert.Equal(10, CountSnapshotRows("Solo_Guitar", 102));
    }

    [Fact]
    public async Task RunAsync_WithMetadataTtlCleanup_PrunesOnlyUnpinnedMetadataRows()
    {
        SeedMetadataRetentionRows();
        var sut = CreateSut(new DatabaseMaintenanceOptions
        {
            SnapshotRetentionRewriteEnabled = false,
            SnapshotRetentionReportOnlyWhenDisabled = false,
            MetadataTtlCleanupEnabled = true,
            MetadataRetentionDays = 30,
            MetadataCleanupBatchSize = 100,
            MetadataCleanupMaxBatches = 2,
            CompletedScrapeLogRowsToKeep = 0,
        });

        var result = await sut.RunAsync(CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.True(result.MetadataCleanup.Enabled);
        Assert.True(result.MetadataCleanup.TotalDeletedRows >= 4);
        Assert.Equal(0, CountRows("rank_history_snapshot_stats", "instrument = 'Solo_Guitar' AND snapshot_date = DATE '2024-01-01'"));
        Assert.Equal(1, CountRows("rank_history_snapshot_stats", "instrument = 'Solo_Guitar' AND snapshot_date = CURRENT_DATE"));
        Assert.Equal(0, CountRows("band_rank_history_jobs", "job_id = 9001"));
        Assert.Equal(0, CountRows("improvement_detection_runs", "run_id = 9101"));
        Assert.Equal(0, CountRows("scrape_log", "id = 80"));
        Assert.Equal(1, CountRows("scrape_log", "id = 81"));
        Assert.Equal(0, CountRows("scrape_log", "id = 82"));
        Assert.Equal(1, CountRows("scrape_log", "id = 83"));
    }

    [Fact]
    public async Task RunAsync_WithDatabasePressure_SkipsRetentionMaintenance()
    {
        _pressureMonitor.GetPressureSnapshotAsync(Arg.Any<DatabaseMaintenanceOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DatabasePressureSnapshot(true, 1, 0, 0, 0, ["active vacuum count 1"])));
        var sut = CreateSut(new DatabaseMaintenanceOptions
        {
            SkipCleanupWhenPressureDetected = true,
            SnapshotRetentionRewriteEnabled = true,
            SnapshotRetentionMinimumEstimatedPurgeBytes = 0,
            MetadataTtlCleanupEnabled = true,
        });

        var result = await sut.RunAsync(CancellationToken.None);

        Assert.True(result.Skipped);
        Assert.Contains("database pressure", result.Reason);
        Assert.False(result.SnapshotRetention.Enabled);
        Assert.False(result.MetadataCleanup.Enabled);
    }

    private DatabaseRetentionMaintenanceService CreateSut(DatabaseMaintenanceOptions options)
    {
        return new DatabaseRetentionMaintenanceService(
            _fixture.DataSource,
            new DatabaseMaintenanceDryRunReporter(_fixture.DataSource),
            _pressureMonitor,
            Options.Create(options),
            NullLogger<DatabaseRetentionMaintenanceService>.Instance);
    }

    private void SeedSnapshotRetentionCandidate()
    {
        Execute("""
            INSERT INTO scrape_log (id, started_at, completed_at)
            VALUES
                (100, now() - INTERVAL '3 days', now() - INTERVAL '3 days'),
                (101, now() - INTERVAL '2 days', now() - INTERVAL '2 days'),
                (102, now() - INTERVAL '1 day', now() - INTERVAL '1 day');

            INSERT INTO leaderboard_snapshot_state (song_id, instrument, active_snapshot_id, scrape_id, is_finalized, updated_at)
            VALUES ('song-a', 'Solo_Guitar', 102, 102, true, now());

            INSERT INTO solo_current_projection_scope (song_id, instrument, projection_generation, row_count, source_snapshot_id, status, updated_at)
            VALUES ('song-a', 'Solo_Guitar', 1, 1, 102, 'ready', now());
            """);

        Execute("""
            INSERT INTO leaderboard_entries_snapshot (
                snapshot_id, song_id, instrument, account_id, score, accuracy,
                is_full_combo, stars, season, percentile, rank, source, difficulty,
                first_seen_at, last_updated_at)
            SELECT
                snapshot_id,
                'song-a',
                'Solo_Guitar',
                'account-' || snapshot_id || '-' || row_number,
                1000 + row_number,
                100,
                false,
                5,
                9,
                0,
                row_number,
                'test',
                3,
                now(),
                now()
            FROM (
                SELECT 100::BIGINT AS snapshot_id, generate_series(1, 30) AS row_number
                UNION ALL
                SELECT 101::BIGINT AS snapshot_id, generate_series(1, 10) AS row_number
                UNION ALL
                SELECT 102::BIGINT AS snapshot_id, generate_series(1, 10) AS row_number
            ) seeded;
            """);
        Execute("ANALYZE leaderboard_entries_snapshot_solo_guitar;");
    }

    private void SeedMetadataRetentionRows()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS band_rank_history_jobs (
                job_id                 BIGSERIAL PRIMARY KEY,
                scrape_id              BIGINT      NOT NULL,
                snapshot_date          DATE        NOT NULL,
                band_type              TEXT        NOT NULL,
                mode                   TEXT        NOT NULL,
                status                 TEXT        NOT NULL,
                updated_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
                UNIQUE (scrape_id, band_type, snapshot_date)
            );

            CREATE TABLE IF NOT EXISTS band_rank_history_job_chunks (
                job_id          BIGINT      NOT NULL REFERENCES band_rank_history_jobs(job_id) ON DELETE CASCADE,
                band_type       TEXT        NOT NULL,
                ranking_scope   TEXT        NOT NULL,
                combo_id        TEXT        NOT NULL DEFAULT '',
                status          TEXT        NOT NULL,
                updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (job_id, ranking_scope, combo_id)
            );

            INSERT INTO scrape_log (id, started_at, completed_at)
            VALUES
                (80, TIMESTAMPTZ '2024-01-01T00:00:00Z', TIMESTAMPTZ '2024-01-01T01:00:00Z'),
                (81, TIMESTAMPTZ '2024-01-02T00:00:00Z', TIMESTAMPTZ '2024-01-02T01:00:00Z'),
                (82, TIMESTAMPTZ '2024-01-03T00:00:00Z', NULL),
                (83, TIMESTAMPTZ '2024-01-04T00:00:00Z', NULL);

            INSERT INTO rank_history_snapshot_stats (instrument, snapshot_date, snapshot_taken_at, total_charted_songs, ranked_account_count)
            VALUES
                ('Solo_Guitar', DATE '2024-01-01', TIMESTAMPTZ '2024-01-01T00:00:00Z', 1, 1),
                ('Solo_Guitar', CURRENT_DATE, now(), 1, 1);

            INSERT INTO band_rank_history_jobs (job_id, scrape_id, snapshot_date, band_type, mode, status, updated_at)
            VALUES (9001, 80, DATE '2024-01-01', 'Band_Duets', 'test', 'complete', TIMESTAMPTZ '2024-01-01T00:00:00Z');

            INSERT INTO band_rank_history_job_chunks (job_id, band_type, ranking_scope, combo_id, status, updated_at)
            VALUES (9001, 'Band_Duets', 'overall', '', 'complete', TIMESTAMPTZ '2024-01-01T00:00:00Z');

            INSERT INTO improvement_detection_runs (run_id, started_at, completed_at, status)
            VALUES (9101, TIMESTAMPTZ '2024-01-01T00:00:00Z', TIMESTAMPTZ '2024-01-01T01:00:00Z', 'complete');
            """);

        InsertSnapshotRow(81, "song-pinned", "Solo_Guitar", "account-pinned", 1000);
        InsertSnapshotRow(83, "song-incomplete-pinned", "Solo_Guitar", "account-incomplete-pinned", 1000);
    }

    private void InsertSnapshotRow(long snapshotId, string songId, string instrument, string accountId, int score)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO leaderboard_entries_snapshot (
                snapshot_id, song_id, instrument, account_id, score, accuracy,
                is_full_combo, stars, season, percentile, rank, source, difficulty,
                first_seen_at, last_updated_at)
            VALUES (
                @snapshotId, @songId, @instrument, @accountId, @score, 100,
                false, 5, 9, 0, 1, 'test', 3,
                now(), now());
            """;
        cmd.Parameters.AddWithValue("snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("score", score);
        cmd.ExecuteNonQuery();
    }

    private int CountSnapshotRows(string instrument, long? snapshotId = null)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = snapshotId.HasValue
            ? """
                SELECT COUNT(*)
                FROM leaderboard_entries_snapshot
                WHERE instrument = @instrument
                  AND snapshot_id = @snapshotId
                """
            : """
                SELECT COUNT(*)
                FROM leaderboard_entries_snapshot
                WHERE instrument = @instrument
                """;
        cmd.Parameters.AddWithValue("instrument", instrument);
        if (snapshotId.HasValue)
            cmd.Parameters.AddWithValue("snapshotId", snapshotId.Value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int CountRows(string tableName, string predicate)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {tableName} WHERE {predicate}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void Execute(string sql)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
