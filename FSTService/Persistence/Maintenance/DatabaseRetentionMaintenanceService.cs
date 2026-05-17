using System.IO;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService.Persistence.Maintenance;

public interface IDatabaseRetentionMaintenanceService
{
    Task<DatabaseRetentionMaintenanceResult> RunAsync(CancellationToken ct = default);
}

public sealed class DatabaseRetentionMaintenanceService : IDatabaseRetentionMaintenanceService
{
    private const long ServiceRetentionMaintenanceAdvisoryLockKey = 2026050901;

    private readonly NpgsqlDataSource _dataSource;
    private readonly DatabaseMaintenanceDryRunReporter _reporter;
    private readonly IDatabasePressureMonitor _pressureMonitor;
    private readonly IOptions<DatabaseMaintenanceOptions> _options;
    private readonly ILogger<DatabaseRetentionMaintenanceService> _log;

    public DatabaseRetentionMaintenanceService(
        NpgsqlDataSource dataSource,
        DatabaseMaintenanceDryRunReporter reporter,
        IDatabasePressureMonitor pressureMonitor,
        IOptions<DatabaseMaintenanceOptions> options,
        ILogger<DatabaseRetentionMaintenanceService> log)
    {
        _dataSource = dataSource;
        _reporter = reporter;
        _pressureMonitor = pressureMonitor;
        _options = options;
        _log = log;
    }

    public async Task<DatabaseRetentionMaintenanceResult> RunAsync(CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        var options = _options.Value;
        if (!options.ServiceLevelRetentionMaintenanceEnabled)
        {
            return DatabaseRetentionMaintenanceResult.SkippedResult(
                startedAtUtc,
                "service-level retention maintenance is disabled");
        }

        if (options.SkipCleanupWhenPressureDetected)
        {
            var pressure = await _pressureMonitor.GetPressureSnapshotAsync(options, ct);
            if (pressure.IsUnderPressure)
            {
                return DatabaseRetentionMaintenanceResult.SkippedResult(
                    startedAtUtc,
                    $"database pressure detected: {string.Join("; ", pressure.Reasons)}");
            }
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        if (!await TryAcquireServiceMaintenanceLockAsync(conn, ct))
        {
            return DatabaseRetentionMaintenanceResult.SkippedResult(
                startedAtUtc,
                "another service-level retention maintenance run already holds the advisory lock");
        }

        try
        {
            var snapshotResult = await RunSnapshotRetentionAsync(options, ct);
            var metadataResult = await RunMetadataTtlCleanupAsync(conn, options, ct);
            var completedAtUtc = DateTime.UtcNow;
            return new DatabaseRetentionMaintenanceResult(
                startedAtUtc,
                completedAtUtc,
                Skipped: false,
                Reason: BuildResultReason(snapshotResult, metadataResult),
                snapshotResult,
                metadataResult);
        }
        finally
        {
            await ReleaseServiceMaintenanceLockAsync(conn, CancellationToken.None);
        }
    }

    private async Task<SnapshotRetentionMaintenanceResult> RunSnapshotRetentionAsync(
        DatabaseMaintenanceOptions options,
        CancellationToken ct)
    {
        if (!options.SnapshotRetentionRewriteEnabled && !options.SnapshotRetentionReportOnlyWhenDisabled)
        {
            return new SnapshotRetentionMaintenanceResult(
                Enabled: false,
                CandidateCount: 0,
                Candidates: [],
                RewriteResults: [],
                "snapshot retention rewrites are disabled and report-only planning is disabled");
        }

        var dryRunOptions = new DatabaseMaintenanceDryRunOptions(
            Math.Max(0, options.SnapshotRetentionRollbackCompletedSnapshotsToKeep));
        var allPlans = await _reporter.BuildSnapshotRetentionRewritePlansAsync(dryRunOptions, ct);
        var eligiblePlans = allPlans
            .Where(plan => IsEligibleSnapshotRewritePlan(plan, options))
            .ToArray();

        if (!options.SnapshotRetentionRewriteEnabled)
        {
            return new SnapshotRetentionMaintenanceResult(
                Enabled: false,
                eligiblePlans.Length,
                eligiblePlans,
                RewriteResults: [],
                eligiblePlans.Length == 0
                    ? "snapshot retention rewrite disabled; no eligible candidates were found"
                    : $"snapshot retention rewrite disabled; {eligiblePlans.Length:N0} eligible candidate partition(s) found");
        }

        if (eligiblePlans.Length == 0)
        {
            return new SnapshotRetentionMaintenanceResult(
                Enabled: true,
                CandidateCount: 0,
                Candidates: [],
                RewriteResults: [],
                "snapshot retention rewrite enabled; no eligible candidates were found");
        }

        var maxPartitions = Math.Max(1, options.SnapshotRetentionMaxPartitionsPerRun);
        var results = new List<SnapshotPartitionRewriteResult>(maxPartitions);
        foreach (var plan in eligiblePlans.Take(maxPartitions))
        {
            var freeSpace = CheckFreeSpaceGate(plan, options);
            if (!freeSpace.CanExecute)
            {
                results.Add(new SnapshotPartitionRewriteResult(
                    Executed: false,
                    plan,
                    Preflight: null,
                    freeSpace.Reason,
                    RetiredPartitionName: null,
                    ReplacementPartitionName: null,
                    DroppedRetiredPartition: false,
                    BeforeTotalBytes: plan.TotalBytes,
                    AfterTotalBytes: plan.TotalBytes,
                    ReclaimedBytes: 0,
                    ExecutedAtUtc: DateTime.UtcNow));
                continue;
            }

            results.Add(await _reporter.RewriteSnapshotPartitionAsync(plan.PartitionName, dryRunOptions, ct));
        }

        return new SnapshotRetentionMaintenanceResult(
            Enabled: true,
            eligiblePlans.Length,
            eligiblePlans,
            results,
            BuildSnapshotRetentionReason(eligiblePlans.Length, results));
    }

    private async Task<MetadataRetentionCleanupResult> RunMetadataTtlCleanupAsync(
        NpgsqlConnection conn,
        DatabaseMaintenanceOptions options,
        CancellationToken ct)
    {
        if (!options.MetadataTtlCleanupEnabled)
            return new MetadataRetentionCleanupResult(Enabled: false, TotalDeletedRows: 0, Items: [], "metadata TTL cleanup is disabled");

        var cutoffTimestamp = DateTime.UtcNow.AddDays(-Math.Max(1, options.MetadataRetentionDays));
        var cutoffDate = DateOnly.FromDateTime(cutoffTimestamp);
        var batchSize = PositiveOrDefault(options.MetadataCleanupBatchSize, DatabaseMaintenanceOptions.DefaultCleanupBatchSize);
        var maxBatches = PositiveOrDefault(options.MetadataCleanupMaxBatches, DatabaseMaintenanceOptions.DefaultCleanupMaxBatches);
        var commandTimeoutSeconds = Math.Max(0, options.CleanupCommandTimeoutSeconds);
        var completedScrapeLogRowsToKeep = Math.Max(0, options.CompletedScrapeLogRowsToKeep);

        var items = new List<MetadataRetentionCleanupItemResult>
        {
            await DeleteRankHistorySnapshotStatsAsync(conn, cutoffDate, batchSize, maxBatches, commandTimeoutSeconds, ct),
            await DeleteBandRankHistoryJobsAsync(conn, cutoffTimestamp, batchSize, maxBatches, commandTimeoutSeconds, ct),
            await DeleteImprovementDetectionRunsAsync(conn, cutoffTimestamp, batchSize, maxBatches, commandTimeoutSeconds, ct),
            await DeleteScrapePhaseTimingsAsync(conn, cutoffTimestamp, batchSize, maxBatches, commandTimeoutSeconds, completedScrapeLogRowsToKeep, ct),
            await DeleteScrapeLogRowsAsync(conn, cutoffTimestamp, batchSize, maxBatches, commandTimeoutSeconds, completedScrapeLogRowsToKeep, ct),
        };

        var totalDeleted = items.Sum(item => item.DeletedRows);
        return new MetadataRetentionCleanupResult(
            Enabled: true,
            totalDeleted,
            items,
            totalDeleted == 0
                ? "metadata TTL cleanup found no eligible rows"
                : $"metadata TTL cleanup deleted {totalDeleted:N0} row(s)");
    }

    private async Task<MetadataRetentionCleanupItemResult> DeleteRankHistorySnapshotStatsAsync(
        NpgsqlConnection conn,
        DateOnly cutoffDate,
        int batchSize,
        int maxBatches,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        const string tableName = "rank_history_snapshot_stats";
        if (!await TableExistsAsync(conn, tableName, ct))
            return MetadataRetentionCleanupItemResult.SkippedResult(tableName, "table does not exist");

        var deleted = await ExecuteBoundedDeleteAsync(conn, tableName, $"""
            WITH doomed AS (
                SELECT stats.ctid
                FROM {tableName} stats
                WHERE stats.snapshot_date < @cutoffDate
                  AND NOT EXISTS (
                      SELECT 1
                      FROM rank_history history
                      WHERE history.instrument = stats.instrument
                        AND history.snapshot_date = stats.snapshot_date
                  )
                ORDER BY stats.snapshot_date ASC, stats.instrument ASC
                LIMIT @batchSize
            )
            DELETE FROM {tableName} stats
            USING doomed
            WHERE stats.ctid = doomed.ctid
            """, batchSize, maxBatches, commandTimeoutSeconds, ct, cmd =>
        {
            cmd.Parameters.AddWithValue("cutoffDate", cutoffDate);
        });

        return MetadataRetentionCleanupItemResult.ExecutedResult(tableName, deleted, "deleted orphaned rank history snapshot stats older than the metadata retention window");
    }

    private async Task<MetadataRetentionCleanupItemResult> DeleteBandRankHistoryJobsAsync(
        NpgsqlConnection conn,
        DateTime cutoffTimestamp,
        int batchSize,
        int maxBatches,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        const string tableName = "band_rank_history_jobs";
        if (!await TableExistsAsync(conn, tableName, ct))
            return MetadataRetentionCleanupItemResult.SkippedResult(tableName, "table does not exist");

        var deleted = await ExecuteBoundedDeleteAsync(conn, tableName, $"""
            WITH doomed AS (
                SELECT job.ctid
                FROM {tableName} job
                WHERE job.updated_at < @cutoffTimestamp
                  AND job.status IN ('complete', 'failed', 'superseded')
                ORDER BY job.updated_at ASC, job.job_id ASC
                LIMIT @batchSize
            )
            DELETE FROM {tableName} job
            USING doomed
            WHERE job.ctid = doomed.ctid
            """, batchSize, maxBatches, commandTimeoutSeconds, ct, cmd =>
        {
            cmd.Parameters.AddWithValue("cutoffTimestamp", cutoffTimestamp);
        });

        return MetadataRetentionCleanupItemResult.ExecutedResult(tableName, deleted, "deleted terminal band rank history jobs older than the metadata retention window");
    }

    private async Task<MetadataRetentionCleanupItemResult> DeleteImprovementDetectionRunsAsync(
        NpgsqlConnection conn,
        DateTime cutoffTimestamp,
        int batchSize,
        int maxBatches,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        const string tableName = "improvement_detection_runs";
        if (!await TableExistsAsync(conn, tableName, ct))
            return MetadataRetentionCleanupItemResult.SkippedResult(tableName, "table does not exist");

        var deleted = await ExecuteBoundedDeleteAsync(conn, tableName, $"""
            WITH doomed AS (
                SELECT run.ctid
                FROM {tableName} run
                WHERE COALESCE(run.completed_at, run.started_at) < @cutoffTimestamp
                  AND run.status <> 'running'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM player_improvement_events event
                      WHERE event.run_id = run.run_id
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM band_improvement_events event
                      WHERE event.run_id = run.run_id
                  )
                ORDER BY COALESCE(run.completed_at, run.started_at) ASC, run.run_id ASC
                LIMIT @batchSize
            )
            DELETE FROM {tableName} run
            USING doomed
            WHERE run.ctid = doomed.ctid
            """, batchSize, maxBatches, commandTimeoutSeconds, ct, cmd =>
        {
            cmd.Parameters.AddWithValue("cutoffTimestamp", cutoffTimestamp);
        });

        return MetadataRetentionCleanupItemResult.ExecutedResult(tableName, deleted, "deleted unreferenced improvement detection runs older than the metadata retention window");
    }

    private async Task<MetadataRetentionCleanupItemResult> DeleteScrapePhaseTimingsAsync(
        NpgsqlConnection conn,
        DateTime cutoffTimestamp,
        int batchSize,
        int maxBatches,
        int commandTimeoutSeconds,
        int completedScrapeLogRowsToKeep,
        CancellationToken ct)
    {
        const string tableName = "scrape_phase_timings";
        if (!await TableExistsAsync(conn, tableName, ct))
            return MetadataRetentionCleanupItemResult.SkippedResult(tableName, "table does not exist");

        var deleted = await ExecuteBoundedDeleteAsync(conn, tableName, $"""
            WITH retained_completed AS (
                SELECT id
                FROM scrape_log
                WHERE completed_at IS NOT NULL
                ORDER BY id DESC
                LIMIT @completedScrapeLogRowsToKeep
            ), doomed AS (
                SELECT timing.ctid
                FROM {tableName} timing
                JOIN scrape_log log ON log.id = timing.scrape_id
                WHERE {BuildScrapeLogRetentionPredicate("log")}
                ORDER BY timing.scrape_id ASC, timing.started_at ASC
                LIMIT @batchSize
            )
            DELETE FROM {tableName} timing
            USING doomed
            WHERE timing.ctid = doomed.ctid
            """, batchSize, maxBatches, commandTimeoutSeconds, ct, cmd =>
        {
            cmd.Parameters.AddWithValue("cutoffTimestamp", cutoffTimestamp);
            cmd.Parameters.AddWithValue("completedScrapeLogRowsToKeep", completedScrapeLogRowsToKeep);
        });

        return MetadataRetentionCleanupItemResult.ExecutedResult(tableName, deleted, "deleted scrape phase timing rows for scrape logs eligible for metadata retention cleanup");
    }

    private async Task<MetadataRetentionCleanupItemResult> DeleteScrapeLogRowsAsync(
        NpgsqlConnection conn,
        DateTime cutoffTimestamp,
        int batchSize,
        int maxBatches,
        int commandTimeoutSeconds,
        int completedScrapeLogRowsToKeep,
        CancellationToken ct)
    {
        const string tableName = "scrape_log";
        if (!await TableExistsAsync(conn, tableName, ct))
            return MetadataRetentionCleanupItemResult.SkippedResult(tableName, "table does not exist");

        var deleted = await ExecuteBoundedDeleteAsync(conn, tableName, $"""
            WITH retained_completed AS (
                SELECT id
                FROM scrape_log
                WHERE completed_at IS NOT NULL
                ORDER BY id DESC
                LIMIT @completedScrapeLogRowsToKeep
            ), doomed AS (
                SELECT log.ctid
                FROM scrape_log log
                WHERE {BuildScrapeLogRetentionPredicate("log")}
                ORDER BY log.id ASC
                LIMIT @batchSize
            )
            DELETE FROM scrape_log log
            USING doomed
            WHERE log.ctid = doomed.ctid
            """, batchSize, maxBatches, commandTimeoutSeconds, ct, cmd =>
        {
            cmd.Parameters.AddWithValue("cutoffTimestamp", cutoffTimestamp);
            cmd.Parameters.AddWithValue("completedScrapeLogRowsToKeep", completedScrapeLogRowsToKeep);
        });

        return MetadataRetentionCleanupItemResult.ExecutedResult(tableName, deleted, "deleted scrape log rows only after confirming no active state, projection source, or snapshot rows still reference them");
    }

    private static string BuildScrapeLogRetentionPredicate(string alias) => $"""
        COALESCE({alias}.completed_at, {alias}.started_at) < @cutoffTimestamp
        AND NOT EXISTS (
            SELECT 1
            FROM retained_completed retained
            WHERE retained.id = {alias}.id
        )
        AND NOT EXISTS (
            SELECT 1
            FROM leaderboard_snapshot_state state
            WHERE state.active_snapshot_id = {alias}.id
        )
        AND NOT EXISTS (
            SELECT 1
            FROM solo_current_projection_scope scope
            WHERE scope.source_snapshot_id = {alias}.id
        )
        AND NOT EXISTS (
            SELECT 1
            FROM leaderboard_entries_snapshot snapshot
            WHERE snapshot.snapshot_id = {alias}.id
        )
        """;

    private async Task<long> ExecuteBoundedDeleteAsync(
        NpgsqlConnection conn,
        string tableName,
        string sql,
        int batchSize,
        int maxBatches,
        int commandTimeoutSeconds,
        CancellationToken ct,
        Action<NpgsqlCommand> addParameters)
    {
        var totalDeleted = 0L;
        for (var batch = 0; batch < maxBatches; batch++)
        {
            ct.ThrowIfCancellationRequested();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = commandTimeoutSeconds;
            cmd.Parameters.AddWithValue("batchSize", batchSize);
            addParameters(cmd);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            totalDeleted += deleted;

            if (deleted > 0)
            {
                _log.LogInformation(
                    "Metadata retention cleanup deleted {DeletedRows:N0} row(s) from {TableName} in batch {BatchNumber:N0}.",
                    deleted,
                    tableName,
                    batch + 1);
            }

            if (deleted < batchSize)
                break;
        }

        return totalDeleted;
    }

    private static bool IsEligibleSnapshotRewritePlan(SnapshotPartitionRewritePlan plan, DatabaseMaintenanceOptions options)
    {
        if (!plan.CanExecute)
            return false;
        if (options.SnapshotRetentionMinimumEstimatedPurgeBytes > 0 && plan.EstimatedPurgeBytes < options.SnapshotRetentionMinimumEstimatedPurgeBytes)
            return false;

        var estimatedRetainedBytes = Math.Max(0, plan.TotalBytes - plan.EstimatedPurgeBytes);
        if (options.SnapshotRetentionMaximumEstimatedRetainedBytes > 0 && estimatedRetainedBytes > options.SnapshotRetentionMaximumEstimatedRetainedBytes)
            return false;

        return true;
    }

    private static SnapshotRetentionFreeSpaceCheck CheckFreeSpaceGate(
        SnapshotPartitionRewritePlan plan,
        DatabaseMaintenanceOptions options)
    {
        if (options.SnapshotRetentionMinimumFreeBytes <= 0)
            return SnapshotRetentionFreeSpaceCheck.Pass;

        if (string.IsNullOrWhiteSpace(options.SnapshotRetentionFreeSpacePath))
        {
            return new SnapshotRetentionFreeSpaceCheck(
                CanExecute: false,
                "blocked: SnapshotRetentionMinimumFreeBytes is configured but SnapshotRetentionFreeSpacePath is empty");
        }

        try
        {
            var path = Path.GetFullPath(options.SnapshotRetentionFreeSpacePath);
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
                return new SnapshotRetentionFreeSpaceCheck(false, $"blocked: cannot resolve filesystem root for {path}");

            var drive = new DriveInfo(root);
            var estimatedRetainedBytes = Math.Max(0, plan.TotalBytes - plan.EstimatedPurgeBytes);
            var requiredBytes = checked(options.SnapshotRetentionMinimumFreeBytes + estimatedRetainedBytes);
            return drive.AvailableFreeSpace >= requiredBytes
                ? SnapshotRetentionFreeSpaceCheck.Pass
                : new SnapshotRetentionFreeSpaceCheck(
                    false,
                    $"blocked: available free bytes {drive.AvailableFreeSpace:N0} are below required bytes {requiredBytes:N0} for rewriting {plan.PartitionName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        {
            return new SnapshotRetentionFreeSpaceCheck(
                false,
                $"blocked: free-space check failed for {options.SnapshotRetentionFreeSpacePath}: {ex.Message}");
        }
    }

    private static string BuildSnapshotRetentionReason(
        int eligiblePlanCount,
        IReadOnlyList<SnapshotPartitionRewriteResult> results)
    {
        if (results.Count == 0)
            return eligiblePlanCount == 0
                ? "snapshot retention rewrite enabled; no eligible candidates were found"
                : $"snapshot retention rewrite enabled; {eligiblePlanCount:N0} eligible candidate(s), but max partitions per run was zero";

        var executed = results.Count(result => result.Executed);
        var blocked = results.Count - executed;
        return $"snapshot retention processed {results.Count:N0} partition(s): {executed:N0} executed, {blocked:N0} blocked";
    }

    private static string BuildResultReason(
        SnapshotRetentionMaintenanceResult snapshotResult,
        MetadataRetentionCleanupResult metadataResult) =>
        $"{snapshotResult.Reason}; {metadataResult.Reason}";

    private static int PositiveOrDefault(int value, int fallback) => value > 0 ? value : fallback;

    private static async Task<bool> TryAcquireServiceMaintenanceLockAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_try_advisory_lock(@lockKey)";
        cmd.Parameters.AddWithValue("lockKey", ServiceRetentionMaintenanceAdvisoryLockKey);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task ReleaseServiceMaintenanceLockAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_advisory_unlock(@lockKey)";
        cmd.Parameters.AddWithValue("lockKey", ServiceRetentionMaintenanceAdvisoryLockKey);
        await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection conn, string tableName, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT to_regclass(@tableName) IS NOT NULL";
        cmd.Parameters.AddWithValue("tableName", $"public.{tableName}");
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }

    private sealed record SnapshotRetentionFreeSpaceCheck(bool CanExecute, string Reason)
    {
        public static SnapshotRetentionFreeSpaceCheck Pass { get; } = new(true, "free-space gate passed");
    }
}

public sealed record DatabaseRetentionMaintenanceResult(
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    bool Skipped,
    string Reason,
    SnapshotRetentionMaintenanceResult SnapshotRetention,
    MetadataRetentionCleanupResult MetadataCleanup)
{
    public static DatabaseRetentionMaintenanceResult SkippedResult(DateTime startedAtUtc, string reason) =>
        new(
            startedAtUtc,
            DateTime.UtcNow,
            Skipped: true,
            reason,
            SnapshotRetentionMaintenanceResult.Skipped(reason),
            MetadataRetentionCleanupResult.Skipped(reason));
}

public sealed record SnapshotRetentionMaintenanceResult(
    bool Enabled,
    int CandidateCount,
    IReadOnlyList<SnapshotPartitionRewritePlan> Candidates,
    IReadOnlyList<SnapshotPartitionRewriteResult> RewriteResults,
    string Reason)
{
    public static SnapshotRetentionMaintenanceResult Skipped(string reason) =>
        new(Enabled: false, CandidateCount: 0, Candidates: [], RewriteResults: [], reason);
}

public sealed record MetadataRetentionCleanupResult(
    bool Enabled,
    long TotalDeletedRows,
    IReadOnlyList<MetadataRetentionCleanupItemResult> Items,
    string Reason)
{
    public static MetadataRetentionCleanupResult Skipped(string reason) =>
        new(Enabled: false, TotalDeletedRows: 0, Items: [], reason);
}

public sealed record MetadataRetentionCleanupItemResult(
    string Name,
    bool Executed,
    long DeletedRows,
    string Reason)
{
    public static MetadataRetentionCleanupItemResult SkippedResult(string name, string reason) =>
        new(name, Executed: false, DeletedRows: 0, reason);

    public static MetadataRetentionCleanupItemResult ExecutedResult(string name, long deletedRows, string reason) =>
        new(name, Executed: true, deletedRows, reason);
}
