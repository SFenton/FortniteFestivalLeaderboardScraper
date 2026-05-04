using System.Diagnostics;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FSTService.Persistence;

public sealed class SoloCurrentProjectionBuilder
{
    public const string ProjectionTable = "current_leaderboard_entries";
    public const string StateTable = "solo_current_projection_state";
    public const string ScopeTable = "solo_current_projection_scope";
    public const string GenerationSequence = "solo_current_projection_generation_seq";

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<SoloCurrentProjectionBuilder> _log;

    public SoloCurrentProjectionBuilder(NpgsqlDataSource dataSource, ILogger<SoloCurrentProjectionBuilder> log)
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

    public SoloCurrentProjectionStats Inspect(int recentScopeLimit = 20)
    {
        using var conn = _dataSource.OpenConnection();

        if (!TableExists(conn, ProjectionTable))
        {
            return new SoloCurrentProjectionStats(
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

        var recentScopes = new List<SoloCurrentProjectionScopeSummary>();
        if (TableExists(conn, ScopeTable) && recentScopeLimit > 0)
        {
            using var recentCmd = conn.CreateCommand();
            recentCmd.CommandText = $"""
                SELECT song_id, instrument, row_count, status, error_message, last_rebuilt_at, source_snapshot_id, projection_generation
                FROM {ScopeTable}
                ORDER BY updated_at DESC
                LIMIT @limit
                """;
            recentCmd.Parameters.AddWithValue("limit", recentScopeLimit);

            using var reader = recentCmd.ExecuteReader();
            while (reader.Read())
            {
                recentScopes.Add(new SoloCurrentProjectionScopeSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.GetInt64(7)));
            }
        }

        return new SoloCurrentProjectionStats(
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

    public async Task<IReadOnlyList<SoloCurrentProjectionScopeKey>> LoadCurrentScopesAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = CurrentScopeSql;

        var scopes = new List<SoloCurrentProjectionScopeKey>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            scopes.Add(new SoloCurrentProjectionScopeKey(reader.GetString(0), reader.GetString(1)));

        return scopes;
    }

    public async Task<IReadOnlyList<SoloCurrentProjectionScopeKey>> LoadStaleScopesAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = $"""
            WITH current_pairs AS ({CurrentScopeSql}), desired AS (
                SELECT pair.song_id,
                       pair.instrument,
                       state.active_snapshot_id
                FROM current_pairs pair
                LEFT JOIN leaderboard_snapshot_state state
                  ON state.song_id = pair.song_id
                 AND state.instrument = pair.instrument
                 AND state.is_finalized = TRUE
                 AND state.active_snapshot_id IS NOT NULL
            )
            SELECT desired.song_id, desired.instrument
            FROM desired
            LEFT JOIN {ScopeTable} scope
              ON scope.song_id = desired.song_id
             AND scope.instrument = desired.instrument
            WHERE scope.song_id IS NULL
               OR scope.status <> 'ready'
               OR scope.source_snapshot_id IS DISTINCT FROM desired.active_snapshot_id
            ORDER BY desired.instrument, desired.song_id
            """;

        var scopes = new List<SoloCurrentProjectionScopeKey>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            scopes.Add(new SoloCurrentProjectionScopeKey(reader.GetString(0), reader.GetString(1)));

        return scopes;
    }

    public async Task<SoloCurrentProjectionRebuildResult> RebuildAllAsync(
        SoloCurrentProjectionRebuildOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new SoloCurrentProjectionRebuildOptions();
        var total = Stopwatch.StartNew();
        var generation = await NextGenerationAsync(ct);
        var scopes = await LoadCurrentScopesAsync(ct);

        if (options.ClearExisting)
            await ClearProjectionAsync(ct);

        var results = new List<SoloCurrentProjectionScopeResult>(scopes.Count);
        foreach (var scope in scopes)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RebuildScopeAsync(scope, options, generation, updateGlobalState: false, ct));
        }

        var orphanedRows = await DeleteOrphanedProjectionRowsAsync(options, ct);
        await RefreshGlobalStateFromScopesAsync(generation, fullRebuiltAt: DateTime.UtcNow, ct);
        total.Stop();

        var stats = Inspect();
        return new SoloCurrentProjectionRebuildResult(
            Generation: generation,
            ScopeCount: scopes.Count,
            InsertedRows: results.Sum(result => result.InsertedRows),
            DeletedRows: results.Sum(result => result.DeletedRows) + orphanedRows,
            OrphanedRowsDeleted: orphanedRows,
            TotalElapsedMs: Math.Round(total.Elapsed.TotalMilliseconds, 3),
            Stats: stats,
            Scopes: results);
    }

    public async Task<SoloCurrentProjectionScopeResult> RebuildScopeAsync(
        SoloCurrentProjectionScopeKey scope,
        SoloCurrentProjectionRebuildOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new SoloCurrentProjectionRebuildOptions();
        var generation = await NextGenerationAsync(ct);
        return await RebuildScopeAsync(scope, options, generation, updateGlobalState: true, ct);
    }

    public async Task<SoloCurrentProjectionIncrementalRefreshResult> RefreshScopesAsync(
        IReadOnlyCollection<SoloCurrentProjectionScopeKey> scopes,
        SoloCurrentProjectionRebuildOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new SoloCurrentProjectionRebuildOptions();
        var normalizedScopes = scopes
            .Where(static scope => !string.IsNullOrWhiteSpace(scope.SongId) && !string.IsNullOrWhiteSpace(scope.Instrument))
            .Select(static scope => new SoloCurrentProjectionScopeKey(scope.SongId.Trim(), scope.Instrument.Trim()))
            .Distinct()
            .OrderBy(static scope => scope.Instrument, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static scope => scope.SongId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedScopes.Length == 0)
            return new SoloCurrentProjectionIncrementalRefreshResult(0, 0, 0, 0, 0, 0, []);

        var sw = Stopwatch.StartNew();
        var generation = await NextGenerationAsync(ct);
        var maxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism);
        var results = new ConcurrentBag<SoloCurrentProjectionScopeResult>();
        var failedScopes = 0;

        await Parallel.ForEachAsync(
            normalizedScopes,
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
            },
            async (scope, token) =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    results.Add(await RebuildScopeAsync(scope, options, generation, updateGlobalState: false, token));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref failedScopes);
                }
            });

        var orderedResults = results
            .OrderBy(static result => result.Instrument, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static result => result.SongId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await RefreshGlobalStateFromScopesAsync(generation, fullRebuiltAt: null, ct);
        sw.Stop();

        return new SoloCurrentProjectionIncrementalRefreshResult(
            normalizedScopes.Length,
            orderedResults.Length,
            failedScopes,
            orderedResults.Sum(static result => result.InsertedRows),
            orderedResults.Sum(static result => result.DeletedRows),
            Math.Round(sw.Elapsed.TotalMilliseconds, 3),
            orderedResults);
    }

    private async Task<SoloCurrentProjectionScopeResult> RebuildScopeAsync(
        SoloCurrentProjectionScopeKey scope,
        SoloCurrentProjectionRebuildOptions options,
        long generation,
        bool updateGlobalState,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope.SongId))
            throw new ArgumentException("Song id is required.", nameof(scope));
        if (string.IsNullOrWhiteSpace(scope.Instrument))
            throw new ArgumentException("Instrument is required.", nameof(scope));

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
                                    AND instrument = @instrument
                                """;
                        deleteCmd.Parameters.AddWithValue("songId", scope.SongId);
                        deleteCmd.Parameters.AddWithValue("instrument", scope.Instrument);
                        var deletedRows = await deleteCmd.ExecuteNonQueryAsync(ct);

                        await using var cmd = conn.CreateCommand();
                        cmd.Transaction = tx;
                        ApplyCommandOptions(cmd, options);
                        cmd.CommandText = RebuildScopeSql;
                        cmd.Parameters.AddWithValue("songId", scope.SongId);
                        cmd.Parameters.AddWithValue("instrument", scope.Instrument);
                        cmd.Parameters.AddWithValue("generation", generation);
                        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);

            long insertedRows = 0;
            long? sourceSnapshotId = null;
            bool sourceScopeExists = false;

            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    insertedRows = reader.GetInt64(0);
                    sourceSnapshotId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                    sourceScopeExists = reader.GetBoolean(2);
                }
            }

            await tx.CommitAsync(ct);

            if (updateGlobalState)
                await RefreshGlobalStateFromScopesAsync(generation, fullRebuiltAt: null, ct);

            sw.Stop();
            return new SoloCurrentProjectionScopeResult(
                scope.SongId,
                scope.Instrument,
                generation,
                insertedRows,
                deletedRows,
                sourceSnapshotId,
                sourceScopeExists,
                Math.Round(sw.Elapsed.TotalMilliseconds, 3));
        }
        catch (Exception ex)
        {
            await MarkScopeFailedAsync(scope, generation, ex.Message, ct);
            _log.LogError(ex, "Failed to rebuild solo current projection scope {SongId}/{Instrument}", scope.SongId, scope.Instrument);
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

    private async Task<long> DeleteOrphanedProjectionRowsAsync(SoloCurrentProjectionRebuildOptions options, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        ApplyCommandOptions(cmd, options);
        cmd.CommandText = $"""
            WITH current_pairs AS ({CurrentScopeSql}), deleted_entries AS (
                DELETE FROM {ProjectionTable} projection
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM current_pairs pair
                    WHERE pair.song_id = projection.song_id
                      AND pair.instrument = projection.instrument
                )
                RETURNING 1
            ), deleted_scopes AS (
                DELETE FROM {ScopeTable} scope
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM current_pairs pair
                    WHERE pair.song_id = scope.song_id
                      AND pair.instrument = scope.instrument
                )
                RETURNING 1
            )
            SELECT COUNT(*)::BIGINT FROM deleted_entries
            """;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private async Task RefreshGlobalStateFromScopesAsync(long generation, DateTime? fullRebuiltAt, CancellationToken ct)
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
        cmd.Parameters.AddWithValue("generation", generation);
        cmd.Parameters.AddWithValue("fullRebuiltAt", fullRebuiltAt.HasValue ? fullRebuiltAt.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task MarkScopeFailedAsync(SoloCurrentProjectionScopeKey scope, long generation, string errorMessage, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(CancellationToken.None);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO {ScopeTable}
                (song_id, instrument, projection_generation, row_count, status, error_message, updated_at)
                VALUES (@songId, @instrument, @generation, 0, 'failed', @errorMessage, @now)
                ON CONFLICT (song_id, instrument) DO UPDATE SET
                    projection_generation = EXCLUDED.projection_generation,
                    status = EXCLUDED.status,
                    error_message = EXCLUDED.error_message,
                    updated_at = EXCLUDED.updated_at
                """;
            cmd.Parameters.AddWithValue("songId", scope.SongId);
            cmd.Parameters.AddWithValue("instrument", scope.Instrument);
            cmd.Parameters.AddWithValue("generation", generation);
            cmd.Parameters.AddWithValue("errorMessage", errorMessage);
            cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            await RefreshGlobalStateFromScopesAsync(generation, fullRebuiltAt: null, CancellationToken.None);
        }
        catch (Exception failure)
        {
            _log.LogWarning(failure, "Failed to mark solo current projection scope {SongId}/{Instrument} as failed", scope.SongId, scope.Instrument);
        }
    }

    private static bool TableExists(NpgsqlConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT to_regclass(@tableName) IS NOT NULL";
        cmd.Parameters.AddWithValue("tableName", $"public.{tableName}");
        return cmd.ExecuteScalar() is bool exists && exists;
    }

    private static void ApplyCommandOptions(NpgsqlCommand cmd, SoloCurrentProjectionRebuildOptions options)
    {
        cmd.CommandTimeout = options.CommandTimeoutSeconds <= 0 ? 0 : options.CommandTimeoutSeconds;
    }

    private const string CurrentScopeSql = """
        SELECT song_id, instrument
        FROM (
            SELECT state.song_id, state.instrument
            FROM leaderboard_snapshot_state state
            WHERE state.is_finalized = TRUE
              AND state.active_snapshot_id IS NOT NULL
            UNION
            SELECT live.song_id, live.instrument
            FROM leaderboard_entries live
            WHERE NOT EXISTS (
                SELECT 1
                FROM leaderboard_snapshot_state state
                WHERE state.song_id = live.song_id
                  AND state.instrument = live.instrument
                  AND state.is_finalized = TRUE
                  AND state.active_snapshot_id IS NOT NULL
            )
            UNION
            SELECT overlay.song_id, overlay.instrument
            FROM leaderboard_entries_overlay overlay
        ) pairs
        ORDER BY instrument, song_id
        """;

    private const string ProjectionSchemaSql = """
        CREATE SEQUENCE IF NOT EXISTS solo_current_projection_generation_seq;

        CREATE TABLE IF NOT EXISTS current_leaderboard_entries (
            song_id               TEXT        NOT NULL,
            instrument            TEXT        NOT NULL,
            account_id            TEXT        NOT NULL,
            score                 INTEGER     NOT NULL,
            accuracy              INTEGER,
            is_full_combo         BOOLEAN,
            stars                 INTEGER,
            season                INTEGER,
            percentile            REAL,
            rank                  INTEGER     NOT NULL DEFAULT 0,
            api_rank              INTEGER,
            source                TEXT        NOT NULL DEFAULT 'projection',
            difficulty            INTEGER     DEFAULT -1,
            end_time              TEXT,
            first_seen_at         TIMESTAMPTZ NOT NULL,
            last_updated_at       TIMESTAMPTZ NOT NULL,
            projection_generation BIGINT      NOT NULL DEFAULT 0,
            computed_at           TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (song_id, instrument, account_id)
        ) PARTITION BY LIST (instrument);

        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_solo_guitar    PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_Guitar');
        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_solo_bass      PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_Bass');
        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_solo_drums     PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_Drums');
        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_solo_vocals    PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_Vocals');
        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_pro_guitar     PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_PeripheralGuitar');
        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_pro_bass       PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_PeripheralBass');
        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_pro_vocals     PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_PeripheralVocals');
        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_pro_cymbals    PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_PeripheralCymbals');
        CREATE TABLE IF NOT EXISTS current_leaderboard_entries_pro_drums      PARTITION OF current_leaderboard_entries FOR VALUES IN ('Solo_PeripheralDrums');

        CREATE INDEX IF NOT EXISTS ix_cle_account_instrument_song
            ON current_leaderboard_entries (account_id, instrument, song_id);

        CREATE INDEX IF NOT EXISTS ix_cle_song_rank
            ON current_leaderboard_entries (song_id, instrument, rank);

        CREATE INDEX IF NOT EXISTS ix_cle_song_score
            ON current_leaderboard_entries (song_id, instrument, score DESC);

        CREATE TABLE IF NOT EXISTS solo_current_projection_state (
            id                    BOOLEAN     PRIMARY KEY DEFAULT TRUE CHECK (id),
            current_generation    BIGINT      NOT NULL DEFAULT 0,
            row_count             BIGINT      NOT NULL DEFAULT 0,
            scope_count           BIGINT      NOT NULL DEFAULT 0,
            failed_scope_count    BIGINT      NOT NULL DEFAULT 0,
            full_rebuilt_at       TIMESTAMPTZ,
            last_scope_rebuilt_at TIMESTAMPTZ,
            updated_at            TIMESTAMPTZ NOT NULL
        );

        CREATE TABLE IF NOT EXISTS solo_current_projection_scope (
            song_id               TEXT        NOT NULL,
            instrument            TEXT        NOT NULL,
            projection_generation BIGINT      NOT NULL DEFAULT 0,
            row_count             BIGINT      NOT NULL DEFAULT 0,
            source_snapshot_id    BIGINT,
            status                TEXT        NOT NULL DEFAULT 'ready',
            error_message         TEXT,
            last_rebuilt_at       TIMESTAMPTZ,
            updated_at            TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (song_id, instrument)
        );

        CREATE INDEX IF NOT EXISTS ix_scps_status_updated
            ON solo_current_projection_scope (status, updated_at DESC);
        """;

    private const string RebuildScopeSql = """
        WITH active_snapshot AS (
            SELECT active_snapshot_id
            FROM leaderboard_snapshot_state
            WHERE song_id = @songId
              AND instrument = @instrument
              AND is_finalized = TRUE
              AND active_snapshot_id IS NOT NULL
            LIMIT 1
        ), source_scope AS (
            SELECT (
                EXISTS (SELECT 1 FROM active_snapshot)
                OR EXISTS (
                    SELECT 1
                    FROM leaderboard_entries live
                    WHERE live.song_id = @songId
                      AND live.instrument = @instrument
                      AND NOT EXISTS (SELECT 1 FROM active_snapshot)
                )
                OR EXISTS (
                    SELECT 1
                    FROM leaderboard_entries_overlay overlay
                    WHERE overlay.song_id = @songId
                      AND overlay.instrument = @instrument
                )
            ) AS exists
        ), base_rows AS (
            SELECT live.account_id, live.score, live.accuracy, live.is_full_combo, live.stars,
                   live.season, live.difficulty, live.percentile, live.end_time, live.rank,
                   live.api_rank, live.source, live.first_seen_at, live.last_updated_at,
                   1 AS origin_precedence,
                   0 AS source_priority
            FROM leaderboard_entries live
            WHERE live.song_id = @songId
              AND live.instrument = @instrument
              AND NOT EXISTS (SELECT 1 FROM active_snapshot)
            UNION ALL
            SELECT snapshot.account_id, snapshot.score, snapshot.accuracy, snapshot.is_full_combo, snapshot.stars,
                   snapshot.season, snapshot.difficulty, snapshot.percentile, snapshot.end_time, snapshot.rank,
                   snapshot.api_rank, snapshot.source, snapshot.first_seen_at, snapshot.last_updated_at,
                   1 AS origin_precedence,
                   0 AS source_priority
            FROM leaderboard_entries_snapshot snapshot
            JOIN active_snapshot active ON active.active_snapshot_id = snapshot.snapshot_id
            WHERE snapshot.song_id = @songId
              AND snapshot.instrument = @instrument
            UNION ALL
            SELECT overlay.account_id, overlay.score, overlay.accuracy, overlay.is_full_combo, overlay.stars,
                   overlay.season, overlay.difficulty, overlay.percentile, overlay.end_time, overlay.rank,
                   overlay.api_rank, overlay.source, overlay.first_seen_at, overlay.last_updated_at,
                   0 AS origin_precedence,
                   overlay.source_priority
            FROM leaderboard_entries_overlay overlay
            WHERE overlay.song_id = @songId
              AND overlay.instrument = @instrument
        ), resolved_rows AS (
            SELECT DISTINCT ON (account_id)
                   account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile,
                   end_time, api_rank, source, first_seen_at, last_updated_at
            FROM base_rows
            ORDER BY account_id, origin_precedence ASC, source_priority DESC
        ), ranked_rows AS (
            SELECT account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile,
                   end_time,
                   (ROW_NUMBER() OVER (ORDER BY score DESC, COALESCE(end_time, first_seen_at::TEXT) ASC))::INTEGER AS rank,
                   api_rank, source, first_seen_at, last_updated_at
            FROM resolved_rows
        ), inserted AS (
            INSERT INTO current_leaderboard_entries
            (song_id, instrument, account_id, score, accuracy, is_full_combo, stars, season, difficulty,
             percentile, end_time, rank, api_rank, source, first_seen_at, last_updated_at, projection_generation, computed_at)
            SELECT @songId, @instrument, account_id, score, accuracy, is_full_combo, stars, season, difficulty,
                   percentile, end_time, rank, api_rank, source, first_seen_at, last_updated_at, @generation, @now
            FROM ranked_rows
            WHERE (SELECT exists FROM source_scope)
            RETURNING 1
        ), scope_deleted AS (
            DELETE FROM solo_current_projection_scope
            WHERE song_id = @songId
              AND instrument = @instrument
              AND NOT (SELECT exists FROM source_scope)
            RETURNING 1
        ), scope_upsert AS (
            INSERT INTO solo_current_projection_scope
            (song_id, instrument, projection_generation, row_count, source_snapshot_id, status, error_message, last_rebuilt_at, updated_at)
            SELECT @songId,
                   @instrument,
                   @generation,
                   (SELECT COUNT(*)::BIGINT FROM inserted),
                   (SELECT active_snapshot_id FROM active_snapshot),
                   'ready',
                   NULL,
                   @now,
                   @now
            WHERE (SELECT exists FROM source_scope)
            ON CONFLICT (song_id, instrument) DO UPDATE SET
                projection_generation = EXCLUDED.projection_generation,
                row_count = EXCLUDED.row_count,
                source_snapshot_id = EXCLUDED.source_snapshot_id,
                status = EXCLUDED.status,
                error_message = EXCLUDED.error_message,
                last_rebuilt_at = EXCLUDED.last_rebuilt_at,
                updated_at = EXCLUDED.updated_at
            RETURNING row_count
        )
        SELECT (SELECT COUNT(*)::BIGINT FROM inserted),
               (SELECT active_snapshot_id FROM active_snapshot),
               (SELECT exists FROM source_scope)
        """;
}

public sealed class SoloCurrentProjectionRebuildOptions
{
    public int CommandTimeoutSeconds { get; init; }
    public bool DisableSynchronousCommit { get; init; } = true;
    public bool ClearExisting { get; init; }
    public int MaxDegreeOfParallelism { get; init; } = 1;
}

public sealed record SoloCurrentProjectionScopeKey(string SongId, string Instrument);

public sealed record SoloCurrentProjectionScopeSummary(
    string SongId,
    string Instrument,
    long RowCount,
    string Status,
    string? ErrorMessage,
    DateTime? LastRebuiltAt,
    long? SourceSnapshotId,
    long ProjectionGeneration);

public sealed record SoloCurrentProjectionStats(
    bool ProjectionExists,
    long RowCount,
    long ScopeCount,
    long FailedScopeCount,
    long? CurrentGeneration,
    DateTime? FullRebuiltAt,
    DateTime? LastScopeRebuiltAt,
    string TotalSize,
    IReadOnlyList<SoloCurrentProjectionScopeSummary> RecentScopes);

public sealed record SoloCurrentProjectionScopeResult(
    string SongId,
    string Instrument,
    long Generation,
    long InsertedRows,
    long DeletedRows,
    long? SourceSnapshotId,
    bool SourceScopeExists,
    double ElapsedMs);

public sealed record SoloCurrentProjectionIncrementalRefreshResult(
    int ScopeCount,
    int SucceededScopeCount,
    int FailedScopeCount,
    long InsertedRows,
    long DeletedRows,
    double TotalElapsedMs,
    IReadOnlyList<SoloCurrentProjectionScopeResult> Scopes);

public sealed record SoloCurrentProjectionRebuildResult(
    long Generation,
    int ScopeCount,
    long InsertedRows,
    long DeletedRows,
    long OrphanedRowsDeleted,
    double TotalElapsedMs,
    SoloCurrentProjectionStats Stats,
    IReadOnlyList<SoloCurrentProjectionScopeResult> Scopes);