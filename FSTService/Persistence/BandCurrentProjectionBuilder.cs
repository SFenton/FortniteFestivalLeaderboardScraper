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
                SELECT song_id, band_type, ranking_scope, scope_combo_id, row_count, status, error_message, last_rebuilt_at, projection_generation
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
                    reader.GetInt64(8)));
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
        for (var i = 0; i < scopes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await RebuildScopeAsync(scopes[i], options, generation, updateGlobalState: false, ct);
            results.Add(result);
            progress?.Invoke(i + 1, scopes.Count, result);
        }

        var canPruneOrphans = !options.ClearExisting
            && (options.BandTypes is null || options.BandTypes.Count == 0)
            && options.IncludeOverallScopes
            && options.IncludeComboScopes;
        var orphanedRows = canPruneOrphans ? await DeleteOrphanedProjectionRowsAsync(options, ct) : 0;
        await RefreshGlobalStateFromScopesAsync(fullRebuiltAt: DateTime.UtcNow, ct);
        total.Stop();

        var stats = Inspect();
        return new BandCurrentProjectionRebuildResult(
            Generation: generation,
            ScopeCount: scopes.Count,
            InsertedRows: results.Sum(static result => result.InsertedRows),
            DeletedRows: results.Sum(static result => result.DeletedRows) + orphanedRows,
            OrphanedRowsDeleted: orphanedRows,
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
            return new BandCurrentProjectionIncrementalRefreshResult(0, 0, 0, 0, 0, 0, []);

        var sw = Stopwatch.StartNew();
        var generation = await NextGenerationAsync(ct);
        var results = new List<BandCurrentProjectionScopeResult>(normalizedScopes.Length);
        var failedScopes = 0;

        foreach (var scope in normalizedScopes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                results.Add(await RebuildScopeAsync(scope, options, generation, updateGlobalState: false, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedScopes++;
            }
        }

        await RefreshGlobalStateFromScopesAsync(fullRebuiltAt: null, ct);
        sw.Stop();

        return new BandCurrentProjectionIncrementalRefreshResult(
            normalizedScopes.Length,
            results.Count,
            failedScopes,
            results.Sum(static result => result.InsertedRows),
            results.Sum(static result => result.DeletedRows),
            Math.Round(sw.Elapsed.TotalMilliseconds, 3),
            results);
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
                """;
            AddScopeParameters(deleteCmd, scope);
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
                await RefreshGlobalStateFromScopesAsync(fullRebuiltAt: null, ct);

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

    private async Task<long> DeleteOrphanedProjectionRowsAsync(BandCurrentProjectionRebuildOptions options, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        ApplyCommandOptions(cmd, options);
        cmd.CommandText = $"""
            WITH current_pairs AS (
                SELECT song_id, band_type, ranking_scope, scope_combo_id
                FROM {ScopeTable}
                WHERE projection_generation = (SELECT MAX(projection_generation) FROM {ScopeTable} WHERE status = 'ready')
            ), deleted_entries AS (
                DELETE FROM {ProjectionTable} projection
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM current_pairs pair
                    WHERE pair.song_id = projection.song_id
                      AND pair.band_type = projection.band_type
                      AND pair.ranking_scope = projection.ranking_scope
                      AND pair.scope_combo_id = projection.scope_combo_id
                )
                RETURNING 1
            )
            SELECT COUNT(*)::BIGINT FROM deleted_entries
            """;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private async Task RefreshGlobalStateFromScopesAsync(DateTime? fullRebuiltAt, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = $"""
            INSERT INTO {StateTable}
            (id, current_generation, row_count, scope_count, failed_scope_count, full_rebuilt_at, last_scope_rebuilt_at, updated_at)
            SELECT TRUE,
                   COALESCE((SELECT MIN(projection_generation) FROM {ScopeTable} WHERE status = 'ready'), 0),
                   COALESCE((SELECT SUM(row_count)::BIGINT FROM {ScopeTable} WHERE status = 'ready'), 0),
                   (SELECT COUNT(*)::BIGINT FROM {ScopeTable}),
                   (SELECT COUNT(*)::BIGINT FROM {ScopeTable} WHERE status = 'failed'),
                   COALESCE(@fullRebuiltAt, (SELECT full_rebuilt_at FROM {StateTable} WHERE id = TRUE)),
                   (SELECT MAX(last_rebuilt_at) FROM {ScopeTable} WHERE status = 'ready'),
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
        ), ScopeDeleted AS (
            DELETE FROM band_current_projection_scope
            WHERE song_id = @songId
              AND band_type = @bandType
              AND ranking_scope = @rankingScope
              AND scope_combo_id = @scopeComboId
              AND NOT (SELECT exists FROM SourceScope)
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
            WHERE (SELECT exists FROM SourceScope)
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
            PRIMARY KEY (song_id, band_type, ranking_scope, scope_combo_id, team_key)
        ) PARTITION BY LIST (band_type);

        CREATE TABLE IF NOT EXISTS current_band_leaderboard_entries_duets PARTITION OF current_band_leaderboard_entries FOR VALUES IN ('Band_Duets');
        CREATE TABLE IF NOT EXISTS current_band_leaderboard_entries_trios PARTITION OF current_band_leaderboard_entries FOR VALUES IN ('Band_Trios');
        CREATE TABLE IF NOT EXISTS current_band_leaderboard_entries_quad  PARTITION OF current_band_leaderboard_entries FOR VALUES IN ('Band_Quad');

        CREATE INDEX IF NOT EXISTS ix_cble_scope_rank
            ON current_band_leaderboard_entries (song_id, band_type, ranking_scope, scope_combo_id, rank);

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
            row_count             BIGINT      NOT NULL DEFAULT 0,
            status                TEXT        NOT NULL DEFAULT 'ready',
            error_message         TEXT,
            last_rebuilt_at       TIMESTAMPTZ,
            updated_at            TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (song_id, band_type, ranking_scope, scope_combo_id)
        );

        CREATE INDEX IF NOT EXISTS ix_bcps_status_updated
            ON band_current_projection_scope (status, updated_at DESC);
        """;
}

public sealed class BandCurrentProjectionRebuildOptions
{
    public int CommandTimeoutSeconds { get; init; }
    public bool DisableSynchronousCommit { get; init; } = true;
    public bool ClearExisting { get; init; }
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
    long ProjectionGeneration);

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
    double TotalElapsedMs,
    BandCurrentProjectionStats Stats,
    IReadOnlyList<BandCurrentProjectionScopeResult> Scopes);

public sealed record BandCurrentProjectionIncrementalRefreshResult(
    int ScopeCount,
    int SuccessfulScopes,
    int FailedScopes,
    long InsertedRows,
    long DeletedRows,
    double TotalElapsedMs,
    IReadOnlyList<BandCurrentProjectionScopeResult> Scopes);