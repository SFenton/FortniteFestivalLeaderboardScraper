using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;

namespace FSTService.Persistence.Maintenance;

public sealed class DatabaseMaintenanceDryRunReporter
{
    private const long LegacyStagingCleanupMinIndexBytes = 1 * 1024 * 1024;
    private const long SnapshotMaintenanceAdvisoryLockKey = 2026050201;
    private const string SnapshotParentTable = "leaderboard_entries_snapshot";

    private static readonly IReadOnlyDictionary<string, string> SnapshotPartitionInstruments = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["leaderboard_entries_snapshot_solo_guitar"] = "Solo_Guitar",
        ["leaderboard_entries_snapshot_solo_bass"] = "Solo_Bass",
        ["leaderboard_entries_snapshot_solo_drums"] = "Solo_Drums",
        ["leaderboard_entries_snapshot_solo_vocals"] = "Solo_Vocals",
        ["leaderboard_entries_snapshot_pro_guitar"] = "Solo_PeripheralGuitar",
        ["leaderboard_entries_snapshot_pro_bass"] = "Solo_PeripheralBass",
        ["leaderboard_entries_snapshot_pro_vocals"] = "Solo_PeripheralVocals",
        ["leaderboard_entries_snapshot_pro_cymbals"] = "Solo_PeripheralCymbals",
        ["leaderboard_entries_snapshot_pro_drums"] = "Solo_PeripheralDrums",
    };

    private static readonly string[] DeprecatedParentIndexNames =
    [
        "ix_le_song_rank",
        "ix_le_account",
        "ix_be_song_rank",
        "ix_be_song_score",
        "ix_be_combo",
        "ix_bms_account",
        "ix_bm_song_type",
    ];

    private static readonly string[] WatchIndexNames =
    [
        "ix_bstr_team_best",
        "ix_bstr_team_worst",
        "leaderboard_staging_pkey",
        "ix_le_song_rank",
        "ix_le_account",
        "ix_le_account_song",
        "ix_le_song_score",
        "ix_le_song_source",
        "ix_le_band_members",
        "ix_be_song_rank",
        "ix_be_song_score",
        "ix_be_combo",
        "ix_bms_account",
        "ix_bm_song_type",
    ];

    private readonly NpgsqlDataSource _dataSource;

    public DatabaseMaintenanceDryRunReporter(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<DatabaseMaintenanceDryRunReport> BuildReportAsync(
        DatabaseMaintenanceDryRunOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new DatabaseMaintenanceDryRunOptions();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var capturedAtUtc = DateTime.UtcNow;
        var activeSnapshotIds = await LoadActiveSnapshotIdsAsync(conn, ct);
        var projectionSourceSnapshotIds = await LoadProjectionSourceSnapshotIdsAsync(conn, ct);
        var scrapes = await LoadScrapesAsync(conn, ct);
        var snapshotPartitions = await LoadSnapshotPartitionStatsAsync(conn, ct);
        var policy = SnapshotRetentionPolicy.Create(
            activeSnapshotIds,
            projectionSourceSnapshotIds,
            scrapes,
            options.RollbackCompletedSnapshotsToKeep);
        var observedSnapshotIds = snapshotPartitions
            .SelectMany(partition => partition.SnapshotEstimates.Select(estimate => estimate.SnapshotId))
            .Distinct()
            .OrderByDescending(id => id)
            .ToArray();
        var snapshotDecisions = policy.Classify(observedSnapshotIds);
        var decisionBySnapshot = snapshotDecisions.ToDictionary(decision => decision.SnapshotId);
        var partitionCandidates = BuildPartitionSnapshotCandidates(snapshotPartitions, decisionBySnapshot);
        var snapshotSummary = BuildSnapshotSummary(
            activeSnapshotIds,
            projectionSourceSnapshotIds,
            scrapes,
            snapshotDecisions,
            partitionCandidates,
            options.RollbackCompletedSnapshotsToKeep);
        var snapshotRewritePlans = BuildSnapshotRewritePlans(snapshotPartitions, snapshotDecisions, partitionCandidates);

        var legacyLive = await LoadLegacyLiveAsync(conn, ct);
        var legacyStaging = await LoadLegacyStagingAsync(conn, ct);
        var indexCandidates = await LoadIndexCandidatesAsync(conn, ct);

        return new DatabaseMaintenanceDryRunReport(
            capturedAtUtc,
            options,
            snapshotSummary,
            snapshotRewritePlans,
            legacyLive,
            legacyStaging,
            indexCandidates,
            "dry-run only; no cleanup SQL was executed");
    }

    public async Task<LegacyStagingCleanupResult> CleanupLegacyStagingAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var before = await LoadLegacyStagingAsync(conn, ct);
        if (!before.CleanupEligible)
        {
            return new LegacyStagingCleanupResult(
                Executed: false,
                before,
                After: null,
                before.Reason,
                Sql: null,
                ExecutedAtUtc: DateTime.UtcNow);
        }

        const string sql = "TRUNCATE TABLE leaderboard_staging";
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            var exactRows = await CountRowsIfTableExistsAsync(conn, "leaderboard_staging", ct, tx);
            var activeMetaRows = await CountActiveStagingMetaRowsAsync(conn, ct, tx);
            if (exactRows != 0 || activeMetaRows != 0)
            {
                await tx.RollbackAsync(ct);
                return new LegacyStagingCleanupResult(
                    Executed: false,
                    before,
                    After: null,
                    $"blocked during transactional preflight: rows={exactRows:N0}, active staging metadata={activeMetaRows:N0}",
                    Sql: null,
                    ExecutedAtUtc: DateTime.UtcNow);
            }

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }

        var after = await LoadLegacyStagingAsync(conn, ct);
        return new LegacyStagingCleanupResult(
            Executed: true,
            before,
            after,
            "executed TRUNCATE TABLE leaderboard_staging after guarded preflight",
            sql,
            DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<SnapshotPartitionRewritePlan>> BuildSnapshotRetentionRewritePlansAsync(
        DatabaseMaintenanceDryRunOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new DatabaseMaintenanceDryRunOptions();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var activeSnapshotIds = await LoadActiveSnapshotIdsAsync(conn, ct);
        var projectionSourceSnapshotIds = await LoadProjectionSourceSnapshotIdsAsync(conn, ct);
        var scrapes = await LoadScrapesAsync(conn, ct);
        var snapshotPartitions = await LoadSnapshotPartitionStatsAsync(conn, ct);
        var policy = SnapshotRetentionPolicy.Create(
            activeSnapshotIds,
            projectionSourceSnapshotIds,
            scrapes,
            options.RollbackCompletedSnapshotsToKeep);
        var observedSnapshotIds = snapshotPartitions
            .SelectMany(partition => partition.SnapshotEstimates.Select(estimate => estimate.SnapshotId))
            .Distinct()
            .OrderByDescending(id => id)
            .ToArray();
        var snapshotDecisions = policy.Classify(observedSnapshotIds);
        var decisionBySnapshot = snapshotDecisions.ToDictionary(decision => decision.SnapshotId);
        var partitionCandidates = BuildPartitionSnapshotCandidates(snapshotPartitions, decisionBySnapshot);

        return BuildSnapshotRewritePlans(snapshotPartitions, snapshotDecisions, partitionCandidates);
    }

    public async Task<SnapshotPartitionRewriteResult> RewriteSnapshotPartitionAsync(
        string partitionName,
        DatabaseMaintenanceDryRunOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new DatabaseMaintenanceDryRunOptions();
        var normalizedPartitionName = NormalizeSnapshotPartitionName(partitionName);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        if (!await TryAcquireSnapshotMaintenanceLockAsync(conn, ct))
        {
            var blockedPlan = await BuildSnapshotRewritePlanForExecutionAsync(conn, normalizedPartitionName, options, ct);
            return new SnapshotPartitionRewriteResult(
                Executed: false,
                blockedPlan,
                Preflight: null,
                "blocked: another snapshot maintenance operation already holds the advisory lock",
                RetiredPartitionName: null,
                ReplacementPartitionName: null,
                DroppedRetiredPartition: false,
                BeforeTotalBytes: blockedPlan.TotalBytes,
                AfterTotalBytes: blockedPlan.TotalBytes,
                ReclaimedBytes: 0,
                ExecutedAtUtc: DateTime.UtcNow);
        }

        string? replacementName = null;
        string? retiredName = null;
        try
        {
            var plan = await BuildSnapshotRewritePlanForExecutionAsync(conn, normalizedPartitionName, options, ct);
            var preflight = await BuildSnapshotRewritePreflightAsync(conn, plan, ct);
            if (!preflight.CanExecute)
            {
                return new SnapshotPartitionRewriteResult(
                    Executed: false,
                    plan,
                    preflight,
                    string.Join("; ", preflight.RefusalReasons),
                    RetiredPartitionName: null,
                    ReplacementPartitionName: null,
                    DroppedRetiredPartition: false,
                    BeforeTotalBytes: plan.TotalBytes,
                    AfterTotalBytes: plan.TotalBytes,
                    ReclaimedBytes: 0,
                    ExecutedAtUtc: DateTime.UtcNow);
            }

            var suffix = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..22];
            replacementName = $"les_rewrite_{suffix}";
            retiredName = $"les_retired_{suffix}";

            await CreateSnapshotReplacementPartitionAsync(conn, plan, preflight, replacementName, ct);
            var replacementRows = await CountRowsIfTableExistsAsync(conn, replacementName, ct);
            if (replacementRows != preflight.RetainedRows)
                throw new InvalidOperationException($"Replacement row count mismatch: expected {preflight.RetainedRows:N0}, found {replacementRows:N0}.");

            await DropTableIfExistsAsync(conn, retiredName, ct);
            await SwapSnapshotPartitionAsync(conn, plan, replacementName, retiredName, ct);
            await DropTableIfExistsAsync(conn, retiredName, ct);
            await RenameSnapshotPartitionIndexesAsync(conn, plan, replacementName, ct);

            var postSwapRows = await CountRowsIfTableExistsAsync(conn, plan.PartitionName, ct);
            var postSwapPurgeRows = await CountSnapshotRowsAsync(conn, plan.PartitionName, plan.PurgeSnapshotIds, ct);
            if (postSwapRows != preflight.RetainedRows)
                throw new InvalidOperationException($"Post-swap row count mismatch: expected {preflight.RetainedRows:N0}, found {postSwapRows:N0}.");
            if (postSwapPurgeRows != 0)
                throw new InvalidOperationException($"Post-swap purge rows remain: found {postSwapPurgeRows:N0} row(s) for purge snapshot ids.");

            var afterBytes = await GetRelationTotalBytesAsync(conn, plan.PartitionName, ct);

            return new SnapshotPartitionRewriteResult(
                Executed: true,
                plan,
                preflight,
                "executed partition-local snapshot retention rewrite and dropped retired partition",
                retiredName,
                replacementName,
                DroppedRetiredPartition: true,
                BeforeTotalBytes: plan.TotalBytes,
                AfterTotalBytes: afterBytes,
                ReclaimedBytes: Math.Max(0, plan.TotalBytes - afterBytes),
                ExecutedAtUtc: DateTime.UtcNow);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(replacementName))
                await DropTableIfExistsAsync(conn, replacementName, CancellationToken.None);
            await ReleaseSnapshotMaintenanceLockAsync(conn, CancellationToken.None);
        }
    }

    private static SnapshotDryRunSummary BuildSnapshotSummary(
        IReadOnlyList<long> activeSnapshotIds,
        IReadOnlyList<long> projectionSourceSnapshotIds,
        IReadOnlyList<ScrapeSummary> scrapes,
        IReadOnlyList<SnapshotRetentionDecision> snapshotDecisions,
        IReadOnlyList<PartitionSnapshotCandidate> partitionCandidates,
        int rollbackCompletedSnapshotsToKeep)
    {
        static long SumEstimatedBytes(IEnumerable<PartitionSnapshotCandidate> candidates, SnapshotCleanupAction action) =>
            candidates.Where(candidate => candidate.Action == action).Sum(candidate => candidate.EstimatedBytes);

        static long SumEstimatedRows(IEnumerable<PartitionSnapshotCandidate> candidates, SnapshotCleanupAction action) =>
            candidates.Where(candidate => candidate.Action == action).Sum(candidate => candidate.EstimatedRows);

        return new SnapshotDryRunSummary(
            rollbackCompletedSnapshotsToKeep,
            activeSnapshotIds,
            projectionSourceSnapshotIds,
            scrapes.OrderByDescending(scrape => scrape.Id).Take(20).ToArray(),
            snapshotDecisions,
            partitionCandidates,
            EstimatedKeepRows: SumEstimatedRows(partitionCandidates, SnapshotCleanupAction.Keep),
            EstimatedPurgeRows: SumEstimatedRows(partitionCandidates, SnapshotCleanupAction.PurgeCandidate),
            EstimatedBlockedRows: SumEstimatedRows(partitionCandidates, SnapshotCleanupAction.Blocked),
            EstimatedKeepBytes: SumEstimatedBytes(partitionCandidates, SnapshotCleanupAction.Keep),
            EstimatedPurgeBytes: SumEstimatedBytes(partitionCandidates, SnapshotCleanupAction.PurgeCandidate),
            EstimatedBlockedBytes: SumEstimatedBytes(partitionCandidates, SnapshotCleanupAction.Blocked));
    }

    private static IReadOnlyList<PartitionSnapshotCandidate> BuildPartitionSnapshotCandidates(
        IReadOnlyList<SnapshotPartitionStats> partitions,
        IReadOnlyDictionary<long, SnapshotRetentionDecision> decisionBySnapshot)
    {
        var candidates = new List<PartitionSnapshotCandidate>();
        foreach (var partition in partitions)
        {
            foreach (var estimate in partition.SnapshotEstimates)
            {
                if (!decisionBySnapshot.TryGetValue(estimate.SnapshotId, out var decision))
                    continue;

                candidates.Add(new PartitionSnapshotCandidate(
                    partition.PartitionName,
                    estimate.SnapshotId,
                    decision.Action,
                    decision.Reasons,
                    estimate.EstimatedRows,
                    estimate.EstimatedBytes,
                    estimate.EstimatedRowShare));
            }
        }

        return candidates
            .OrderBy(candidate => candidate.PartitionName, StringComparer.Ordinal)
            .ThenByDescending(candidate => candidate.SnapshotId)
            .ToArray();
    }

    private static IReadOnlyList<SnapshotPartitionRewritePlan> BuildSnapshotRewritePlans(
        IReadOnlyList<SnapshotPartitionStats> partitions,
        IReadOnlyList<SnapshotRetentionDecision> snapshotDecisions,
        IReadOnlyList<PartitionSnapshotCandidate> partitionCandidates)
    {
        var decisionsById = snapshotDecisions.ToDictionary(decision => decision.SnapshotId);
        return partitions
            .Select(partition => BuildSnapshotRewritePlan(partition, decisionsById, partitionCandidates))
            .OrderBy(plan => plan.TotalBytes)
            .ThenBy(plan => plan.PartitionName, StringComparer.Ordinal)
            .ToArray();
    }

    private static SnapshotPartitionRewritePlan BuildSnapshotRewritePlan(
        SnapshotPartitionStats partition,
        IReadOnlyDictionary<long, SnapshotRetentionDecision> decisionsById,
        IReadOnlyList<PartitionSnapshotCandidate> partitionCandidates)
    {
        SnapshotPartitionInstruments.TryGetValue(partition.PartitionName, out var instrument);
        instrument ??= string.Empty;

        var observedIds = partition.SnapshotEstimates
            .Select(estimate => estimate.SnapshotId)
            .Distinct()
            .OrderByDescending(id => id)
            .ToArray();
        var keepIds = observedIds
            .Where(id => decisionsById.TryGetValue(id, out var decision) && decision.Action != SnapshotCleanupAction.PurgeCandidate)
            .OrderByDescending(id => id)
            .ToArray();
        var blockedIds = observedIds
            .Where(id => decisionsById.TryGetValue(id, out var decision) && decision.Action == SnapshotCleanupAction.Blocked)
            .OrderByDescending(id => id)
            .ToArray();
        var purgeIds = observedIds
            .Where(id => decisionsById.TryGetValue(id, out var decision) && decision.Action == SnapshotCleanupAction.PurgeCandidate)
            .OrderByDescending(id => id)
            .ToArray();
        var candidates = partitionCandidates
            .Where(candidate => string.Equals(candidate.PartitionName, partition.PartitionName, StringComparison.Ordinal))
            .ToArray();
        var canExecute = !string.IsNullOrWhiteSpace(instrument) && keepIds.Length > 0 && purgeIds.Length > 0;
        var reason = canExecute
            ? "eligible for partition-local keep-only rewrite with explicit execution approval"
            : BuildSnapshotRewriteRefusalReason(instrument, keepIds, purgeIds);

        return new SnapshotPartitionRewritePlan(
            partition.PartitionName,
            instrument,
            keepIds,
            blockedIds,
            purgeIds,
            partition.TotalBytes,
            partition.HeapBytes,
            partition.IndexBytes,
            partition.LiveTuples,
            candidates.Where(candidate => candidate.Action == SnapshotCleanupAction.Keep).Sum(candidate => candidate.EstimatedRows),
            candidates.Where(candidate => candidate.Action == SnapshotCleanupAction.PurgeCandidate).Sum(candidate => candidate.EstimatedRows),
            candidates.Where(candidate => candidate.Action == SnapshotCleanupAction.PurgeCandidate).Sum(candidate => candidate.EstimatedBytes),
            canExecute,
            reason);
    }

    private static string BuildSnapshotRewriteRefusalReason(string instrument, IReadOnlyList<long> keepIds, IReadOnlyList<long> purgeIds)
    {
        if (string.IsNullOrWhiteSpace(instrument))
            return "blocked: partition is not an expected leaderboard_entries_snapshot child";
        if (keepIds.Count == 0)
            return "blocked: no retained snapshot ids were identified";
        if (purgeIds.Count == 0)
            return "not a cleanup candidate: no purge snapshot ids were identified";
        return "blocked by snapshot rewrite policy";
    }

    private async Task<SnapshotPartitionRewritePlan> BuildSnapshotRewritePlanForExecutionAsync(
        NpgsqlConnection conn,
        string partitionName,
        DatabaseMaintenanceDryRunOptions options,
        CancellationToken ct)
    {
        if (!SnapshotPartitionInstruments.TryGetValue(partitionName, out var instrument))
        {
            return new SnapshotPartitionRewritePlan(
                partitionName,
                Instrument: string.Empty,
                KeepSnapshotIds: [],
                BlockedSnapshotIds: [],
                PurgeSnapshotIds: [],
                TotalBytes: 0,
                HeapBytes: 0,
                IndexBytes: 0,
                LiveTuples: 0,
                EstimatedRetainRows: 0,
                EstimatedPurgeRows: 0,
                EstimatedPurgeBytes: 0,
                CanExecute: false,
                Reason: "blocked: partition is not an expected leaderboard_entries_snapshot child");
        }

        var activeSnapshotIds = await LoadActiveSnapshotIdsAsync(conn, ct);
        var projectionSourceSnapshotIds = await LoadProjectionSourceSnapshotIdsAsync(conn, ct);
        var scrapes = await LoadScrapesAsync(conn, ct);
        var exactSnapshotIds = await LoadDistinctSnapshotIdsAsync(conn, partitionName, ct);
        var policy = SnapshotRetentionPolicy.Create(
            activeSnapshotIds,
            projectionSourceSnapshotIds,
            scrapes,
            options.RollbackCompletedSnapshotsToKeep);
        var decisions = policy.Classify(exactSnapshotIds);
        var keepIds = decisions
            .Where(decision => decision.Action != SnapshotCleanupAction.PurgeCandidate)
            .Select(decision => decision.SnapshotId)
            .OrderByDescending(id => id)
            .ToArray();
        var blockedIds = decisions
            .Where(decision => decision.Action == SnapshotCleanupAction.Blocked)
            .Select(decision => decision.SnapshotId)
            .OrderByDescending(id => id)
            .ToArray();
        var purgeIds = decisions
            .Where(decision => decision.Action == SnapshotCleanupAction.PurgeCandidate)
            .Select(decision => decision.SnapshotId)
            .OrderByDescending(id => id)
            .ToArray();
        var footprint = await LoadRelationFootprintAsync(conn, partitionName, ct);
        var estimatedRetainRows = keepIds.Length == 0 ? 0 : await CountSnapshotRowsAsync(conn, partitionName, keepIds, ct);
        var estimatedPurgeRows = purgeIds.Length == 0 ? 0 : await CountSnapshotRowsAsync(conn, partitionName, purgeIds, ct);
        var canExecute = keepIds.Length > 0 && purgeIds.Length > 0;

        return new SnapshotPartitionRewritePlan(
            partitionName,
            instrument,
            keepIds,
            blockedIds,
            purgeIds,
            footprint?.TotalBytes ?? 0,
            footprint?.HeapBytes ?? 0,
            footprint?.IndexBytes ?? 0,
            footprint?.LiveTuples ?? 0,
            estimatedRetainRows,
            estimatedPurgeRows,
            purgeIds.Length == 0 || footprint is null ? 0 : Math.Max(0, footprint.TotalBytes - EstimateRetainedBytes(footprint.TotalBytes, estimatedRetainRows, estimatedPurgeRows)),
            canExecute,
            canExecute ? "eligible for partition-local keep-only rewrite with explicit execution approval" : BuildSnapshotRewriteRefusalReason(instrument, keepIds, purgeIds));
    }

    private static long EstimateRetainedBytes(long totalBytes, long retainRows, long purgeRows)
    {
        var totalRows = retainRows + purgeRows;
        return totalRows <= 0 ? totalBytes : (long)Math.Round(totalBytes * (retainRows / (double)totalRows), MidpointRounding.AwayFromZero);
    }

    private static async Task<IReadOnlyList<long>> LoadActiveSnapshotIdsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "leaderboard_snapshot_state", ct))
            return [];

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT active_snapshot_id
            FROM leaderboard_snapshot_state
            WHERE active_snapshot_id IS NOT NULL
            ORDER BY active_snapshot_id DESC
            """;

        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetInt64(0));

        return ids;
    }

    private static async Task<IReadOnlyList<long>> LoadProjectionSourceSnapshotIdsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "solo_current_projection_scope", ct))
            return [];

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT source_snapshot_id
            FROM solo_current_projection_scope
            WHERE source_snapshot_id IS NOT NULL
            ORDER BY source_snapshot_id DESC
            """;

        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetInt64(0));

        return ids;
    }

    private static async Task<IReadOnlyList<ScrapeSummary>> LoadScrapesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "scrape_log", ct))
            return [];

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, started_at, completed_at
            FROM scrape_log
            ORDER BY id DESC
            """;

        var scrapes = new List<ScrapeSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            scrapes.Add(new ScrapeSummary(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2)));
        }

        return scrapes;
    }

    private static async Task<IReadOnlyList<SnapshotPartitionStats>> LoadSnapshotPartitionStatsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                c.relname,
                pg_total_relation_size(c.oid)::BIGINT AS total_bytes,
                pg_relation_size(c.oid)::BIGINT AS heap_bytes,
                pg_indexes_size(c.oid)::BIGINT AS index_bytes,
                COALESCE(st.n_live_tup, 0)::BIGINT AS n_live_tup,
                COALESCE(stats.n_distinct, 0)::REAL AS n_distinct,
                COALESCE(stats.most_common_vals::TEXT, '') AS most_common_vals,
                COALESCE(stats.most_common_freqs::TEXT, '') AS most_common_freqs
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_stat_all_tables st ON st.relid = c.oid
            LEFT JOIN pg_stats stats ON stats.schemaname = n.nspname
                AND stats.tablename = c.relname
                AND stats.attname = 'snapshot_id'
            WHERE n.nspname = 'public'
              AND c.relkind = 'r'
              AND c.relname LIKE 'leaderboard_entries_snapshot_%'
            ORDER BY pg_total_relation_size(c.oid) DESC
            """;

        var partitions = new List<SnapshotPartitionStats>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var partitionName = reader.GetString(0);
            var totalBytes = reader.GetInt64(1);
            var heapBytes = reader.GetInt64(2);
            var indexBytes = reader.GetInt64(3);
            var liveTuples = reader.GetInt64(4);
            var nDistinct = reader.GetFloat(5);
            var valuesText = reader.GetString(6);
            var frequenciesText = reader.GetString(7);
            var estimates = ParseSnapshotEstimates(valuesText, frequenciesText, liveTuples, totalBytes);
            var coverage = estimates.Sum(estimate => estimate.EstimatedRowShare);

            partitions.Add(new SnapshotPartitionStats(
                partitionName,
                totalBytes,
                heapBytes,
                indexBytes,
                liveTuples,
                nDistinct,
                coverage,
                estimates));
        }

        return partitions;
    }

    private static async Task<LegacyLiveDryRunSummary> LoadLegacyLiveAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "leaderboard_entries", ct))
        {
            return new LegacyLiveDryRunSummary(true, [], [], 0, 0, 0, 0, "leaderboard_entries does not exist; planned drop is already satisfied");
        }

        var tables = new List<RelationFootprint>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    child.relname,
                    pg_total_relation_size(child.oid)::BIGINT AS total_bytes,
                    pg_relation_size(child.oid)::BIGINT AS heap_bytes,
                    pg_indexes_size(child.oid)::BIGINT AS index_bytes,
                    COALESCE(st.n_live_tup, 0)::BIGINT AS n_live_tup,
                    COALESCE(st.n_dead_tup, 0)::BIGINT AS n_dead_tup
                FROM pg_inherits inh
                JOIN pg_class parent ON parent.oid = inh.inhparent
                JOIN pg_class child ON child.oid = inh.inhrelid
                JOIN pg_namespace n ON n.oid = child.relnamespace
                LEFT JOIN pg_stat_all_tables st ON st.relid = child.oid
                WHERE n.nspname = 'public'
                  AND parent.relname = 'leaderboard_entries'
                  AND child.relkind = 'r'
                ORDER BY pg_total_relation_size(child.oid) DESC
                """;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                tables.Add(ReadRelationFootprint(reader));
        }

        var indexes = await LoadIndexesForTablesAsync(conn, tables.Select(table => table.Name).ToArray(), ct);
        return new LegacyLiveDryRunSummary(
            PlannedDropCandidate: true,
            tables,
            indexes,
            TotalBytes: tables.Sum(table => table.TotalBytes),
            HeapBytes: tables.Sum(table => table.HeapBytes),
            IndexBytes: tables.Sum(table => table.IndexBytes),
            LiveTuples: tables.Sum(table => table.LiveTuples),
            "legacy live solo is planned for drop; report-only in this act slice");
    }

    private static async Task<LegacyStagingDryRunSummary> LoadLegacyStagingAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "leaderboard_staging", ct))
        {
            return new LegacyStagingDryRunSummary(false, null, 0, 0, false, "leaderboard_staging does not exist", []);
        }

        RelationFootprint? footprint;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    c.relname,
                    pg_total_relation_size(c.oid)::BIGINT AS total_bytes,
                    pg_relation_size(c.oid)::BIGINT AS heap_bytes,
                    pg_indexes_size(c.oid)::BIGINT AS index_bytes,
                    COALESCE(st.n_live_tup, 0)::BIGINT AS n_live_tup,
                    COALESCE(st.n_dead_tup, 0)::BIGINT AS n_dead_tup
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                LEFT JOIN pg_stat_all_tables st ON st.relid = c.oid
                WHERE n.nspname = 'public'
                  AND c.relname = 'leaderboard_staging'
                  AND c.relkind = 'r'
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            footprint = await reader.ReadAsync(ct) ? ReadRelationFootprint(reader) : null;
        }

        var exactRowCount = await CountRowsIfTableExistsAsync(conn, "leaderboard_staging", ct);
        var activeStagingMetaRows = await CountActiveStagingMetaRowsAsync(conn, ct);
        var eligible = IsLegacyStagingCleanupEligible(footprint, exactRowCount, activeStagingMetaRows);
        var reason = BuildLegacyStagingReason(footprint, exactRowCount, activeStagingMetaRows, eligible);
        var indexes = await LoadIndexesForTablesAsync(conn, ["leaderboard_staging"], ct);

        return new LegacyStagingDryRunSummary(eligible, footprint, exactRowCount, activeStagingMetaRows, true, reason, indexes);
    }

    private static bool IsLegacyStagingCleanupEligible(RelationFootprint? footprint, long exactRowCount, long activeStagingMetaRows) =>
        footprint is not null && exactRowCount == 0 && activeStagingMetaRows == 0 && footprint.IndexBytes >= LegacyStagingCleanupMinIndexBytes;

    private static string BuildLegacyStagingReason(RelationFootprint? footprint, long exactRowCount, long activeStagingMetaRows, bool eligible)
    {
        if (footprint is null)
            return "leaderboard_staging relation was not found";
        if (eligible)
            return "eligible candidate: no rows, no active staging metadata, and index bytes exceed cleanup threshold";
        if (exactRowCount > 0)
            return "blocked: legacy staging still has rows";
        if (activeStagingMetaRows > 0)
            return "blocked: active staging metadata exists for incomplete scrapes";
        if (footprint.IndexBytes < LegacyStagingCleanupMinIndexBytes)
            return "not a cleanup candidate: index bytes are below cleanup threshold";
        return "not eligible under current dry-run policy";
    }

    private static async Task<IReadOnlyList<IndexDryRunCandidate>> LoadIndexCandidatesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH indexes AS (
                SELECT
                    idx.oid AS index_oid,
                    idx.relname AS index_name,
                    tbl.relname AS table_name,
                    pg_relation_size(idx.oid)::BIGINT AS index_bytes,
                    COALESCE(st.idx_scan, 0)::BIGINT AS idx_scan,
                    COALESCE(st.idx_tup_read, 0)::BIGINT AS idx_tup_read,
                    COALESCE(st.idx_tup_fetch, 0)::BIGINT AS idx_tup_fetch,
                    pg_get_indexdef(idx.oid) AS indexdef
                FROM pg_class idx
                JOIN pg_index ix ON ix.indexrelid = idx.oid
                JOIN pg_class tbl ON tbl.oid = ix.indrelid
                JOIN pg_namespace n ON n.oid = idx.relnamespace
                LEFT JOIN pg_stat_user_indexes st ON st.indexrelid = idx.oid
                WHERE n.nspname = 'public'
            )
            SELECT index_name, table_name, index_bytes, idx_scan, idx_tup_read, idx_tup_fetch, indexdef
            FROM indexes
            WHERE index_name = ANY(@watchNames)
            OR (table_name LIKE 'leaderboard_entries_%'
                AND table_name NOT LIKE 'leaderboard_entries_snapshot%'
                AND (
                    indexdef LIKE '%song_id, instrument, rank%'
                    OR indexdef LIKE '%song_id, instrument, score DESC%'
                    OR indexdef LIKE '%account_id, instrument%'
                    OR indexdef LIKE '%account_id, song_id, instrument%'))
            ORDER BY index_bytes DESC, index_name
            """;
        cmd.Parameters.AddWithValue("watchNames", WatchIndexNames);

        var results = new List<IndexDryRunCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var tableName = reader.GetString(1);
            var indexDef = reader.GetString(6);
            var source = DescribeRecreationSource(name, tableName, indexDef);
            var deprecated = IsDeprecatedIndexName(name) || source.Contains("deprecated", StringComparison.OrdinalIgnoreCase);
            results.Add(new IndexDryRunCandidate(
                name,
                tableName,
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                indexDef,
                source,
                deprecated,
                "report-only; no index drop in this act slice"));
        }

        return results;
    }

    private static bool IsDeprecatedIndexName(string name) =>
        DeprecatedParentIndexNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string DescribeRecreationSource(string name, string tableName, string indexDef)
    {
        if (name is "ix_bstr_team_best" or "ix_bstr_team_worst")
            return "DatabaseInitializer creates this band ranking index";
        if (name == "leaderboard_staging_pkey")
            return "leaderboard_staging primary key exists while legacy table remains";
        if (name == "ix_be_combo")
            return "deprecated band combo index; schema comments mark it removed";
        if (IsDeprecatedIndexName(name))
            return "deprecated parent index name still appears in runtime recreate arrays or legacy schema drift";
        if (tableName.StartsWith("leaderboard_entries_", StringComparison.Ordinal) && indexDef.Contains("song_id, instrument, rank", StringComparison.Ordinal))
            return "child legacy live rank index inherited from deprecated parent/runtime definition";
        if (tableName.StartsWith("leaderboard_entries_", StringComparison.Ordinal) && indexDef.Contains("song_id, instrument, score DESC", StringComparison.Ordinal))
            return "child legacy live score index inherited from current/deprecated parent definition";
        if (tableName.StartsWith("leaderboard_entries_", StringComparison.Ordinal))
            return "child legacy live index; legacy live is planned for drop";
        return "watched index candidate";
    }

    private static async Task<IReadOnlyList<IndexFootprint>> LoadIndexesForTablesAsync(NpgsqlConnection conn, IReadOnlyList<string> tableNames, CancellationToken ct)
    {
        if (tableNames.Count == 0)
            return [];

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                idx.relname AS index_name,
                tbl.relname AS table_name,
                pg_relation_size(idx.oid)::BIGINT AS index_bytes,
                COALESCE(st.idx_scan, 0)::BIGINT AS idx_scan,
                COALESCE(st.idx_tup_read, 0)::BIGINT AS idx_tup_read,
                COALESCE(st.idx_tup_fetch, 0)::BIGINT AS idx_tup_fetch,
                pg_get_indexdef(idx.oid) AS indexdef
            FROM pg_class idx
            JOIN pg_index ix ON ix.indexrelid = idx.oid
            JOIN pg_class tbl ON tbl.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = idx.relnamespace
            LEFT JOIN pg_stat_user_indexes st ON st.indexrelid = idx.oid
            WHERE n.nspname = 'public'
              AND tbl.relname = ANY(@tableNames)
            ORDER BY pg_relation_size(idx.oid) DESC, idx.relname
            """;
        cmd.Parameters.AddWithValue("tableNames", tableNames.ToArray());

        var indexes = new List<IndexFootprint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            indexes.Add(new IndexFootprint(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetString(6)));
        }

        return indexes;
    }

    private static async Task<SnapshotPartitionRewritePreflight> BuildSnapshotRewritePreflightAsync(
        NpgsqlConnection conn,
        SnapshotPartitionRewritePlan plan,
        CancellationToken ct)
    {
        var refusalReasons = new List<string>();
        if (!plan.CanExecute)
            refusalReasons.Add(plan.Reason);

        var latestScrape = await LoadLatestScrapeAsync(conn, ct);
        if (latestScrape is null)
            refusalReasons.Add("blocked: scrape_log has no rows");
        else if (!latestScrape.IsCompleted)
            refusalReasons.Add($"blocked: latest scrape {latestScrape.Id:N0} is still incomplete");

        if (!await IsSnapshotChildPartitionAsync(conn, plan.PartitionName, ct))
            refusalReasons.Add("blocked: requested table is not currently attached to leaderboard_entries_snapshot");

        var totalRows = await CountRowsIfTableExistsAsync(conn, plan.PartitionName, ct);
        var retainedRows = plan.KeepSnapshotIds.Count == 0 ? 0 : await CountSnapshotRowsAsync(conn, plan.PartitionName, plan.KeepSnapshotIds, ct);
        var purgeRows = plan.PurgeSnapshotIds.Count == 0 ? 0 : await CountSnapshotRowsAsync(conn, plan.PartitionName, plan.PurgeSnapshotIds, ct);
        if (retainedRows == 0)
            refusalReasons.Add("blocked: retained row count is zero");
        if (purgeRows == 0)
            refusalReasons.Add("blocked: purge row count is zero");
        if (retainedRows + purgeRows != totalRows)
            refusalReasons.Add($"blocked: retained + purge rows ({retainedRows + purgeRows:N0}) do not match total rows ({totalRows:N0})");

        return new SnapshotPartitionRewritePreflight(
            CanExecute: refusalReasons.Count == 0,
            refusalReasons,
            latestScrape?.Id,
            latestScrape?.CompletedAt,
            totalRows,
            retainedRows,
            purgeRows);
    }

    private static async Task CreateSnapshotReplacementPartitionAsync(
        NpgsqlConnection conn,
        SnapshotPartitionRewritePlan plan,
        SnapshotPartitionRewritePreflight preflight,
        string replacementName,
        CancellationToken ct)
    {
        await ExecuteNonQueryAsync(conn, $"DROP TABLE IF EXISTS {QualifiedIdentifier(replacementName)}", ct);
        await ExecuteNonQueryAsync(conn, $"CREATE TABLE {QualifiedIdentifier(replacementName)} (LIKE {QualifiedIdentifier(plan.PartitionName)} INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING STORAGE INCLUDING GENERATED INCLUDING IDENTITY)", ct);

        await using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.CommandTimeout = 0;
            insertCmd.CommandText = $"INSERT INTO {QualifiedIdentifier(replacementName)} SELECT * FROM {QualifiedIdentifier(plan.PartitionName)} WHERE snapshot_id = ANY(@keepIds)";
            insertCmd.Parameters.AddWithValue("keepIds", plan.KeepSnapshotIds.ToArray());
            var inserted = await insertCmd.ExecuteNonQueryAsync(ct);
            if (inserted != preflight.RetainedRows)
                throw new InvalidOperationException($"Replacement insert mismatch: expected {preflight.RetainedRows:N0}, inserted {inserted:N0}.");
        }

        await ExecuteNonQueryAsync(conn, $"ALTER TABLE {QualifiedIdentifier(replacementName)} ADD PRIMARY KEY (snapshot_id, song_id, instrument, account_id)", ct);
        await ExecuteNonQueryAsync(conn, $"CREATE INDEX {QuoteIdentifier(replacementName + "_score_idx")} ON {QualifiedIdentifier(replacementName)} (snapshot_id, song_id, instrument, score DESC)", ct);
        await ExecuteNonQueryAsync(conn, $"ANALYZE {QualifiedIdentifier(replacementName)}", ct);
    }

    private static async Task SwapSnapshotPartitionAsync(
        NpgsqlConnection conn,
        SnapshotPartitionRewritePlan plan,
        string replacementName,
        string retiredName,
        CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await ExecuteNonQueryAsync(conn, $"LOCK TABLE {QualifiedIdentifier(SnapshotParentTable)} IN ACCESS EXCLUSIVE MODE", ct, tx);
            await ExecuteNonQueryAsync(conn, $"ALTER TABLE {QualifiedIdentifier(SnapshotParentTable)} DETACH PARTITION {QualifiedIdentifier(plan.PartitionName)}", ct, tx);
            await ExecuteNonQueryAsync(conn, $"ALTER TABLE {QualifiedIdentifier(plan.PartitionName)} RENAME TO {QuoteIdentifier(retiredName)}", ct, tx);
            await ExecuteNonQueryAsync(conn, $"ALTER TABLE {QualifiedIdentifier(replacementName)} RENAME TO {QuoteIdentifier(plan.PartitionName)}", ct, tx);
            await ExecuteNonQueryAsync(conn, $"ALTER TABLE {QualifiedIdentifier(SnapshotParentTable)} ATTACH PARTITION {QualifiedIdentifier(plan.PartitionName)} FOR VALUES IN ({QuoteLiteral(plan.Instrument)})", ct, tx);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task RenameSnapshotPartitionIndexesAsync(
        NpgsqlConnection conn,
        SnapshotPartitionRewritePlan plan,
        string replacementName,
        CancellationToken ct)
    {
        await RenameIndexIfExistsAsync(conn, replacementName + "_pkey", plan.PartitionName + "_pkey", ct);
        await RenameIndexIfExistsAsync(conn, replacementName + "_score_idx", plan.PartitionName + "_score_idx", ct);
    }

    private static async Task RenameIndexIfExistsAsync(
        NpgsqlConnection conn,
        string oldIndexName,
        string newIndexName,
        CancellationToken ct)
    {
        if (!IsSafeIdentifier(oldIndexName) || !IsSafeIdentifier(newIndexName))
            throw new ArgumentException("Index name is not a safe public identifier.");

        if (!await RelationExistsAsync(conn, oldIndexName, ct) || await RelationExistsAsync(conn, newIndexName, ct))
            return;

        await ExecuteNonQueryAsync(conn, $"ALTER INDEX {QualifiedIdentifier(oldIndexName)} RENAME TO {QuoteIdentifier(newIndexName)}", ct);
    }

    private static async Task<IReadOnlyList<long>> LoadDistinctSnapshotIdsAsync(NpgsqlConnection conn, string tableName, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = $"SELECT DISTINCT snapshot_id FROM {QualifiedIdentifier(tableName)} ORDER BY snapshot_id DESC";
        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static async Task<long> CountSnapshotRowsAsync(NpgsqlConnection conn, string tableName, IReadOnlyCollection<long> snapshotIds, CancellationToken ct)
    {
        if (snapshotIds.Count == 0)
            return 0;

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = $"SELECT COUNT(*)::BIGINT FROM {QualifiedIdentifier(tableName)} WHERE snapshot_id = ANY(@snapshotIds)";
        cmd.Parameters.AddWithValue("snapshotIds", snapshotIds.ToArray());
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<ScrapeSummary?> LoadLatestScrapeAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "scrape_log", ct))
            return null;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, started_at, completed_at FROM scrape_log ORDER BY id DESC LIMIT 1";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new ScrapeSummary(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetDateTime(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2));
    }

    private static async Task<bool> IsSnapshotChildPartitionAsync(NpgsqlConnection conn, string tableName, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_inherits inh
                JOIN pg_class parent ON parent.oid = inh.inhparent
                JOIN pg_class child ON child.oid = inh.inhrelid
                JOIN pg_namespace n ON n.oid = child.relnamespace
                WHERE n.nspname = 'public'
                  AND parent.relname = 'leaderboard_entries_snapshot'
                  AND child.relname = @tableName
            )
            """;
        cmd.Parameters.AddWithValue("tableName", tableName);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<RelationFootprint?> LoadRelationFootprintAsync(NpgsqlConnection conn, string tableName, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                c.relname,
                pg_total_relation_size(c.oid)::BIGINT AS total_bytes,
                pg_relation_size(c.oid)::BIGINT AS heap_bytes,
                pg_indexes_size(c.oid)::BIGINT AS index_bytes,
                COALESCE(st.n_live_tup, 0)::BIGINT AS n_live_tup,
                COALESCE(st.n_dead_tup, 0)::BIGINT AS n_dead_tup
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_stat_all_tables st ON st.relid = c.oid
            WHERE n.nspname = 'public'
              AND c.relname = @tableName
              AND c.relkind = 'r'
            """;
        cmd.Parameters.AddWithValue("tableName", tableName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRelationFootprint(reader) : null;
    }

    private static async Task<long> GetRelationTotalBytesAsync(NpgsqlConnection conn, string tableName, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(pg_total_relation_size(to_regclass(@name)), 0)::BIGINT";
        cmd.Parameters.AddWithValue("name", $"public.{tableName}");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task DropTableIfExistsAsync(NpgsqlConnection conn, string tableName, CancellationToken ct) =>
        await ExecuteNonQueryAsync(conn, $"DROP TABLE IF EXISTS {QualifiedIdentifier(tableName)}", ct);

    private static async Task ExecuteNonQueryAsync(NpgsqlConnection conn, string sql, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> TryAcquireSnapshotMaintenanceLockAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_try_advisory_lock(@lockKey)";
        cmd.Parameters.AddWithValue("lockKey", SnapshotMaintenanceAdvisoryLockKey);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task ReleaseSnapshotMaintenanceLockAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_advisory_unlock(@lockKey)";
        cmd.Parameters.AddWithValue("lockKey", SnapshotMaintenanceAdvisoryLockKey);
        await cmd.ExecuteScalarAsync(ct);
    }

    private static string NormalizeSnapshotPartitionName(string partitionName)
    {
        if (string.IsNullOrWhiteSpace(partitionName))
            throw new ArgumentException("Snapshot partition name is required.", nameof(partitionName));
        var normalized = partitionName.Trim();
        if (!IsSafeIdentifier(normalized))
            throw new ArgumentException("Snapshot partition name is not a safe public identifier.", nameof(partitionName));
        return normalized;
    }

    private static string QualifiedIdentifier(string identifier) => $"public.{QuoteIdentifier(identifier)}";

    private static string QuoteIdentifier(string identifier)
    {
        if (!IsSafeIdentifier(identifier))
            throw new ArgumentException($"Identifier is not safe: {identifier}", nameof(identifier));
        return $"\"{identifier}\"";
    }

    private static bool IsSafeIdentifier(string identifier) =>
        Regex.IsMatch(identifier, "^[A-Za-z_][A-Za-z0-9_]*$");

    private static string QuoteLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static async Task<long> CountRowsIfTableExistsAsync(
        NpgsqlConnection conn,
        string tableName,
        CancellationToken ct,
        NpgsqlTransaction? tx = null)
    {
        if (!await TableExistsAsync(conn, tableName, ct, tx))
            return 0;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT COUNT(*)::BIGINT FROM {tableName}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<long> CountActiveStagingMetaRowsAsync(
        NpgsqlConnection conn,
        CancellationToken ct,
        NpgsqlTransaction? tx = null)
    {
        if (!await TableExistsAsync(conn, "leaderboard_staging_meta", ct, tx) || !await TableExistsAsync(conn, "scrape_log", ct, tx))
            return 0;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT COUNT(*)::BIGINT
            FROM leaderboard_staging_meta meta
            WHERE EXISTS (
                SELECT 1
                FROM scrape_log log
                WHERE log.id = meta.scrape_id
                  AND log.completed_at IS NULL
            )
            """;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection conn,
        string tableName,
        CancellationToken ct,
        NpgsqlTransaction? tx = null) =>
        await RelationExistsAsync(conn, tableName, ct, tx);

    private static async Task<bool> RelationExistsAsync(
        NpgsqlConnection conn,
        string relationName,
        CancellationToken ct,
        NpgsqlTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT to_regclass(@name) IS NOT NULL";
        cmd.Parameters.AddWithValue("name", $"public.{relationName}");
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }

    private static RelationFootprint ReadRelationFootprint(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5));

    private static IReadOnlyList<SnapshotEstimate> ParseSnapshotEstimates(string valuesText, string frequenciesText, long liveTuples, long totalBytes)
    {
        var values = ParseLongArray(valuesText);
        var frequencies = ParseDoubleArray(frequenciesText);
        var count = Math.Min(values.Count, frequencies.Count);
        var estimates = new List<SnapshotEstimate>(count);

        for (var i = 0; i < count; i++)
        {
            var frequency = Math.Clamp(frequencies[i], 0, 1);
            estimates.Add(new SnapshotEstimate(
                values[i],
                frequency,
                (long)Math.Round(liveTuples * frequency, MidpointRounding.AwayFromZero),
                (long)Math.Round(totalBytes * frequency, MidpointRounding.AwayFromZero)));
        }

        return estimates.OrderByDescending(estimate => estimate.SnapshotId).ToArray();
    }

    private static IReadOnlyList<long> ParseLongArray(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "{}")
            return [];

        return TrimPgArray(text)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => long.Parse(value.Trim('"'), CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static IReadOnlyList<double> ParseDoubleArray(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "{}")
            return [];

        return TrimPgArray(text)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.Parse(value.Trim('"'), CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static string TrimPgArray(string text) =>
        Regex.Replace(text.Trim(), "^\\{|\\}$", string.Empty);
}

public sealed record DatabaseMaintenanceDryRunOptions(int RollbackCompletedSnapshotsToKeep = 1);

public sealed record DatabaseMaintenanceDryRunReport(
    DateTime CapturedAtUtc,
    DatabaseMaintenanceDryRunOptions Options,
    SnapshotDryRunSummary Snapshots,
    IReadOnlyList<SnapshotPartitionRewritePlan> SnapshotRewritePlans,
    LegacyLiveDryRunSummary LegacyLive,
    LegacyStagingDryRunSummary LegacyStaging,
    IReadOnlyList<IndexDryRunCandidate> IndexCandidates,
    string Mode);

public sealed record SnapshotDryRunSummary(
    int RollbackCompletedSnapshotsToKeep,
    IReadOnlyList<long> ActiveSnapshotIds,
    IReadOnlyList<long> ProjectionSourceSnapshotIds,
    IReadOnlyList<ScrapeSummary> RecentScrapes,
    IReadOnlyList<SnapshotRetentionDecision> SnapshotDecisions,
    IReadOnlyList<PartitionSnapshotCandidate> PartitionCandidates,
    long EstimatedKeepRows,
    long EstimatedPurgeRows,
    long EstimatedBlockedRows,
    long EstimatedKeepBytes,
    long EstimatedPurgeBytes,
    long EstimatedBlockedBytes);

public sealed record ScrapeSummary(long Id, DateTime? StartedAt, DateTime? CompletedAt)
{
    public bool IsCompleted => CompletedAt.HasValue;
}

public sealed record SnapshotPartitionStats(
    string PartitionName,
    long TotalBytes,
    long HeapBytes,
    long IndexBytes,
    long LiveTuples,
    double NDistinct,
    double EstimatedStatsCoverage,
    IReadOnlyList<SnapshotEstimate> SnapshotEstimates);

public sealed record SnapshotEstimate(long SnapshotId, double EstimatedRowShare, long EstimatedRows, long EstimatedBytes);

public sealed record PartitionSnapshotCandidate(
    string PartitionName,
    long SnapshotId,
    SnapshotCleanupAction Action,
    IReadOnlyList<string> Reasons,
    long EstimatedRows,
    long EstimatedBytes,
    double EstimatedRowShare);

public sealed record SnapshotPartitionRewritePlan(
    string PartitionName,
    string Instrument,
    IReadOnlyList<long> KeepSnapshotIds,
    IReadOnlyList<long> BlockedSnapshotIds,
    IReadOnlyList<long> PurgeSnapshotIds,
    long TotalBytes,
    long HeapBytes,
    long IndexBytes,
    long LiveTuples,
    long EstimatedRetainRows,
    long EstimatedPurgeRows,
    long EstimatedPurgeBytes,
    bool CanExecute,
    string Reason);

public sealed record SnapshotPartitionRewritePreflight(
    bool CanExecute,
    IReadOnlyList<string> RefusalReasons,
    long? LatestScrapeId,
    DateTime? LatestScrapeCompletedAt,
    long TotalRows,
    long RetainedRows,
    long PurgeRows);

public sealed record SnapshotPartitionRewriteResult(
    bool Executed,
    SnapshotPartitionRewritePlan Plan,
    SnapshotPartitionRewritePreflight? Preflight,
    string Reason,
    string? RetiredPartitionName,
    string? ReplacementPartitionName,
    bool DroppedRetiredPartition,
    long BeforeTotalBytes,
    long AfterTotalBytes,
    long ReclaimedBytes,
    DateTime ExecutedAtUtc);

public sealed record LegacyLiveDryRunSummary(
    bool PlannedDropCandidate,
    IReadOnlyList<RelationFootprint> Tables,
    IReadOnlyList<IndexFootprint> Indexes,
    long TotalBytes,
    long HeapBytes,
    long IndexBytes,
    long LiveTuples,
    string Reason);

public sealed record LegacyStagingDryRunSummary(
    bool CleanupEligible,
    RelationFootprint? Footprint,
    long ExactRowCount,
    long ActiveStagingMetaRows,
    bool ReportOnly,
    string Reason,
    IReadOnlyList<IndexFootprint> Indexes);

public sealed record LegacyStagingCleanupResult(
    bool Executed,
    LegacyStagingDryRunSummary Before,
    LegacyStagingDryRunSummary? After,
    string Reason,
    string? Sql,
    DateTime ExecutedAtUtc);

public sealed record RelationFootprint(string Name, long TotalBytes, long HeapBytes, long IndexBytes, long LiveTuples, long DeadTuples);

public sealed record IndexFootprint(
    string Name,
    string TableName,
    long IndexBytes,
    long ScanCount,
    long TuplesRead,
    long TuplesFetched,
    string Definition);

public sealed record IndexDryRunCandidate(
    string Name,
    string TableName,
    long IndexBytes,
    long ScanCount,
    long TuplesRead,
    long TuplesFetched,
    string Definition,
    string RecreationSource,
    bool DeprecatedOrPlannedDrop,
    string Recommendation);

public sealed class SnapshotRetentionPolicy
{
    private readonly HashSet<long> _activeSnapshotIds;
    private readonly HashSet<long> _projectionSourceSnapshotIds;
    private readonly HashSet<long> _rollbackSnapshotIds;
    private readonly IReadOnlyDictionary<long, ScrapeSummary> _scrapesById;
    private readonly long? _maxScrapeId;

    private SnapshotRetentionPolicy(
        IReadOnlyCollection<long> activeSnapshotIds,
        IReadOnlyCollection<long> projectionSourceSnapshotIds,
        IReadOnlyCollection<long> rollbackSnapshotIds,
        IReadOnlyDictionary<long, ScrapeSummary> scrapesById,
        long? maxScrapeId)
    {
        _activeSnapshotIds = activeSnapshotIds.ToHashSet();
        _projectionSourceSnapshotIds = projectionSourceSnapshotIds.ToHashSet();
        _rollbackSnapshotIds = rollbackSnapshotIds.ToHashSet();
        _scrapesById = scrapesById;
        _maxScrapeId = maxScrapeId;
    }

    public static SnapshotRetentionPolicy Create(
        IReadOnlyCollection<long> activeSnapshotIds,
        IReadOnlyCollection<long> projectionSourceSnapshotIds,
        IReadOnlyCollection<ScrapeSummary> scrapes,
        int rollbackCompletedSnapshotsToKeep)
    {
        if (rollbackCompletedSnapshotsToKeep < 0)
            throw new ArgumentOutOfRangeException(nameof(rollbackCompletedSnapshotsToKeep), "Rollback keep count cannot be negative.");

        var active = activeSnapshotIds.ToHashSet();
        var projectionSources = projectionSourceSnapshotIds.ToHashSet();
        var pinned = active.Concat(projectionSources).ToHashSet();
        var rollback = scrapes
            .Where(scrape => scrape.IsCompleted && !pinned.Contains(scrape.Id))
            .OrderByDescending(scrape => scrape.Id)
            .Take(rollbackCompletedSnapshotsToKeep)
            .Select(scrape => scrape.Id)
            .ToArray();

        return new SnapshotRetentionPolicy(
            active,
            projectionSources,
            rollback,
            scrapes.ToDictionary(scrape => scrape.Id),
            scrapes.Count == 0 ? null : scrapes.Max(scrape => scrape.Id));
    }

    public IReadOnlyList<SnapshotRetentionDecision> Classify(IEnumerable<long> observedSnapshotIds)
    {
        return observedSnapshotIds
            .Distinct()
            .OrderByDescending(id => id)
            .Select(Classify)
            .ToArray();
    }

    public SnapshotRetentionDecision Classify(long snapshotId)
    {
        var reasons = new List<string>();
        var action = SnapshotCleanupAction.PurgeCandidate;

        if (_activeSnapshotIds.Contains(snapshotId))
            reasons.Add("active snapshot state");
        if (_projectionSourceSnapshotIds.Contains(snapshotId))
            reasons.Add("current projection source");
        if (_rollbackSnapshotIds.Contains(snapshotId))
            reasons.Add("rollback completed snapshot");

        if (reasons.Count > 0)
        {
            action = SnapshotCleanupAction.Keep;
        }
        else if (!_scrapesById.TryGetValue(snapshotId, out var scrape))
        {
            action = SnapshotCleanupAction.Blocked;
            reasons.Add("missing scrape_log row");
        }
        else if (!scrape.IsCompleted)
        {
            if (_maxScrapeId.HasValue && _maxScrapeId.Value > snapshotId)
            {
                reasons.Add("incomplete scrape with newer scrape started");
            }
            else
            {
                action = SnapshotCleanupAction.Blocked;
                reasons.Add("latest incomplete scrape has no newer scrape yet");
            }
        }
        else
        {
            reasons.Add("older completed scrape outside rollback window");
        }

        return new SnapshotRetentionDecision(snapshotId, action, reasons);
    }
}

public sealed record SnapshotRetentionDecision(long SnapshotId, SnapshotCleanupAction Action, IReadOnlyList<string> Reasons);

public enum SnapshotCleanupAction
{
    Keep,
    PurgeCandidate,
    Blocked,
}