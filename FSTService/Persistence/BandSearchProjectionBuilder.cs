using System.Diagnostics;
using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

/// <summary>
/// Builds the denormalized projection used by global band search.
/// The projection is intentionally separate from player-band membership summaries:
/// search needs one complete, rich, request-independent index for every band.
/// </summary>
public sealed class BandSearchProjectionBuilder
{
    public const string TeamProjectionTable = "band_search_team_projection";
    public const string MemberProjectionTable = "band_search_member_projection";
    public const string StateTable = "band_search_projection_state";

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<BandSearchProjectionBuilder> _log;

    public BandSearchProjectionBuilder(NpgsqlDataSource dataSource, ILogger<BandSearchProjectionBuilder> log)
    {
        _dataSource = dataSource;
        _log = log;
    }

    public BandSearchProjectionStats Inspect()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                (SELECT COUNT(*) FROM {TeamProjectionTable}) AS team_rows,
                (SELECT COUNT(*) FROM {MemberProjectionTable}) AS member_rows,
                (SELECT rebuilt_at FROM {StateTable} WHERE id = TRUE) AS rebuilt_at,
                (SELECT team_rows FROM {StateTable} WHERE id = TRUE) AS state_team_rows,
                (SELECT member_rows FROM {StateTable} WHERE id = TRUE) AS state_member_rows
            """;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return new BandSearchProjectionStats(0, 0, null, null, null);

        return new BandSearchProjectionStats(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4));
    }

    public async Task<BandSearchProjectionBackfillResult> RebuildAllAsync(CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        var rebuiltAt = DateTime.UtcNow;

        await using var lockConn = await _dataSource.OpenConnectionAsync(ct);
        await AcquireRebuildLockAsync(lockConn, ct);
        try
        {
            var load = await LoadProjectionRowsAsync(rebuiltAt, ct);
            var idUpdate = await BackfillBandIdsAndPublishStateAsync(rebuiltAt, ct);

            total.Stop();
            var stats = Inspect();
            return new BandSearchProjectionBackfillResult(
                stats,
                Math.Round(load.TeamLoad.TotalMilliseconds, 3),
                Math.Round(load.MemberLoad.TotalMilliseconds, 3),
                Math.Round(idUpdate.TotalMilliseconds, 3),
                Math.Round(total.Elapsed.TotalMilliseconds, 3));
        }
        finally
        {
            await ReleaseRebuildLockAsync(lockConn, CancellationToken.None);
        }
    }

    public async Task<BandSearchProjectionIncrementalResult> RefreshIncrementalAsync(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> additionalTeamsByBandType,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        await using var lockConn = await _dataSource.OpenConnectionAsync(ct);
        await AcquireRebuildLockAsync(lockConn, ct);
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            var cutoff = await GetIncrementalCutoffAsync(conn, tx, ct);
            if (cutoff is null)
            {
                _log.LogDebug("Band search projection incremental refresh skipped: projection state is absent.");
                await tx.RollbackAsync(ct);
                return new BandSearchProjectionIncrementalResult(false, 0, 0, 0, 0, 0, 0, 0, 0);
            }

            var refreshedAt = DateTime.UtcNow;
            await CreateRefreshKeysTableAsync(conn, tx, ct);
            var providedTeams = await CopyProvidedRefreshKeysAsync(conn, additionalTeamsByBandType, ct);
            var changedTeams = await InsertChangedRefreshKeysAsync(conn, tx, cutoff.Value, ct);
            var impactedTeams = await CountRefreshKeysAsync(conn, tx, ct);

            if (impactedTeams == 0)
            {
                await ExecuteNonQueryAsync(conn, tx, $"UPDATE {StateTable} SET refreshed_at = @refreshedAt WHERE id = TRUE", ct, ("refreshedAt", refreshedAt));
                await tx.CommitAsync(ct);
                sw.Stop();
                return new BandSearchProjectionIncrementalResult(true, 0, providedTeams, changedTeams, 0, 0, 0, 0, Math.Round(sw.Elapsed.TotalMilliseconds, 3));
            }

            await FillMissingRefreshKeyBandIdsAsync(conn, tx, ct);

            var (deletedTeamRows, deletedMemberRows) = await CountExistingProjectionRowsForRefreshAsync(conn, tx, ct);
            await DeleteProjectionRowsForRefreshAsync(conn, tx, ct);

            var insertedTeamRows = await ExecuteNonQueryAsync(conn, tx, BuildTeamProjectionRefreshSql(), ct, ("refreshedAt", refreshedAt));
            var insertedMemberRows = await ExecuteNonQueryAsync(conn, tx, BuildMemberProjectionRefreshSql(), ct, ("refreshedAt", refreshedAt));

            await ExecuteNonQueryAsync(conn, tx, $"""
                UPDATE {StateTable}
                SET refreshed_at = @refreshedAt,
                    team_rows = GREATEST(0, team_rows - @deletedTeamRows + @insertedTeamRows),
                    member_rows = GREATEST(0, member_rows - @deletedMemberRows + @insertedMemberRows)
                WHERE id = TRUE
                """, ct,
                ("refreshedAt", refreshedAt),
                ("deletedTeamRows", deletedTeamRows),
                ("insertedTeamRows", insertedTeamRows),
                ("deletedMemberRows", deletedMemberRows),
                ("insertedMemberRows", insertedMemberRows));

            await tx.CommitAsync(ct);
            sw.Stop();

            _log.LogInformation(
                "Refreshed band search projection for {ImpactedTeams:N0} impacted team(s) in {Elapsed:n1}s. " +
                "Teams: {DeletedTeams:N0} deleted / {InsertedTeams:N0} inserted; members: {DeletedMembers:N0} deleted / {InsertedMembers:N0} inserted.",
                impactedTeams,
                sw.Elapsed.TotalSeconds,
                deletedTeamRows,
                insertedTeamRows,
                deletedMemberRows,
                insertedMemberRows);

            return new BandSearchProjectionIncrementalResult(
                true,
                impactedTeams,
                providedTeams,
                changedTeams,
                deletedTeamRows,
                insertedTeamRows,
                deletedMemberRows,
                insertedMemberRows,
                Math.Round(sw.Elapsed.TotalMilliseconds, 3));
        }
        finally
        {
            await ReleaseRebuildLockAsync(lockConn, CancellationToken.None);
        }
    }

    public async Task EnsureStateRefreshedAtAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await ExecuteNonQueryAsync(conn, tx, $"ALTER TABLE {StateTable} ADD COLUMN IF NOT EXISTS refreshed_at TIMESTAMPTZ", ct);
        await ExecuteNonQueryAsync(conn, tx, $"UPDATE {StateTable} SET refreshed_at = rebuilt_at WHERE refreshed_at IS NULL", ct);

        await tx.CommitAsync(ct);
    }

    public async Task<BandSearchProjectionCatchUpResult> CatchUpAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        await using var lockConn = await _dataSource.OpenConnectionAsync(ct);
        await AcquireRebuildLockAsync(lockConn, ct);
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            await CreateSourceSummaryTableAsync(conn, tx, ct);
            var counts = await CountCatchUpCandidatesAsync(conn, tx, ct);

            var refreshedAt = DateTime.UtcNow;
            await CreateRefreshKeysTableAsync(conn, tx, ct);
            var impactedTeams = await InsertCatchUpRefreshKeysAsync(conn, tx, ct);

            if (impactedTeams == 0)
            {
                var (teamRows, memberRows) = await CountProjectionTablesAsync(conn, tx, ct);
                await UpdateProjectionStateCountsAsync(conn, tx, refreshedAt, teamRows, memberRows, ct);
                await tx.CommitAsync(ct);
                sw.Stop();
                return new BandSearchProjectionCatchUpResult(0, 0, 0, 0, 0, 0, 0, 0, teamRows, memberRows, Math.Round(sw.Elapsed.TotalMilliseconds, 3));
            }

            await FillMissingRefreshKeyBandIdsAsync(conn, tx, ct);

            var (deletedTeamRows, deletedMemberRows) = await CountExistingProjectionRowsForRefreshAsync(conn, tx, ct);
            await DeleteProjectionRowsForRefreshAsync(conn, tx, ct);

            var insertedTeamRows = await ExecuteNonQueryAsync(conn, tx, BuildTeamProjectionRefreshSql(), ct, ("refreshedAt", refreshedAt));
            var insertedMemberRows = await ExecuteNonQueryAsync(conn, tx, BuildMemberProjectionRefreshSql(), ct, ("refreshedAt", refreshedAt));

            var (finalTeamRows, finalMemberRows) = await CountProjectionTablesAsync(conn, tx, ct);
            await UpdateProjectionStateCountsAsync(conn, tx, refreshedAt, finalTeamRows, finalMemberRows, ct);

            await tx.CommitAsync(ct);
            sw.Stop();

            _log.LogInformation(
                "Caught up band search projection for {ImpactedTeams:N0} impacted team(s) in {Elapsed:n1}s. " +
                "Dead: {DeadTeams:N0}; stale live: {StaleTeams:N0}; new: {NewTeams:N0}. " +
                "Teams: {DeletedTeams:N0} deleted / {InsertedTeams:N0} inserted; members: {DeletedMembers:N0} deleted / {InsertedMembers:N0} inserted.",
                impactedTeams,
                sw.Elapsed.TotalSeconds,
                counts.DeadProjectedTeams,
                counts.StaleLiveProjectedTeams,
                counts.NewSourceTeams,
                deletedTeamRows,
                insertedTeamRows,
                deletedMemberRows,
                insertedMemberRows);

            return new BandSearchProjectionCatchUpResult(
                impactedTeams,
                counts.DeadProjectedTeams,
                counts.StaleLiveProjectedTeams,
                counts.NewSourceTeams,
                deletedTeamRows,
                insertedTeamRows,
                deletedMemberRows,
                insertedMemberRows,
                finalTeamRows,
                finalMemberRows,
                Math.Round(sw.Elapsed.TotalMilliseconds, 3));
        }
        finally
        {
            await ReleaseRebuildLockAsync(lockConn, CancellationToken.None);
        }
    }

    private static async Task AcquireRebuildLockAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = "SELECT pg_advisory_lock(hashtextextended('band_search_projection_rebuild', 0))";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ReleaseRebuildLockAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = "SELECT pg_advisory_unlock(hashtextextended('band_search_projection_rebuild', 0))";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<(TimeSpan TeamLoad, TimeSpan MemberLoad)> LoadProjectionRowsAsync(DateTime rebuiltAt, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        await ExecuteNonQueryAsync(conn, tx, $"DELETE FROM {StateTable}", ct);
        await ExecuteNonQueryAsync(conn, tx, $"TRUNCATE TABLE {MemberProjectionTable}, {TeamProjectionTable}", ct);

        _log.LogInformation("Building band search team projection...");
        var teamSw = Stopwatch.StartNew();
        await ExecuteNonQueryAsync(conn, tx, BuildTeamProjectionSql(), ct, ("rebuiltAt", rebuiltAt));
        teamSw.Stop();
        _log.LogInformation("Built band search team projection in {Elapsed:n1}s", teamSw.Elapsed.TotalSeconds);

        _log.LogInformation("Building band search member projection...");
        var memberSw = Stopwatch.StartNew();
        await ExecuteNonQueryAsync(conn, tx, BuildMemberProjectionSql(), ct, ("rebuiltAt", rebuiltAt));
        memberSw.Stop();
        _log.LogInformation("Built band search member projection in {Elapsed:n1}s", memberSw.Elapsed.TotalSeconds);

        await tx.CommitAsync(ct);
        return (teamSw.Elapsed, memberSw.Elapsed);
    }

    private async Task<TimeSpan> BackfillBandIdsAndPublishStateAsync(DateTime rebuiltAt, CancellationToken ct)
    {
        _log.LogInformation("Backfilling deterministic band ids for band search projection...");
        var sw = Stopwatch.StartNew();

        await using var readConn = await _dataSource.OpenConnectionAsync(ct);
        await using var writeConn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await writeConn.BeginTransactionAsync(ct);

        await ExecuteNonQueryAsync(writeConn, tx, """
            CREATE TEMP TABLE _band_search_band_ids (
                band_type TEXT NOT NULL,
                team_key  TEXT NOT NULL,
                band_id   TEXT NOT NULL,
                PRIMARY KEY (band_type, team_key)
            ) ON COMMIT DROP
            """, ct);

        await using (var readCmd = readConn.CreateCommand())
        {
            readCmd.CommandTimeout = 0;
            readCmd.CommandText = $"""
                SELECT band_type, team_key
                FROM {TeamProjectionTable}
                ORDER BY band_type, team_key
                """;

            await using var reader = await readCmd.ExecuteReaderAsync(ct);
            await using var writer = await writeConn.BeginBinaryImportAsync(
                "COPY _band_search_band_ids (band_type, team_key, band_id) FROM STDIN (FORMAT BINARY)", ct);

            while (await reader.ReadAsync(ct))
            {
                var bandType = reader.GetString(0);
                var teamKey = reader.GetString(1);

                await writer.StartRowAsync(ct);
                await writer.WriteAsync(bandType, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(teamKey, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(BandIdentity.CreateBandId(bandType, teamKey), NpgsqlDbType.Text, ct);
            }

            await writer.CompleteAsync(ct);
        }

        await ExecuteNonQueryAsync(writeConn, tx, $"""
            UPDATE {TeamProjectionTable} team_projection
            SET band_id = ids.band_id
            FROM _band_search_band_ids ids
            WHERE team_projection.band_type = ids.band_type
              AND team_projection.team_key = ids.team_key
            """, ct);

        await ExecuteNonQueryAsync(writeConn, tx, $"""
            UPDATE {MemberProjectionTable} member_projection
            SET band_id = ids.band_id
            FROM _band_search_band_ids ids
            WHERE member_projection.band_type = ids.band_type
              AND member_projection.team_key = ids.team_key
            """, ct);

        await ExecuteNonQueryAsync(writeConn, tx, $"""
            INSERT INTO {StateTable} (id, rebuilt_at, refreshed_at, team_rows, member_rows)
            VALUES (
                TRUE,
                @rebuiltAt,
                @rebuiltAt,
                (SELECT COUNT(*) FROM {TeamProjectionTable}),
                (SELECT COUNT(*) FROM {MemberProjectionTable})
            )
            ON CONFLICT (id) DO UPDATE SET
                rebuilt_at = EXCLUDED.rebuilt_at,
                refreshed_at = EXCLUDED.rebuilt_at,
                team_rows = EXCLUDED.team_rows,
                member_rows = EXCLUDED.member_rows
            """, ct, ("rebuiltAt", rebuiltAt));

        await tx.CommitAsync(ct);
        sw.Stop();
        _log.LogInformation("Backfilled band ids and published projection state in {Elapsed:n1}s", sw.Elapsed.TotalSeconds);
        return sw.Elapsed;
    }

    private static async Task<int> ExecuteNonQueryAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string sql,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<DateTime?> GetIncrementalCutoffAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT COALESCE(refreshed_at, rebuilt_at) FROM {StateTable} WHERE id = TRUE";
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : (DateTime)value;
    }

    private static async Task CreateRefreshKeysTableAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await ExecuteNonQueryAsync(conn, tx, """
            CREATE TEMP TABLE _band_search_refresh_keys (
                band_type TEXT NOT NULL,
                team_key  TEXT NOT NULL,
                band_id   TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (band_type, team_key)
            ) ON COMMIT DROP
            """, ct);
    }

    private static async Task CreateSourceSummaryTableAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await ExecuteNonQueryAsync(conn, tx, """
            CREATE TEMP TABLE _band_search_source_summary AS
            SELECT band_type,
                   team_key,
                   COUNT(*)::integer AS source_appearance_count
            FROM band_entries
            GROUP BY band_type, team_key
            """, ct);

        await ExecuteNonQueryAsync(conn, tx, """
            CREATE UNIQUE INDEX ix_band_search_source_summary_key
            ON _band_search_source_summary (band_type, team_key)
            """, ct);

        await ExecuteNonQueryAsync(conn, tx, "ANALYZE _band_search_source_summary", ct);
    }

    private static async Task<BandSearchProjectionCatchUpCounts> CountCatchUpCandidatesAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        cmd.CommandText = $"""
            SELECT
                COUNT(*) FILTER (WHERE source_summary.team_key IS NULL) AS dead_projected_teams,
                COUNT(*) FILTER (
                    WHERE source_summary.team_key IS NOT NULL
                      AND source_summary.source_appearance_count <> team_projection.appearance_count
                ) AS stale_live_projected_teams,
                (
                    SELECT COUNT(*)
                    FROM _band_search_source_summary source_only
                    LEFT JOIN {TeamProjectionTable} projected
                      ON projected.band_type = source_only.band_type
                     AND projected.team_key = source_only.team_key
                    WHERE projected.team_key IS NULL
                ) AS new_source_teams
            FROM {TeamProjectionTable} team_projection
            LEFT JOIN _band_search_source_summary source_summary
              ON source_summary.band_type = team_projection.band_type
             AND source_summary.team_key = team_projection.team_key
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new BandSearchProjectionCatchUpCounts(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2));
    }

    private static async Task<int> InsertCatchUpRefreshKeysAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        return await ExecuteNonQueryAsync(conn, tx, $"""
            INSERT INTO _band_search_refresh_keys (band_type, team_key)
            SELECT keys.band_type, keys.team_key
            FROM (
                SELECT team_projection.band_type, team_projection.team_key
                FROM {TeamProjectionTable} team_projection
                LEFT JOIN _band_search_source_summary source_summary
                  ON source_summary.band_type = team_projection.band_type
                 AND source_summary.team_key = team_projection.team_key
                WHERE source_summary.team_key IS NULL
                   OR source_summary.source_appearance_count <> team_projection.appearance_count

                UNION

                SELECT source_summary.band_type, source_summary.team_key
                FROM _band_search_source_summary source_summary
                LEFT JOIN {TeamProjectionTable} team_projection
                  ON team_projection.band_type = source_summary.band_type
                 AND team_projection.team_key = source_summary.team_key
                WHERE team_projection.team_key IS NULL
            ) keys
            ON CONFLICT (band_type, team_key) DO NOTHING
            """, ct);
    }

    private static async Task<(long TeamRows, long MemberRows)> CountProjectionTablesAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        cmd.CommandText = $"""
            SELECT
                (SELECT COUNT(*) FROM {TeamProjectionTable}) AS team_rows,
                (SELECT COUNT(*) FROM {MemberProjectionTable}) AS member_rows
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task UpdateProjectionStateCountsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        DateTime refreshedAt,
        long teamRows,
        long memberRows,
        CancellationToken ct)
    {
        await ExecuteNonQueryAsync(conn, tx, $"""
            UPDATE {StateTable}
            SET refreshed_at = @refreshedAt,
                team_rows = @teamRows,
                member_rows = @memberRows
            WHERE id = TRUE
            """, ct,
            ("refreshedAt", refreshedAt),
            ("teamRows", teamRows),
            ("memberRows", memberRows));
    }

    private static async Task<int> CopyProvidedRefreshKeysAsync(
        NpgsqlConnection conn,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> teamsByBandType,
        CancellationToken ct)
    {
        var count = teamsByBandType.Sum(static kvp => kvp.Value.Count);
        if (count == 0)
            return 0;

        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY _band_search_refresh_keys (band_type, team_key, band_id) FROM STDIN (FORMAT BINARY)", ct);

        foreach (var (bandType, teamKeys) in teamsByBandType.OrderBy(static kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var teamKey in teamKeys.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(bandType) || string.IsNullOrWhiteSpace(teamKey))
                    continue;

                await writer.StartRowAsync(ct);
                await writer.WriteAsync(bandType, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(teamKey, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(BandIdentity.CreateBandId(bandType, teamKey), NpgsqlDbType.Text, ct);
            }
        }

        await writer.CompleteAsync(ct);
        return count;
    }

    private static async Task<int> InsertChangedRefreshKeysAsync(NpgsqlConnection conn, NpgsqlTransaction tx, DateTime cutoff, CancellationToken ct)
    {
        return await ExecuteNonQueryAsync(conn, tx, """
            INSERT INTO _band_search_refresh_keys (band_type, team_key)
            SELECT DISTINCT band_type, team_key
            FROM band_entries
            WHERE last_updated_at >= @cutoff
            ON CONFLICT (band_type, team_key) DO NOTHING
            """, ct, ("cutoff", cutoff));
    }

    private static async Task<int> CountRefreshKeysAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM _band_search_refresh_keys";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task FillMissingRefreshKeyBandIdsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await ExecuteNonQueryAsync(conn, tx, """
            CREATE TEMP TABLE _band_search_refresh_id_batch (
                band_type TEXT NOT NULL,
                team_key  TEXT NOT NULL,
                band_id   TEXT NOT NULL,
                PRIMARY KEY (band_type, team_key)
            ) ON COMMIT DROP
            """, ct);

        while (true)
        {
            var rows = new List<(string BandType, string TeamKey)>(25_000);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    SELECT band_type, team_key
                    FROM _band_search_refresh_keys
                    WHERE band_id = ''
                    ORDER BY band_type, team_key
                    LIMIT 25000
                    """;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    rows.Add((reader.GetString(0), reader.GetString(1)));
            }

            if (rows.Count == 0)
                return;

            await ExecuteNonQueryAsync(conn, tx, "TRUNCATE _band_search_refresh_id_batch", ct);
            await using (var writer = await conn.BeginBinaryImportAsync(
                "COPY _band_search_refresh_id_batch (band_type, team_key, band_id) FROM STDIN (FORMAT BINARY)", ct))
            {
                foreach (var (bandType, teamKey) in rows)
                {
                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(bandType, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(teamKey, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(BandIdentity.CreateBandId(bandType, teamKey), NpgsqlDbType.Text, ct);
                }

                await writer.CompleteAsync(ct);
            }

            await ExecuteNonQueryAsync(conn, tx, """
                UPDATE _band_search_refresh_keys keys
                SET band_id = batch.band_id
                FROM _band_search_refresh_id_batch batch
                WHERE keys.band_type = batch.band_type
                  AND keys.team_key = batch.team_key
                """, ct);
        }
    }

    private static async Task<(long TeamRows, long MemberRows)> CountExistingProjectionRowsForRefreshAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT
                (SELECT COUNT(*)
                 FROM {TeamProjectionTable} team_projection
                 JOIN _band_search_refresh_keys keys
                   ON keys.band_type = team_projection.band_type
                  AND keys.team_key = team_projection.team_key) AS team_rows,
                (SELECT COUNT(*)
                 FROM {MemberProjectionTable} member_projection
                 JOIN _band_search_refresh_keys keys
                   ON keys.band_type = member_projection.band_type
                  AND keys.team_key = member_projection.team_key) AS member_rows
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task DeleteProjectionRowsForRefreshAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await ExecuteNonQueryAsync(conn, tx, $"""
            DELETE FROM {MemberProjectionTable} member_projection
            USING _band_search_refresh_keys keys
            WHERE member_projection.band_type = keys.band_type
              AND member_projection.team_key = keys.team_key
            """, ct);

        await ExecuteNonQueryAsync(conn, tx, $"""
            DELETE FROM {TeamProjectionTable} team_projection
            USING _band_search_refresh_keys keys
            WHERE team_projection.band_type = keys.band_type
              AND team_projection.team_key = keys.team_key
            """, ct);
    }

    private static string BuildTeamProjectionSql() => $"""
        INSERT INTO {TeamProjectionTable} (
            band_type,
            team_key,
            band_id,
            appearance_count,
            member_account_ids,
            member_instruments_json,
            combo_appearances_json,
            updated_at)
        WITH team_combo_counts AS (
            SELECT band_type,
                   team_key,
                   instrument_combo,
                   COUNT(*)::integer AS appearance_count
            FROM band_entries
            GROUP BY band_type, team_key, instrument_combo
        ),
        team_summary AS (
            SELECT band_type,
                   team_key,
                   SUM(appearance_count)::integer AS appearance_count,
                   string_to_array(team_key, ':') AS member_account_ids,
                   jsonb_object_agg(COALESCE(instrument_combo, ''), appearance_count ORDER BY instrument_combo) AS combo_appearances_json
            FROM team_combo_counts
            GROUP BY band_type, team_key
        ),
        mapped_member_instruments AS (
            SELECT DISTINCT
                   band_type,
                   team_key,
                   account_id,
                   CASE instrument_id
                       WHEN 0 THEN 'Solo_Guitar'
                       WHEN 1 THEN 'Solo_Bass'
                       WHEN 2 THEN 'Solo_Vocals'
                       WHEN 3 THEN 'Solo_Drums'
                       WHEN 4 THEN 'Solo_PeripheralGuitar'
                       WHEN 5 THEN 'Solo_PeripheralBass'
                       WHEN 6 THEN 'Solo_PeripheralDrums'
                       WHEN 7 THEN 'Solo_PeripheralVocals'
                       WHEN 8 THEN 'Solo_PeripheralCymbals'
                   END AS instrument,
                   CASE instrument_id
                       WHEN 0 THEN 0
                       WHEN 1 THEN 1
                       WHEN 3 THEN 2
                       WHEN 2 THEN 3
                       WHEN 4 THEN 4
                       WHEN 5 THEN 5
                       WHEN 7 THEN 6
                       WHEN 8 THEN 7
                       WHEN 6 THEN 8
                       ELSE 99
                   END AS instrument_order
            FROM band_member_stats
            WHERE instrument_id BETWEEN 0 AND 8
        ),
        member_instruments AS (
            SELECT band_type,
                   team_key,
                   account_id,
                   array_agg(instrument ORDER BY instrument_order, instrument) AS instruments
            FROM mapped_member_instruments
            WHERE instrument IS NOT NULL
            GROUP BY band_type, team_key, account_id
        ),
        team_instruments AS (
            SELECT band_type,
                   team_key,
                   jsonb_object_agg(account_id, to_jsonb(instruments) ORDER BY account_id) AS member_instruments_json
            FROM member_instruments
            GROUP BY band_type, team_key
        )
        SELECT team_summary.band_type,
               team_summary.team_key,
               '' AS band_id,
               team_summary.appearance_count,
               team_summary.member_account_ids,
               COALESCE(team_instruments.member_instruments_json, jsonb_build_object()),
               COALESCE(team_summary.combo_appearances_json, jsonb_build_object()),
               @rebuiltAt
        FROM team_summary
        LEFT JOIN team_instruments
          ON team_instruments.band_type = team_summary.band_type
         AND team_instruments.team_key = team_summary.team_key
        """;

    private static string BuildMemberProjectionSql() => $"""
        INSERT INTO {MemberProjectionTable} (
            account_id,
            band_type,
            team_key,
            band_id,
            appearance_count,
            team_appearance_count,
            instrument_combos,
            updated_at)
        WITH member_combo_counts AS (
            SELECT account_id,
                   band_type,
                   team_key,
                   instrument_combo,
                   COUNT(*)::integer AS appearance_count
            FROM band_members
            GROUP BY account_id, band_type, team_key, instrument_combo
        ),
        member_summary AS (
            SELECT account_id,
                   band_type,
                   team_key,
                   SUM(appearance_count)::integer AS appearance_count,
                   array_agg(instrument_combo ORDER BY instrument_combo) AS instrument_combos
            FROM member_combo_counts
            GROUP BY account_id, band_type, team_key
        )
        SELECT member_summary.account_id,
               member_summary.band_type,
               member_summary.team_key,
               '' AS band_id,
               member_summary.appearance_count,
               team_projection.appearance_count AS team_appearance_count,
               member_summary.instrument_combos,
               @rebuiltAt
        FROM member_summary
        JOIN {TeamProjectionTable} team_projection
          ON team_projection.band_type = member_summary.band_type
         AND team_projection.team_key = member_summary.team_key
        """;

    private static string BuildTeamProjectionRefreshSql() => $"""
        INSERT INTO {TeamProjectionTable} (
            band_type,
            team_key,
            band_id,
            appearance_count,
            member_account_ids,
            member_instruments_json,
            combo_appearances_json,
            updated_at)
        WITH team_combo_counts AS (
            SELECT band_entries.band_type,
                   band_entries.team_key,
                   band_entries.instrument_combo,
                   COUNT(*)::integer AS appearance_count
            FROM band_entries
            JOIN _band_search_refresh_keys keys
              ON keys.band_type = band_entries.band_type
             AND keys.team_key = band_entries.team_key
            GROUP BY band_entries.band_type, band_entries.team_key, band_entries.instrument_combo
        ),
        team_summary AS (
            SELECT band_type,
                   team_key,
                   SUM(appearance_count)::integer AS appearance_count,
                   string_to_array(team_key, ':') AS member_account_ids,
                   jsonb_object_agg(COALESCE(instrument_combo, ''), appearance_count ORDER BY instrument_combo) AS combo_appearances_json
            FROM team_combo_counts
            GROUP BY band_type, team_key
        ),
        mapped_member_instruments AS (
            SELECT DISTINCT
                   band_member_stats.band_type,
                   band_member_stats.team_key,
                   band_member_stats.account_id,
                   CASE band_member_stats.instrument_id
                       WHEN 0 THEN 'Solo_Guitar'
                       WHEN 1 THEN 'Solo_Bass'
                       WHEN 2 THEN 'Solo_Vocals'
                       WHEN 3 THEN 'Solo_Drums'
                       WHEN 4 THEN 'Solo_PeripheralGuitar'
                       WHEN 5 THEN 'Solo_PeripheralBass'
                       WHEN 6 THEN 'Solo_PeripheralDrums'
                       WHEN 7 THEN 'Solo_PeripheralVocals'
                       WHEN 8 THEN 'Solo_PeripheralCymbals'
                   END AS instrument,
                   CASE band_member_stats.instrument_id
                       WHEN 0 THEN 0
                       WHEN 1 THEN 1
                       WHEN 3 THEN 2
                       WHEN 2 THEN 3
                       WHEN 4 THEN 4
                       WHEN 5 THEN 5
                       WHEN 7 THEN 6
                       WHEN 8 THEN 7
                       WHEN 6 THEN 8
                       ELSE 99
                   END AS instrument_order
            FROM band_member_stats
            JOIN _band_search_refresh_keys keys
              ON keys.band_type = band_member_stats.band_type
             AND keys.team_key = band_member_stats.team_key
            WHERE band_member_stats.instrument_id BETWEEN 0 AND 8
        ),
        member_instruments AS (
            SELECT band_type,
                   team_key,
                   account_id,
                   array_agg(instrument ORDER BY instrument_order, instrument) AS instruments
            FROM mapped_member_instruments
            WHERE instrument IS NOT NULL
            GROUP BY band_type, team_key, account_id
        ),
        team_instruments AS (
            SELECT band_type,
                   team_key,
                   jsonb_object_agg(account_id, to_jsonb(instruments) ORDER BY account_id) AS member_instruments_json
            FROM member_instruments
            GROUP BY band_type, team_key
        )
        SELECT team_summary.band_type,
               team_summary.team_key,
               keys.band_id,
               team_summary.appearance_count,
               team_summary.member_account_ids,
               COALESCE(team_instruments.member_instruments_json, jsonb_build_object()),
               COALESCE(team_summary.combo_appearances_json, jsonb_build_object()),
               @refreshedAt
        FROM team_summary
        JOIN _band_search_refresh_keys keys
          ON keys.band_type = team_summary.band_type
         AND keys.team_key = team_summary.team_key
        LEFT JOIN team_instruments
          ON team_instruments.band_type = team_summary.band_type
         AND team_instruments.team_key = team_summary.team_key
        """;

    private static string BuildMemberProjectionRefreshSql() => $"""
        INSERT INTO {MemberProjectionTable} (
            account_id,
            band_type,
            team_key,
            band_id,
            appearance_count,
            team_appearance_count,
            instrument_combos,
            updated_at)
        WITH member_combo_counts AS (
            SELECT band_members.account_id,
                   band_members.band_type,
                   band_members.team_key,
                   band_members.instrument_combo,
                   COUNT(*)::integer AS appearance_count
            FROM band_members
            JOIN _band_search_refresh_keys keys
              ON keys.band_type = band_members.band_type
             AND keys.team_key = band_members.team_key
            GROUP BY band_members.account_id, band_members.band_type, band_members.team_key, band_members.instrument_combo
        ),
        member_summary AS (
            SELECT account_id,
                   band_type,
                   team_key,
                   SUM(appearance_count)::integer AS appearance_count,
                   array_agg(instrument_combo ORDER BY instrument_combo) AS instrument_combos
            FROM member_combo_counts
            GROUP BY account_id, band_type, team_key
        )
        SELECT member_summary.account_id,
               member_summary.band_type,
               member_summary.team_key,
               team_projection.band_id,
               member_summary.appearance_count,
               team_projection.appearance_count AS team_appearance_count,
               member_summary.instrument_combos,
               @refreshedAt
        FROM member_summary
        JOIN {TeamProjectionTable} team_projection
          ON team_projection.band_type = member_summary.band_type
         AND team_projection.team_key = member_summary.team_key
        JOIN _band_search_refresh_keys keys
          ON keys.band_type = member_summary.band_type
         AND keys.team_key = member_summary.team_key
        """;
}

public sealed record BandSearchProjectionStats(
    long TeamRows,
    long MemberRows,
    DateTime? RebuiltAt,
    long? StateTeamRows,
    long? StateMemberRows);

public sealed record BandSearchProjectionBackfillResult(
    BandSearchProjectionStats Stats,
    double TeamLoadMs,
    double MemberLoadMs,
    double BandIdUpdateMs,
    double TotalElapsedMs);

public sealed record BandSearchProjectionIncrementalResult(
    bool ProjectionAvailable,
    int ImpactedTeams,
    int ProvidedTeams,
    int ChangedSourceTeams,
    long DeletedTeamRows,
    long InsertedTeamRows,
    long DeletedMemberRows,
    long InsertedMemberRows,
    double TotalElapsedMs);

public sealed record BandSearchProjectionCatchUpResult(
    int ImpactedTeams,
    long DeadProjectedTeams,
    long StaleLiveProjectedTeams,
    long NewSourceTeams,
    long DeletedTeamRows,
    long InsertedTeamRows,
    long DeletedMemberRows,
    long InsertedMemberRows,
    long FinalTeamRows,
    long FinalMemberRows,
    double TotalElapsedMs);

internal sealed record BandSearchProjectionCatchUpCounts(
    long DeadProjectedTeams,
    long StaleLiveProjectedTeams,
    long NewSourceTeams);
