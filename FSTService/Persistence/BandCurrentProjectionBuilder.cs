using System.Collections.Concurrent;
using System.Diagnostics;
using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FSTService.Persistence;

public sealed class BandCurrentProjectionBuilder
{
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
            WITH NormalizedEntries AS (
                SELECT
                    be.song_id,
                    be.band_type,
                    {BandSongComboIdExpression} AS combo_id
                FROM band_entries be
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

    public async Task<BandCurrentProjectionIncrementalRefreshResult> RefreshScopesAsync(
        IReadOnlyCollection<BandCurrentProjectionScopeKey> scopes,
        BandCurrentProjectionRebuildOptions? options = null,
        CancellationToken ct = default)
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
                []);

        var sw = Stopwatch.StartNew();
        var generation = await NextGenerationAsync(ct);
        var scopesToRefresh = options.SkipUnchangedScopes
            ? await FilterScopesNeedingRefreshAsync(normalizedScopes, ct)
            : normalizedScopes;

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
                []);
        }

        var results = new ConcurrentBag<BandCurrentProjectionScopeResult>();
        var failedScopes = 0;
        var maxParallelBandTypes = Math.Clamp(options.MaxParallelBandTypes, 1, BandInstrumentMapping.AllBandTypes.Count);
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
                {
                    innerCt.ThrowIfCancellationRequested();
                    try
                    {
                        results.Add(await RebuildScopeAsync(scope, options, generation, updateGlobalState: false, innerCt));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref failedScopes);
                    }
                }
            });

        var orderedResults = results
            .OrderBy(static result => result.BandType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static result => result.RankingScope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static result => result.ScopeComboId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static result => result.SongId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var publishResult = options.PublishOnSuccess
            ? await TryPublishGenerationAsync(generation, scopesToRefresh, fullRebuiltAt: null, ct)
            : BandCurrentProjectionPublishResult.NotPublished(generation, scopesToRefresh.Length, 0, scopesToRefresh.Length, failedScopes, 0);

        await RefreshGlobalStateFromScopesAsync(fullRebuiltAt: null, ct);

        var affectedBandTypes = GetAffectedBandTypes(options, scopesToRefresh, includeAllWhenUnfiltered: false);
        var candidateRowsDeleted = options.PublishOnSuccess
            ? await DeleteUnpublishedCandidateRowsAsync(options, affectedBandTypes, ct)
            : 0;

        sw.Stop();

        _log.LogInformation(
            "Band current projection refresh selected {RefreshScopes:N0}/{ProvidedScopes:N0} scope(s) after unchanged-scope filtering; maxParallelBandTypes={MaxParallelBandTypes}.",
            scopesToRefresh.Length,
            normalizedScopes.Length,
            maxParallelBandTypes);

        return new BandCurrentProjectionIncrementalRefreshResult(
            scopesToRefresh.Length,
            orderedResults.Length,
            failedScopes,
            orderedResults.Sum(static result => result.InsertedRows),
            orderedResults.Sum(static result => result.DeletedRows) + publishResult.DeletedRows + candidateRowsDeleted,
            candidateRowsDeleted,
            publishResult,
            Math.Round(sw.Elapsed.TotalMilliseconds, 3),
            orderedResults);
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
            WITH source_scope AS (
                SELECT requested.song_id,
                       requested.band_type,
                       requested.ranking_scope,
                       requested.scope_combo_id,
                       COUNT(DISTINCT be.team_key)::BIGINT AS projected_rows,
                       MAX(be.last_updated_at) AS max_source_updated_at
                FROM _band_current_refresh_scopes requested
                LEFT JOIN band_entries be
                  ON be.song_id = requested.song_id
                 AND be.band_type = requested.band_type
                 AND NOT be.is_over_threshold
                 AND (
                     requested.ranking_scope = 'overall'
                     OR ({BandSongComboIdExpression}) = requested.scope_combo_id
                 )
                GROUP BY requested.song_id, requested.band_type, requested.ranking_scope, requested.scope_combo_id
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
            WHERE (source_scope.projected_rows = 0 AND existing.song_id IS NOT NULL)
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
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new BandCurrentProjectionScopeKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
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

            if (options.DisableSynchronousCommit)
            {
                await using var syncCmd = conn.CreateCommand();
                syncCmd.Transaction = tx;
                syncCmd.CommandText = "SET LOCAL synchronous_commit = off";
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
            var deletedRows = await deleteCmd.ExecuteNonQueryAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            ApplyCommandOptions(cmd, options);
            cmd.CommandText = RebuildScopeSql;
            AddScopeParameters(cmd, scope);
            cmd.Parameters.AddWithValue("expectedMembers", BandInstrumentMapping.ExpectedMemberCount(scope.BandType));
            cmd.Parameters.AddWithValue("generation", generation);
            cmd.Parameters.AddWithValue("now", DateTime.UtcNow);

            long insertedRows = 0;
            bool sourceScopeExists = false;

            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    insertedRows = reader.GetInt64(0);
                    sourceScopeExists = reader.GetBoolean(1);
                }
            }

            await tx.CommitAsync(ct);

            if (updateGlobalState)
            {
                if (options.PublishOnSuccess)
                    await TryPublishGenerationAsync(generation, [scope], fullRebuiltAt: null, ct);
                else
                    await RefreshGlobalStateFromScopesAsync(fullRebuiltAt: null, ct);
            }

            sw.Stop();
            return new BandCurrentProjectionScopeResult(
                scope.SongId,
                scope.BandType,
                scope.RankingScope,
                scope.ScopeComboId,
                generation,
                insertedRows,
                deletedRows,
                sourceScopeExists,
                Math.Round(sw.Elapsed.TotalMilliseconds, 3));
        }
        catch (Exception ex)
        {
            await MarkScopeFailedAsync(scope, generation, ex.Message, ct);
            _log.LogError(ex, "Failed to rebuild band current projection scope {SongId}/{BandType}/{RankingScope}/{ScopeComboId}", scope.SongId, scope.BandType, scope.RankingScope, scope.ScopeComboId);
            throw;
        }
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

    public async Task<BandCurrentProjectionPublishResult> TryPublishGenerationAsync(
        long generation,
        IReadOnlyCollection<BandCurrentProjectionScopeKey> scopes,
        DateTime? fullRebuiltAt = null,
        CancellationToken ct = default)
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
        await RefreshGlobalStateFromScopesAsync(conn, tx, effectiveFullRebuiltAt, ct);
        await tx.CommitAsync(ct);

        _log.LogInformation(
            "Published band current projection generation {Generation:N0}: {ReadyScopes:N0}/{ScopeCount:N0} scope(s), {PublishedRows:N0} row(s), {DeletedRows:N0} old row(s) deleted.",
            generation,
            readyScopes,
            normalizedScopes.Length,
            publishedRows,
            deletedRows);

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

    private const string RebuildScopeSql = $"""
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
        ), Inserted AS (
            INSERT INTO current_band_leaderboard_entries
            (song_id, band_type, ranking_scope, scope_combo_id, team_key, entry_combo_id, entry_instrument_combo,
             team_members, score, accuracy, is_full_combo, stars, difficulty, season, rank, total_entries,
             percentile, end_time, first_seen_at, last_updated_at, projection_generation, computed_at)
            SELECT song_id, band_type, ranking_scope, scope_combo_id, team_key, entry_combo_id, entry_instrument_combo,
                   team_members, score, accuracy, is_full_combo, stars, difficulty, season, rank, total_entries,
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

public sealed class BandCurrentProjectionRebuildOptions
{
    public int CommandTimeoutSeconds { get; init; }
    public bool DisableSynchronousCommit { get; init; } = true;
    public bool SkipUnchangedScopes { get; init; } = true;
    public int MaxParallelBandTypes { get; init; } = 2;
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
    IReadOnlyList<BandCurrentProjectionScopeResult> Scopes);

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
