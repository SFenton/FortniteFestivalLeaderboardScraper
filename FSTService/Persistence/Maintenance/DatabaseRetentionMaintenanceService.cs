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
    private readonly NpgsqlDataSource _dataSource;
    private readonly DatabaseMaintenanceDryRunReporter _reporter;
    private readonly IDatabasePressureMonitor _pressureMonitor;
    private readonly ServiceMaintenanceLock _serviceMaintenanceLock;
    private readonly IOptions<DatabaseMaintenanceOptions> _options;
    private readonly ILogger<DatabaseRetentionMaintenanceService> _log;

    public DatabaseRetentionMaintenanceService(
        NpgsqlDataSource dataSource,
        DatabaseMaintenanceDryRunReporter reporter,
        IDatabasePressureMonitor pressureMonitor,
        ServiceMaintenanceLock serviceMaintenanceLock,
        IOptions<DatabaseMaintenanceOptions> options,
        ILogger<DatabaseRetentionMaintenanceService> log)
    {
        _dataSource = dataSource;
        _reporter = reporter;
        _pressureMonitor = pressureMonitor;
        _serviceMaintenanceLock = serviceMaintenanceLock;
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
        var maintenanceLock =
            await _serviceMaintenanceLock.TryAcquireAsync(
                conn,
                TimeSpan.FromMilliseconds(
                    Math.Clamp(
                        options
                            .ServiceMaintenanceLockWaitMilliseconds,
                        0,
                        5_000)),
                ct);
        if (maintenanceLock is null)
        {
            return DatabaseRetentionMaintenanceResult.SkippedResult(
                startedAtUtc,
                "another service-level retention maintenance run already holds the advisory lock");
        }

        await using var maintenanceLease = maintenanceLock;
        var snapshotResult =
            await RunSnapshotRetentionAsync(options, ct);
        var metadataResult =
            await RunMetadataTtlCleanupAsync(conn, options, ct);
        var completedAtUtc = DateTime.UtcNow;
        return new DatabaseRetentionMaintenanceResult(
            startedAtUtc,
            completedAtUtc,
            Skipped: false,
            Reason: BuildResultReason(
                snapshotResult,
                metadataResult),
            snapshotResult,
            metadataResult);
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
            await RetireEligiblePublicationGenerationsAsync(conn, cutoffTimestamp, batchSize, maxBatches, commandTimeoutSeconds, options.ServiceMaintenanceLockWaitMilliseconds, ct),
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

    private async Task<MetadataRetentionCleanupItemResult>
        RetireEligiblePublicationGenerationsAsync(
            NpgsqlConnection conn,
            DateTime cutoffTimestamp,
            int batchSize,
            int maxBatches,
            int commandTimeoutSeconds,
            int lockWaitMilliseconds,
            CancellationToken ct)
    {
        const string itemName =
            "publication_generation_retirement";
        if (!await TableExistsAsync(
                conn,
                "publication_generations",
                ct)
            || !await TableExistsAsync(
                conn,
                "publication_surface_bindings",
                ct)
            || !await TableExistsAsync(
                conn,
                "leaderboard_published_scope_source",
                ct))
        {
            return MetadataRetentionCleanupItemResult
                .SkippedResult(
                    itemName,
                    "publication generation provenance tables do not exist");
        }

        var publicationLock =
            await PostgresSessionAdvisoryLock.TryAcquireAsync(
                conn,
                PublicationGenerationSchema.AdvisoryLockKey,
                shared: true,
                TimeSpan.FromMilliseconds(
                    Math.Clamp(
                        lockWaitMilliseconds,
                        0,
                        5_000)),
                ct);
        if (publicationLock is null)
        {
            return MetadataRetentionCleanupItemResult
                .SkippedResult(
                    itemName,
                    "publication generation retirement deferred because the publication lock is busy");
        }

        await using var publicationLease = publicationLock;
        var totalRetired = 0L;
        var totalDeletedSources = 0L;
        for (var batch = 0; batch < maxBatches; batch++)
        {
            ct.ThrowIfCancellationRequested();
            await using var transaction =
                await conn.BeginTransactionAsync(ct);
            await using (var timeout = conn.CreateCommand())
            {
                timeout.Transaction = transaction;
                timeout.CommandTimeout =
                    commandTimeoutSeconds;
                timeout.CommandText = """
                    SELECT set_config(
                        'lock_timeout',
                        '500ms',
                        TRUE);
                    SELECT set_config(
                        'statement_timeout',
                        @statementTimeout,
                        TRUE);
                    """;
                timeout.Parameters.AddWithValue(
                    "statementTimeout",
                    commandTimeoutSeconds > 0
                        ? $"{commandTimeoutSeconds}s"
                        : "0");
                await timeout.ExecuteNonQueryAsync(ct);
            }

            var publications =
                new List<(
                    long PublicationId,
                    long ScrapeId,
                    long ExpectedSourceRows)>();
            await using (var select = conn.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandTimeout =
                    commandTimeoutSeconds;
                select.CommandText = """
                    WITH candidate_generations AS MATERIALIZED (
                        SELECT
                            generation.publication_id,
                            generation.scrape_id
                        FROM publication_generations generation
                        JOIN scrape_log scrape
                          ON scrape.id =
                                generation.scrape_id
                        JOIN scrape_publication_state state
                          ON state.id = TRUE
                        WHERE generation.status = 'retained'
                          AND scrape.status = 'completed'
                          AND scrape.completed_at IS NOT NULL
                          AND scrape.failed_at IS NULL
                          AND scrape.completed_at <
                                @cutoffTimestamp
                          AND generation.publication_id
                                IS DISTINCT FROM
                                    state.current_publication_id
                          AND generation.publication_id
                                IS DISTINCT FROM
                                    state.previous_publication_id
                          AND generation.publication_id
                                IS DISTINCT FROM
                                    state.working_publication_id
                          AND COALESCE(
                                state.public_reads_frozen,
                                FALSE) = FALSE
                          AND state.publication_commit_intent_started_at
                                IS NULL
                          AND state.max_score_mutation_gate_token
                                IS NULL
                          AND generation.metadata #>>
                                '{publicationPreparation,expectedPublishedScopeCount}'
                                    ~ '^[1-9][0-9]*$'
                        ORDER BY generation.publication_id
                        LIMIT @scanSize
                    ), source_stats AS (
                        SELECT
                            source.published_scrape_id,
                            COUNT(*)::BIGINT
                                AS actual_row_count,
                            COUNT(*) FILTER (
                                WHERE btrim(source.song_id) = ''
                                   OR source.instrument NOT IN (
                                        'Solo_Guitar',
                                        'Solo_Bass',
                                        'Solo_Vocals',
                                        'Solo_Drums',
                                        'Solo_PeripheralGuitar',
                                        'Solo_PeripheralBass',
                                        'Solo_PeripheralVocals',
                                        'Solo_PeripheralCymbals',
                                        'Solo_PeripheralDrums')
                                   OR source.scope_kind <>
                                        'alltime'
                                   OR btrim(
                                        source.content_fingerprint) =
                                        ''
                                   OR btrim(
                                        source.coverage_fingerprint) =
                                        ''
                                   OR NOT source.is_complete
                                   OR source.source_scrape_id <= 0
                                   OR source.source_scrape_id >
                                        source.published_scrape_id
                                   OR source.reported_total_entries <
                                        source.row_count
                                   OR (
                                        source.source_kind =
                                            'snapshot'
                                        AND (
                                            source.source_snapshot_id
                                                IS NULL
                                            OR source.source_snapshot_id
                                                <= 0
                                            OR source.source_snapshot_id
                                                <>
                                                source.source_scrape_id
                                            OR source.row_count <= 0
                                            OR source.reported_total_pages
                                                <= 0
                                        )
                                   )
                                   OR (
                                        source.source_kind = 'empty'
                                        AND (
                                            source.source_snapshot_id
                                                IS NOT NULL
                                            OR source.row_count <> 0
                                            OR source.reported_total_entries
                                                <> 0
                                            OR source.reported_total_pages
                                                <> 0
                                        )
                                   )
                                   OR source.source_kind NOT IN (
                                        'snapshot',
                                        'empty')
                            )::INTEGER AS invalid_row_count,
                            (
                                COUNT(*)
                                - COUNT(DISTINCT (
                                    source.instrument,
                                    source.song_id,
                                    source.scope_kind))
                            )::INTEGER
                                AS duplicate_key_count,
                            encode(
                                sha256(
                                    convert_to(
                                        COALESCE(
                                            string_agg(
                                                octet_length(
                                                    source.instrument)
                                                    ::TEXT
                                                || ':'
                                                || source.instrument
                                                || octet_length(
                                                    source.song_id)
                                                    ::TEXT
                                                || ':'
                                                || source.song_id
                                                || octet_length(
                                                    source.scope_kind)
                                                    ::TEXT
                                                || ':'
                                                || source.scope_kind
                                                || chr(10),
                                                '' ORDER BY
                                                    source.instrument
                                                        COLLATE "C",
                                                    source.song_id
                                                        COLLATE "C",
                                                    source.scope_kind
                                                        COLLATE "C"),
                                            ''),
                                        'UTF8')),
                                'hex') AS actual_key_hash
                        FROM leaderboard_published_scope_source
                            source
                        JOIN candidate_generations candidate
                          ON candidate.scrape_id =
                                source.published_scrape_id
                        GROUP BY source.published_scrape_id
                    )
                    SELECT
                        generation.publication_id,
                        generation.scrape_id,
                        binding.row_count
                    FROM publication_generations generation
                    JOIN candidate_generations candidate
                      ON candidate.publication_id =
                            generation.publication_id
                     AND candidate.scrape_id =
                            generation.scrape_id
                    JOIN scrape_log scrape
                      ON scrape.id = generation.scrape_id
                    JOIN publication_surface_bindings binding
                      ON binding.publication_id =
                            generation.publication_id
                     AND binding.surface_name =
                            'solo_scope_sources'
                    JOIN source_stats source
                      ON source.published_scrape_id =
                            generation.scrape_id
                    JOIN scrape_publication_state state
                      ON state.id = TRUE
                    WHERE generation.status = 'retained'
                      AND scrape.status = 'completed'
                      AND scrape.completed_at IS NOT NULL
                      AND scrape.failed_at IS NULL
                      AND scrape.completed_at <
                            @cutoffTimestamp
                      AND generation.publication_id
                            IS DISTINCT FROM
                                state.current_publication_id
                      AND generation.publication_id
                            IS DISTINCT FROM
                                state.previous_publication_id
                      AND generation.publication_id
                            IS DISTINCT FROM
                                state.working_publication_id
                      AND generation.scrape_id
                            IS DISTINCT FROM
                                state.published_scrape_id
                      AND generation.scrape_id
                            IS DISTINCT FROM
                                state.public_reads_frozen_scrape_id
                      AND generation.scrape_id
                            IS DISTINCT FROM
                                state.improvement_notifications_scrape_id
                      AND generation.scrape_id
                            IS DISTINCT FROM
                                state.improvement_notifications_projection_scrape_id
                      AND COALESCE(
                            state.public_reads_frozen,
                            FALSE) = FALSE
                      AND state.publication_commit_intent_started_at
                            IS NULL
                      AND state.publication_commit_intent_owner
                            IS NULL
                      AND state.max_score_mutation_gate_token
                            IS NULL
                      AND state.max_score_mutation_gate_publication_id
                            IS NULL
                      AND generation.metadata #>>
                            '{publicationPreparation,scrapeId}' =
                                generation.scrape_id::TEXT
                      AND generation.metadata #>>
                            '{publicationPreparation,publicationId}' =
                                generation.publication_id::TEXT
                      AND generation.metadata #>>
                            '{publicationPreparation,expectedPublishedScopeCount}'
                                ~ '^[1-9][0-9]*$'
                      AND binding.binding_kind = 'scrape_id'
                      AND binding.status = 'ready'
                      AND binding.binding_json ->> 'table' =
                            'leaderboard_published_scope_source'
                      AND binding.binding_json ->> 'publicationId' =
                            generation.publication_id::TEXT
                      AND binding.binding_json ->> 'publishedScrapeId' =
                            generation.scrape_id::TEXT
                      AND binding.binding_json ->> 'keyHashVersion' =
                            '1'
                      AND binding.row_count = (
                            generation.metadata #>>
                                '{publicationPreparation,expectedPublishedScopeCount}'
                          )::BIGINT
                      AND source.actual_row_count =
                            binding.row_count
                      AND source.invalid_row_count = 0
                      AND source.duplicate_key_count = 0
                      AND binding.content_hash ~
                            '^[0-9a-f]{64}$'
                      AND binding.content_hash =
                            source.actual_key_hash
                      AND NOT EXISTS (
                            SELECT 1
                            FROM publication_surface_bindings
                                other_binding
                            WHERE other_binding.publication_id =
                                    generation.publication_id
                              AND other_binding.status NOT IN (
                                  'ready',
                                  'retired')
                              AND NOT (
                                  other_binding.surface_name =
                                      'item_shop'
                                  AND other_binding.status =
                                      'building'
                                  AND other_binding.binding_kind =
                                      'legacy_live_unversioned'
                                  AND other_binding.binding_json
                                          ->> 'table' =
                                      'item_shop_tracks')
                      )
                      AND NOT EXISTS (
                            SELECT 1
                            FROM publication_api_response_cache
                            WHERE publication_id =
                                    generation.publication_id
                      )
                      AND NOT EXISTS (
                            SELECT 1
                            FROM publication_api_response_cache_staging
                            WHERE publication_id =
                                    generation.publication_id
                      )
                      AND NOT EXISTS (
                            SELECT 1
                            FROM publication_song_catalog
                            WHERE publication_id =
                                    generation.publication_id
                      )
                      AND NOT EXISTS (
                            SELECT 1
                            FROM publication_path_artifacts
                            WHERE publication_id =
                                    generation.publication_id
                      )
                    ORDER BY generation.publication_id
                    FOR UPDATE OF generation SKIP LOCKED
                    LIMIT @batchSize
                    """;
                select.Parameters.AddWithValue(
                    "cutoffTimestamp",
                    cutoffTimestamp);
                select.Parameters.AddWithValue(
                    "batchSize",
                    batchSize);
                select.Parameters.AddWithValue(
                    "scanSize",
                    Math.Min(
                        (long)batchSize * 8,
                        Math.Max(
                            (long)batchSize,
                            4_096)));
                await using var reader =
                    await select.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    publications.Add((
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        reader.GetInt64(2)));
                }
            }

            if (publications.Count == 0)
            {
                await transaction.CommitAsync(ct);
                break;
            }

            var publicationIds = publications
                .Select(static item => item.PublicationId)
                .ToArray();
            var scrapeIds = publications
                .Select(static item => item.ScrapeId)
                .ToArray();
            var retiredAt = DateTime.UtcNow;
            await using (var retireBindings =
                         conn.CreateCommand())
            {
                retireBindings.Transaction = transaction;
                retireBindings.CommandTimeout =
                    commandTimeoutSeconds;
                retireBindings.CommandText = """
                    UPDATE publication_surface_bindings
                    SET binding_kind = CASE
                            WHEN surface_name =
                                'solo_scope_sources'
                            THEN 'retired_scrape_id'
                            ELSE binding_kind
                        END,
                        binding_json = binding_json
                            || jsonb_build_object(
                                'retired', TRUE,
                                'retiredAt', @retiredAt),
                        status = 'retired',
                        built_at = @retiredAt
                    WHERE publication_id =
                            ANY(@publicationIds)
                    """;
                retireBindings.Parameters.AddWithValue(
                    "publicationIds",
                    publicationIds);
                retireBindings.Parameters.AddWithValue(
                    "retiredAt",
                    retiredAt);
                await retireBindings.ExecuteNonQueryAsync(ct);
            }

            await using (var deleteSources =
                         conn.CreateCommand())
            {
                deleteSources.Transaction = transaction;
                deleteSources.CommandTimeout =
                    commandTimeoutSeconds;
                deleteSources.CommandText = """
                    DELETE FROM leaderboard_published_scope_source
                    WHERE published_scrape_id =
                            ANY(@scrapeIds)
                    """;
                deleteSources.Parameters.AddWithValue(
                    "scrapeIds",
                    scrapeIds);
                var deletedSources =
                    await deleteSources.ExecuteNonQueryAsync(ct);
                var expectedSources = publications.Aggregate(
                    0L,
                    static (total, publication) =>
                        checked(
                            total
                            + publication.ExpectedSourceRows));
                if (deletedSources != expectedSources)
                {
                    throw new InvalidOperationException(
                        $"Publication generation retirement expected to remove {expectedSources} source row(s) but removed {deletedSources}.");
                }
                totalDeletedSources += deletedSources;
            }

            await using (var retireGenerations =
                         conn.CreateCommand())
            {
                retireGenerations.Transaction = transaction;
                retireGenerations.CommandTimeout =
                    commandTimeoutSeconds;
                retireGenerations.CommandText = """
                    UPDATE publication_generations generation
                    SET status = 'retired',
                        retired_at = @retiredAt,
                        retired_scrape_id = selected.scrape_id,
                        scrape_id = NULL,
                        metadata = generation.metadata
                            || jsonb_build_object(
                                'retirement',
                                jsonb_build_object(
                                    'retiredAt', @retiredAt,
                                    'scrapeId',
                                        selected.scrape_id,
                                    'reason',
                                        'metadata_ttl'))
                    FROM unnest(
                        @publicationIds::BIGINT[],
                        @scrapeIds::BIGINT[])
                        AS selected(
                            publication_id,
                            scrape_id)
                    WHERE generation.publication_id =
                            selected.publication_id
                      AND generation.scrape_id =
                            selected.scrape_id
                      AND generation.status = 'retained'
                    """;
                retireGenerations.Parameters.AddWithValue(
                    "retiredAt",
                    retiredAt);
                retireGenerations.Parameters.AddWithValue(
                    "publicationIds",
                    publicationIds);
                retireGenerations.Parameters.AddWithValue(
                    "scrapeIds",
                    scrapeIds);
                var retired =
                    await retireGenerations
                        .ExecuteNonQueryAsync(ct);
                if (retired != publications.Count)
                {
                    throw new InvalidOperationException(
                        $"Publication generation retirement selected {publications.Count} generation(s) but retired {retired}.");
                }
                totalRetired += retired;
            }

            await transaction.CommitAsync(ct);
            _log.LogInformation(
                "Metadata TTL retired {RetiredGenerations:N0} publication generation(s) and deleted {DeletedSourceRows:N0} retired publication source row(s) in batch {BatchNumber:N0}.",
                publications.Count,
                totalDeletedSources,
                batch + 1);
            if (publications.Count < batchSize)
                break;
        }

        return MetadataRetentionCleanupItemResult
            .ExecutedResult(
                itemName,
                totalDeletedSources,
                totalRetired == 0
                    ? "no safely retired unnamed publication generations were eligible"
                    : $"retired {totalRetired:N0} unnamed terminal publication generation(s) and deleted {totalDeletedSources:N0} retired source row(s)");
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

        return MetadataRetentionCleanupItemResult.ExecutedResult(
            tableName,
            deleted,
            "deleted scrape log rows only after confirming no physical, publication, writer-failure, or retention-control provenance still references them");
    }

    internal static string BuildScrapeLogRetentionPredicate(string alias) => $"""
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
        AND NOT EXISTS (
            SELECT 1
            FROM leaderboard_published_scope_source source
            WHERE source.published_scrape_id = {alias}.id
               OR source.source_scrape_id = {alias}.id
        )
        AND NOT EXISTS (
            SELECT 1
            FROM publication_generations generation
            WHERE (
                  generation.scrape_id = {alias}.id
                  OR generation.retired_scrape_id =
                        {alias}.id
              )
              AND (
                  generation.status <> 'retired'
                  OR generation.retired_at IS NULL
                  OR generation.retired_scrape_id <>
                        {alias}.id
                  OR EXISTS (
                      SELECT 1
                      FROM publication_surface_bindings binding
                      WHERE binding.publication_id =
                            generation.publication_id
                        AND binding.status <> 'retired'
                  )
              )
        )
        AND NOT EXISTS (
            SELECT 1
            FROM scrape_writer_failures failure
            WHERE failure.scrape_id = {alias}.id
              AND failure.replayed_at IS NULL
        )
        AND NOT EXISTS (
            SELECT 1
            FROM scrape_publication_state publication
            WHERE publication.id = TRUE
              AND (
                  publication.published_scrape_id = {alias}.id
                  OR publication.public_reads_frozen_scrape_id =
                        {alias}.id
                  OR publication.improvement_notifications_scrape_id =
                        {alias}.id
                  OR publication.improvement_notifications_projection_scrape_id =
                        {alias}.id
              )
        )
        AND NOT EXISTS (
            SELECT 1
            FROM snapshot_generation_retention_cycles cycle
            WHERE cycle.trigger_scrape_id = {alias}.id
        )
        AND NOT EXISTS (
            SELECT 1
            FROM snapshot_generation_retention_deferrals deferral
            WHERE deferral.trigger_scrape_id = {alias}.id
        )
        AND NOT EXISTS (
            SELECT 1
            FROM snapshot_generation_retention_observations observation
            WHERE observation.snapshot_id = {alias}.id
        )
        AND NOT EXISTS (
            SELECT 1
            FROM snapshot_generation_retention_holds hold
            WHERE hold.snapshot_id = {alias}.id
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

        var estimatedRetainedBytes = plan.EstimatedRetainBytes;
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
            var estimatedRetainedBytes = plan.EstimatedRetainBytes;
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
