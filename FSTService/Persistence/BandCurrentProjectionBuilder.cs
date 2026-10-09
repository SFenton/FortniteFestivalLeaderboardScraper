using System.Collections.Concurrent;
using System.Diagnostics;
using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FSTService.Persistence;

public sealed class BandCurrentProjectionBuilder
{
    internal const int LegacyMemberStatsAggregateSubqueriesPerRow = 7;
    internal const int MaxParallelScopesLimit = 16;
    private const int UnsettledScopeCleanupChunkSize = 500;
    private const string ExpectedMemberCountSql = """
        CASE band_type
            WHEN 'Band_Duets' THEN 2
            WHEN 'Band_Trios' THEN 3
            WHEN 'Band_Quad' THEN 4
            ELSE 0
        END
        """;
    public const string ProjectionTable = "current_band_leaderboard_entries";
    public const string StateTable = "band_current_projection_state";
    public const string ScopeTable = "band_current_projection_scope";
    public const string GenerationSequence = "band_current_projection_generation_seq";

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<BandCurrentProjectionBuilder> _log;

    public BandCurrentProjectionBuilder(NpgsqlDataSource dataSource, ILogger<BandCurrentProjectionBuilder> log)
    {
        _dataSource = dataSource;
        _log = log;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = ProjectionSchemaSql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public BandCurrentProjectionStats Inspect(int recentScopeLimit = 20)
    {
        using var conn = _dataSource.OpenConnection();

        if (!TableExists(conn, ProjectionTable))
        {
            return new BandCurrentProjectionStats(
                ProjectionExists: false,
                RowCount: 0,
                ScopeCount: 0,
                FailedScopeCount: 0,
                CurrentGeneration: null,
                FullRebuiltAt: null,
                LastScopeRebuiltAt: null,
                TotalSize: "0 bytes",
                RecentScopes: []);
        }

        using var statsCmd = conn.CreateCommand();
        statsCmd.CommandText = $"""
            SELECT
                COALESCE((SELECT row_count FROM {StateTable} WHERE id = TRUE), 0) AS row_count,
                COALESCE((SELECT scope_count FROM {StateTable} WHERE id = TRUE), 0) AS scope_count,
                COALESCE((SELECT failed_scope_count FROM {StateTable} WHERE id = TRUE), 0) AS failed_scope_count,
                (SELECT current_generation FROM {StateTable} WHERE id = TRUE) AS current_generation,
                (SELECT full_rebuilt_at FROM {StateTable} WHERE id = TRUE) AS full_rebuilt_at,
                (SELECT last_scope_rebuilt_at FROM {StateTable} WHERE id = TRUE) AS last_scope_rebuilt_at,
                pg_size_pretty(COALESCE((
                    SELECT SUM(pg_total_relation_size(relid))
                    FROM pg_partition_tree('{ProjectionTable}'::regclass)
                ), 0)) AS total_size
            """;

        long rowCount = 0;
        long scopeCount = 0;
        long failedScopeCount = 0;
        long? currentGeneration = null;
        DateTime? fullRebuiltAt = null;
        DateTime? lastScopeRebuiltAt = null;
        var totalSize = "0 bytes";

        using (var reader = statsCmd.ExecuteReader())
        {
            if (reader.Read())
            {
                rowCount = reader.GetInt64(0);
                scopeCount = reader.GetInt64(1);
                failedScopeCount = reader.GetInt64(2);
                currentGeneration = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                fullRebuiltAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
                lastScopeRebuiltAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5);
                totalSize = reader.IsDBNull(6) ? "0 bytes" : reader.GetString(6);
            }
        }

        var recentScopes = new List<BandCurrentProjectionScopeSummary>();
        if (TableExists(conn, ScopeTable) && recentScopeLimit > 0)
        {
            using var recentCmd = conn.CreateCommand();
            recentCmd.CommandText = $"""
                SELECT song_id, band_type, ranking_scope, scope_combo_id, row_count, status, error_message, last_rebuilt_at, projection_generation, published_generation, published_row_count
                FROM {ScopeTable}
                ORDER BY updated_at DESC
                LIMIT @limit
                """;
            recentCmd.Parameters.AddWithValue("limit", recentScopeLimit);

            using var reader = recentCmd.ExecuteReader();
            while (reader.Read())
            {
                recentScopes.Add(new BandCurrentProjectionScopeSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                    reader.GetInt64(8),
                    reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    reader.GetInt64(10)));
            }
        }

        return new BandCurrentProjectionStats(
            ProjectionExists: true,
            RowCount: rowCount,
            ScopeCount: scopeCount,
            FailedScopeCount: failedScopeCount,
            CurrentGeneration: currentGeneration,
            FullRebuiltAt: fullRebuiltAt,
            LastScopeRebuiltAt: lastScopeRebuiltAt,
            TotalSize: totalSize,
            RecentScopes: recentScopes);
    }

    public async Task<IReadOnlyList<BandCurrentProjectionScopeKey>> LoadCurrentScopesAsync(
        IReadOnlyCollection<string>? bandTypes = null,
        bool includeOverallScopes = true,
        bool includeComboScopes = true,
        CancellationToken ct = default)
    {
        if (!includeOverallScopes && !includeComboScopes)
            return [];

        var normalizedBandTypes = NormalizeBandTypes(bandTypes);
        var bandTypeFilter = normalizedBandTypes.Count > 0
            ? "AND be.band_type = ANY(@bandTypes)"
            : string.Empty;

        var unions = new List<string>();
        if (includeOverallScopes)
        {
            unions.Add("""
                SELECT DISTINCT song_id, band_type, 'overall'::TEXT AS ranking_scope, ''::TEXT AS scope_combo_id
                FROM NormalizedEntries
                """);
        }

        if (includeComboScopes)
        {
            unions.Add("""
                SELECT DISTINCT song_id, band_type, 'combo'::TEXT AS ranking_scope, combo_id AS scope_combo_id
                FROM NormalizedEntries
                WHERE combo_id <> ''
                  AND array_length(string_to_array(combo_id, '+'), 1) = CASE band_type
                      WHEN 'Band_Duets' THEN 2
                      WHEN 'Band_Trios' THEN 3
                      WHEN 'Band_Quad' THEN 4
                      ELSE 0
                  END
                """);
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = $"""
            WITH {BandComboMapCtes}, NormalizedEntries AS (
                SELECT
                    be.song_id,
                    be.band_type,
                    {BandSongComboIdFromMapSql} AS combo_id
                FROM band_entries be
                {BandComboMapJoinSql}
                WHERE NOT be.is_over_threshold
                  {bandTypeFilter}
            )
            {string.Join("\nUNION\n", unions)}
            ORDER BY band_type, ranking_scope, scope_combo_id, song_id
            """;

        if (normalizedBandTypes.Count > 0)
            cmd.Parameters.AddWithValue("bandTypes", normalizedBandTypes.ToArray());

        var scopes = new List<BandCurrentProjectionScopeKey>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            scopes.Add(new BandCurrentProjectionScopeKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return scopes;
    }

    public async Task<BandCurrentProjectionRebuildResult> RebuildAllAsync(
        BandCurrentProjectionRebuildOptions? options = null,
        Action<int, int, BandCurrentProjectionScopeResult>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new BandCurrentProjectionRebuildOptions();
        var total = Stopwatch.StartNew();
        var generation = await NextGenerationAsync(ct);
        var scopes = await LoadCurrentScopesAsync(options.BandTypes, options.IncludeOverallScopes, options.IncludeComboScopes, ct);

        if (options.ClearExisting)
            await ClearProjectionAsync(ct);

        var results = new List<BandCurrentProjectionScopeResult>(scopes.Count);
        var failedScopes = 0;
        for (var i = 0; i < scopes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await RebuildScopeAsync(scopes[i], options, generation, updateGlobalState: false, ct);
                results.Add(result);
                progress?.Invoke(i + 1, scopes.Count, result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedScopes++;
            }
        }

        var affectedBandTypes = GetAffectedBandTypes(options, scopes, includeAllWhenUnfiltered: true);
        var canPruneOrphans = !options.ClearExisting
            && options.IncludeOverallScopes
            && options.IncludeComboScopes
            && affectedBandTypes.Count > 0;
        var fullRebuiltAt = DateTime.UtcNow;
        var publishResult = options.PublishOnSuccess
            ? await TryPublishGenerationAsync(generation, scopes, fullRebuiltAt, ct)
            : BandCurrentProjectionPublishResult.NotPublished(generation, scopes.Count, 0, scopes.Count, failedScopes, 0);

        if (!options.PublishOnSuccess)
            await RefreshGlobalStateFromScopesAsync(fullRebuiltAt: fullRebuiltAt, ct);

        var orphanedRows = canPruneOrphans && options.PublishOnSuccess
            ? await DeleteOrphanedProjectionRowsAsync(options, scopes, affectedBandTypes, ct)
            : 0;
        var candidateRowsDeleted = options.PublishOnSuccess
            ? await DeleteUnpublishedCandidateRowsAsync(options, affectedBandTypes, ct)
            : 0;
        total.Stop();

        var stats = Inspect();
        return new BandCurrentProjectionRebuildResult(
            Generation: generation,
            ScopeCount: scopes.Count,
            InsertedRows: results.Sum(static result => result.InsertedRows),
            DeletedRows: results.Sum(static result => result.DeletedRows) + publishResult.DeletedRows + orphanedRows + candidateRowsDeleted,
            OrphanedRowsDeleted: orphanedRows,
            CandidateRowsDeleted: candidateRowsDeleted,
            PublishResult: publishResult,
            TotalElapsedMs: Math.Round(total.Elapsed.TotalMilliseconds, 3),
            Stats: stats,
            Scopes: results);
    }

    public async Task<BandCurrentProjectionScopeResult> RebuildScopeAsync(
        BandCurrentProjectionScopeKey scope,
        BandCurrentProjectionRebuildOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new BandCurrentProjectionRebuildOptions();
        var generation = await NextGenerationAsync(ct);
        return await RebuildScopeAsync(scope, options, generation, updateGlobalState: true, ct);
    }

    /// <param name="onScopesFinalized">
    /// Invoked exactly once on the normal (non-empty-input) path, immediately
    /// after unchanged-scope filtering finalizes <c>scopesToRefresh</c> — the
    /// actual, normalized/deduplicated, filtered selected-scope set that will
    /// be rebuilt. Fires even when the selected count is zero (all scopes were
    /// unchanged), so callers can begin exact progress reporting against the
    /// real denominator instead of the pre-filter candidate count.
    /// </param>
    /// <param name="onScopeCompleted">
    /// Invoked once per scope, only after that scope's individual rebuild
    /// succeeds (never for scopes whose rebuild throws). Band-type groups are
    /// processed concurrently, so this callback may be invoked concurrently
    /// from multiple groups; callers must make it thread-safe.
    /// </param>
    public async Task<BandCurrentProjectionIncrementalRefreshResult> RefreshScopesAsync(
        IReadOnlyCollection<BandCurrentProjectionScopeKey> scopes,
        BandCurrentProjectionRebuildOptions? options = null,
        CancellationToken ct = default,
        Action<IReadOnlyCollection<BandCurrentProjectionScopeKey>>? onScopesFinalized = null,
        Action<BandCurrentProjectionScopeKey>? onScopeCompleted = null)
    {
        options ??= new BandCurrentProjectionRebuildOptions();
        var normalizedScopes = scopes
            .Select(NormalizeScope)
            .Distinct()
            .OrderBy(static scope => scope.BandType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static scope => scope.RankingScope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static scope => scope.ScopeComboId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static scope => scope.SongId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedScopes.Length == 0)
            return new BandCurrentProjectionIncrementalRefreshResult(
                0,
                0,
                0,
                0,
                0,
                0,
                BandCurrentProjectionPublishResult.NotPublished(0, 0, 0, 0, 0, 0),
                0,
                [],
                BandCurrentProjectionOperationMetrics.Empty);

        var sw = Stopwatch.StartNew();
        var generation = await NextGenerationAsync(ct);
        var scopesToRefresh = options.SkipUnchangedScopes
            ? await FilterScopesNeedingRefreshAsync(normalizedScopes, ct)
            : normalizedScopes;
        onScopesFinalized?.Invoke(scopesToRefresh);

        if (scopesToRefresh.Length == 0)
        {
            sw.Stop();
            _log.LogInformation(
                "Band current projection refresh skipped {SkippedScopes:N0}/{ScopeCount:N0} unchanged scope(s).",
                normalizedScopes.Length,
                normalizedScopes.Length);
            return new BandCurrentProjectionIncrementalRefreshResult(
                0,
                0,
                0,
                0,
                0,
                0,
                BandCurrentProjectionPublishResult.NotPublished(generation, 0, 0, 0, 0, 0),
                Math.Round(sw.Elapsed.TotalMilliseconds, 3),
                [],
                BandCurrentProjectionOperationMetrics.Empty);
        }

        var results = new ConcurrentBag<BandCurrentProjectionScopeResult>();
        var failedScopes = 0;
        var maxParallelBandTypes = Math.Clamp(options.MaxParallelBandTypes, 1, BandInstrumentMapping.AllBandTypes.Count);
        var maxParallelScopes = Math.Clamp(options.MaxParallelScopes, 0, MaxParallelScopesLimit);

        async ValueTask RefreshScopeAsync(BandCurrentProjectionScopeKey scope, CancellationToken innerCt)
        {
            innerCt.ThrowIfCancellationRequested();
            BandCurrentProjectionScopeResult scopeResult;
            try
            {
                scopeResult = await RebuildScopeAsync(
                    scope,
                    options,
                    generation,
                    updateGlobalState: false,
                    innerCt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failedScopes);
                return;
            }

            results.Add(scopeResult);
            onScopeCompleted?.Invoke(scope);
        }

        async ValueTask RefreshPairAsync(BandCurrentProjectionScopeKey[] pairScopes, CancellationToken innerCt)
        {
            innerCt.ThrowIfCancellationRequested();
            IReadOnlyList<BandCurrentProjectionScopeResult> pairResults;
            try
            {
                pairResults = await RebuildPairAsync(pairScopes, options, generation, innerCt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Add(ref failedScopes, pairScopes.Length);
                return;
            }

            foreach (var pairResult in pairResults)
            {
                results.Add(pairResult);
                onScopeCompleted?.Invoke(
                    new BandCurrentProjectionScopeKey(
                        pairResult.SongId,
                        pairResult.BandType,
                        pairResult.RankingScope,
                        pairResult.ScopeComboId));
            }
        }

        if (maxParallelScopes > 0 && options.BatchScopesBySourcePair)
        {
            // Pair transactions write disjoint projection and scope-state keys.
            await Parallel.ForEachAsync(
                GroupBySourcePair(scopesToRefresh),
                new ParallelOptions { MaxDegreeOfParallelism = maxParallelScopes, CancellationToken = ct },
                RefreshPairAsync);
        }
        else if (maxParallelScopes > 0)
        {
            // Scope transactions write disjoint projection and scope-state keys,
            // so any band type can run beside any other.
            await Parallel.ForEachAsync(
                InterleaveByBandType(scopesToRefresh),
                new ParallelOptions { MaxDegreeOfParallelism = maxParallelScopes, CancellationToken = ct },
                RefreshScopeAsync);
        }
        else
        {
            var bandTypeGroups = scopesToRefresh
                .GroupBy(static scope => scope.BandType, StringComparer.OrdinalIgnoreCase)
                .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.ToArray())
                .ToArray();

            await Parallel.ForEachAsync(
                bandTypeGroups,
                new ParallelOptions { MaxDegreeOfParallelism = maxParallelBandTypes, CancellationToken = ct },
                async (group, innerCt) =>
                {
                    foreach (var scope in group)
                        await RefreshScopeAsync(scope, innerCt);
                });
        }

        var orderedResults = results
            .OrderBy(static result => result.BandType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static result => result.RankingScope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static result => result.ScopeComboId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static result => result.SongId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var publishParallelism = Math.Clamp(options.PublishParallelism, 0, MaxParallelScopesLimit);
        var publishResult = !options.PublishOnSuccess
            ? BandCurrentProjectionPublishResult.NotPublished(generation, scopesToRefresh.Length, 0, scopesToRefresh.Length, failedScopes, 0)
            : publishParallelism > 0
                ? await PublishGenerationBySongAsync(generation, scopesToRefresh, orderedResults, publishParallelism, ct)
                : await TryPublishGenerationAsync(generation, scopesToRefresh, fullRebuiltAt: null, ct);

        await RefreshGlobalStateFromScopesAsync(fullRebuiltAt: null, ct);

        var affectedBandTypes = GetAffectedBandTypes(options, scopesToRefresh, includeAllWhenUnfiltered: false);
        var candidateRowsDeleted = !options.PublishOnSuccess
            ? 0
            : publishParallelism > 0
                ? await DeleteUnpublishedCandidateRowsForUnsettledScopesAsync(options, affectedBandTypes, ct)
                : await DeleteUnpublishedCandidateRowsAsync(options, affectedBandTypes, ct);

        sw.Stop();

        var batchedBySourcePair = maxParallelScopes > 0 && options.BatchScopesBySourcePair;
        var operationMetrics =
            CreateOperationMetrics(
                orderedResults,
                options,
                batchedBySourcePair);
        _log.LogInformation(
            "Band current projection refresh selected {RefreshScopes:N0}/{ProvidedScopes:N0} scope(s) after unchanged-scope filtering; maxParallelBandTypes={MaxParallelBandTypes}, maxParallelScopes={MaxParallelScopes}, publishParallelism={PublishParallelism}, batchedMemberStatsAggregation={BatchedMemberStatsAggregation}, batchedBySourcePair={BatchedBySourcePair}, scopeTransactions={ScopeTransactions:N0}, derivedScopeCommands={DerivedScopeCommands:N0}, derivedScopeRoundTrips={DerivedScopeRoundTrips:N0}, derivedMemberStatsAggregationPasses={DerivedMemberStatsAggregationPasses:N0}.",
            scopesToRefresh.Length,
            normalizedScopes.Length,
            maxParallelBandTypes,
            maxParallelScopes,
            publishParallelism,
            options.UseBatchedMemberStatsAggregation,
            batchedBySourcePair,
            operationMetrics.SuccessfulScopeTransactions,
            operationMetrics.DerivedSuccessfulScopeCommandExecutions,
            operationMetrics.DerivedSuccessfulScopeRoundTrips,
            operationMetrics.DerivedMemberStatsAggregationPasses);

        return new BandCurrentProjectionIncrementalRefreshResult(
            scopesToRefresh.Length,
            orderedResults.Length,
            failedScopes,
            orderedResults.Sum(static result => result.InsertedRows),
            orderedResults.Sum(static result => result.DeletedRows) + publishResult.DeletedRows + candidateRowsDeleted,
            candidateRowsDeleted,
            publishResult,
            Math.Round(sw.Elapsed.TotalMilliseconds, 3),
            orderedResults,
            operationMetrics);
    }

    internal async Task<BandCurrentProjectionIncrementalRefreshResult>
        RefreshScopesForMaxScoreMaintenanceAsync(
            IReadOnlyCollection<BandCurrentProjectionScopeKey> scopes,
            IMaxScoreMaintenanceLease maintenanceLease,
            BandCurrentProjectionRebuildOptions? options = null,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(maintenanceLease);
        options ??= new BandCurrentProjectionRebuildOptions();
        var normalizedScopes = scopes
            .Select(NormalizeScope)
            .Distinct()
            .OrderBy(static scope => scope.BandType,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(static scope => scope.RankingScope,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(static scope => scope.ScopeComboId,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(static scope => scope.SongId,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedScopes.Length == 0)
        {
            return new BandCurrentProjectionIncrementalRefreshResult(
                0,
                0,
                0,
                0,
                0,
                0,
                BandCurrentProjectionPublishResult.NotPublished(
                    0,
                    0,
                    0,
                    0,
                    0,
                    0),
                0,
                [],
                BandCurrentProjectionOperationMetrics.Empty);
        }

        var sw = Stopwatch.StartNew();
        var scopesToRefresh = options.SkipUnchangedScopes
            ? await FilterScopesNeedingRefreshAsync(
                normalizedScopes,
                ct)
            : normalizedScopes;
        if (scopesToRefresh.Length == 0)
        {
            return new BandCurrentProjectionIncrementalRefreshResult(
                0,
                0,
                0,
                0,
                0,
                0,
                BandCurrentProjectionPublishResult.NotPublished(
                    0,
                    0,
                    0,
                    0,
                    0,
                    0),
                Math.Round(sw.Elapsed.TotalMilliseconds, 3),
                [],
                BandCurrentProjectionOperationMetrics.Empty);
        }

        var generation =
            await maintenanceLease.ExecuteTransactionAsync(
                "derived-band-projection-generation",
                requireSourceLocks: true,
                async (connection, transaction, token) =>
                {
                    await using var command =
                        connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        $"SELECT nextval('{GenerationSequence}'::regclass)";
                    return Convert.ToInt64(
                        await command.ExecuteScalarAsync(token));
                },
                ct: ct);

        var results =
            new List<BandCurrentProjectionScopeResult>(
                scopesToRefresh.Length);
        foreach (var scope in scopesToRefresh)
        {
            ct.ThrowIfCancellationRequested();
            var scopeSw = Stopwatch.StartNew();
            var result =
                await maintenanceLease.ExecuteTransactionAsync(
                    $"derived-band-projection-scope:{scope.BandType}:{scope.RankingScope}:{scope.ScopeComboId}:{scope.SongId}",
                    requireSourceLocks: true,
                    (connection, transaction, token) =>
                        RebuildScopeInTransactionAsync(
                            scope,
                            options,
                            generation,
                            connection,
                            transaction,
                            token),
                    ct: ct);
            scopeSw.Stop();
            results.Add(result with
            {
                ElapsedMs = Math.Round(
                    scopeSw.Elapsed.TotalMilliseconds,
                    3),
            });
        }

        var publishResult = options.PublishOnSuccess
            ? await maintenanceLease.ExecuteTransactionAsync(
                "derived-band-projection-publish",
                requireSourceLocks: true,
                (connection, transaction, token) =>
                    TryPublishGenerationInTransactionAsync(
                        generation,
                        scopesToRefresh,
                        connection,
                        transaction,
                        fullRebuiltAt: null,
                        token),
                ct: ct)
            : BandCurrentProjectionPublishResult.NotPublished(
                generation,
                scopesToRefresh.Length,
                0,
                scopesToRefresh.Length,
                0,
                0);

        var affectedBandTypes = GetAffectedBandTypes(
            options,
            scopesToRefresh,
            includeAllWhenUnfiltered: false);
        var candidateRowsDeleted = options.PublishOnSuccess
            ? await maintenanceLease.ExecuteTransactionAsync(
                "derived-band-projection-cleanup",
                requireSourceLocks: true,
                (connection, transaction, token) =>
                    DeleteUnpublishedCandidateRowsInTransactionAsync(
                        options,
                        affectedBandTypes,
                        connection,
                        transaction,
                        token),
                ct: ct)
            : 0;

        sw.Stop();
        return new BandCurrentProjectionIncrementalRefreshResult(
            scopesToRefresh.Length,
            results.Count,
            0,
            results.Sum(static result => result.InsertedRows),
            results.Sum(static result => result.DeletedRows)
                + publishResult.DeletedRows
                + candidateRowsDeleted,
            candidateRowsDeleted,
            publishResult,
            Math.Round(sw.Elapsed.TotalMilliseconds, 3),
            results,
            CreateOperationMetrics(
                results,
                options));
    }

    /// <summary>
    /// Returns every scope key that already has projection state, including
    /// scopes whose source rows have since disappeared.
    /// </summary>
    public async Task<IReadOnlyList<BandCurrentProjectionScopeKey>> LoadProjectionScopeKeysAsync(
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = $"SELECT song_id, band_type, ranking_scope, scope_combo_id FROM {ScopeTable}";
        var keys = new List<BandCurrentProjectionScopeKey>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            keys.Add(new BandCurrentProjectionScopeKey(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return keys;
    }

    /// <summary>
    /// Applies the unchanged-scope filter to <paramref name="candidates"/> and
    /// returns at most <paramref name="maxScopes"/> scopes that need a rebuild
    /// (all of them when <paramref name="maxScopes"/> is not positive), in the
    /// filter's deterministic order.
    /// </summary>
    public async Task<IReadOnlyList<BandCurrentProjectionScopeKey>> SelectScopesNeedingRefreshAsync(
        IReadOnlyCollection<BandCurrentProjectionScopeKey> candidates,
        int maxScopes,
        CancellationToken ct = default)
    {
        var normalized = candidates
            .Select(static scope => TryNormalizeScope(scope, out var key) ? key : null)
            .OfType<BandCurrentProjectionScopeKey>()
            .Distinct()
            .ToArray();
        if (normalized.Length == 0)
            return [];

        var selected = await FilterScopesNeedingRefreshAsync(normalized, ct);
        return maxScopes > 0 && selected.Length > maxScopes
            ? selected[..maxScopes]
            : selected;
    }

    /// <summary>
    /// Runs the unchanged-scope filter once over <paramref name="impactedScopes"/>
    /// plus the stale-sweep <paramref name="sweepCandidates"/>, returning every
    /// impacted scope that needs a rebuild and at most <paramref name="maxStaleScopes"/>
    /// non-impacted ones, each in the filter's deterministic order. One pass
    /// replaces a sweep filter followed by the refresh's own filter over the
    /// merged set, which read every requested song's band entries twice.
    /// </summary>
    public async Task<BandCurrentProjectionSweepSelection> SelectImpactedAndStaleScopesAsync(
        IReadOnlyCollection<BandCurrentProjectionScopeKey> impactedScopes,
        IReadOnlyCollection<BandCurrentProjectionScopeKey> sweepCandidates,
        int maxStaleScopes,
        CancellationToken ct = default)
    {
        var impacted = impactedScopes
            .Select(static scope => TryNormalizeScope(scope, out var key) ? key : null)
            .OfType<BandCurrentProjectionScopeKey>()
            .ToHashSet();
        var candidates = sweepCandidates
            .Select(static scope => TryNormalizeScope(scope, out var key) ? key : null)
            .OfType<BandCurrentProjectionScopeKey>()
            .Where(scope => !impacted.Contains(scope))
            .Distinct()
            .ToArray();
        var requested = impacted.Concat(candidates).ToArray();
        if (requested.Length == 0)
            return new BandCurrentProjectionSweepSelection([], [], 0);

        var selected = await FilterScopesNeedingRefreshAsync(requested, ct);
        var impactedSelected = selected.Where(impacted.Contains).ToArray();
        var staleSelected = selected.Where(scope => !impacted.Contains(scope));
        return new BandCurrentProjectionSweepSelection(
            impactedSelected,
            maxStaleScopes > 0 ? staleSelected.Take(maxStaleScopes).ToArray() : [],
            candidates.Length);
    }

    private async Task<BandCurrentProjectionScopeKey[]> FilterScopesNeedingRefreshAsync(
        IReadOnlyCollection<BandCurrentProjectionScopeKey> scopes,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var create = conn.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = """
                CREATE TEMP TABLE _band_current_refresh_scopes (
                    song_id TEXT NOT NULL,
                    band_type TEXT NOT NULL,
                    ranking_scope TEXT NOT NULL,
                    scope_combo_id TEXT NOT NULL,
                    PRIMARY KEY (song_id, band_type, ranking_scope, scope_combo_id)
                ) ON COMMIT DROP
                """;
            await create.ExecuteNonQueryAsync(ct);
        }

        await using (var writer = await conn.BeginBinaryImportAsync(
            "COPY _band_current_refresh_scopes (song_id, band_type, ranking_scope, scope_combo_id) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var scope in scopes)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(scope.SongId, NpgsqlTypes.NpgsqlDbType.Text, ct);
                await writer.WriteAsync(scope.BandType, NpgsqlTypes.NpgsqlDbType.Text, ct);
                await writer.WriteAsync(scope.RankingScope, NpgsqlTypes.NpgsqlDbType.Text, ct);
                await writer.WriteAsync(scope.ScopeComboId, NpgsqlTypes.NpgsqlDbType.Text, ct);
            }

            await writer.CompleteAsync(ct);
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        cmd.CommandText = $"""
            WITH {BandComboMapCtes}, requested_sources AS (
                SELECT DISTINCT song_id, band_type
                FROM _band_current_refresh_scopes
            ), entry_combos AS (
                SELECT be.song_id,
                       be.band_type,
                       be.team_key,
                       be.last_updated_at,
                       {BandSongComboIdFromMapSql} AS combo_id
                FROM band_entries be
                JOIN requested_sources rs
                  ON rs.song_id = be.song_id
                 AND rs.band_type = be.band_type
                {BandComboMapJoinSql}
                WHERE NOT be.is_over_threshold
            ), overall_scope AS (
                SELECT song_id,
                       band_type,
                       COUNT(DISTINCT team_key)::BIGINT AS projected_rows,
                       MAX(last_updated_at) AS max_source_updated_at
                FROM entry_combos
                GROUP BY song_id, band_type
            ), combo_scope AS (
                -- Match the rebuild: combo scopes contain only full-size combos.
                SELECT song_id,
                       band_type,
                       combo_id,
                       COUNT(DISTINCT team_key)::BIGINT AS projected_rows,
                       MAX(last_updated_at) AS max_source_updated_at
                FROM entry_combos
                WHERE combo_id <> ''
                  AND array_length(string_to_array(combo_id, '+'), 1) = {ExpectedMemberCountSql}
                GROUP BY song_id, band_type, combo_id
            ), source_scope AS (
                SELECT requested.song_id,
                       requested.band_type,
                       requested.ranking_scope,
                       requested.scope_combo_id,
                       COALESCE(
                           CASE WHEN requested.ranking_scope = 'overall'
                                THEN overall_scope.projected_rows
                                ELSE combo_scope.projected_rows
                           END,
                           0)::BIGINT AS projected_rows,
                       CASE WHEN requested.ranking_scope = 'overall'
                            THEN overall_scope.max_source_updated_at
                            ELSE combo_scope.max_source_updated_at
                       END AS max_source_updated_at
                FROM _band_current_refresh_scopes requested
                LEFT JOIN overall_scope
                  ON requested.ranking_scope = 'overall'
                 AND overall_scope.song_id = requested.song_id
                 AND overall_scope.band_type = requested.band_type
                LEFT JOIN combo_scope
                  ON requested.ranking_scope <> 'overall'
                 AND combo_scope.song_id = requested.song_id
                 AND combo_scope.band_type = requested.band_type
                 AND combo_scope.combo_id = requested.scope_combo_id
            )
            SELECT source_scope.song_id,
                   source_scope.band_type,
                   source_scope.ranking_scope,
                   source_scope.scope_combo_id
            FROM source_scope
            LEFT JOIN {ScopeTable} existing
              ON existing.song_id = source_scope.song_id
             AND existing.band_type = source_scope.band_type
             AND existing.ranking_scope = source_scope.ranking_scope
             AND existing.scope_combo_id = source_scope.scope_combo_id
            WHERE (source_scope.projected_rows = 0
                   AND existing.song_id IS NOT NULL
                   AND (existing.status <> 'ready' OR existing.row_count <> 0))
               -- row_count/last_rebuilt_at describe the latest rebuilt candidate,
               -- not the published generation. A ready candidate that was never
               -- published (interrupted or failed publish) would otherwise look
               -- current forever while readers keep serving the older generation.
               OR (existing.status = 'ready'
                   AND existing.projection_generation IS DISTINCT FROM existing.published_generation
                   AND NOT (existing.row_count = 0 AND existing.published_generation IS NULL))
               OR (source_scope.projected_rows > 0 AND (
                    existing.song_id IS NULL
                    OR existing.status <> 'ready'
                    OR existing.last_rebuilt_at IS NULL
                    OR existing.row_count <> source_scope.projected_rows
                    OR source_scope.max_source_updated_at > existing.last_rebuilt_at
               ))
            ORDER BY source_scope.band_type, source_scope.ranking_scope, source_scope.scope_combo_id, source_scope.song_id
            """;

        var result = new List<BandCurrentProjectionScopeKey>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                result.Add(new BandCurrentProjectionScopeKey(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        await tx.CommitAsync(ct);
        return result.ToArray();
    }

    private async Task<BandCurrentProjectionScopeResult> RebuildScopeAsync(
        BandCurrentProjectionScopeKey scope,
        BandCurrentProjectionRebuildOptions options,
        long generation,
        bool updateGlobalState,
        CancellationToken ct)
    {
        scope = NormalizeScope(scope);
        var sw = Stopwatch.StartNew();

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var result = await RebuildScopeInTransactionAsync(
                scope,
                options,
                generation,
                conn,
                tx,
                ct);
            await tx.CommitAsync(ct);

            if (updateGlobalState)
            {
                if (options.PublishOnSuccess)
                    await TryPublishGenerationAsync(generation, [scope], fullRebuiltAt: null, ct);
                else
                    await RefreshGlobalStateFromScopesAsync(fullRebuiltAt: null, ct);
            }

            sw.Stop();
            return result with
            {
                ElapsedMs =
                    Math.Round(sw.Elapsed.TotalMilliseconds, 3),
            };
        }
        catch (Exception ex)
        {
            await MarkScopeFailedAsync(scope, generation, ex.Message, ct);
            _log.LogError(ex, "Failed to rebuild band current projection scope {SongId}/{BandType}/{RankingScope}/{ScopeComboId}", scope.SongId, scope.BandType, scope.RankingScope, scope.ScopeComboId);
            throw;
        }
    }

    private async Task<IReadOnlyList<BandCurrentProjectionScopeResult>> RebuildPairAsync(
        BandCurrentProjectionScopeKey[] pairScopes,
        BandCurrentProjectionRebuildOptions options,
        long generation,
        CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var results = await RebuildPairInTransactionAsync(
                pairScopes,
                options,
                generation,
                conn,
                tx,
                ct);
            await tx.CommitAsync(ct);
            return results;
        }
        catch (Exception ex)
        {
            foreach (var scope in pairScopes)
                await MarkScopeFailedAsync(scope, generation, ex.Message, ct);
            _log.LogError(
                ex,
                "Failed to rebuild {ScopeCount} band current projection scope(s) of {SongId}/{BandType}",
                pairScopes.Length,
                pairScopes[0].SongId,
                pairScopes[0].BandType);
            throw;
        }
    }

    /// <summary>
    /// Rebuilds every given scope of one (song, band type) in the caller's
    /// transaction. The song's non-over-threshold entries, their combo ids and
    /// their member stats are read once into a temporary table; each scope then
    /// selects, chooses and ranks its rows from it exactly as
    /// <see cref="RebuildScopeInTransactionAsync"/> does from the source tables.
    /// </summary>
    internal static async Task<IReadOnlyList<BandCurrentProjectionScopeResult>>
        RebuildPairInTransactionAsync(
            IReadOnlyList<BandCurrentProjectionScopeKey> pairScopes,
            BandCurrentProjectionRebuildOptions options,
            long generation,
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            CancellationToken ct)
    {
        var scopes = pairScopes.Select(NormalizeScope).Distinct().ToArray();
        if (scopes.Length == 0)
            return [];
        if (scopes.Any(scope =>
                !string.Equals(scope.SongId, scopes[0].SongId, StringComparison.Ordinal)
                || !string.Equals(scope.BandType, scopes[0].BandType, StringComparison.Ordinal)))
        {
            throw new ArgumentException("All scopes of a pair rebuild must share one song and band type.", nameof(pairScopes));
        }

        if (options.DisableSynchronousCommit)
        {
            await using var syncCmd = conn.CreateCommand();
            syncCmd.Transaction = tx;
            syncCmd.CommandText = "SET LOCAL synchronous_commit = off";
            await syncCmd.ExecuteNonQueryAsync(ct);
        }

        // Captured before the source read, as the per-scope path does, so a
        // source row committed after the read is newer than last_rebuilt_at.
        var now = DateTime.UtcNow;
        long sourceRows;
        await using (var sourceCmd = conn.CreateCommand())
        {
            sourceCmd.Transaction = tx;
            ApplyCommandOptions(sourceCmd, options);
            sourceCmd.CommandText = PairSourceSql;
            sourceCmd.Parameters.AddWithValue("songId", scopes[0].SongId);
            sourceCmd.Parameters.AddWithValue("bandType", scopes[0].BandType);
            sourceRows = Convert.ToInt64(await sourceCmd.ExecuteScalarAsync(ct));
        }

        var expectedMembers = BandInstrumentMapping.ExpectedMemberCount(scopes[0].BandType);
        var results = new List<BandCurrentProjectionScopeResult>(scopes.Length);
        foreach (var scope in scopes)
        {
            var scopeSw = Stopwatch.StartNew();
            await using var deleteCmd = conn.CreateCommand();
            deleteCmd.Transaction = tx;
            ApplyCommandOptions(deleteCmd, options);
            deleteCmd.CommandText = $"""
                DELETE FROM {ProjectionTable}
                WHERE song_id = @songId
                  AND band_type = @bandType
                  AND ranking_scope = @rankingScope
                  AND scope_combo_id = @scopeComboId
                  AND projection_generation = @generation
                """;
            AddScopeParameters(deleteCmd, scope);
            deleteCmd.Parameters.AddWithValue("generation", generation);
            var deletedRows = await deleteCmd.ExecuteNonQueryAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            ApplyCommandOptions(cmd, options);
            cmd.CommandText = RebuildScopeFromPairSourceSql;
            AddScopeParameters(cmd, scope);
            cmd.Parameters.AddWithValue("expectedMembers", expectedMembers);
            cmd.Parameters.AddWithValue("generation", generation);
            cmd.Parameters.AddWithValue("now", now);

            long insertedRows = 0;
            var sourceScopeExists = false;
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    insertedRows = reader.GetInt64(0);
                    sourceScopeExists = reader.GetBoolean(1);
                }
            }

            scopeSw.Stop();
            results.Add(new BandCurrentProjectionScopeResult(
                scope.SongId,
                scope.BandType,
                scope.RankingScope,
                scope.ScopeComboId,
                generation,
                insertedRows,
                deletedRows,
                sourceScopeExists,
                results.Count == 0 ? sourceRows : 0,
                Math.Round(scopeSw.Elapsed.TotalMilliseconds, 3)));
        }

        return results;
    }

    /// <summary>
    /// Groups scopes by (song, band type), overall scope first within a pair,
    /// and orders the pairs so band types alternate, largest pairs first.
    /// </summary>
    internal static BandCurrentProjectionScopeKey[][] GroupBySourcePair(
        IReadOnlyList<BandCurrentProjectionScopeKey> scopes)
    {
        var queues = scopes
            .GroupBy(static scope => (scope.SongId, scope.BandType))
            .Select(static group => group
                .OrderBy(static scope =>
                    string.Equals(scope.RankingScope, "overall", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(static scope => scope.ScopeComboId, StringComparer.Ordinal)
                .ToArray())
            .GroupBy(static pair => pair[0].BandType, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => new Queue<BandCurrentProjectionScopeKey[]>(
                group
                    .OrderByDescending(static pair => pair.Length)
                    .ThenBy(static pair => pair[0].SongId, StringComparer.Ordinal)))
            .ToList();
        var ordered = new List<BandCurrentProjectionScopeKey[]>();
        while (queues.Count > 0)
        {
            for (var i = 0; i < queues.Count; i++)
            {
                ordered.Add(queues[i].Dequeue());
                if (queues[i].Count == 0)
                {
                    queues.RemoveAt(i);
                    i--;
                }
            }
        }

        return ordered.ToArray();
    }

    private static async Task<BandCurrentProjectionScopeResult>
        RebuildScopeInTransactionAsync(
            BandCurrentProjectionScopeKey scope,
            BandCurrentProjectionRebuildOptions options,
            long generation,
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            CancellationToken ct)
    {
        if (options.DisableSynchronousCommit)
        {
            await using var syncCmd = conn.CreateCommand();
            syncCmd.Transaction = tx;
            syncCmd.CommandText =
                "SET LOCAL synchronous_commit = off";
            await syncCmd.ExecuteNonQueryAsync(ct);
        }

        await using var deleteCmd = conn.CreateCommand();
        deleteCmd.Transaction = tx;
        ApplyCommandOptions(deleteCmd, options);
        deleteCmd.CommandText = $"""
            DELETE FROM {ProjectionTable}
            WHERE song_id = @songId
              AND band_type = @bandType
              AND ranking_scope = @rankingScope
              AND scope_combo_id = @scopeComboId
              AND projection_generation = @generation
            """;
        AddScopeParameters(deleteCmd, scope);
        deleteCmd.Parameters.AddWithValue("generation", generation);
        var deletedRows =
            await deleteCmd.ExecuteNonQueryAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        ApplyCommandOptions(cmd, options);
        cmd.CommandText = options.UseBatchedMemberStatsAggregation
            ? RebuildScopeBatchedMemberStatsSql
            : RebuildScopeSql;
        AddScopeParameters(cmd, scope);
        cmd.Parameters.AddWithValue(
            "expectedMembers",
            BandInstrumentMapping.ExpectedMemberCount(
                scope.BandType));
        cmd.Parameters.AddWithValue("generation", generation);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);

        long insertedRows = 0;
        var sourceScopeExists = false;
        await using (var reader =
                     await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                insertedRows = reader.GetInt64(0);
                sourceScopeExists = reader.GetBoolean(1);
            }
        }

        return new BandCurrentProjectionScopeResult(
            scope.SongId,
            scope.BandType,
            scope.RankingScope,
            scope.ScopeComboId,
            generation,
            insertedRows,
            deletedRows,
            sourceScopeExists,
            insertedRows * (
                options.UseBatchedMemberStatsAggregation
                    ? 1
                    : LegacyMemberStatsAggregateSubqueriesPerRow),
            ElapsedMs: 0);
    }

    private static BandCurrentProjectionOperationMetrics
        CreateOperationMetrics(
            IReadOnlyCollection<BandCurrentProjectionScopeResult> results,
            BandCurrentProjectionRebuildOptions options,
            bool batchedBySourcePair = false)
    {
        var successfulScopes = results.Count;
        if (batchedBySourcePair)
        {
            var pairs = results
                .Select(static result => (result.SongId, result.BandType))
                .Distinct()
                .LongCount();
            var commands = pairs * (1 + (options.DisableSynchronousCommit ? 1 : 0))
                + successfulScopes * 2L;
            return new BandCurrentProjectionOperationMetrics(
                SuccessfulScopeTransactions: pairs,
                DerivedSuccessfulScopeCommandExecutions: commands,
                DerivedSuccessfulScopeRoundTrips: commands + pairs * 2,
                DerivedMemberStatsAggregationPasses:
                    results.Sum(static result =>
                        result.DerivedMemberStatsAggregationPasses));
        }

        var commandsPerScope =
            2 + (options.DisableSynchronousCommit ? 1 : 0);
        return new BandCurrentProjectionOperationMetrics(
            SuccessfulScopeTransactions: successfulScopes,
            DerivedSuccessfulScopeCommandExecutions:
                successfulScopes * commandsPerScope,
            DerivedSuccessfulScopeRoundTrips:
                successfulScopes * (commandsPerScope + 2),
            DerivedMemberStatsAggregationPasses:
                results.Sum(static result =>
                    result.DerivedMemberStatsAggregationPasses));
    }

    private async Task<long> NextGenerationAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT nextval('{GenerationSequence}'::regclass)";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private async Task ClearProjectionAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = $"TRUNCATE TABLE {ProjectionTable}, {ScopeTable}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<long> DeleteOrphanedProjectionRowsAsync(
        BandCurrentProjectionRebuildOptions options,
        IReadOnlyCollection<BandCurrentProjectionScopeKey> currentScopes,
        IReadOnlyCollection<string> affectedBandTypes,
        CancellationToken ct)
    {
        if (affectedBandTypes.Count == 0)
            return 0;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var create = conn.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = """
                CREATE TEMP TABLE _band_current_orphan_scopes (
                    song_id TEXT NOT NULL,
                    band_type TEXT NOT NULL,
                    ranking_scope TEXT NOT NULL,
                    scope_combo_id TEXT NOT NULL,
                    PRIMARY KEY (song_id, band_type, ranking_scope, scope_combo_id)
                ) ON COMMIT DROP
                """;
            await create.ExecuteNonQueryAsync(ct);
        }

        await using (var writer = await conn.BeginBinaryImportAsync(
            "COPY _band_current_orphan_scopes (song_id, band_type, ranking_scope, scope_combo_id) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var scope in currentScopes.Select(NormalizeScope).Distinct())
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(scope.SongId, NpgsqlTypes.NpgsqlDbType.Text, ct);
                await writer.WriteAsync(scope.BandType, NpgsqlTypes.NpgsqlDbType.Text, ct);
                await writer.WriteAsync(scope.RankingScope, NpgsqlTypes.NpgsqlDbType.Text, ct);
                await writer.WriteAsync(scope.ScopeComboId, NpgsqlTypes.NpgsqlDbType.Text, ct);
            }

            await writer.CompleteAsync(ct);
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        ApplyCommandOptions(cmd, options);
        cmd.CommandText = $"""
            WITH deleted_entries AS (
                DELETE FROM {ProjectionTable} projection
                WHERE projection.band_type = ANY(@affectedBandTypes)
                  AND NOT EXISTS (
                    SELECT 1
                    FROM _band_current_orphan_scopes current_scope
                    WHERE current_scope.song_id = projection.song_id
                      AND current_scope.band_type = projection.band_type
                      AND current_scope.ranking_scope = projection.ranking_scope
                      AND current_scope.scope_combo_id = projection.scope_combo_id
                )
                RETURNING 1
            ), deleted_scopes AS (
                DELETE FROM {ScopeTable} scope
                                WHERE scope.band_type = ANY(@affectedBandTypes)
                                    AND NOT EXISTS (
                    SELECT 1
                    FROM _band_current_orphan_scopes current_scope
                    WHERE current_scope.song_id = scope.song_id
                      AND current_scope.band_type = scope.band_type
                      AND current_scope.ranking_scope = scope.ranking_scope
                      AND current_scope.scope_combo_id = scope.scope_combo_id
                )
                RETURNING 1
            )
            SELECT COUNT(*)::BIGINT FROM deleted_entries
            """;
        cmd.Parameters.AddWithValue("affectedBandTypes", affectedBandTypes.ToArray());
        var deletedRows = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        await RefreshGlobalStateFromScopesAsync(conn, tx, fullRebuiltAt: null, ct);
        await tx.CommitAsync(ct);
        return deletedRows;
    }

    private async Task<long> DeleteUnpublishedCandidateRowsAsync(
        BandCurrentProjectionRebuildOptions options,
        IReadOnlyCollection<string> affectedBandTypes,
        CancellationToken ct)
    {
        if (affectedBandTypes.Count == 0 || options.CandidateCleanupBatchSize <= 0)
            return 0;

        long totalDeleted = 0;
        var batches = 0;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        while (true)
        {
            if (options.CandidateCleanupMaxBatches > 0 && batches >= options.CandidateCleanupMaxBatches)
            {
                _log.LogInformation(
                    "Band current projection candidate cleanup stopped after {BatchCount:N0} batch(es); additional unpublished candidates may remain for {BandTypes}.",
                    batches,
                    string.Join(',', affectedBandTypes));
                break;
            }

            await using var cmd = conn.CreateCommand();
            ApplyCommandOptions(cmd, options);
            cmd.CommandText = $"""
                WITH candidates AS (
                    SELECT projection.song_id,
                           projection.band_type,
                           projection.ranking_scope,
                           projection.scope_combo_id,
                           projection.projection_generation,
                           projection.team_key
                    FROM {ProjectionTable} projection
                    WHERE projection.band_type = ANY(@affectedBandTypes)
                      AND NOT EXISTS (
                          SELECT 1
                          FROM {ScopeTable} scope
                          WHERE scope.song_id = projection.song_id
                            AND scope.band_type = projection.band_type
                            AND scope.ranking_scope = projection.ranking_scope
                            AND scope.scope_combo_id = projection.scope_combo_id
                            AND scope.published_generation = projection.projection_generation
                      )
                      AND NOT EXISTS (
                          SELECT 1
                          FROM {ScopeTable} scope
                          WHERE scope.song_id = projection.song_id
                            AND scope.band_type = projection.band_type
                            AND scope.ranking_scope = projection.ranking_scope
                            AND scope.scope_combo_id = projection.scope_combo_id
                            AND scope.projection_generation = projection.projection_generation
                            AND scope.status = 'ready'
                      )
                    LIMIT @batchSize
                ), deleted AS (
                    DELETE FROM {ProjectionTable} projection
                    USING candidates
                    WHERE projection.song_id = candidates.song_id
                      AND projection.band_type = candidates.band_type
                      AND projection.ranking_scope = candidates.ranking_scope
                      AND projection.scope_combo_id = candidates.scope_combo_id
                      AND projection.projection_generation = candidates.projection_generation
                      AND projection.team_key = candidates.team_key
                    RETURNING 1
                )
                SELECT COUNT(*)::BIGINT FROM deleted
                """;
            cmd.Parameters.AddWithValue("affectedBandTypes", affectedBandTypes.ToArray());
            cmd.Parameters.AddWithValue("batchSize", options.CandidateCleanupBatchSize);

            var deletedRows = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            totalDeleted += deletedRows;
            batches++;
            if (deletedRows < options.CandidateCleanupBatchSize)
                break;
        }

        if (totalDeleted > 0)
        {
            _log.LogInformation(
                "Deleted {DeletedRows:N0} unpublished band current projection candidate row(s) for {BandTypes}.",
                totalDeleted,
                string.Join(',', affectedBandTypes));
        }

        return totalDeleted;
    }

    /// <summary>
    /// Targeted alternative to <see cref="DeleteUnpublishedCandidateRowsAsync"/>
    /// for the per-song publish path. A scope whose rebuild and publish both
    /// committed holds only its published generation: every rebuild writes its
    /// rows and scope state in one transaction, and every publish deletes the
    /// scope's other generations in the publishing transaction. Rows that are
    /// neither published nor the ready candidate can therefore exist only for
    /// unsettled scopes (not ready, or candidate generation different from the
    /// published generation), so only those scopes are probed through the
    /// scope-key index instead of scanning the whole projection.
    /// </summary>
    private async Task<long> DeleteUnpublishedCandidateRowsForUnsettledScopesAsync(
        BandCurrentProjectionRebuildOptions options,
        IReadOnlyCollection<string> affectedBandTypes,
        CancellationToken ct)
    {
        if (affectedBandTypes.Count == 0 || options.CandidateCleanupBatchSize <= 0)
            return 0;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var unsettled = new List<BandCurrentProjectionScopeKey>();
        await using (var select = conn.CreateCommand())
        {
            ApplyCommandOptions(select, options);
            select.CommandText = $"""
                SELECT song_id, band_type, ranking_scope, scope_combo_id
                FROM {ScopeTable}
                WHERE band_type = ANY(@affectedBandTypes)
                  AND (status <> 'ready' OR projection_generation IS DISTINCT FROM published_generation)
                ORDER BY band_type, ranking_scope, scope_combo_id, song_id
                """;
            select.Parameters.AddWithValue("affectedBandTypes", affectedBandTypes.ToArray());
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                unsettled.Add(new BandCurrentProjectionScopeKey(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        long totalDeleted = 0;
        foreach (var chunk in unsettled.Chunk(UnsettledScopeCleanupChunkSize))
        {
            await using var cmd = conn.CreateCommand();
            ApplyCommandOptions(cmd, options);
            cmd.CommandText = $"""
                WITH requested AS (
                    SELECT *
                    FROM unnest(@songIds, @bandTypes, @rankingScopes, @scopeComboIds)
                        AS requested(song_id, band_type, ranking_scope, scope_combo_id)
                ), deleted AS (
                    DELETE FROM {ProjectionTable} projection
                    USING requested
                    JOIN {ScopeTable} scope
                      ON scope.song_id = requested.song_id
                     AND scope.band_type = requested.band_type
                     AND scope.ranking_scope = requested.ranking_scope
                     AND scope.scope_combo_id = requested.scope_combo_id
                    WHERE projection.song_id = requested.song_id
                      AND projection.band_type = requested.band_type
                      AND projection.ranking_scope = requested.ranking_scope
                      AND projection.scope_combo_id = requested.scope_combo_id
                      AND projection.projection_generation IS DISTINCT FROM scope.published_generation
                      AND NOT (projection.projection_generation = scope.projection_generation AND scope.status = 'ready')
                    RETURNING 1
                )
                SELECT COUNT(*)::BIGINT FROM deleted
                """;
            cmd.Parameters.AddWithValue("songIds", chunk.Select(static scope => scope.SongId).ToArray());
            cmd.Parameters.AddWithValue("bandTypes", chunk.Select(static scope => scope.BandType).ToArray());
            cmd.Parameters.AddWithValue("rankingScopes", chunk.Select(static scope => scope.RankingScope).ToArray());
            cmd.Parameters.AddWithValue("scopeComboIds", chunk.Select(static scope => scope.ScopeComboId).ToArray());
            totalDeleted += Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }

        _log.LogInformation(
            "Band current projection unsettled-scope candidate cleanup probed {UnsettledScopes:N0} scope(s) and deleted {DeletedRows:N0} row(s) for {BandTypes}.",
            unsettled.Count,
            totalDeleted,
            string.Join(',', affectedBandTypes));
        return totalDeleted;
    }

    /// <summary>
    /// Publishes <paramref name="generation"/> one song at a time in independent
    /// transactions, up to <paramref name="parallelism"/> at once, largest
    /// songs first. Each transaction flips that song's ready scopes and deletes
    /// their older generations atomically, so a reader never sees a song's
    /// scope without rows and an interruption leaves every song either fully
    /// old or fully new. The caller refreshes the global state once afterwards.
    /// </summary>
    private async Task<BandCurrentProjectionPublishResult> PublishGenerationBySongAsync(
        long generation,
        IReadOnlyCollection<BandCurrentProjectionScopeKey> scopes,
        IReadOnlyCollection<BandCurrentProjectionScopeResult> results,
        int parallelism,
        CancellationToken ct)
    {
        if (scopes.Count == 0)
            return new BandCurrentProjectionPublishResult(generation, true, 0, 0, 0, 0, 0, 0, 0);

        var songWeights = results
            .GroupBy(static result => result.SongId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Sum(static result => result.InsertedRows),
                StringComparer.OrdinalIgnoreCase);
        var songGroups = scopes
            .GroupBy(static scope => scope.SongId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.ToArray())
            .OrderByDescending(group => songWeights.GetValueOrDefault(group[0].SongId))
            .ThenBy(static group => group[0].SongId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var partials = new ConcurrentBag<BandCurrentProjectionPublishResult>();
        // Close the global publication gate before any song commits: the gate
        // compares the scrape publication's band generation with
        // current_generation, which the legacy single transaction advanced
        // atomically with every scope flip. The caller's final state refresh
        // recomputes it from the published scopes.
        try
        {
            await AdvanceGlobalGenerationAsync(generation, ct);
            await Parallel.ForEachAsync(
                songGroups,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
                async (songScopes, innerCt) =>
                    partials.Add(await TryPublishGenerationCoreAsync(
                        generation,
                        songScopes,
                        fullRebuiltAt: null,
                        updateGlobalState: false,
                        logPublished: false,
                        innerCt)));
        }
        catch
        {
            // Reconcile on any failure, including cancellation: the advanced
            // generation must not keep the gate closed when no song (or only
            // some songs) published. Unpublished ready scopes stay selectable.
            try
            {
                await RefreshGlobalStateFromScopesAsync(fullRebuiltAt: null, CancellationToken.None);
            }
            catch (Exception reconcileFailure)
            {
                _log.LogWarning(reconcileFailure, "Failed to reconcile band current projection global state after an interrupted per-song publish.");
            }

            throw;
        }

        var result = new BandCurrentProjectionPublishResult(
            generation,
            partials.Any(static partial => partial.Published),
            scopes.Count,
            partials.Sum(static partial => partial.ReadyScopes),
            partials.Sum(static partial => partial.FailedScopes),
            partials.Sum(static partial => partial.MissingScopes),
            partials.Sum(static partial => partial.PublishedScopes),
            partials.Sum(static partial => partial.PublishedRows),
            partials.Sum(static partial => partial.DeletedRows));

        _log.LogInformation(
            "Published band current projection generation {Generation:N0} in {SongCount:N0} song transaction(s) (parallelism {Parallelism}): {PublishedScopes:N0}/{ScopeCount:N0} scope(s), {PublishedRows:N0} row(s), {DeletedRows:N0} old row(s) deleted.",
            generation,
            songGroups.Length,
            parallelism,
            result.PublishedScopes,
            scopes.Count,
            result.PublishedRows,
            result.DeletedRows);
        return result;
    }

    public Task<BandCurrentProjectionPublishResult> TryPublishGenerationAsync(
        long generation,
        IReadOnlyCollection<BandCurrentProjectionScopeKey> scopes,
        DateTime? fullRebuiltAt = null,
        CancellationToken ct = default) =>
        TryPublishGenerationCoreAsync(
            generation,
            scopes,
            fullRebuiltAt,
            updateGlobalState: true,
            logPublished: true,
            ct);

    private async Task<BandCurrentProjectionPublishResult> TryPublishGenerationCoreAsync(
        long generation,
        IReadOnlyCollection<BandCurrentProjectionScopeKey> scopes,
        DateTime? fullRebuiltAt,
        bool updateGlobalState,
        bool logPublished,
        CancellationToken ct)
    {
        var normalizedScopes = scopes
            .Select(NormalizeScope)
            .Distinct()
            .OrderBy(static scope => scope.BandType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static scope => scope.RankingScope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static scope => scope.ScopeComboId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static scope => scope.SongId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedScopes.Length == 0)
            return new BandCurrentProjectionPublishResult(generation, true, 0, 0, 0, 0, 0, 0, 0);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var create = conn.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = """
                CREATE TEMP TABLE _band_current_publish_scopes (
                    song_id TEXT NOT NULL,
                    band_type TEXT NOT NULL,
                    ranking_scope TEXT NOT NULL,
                    scope_combo_id TEXT NOT NULL,
                    PRIMARY KEY (song_id, band_type, ranking_scope, scope_combo_id)
                ) ON COMMIT DROP
                """;
            await create.ExecuteNonQueryAsync(ct);
        }

        await using (var writer = await conn.BeginBinaryImportAsync(
            "COPY _band_current_publish_scopes (song_id, band_type, ranking_scope, scope_combo_id) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var scope in normalizedScopes)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(scope.SongId, NpgsqlTypes.NpgsqlDbType.Text, ct);
                await writer.WriteAsync(scope.BandType, NpgsqlTypes.NpgsqlDbType.Text, ct);
                await writer.WriteAsync(scope.RankingScope, NpgsqlTypes.NpgsqlDbType.Text, ct);
                await writer.WriteAsync(scope.ScopeComboId, NpgsqlTypes.NpgsqlDbType.Text, ct);
            }

            await writer.CompleteAsync(ct);
        }

        long readyScopes;
        long failedScopes;
        long missingScopes;
        await using (var guard = conn.CreateCommand())
        {
            guard.Transaction = tx;
            guard.CommandText = $"""
                SELECT
                    COUNT(*) FILTER (
                        WHERE scope.status = 'ready'
                          AND scope.projection_generation = @generation
                          AND NOT (scope.row_count = 0 AND scope.published_generation IS NULL)
                    )::BIGINT AS ready_scopes,
                    COUNT(*) FILTER (WHERE scope.status = 'failed' AND scope.projection_generation = @generation)::BIGINT AS failed_scopes,
                    COUNT(*) FILTER (
                        WHERE scope.song_id IS NULL
                           OR scope.projection_generation IS DISTINCT FROM @generation
                           OR scope.status NOT IN ('ready', 'failed')
                           OR (scope.status = 'ready' AND scope.row_count = 0 AND scope.published_generation IS NULL)
                    )::BIGINT AS missing_scopes
                FROM _band_current_publish_scopes requested
                LEFT JOIN {ScopeTable} scope
                  ON scope.song_id = requested.song_id
                 AND scope.band_type = requested.band_type
                 AND scope.ranking_scope = requested.ranking_scope
                 AND scope.scope_combo_id = requested.scope_combo_id
                """;
            guard.Parameters.AddWithValue("generation", generation);

            await using var reader = await guard.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            readyScopes = reader.GetInt64(0);
            failedScopes = reader.GetInt64(1);
            missingScopes = reader.GetInt64(2);
        }

        if (readyScopes == 0)
        {
            await tx.RollbackAsync(ct);
            if (updateGlobalState)
                await RefreshGlobalStateFromScopesAsync(fullRebuiltAt: null, CancellationToken.None);
            return new BandCurrentProjectionPublishResult(generation, false, normalizedScopes.Length, readyScopes, failedScopes, missingScopes, 0, 0, 0);
        }

        long publishedScopes;
        long publishedRows;
        long deletedRows;
        await using (var publish = conn.CreateCommand())
        {
            publish.Transaction = tx;
            publish.CommandTimeout = 0;
            publish.CommandText = $"""
                WITH Published AS (
                    UPDATE {ScopeTable} scope
                    SET published_generation = scope.projection_generation,
                        published_row_count = scope.row_count,
                        updated_at = @now
                    FROM _band_current_publish_scopes requested
                    WHERE scope.song_id = requested.song_id
                      AND scope.band_type = requested.band_type
                      AND scope.ranking_scope = requested.ranking_scope
                      AND scope.scope_combo_id = requested.scope_combo_id
                      AND scope.projection_generation = @generation
                      AND scope.status = 'ready'
                      AND NOT (scope.row_count = 0 AND scope.published_generation IS NULL)
                    RETURNING scope.song_id, scope.band_type, scope.ranking_scope, scope.scope_combo_id, scope.published_row_count
                ), DeletedRows AS (
                    DELETE FROM {ProjectionTable} projection
                    USING Published published
                    WHERE projection.song_id = published.song_id
                      AND projection.band_type = published.band_type
                      AND projection.ranking_scope = published.ranking_scope
                      AND projection.scope_combo_id = published.scope_combo_id
                      AND projection.projection_generation <> @generation
                    RETURNING 1
                )
                  SELECT COUNT(*)::BIGINT,
                      COALESCE(SUM(published_row_count), 0)::BIGINT,
                       (SELECT COUNT(*)::BIGINT FROM DeletedRows)
                FROM Published;
                """;
            publish.Parameters.AddWithValue("generation", generation);
            publish.Parameters.AddWithValue("now", DateTime.UtcNow);

            await using var reader = await publish.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            publishedScopes = reader.GetInt64(0);
            publishedRows = reader.GetInt64(1);
            deletedRows = reader.GetInt64(2);
        }

        if (publishedScopes != readyScopes)
        {
            await tx.RollbackAsync(ct);
            if (updateGlobalState)
                await RefreshGlobalStateFromScopesAsync(fullRebuiltAt: null, CancellationToken.None);
            return new BandCurrentProjectionPublishResult(
                generation,
                false,
                normalizedScopes.Length,
                readyScopes,
                failedScopes,
                missingScopes + Math.Max(0, readyScopes - publishedScopes),
                publishedScopes,
                0,
                0);
        }

        var effectiveFullRebuiltAt = failedScopes == 0 && missingScopes == 0 && publishedScopes == normalizedScopes.Length
            ? fullRebuiltAt
            : null;
        if (updateGlobalState)
            await RefreshGlobalStateFromScopesAsync(conn, tx, effectiveFullRebuiltAt, ct);
        await tx.CommitAsync(ct);

        if (logPublished)
        {
            _log.LogInformation(
                "Published band current projection generation {Generation:N0}: {ReadyScopes:N0}/{ScopeCount:N0} scope(s), {PublishedRows:N0} row(s), {DeletedRows:N0} old row(s) deleted.",
                generation,
                readyScopes,
                normalizedScopes.Length,
                publishedRows,
                deletedRows);
        }

        return new BandCurrentProjectionPublishResult(
            generation,
            publishedScopes > 0,
            normalizedScopes.Length,
            readyScopes,
            failedScopes,
            missingScopes,
            publishedScopes,
            publishedRows,
            deletedRows);
    }

    private async Task<BandCurrentProjectionPublishResult>
        TryPublishGenerationInTransactionAsync(
            long generation,
            IReadOnlyCollection<BandCurrentProjectionScopeKey> scopes,
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            DateTime? fullRebuiltAt,
            CancellationToken ct)
    {
        var normalizedScopes = scopes
            .Select(NormalizeScope)
            .Distinct()
            .OrderBy(static scope => scope.BandType,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(static scope => scope.RankingScope,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(static scope => scope.ScopeComboId,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(static scope => scope.SongId,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedScopes.Length == 0)
        {
            return new BandCurrentProjectionPublishResult(
                generation,
                true,
                0,
                0,
                0,
                0,
                0,
                0,
                0);
        }

        await using (var create = conn.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = """
                CREATE TEMP TABLE _band_current_publish_scopes (
                    song_id TEXT NOT NULL,
                    band_type TEXT NOT NULL,
                    ranking_scope TEXT NOT NULL,
                    scope_combo_id TEXT NOT NULL,
                    PRIMARY KEY (
                        song_id,
                        band_type,
                        ranking_scope,
                        scope_combo_id)
                ) ON COMMIT DROP
                """;
            await create.ExecuteNonQueryAsync(ct);
        }

        await using (var writer =
                     await conn.BeginBinaryImportAsync(
                         "COPY _band_current_publish_scopes (song_id, band_type, ranking_scope, scope_combo_id) FROM STDIN (FORMAT BINARY)",
                         ct))
        {
            foreach (var scope in normalizedScopes)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(
                    scope.SongId,
                    NpgsqlTypes.NpgsqlDbType.Text,
                    ct);
                await writer.WriteAsync(
                    scope.BandType,
                    NpgsqlTypes.NpgsqlDbType.Text,
                    ct);
                await writer.WriteAsync(
                    scope.RankingScope,
                    NpgsqlTypes.NpgsqlDbType.Text,
                    ct);
                await writer.WriteAsync(
                    scope.ScopeComboId,
                    NpgsqlTypes.NpgsqlDbType.Text,
                    ct);
            }
            await writer.CompleteAsync(ct);
        }

        long readyScopes;
        long failedScopes;
        long missingScopes;
        await using (var guard = conn.CreateCommand())
        {
            guard.Transaction = tx;
            guard.CommandText = $"""
                SELECT
                    COUNT(*) FILTER (
                        WHERE scope.status = 'ready'
                          AND scope.projection_generation =
                              @generation
                          AND NOT (
                              scope.row_count = 0
                              AND scope.published_generation IS NULL
                          )
                    )::BIGINT,
                    COUNT(*) FILTER (
                        WHERE scope.status = 'failed'
                          AND scope.projection_generation =
                              @generation
                    )::BIGINT,
                    COUNT(*) FILTER (
                        WHERE scope.song_id IS NULL
                           OR scope.projection_generation
                              IS DISTINCT FROM @generation
                           OR scope.status NOT IN ('ready', 'failed')
                           OR (
                               scope.status = 'ready'
                               AND scope.row_count = 0
                               AND scope.published_generation IS NULL
                           )
                    )::BIGINT
                FROM _band_current_publish_scopes requested
                LEFT JOIN {ScopeTable} scope
                  ON scope.song_id = requested.song_id
                 AND scope.band_type = requested.band_type
                 AND scope.ranking_scope =
                     requested.ranking_scope
                 AND scope.scope_combo_id =
                     requested.scope_combo_id
                """;
            guard.Parameters.AddWithValue(
                "generation",
                generation);
            await using var reader =
                await guard.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            readyScopes = reader.GetInt64(0);
            failedScopes = reader.GetInt64(1);
            missingScopes = reader.GetInt64(2);
        }

        if (readyScopes == 0)
        {
            return new BandCurrentProjectionPublishResult(
                generation,
                false,
                normalizedScopes.Length,
                readyScopes,
                failedScopes,
                missingScopes,
                0,
                0,
                0);
        }

        long publishedScopes;
        long publishedRows;
        long deletedRows;
        await using (var publish = conn.CreateCommand())
        {
            publish.Transaction = tx;
            publish.CommandTimeout = 0;
            publish.CommandText = $"""
                WITH Published AS (
                    UPDATE {ScopeTable} scope
                    SET published_generation =
                            scope.projection_generation,
                        published_row_count = scope.row_count,
                        updated_at = @now
                    FROM _band_current_publish_scopes requested
                    WHERE scope.song_id = requested.song_id
                      AND scope.band_type = requested.band_type
                      AND scope.ranking_scope =
                          requested.ranking_scope
                      AND scope.scope_combo_id =
                          requested.scope_combo_id
                      AND scope.projection_generation =
                          @generation
                      AND scope.status = 'ready'
                      AND NOT (
                          scope.row_count = 0
                          AND scope.published_generation IS NULL
                      )
                    RETURNING
                        scope.song_id,
                        scope.band_type,
                        scope.ranking_scope,
                        scope.scope_combo_id,
                        scope.published_row_count
                ),
                DeletedRows AS (
                    DELETE FROM {ProjectionTable} projection
                    USING Published published
                    WHERE projection.song_id =
                              published.song_id
                      AND projection.band_type =
                              published.band_type
                      AND projection.ranking_scope =
                              published.ranking_scope
                      AND projection.scope_combo_id =
                              published.scope_combo_id
                      AND projection.projection_generation
                              <> @generation
                    RETURNING 1
                )
                SELECT
                    COUNT(*)::BIGINT,
                    COALESCE(
                        SUM(published_row_count),
                        0)::BIGINT,
                    (
                        SELECT COUNT(*)::BIGINT
                        FROM DeletedRows
                    )
                FROM Published
                """;
            publish.Parameters.AddWithValue(
                "generation",
                generation);
            publish.Parameters.AddWithValue(
                "now",
                DateTime.UtcNow);
            await using var reader =
                await publish.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            publishedScopes = reader.GetInt64(0);
            publishedRows = reader.GetInt64(1);
            deletedRows = reader.GetInt64(2);
        }

        if (publishedScopes != readyScopes)
        {
            throw new InvalidOperationException(
                $"Band projection generation {generation} published {publishedScopes:N0}/{readyScopes:N0} ready scopes.");
        }

        var effectiveFullRebuiltAt =
            failedScopes == 0
            && missingScopes == 0
            && publishedScopes == normalizedScopes.Length
                ? fullRebuiltAt
                : null;
        await RefreshGlobalStateFromScopesAsync(
            conn,
            tx,
            effectiveFullRebuiltAt,
            ct);
        return new BandCurrentProjectionPublishResult(
            generation,
            publishedScopes > 0,
            normalizedScopes.Length,
            readyScopes,
            failedScopes,
            missingScopes,
            publishedScopes,
            publishedRows,
            deletedRows);
    }

    private async Task<long>
        DeleteUnpublishedCandidateRowsInTransactionAsync(
            BandCurrentProjectionRebuildOptions options,
            IReadOnlyCollection<string> affectedBandTypes,
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            CancellationToken ct)
    {
        if (affectedBandTypes.Count == 0
            || options.CandidateCleanupBatchSize <= 0)
        {
            return 0;
        }

        long totalDeleted = 0;
        var batches = 0;
        while (true)
        {
            if (options.CandidateCleanupMaxBatches > 0
                && batches >= options.CandidateCleanupMaxBatches)
            {
                break;
            }

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            ApplyCommandOptions(cmd, options);
            cmd.CommandText = $"""
                WITH candidates AS (
                    SELECT
                        projection.song_id,
                        projection.band_type,
                        projection.ranking_scope,
                        projection.scope_combo_id,
                        projection.projection_generation,
                        projection.team_key
                    FROM {ProjectionTable} projection
                    WHERE projection.band_type =
                              ANY(@affectedBandTypes)
                      AND NOT EXISTS (
                          SELECT 1
                          FROM {ScopeTable} scope
                          WHERE scope.song_id =
                                    projection.song_id
                            AND scope.band_type =
                                    projection.band_type
                            AND scope.ranking_scope =
                                    projection.ranking_scope
                            AND scope.scope_combo_id =
                                    projection.scope_combo_id
                            AND scope.published_generation =
                                    projection.projection_generation
                      )
                      AND NOT EXISTS (
                          SELECT 1
                          FROM {ScopeTable} scope
                          WHERE scope.song_id =
                                    projection.song_id
                            AND scope.band_type =
                                    projection.band_type
                            AND scope.ranking_scope =
                                    projection.ranking_scope
                            AND scope.scope_combo_id =
                                    projection.scope_combo_id
                            AND scope.projection_generation =
                                    projection.projection_generation
                            AND scope.status = 'ready'
                      )
                    LIMIT @batchSize
                ),
                deleted AS (
                    DELETE FROM {ProjectionTable} projection
                    USING candidates
                    WHERE projection.song_id =
                              candidates.song_id
                      AND projection.band_type =
                              candidates.band_type
                      AND projection.ranking_scope =
                              candidates.ranking_scope
                      AND projection.scope_combo_id =
                              candidates.scope_combo_id
                      AND projection.projection_generation =
                              candidates.projection_generation
                      AND projection.team_key =
                              candidates.team_key
                    RETURNING 1
                )
                SELECT COUNT(*)::BIGINT FROM deleted
                """;
            cmd.Parameters.AddWithValue(
                "affectedBandTypes",
                affectedBandTypes.ToArray());
            cmd.Parameters.AddWithValue(
                "batchSize",
                options.CandidateCleanupBatchSize);
            var deletedRows = Convert.ToInt64(
                await cmd.ExecuteScalarAsync(ct));
            totalDeleted += deletedRows;
            batches++;
            if (deletedRows < options.CandidateCleanupBatchSize)
                break;
        }
        return totalDeleted;
    }

    private async Task RefreshGlobalStateFromScopesAsync(DateTime? fullRebuiltAt, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await RefreshGlobalStateFromScopesAsync(conn, null, fullRebuiltAt, ct);
    }

    private static async Task RefreshGlobalStateFromScopesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, DateTime? fullRebuiltAt, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        cmd.CommandText = $"""
            INSERT INTO {StateTable}
            (id, current_generation, row_count, scope_count, failed_scope_count, full_rebuilt_at, last_scope_rebuilt_at, updated_at)
            SELECT TRUE,
                   COALESCE((SELECT MAX(published_generation) FROM {ScopeTable} WHERE published_generation IS NOT NULL), 0),
                   COALESCE((SELECT SUM(published_row_count)::BIGINT FROM {ScopeTable} WHERE published_generation IS NOT NULL), 0),
                   (SELECT COUNT(*)::BIGINT FROM {ScopeTable}),
                   (SELECT COUNT(*)::BIGINT FROM {ScopeTable} WHERE status = 'failed'),
                   COALESCE(@fullRebuiltAt, (SELECT full_rebuilt_at FROM {StateTable} WHERE id = TRUE)),
                   (SELECT MAX(last_rebuilt_at) FROM {ScopeTable} WHERE published_generation IS NOT NULL),
                   @now
            ON CONFLICT (id) DO UPDATE SET
                current_generation = EXCLUDED.current_generation,
                row_count = EXCLUDED.row_count,
                scope_count = EXCLUDED.scope_count,
                failed_scope_count = EXCLUDED.failed_scope_count,
                full_rebuilt_at = EXCLUDED.full_rebuilt_at,
                last_scope_rebuilt_at = EXCLUDED.last_scope_rebuilt_at,
                updated_at = EXCLUDED.updated_at
            """;
        cmd.Parameters.AddWithValue("fullRebuiltAt", fullRebuiltAt.HasValue ? fullRebuiltAt.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task AdvanceGlobalGenerationAsync(long generation, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {StateTable} (id, current_generation, updated_at)
            VALUES (TRUE, @generation, @now)
            ON CONFLICT (id) DO UPDATE SET
                current_generation = GREATEST({StateTable}.current_generation, EXCLUDED.current_generation),
                updated_at = EXCLUDED.updated_at
            """;
        cmd.Parameters.AddWithValue("generation", generation);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task MarkScopeFailedAsync(BandCurrentProjectionScopeKey scope, long generation, string errorMessage, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(CancellationToken.None);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO {ScopeTable}
                (song_id, band_type, ranking_scope, scope_combo_id, projection_generation, row_count, status, error_message, updated_at)
                VALUES (@songId, @bandType, @rankingScope, @scopeComboId, @generation, 0, 'failed', @errorMessage, @now)
                ON CONFLICT (song_id, band_type, ranking_scope, scope_combo_id) DO UPDATE SET
                    projection_generation = EXCLUDED.projection_generation,
                    status = EXCLUDED.status,
                    error_message = EXCLUDED.error_message,
                    updated_at = EXCLUDED.updated_at
                """;
            AddScopeParameters(cmd, scope);
            cmd.Parameters.AddWithValue("generation", generation);
            cmd.Parameters.AddWithValue("errorMessage", errorMessage);
            cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            await RefreshGlobalStateFromScopesAsync(fullRebuiltAt: null, CancellationToken.None);
        }
        catch (Exception failure)
        {
            _log.LogWarning(failure, "Failed to mark band current projection scope {SongId}/{BandType}/{RankingScope}/{ScopeComboId} as failed", scope.SongId, scope.BandType, scope.RankingScope, scope.ScopeComboId);
        }
    }

    private static void AddScopeParameters(NpgsqlCommand cmd, BandCurrentProjectionScopeKey scope)
    {
        cmd.Parameters.AddWithValue("songId", scope.SongId);
        cmd.Parameters.AddWithValue("bandType", scope.BandType);
        cmd.Parameters.AddWithValue("rankingScope", scope.RankingScope);
        cmd.Parameters.AddWithValue("scopeComboId", scope.ScopeComboId);
    }

    /// <summary>
    /// Orders scopes for the parallel refresh: band types alternate, and within
    /// each band type the song-wide <c>overall</c> scopes (each spanning every
    /// combo of the song, roughly ten times a single combo scope's rows) start
    /// first so the largest transactions never form a single-worker tail.
    /// Relative order is otherwise preserved.
    /// </summary>
    internal static BandCurrentProjectionScopeKey[] InterleaveByBandType(
        IReadOnlyList<BandCurrentProjectionScopeKey> scopes)
    {
        var queues = scopes
            .GroupBy(static scope => scope.BandType, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => new Queue<BandCurrentProjectionScopeKey>(
                group.OrderBy(static scope =>
                    string.Equals(scope.RankingScope, "overall", StringComparison.OrdinalIgnoreCase) ? 0 : 1)))
            .ToList();
        var ordered = new List<BandCurrentProjectionScopeKey>(scopes.Count);
        while (queues.Count > 0)
        {
            for (var i = 0; i < queues.Count; i++)
                ordered.Add(queues[i].Dequeue());
            queues.RemoveAll(static queue => queue.Count == 0);
        }

        return [.. ordered];
    }

    private static bool TryNormalizeScope(
        BandCurrentProjectionScopeKey scope,
        out BandCurrentProjectionScopeKey normalized)
    {
        try
        {
            normalized = NormalizeScope(scope);
            return true;
        }
        catch (ArgumentException)
        {
            normalized = scope;
            return false;
        }
    }

    private static BandCurrentProjectionScopeKey NormalizeScope(BandCurrentProjectionScopeKey scope)
    {
        if (string.IsNullOrWhiteSpace(scope.SongId))
            throw new ArgumentException("Song id is required.", nameof(scope));
        if (!BandComboIds.IsValidBandType(scope.BandType))
            throw new ArgumentOutOfRangeException(nameof(scope), scope.BandType, "Unsupported band type.");

        var rankingScope = string.IsNullOrWhiteSpace(scope.RankingScope) ? "overall" : scope.RankingScope.Trim().ToLowerInvariant();
        if (rankingScope is not "overall" and not "combo")
            throw new ArgumentOutOfRangeException(nameof(scope), scope.RankingScope, "Ranking scope must be overall or combo.");

        var scopeComboId = string.Empty;
        if (rankingScope == "combo")
        {
            var normalized = BandComboIds.TryNormalizeForBandType(scope.BandType, scope.ScopeComboId);
            if (normalized.Error is not null || string.IsNullOrWhiteSpace(normalized.ComboId))
                throw new ArgumentException(normalized.Error ?? "Combo scope requires a combo id.", nameof(scope));
            scopeComboId = normalized.ComboId;
        }

        return new BandCurrentProjectionScopeKey(scope.SongId.Trim(), scope.BandType.Trim(), rankingScope, scopeComboId);
    }

    private static IReadOnlyList<string> NormalizeBandTypes(IReadOnlyCollection<string>? bandTypes)
    {
        if (bandTypes is null || bandTypes.Count == 0)
            return [];

        var result = new List<string>();
        foreach (var bandType in bandTypes)
        {
            if (!BandComboIds.IsValidBandType(bandType))
                throw new ArgumentOutOfRangeException(nameof(bandTypes), bandType, "Unsupported band type.");
            if (!result.Contains(bandType, StringComparer.OrdinalIgnoreCase))
                result.Add(bandType);
        }

        return result;
    }

    private static IReadOnlyList<string> GetAffectedBandTypes(
        BandCurrentProjectionRebuildOptions options,
        IReadOnlyCollection<BandCurrentProjectionScopeKey> scopes,
        bool includeAllWhenUnfiltered)
    {
        var fromOptions = NormalizeBandTypes(options.BandTypes);
        if (fromOptions.Count > 0)
            return fromOptions;

        if (includeAllWhenUnfiltered)
            return BandInstrumentMapping.AllBandTypes;

        return scopes
            .Select(static scope => scope.BandType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static bandType => bandType, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TableExists(NpgsqlConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT to_regclass(@tableName) IS NOT NULL";
        cmd.Parameters.AddWithValue("tableName", $"public.{tableName}");
        return cmd.ExecuteScalar() is bool exists && exists;
    }

    private static void ApplyCommandOptions(NpgsqlCommand cmd, BandCurrentProjectionRebuildOptions options)
    {
        cmd.CommandTimeout = options.CommandTimeoutSeconds <= 0 ? 0 : options.CommandTimeoutSeconds;
    }

    private const string BandSongComboIdExpression = @"
        COALESCE((
            SELECT string_agg(mapped.instrument, '+' ORDER BY mapped.sort_order, mapped.instrument)
            FROM (
                SELECT
                    CASE part::INT
                        WHEN 0 THEN 'Solo_Guitar'
                        WHEN 1 THEN 'Solo_Bass'
                        WHEN 3 THEN 'Solo_Drums'
                        WHEN 2 THEN 'Solo_Vocals'
                        WHEN 4 THEN 'Solo_PeripheralGuitar'
                        WHEN 5 THEN 'Solo_PeripheralBass'
                        WHEN 7 THEN 'Solo_PeripheralVocals'
                        WHEN 8 THEN 'Solo_PeripheralCymbals'
                        WHEN 6 THEN 'Solo_PeripheralDrums'
                        ELSE NULL
                    END AS instrument,
                    CASE part::INT
                        WHEN 0 THEN 0
                        WHEN 1 THEN 1
                        WHEN 3 THEN 2
                        WHEN 2 THEN 3
                        WHEN 4 THEN 4
                        WHEN 5 THEN 5
                        WHEN 7 THEN 6
                        WHEN 8 THEN 7
                        WHEN 6 THEN 8
                        ELSE 999
                    END AS sort_order
                FROM unnest(string_to_array(be.instrument_combo, ':')) AS parts(part)
            ) mapped
            WHERE mapped.instrument IS NOT NULL
        ), '')";

    // A combo id depends only on instrument_combo, so statements that scan many
    // band entries look it up in a map evaluated once per statement over every
    // combo of one to four instrument ids from 0 to ComboMapMaxInstrumentId (plus
    // the empty combo) instead of evaluating the expression for every entry.
    // Values outside that domain fall back to the expression.
    internal const int ComboMapMaxInstrumentId = 10;

    private static readonly string BandComboMapCtes = $"""
        band_combo_parts AS (
            SELECT part::TEXT AS part
            FROM generate_series(0, {ComboMapMaxInstrumentId}) AS part
        ), band_combo_domain AS (
            SELECT ''::TEXT AS instrument_combo
            UNION ALL
            SELECT a.part FROM band_combo_parts a
            UNION ALL
            SELECT a.part || ':' || b.part
            FROM band_combo_parts a CROSS JOIN band_combo_parts b
            UNION ALL
            SELECT a.part || ':' || b.part || ':' || c.part
            FROM band_combo_parts a CROSS JOIN band_combo_parts b CROSS JOIN band_combo_parts c
            UNION ALL
            SELECT a.part || ':' || b.part || ':' || c.part || ':' || d.part
            FROM band_combo_parts a CROSS JOIN band_combo_parts b CROSS JOIN band_combo_parts c CROSS JOIN band_combo_parts d
        ), band_combo_map AS MATERIALIZED (
            SELECT be.instrument_combo, {BandSongComboIdExpression} AS combo_id
            FROM band_combo_domain be
        )
        """;

    private const string BandComboMapJoinSql =
        "LEFT JOIN band_combo_map ON band_combo_map.instrument_combo = be.instrument_combo";

    private const string BandSongComboIdFromMapSql =
        "CASE WHEN band_combo_map.instrument_combo IS NOT NULL THEN band_combo_map.combo_id ELSE "
        + BandSongComboIdExpression + " END";

    internal static string GetComboIdComparisonSqlForTesting() => $"""
        WITH {BandComboMapCtes}, be AS (
            SELECT unnest(@combos::TEXT[]) AS instrument_combo
        )
        SELECT be.instrument_combo,
               {BandSongComboIdExpression} AS expression_combo_id,
               {BandSongComboIdFromMapSql} AS mapped_combo_id,
               band_combo_map.instrument_combo IS NOT NULL AS in_map
        FROM be
        {BandComboMapJoinSql}
        """;

    private const string RebuildScopeSqlTemplate = $"""
        WITH NormalizedEntries AS (
            SELECT
                be.song_id,
                be.band_type,
                be.team_key,
                be.instrument_combo,
                be.team_members,
                be.score,
                be.accuracy,
                be.is_full_combo,
                be.stars,
                be.difficulty,
                be.season,
                COALESCE(be.end_time, '') AS end_time_sort,
                be.first_seen_at,
                be.last_updated_at,
                {BandSongComboIdExpression} AS combo_id
            FROM band_entries be
            WHERE be.song_id = @songId
              AND be.band_type = @bandType
              AND NOT be.is_over_threshold
        ), ScopedEntries AS (
            SELECT *
            FROM NormalizedEntries
            WHERE @rankingScope = 'overall'
               OR (
                    @rankingScope = 'combo'
                    AND combo_id = @scopeComboId
                    AND combo_id <> ''
                    AND array_length(string_to_array(combo_id, '+'), 1) = @expectedMembers
               )
        ), SourceScope AS (
            SELECT EXISTS (SELECT 1 FROM ScopedEntries) AS exists
        ), ChosenEntries AS (
            SELECT *
            FROM (
                SELECT
                    scoped.*,
                    ROW_NUMBER() OVER (
                        PARTITION BY scoped.team_key
                        ORDER BY scoped.score DESC, scoped.end_time_sort ASC, scoped.combo_id ASC, scoped.instrument_combo ASC, scoped.team_key ASC
                    ) AS choice_rank
                FROM ScopedEntries scoped
            ) ranked
            WHERE choice_rank = 1
        ), RankedRows AS (
            SELECT
                @songId AS song_id,
                @bandType AS band_type,
                @rankingScope AS ranking_scope,
                @scopeComboId AS scope_combo_id,
                team_key,
                combo_id AS entry_combo_id,
                instrument_combo AS entry_instrument_combo,
                team_members,
                __MEMBER_STATS_PROJECTION__
                score,
                accuracy,
                is_full_combo,
                stars,
                difficulty,
                season,
                (ROW_NUMBER() OVER (ORDER BY score DESC, end_time_sort ASC, team_key ASC))::INTEGER AS rank,
                (COUNT(*) OVER ())::INTEGER AS total_entries,
                NULLIF(end_time_sort, '') AS end_time,
                first_seen_at,
                last_updated_at
            FROM ChosenEntries
            __MEMBER_STATS_JOIN__
        ), Inserted AS (
            INSERT INTO current_band_leaderboard_entries
            (song_id, band_type, ranking_scope, scope_combo_id, team_key, entry_combo_id, entry_instrument_combo,
                  team_members, member_account_ids, member_instrument_ids, member_scores, member_accuracies, member_full_combos,
                  member_stars, member_difficulties, score, accuracy, is_full_combo, stars, difficulty, season, rank, total_entries,
             percentile, end_time, first_seen_at, last_updated_at, projection_generation, computed_at)
            SELECT song_id, band_type, ranking_scope, scope_combo_id, team_key, entry_combo_id, entry_instrument_combo,
                     team_members, member_account_ids, member_instrument_ids, member_scores, member_accuracies, member_full_combos,
                     member_stars, member_difficulties, score, accuracy, is_full_combo, stars, difficulty, season, rank, total_entries,
                   (rank::DOUBLE PRECISION / NULLIF(total_entries, 0)) * 100.0, end_time, first_seen_at, last_updated_at,
                   @generation, @now
            FROM RankedRows
            WHERE (SELECT exists FROM SourceScope)
            RETURNING 1
        ), ScopeUpsert AS (
            INSERT INTO band_current_projection_scope
            (song_id, band_type, ranking_scope, scope_combo_id, projection_generation, row_count, status, error_message, last_rebuilt_at, updated_at)
            SELECT @songId,
                   @bandType,
                   @rankingScope,
                   @scopeComboId,
                   @generation,
                   (SELECT COUNT(*)::BIGINT FROM Inserted),
                   'ready',
                   NULL,
                   @now,
                   @now
            ON CONFLICT (song_id, band_type, ranking_scope, scope_combo_id) DO UPDATE SET
                projection_generation = EXCLUDED.projection_generation,
                row_count = EXCLUDED.row_count,
                status = EXCLUDED.status,
                error_message = EXCLUDED.error_message,
                last_rebuilt_at = EXCLUDED.last_rebuilt_at,
                updated_at = EXCLUDED.updated_at
            RETURNING row_count
        )
        SELECT (SELECT COUNT(*)::BIGINT FROM Inserted),
               (SELECT exists FROM SourceScope)
        """;

    private const string LegacyMemberStatsProjectionSql = """
        COALESCE((
            SELECT ARRAY_AGG(bms.account_id ORDER BY bms.member_index)
            FROM band_member_stats bms
            WHERE bms.song_id = ChosenEntries.song_id
              AND bms.band_type = ChosenEntries.band_type
              AND bms.team_key = ChosenEntries.team_key
              AND bms.instrument_combo = ChosenEntries.instrument_combo
        ), ARRAY[]::TEXT[]) AS member_account_ids,
        COALESCE((
            SELECT ARRAY_AGG(COALESCE(bms.instrument_id, -1) ORDER BY bms.member_index)
            FROM band_member_stats bms
            WHERE bms.song_id = ChosenEntries.song_id
              AND bms.band_type = ChosenEntries.band_type
              AND bms.team_key = ChosenEntries.team_key
              AND bms.instrument_combo = ChosenEntries.instrument_combo
        ), ARRAY[]::INTEGER[]) AS member_instrument_ids,
        COALESCE((
            SELECT ARRAY_AGG(COALESCE(bms.score, -1) ORDER BY bms.member_index)
            FROM band_member_stats bms
            WHERE bms.song_id = ChosenEntries.song_id
              AND bms.band_type = ChosenEntries.band_type
              AND bms.team_key = ChosenEntries.team_key
              AND bms.instrument_combo = ChosenEntries.instrument_combo
        ), ARRAY[]::INTEGER[]) AS member_scores,
        COALESCE((
            SELECT ARRAY_AGG(COALESCE(bms.accuracy, -1) ORDER BY bms.member_index)
            FROM band_member_stats bms
            WHERE bms.song_id = ChosenEntries.song_id
              AND bms.band_type = ChosenEntries.band_type
              AND bms.team_key = ChosenEntries.team_key
              AND bms.instrument_combo = ChosenEntries.instrument_combo
        ), ARRAY[]::INTEGER[]) AS member_accuracies,
        COALESCE((
            SELECT ARRAY_AGG(
                CASE
                    WHEN bms.is_full_combo IS TRUE THEN 1
                    WHEN bms.is_full_combo IS FALSE THEN 0
                    ELSE -1
                END
                ORDER BY bms.member_index)
            FROM band_member_stats bms
            WHERE bms.song_id = ChosenEntries.song_id
              AND bms.band_type = ChosenEntries.band_type
              AND bms.team_key = ChosenEntries.team_key
              AND bms.instrument_combo = ChosenEntries.instrument_combo
        ), ARRAY[]::INTEGER[]) AS member_full_combos,
        COALESCE((
            SELECT ARRAY_AGG(COALESCE(bms.stars, -1) ORDER BY bms.member_index)
            FROM band_member_stats bms
            WHERE bms.song_id = ChosenEntries.song_id
              AND bms.band_type = ChosenEntries.band_type
              AND bms.team_key = ChosenEntries.team_key
              AND bms.instrument_combo = ChosenEntries.instrument_combo
        ), ARRAY[]::INTEGER[]) AS member_stars,
        COALESCE((
            SELECT ARRAY_AGG(COALESCE(bms.difficulty, -1) ORDER BY bms.member_index)
            FROM band_member_stats bms
            WHERE bms.song_id = ChosenEntries.song_id
              AND bms.band_type = ChosenEntries.band_type
              AND bms.team_key = ChosenEntries.team_key
              AND bms.instrument_combo = ChosenEntries.instrument_combo
        ), ARRAY[]::INTEGER[]) AS member_difficulties,
        """;

    private const string BatchedMemberStatsProjectionSql = """
        member_stats.member_account_ids,
        member_stats.member_instrument_ids,
        member_stats.member_scores,
        member_stats.member_accuracies,
        member_stats.member_full_combos,
        member_stats.member_stars,
        member_stats.member_difficulties,
        """;

    private const string BatchedMemberStatsJoinSql = """
        LEFT JOIN LATERAL (
            SELECT
                COALESCE(
                    ARRAY_AGG(bms.account_id ORDER BY bms.member_index),
                    ARRAY[]::TEXT[]) AS member_account_ids,
                COALESCE(
                    ARRAY_AGG(COALESCE(bms.instrument_id, -1) ORDER BY bms.member_index),
                    ARRAY[]::INTEGER[]) AS member_instrument_ids,
                COALESCE(
                    ARRAY_AGG(COALESCE(bms.score, -1) ORDER BY bms.member_index),
                    ARRAY[]::INTEGER[]) AS member_scores,
                COALESCE(
                    ARRAY_AGG(COALESCE(bms.accuracy, -1) ORDER BY bms.member_index),
                    ARRAY[]::INTEGER[]) AS member_accuracies,
                COALESCE(
                    ARRAY_AGG(
                        CASE
                            WHEN bms.is_full_combo IS TRUE THEN 1
                            WHEN bms.is_full_combo IS FALSE THEN 0
                            ELSE -1
                        END
                        ORDER BY bms.member_index),
                    ARRAY[]::INTEGER[]) AS member_full_combos,
                COALESCE(
                    ARRAY_AGG(COALESCE(bms.stars, -1) ORDER BY bms.member_index),
                    ARRAY[]::INTEGER[]) AS member_stars,
                COALESCE(
                    ARRAY_AGG(COALESCE(bms.difficulty, -1) ORDER BY bms.member_index),
                    ARRAY[]::INTEGER[]) AS member_difficulties
            FROM band_member_stats bms
            WHERE bms.song_id = ChosenEntries.song_id
              AND bms.band_type = ChosenEntries.band_type
              AND bms.team_key = ChosenEntries.team_key
              AND bms.instrument_combo = ChosenEntries.instrument_combo
        ) member_stats ON TRUE
        """;

    // The pair source materializes NormalizedEntries plus each entry's member
    // stats (the same lateral aggregate the batched scope path uses) once per
    // (song, band type).
    private static readonly string PairSourceSql = $"""
        CREATE TEMP TABLE _band_pair_source ON COMMIT DROP AS
        SELECT
            be.song_id,
            be.band_type,
            be.team_key,
            be.instrument_combo,
            be.team_members,
            be.score,
            be.accuracy,
            be.is_full_combo,
            be.stars,
            be.difficulty,
            be.season,
            COALESCE(be.end_time, '') AS end_time_sort,
            be.first_seen_at,
            be.last_updated_at,
            {BandSongComboIdExpression} AS combo_id,
            member_stats.member_account_ids,
            member_stats.member_instrument_ids,
            member_stats.member_scores,
            member_stats.member_accuracies,
            member_stats.member_full_combos,
            member_stats.member_stars,
            member_stats.member_difficulties
        FROM band_entries be
        {BatchedMemberStatsJoinSql.Replace("ChosenEntries.", "be.", StringComparison.Ordinal)}
        WHERE be.song_id = @songId
          AND be.band_type = @bandType
          AND NOT be.is_over_threshold;
        SELECT COUNT(*)::BIGINT FROM _band_pair_source;
        """;

    private const string PairSourceMemberStatsProjectionSql = """
        member_account_ids,
        member_instrument_ids,
        member_scores,
        member_accuracies,
        member_full_combos,
        member_stars,
        member_difficulties,
        """;

    private static readonly string RebuildScopeFromPairSourceSql =
        BuildRebuildScopeFromPairSourceSql();

    internal static string GetRebuildScopeFromPairSourceSqlForTesting() =>
        RebuildScopeFromPairSourceSql;

    internal static string GetPairSourceSqlForTesting() =>
        PairSourceSql;

    private static string BuildRebuildScopeFromPairSourceSql()
    {
        const string scopedEntriesMarker = "), ScopedEntries AS (";
        var template = BuildRebuildScopeSql(
            PairSourceMemberStatsProjectionSql,
            string.Empty);
        var markerIndex = template.IndexOf(scopedEntriesMarker, StringComparison.Ordinal);
        if (!template.StartsWith("WITH NormalizedEntries AS (", StringComparison.Ordinal) || markerIndex < 0)
            throw new InvalidOperationException("The band projection rebuild template no longer starts with NormalizedEntries.");
        return "WITH NormalizedEntries AS (\n    SELECT * FROM _band_pair_source\n" + template[markerIndex..];
    }

    private static readonly string RebuildScopeSql =
        BuildRebuildScopeSql(
            LegacyMemberStatsProjectionSql,
            string.Empty);

    private static readonly string RebuildScopeBatchedMemberStatsSql =
        BuildRebuildScopeSql(
            BatchedMemberStatsProjectionSql,
            BatchedMemberStatsJoinSql);

    internal static string GetRebuildScopeSqlForTesting(
        bool useBatchedMemberStatsAggregation) =>
        useBatchedMemberStatsAggregation
            ? RebuildScopeBatchedMemberStatsSql
            : RebuildScopeSql;

    private static string BuildRebuildScopeSql(
        string memberStatsProjection,
        string memberStatsJoin) =>
        RebuildScopeSqlTemplate
            .Replace(
                "__MEMBER_STATS_PROJECTION__",
                memberStatsProjection,
                StringComparison.Ordinal)
            .Replace(
                "__MEMBER_STATS_JOIN__",
                memberStatsJoin,
                StringComparison.Ordinal);

    private const string ProjectionSchemaSql = """
        CREATE SEQUENCE IF NOT EXISTS band_current_projection_generation_seq;

        CREATE TABLE IF NOT EXISTS current_band_leaderboard_entries (
            song_id                TEXT             NOT NULL,
            band_type              TEXT             NOT NULL,
            ranking_scope          TEXT             NOT NULL DEFAULT 'overall',
            scope_combo_id         TEXT             NOT NULL DEFAULT '',
            team_key               TEXT             NOT NULL,
            entry_combo_id         TEXT             NOT NULL DEFAULT '',
            entry_instrument_combo TEXT             NOT NULL DEFAULT '',
            team_members           TEXT[]           NOT NULL,
            member_account_ids     TEXT[]           NOT NULL DEFAULT ARRAY[]::TEXT[],
            member_instrument_ids  INTEGER[]        NOT NULL DEFAULT ARRAY[]::INTEGER[],
            member_scores          INTEGER[]        NOT NULL DEFAULT ARRAY[]::INTEGER[],
            member_accuracies      INTEGER[]        NOT NULL DEFAULT ARRAY[]::INTEGER[],
            member_full_combos     INTEGER[]        NOT NULL DEFAULT ARRAY[]::INTEGER[],
            member_stars           INTEGER[]        NOT NULL DEFAULT ARRAY[]::INTEGER[],
            member_difficulties    INTEGER[]        NOT NULL DEFAULT ARRAY[]::INTEGER[],
            score                  INTEGER          NOT NULL,
            accuracy               INTEGER,
            is_full_combo          BOOLEAN,
            stars                  INTEGER,
            difficulty             INTEGER,
            season                 INTEGER,
            rank                   INTEGER          NOT NULL DEFAULT 0,
            total_entries          INTEGER          NOT NULL DEFAULT 0,
            percentile             DOUBLE PRECISION NOT NULL DEFAULT 0,
            end_time               TEXT,
            first_seen_at          TIMESTAMPTZ      NOT NULL,
            last_updated_at        TIMESTAMPTZ      NOT NULL,
            projection_generation  BIGINT           NOT NULL DEFAULT 0,
            computed_at            TIMESTAMPTZ      NOT NULL,
            PRIMARY KEY (song_id, band_type, ranking_scope, scope_combo_id, projection_generation, team_key)
        ) PARTITION BY LIST (band_type);

        DO $$
        DECLARE
            key_columns TEXT[];
        BEGIN
            SELECT array_agg(att.attname ORDER BY ord.ordinality)
            INTO key_columns
            FROM pg_constraint con
            JOIN unnest(con.conkey) WITH ORDINALITY AS ord(attnum, ordinality) ON TRUE
            JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = ord.attnum
            WHERE con.conrelid = 'current_band_leaderboard_entries'::regclass
              AND con.contype = 'p'
              AND con.conname = 'current_band_leaderboard_entries_pkey';

            IF key_columns IS NOT NULL AND key_columns <> ARRAY['song_id', 'band_type', 'ranking_scope', 'scope_combo_id', 'projection_generation', 'team_key'] THEN
                ALTER TABLE current_band_leaderboard_entries DROP CONSTRAINT current_band_leaderboard_entries_pkey;
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid = 'current_band_leaderboard_entries'::regclass
                  AND contype = 'p'
                  AND conname = 'current_band_leaderboard_entries_pkey'
            ) THEN
                ALTER TABLE current_band_leaderboard_entries
                    ADD CONSTRAINT current_band_leaderboard_entries_pkey
                    PRIMARY KEY (song_id, band_type, ranking_scope, scope_combo_id, projection_generation, team_key);
            END IF;
        END $$;

        CREATE TABLE IF NOT EXISTS current_band_leaderboard_entries_duets PARTITION OF current_band_leaderboard_entries FOR VALUES IN ('Band_Duets');
        CREATE TABLE IF NOT EXISTS current_band_leaderboard_entries_trios PARTITION OF current_band_leaderboard_entries FOR VALUES IN ('Band_Trios');
        CREATE TABLE IF NOT EXISTS current_band_leaderboard_entries_quad  PARTITION OF current_band_leaderboard_entries FOR VALUES IN ('Band_Quad');

        ALTER TABLE current_band_leaderboard_entries
            ADD COLUMN IF NOT EXISTS member_account_ids TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
            ADD COLUMN IF NOT EXISTS member_instrument_ids INTEGER[] NOT NULL DEFAULT ARRAY[]::INTEGER[],
            ADD COLUMN IF NOT EXISTS member_scores INTEGER[] NOT NULL DEFAULT ARRAY[]::INTEGER[],
            ADD COLUMN IF NOT EXISTS member_accuracies INTEGER[] NOT NULL DEFAULT ARRAY[]::INTEGER[],
            ADD COLUMN IF NOT EXISTS member_full_combos INTEGER[] NOT NULL DEFAULT ARRAY[]::INTEGER[],
            ADD COLUMN IF NOT EXISTS member_stars INTEGER[] NOT NULL DEFAULT ARRAY[]::INTEGER[],
            ADD COLUMN IF NOT EXISTS member_difficulties INTEGER[] NOT NULL DEFAULT ARRAY[]::INTEGER[];

        CREATE INDEX IF NOT EXISTS ix_cble_scope_rank
            ON current_band_leaderboard_entries (song_id, band_type, ranking_scope, scope_combo_id, rank);

        CREATE INDEX IF NOT EXISTS ix_cble_scope_generation_rank
            ON current_band_leaderboard_entries (song_id, band_type, ranking_scope, scope_combo_id, projection_generation, rank);

        CREATE INDEX IF NOT EXISTS ix_cble_team_song
            ON current_band_leaderboard_entries (band_type, team_key, song_id, ranking_scope, scope_combo_id);

        CREATE TABLE IF NOT EXISTS band_current_projection_state (
            id                    BOOLEAN     PRIMARY KEY DEFAULT TRUE CHECK (id),
            current_generation    BIGINT      NOT NULL DEFAULT 0,
            row_count             BIGINT      NOT NULL DEFAULT 0,
            scope_count           BIGINT      NOT NULL DEFAULT 0,
            failed_scope_count    BIGINT      NOT NULL DEFAULT 0,
            full_rebuilt_at       TIMESTAMPTZ,
            last_scope_rebuilt_at TIMESTAMPTZ,
            updated_at            TIMESTAMPTZ NOT NULL
        );

        CREATE TABLE IF NOT EXISTS band_current_projection_scope (
            song_id               TEXT        NOT NULL,
            band_type             TEXT        NOT NULL,
            ranking_scope         TEXT        NOT NULL DEFAULT 'overall',
            scope_combo_id        TEXT        NOT NULL DEFAULT '',
            projection_generation BIGINT      NOT NULL DEFAULT 0,
            published_generation  BIGINT,
            row_count             BIGINT      NOT NULL DEFAULT 0,
            published_row_count   BIGINT      NOT NULL DEFAULT 0,
            status                TEXT        NOT NULL DEFAULT 'ready',
            error_message         TEXT,
            last_rebuilt_at       TIMESTAMPTZ,
            updated_at            TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (song_id, band_type, ranking_scope, scope_combo_id)
        );

        ALTER TABLE band_current_projection_scope
            ADD COLUMN IF NOT EXISTS published_generation BIGINT;

        ALTER TABLE band_current_projection_scope
            ADD COLUMN IF NOT EXISTS published_row_count BIGINT NOT NULL DEFAULT 0;

        UPDATE band_current_projection_scope scope
        SET published_generation = scope.projection_generation,
            published_row_count = scope.row_count
        WHERE scope.published_generation IS NULL
                    AND scope.status = 'ready';

        CREATE INDEX IF NOT EXISTS ix_bcps_status_updated
            ON band_current_projection_scope (status, updated_at DESC);

        CREATE INDEX IF NOT EXISTS ix_bcps_scope_ready
            ON band_current_projection_scope (band_type, ranking_scope, scope_combo_id, status);

        CREATE INDEX IF NOT EXISTS ix_bcps_scope_published
            ON band_current_projection_scope (band_type, ranking_scope, scope_combo_id, published_generation)
            WHERE published_generation IS NOT NULL;
        """;
}

public sealed record BandCurrentProjectionRebuildOptions
{
    public int CommandTimeoutSeconds { get; init; }
    public bool DisableSynchronousCommit { get; init; } = true;
    public bool SkipUnchangedScopes { get; init; } = true;
    public bool UseBatchedMemberStatsAggregation { get; init; }
    public int MaxParallelBandTypes { get; init; } = 2;

    /// <summary>
    /// Zero keeps one sequential worker per band type (bounded by
    /// <see cref="MaxParallelBandTypes"/>). A positive value runs up to that
    /// many independent scope transactions at once across all band types.
    /// </summary>
    public int MaxParallelScopes { get; init; }
    /// <summary>
    /// Zero publishes an incremental refresh in one transaction and then scans
    /// the whole projection for unpublished candidates. A positive value
    /// publishes one song per transaction with up to that many at once (values
    /// above 16 are clamped) and probes only unsettled scopes for candidates.
    /// </summary>
    public int PublishParallelism { get; init; }

    /// <summary>
    /// When true (and <see cref="MaxParallelScopes"/> is positive), the refresh
    /// rebuilds all selected scopes of one (song, band type) in one transaction
    /// that reads and normalizes the song's band entries and member stats once,
    /// instead of once per scope.
    /// </summary>
    public bool BatchScopesBySourcePair { get; init; }
    public int CandidateCleanupBatchSize { get; init; } = 100_000;
    public int CandidateCleanupMaxBatches { get; init; } = 100;
    public bool ClearExisting { get; init; }
    public bool PublishOnSuccess { get; init; } = true;
    public IReadOnlyCollection<string>? BandTypes { get; init; }
    public bool IncludeOverallScopes { get; init; } = true;
    public bool IncludeComboScopes { get; init; } = true;
}

public sealed record BandCurrentProjectionScopeKey(
    string SongId,
    string BandType,
    string RankingScope,
    string ScopeComboId);

public sealed record BandCurrentProjectionSweepSelection(
    IReadOnlyList<BandCurrentProjectionScopeKey> ImpactedScopes,
    IReadOnlyList<BandCurrentProjectionScopeKey> StaleScopes,
    int SweepCandidateCount);

public sealed record BandCurrentProjectionScopeSummary(
    string SongId,
    string BandType,
    string RankingScope,
    string ScopeComboId,
    long RowCount,
    string Status,
    string? ErrorMessage,
    DateTime? LastRebuiltAt,
    long ProjectionGeneration,
    long? PublishedGeneration,
    long PublishedRowCount);

public sealed record BandCurrentProjectionStats(
    bool ProjectionExists,
    long RowCount,
    long ScopeCount,
    long FailedScopeCount,
    long? CurrentGeneration,
    DateTime? FullRebuiltAt,
    DateTime? LastScopeRebuiltAt,
    string TotalSize,
    IReadOnlyList<BandCurrentProjectionScopeSummary> RecentScopes);

public sealed record BandCurrentProjectionScopeResult(
    string SongId,
    string BandType,
    string RankingScope,
    string ScopeComboId,
    long Generation,
    long InsertedRows,
    long DeletedRows,
    bool SourceScopeExists,
    long DerivedMemberStatsAggregationPasses,
    double ElapsedMs);

public sealed record BandCurrentProjectionRebuildResult(
    long Generation,
    int ScopeCount,
    long InsertedRows,
    long DeletedRows,
    long OrphanedRowsDeleted,
    long CandidateRowsDeleted,
    BandCurrentProjectionPublishResult PublishResult,
    double TotalElapsedMs,
    BandCurrentProjectionStats Stats,
    IReadOnlyList<BandCurrentProjectionScopeResult> Scopes);

public sealed record BandCurrentProjectionIncrementalRefreshResult(
    int ScopeCount,
    int SuccessfulScopes,
    int FailedScopes,
    long InsertedRows,
    long DeletedRows,
    long CandidateRowsDeleted,
    BandCurrentProjectionPublishResult PublishResult,
    double TotalElapsedMs,
    IReadOnlyList<BandCurrentProjectionScopeResult> Scopes,
    BandCurrentProjectionOperationMetrics? OperationMetrics = null);

public sealed record BandCurrentProjectionOperationMetrics(
    long SuccessfulScopeTransactions,
    long DerivedSuccessfulScopeCommandExecutions,
    long DerivedSuccessfulScopeRoundTrips,
    long DerivedMemberStatsAggregationPasses)
{
    public static BandCurrentProjectionOperationMetrics Empty { get; } =
        new(0, 0, 0, 0);
}

public sealed record BandCurrentProjectionPublishResult(
    long Generation,
    bool Published,
    int ScopeCount,
    long ReadyScopes,
    long FailedScopes,
    long MissingScopes,
    long PublishedScopes,
    long PublishedRows,
    long DeletedRows)
{
    public static BandCurrentProjectionPublishResult NotPublished(
        long generation,
        int scopeCount,
        long readyScopes,
        long missingScopes,
        long failedScopes,
        long publishedRows,
        long deletedRows = 0) =>
        new(generation, false, scopeCount, readyScopes, failedScopes, missingScopes, 0, publishedRows, deletedRows);
}
