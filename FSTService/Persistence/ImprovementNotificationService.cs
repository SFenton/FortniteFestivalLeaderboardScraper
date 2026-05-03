using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

public sealed class ImprovementNotificationService
{
    private const int DefaultLiveHours = 72;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ImprovementNotificationService> _log;

    public ImprovementNotificationService(NpgsqlDataSource dataSource, ILogger<ImprovementNotificationService> log)
    {
        _dataSource = dataSource;
        _log = log;
    }

    public ImprovementNotificationsEnvelope GetPlayerNotifications(
        string accountId,
        int limit = 50,
        bool includeExpired = false,
        string? kind = null,
        string? instrument = null,
        string? songId = null)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 200);
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT event_id,
                   run_id,
                   account_id,
                   NULL::BIGINT AS band_subject_id,
                   NULL::TEXT AS band_type,
                   NULL::TEXT AS team_key,
                   event_kind,
                   song_id,
                   instrument,
                   NULL::TEXT AS ranking_scope,
                   NULL::TEXT AS combo_id,
                   metric,
                   old_numeric,
                   new_numeric,
                   old_rank,
                   new_rank,
                   payload::TEXT,
                   detected_at,
                   expires_at
            FROM player_improvement_events
            WHERE account_id = @accountId
              AND (@includeExpired OR expires_at > now())
              AND (@kind IS NULL OR event_kind = @kind)
              AND (@instrument IS NULL OR instrument = @instrument)
              AND (@songId IS NULL OR song_id = @songId)
            ORDER BY detected_at DESC, event_id DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("includeExpired", includeExpired);
        cmd.Parameters.Add("kind", NpgsqlDbType.Text).Value = NullableValue(kind);
        cmd.Parameters.Add("instrument", NpgsqlDbType.Text).Value = NullableValue(instrument);
        cmd.Parameters.Add("songId", NpgsqlDbType.Text).Value = NullableValue(songId);
        cmd.Parameters.AddWithValue("limit", effectiveLimit);

        var items = ReadNotifications(cmd);
        return new ImprovementNotificationsEnvelope(DateTime.UtcNow, DefaultLiveHours, items);
    }

    public ImprovementNotificationsEnvelope GetBandNotificationsBySubject(
        long bandSubjectId,
        int limit = 50,
        bool includeExpired = false,
        string rankingScope = "overall",
        string? comboId = null,
        string? kind = null)
    {
        return GetBandNotificationsCore(bandSubjectId, null, null, limit, includeExpired, rankingScope, comboId, kind);
    }

    public ImprovementNotificationsEnvelope GetBandNotificationsByTeamKey(
        string bandType,
        string teamKey,
        int limit = 50,
        bool includeExpired = false,
        string rankingScope = "overall",
        string? comboId = null,
        string? kind = null)
    {
        return GetBandNotificationsCore(null, bandType, teamKey, limit, includeExpired, rankingScope, comboId, kind);
    }

    public ImprovementNotificationPrecomputeReport Precompute(ImprovementNotificationPrecomputeOptions options)
    {
        var startedAt = DateTime.UtcNow;
        var detectedAt = options.DetectedAtUtc ?? startedAt;
        var expiresAt = detectedAt.AddHours(DefaultLiveHours);
        var registeredOnly = options.Scope.Equals("registered", StringComparison.OrdinalIgnoreCase);
        var execute = options.Execute;
        var mode = options.BaselineOnly ? "baseline" : execute ? "execute" : "dry-run";
        var source = NormalizeSource(options.Source);
        long? runId = null;

        using var conn = _dataSource.OpenConnection();
        using var tx = execute ? conn.BeginTransaction() : null;

        var report = new ImprovementNotificationPrecomputeReport(
            StartedAtUtc: startedAt,
            CompletedAtUtc: null,
            Scope: registeredOnly ? "registered" : "all",
            Mode: mode,
            Execute: execute,
            BaselineOnly: options.BaselineOnly,
            IncludePlayers: options.IncludePlayers,
            IncludeBands: options.IncludeBands,
            IncludeSongEvents: options.IncludeSongEvents,
            IncludeRankings: options.IncludeRankings,
            PruneExpired: options.PruneExpired,
            RunId: null,
            PlayerSongRowsScanned: 0,
            PlayerSongEventsInserted: 0,
            PlayerSongStateUpserts: 0,
            PlayerRankRowsScanned: 0,
            PlayerRankEventsInserted: 0,
            PlayerRankStateUpserts: 0,
            BandSubjectsUpserted: 0,
            BandSongRowsScanned: 0,
            BandSongEventsInserted: 0,
            BandSongStateUpserts: 0,
            BandRankRowsScanned: 0,
            BandRankEventsInserted: 0,
            BandRankStateUpserts: 0,
            ExpiredPlayerEventsDeleted: 0,
            ExpiredBandEventsDeleted: 0,
            ErrorMessage: null);

        try
        {
            if (execute)
            {
                runId = InsertRun(conn, tx, options, mode, registeredOnly, source);
                report = report with { RunId = runId };
            }

            if (options.PruneExpired)
            {
                var expiredPlayer = PruneExpiredEvents(conn, tx, "player_improvement_events", execute, detectedAt);
                var expiredBand = PruneExpiredEvents(conn, tx, "band_improvement_events", execute, detectedAt);
                report = report with
                {
                    ExpiredPlayerEventsDeleted = expiredPlayer,
                    ExpiredBandEventsDeleted = expiredBand,
                };
            }

            if (options.IncludePlayers && options.IncludeSongEvents)
            {
                var rows = ExecuteScalarLong(conn, tx, CountPlayerSongRowsSql(registeredOnly), options.CommandTimeoutSeconds);
                var events = options.BaselineOnly
                    ? 0
                    : ExecuteScalarLong(conn, tx, PlayerSongEventsSql(registeredOnly, execute), options.CommandTimeoutSeconds, runId, detectedAt, expiresAt, source);
                var stateRows = execute
                    ? ExecuteScalarLong(conn, tx, PlayerSongStateUpsertSql(registeredOnly), options.CommandTimeoutSeconds, null, detectedAt, expiresAt)
                    : rows;

                report = report with
                {
                    PlayerSongRowsScanned = rows,
                    PlayerSongEventsInserted = events,
                    PlayerSongStateUpserts = stateRows,
                };
            }

            if (options.IncludePlayers && options.IncludeRankings)
            {
                var rows = ExecuteScalarLong(conn, tx, CountPlayerRankRowsSql(registeredOnly), options.CommandTimeoutSeconds);
                var events = options.BaselineOnly
                    ? 0
                    : ExecuteScalarLong(conn, tx, PlayerRankEventsSql(registeredOnly, execute), options.CommandTimeoutSeconds, runId, detectedAt, expiresAt, source);
                var stateRows = execute
                    ? ExecuteScalarLong(conn, tx, PlayerRankStateUpsertSql(registeredOnly), options.CommandTimeoutSeconds, null, detectedAt, expiresAt)
                    : rows;

                report = report with
                {
                    PlayerRankRowsScanned = rows,
                    PlayerRankEventsInserted = events,
                    PlayerRankStateUpserts = stateRows,
                };
            }

            if (options.IncludeBands)
            {
                var subjects = execute
                    ? ExecuteScalarLong(conn, tx, BandSubjectUpsertSql(registeredOnly), options.CommandTimeoutSeconds)
                    : ExecuteScalarLong(conn, tx, CountBandSubjectRowsSql(registeredOnly), options.CommandTimeoutSeconds);
                report = report with { BandSubjectsUpserted = subjects };
            }

            if (options.IncludeBands && options.IncludeSongEvents)
            {
                var rows = ExecuteScalarLong(conn, tx, CountBandSongRowsSql(registeredOnly), options.CommandTimeoutSeconds);
                var events = options.BaselineOnly
                    ? 0
                    : ExecuteScalarLong(conn, tx, BandSongEventsSql(registeredOnly, execute), options.CommandTimeoutSeconds, runId, detectedAt, expiresAt, source);
                var stateRows = execute
                    ? ExecuteScalarLong(conn, tx, BandSongStateUpsertSql(registeredOnly), options.CommandTimeoutSeconds, null, detectedAt, expiresAt)
                    : rows;

                report = report with
                {
                    BandSongRowsScanned = rows,
                    BandSongEventsInserted = events,
                    BandSongStateUpserts = stateRows,
                };
            }

            if (options.IncludeBands && options.IncludeRankings)
            {
                var rows = ExecuteScalarLong(conn, tx, CountBandRankRowsSql(registeredOnly), options.CommandTimeoutSeconds);
                var events = options.BaselineOnly
                    ? 0
                    : ExecuteScalarLong(conn, tx, BandRankEventsSql(registeredOnly, execute), options.CommandTimeoutSeconds, runId, detectedAt, expiresAt, source);
                var stateRows = execute
                    ? ExecuteScalarLong(conn, tx, BandRankStateUpsertSql(registeredOnly), options.CommandTimeoutSeconds, null, detectedAt, expiresAt)
                    : rows;

                report = report with
                {
                    BandRankRowsScanned = rows,
                    BandRankEventsInserted = events,
                    BandRankStateUpserts = stateRows,
                };
            }

            var completedAt = DateTime.UtcNow;
            report = report with { CompletedAtUtc = completedAt };

            if (execute && runId.HasValue)
            {
                UpdateRunSuccess(conn, tx!, runId.Value, report, completedAt);
                tx!.Commit();
            }

            return report;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Improvement notification precompute failed");
            if (execute && runId.HasValue)
            {
                try
                {
                    tx?.Rollback();
                    using var failConn = _dataSource.OpenConnection();
                    UpdateRunFailure(failConn, runId.Value, ex.Message);
                }
                catch (Exception rollbackEx)
                {
                    _log.LogWarning(rollbackEx, "Failed to record notification precompute failure");
                }
            }

            return report with { CompletedAtUtc = DateTime.UtcNow, ErrorMessage = ex.Message };
        }
    }

    private ImprovementNotificationsEnvelope GetBandNotificationsCore(
        long? bandSubjectId,
        string? bandType,
        string? teamKey,
        int limit,
        bool includeExpired,
        string rankingScope,
        string? comboId,
        string? kind)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 200);
        var normalizedScope = string.IsNullOrWhiteSpace(rankingScope) ? "overall" : rankingScope.Trim().ToLowerInvariant();
        if (normalizedScope is not ("overall" or "combo" or "all"))
            normalizedScope = "overall";

        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.event_id,
                   e.run_id,
                   NULL::TEXT AS account_id,
                   s.band_subject_id,
                   s.band_type,
                   s.team_key,
                   e.event_kind,
                   e.song_id,
                   NULL::TEXT AS instrument,
                   e.ranking_scope,
                   e.combo_id,
                   e.metric,
                   e.old_numeric,
                   e.new_numeric,
                   e.old_rank,
                   e.new_rank,
                   (e.payload || jsonb_build_object('teamMembers', s.team_members))::TEXT,
                   e.detected_at,
                   e.expires_at
            FROM band_improvement_events e
            JOIN band_improvement_subjects s ON s.band_subject_id = e.band_subject_id
            WHERE (@bandSubjectId IS NULL OR s.band_subject_id = @bandSubjectId)
              AND (@bandType IS NULL OR s.band_type = @bandType)
              AND (@teamKey IS NULL OR s.team_key = @teamKey)
              AND (@includeExpired OR e.expires_at > now())
              AND (@kind IS NULL OR e.event_kind = @kind)
              AND (@rankingScope = 'all' OR e.ranking_scope = @rankingScope)
              AND (@comboId IS NULL OR e.combo_id = @comboId)
            ORDER BY e.detected_at DESC, e.event_id DESC
            LIMIT @limit;
            """;
        cmd.Parameters.Add("bandSubjectId", NpgsqlDbType.Bigint).Value = NullableValue(bandSubjectId);
        cmd.Parameters.Add("bandType", NpgsqlDbType.Text).Value = NullableValue(bandType);
        cmd.Parameters.Add("teamKey", NpgsqlDbType.Text).Value = NullableValue(teamKey);
        cmd.Parameters.AddWithValue("includeExpired", includeExpired);
        cmd.Parameters.Add("kind", NpgsqlDbType.Text).Value = NullableValue(kind);
        cmd.Parameters.AddWithValue("rankingScope", normalizedScope);
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = NullableValue(comboId);
        cmd.Parameters.AddWithValue("limit", effectiveLimit);

        var items = ReadNotifications(cmd);
        return new ImprovementNotificationsEnvelope(DateTime.UtcNow, DefaultLiveHours, items);
    }

    private static IReadOnlyList<ImprovementNotificationDto> ReadNotifications(NpgsqlCommand cmd)
    {
        var items = new List<ImprovementNotificationDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var payloadJson = reader.GetString(16);
            var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson);
            items.Add(new ImprovementNotificationDto(
                EventId: reader.GetInt64(0),
                RunId: reader.IsDBNull(1) ? null : reader.GetInt64(1),
                AccountId: reader.IsDBNull(2) ? null : reader.GetString(2),
                BandSubjectId: reader.IsDBNull(3) ? null : reader.GetInt64(3),
                BandType: reader.IsDBNull(4) ? null : reader.GetString(4),
                TeamKey: reader.IsDBNull(5) ? null : reader.GetString(5),
                EventKind: reader.GetString(6),
                SongId: reader.IsDBNull(7) ? null : reader.GetString(7),
                Instrument: reader.IsDBNull(8) ? null : reader.GetString(8),
                RankingScope: reader.IsDBNull(9) ? null : reader.GetString(9),
                ComboId: reader.IsDBNull(10) ? null : reader.GetString(10),
                Metric: reader.IsDBNull(11) ? null : reader.GetString(11),
                OldNumeric: reader.IsDBNull(12) ? null : reader.GetDecimal(12),
                NewNumeric: reader.IsDBNull(13) ? null : reader.GetDecimal(13),
                OldRank: reader.IsDBNull(14) ? null : reader.GetInt32(14),
                NewRank: reader.IsDBNull(15) ? null : reader.GetInt32(15),
                Payload: payload,
                DetectedAt: reader.GetDateTime(17),
                ExpiresAt: reader.GetDateTime(18)));
        }

        return items;
    }

    private static long InsertRun(NpgsqlConnection conn, NpgsqlTransaction? tx, ImprovementNotificationPrecomputeOptions options, string mode, bool registeredOnly, string source)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO improvement_detection_runs (
                scope, mode, source, baseline_only, include_players, include_bands,
                include_song_events, include_rankings, prune_expired)
            VALUES (@scope, @mode, @source, @baselineOnly, @includePlayers, @includeBands,
                    @includeSongEvents, @includeRankings, @pruneExpired)
            RETURNING run_id;
            """;
        cmd.Parameters.AddWithValue("scope", registeredOnly ? "registered" : "all");
        cmd.Parameters.AddWithValue("mode", mode);
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("baselineOnly", options.BaselineOnly);
        cmd.Parameters.AddWithValue("includePlayers", options.IncludePlayers);
        cmd.Parameters.AddWithValue("includeBands", options.IncludeBands);
        cmd.Parameters.AddWithValue("includeSongEvents", options.IncludeSongEvents);
        cmd.Parameters.AddWithValue("includeRankings", options.IncludeRankings);
        cmd.Parameters.AddWithValue("pruneExpired", options.PruneExpired);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void UpdateRunSuccess(NpgsqlConnection conn, NpgsqlTransaction tx, long runId, ImprovementNotificationPrecomputeReport report, DateTime completedAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE improvement_detection_runs
            SET completed_at = @completedAt,
                status = 'completed',
                player_song_rows_scanned = @playerSongRowsScanned,
                player_song_events_inserted = @playerSongEventsInserted,
                player_song_state_upserts = @playerSongStateUpserts,
                player_rank_rows_scanned = @playerRankRowsScanned,
                player_rank_events_inserted = @playerRankEventsInserted,
                player_rank_state_upserts = @playerRankStateUpserts,
                band_subjects_upserted = @bandSubjectsUpserted,
                band_song_rows_scanned = @bandSongRowsScanned,
                band_song_events_inserted = @bandSongEventsInserted,
                band_song_state_upserts = @bandSongStateUpserts,
                band_rank_rows_scanned = @bandRankRowsScanned,
                band_rank_events_inserted = @bandRankEventsInserted,
                band_rank_state_upserts = @bandRankStateUpserts,
                expired_player_events_deleted = @expiredPlayerEventsDeleted,
                expired_band_events_deleted = @expiredBandEventsDeleted
            WHERE run_id = @runId;
            """;
        AddReportParameters(cmd, report);
        cmd.Parameters.AddWithValue("completedAt", completedAt);
        cmd.Parameters.AddWithValue("runId", runId);
        cmd.ExecuteNonQuery();
    }

    private static void UpdateRunFailure(NpgsqlConnection conn, long runId, string errorMessage)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE improvement_detection_runs
            SET completed_at = now(), status = 'failed', error_message = @errorMessage
            WHERE run_id = @runId;
            """;
        cmd.Parameters.AddWithValue("errorMessage", errorMessage);
        cmd.Parameters.AddWithValue("runId", runId);
        cmd.ExecuteNonQuery();
    }

    private static void AddReportParameters(NpgsqlCommand cmd, ImprovementNotificationPrecomputeReport report)
    {
        cmd.Parameters.AddWithValue("playerSongRowsScanned", report.PlayerSongRowsScanned);
        cmd.Parameters.AddWithValue("playerSongEventsInserted", report.PlayerSongEventsInserted);
        cmd.Parameters.AddWithValue("playerSongStateUpserts", report.PlayerSongStateUpserts);
        cmd.Parameters.AddWithValue("playerRankRowsScanned", report.PlayerRankRowsScanned);
        cmd.Parameters.AddWithValue("playerRankEventsInserted", report.PlayerRankEventsInserted);
        cmd.Parameters.AddWithValue("playerRankStateUpserts", report.PlayerRankStateUpserts);
        cmd.Parameters.AddWithValue("bandSubjectsUpserted", report.BandSubjectsUpserted);
        cmd.Parameters.AddWithValue("bandSongRowsScanned", report.BandSongRowsScanned);
        cmd.Parameters.AddWithValue("bandSongEventsInserted", report.BandSongEventsInserted);
        cmd.Parameters.AddWithValue("bandSongStateUpserts", report.BandSongStateUpserts);
        cmd.Parameters.AddWithValue("bandRankRowsScanned", report.BandRankRowsScanned);
        cmd.Parameters.AddWithValue("bandRankEventsInserted", report.BandRankEventsInserted);
        cmd.Parameters.AddWithValue("bandRankStateUpserts", report.BandRankStateUpserts);
        cmd.Parameters.AddWithValue("expiredPlayerEventsDeleted", report.ExpiredPlayerEventsDeleted);
        cmd.Parameters.AddWithValue("expiredBandEventsDeleted", report.ExpiredBandEventsDeleted);
    }

    private static long PruneExpiredEvents(NpgsqlConnection conn, NpgsqlTransaction? tx, string tableName, bool execute, DateTime detectedAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = execute
            ? $"DELETE FROM {tableName} WHERE expires_at <= @detectedAt;"
            : $"SELECT COUNT(*) FROM {tableName} WHERE expires_at <= @detectedAt;";
        cmd.Parameters.AddWithValue("detectedAt", detectedAt);
        return execute ? cmd.ExecuteNonQuery() : Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static long ExecuteScalarLong(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string sql,
        int commandTimeoutSeconds,
        long? runId = null,
        DateTime? detectedAt = null,
        DateTime? expiresAt = null,
        string? source = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = commandTimeoutSeconds <= 0 ? 0 : commandTimeoutSeconds;
        cmd.CommandText = sql;
        if (runId is not null)
            cmd.Parameters.AddWithValue("runId", runId.Value);
        if (detectedAt is not null)
            cmd.Parameters.AddWithValue("detectedAt", detectedAt.Value);
        if (expiresAt is not null)
            cmd.Parameters.AddWithValue("expiresAt", expiresAt.Value);
        if (source is not null && sql.Contains("@source", StringComparison.Ordinal))
            cmd.Parameters.AddWithValue("source", NormalizeSource(source));
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static string NormalizeSource(string? source) => string.IsNullOrWhiteSpace(source)
        ? "precompute"
        : source.Trim().Length > 64
            ? source.Trim()[..64]
            : source.Trim();

    private static object NullableValue(string? value) => value is null ? DBNull.Value : value;

    private static object NullableValue(long? value) => value is null ? DBNull.Value : value.Value;

    private static string RegisteredPlayerFilter(bool registeredOnly, string alias = "c") => registeredOnly
        ? $"WHERE EXISTS (SELECT 1 FROM (SELECT DISTINCT account_id FROM registered_users) ru WHERE ru.account_id = {alias}.account_id)"
        : string.Empty;

    private static string RegisteredBandCurrentFilter(bool registeredOnly, string alias = "c") => registeredOnly
        ? $"WHERE EXISTS (SELECT 1 FROM registered_bands rb WHERE rb.band_type = {alias}.band_type AND rb.team_key = {alias}.team_key)"
        : string.Empty;

    private static string RegisteredBandRankFilter(bool registeredOnly, string alias = "r") => registeredOnly
        ? $"WHERE EXISTS (SELECT 1 FROM registered_bands rb WHERE rb.band_type = {alias}.band_type AND rb.team_key = {alias}.team_key)"
        : string.Empty;

    private static string BandRankUnionSql => """
        SELECT band_type, ranking_scope, combo_id, team_key, team_members,
               adjusted_skill_rank, weighted_rank, fc_rate_rank, total_score_rank,
               total_score, full_combo_count, computed_at
        FROM band_team_rankings_current_band_duets
        UNION ALL
        SELECT band_type, ranking_scope, combo_id, team_key, team_members,
               adjusted_skill_rank, weighted_rank, fc_rate_rank, total_score_rank,
               total_score, full_combo_count, computed_at
        FROM band_team_rankings_current_band_trios
        UNION ALL
        SELECT band_type, ranking_scope, combo_id, team_key, team_members,
               adjusted_skill_rank, weighted_rank, fc_rate_rank, total_score_rank,
               total_score, full_combo_count, computed_at
        FROM band_team_rankings_current_band_quad
        """;

    private static string CountPlayerSongRowsSql(bool registeredOnly) => $"""
        SELECT COUNT(*)
        FROM current_leaderboard_entries c
        {RegisteredPlayerFilter(registeredOnly)};
        """;

    private static string CountPlayerRankRowsSql(bool registeredOnly) => $"""
        SELECT COUNT(*)
        FROM account_rankings c
        {RegisteredPlayerFilter(registeredOnly)};
        """;

    private static string CountBandSongRowsSql(bool registeredOnly) => $"""
        SELECT COUNT(*)
        FROM current_band_leaderboard_entries c
        {RegisteredBandCurrentFilter(registeredOnly)};
        """;

    private static string CountBandRankRowsSql(bool registeredOnly) => $"""
        WITH current_rows AS (
            SELECT * FROM ({BandRankUnionSql}) r
            {RegisteredBandRankFilter(registeredOnly)}
        )
        SELECT COUNT(*) FROM current_rows;
        """;

    private static string CountBandSubjectRowsSql(bool registeredOnly) => $"""
        WITH subject_rows AS (
            SELECT DISTINCT c.band_type, c.team_key
            FROM current_band_leaderboard_entries c
            {RegisteredBandCurrentFilter(registeredOnly)}
            UNION
            SELECT DISTINCT r.band_type, r.team_key
            FROM ({BandRankUnionSql}) r
            {RegisteredBandRankFilter(registeredOnly)}
        )
        SELECT COUNT(*) FROM subject_rows;
        """;

    private static string PlayerSongEventsSql(bool registeredOnly, bool execute) => $"""
        WITH current_rows AS (
            SELECT c.*
            FROM current_leaderboard_entries c
            {RegisteredPlayerFilter(registeredOnly)}
        ), event_rows AS (
            SELECT c.account_id,
                   v.event_kind,
                   c.song_id,
                   c.instrument,
                   v.metric,
                   v.old_numeric,
                   v.new_numeric,
                   v.old_rank,
                   v.new_rank,
                   jsonb_build_object(
                       'oldScore', s.score,
                       'newScore', c.score,
                       'oldRank', s.rank,
                       'newRank', c.rank,
                       'oldStars', s.stars,
                       'newStars', c.stars,
                       'oldFullCombo', s.is_full_combo,
                       'newFullCombo', c.is_full_combo,
                       'oldDifficulty', s.difficulty,
                       'newDifficulty', c.difficulty,
                       'percentile', c.percentile,
                       'season', c.season
                   ) AS payload
            FROM current_rows c
            LEFT JOIN player_improvement_state s
              ON s.account_id = c.account_id
             AND s.song_id = c.song_id
             AND s.instrument = c.instrument
            CROSS JOIN LATERAL (VALUES
                ('player_first_score', 'score', NULL::NUMERIC, c.score::NUMERIC, NULL::INTEGER, c.rank, s.account_id IS NULL),
                ('player_score_pb', 'score', s.score::NUMERIC, c.score::NUMERIC, NULL::INTEGER, NULL::INTEGER, s.account_id IS NOT NULL AND c.score > COALESCE(s.score, -1)),
                ('player_song_rank_improved', 'song_rank', NULL::NUMERIC, NULL::NUMERIC, s.rank, c.rank, s.rank IS NOT NULL AND c.rank IS NOT NULL AND c.rank > 0 AND c.rank < s.rank),
                ('player_stars_improved', 'stars', s.stars::NUMERIC, c.stars::NUMERIC, NULL::INTEGER, NULL::INTEGER, s.account_id IS NOT NULL AND c.stars IS NOT NULL AND s.stars IS NOT NULL AND c.stars > s.stars),
                ('player_gold_stars_achieved', 'stars', s.stars::NUMERIC, c.stars::NUMERIC, NULL::INTEGER, NULL::INTEGER, c.stars >= 6 AND (s.account_id IS NULL OR COALESCE(s.stars, 0) < 6)),
                ('player_fc_achieved', 'full_combo', NULL::NUMERIC, NULL::NUMERIC, NULL::INTEGER, NULL::INTEGER, c.is_full_combo IS TRUE AND (s.account_id IS NULL OR COALESCE(s.is_full_combo, false) = false)),
                ('player_difficulty_bumped', 'difficulty', s.difficulty::NUMERIC, c.difficulty::NUMERIC, NULL::INTEGER, NULL::INTEGER, s.account_id IS NOT NULL AND c.difficulty IS NOT NULL AND s.difficulty IS NOT NULL AND c.difficulty > s.difficulty)
            ) AS v(event_kind, metric, old_numeric, new_numeric, old_rank, new_rank, should_emit)
            WHERE v.should_emit
        )
        {EventSelectOrInsertSql("player_improvement_events", execute, "account_id, event_kind, song_id, instrument, metric, old_numeric, new_numeric, old_rank, new_rank, payload", "account_id, event_kind, song_id, instrument, metric, old_numeric, new_numeric, old_rank, new_rank, payload")}
        """;

    private static string PlayerRankEventsSql(bool registeredOnly, bool execute) => $"""
        WITH current_rows AS (
            SELECT c.*
            FROM account_rankings c
            {RegisteredPlayerFilter(registeredOnly)}
        ), event_rows AS (
            SELECT c.account_id,
                   v.event_kind,
                   c.instrument,
                   v.metric,
                   v.old_numeric,
                   v.new_numeric,
                   v.old_rank,
                   v.new_rank,
                   jsonb_build_object(
                       'oldAdjustedSkillRank', s.adjusted_skill_rank,
                       'newAdjustedSkillRank', c.adjusted_skill_rank,
                       'oldWeightedRank', s.weighted_rank,
                       'newWeightedRank', c.weighted_rank,
                       'oldFcRateRank', s.fc_rate_rank,
                       'newFcRateRank', c.fc_rate_rank,
                       'oldTotalScoreRank', s.total_score_rank,
                       'newTotalScoreRank', c.total_score_rank,
                       'oldTotalScore', s.total_score,
                       'newTotalScore', c.total_score,
                       'oldFullComboCount', s.full_combo_count,
                       'newFullComboCount', c.full_combo_count
                   ) AS payload
            FROM current_rows c
            LEFT JOIN player_rank_improvement_state s
              ON s.account_id = c.account_id
             AND s.instrument = c.instrument
            CROSS JOIN LATERAL (VALUES
                ('player_weighted_rank_improved', 'weighted_rank', NULL::NUMERIC, NULL::NUMERIC, s.weighted_rank, c.weighted_rank, s.weighted_rank IS NOT NULL AND c.weighted_rank IS NOT NULL AND c.weighted_rank > 0 AND c.weighted_rank < s.weighted_rank),
                ('player_skill_rank_improved', 'adjusted_skill_rank', NULL::NUMERIC, NULL::NUMERIC, s.adjusted_skill_rank, c.adjusted_skill_rank, s.adjusted_skill_rank IS NOT NULL AND c.adjusted_skill_rank IS NOT NULL AND c.adjusted_skill_rank > 0 AND c.adjusted_skill_rank < s.adjusted_skill_rank),
                ('player_total_score_rank_improved', 'total_score_rank', NULL::NUMERIC, NULL::NUMERIC, s.total_score_rank, c.total_score_rank, s.total_score_rank IS NOT NULL AND c.total_score_rank IS NOT NULL AND c.total_score_rank > 0 AND c.total_score_rank < s.total_score_rank),
                ('player_fc_rate_rank_improved', 'fc_rate_rank', NULL::NUMERIC, NULL::NUMERIC, s.fc_rate_rank, c.fc_rate_rank, s.fc_rate_rank IS NOT NULL AND c.fc_rate_rank IS NOT NULL AND c.fc_rate_rank > 0 AND c.fc_rate_rank < s.fc_rate_rank),
                ('player_total_score_improved', 'total_score', s.total_score::NUMERIC, c.total_score::NUMERIC, NULL::INTEGER, NULL::INTEGER, s.total_score IS NOT NULL AND c.total_score IS NOT NULL AND c.total_score > s.total_score),
                ('player_fc_count_improved', 'full_combo_count', s.full_combo_count::NUMERIC, c.full_combo_count::NUMERIC, NULL::INTEGER, NULL::INTEGER, s.full_combo_count IS NOT NULL AND c.full_combo_count IS NOT NULL AND c.full_combo_count > s.full_combo_count)
            ) AS v(event_kind, metric, old_numeric, new_numeric, old_rank, new_rank, should_emit)
            WHERE v.should_emit
        )
        {EventSelectOrInsertSql("player_improvement_events", execute, "account_id, event_kind, instrument, metric, old_numeric, new_numeric, old_rank, new_rank, payload", "account_id, event_kind, instrument, metric, old_numeric, new_numeric, old_rank, new_rank, payload")}
        """;

    private static string EventSelectOrInsertSql(string tableName, bool execute, string columns, string selectColumns)
    {
        if (!execute)
            return "SELECT COUNT(*) FROM event_rows;";

        return $"""
            , inserted AS (
                INSERT INTO {tableName} (run_id, {columns}, detected_at, expires_at, source)
                SELECT @runId, {selectColumns}, @detectedAt, @expiresAt, @source
                FROM event_rows
                RETURNING 1
            )
            SELECT COUNT(*) FROM inserted;
            """;
    }

    private static string PlayerSongStateUpsertSql(bool registeredOnly) => $"""
        WITH current_rows AS (
            SELECT c.*
            FROM current_leaderboard_entries c
            {RegisteredPlayerFilter(registeredOnly)}
        ), upserted AS (
            INSERT INTO player_improvement_state (
                account_id, song_id, instrument, score, rank, stars, is_full_combo,
                difficulty, percentile, season, first_seen_at, last_updated_at, observed_at, updated_at)
            SELECT account_id, song_id, instrument, score, rank, stars, is_full_combo,
                   difficulty, percentile, season, first_seen_at, last_updated_at, @detectedAt, now()
            FROM current_rows
            ON CONFLICT (account_id, song_id, instrument) DO UPDATE
            SET score = EXCLUDED.score,
                rank = EXCLUDED.rank,
                stars = EXCLUDED.stars,
                is_full_combo = EXCLUDED.is_full_combo,
                difficulty = EXCLUDED.difficulty,
                percentile = EXCLUDED.percentile,
                season = EXCLUDED.season,
                first_seen_at = EXCLUDED.first_seen_at,
                last_updated_at = EXCLUDED.last_updated_at,
                observed_at = EXCLUDED.observed_at,
                updated_at = now()
            RETURNING 1
        )
        SELECT COUNT(*) FROM upserted;
        """;

    private static string PlayerRankStateUpsertSql(bool registeredOnly) => $"""
        WITH current_rows AS (
            SELECT c.*
            FROM account_rankings c
            {RegisteredPlayerFilter(registeredOnly)}
        ), upserted AS (
            INSERT INTO player_rank_improvement_state (
                account_id, instrument, adjusted_skill_rank, weighted_rank, fc_rate_rank,
                total_score_rank, max_score_percent_rank, total_score, full_combo_count,
                computed_at, observed_at, updated_at)
            SELECT account_id, instrument, adjusted_skill_rank, weighted_rank, fc_rate_rank,
                   total_score_rank, max_score_percent_rank, total_score, full_combo_count,
                   computed_at, @detectedAt, now()
            FROM current_rows
            ON CONFLICT (account_id, instrument) DO UPDATE
            SET adjusted_skill_rank = EXCLUDED.adjusted_skill_rank,
                weighted_rank = EXCLUDED.weighted_rank,
                fc_rate_rank = EXCLUDED.fc_rate_rank,
                total_score_rank = EXCLUDED.total_score_rank,
                max_score_percent_rank = EXCLUDED.max_score_percent_rank,
                total_score = EXCLUDED.total_score,
                full_combo_count = EXCLUDED.full_combo_count,
                computed_at = EXCLUDED.computed_at,
                observed_at = EXCLUDED.observed_at,
                updated_at = now()
            RETURNING 1
        )
        SELECT COUNT(*) FROM upserted;
        """;

    private static string BandSubjectUpsertSql(bool registeredOnly) => $"""
        WITH source_rows AS (
            SELECT c.band_type, c.team_key, c.team_members, MIN(c.first_seen_at) AS first_seen_at, MAX(c.last_updated_at) AS last_seen_at
            FROM current_band_leaderboard_entries c
            {RegisteredBandCurrentFilter(registeredOnly)}
            GROUP BY c.band_type, c.team_key, c.team_members
            UNION ALL
            SELECT r.band_type, r.team_key, r.team_members, MIN(r.computed_at) AS first_seen_at, MAX(r.computed_at) AS last_seen_at
            FROM ({BandRankUnionSql}) r
            {RegisteredBandRankFilter(registeredOnly)}
            GROUP BY r.band_type, r.team_key, r.team_members
        ), collapsed AS (
                 SELECT band_type,
                     team_key,
                     string_to_array(MIN(COALESCE(array_to_string(team_members, chr(31)), '')), chr(31)) AS team_members,
                     MIN(first_seen_at) AS first_seen_at,
                     MAX(last_seen_at) AS last_seen_at
            FROM source_rows
            GROUP BY band_type, team_key
        ), upserted AS (
            INSERT INTO band_improvement_subjects (band_type, team_key, team_members, first_seen_at, last_seen_at, updated_at)
            SELECT band_type, team_key, COALESCE(team_members, ARRAY[]::TEXT[]), first_seen_at, last_seen_at, now()
            FROM collapsed
            ON CONFLICT (band_type, team_key) DO UPDATE
            SET team_members = EXCLUDED.team_members,
                first_seen_at = COALESCE(band_improvement_subjects.first_seen_at, EXCLUDED.first_seen_at),
                last_seen_at = GREATEST(COALESCE(band_improvement_subjects.last_seen_at, '-infinity'::timestamptz), COALESCE(EXCLUDED.last_seen_at, '-infinity'::timestamptz)),
                updated_at = now()
            RETURNING 1
        )
        SELECT COUNT(*) FROM upserted;
        """;

    private static string BandSongEventsSql(bool registeredOnly, bool execute) => $"""
        WITH current_rows AS (
            SELECT c.*, s.band_subject_id, s.team_members AS subject_members
            FROM current_band_leaderboard_entries c
            JOIN band_improvement_subjects s ON s.band_type = c.band_type AND s.team_key = c.team_key
            {RegisteredBandCurrentFilter(registeredOnly)}
        ), event_rows AS (
            SELECT c.band_subject_id,
                   v.event_kind,
                   c.song_id,
                   c.ranking_scope,
                   COALESCE(c.scope_combo_id, '') AS combo_id,
                   v.metric,
                   v.old_numeric,
                   v.new_numeric,
                   v.old_rank,
                   v.new_rank,
                   jsonb_build_object(
                       'bandType', c.band_type,
                       'teamKey', c.team_key,
                       'teamMembers', c.subject_members,
                       'rankingScope', c.ranking_scope,
                       'scopeComboId', COALESCE(c.scope_combo_id, ''),
                       'entryComboId', c.entry_combo_id,
                       'entryInstrumentCombo', c.entry_instrument_combo,
                       'oldScore', s.score,
                       'newScore', c.score,
                       'oldRank', s.rank,
                       'newRank', c.rank,
                       'oldStars', s.stars,
                       'newStars', c.stars,
                       'oldFullCombo', s.is_full_combo,
                       'newFullCombo', c.is_full_combo,
                       'oldDifficulty', s.difficulty,
                       'newDifficulty', c.difficulty
                   ) AS payload
            FROM current_rows c
            LEFT JOIN band_improvement_state s
              ON s.band_subject_id = c.band_subject_id
             AND s.song_id = c.song_id
             AND s.ranking_scope = c.ranking_scope
             AND s.scope_combo_id = COALESCE(c.scope_combo_id, '')
            CROSS JOIN LATERAL (VALUES
                ('band_first_score', 'score', NULL::NUMERIC, c.score::NUMERIC, NULL::INTEGER, c.rank, s.band_subject_id IS NULL),
                (CASE WHEN c.ranking_scope = 'combo' THEN 'band_combo_score_pb' ELSE 'band_score_pb' END, 'score', s.score::NUMERIC, c.score::NUMERIC, NULL::INTEGER, NULL::INTEGER, s.band_subject_id IS NOT NULL AND c.score > COALESCE(s.score, -1)),
                ('band_song_rank_improved', 'song_rank', NULL::NUMERIC, NULL::NUMERIC, s.rank, c.rank, s.rank IS NOT NULL AND c.rank IS NOT NULL AND c.rank > 0 AND c.rank < s.rank),
                ('band_stars_improved', 'stars', s.stars::NUMERIC, c.stars::NUMERIC, NULL::INTEGER, NULL::INTEGER, s.band_subject_id IS NOT NULL AND c.stars IS NOT NULL AND s.stars IS NOT NULL AND c.stars > s.stars),
                ('band_gold_stars_achieved', 'stars', s.stars::NUMERIC, c.stars::NUMERIC, NULL::INTEGER, NULL::INTEGER, c.stars >= 6 AND (s.band_subject_id IS NULL OR COALESCE(s.stars, 0) < 6)),
                ('band_fc_achieved', 'full_combo', NULL::NUMERIC, NULL::NUMERIC, NULL::INTEGER, NULL::INTEGER, c.is_full_combo IS TRUE AND (s.band_subject_id IS NULL OR COALESCE(s.is_full_combo, false) = false)),
                ('band_member_difficulty_bumped', 'difficulty', s.difficulty::NUMERIC, c.difficulty::NUMERIC, NULL::INTEGER, NULL::INTEGER, s.band_subject_id IS NOT NULL AND c.difficulty IS NOT NULL AND s.difficulty IS NOT NULL AND c.difficulty > s.difficulty)
            ) AS v(event_kind, metric, old_numeric, new_numeric, old_rank, new_rank, should_emit)
            WHERE v.should_emit
        )
        {EventSelectOrInsertSql("band_improvement_events", execute, "band_subject_id, event_kind, song_id, ranking_scope, combo_id, metric, old_numeric, new_numeric, old_rank, new_rank, payload", "band_subject_id, event_kind, song_id, ranking_scope, combo_id, metric, old_numeric, new_numeric, old_rank, new_rank, payload")}
        """;

    private static string BandSongStateUpsertSql(bool registeredOnly) => $"""
        WITH current_rows AS (
            SELECT c.*, s.band_subject_id
            FROM current_band_leaderboard_entries c
            JOIN band_improvement_subjects s ON s.band_type = c.band_type AND s.team_key = c.team_key
            {RegisteredBandCurrentFilter(registeredOnly)}
        ), upserted AS (
            INSERT INTO band_improvement_state (
                band_subject_id, song_id, ranking_scope, scope_combo_id, entry_combo_id,
                entry_instrument_combo, score, rank, stars, is_full_combo, difficulty,
                percentile, season, total_entries, first_seen_at, last_updated_at, observed_at, updated_at)
            SELECT band_subject_id, song_id, ranking_scope, COALESCE(scope_combo_id, ''), entry_combo_id,
                   entry_instrument_combo, score, rank, stars, is_full_combo, difficulty,
                   percentile, season, total_entries, first_seen_at, last_updated_at, @detectedAt, now()
            FROM current_rows
            ON CONFLICT (band_subject_id, song_id, ranking_scope, scope_combo_id) DO UPDATE
            SET entry_combo_id = EXCLUDED.entry_combo_id,
                entry_instrument_combo = EXCLUDED.entry_instrument_combo,
                score = EXCLUDED.score,
                rank = EXCLUDED.rank,
                stars = EXCLUDED.stars,
                is_full_combo = EXCLUDED.is_full_combo,
                difficulty = EXCLUDED.difficulty,
                percentile = EXCLUDED.percentile,
                season = EXCLUDED.season,
                total_entries = EXCLUDED.total_entries,
                first_seen_at = EXCLUDED.first_seen_at,
                last_updated_at = EXCLUDED.last_updated_at,
                observed_at = EXCLUDED.observed_at,
                updated_at = now()
            RETURNING 1
        )
        SELECT COUNT(*) FROM upserted;
        """;

    private static string BandRankEventsSql(bool registeredOnly, bool execute) => $"""
        WITH current_rows AS (
            SELECT r.*, s.band_subject_id, s.team_members AS subject_members
            FROM ({BandRankUnionSql}) r
            JOIN band_improvement_subjects s ON s.band_type = r.band_type AND s.team_key = r.team_key
            {RegisteredBandRankFilter(registeredOnly, "r")}
        ), event_rows AS (
            SELECT c.band_subject_id,
                   v.event_kind,
                   c.ranking_scope,
                   COALESCE(c.combo_id, '') AS combo_id,
                   v.metric,
                   v.old_numeric,
                   v.new_numeric,
                   v.old_rank,
                   v.new_rank,
                   jsonb_build_object(
                       'bandType', c.band_type,
                       'teamKey', c.team_key,
                       'teamMembers', c.subject_members,
                       'rankingScope', c.ranking_scope,
                       'comboId', COALESCE(c.combo_id, ''),
                       'oldWeightedRank', s.weighted_rank,
                       'newWeightedRank', c.weighted_rank,
                       'oldTotalScoreRank', s.total_score_rank,
                       'newTotalScoreRank', c.total_score_rank,
                       'oldFcRateRank', s.fc_rate_rank,
                       'newFcRateRank', c.fc_rate_rank,
                       'oldTotalScore', s.total_score,
                       'newTotalScore', c.total_score,
                       'oldFullComboCount', s.full_combo_count,
                       'newFullComboCount', c.full_combo_count
                   ) AS payload
            FROM current_rows c
            LEFT JOIN band_rank_improvement_state s
              ON s.band_subject_id = c.band_subject_id
             AND s.ranking_scope = c.ranking_scope
             AND s.combo_id = COALESCE(c.combo_id, '')
            CROSS JOIN LATERAL (VALUES
                ('band_weighted_rank_improved', 'weighted_rank', NULL::NUMERIC, NULL::NUMERIC, s.weighted_rank, c.weighted_rank, s.weighted_rank IS NOT NULL AND c.weighted_rank IS NOT NULL AND c.weighted_rank > 0 AND c.weighted_rank < s.weighted_rank),
                ('band_total_score_rank_improved', 'total_score_rank', NULL::NUMERIC, NULL::NUMERIC, s.total_score_rank, c.total_score_rank, s.total_score_rank IS NOT NULL AND c.total_score_rank IS NOT NULL AND c.total_score_rank > 0 AND c.total_score_rank < s.total_score_rank),
                ('band_fc_rate_rank_improved', 'fc_rate_rank', NULL::NUMERIC, NULL::NUMERIC, s.fc_rate_rank, c.fc_rate_rank, s.fc_rate_rank IS NOT NULL AND c.fc_rate_rank IS NOT NULL AND c.fc_rate_rank > 0 AND c.fc_rate_rank < s.fc_rate_rank),
                ('band_total_score_improved', 'total_score', s.total_score::NUMERIC, c.total_score::NUMERIC, NULL::INTEGER, NULL::INTEGER, s.total_score IS NOT NULL AND c.total_score IS NOT NULL AND c.total_score > s.total_score),
                ('band_fc_count_improved', 'full_combo_count', s.full_combo_count::NUMERIC, c.full_combo_count::NUMERIC, NULL::INTEGER, NULL::INTEGER, s.full_combo_count IS NOT NULL AND c.full_combo_count IS NOT NULL AND c.full_combo_count > s.full_combo_count)
            ) AS v(event_kind, metric, old_numeric, new_numeric, old_rank, new_rank, should_emit)
            WHERE v.should_emit
        )
        {EventSelectOrInsertSql("band_improvement_events", execute, "band_subject_id, event_kind, ranking_scope, combo_id, metric, old_numeric, new_numeric, old_rank, new_rank, payload", "band_subject_id, event_kind, ranking_scope, combo_id, metric, old_numeric, new_numeric, old_rank, new_rank, payload")}
        """;

    private static string BandRankStateUpsertSql(bool registeredOnly) => $"""
        WITH current_rows AS (
            SELECT r.*, s.band_subject_id
            FROM ({BandRankUnionSql}) r
            JOIN band_improvement_subjects s ON s.band_type = r.band_type AND s.team_key = r.team_key
            {RegisteredBandRankFilter(registeredOnly, "r")}
        ), upserted AS (
            INSERT INTO band_rank_improvement_state (
                band_subject_id, ranking_scope, combo_id, adjusted_skill_rank, weighted_rank,
                fc_rate_rank, total_score_rank, total_score, full_combo_count,
                computed_at, observed_at, updated_at)
            SELECT band_subject_id, ranking_scope, COALESCE(combo_id, ''), adjusted_skill_rank, weighted_rank,
                   fc_rate_rank, total_score_rank, total_score, full_combo_count,
                   computed_at, @detectedAt, now()
            FROM current_rows
            ON CONFLICT (band_subject_id, ranking_scope, combo_id) DO UPDATE
            SET adjusted_skill_rank = EXCLUDED.adjusted_skill_rank,
                weighted_rank = EXCLUDED.weighted_rank,
                fc_rate_rank = EXCLUDED.fc_rate_rank,
                total_score_rank = EXCLUDED.total_score_rank,
                total_score = EXCLUDED.total_score,
                full_combo_count = EXCLUDED.full_combo_count,
                computed_at = EXCLUDED.computed_at,
                observed_at = EXCLUDED.observed_at,
                updated_at = now()
            RETURNING 1
        )
        SELECT COUNT(*) FROM upserted;
        """;
}

public sealed record ImprovementNotificationDto(
    long EventId,
    long? RunId,
    string? AccountId,
    long? BandSubjectId,
    string? BandType,
    string? TeamKey,
    string EventKind,
    string? SongId,
    string? Instrument,
    string? RankingScope,
    string? ComboId,
    string? Metric,
    decimal? OldNumeric,
    decimal? NewNumeric,
    int? OldRank,
    int? NewRank,
    JsonElement Payload,
    DateTime DetectedAt,
    DateTime ExpiresAt);

public sealed record ImprovementNotificationsEnvelope(
    DateTime GeneratedAt,
    int ExpiresAfterHours,
    IReadOnlyList<ImprovementNotificationDto> Items);

public sealed record ImprovementNotificationPrecomputeOptions(
    bool Execute,
    bool BaselineOnly,
    string Scope,
    bool IncludePlayers,
    bool IncludeBands,
    bool IncludeSongEvents,
    bool IncludeRankings,
    bool PruneExpired,
    int CommandTimeoutSeconds = 0,
    DateTime? DetectedAtUtc = null,
    string Source = "precompute");

public sealed record ImprovementNotificationPrecomputeReport(
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    string Scope,
    string Mode,
    bool Execute,
    bool BaselineOnly,
    bool IncludePlayers,
    bool IncludeBands,
    bool IncludeSongEvents,
    bool IncludeRankings,
    bool PruneExpired,
    long? RunId,
    long PlayerSongRowsScanned,
    long PlayerSongEventsInserted,
    long PlayerSongStateUpserts,
    long PlayerRankRowsScanned,
    long PlayerRankEventsInserted,
    long PlayerRankStateUpserts,
    long BandSubjectsUpserted,
    long BandSongRowsScanned,
    long BandSongEventsInserted,
    long BandSongStateUpserts,
    long BandRankRowsScanned,
    long BandRankEventsInserted,
    long BandRankStateUpserts,
    long ExpiredPlayerEventsDeleted,
    long ExpiredBandEventsDeleted,
    string? ErrorMessage);