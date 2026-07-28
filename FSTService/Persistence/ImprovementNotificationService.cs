using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

public sealed class ImprovementNotificationService
{
    public const int DefaultLiveHours = 72;
    public const string ServiceNewShopSongKind = "service_new_shop_song";

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ImprovementNotificationService> _log;

    public ImprovementNotificationService(NpgsqlDataSource dataSource, ILogger<ImprovementNotificationService> log)
    {
        _dataSource = dataSource;
        _log = log;
    }

    public ImprovementNotificationPublicationStatus GetPublicationStatus()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH publication AS (
                SELECT published_scrape_id,
                       published_at,
                       public_reads_frozen,
                       improvement_notifications_scrape_id,
                       improvement_notifications_status,
                       improvement_notifications_attempt_count,
                       improvement_notifications_started_at,
                       improvement_notifications_completed_at,
                       improvement_notifications_error
                FROM scrape_publication_state
                WHERE id = TRUE
            ), player_run AS (
                SELECT published_scrape_id, run_id, completed_at
                FROM improvement_detection_runs
                WHERE status = 'completed'
                  AND include_players
                ORDER BY completed_at DESC, run_id DESC
                LIMIT 1
            ), band_run AS (
                SELECT published_scrape_id, run_id, completed_at
                FROM improvement_detection_runs
                WHERE status = 'completed'
                  AND include_bands
                ORDER BY completed_at DESC, run_id DESC
                LIMIT 1
            )
            SELECT publication.published_scrape_id,
                   publication.published_at,
                   publication.public_reads_frozen,
                   publication.improvement_notifications_scrape_id,
                   publication.improvement_notifications_status,
                   publication.improvement_notifications_attempt_count,
                   publication.improvement_notifications_started_at,
                   publication.improvement_notifications_completed_at,
                   publication.improvement_notifications_error,
                   player_run.published_scrape_id,
                   player_run.run_id,
                   player_run.completed_at,
                   band_run.published_scrape_id,
                   band_run.run_id,
                   band_run.completed_at
            FROM publication
            LEFT JOIN player_run ON TRUE
            LEFT JOIN band_run ON TRUE;
            """;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return ImprovementNotificationPublicationStatus.Empty;

        return new ImprovementNotificationPublicationStatus(
            PublishedScrapeId: reader.IsDBNull(0) ? null : reader.GetInt32(0),
            PublishedAtUtc: reader.IsDBNull(1) ? null : reader.GetDateTime(1),
            PublicReadsFrozen: !reader.IsDBNull(2) && reader.GetBoolean(2),
            MarkerScrapeId: reader.IsDBNull(3) ? null : reader.GetInt32(3),
            MarkerStatus: reader.IsDBNull(4) ? null : reader.GetString(4),
            AttemptCount: reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            StartedAtUtc: reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            CompletedAtUtc: reader.IsDBNull(7) ? null : reader.GetDateTime(7),
            ErrorMessage: reader.IsDBNull(8) ? null : reader.GetString(8),
            LatestPlayerScrapeId: reader.IsDBNull(9) ? null : reader.GetInt32(9),
            LatestPlayerRunId: reader.IsDBNull(10) ? null : reader.GetInt64(10),
            LatestPlayerCompletedAtUtc: reader.IsDBNull(11) ? null : reader.GetDateTime(11),
            LatestBandScrapeId: reader.IsDBNull(12) ? null : reader.GetInt32(12),
            LatestBandRunId: reader.IsDBNull(13) ? null : reader.GetInt64(13),
            LatestBandCompletedAtUtc: reader.IsDBNull(14) ? null : reader.GetDateTime(14));
    }

    public void EnsurePublicationPending(long scrapeId)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE scrape_publication_state
            SET improvement_notifications_scrape_id = @scrapeId,
                improvement_notifications_status = CASE
                    WHEN improvement_notifications_scrape_id = @scrapeId
                     AND improvement_notifications_status = 'completed'
                        THEN improvement_notifications_status
                    ELSE 'pending'
                END,
                improvement_notifications_error = CASE
                    WHEN improvement_notifications_scrape_id = @scrapeId
                     AND improvement_notifications_status = 'completed'
                        THEN improvement_notifications_error
                    ELSE NULL
                END,
                updated_at = now()
            WHERE id = TRUE
              AND published_scrape_id = @scrapeId;
            """;
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.ExecuteNonQuery();
    }

    public void MarkPublicationRunning(long scrapeId)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE scrape_publication_state
            SET improvement_notifications_scrape_id = @scrapeId,
                improvement_notifications_status = 'running',
                improvement_notifications_attempt_count = improvement_notifications_attempt_count + 1,
                improvement_notifications_started_at = now(),
                improvement_notifications_completed_at = NULL,
                improvement_notifications_error = NULL,
                updated_at = now()
            WHERE id = TRUE
              AND published_scrape_id = @scrapeId;
            """;
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.ExecuteNonQuery();
    }

    public void MarkPublicationCompleted(long scrapeId)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE scrape_publication_state
            SET improvement_notifications_scrape_id = @scrapeId,
                improvement_notifications_status = 'completed',
                improvement_notifications_completed_at = now(),
                improvement_notifications_error = NULL,
                updated_at = now()
            WHERE id = TRUE
              AND published_scrape_id = @scrapeId;
            """;
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.ExecuteNonQuery();
    }

    public void MarkPublicationDeferred(long scrapeId, string? detail = null)
        => MarkPublicationIncomplete(scrapeId, "pending", detail);

    public void MarkPublicationFailed(long scrapeId, string detail)
        => MarkPublicationIncomplete(scrapeId, "failed", detail);

    private void MarkPublicationIncomplete(long scrapeId, string status, string? detail)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE scrape_publication_state
            SET improvement_notifications_scrape_id = @scrapeId,
                improvement_notifications_status = @status,
                improvement_notifications_error = @detail,
                updated_at = now()
            WHERE id = TRUE
              AND published_scrape_id = @scrapeId;
            """;
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.Add("detail", NpgsqlDbType.Text).Value = NullableValue(detail);
        cmd.ExecuteNonQuery();
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
            WITH publication AS (
                SELECT
                    COALESCE((SELECT public_reads_frozen FROM scrape_publication_state WHERE id = TRUE), FALSE) AS public_reads_frozen,
                    (SELECT public_reads_frozen_at FROM scrape_publication_state WHERE id = TRUE) AS public_reads_frozen_at
            ), combined AS (
            SELECT event_id,
                   notification_guid,
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
            CROSS JOIN publication
            WHERE account_id = @accountId
              AND (@includeExpired OR expires_at > now())
              AND (
                  NOT publication.public_reads_frozen
                  OR publication.public_reads_frozen_at IS NULL
                  OR detected_at < publication.public_reads_frozen_at
              )
              AND (
                  @kind IS NULL
                  OR event_kind = @kind
                  OR EXISTS (
                      SELECT 1
                      FROM jsonb_array_elements(COALESCE(payload->'coalescedEvents', '[]'::jsonb)) child
                      WHERE child->>'eventKind' = @kind
                  )
              )
              AND (
                  @instrument IS NULL
                  OR instrument = @instrument
                  OR COALESCE(payload->'coalescedInstruments', '[]'::jsonb) ? @instrument
                  OR EXISTS (
                      SELECT 1
                      FROM jsonb_array_elements(COALESCE(payload->'coalescedEvents', '[]'::jsonb)) child
                      WHERE child->>'instrument' = @instrument
                  )
              )
              AND (@songId IS NULL OR song_id = @songId)
                        UNION ALL
                        SELECT event_id,
                                     notification_guid,
                                     NULL::BIGINT AS run_id,
                                     NULL::TEXT AS account_id,
                                     NULL::BIGINT AS band_subject_id,
                                     NULL::TEXT AS band_type,
                                     NULL::TEXT AS team_key,
                                     notification_kind AS event_kind,
                                     song_id,
                                     NULL::TEXT AS instrument,
                                     NULL::TEXT AS ranking_scope,
                                     NULL::TEXT AS combo_id,
                                     NULL::TEXT AS metric,
                                     NULL::NUMERIC AS old_numeric,
                                     NULL::NUMERIC AS new_numeric,
                                     NULL::INTEGER AS old_rank,
                                     NULL::INTEGER AS new_rank,
                                     (payload || jsonb_build_object(
                                             'songTitle', title,
                                             'artist', artist,
                                             'albumArt', album_art))::TEXT AS payload,
                                     detected_at,
                                     expires_at
                        FROM service_notifications
                        WHERE (@includeExpired OR expires_at > now())
                            AND (@kind IS NULL OR notification_kind = @kind)
                            AND (@instrument IS NULL)
                            AND (@songId IS NULL OR song_id = @songId)
                        )
                        SELECT * FROM combined
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
        var source = ReadLatestNotificationSource(conn, includePlayers: true, includeBands: false);
        return new ImprovementNotificationsEnvelope(DateTime.UtcNow, DefaultLiveHours, source.RunId, source.CompletedAt, items);
    }

    public long UpsertNewShopSongNotifications(
        IReadOnlyCollection<NewShopSongServiceNotification> notifications,
        DateTime detectedAtUtc)
    {
        if (notifications.Count == 0) return 0;

        var expiresAt = detectedAtUtc.AddHours(DefaultLiveHours);
        long inserted = 0;
        using var conn = _dataSource.OpenConnection();
        using var tx = conn.BeginTransaction();

        foreach (var notification in notifications)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO service_notifications (
                    notification_kind, song_id, title, artist, album_art, payload,
                    detected_at, expires_at, source, source_key)
                VALUES (
                    @kind, @songId, @title, @artist, @albumArt, @payload,
                    @detectedAt, @expiresAt, 'item_shop', @sourceKey)
                ON CONFLICT (notification_kind, song_id, source_key) DO NOTHING
                RETURNING 1;
                """;
            cmd.Parameters.AddWithValue("kind", ServiceNewShopSongKind);
            cmd.Parameters.AddWithValue("songId", notification.SongId);
            cmd.Parameters.AddWithValue("title", notification.Title);
            cmd.Parameters.AddWithValue("artist", notification.Artist);
            cmd.Parameters.Add("albumArt", NpgsqlDbType.Text).Value = NullableValue(notification.AlbumArt);
            cmd.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = BuildNewShopSongPayload(notification);
            cmd.Parameters.AddWithValue("detectedAt", detectedAtUtc);
            cmd.Parameters.AddWithValue("expiresAt", expiresAt);
            cmd.Parameters.AddWithValue("sourceKey", notification.SourceKey);
            if (cmd.ExecuteScalar() is not null) inserted++;
        }

        tx.Commit();
        return inserted;
    }

    public long CleanupExpiredServiceNotifications(DateTime detectedAtUtc)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM service_notifications WHERE expires_at <= @detectedAt;";
        cmd.Parameters.AddWithValue("detectedAt", detectedAtUtc);
        return cmd.ExecuteNonQuery();
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
        var previousPlayerSongCompletedAt = options.IncludePlayers && options.IncludeSongEvents
            ? ReadLatestCompletedRunAt(conn, includePlayers: true, includeSongEvents: true)
            : null;
        var previousPlayerRankCompletedAt = options.IncludePlayers && options.IncludeRankings
            ? ReadLatestCompletedRunAt(conn, includePlayers: true, includeSongEvents: false)
            : null;
        var previousBandSongCompletedAt = options.IncludeBands && options.IncludeSongEvents
            ? ReadLatestCompletedRunAt(conn, includePlayers: false, includeSongEvents: true)
            : null;
        var previousBandRankCompletedAt = options.IncludeBands && options.IncludeRankings
            ? ReadLatestCompletedRunAt(conn, includePlayers: false, includeSongEvents: false)
            : null;
        NpgsqlTransaction? tx = null;

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
            PlayerSongBaselineRows: 0,
            PlayerRankBaselineRows: 0,
            BandSongBaselineRows: 0,
            BandRankBaselineRows: 0,
            ErrorMessage: null);

        try
        {
            if (execute)
            {
                runId = InsertRun(conn, null, options, mode, registeredOnly, source);
                report = report with { RunId = runId };
                tx = conn.BeginTransaction();
            }

            if (options.PruneExpired)
            {
                var expiredPlayer = options.IncludePlayers
                    ? PruneExpiredEvents(conn, tx, "player_improvement_events", execute, detectedAt)
                    : 0;
                var expiredBand = options.IncludeBands
                    ? PruneExpiredEvents(conn, tx, "band_improvement_events", execute, detectedAt)
                    : 0;
                report = report with
                {
                    ExpiredPlayerEventsDeleted = expiredPlayer,
                    ExpiredBandEventsDeleted = expiredBand,
                };
            }

            if (options.IncludeBands && registeredOnly)
                PrepareRegisteredBandWorkingSet(conn, tx, options.CommandTimeoutSeconds);

            if (options.IncludePlayers && options.IncludeSongEvents)
            {
                var rows = ExecuteScalarLong(conn, tx, CountPlayerSongRowsSql(registeredOnly), options.CommandTimeoutSeconds);
                var baselineRows = execute && !options.BaselineOnly && registeredOnly
                    ? ExecuteScalarLong(
                        conn,
                        tx,
                        BaselineNewPlayerSongSubjectsSql(),
                        options.CommandTimeoutSeconds,
                        detectedAt: detectedAt,
                        previousCompletedAt: previousPlayerSongCompletedAt)
                    : 0;
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
                    PlayerSongBaselineRows = baselineRows,
                };
            }

            if (options.IncludePlayers && options.IncludeRankings)
            {
                var rows = ExecuteScalarLong(conn, tx, CountPlayerRankRowsSql(registeredOnly), options.CommandTimeoutSeconds);
                var baselineRows = execute && !options.BaselineOnly && registeredOnly
                    ? ExecuteScalarLong(
                        conn,
                        tx,
                        BaselineNewPlayerRankSubjectsSql(),
                        options.CommandTimeoutSeconds,
                        detectedAt: detectedAt,
                        previousCompletedAt: previousPlayerRankCompletedAt)
                    : 0;
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
                    PlayerRankBaselineRows = baselineRows,
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
                var baselineRows = execute && !options.BaselineOnly && registeredOnly
                    ? ExecuteScalarLong(
                        conn,
                        tx,
                        BaselineNewBandSongSubjectsSql(),
                        options.CommandTimeoutSeconds,
                        detectedAt: detectedAt,
                        previousCompletedAt: previousBandSongCompletedAt)
                    : 0;
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
                    BandSongBaselineRows = baselineRows,
                };
            }

            if (options.IncludeBands && options.IncludeRankings)
            {
                var rows = ExecuteScalarLong(conn, tx, CountBandRankRowsSql(registeredOnly), options.CommandTimeoutSeconds);
                var baselineRows = execute && !options.BaselineOnly && registeredOnly
                    ? ExecuteScalarLong(
                        conn,
                        tx,
                        BaselineNewBandRankSubjectsSql(),
                        options.CommandTimeoutSeconds,
                        detectedAt: detectedAt,
                        previousCompletedAt: previousBandRankCompletedAt)
                    : 0;
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
                    BandRankBaselineRows = baselineRows,
                };
            }

            var completedAt = DateTime.UtcNow;
            report = report with { CompletedAtUtc = completedAt };

            if (execute && runId.HasValue)
            {
                UpdateRunSuccess(conn, tx!, runId.Value, report, completedAt);
                tx!.Commit();
                tx.Dispose();
                tx = null;
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

            throw;
        }
        finally
        {
            tx?.Dispose();
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
            WITH publication AS (
                SELECT
                    COALESCE((SELECT public_reads_frozen FROM scrape_publication_state WHERE id = TRUE), FALSE) AS public_reads_frozen,
                    (SELECT public_reads_frozen_at FROM scrape_publication_state WHERE id = TRUE) AS public_reads_frozen_at
            ), combined AS (
            SELECT e.event_id,
                   e.notification_guid,
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
            CROSS JOIN publication
            WHERE (@bandSubjectId IS NULL OR s.band_subject_id = @bandSubjectId)
              AND (@bandType IS NULL OR s.band_type = @bandType)
              AND (@teamKey IS NULL OR s.team_key = @teamKey)
              AND (@includeExpired OR e.expires_at > now())
              AND (
                  NOT publication.public_reads_frozen
                  OR publication.public_reads_frozen_at IS NULL
                  OR e.detected_at < publication.public_reads_frozen_at
              )
              AND (
                  @kind IS NULL
                  OR e.event_kind = @kind
                  OR EXISTS (
                      SELECT 1
                      FROM jsonb_array_elements(COALESCE(e.payload->'coalescedEvents', '[]'::jsonb)) child
                      WHERE child->>'eventKind' = @kind
                  )
              )
              AND (
                  @rankingScope = 'all'
                  OR e.ranking_scope = @rankingScope
                  OR EXISTS (
                      SELECT 1
                      FROM jsonb_array_elements(COALESCE(e.payload->'coalescedEvents', '[]'::jsonb)) child
                      WHERE child->>'rankingScope' = @rankingScope
                        AND (
                            @rankingScope <> 'combo'
                            OR @comboId IS NULL
                            OR COALESCE(child->>'scopeComboId', child->>'comboId', '') = @comboId
                        )
                  )
              )
              AND (
                  @comboId IS NULL
                  OR e.combo_id = @comboId
                  OR EXISTS (
                      SELECT 1
                      FROM jsonb_array_elements(COALESCE(e.payload->'coalescedEvents', '[]'::jsonb)) child
                      WHERE COALESCE(child->>'scopeComboId', child->>'comboId', '') = @comboId
                  )
              )
                        UNION ALL
                        SELECT event_id,
                                     notification_guid,
                                     NULL::BIGINT AS run_id,
                                     NULL::TEXT AS account_id,
                                     NULL::BIGINT AS band_subject_id,
                                     NULL::TEXT AS band_type,
                                     NULL::TEXT AS team_key,
                                     notification_kind AS event_kind,
                                     song_id,
                                     NULL::TEXT AS instrument,
                                     NULL::TEXT AS ranking_scope,
                                     NULL::TEXT AS combo_id,
                                     NULL::TEXT AS metric,
                                     NULL::NUMERIC AS old_numeric,
                                     NULL::NUMERIC AS new_numeric,
                                     NULL::INTEGER AS old_rank,
                                     NULL::INTEGER AS new_rank,
                                     (payload || jsonb_build_object(
                                             'songTitle', title,
                                             'artist', artist,
                                             'albumArt', album_art))::TEXT AS payload,
                                     detected_at,
                                     expires_at
                        FROM service_notifications
                        WHERE (@includeExpired OR expires_at > now())
                            AND (@kind IS NULL OR notification_kind = @kind)
                            AND (@rankingScope = 'overall' OR @rankingScope = 'all')
                            AND (@comboId IS NULL)
                        )
                        SELECT * FROM combined
                        ORDER BY detected_at DESC, event_id DESC
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
        var source = ReadLatestNotificationSource(conn, includePlayers: false, includeBands: true);
        return new ImprovementNotificationsEnvelope(DateTime.UtcNow, DefaultLiveHours, source.RunId, source.CompletedAt, items);
    }

    private static string BuildNewShopSongPayload(NewShopSongServiceNotification notification)
    {
        return JsonSerializer.Serialize(new
        {
            songTitle = notification.Title,
            artist = notification.Artist,
            albumArt = notification.AlbumArt,
            sourceKey = notification.SourceKey,
            shopInDate = notification.ShopInDateUtc?.ToString("O"),
        });
    }

    private static ImprovementNotificationSourceCursor ReadLatestNotificationSource(
        NpgsqlConnection conn,
        bool includePlayers,
        bool includeBands)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, completed_at
            FROM improvement_detection_runs
            WHERE status = 'completed'
              AND (@includePlayers = false OR include_players)
              AND (@includeBands = false OR include_bands)
            ORDER BY run_id DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("includePlayers", includePlayers);
        cmd.Parameters.AddWithValue("includeBands", includeBands);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new ImprovementNotificationSourceCursor(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetDateTime(1))
            : new ImprovementNotificationSourceCursor(null, null);
    }

    private static IReadOnlyList<ImprovementNotificationDto> ReadNotifications(NpgsqlCommand cmd)
    {
        var items = new List<ImprovementNotificationDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var payloadJson = reader.GetString(17);
            var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson);
            items.Add(new ImprovementNotificationDto(
                EventId: reader.GetInt64(0),
                NotificationGuid: reader.GetGuid(1),
                RunId: reader.IsDBNull(2) ? null : reader.GetInt64(2),
                AccountId: reader.IsDBNull(3) ? null : reader.GetString(3),
                BandSubjectId: reader.IsDBNull(4) ? null : reader.GetInt64(4),
                BandType: reader.IsDBNull(5) ? null : reader.GetString(5),
                TeamKey: reader.IsDBNull(6) ? null : reader.GetString(6),
                EventKind: reader.GetString(7),
                SongId: reader.IsDBNull(8) ? null : reader.GetString(8),
                Instrument: reader.IsDBNull(9) ? null : reader.GetString(9),
                RankingScope: reader.IsDBNull(10) ? null : reader.GetString(10),
                ComboId: reader.IsDBNull(11) ? null : reader.GetString(11),
                Metric: reader.IsDBNull(12) ? null : reader.GetString(12),
                OldNumeric: reader.IsDBNull(13) ? null : reader.GetDecimal(13),
                NewNumeric: reader.IsDBNull(14) ? null : reader.GetDecimal(14),
                OldRank: reader.IsDBNull(15) ? null : reader.GetInt32(15),
                NewRank: reader.IsDBNull(16) ? null : reader.GetInt32(16),
                Payload: payload,
                DetectedAt: reader.GetDateTime(18),
                ExpiresAt: reader.GetDateTime(19)));
        }

        return items;
    }

    private static DateTime? ReadLatestCompletedRunAt(
        NpgsqlConnection conn,
        bool includePlayers,
        bool includeSongEvents)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT completed_at
            FROM improvement_detection_runs
            WHERE status = 'completed'
              AND (
                  (@includePlayers AND include_players)
                  OR (NOT @includePlayers AND include_bands)
              )
              AND (
                  (@includeSongEvents AND include_song_events)
                  OR (NOT @includeSongEvents AND include_rankings)
              )
            ORDER BY completed_at DESC, run_id DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("includePlayers", includePlayers);
        cmd.Parameters.AddWithValue("includeSongEvents", includeSongEvents);
        return cmd.ExecuteScalar() is DateTime completedAt ? completedAt : null;
    }

    private static long InsertRun(NpgsqlConnection conn, NpgsqlTransaction? tx, ImprovementNotificationPrecomputeOptions options, string mode, bool registeredOnly, string source)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO improvement_detection_runs (
                published_scrape_id, scope, mode, source, baseline_only, include_players, include_bands,
                include_song_events, include_rankings, prune_expired)
            VALUES (@publishedScrapeId, @scope, @mode, @source, @baselineOnly, @includePlayers, @includeBands,
                    @includeSongEvents, @includeRankings, @pruneExpired)
            RETURNING run_id;
            """;
        cmd.Parameters.Add("publishedScrapeId", NpgsqlDbType.Integer).Value =
            options.PublishedScrapeId.HasValue ? checked((int)options.PublishedScrapeId.Value) : DBNull.Value;
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
                expired_band_events_deleted = @expiredBandEventsDeleted,
                player_song_baseline_rows = @playerSongBaselineRows,
                player_rank_baseline_rows = @playerRankBaselineRows,
                band_song_baseline_rows = @bandSongBaselineRows,
                band_rank_baseline_rows = @bandRankBaselineRows
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
        cmd.Parameters.AddWithValue("playerSongBaselineRows", report.PlayerSongBaselineRows);
        cmd.Parameters.AddWithValue("playerRankBaselineRows", report.PlayerRankBaselineRows);
        cmd.Parameters.AddWithValue("bandSongBaselineRows", report.BandSongBaselineRows);
        cmd.Parameters.AddWithValue("bandRankBaselineRows", report.BandRankBaselineRows);
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
        string? source = null,
        DateTime? previousCompletedAt = null)
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
        if (sql.Contains("@previousCompletedAt", StringComparison.Ordinal))
            cmd.Parameters.Add("previousCompletedAt", NpgsqlDbType.TimestampTz).Value =
                previousCompletedAt.HasValue ? previousCompletedAt.Value : DBNull.Value;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private void PrepareRegisteredBandWorkingSet(NpgsqlConnection conn, NpgsqlTransaction? tx, int commandTimeoutSeconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = commandTimeoutSeconds <= 0 ? 0 : commandTimeoutSeconds;
        cmd.CommandText = $$"""
            CREATE TEMP TABLE _registered_bands ON COMMIT DROP AS
            SELECT DISTINCT band_type, team_key
            FROM registered_bands;

            CREATE INDEX ON _registered_bands (band_type, team_key);
            ANALYZE _registered_bands;

            CREATE TEMP TABLE _registered_band_current ON COMMIT DROP AS
            SELECT c.*
            FROM _registered_bands rb
            JOIN current_band_leaderboard_entries c
              ON c.band_type = rb.band_type
             AND c.team_key = rb.team_key;

            CREATE INDEX ON _registered_band_current
                (song_id, band_type, ranking_scope, scope_combo_id, projection_generation);
            CREATE INDEX ON _registered_band_current
                (band_type, team_key);
            ANALYZE _registered_band_current;

            CREATE TEMP TABLE _registered_band_rank ON COMMIT DROP AS
            SELECT r.*
            FROM ({{BandRankUnionSql(registeredOnly: true, usePreparedRegisteredBands: true)}}) r;

            CREATE INDEX ON _registered_band_rank (band_type, team_key);
            ANALYZE _registered_band_rank;
            """;
        cmd.ExecuteNonQuery();
        sw.Stop();
        _log.LogInformation("Prepared registered band notification working set in {ElapsedMs:N0}ms.", sw.ElapsedMilliseconds);
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

    private static string PublishedBandCurrentJoin(string alias = "c") => $"""
        JOIN band_current_projection_scope {alias}_published_scope
          ON {alias}_published_scope.song_id = {alias}.song_id
         AND {alias}_published_scope.band_type = {alias}.band_type
         AND {alias}_published_scope.ranking_scope = {alias}.ranking_scope
         AND {alias}_published_scope.scope_combo_id = {alias}.scope_combo_id
         AND {alias}_published_scope.published_generation = {alias}.projection_generation
        """;

    private static string BandCurrentRowsFromSql(bool registeredOnly) => registeredOnly
        ? $"""
            FROM _registered_band_current c
            {PublishedBandCurrentJoin()}
            """
        : $"""
            FROM current_band_leaderboard_entries c
            {PublishedBandCurrentJoin()}
            """;

    private static string BandRankUnionSql(bool registeredOnly, bool usePreparedRegisteredBands = false)
    {
        if (usePreparedRegisteredBands)
            return BandRankUnionFromRegisteredBandsSql("_registered_bands");

        if (registeredOnly)
            return "SELECT * FROM _registered_band_rank";

        return BandRankUnionAllSql(
            "FROM band_team_rankings_current_band_duets r",
            "FROM band_team_rankings_current_band_trios r",
            "FROM band_team_rankings_current_band_quad r");
    }

    private static string BandRankUnionFromRegisteredBandsSql(string registeredBandsTable)
        => BandRankUnionAllSql(
            $"FROM {registeredBandsTable} rb JOIN band_team_rankings_current_band_duets r ON r.band_type = rb.band_type AND r.team_key = rb.team_key",
            $"FROM {registeredBandsTable} rb JOIN band_team_rankings_current_band_trios r ON r.band_type = rb.band_type AND r.team_key = rb.team_key",
            $"FROM {registeredBandsTable} rb JOIN band_team_rankings_current_band_quad r ON r.band_type = rb.band_type AND r.team_key = rb.team_key");

    private static string BandRankUnionAllSql(string duetsFrom, string triosFrom, string quadFrom)
    {
        return $$"""
        SELECT r.band_type, r.ranking_scope, r.combo_id, r.team_key, r.team_members,
               r.adjusted_skill_rank, r.weighted_rank, r.fc_rate_rank, r.total_score_rank,
               r.total_score, r.full_combo_count, r.computed_at
        {{duetsFrom}}
        UNION ALL
        SELECT r.band_type, r.ranking_scope, r.combo_id, r.team_key, r.team_members,
               r.adjusted_skill_rank, r.weighted_rank, r.fc_rate_rank, r.total_score_rank,
               r.total_score, r.full_combo_count, r.computed_at
        {{triosFrom}}
        UNION ALL
        SELECT r.band_type, r.ranking_scope, r.combo_id, r.team_key, r.team_members,
               r.adjusted_skill_rank, r.weighted_rank, r.fc_rate_rank, r.total_score_rank,
               r.total_score, r.full_combo_count, r.computed_at
        {{quadFrom}}
        """;
    }

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
        {BandCurrentRowsFromSql(registeredOnly)};
        """;

    private static string CountBandRankRowsSql(bool registeredOnly) => $"""
        WITH current_rows AS (
            SELECT * FROM ({BandRankUnionSql(registeredOnly)}) r
        )
        SELECT COUNT(*) FROM current_rows;
        """;

    private static string CountBandSubjectRowsSql(bool registeredOnly) => $"""
        WITH subject_rows AS (
            SELECT DISTINCT c.band_type, c.team_key
            {BandCurrentRowsFromSql(registeredOnly)}
            UNION
            SELECT DISTINCT r.band_type, r.team_key
            FROM ({BandRankUnionSql(registeredOnly)}) r
        )
        SELECT COUNT(*) FROM subject_rows;
        """;

    private static string BaselineNewPlayerSongSubjectsSql() => """
        WITH new_subjects AS (
            SELECT ru.account_id
            FROM (
                SELECT account_id, MIN(registered_at) AS registered_at
                FROM registered_users
                GROUP BY account_id
            ) ru
            WHERE (@previousCompletedAt IS NULL OR ru.registered_at > @previousCompletedAt)
              AND NOT EXISTS (
                  SELECT 1
                  FROM player_improvement_state existing
                  WHERE existing.account_id = ru.account_id
              )
        ), inserted AS (
            INSERT INTO player_improvement_state (
                account_id, song_id, instrument, score, rank, stars, is_full_combo,
                difficulty, percentile, season, first_seen_at, last_updated_at, observed_at, updated_at)
            SELECT c.account_id, c.song_id, c.instrument, c.score, c.rank, c.stars, c.is_full_combo,
                   c.difficulty, c.percentile, c.season, c.first_seen_at, c.last_updated_at, @detectedAt, now()
            FROM current_leaderboard_entries c
            JOIN new_subjects subject ON subject.account_id = c.account_id
            ON CONFLICT (account_id, song_id, instrument) DO NOTHING
            RETURNING 1
        )
        SELECT COUNT(*) FROM inserted;
        """;

    private static string BaselineNewPlayerRankSubjectsSql() => """
        WITH new_subjects AS (
            SELECT ru.account_id
            FROM (
                SELECT account_id, MIN(registered_at) AS registered_at
                FROM registered_users
                GROUP BY account_id
            ) ru
            WHERE (@previousCompletedAt IS NULL OR ru.registered_at > @previousCompletedAt)
              AND NOT EXISTS (
                  SELECT 1
                  FROM player_rank_improvement_state existing
                  WHERE existing.account_id = ru.account_id
              )
        ), inserted AS (
            INSERT INTO player_rank_improvement_state (
                account_id, instrument, adjusted_skill_rank, weighted_rank, fc_rate_rank,
                total_score_rank, max_score_percent_rank, total_score, full_combo_count,
                computed_at, observed_at, updated_at)
            SELECT c.account_id, c.instrument, c.adjusted_skill_rank, c.weighted_rank, c.fc_rate_rank,
                   c.total_score_rank, c.max_score_percent_rank, c.total_score, c.full_combo_count,
                   c.computed_at, @detectedAt, now()
            FROM account_rankings c
            JOIN new_subjects subject ON subject.account_id = c.account_id
            ON CONFLICT (account_id, instrument) DO NOTHING
            RETURNING 1
        )
        SELECT COUNT(*) FROM inserted;
        """;

    private static string BaselineNewBandSongSubjectsSql() => $"""
        WITH new_subjects AS (
            SELECT subject.band_subject_id
            FROM registered_bands rb
            JOIN band_improvement_subjects subject
              ON subject.band_type = rb.band_type
             AND subject.team_key = rb.team_key
            GROUP BY subject.band_subject_id
            HAVING (@previousCompletedAt IS NULL OR MIN(rb.registered_at) > @previousCompletedAt)
               AND NOT EXISTS (
                   SELECT 1
                   FROM band_improvement_state existing
                   WHERE existing.band_subject_id = subject.band_subject_id
               )
        ), current_rows AS (
            SELECT c.*, subject.band_subject_id
            {BandCurrentRowsFromSql(registeredOnly: true)}
            JOIN band_improvement_subjects subject
              ON subject.band_type = c.band_type
             AND subject.team_key = c.team_key
            JOIN new_subjects new_subject
              ON new_subject.band_subject_id = subject.band_subject_id
        ), inserted AS (
            INSERT INTO band_improvement_state (
                band_subject_id, song_id, ranking_scope, scope_combo_id, entry_combo_id,
                entry_instrument_combo, score, rank, stars, is_full_combo, difficulty,
                percentile, season, total_entries, first_seen_at, last_updated_at, observed_at, updated_at)
            SELECT band_subject_id, song_id, ranking_scope, COALESCE(scope_combo_id, ''), entry_combo_id,
                   entry_instrument_combo, score, rank, stars, is_full_combo, difficulty,
                   percentile, season, total_entries, first_seen_at, last_updated_at, @detectedAt, now()
            FROM current_rows
            ON CONFLICT (band_subject_id, song_id, ranking_scope, scope_combo_id) DO NOTHING
            RETURNING 1
        )
        SELECT COUNT(*) FROM inserted;
        """;

    private static string BaselineNewBandRankSubjectsSql() => $"""
        WITH new_subjects AS (
            SELECT subject.band_subject_id
            FROM registered_bands rb
            JOIN band_improvement_subjects subject
              ON subject.band_type = rb.band_type
             AND subject.team_key = rb.team_key
            GROUP BY subject.band_subject_id
            HAVING (@previousCompletedAt IS NULL OR MIN(rb.registered_at) > @previousCompletedAt)
               AND NOT EXISTS (
                   SELECT 1
                   FROM band_rank_improvement_state existing
                   WHERE existing.band_subject_id = subject.band_subject_id
               )
        ), current_rows AS (
            SELECT r.*, subject.band_subject_id
            FROM ({BandRankUnionSql(registeredOnly: true)}) r
            JOIN band_improvement_subjects subject
              ON subject.band_type = r.band_type
             AND subject.team_key = r.team_key
            JOIN new_subjects new_subject
              ON new_subject.band_subject_id = subject.band_subject_id
        ), inserted AS (
            INSERT INTO band_rank_improvement_state (
                band_subject_id, ranking_scope, combo_id, adjusted_skill_rank, weighted_rank,
                fc_rate_rank, total_score_rank, total_score, full_combo_count,
                computed_at, observed_at, updated_at)
            SELECT band_subject_id, ranking_scope, COALESCE(combo_id, ''), adjusted_skill_rank, weighted_rank,
                   fc_rate_rank, total_score_rank, total_score, full_combo_count,
                   computed_at, @detectedAt, now()
            FROM current_rows
            ON CONFLICT (band_subject_id, ranking_scope, combo_id) DO NOTHING
            RETURNING 1
        )
        SELECT COUNT(*) FROM inserted;
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
                ('player_song_rank_improved', 'song_rank', NULL::NUMERIC, NULL::NUMERIC, s.rank, c.rank, s.score IS NOT NULL AND c.score IS NOT NULL AND c.score > s.score AND s.rank IS NOT NULL AND c.rank IS NOT NULL AND c.rank > 0 AND c.rank < s.rank),
                ('player_stars_improved', 'stars', s.stars::NUMERIC, c.stars::NUMERIC, NULL::INTEGER, NULL::INTEGER, s.account_id IS NOT NULL AND c.stars IS NOT NULL AND s.stars IS NOT NULL AND c.stars > s.stars),
                ('player_gold_stars_achieved', 'stars', s.stars::NUMERIC, c.stars::NUMERIC, NULL::INTEGER, NULL::INTEGER, c.stars >= 6 AND (s.account_id IS NULL OR COALESCE(s.stars, 0) < 6)),
                ('player_fc_achieved', 'full_combo', NULL::NUMERIC, NULL::NUMERIC, NULL::INTEGER, NULL::INTEGER, c.is_full_combo IS TRUE AND (s.account_id IS NULL OR COALESCE(s.is_full_combo, false) = false)),
                ('player_difficulty_bumped', 'difficulty', s.difficulty::NUMERIC, c.difficulty::NUMERIC, NULL::INTEGER, NULL::INTEGER, s.account_id IS NOT NULL AND c.difficulty IS NOT NULL AND s.difficulty IS NOT NULL AND c.difficulty > s.difficulty)
            ) AS v(event_kind, metric, old_numeric, new_numeric, old_rank, new_rank, should_emit)
            WHERE v.should_emit
        )
        {PlayerSongEventSelectOrInsertSql(execute)}
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
        {PlayerRankEventSelectOrInsertSql(execute)}
        """;

    private static string PlayerSongEventSelectOrInsertSql(bool execute)
    {
        const string coalesced = """
            , grouped_event_rows AS (
                SELECT account_id,
                       song_id,
                       COUNT(*) AS event_count,
                       array_agg(event_kind ORDER BY CASE instrument
                           WHEN 'Solo_Guitar' THEN 10
                           WHEN 'Solo_Bass' THEN 20
                           WHEN 'Solo_Drums' THEN 30
                           WHEN 'Solo_Vocals' THEN 40
                           WHEN 'Solo_PeripheralGuitar' THEN 50
                           WHEN 'Solo_PeripheralBass' THEN 60
                           WHEN 'Solo_PeripheralVocals' THEN 70
                           WHEN 'Solo_PeripheralCymbals' THEN 80
                           WHEN 'Solo_PeripheralDrums' THEN 90
                           ELSE 100 END, CASE event_kind
                           WHEN 'player_first_score' THEN 10
                           WHEN 'player_score_pb' THEN 20
                           WHEN 'player_fc_achieved' THEN 30
                           WHEN 'player_gold_stars_achieved' THEN 40
                           WHEN 'player_stars_improved' THEN 50
                           WHEN 'player_song_rank_improved' THEN 60
                           WHEN 'player_difficulty_bumped' THEN 70
                           ELSE 100 END, event_kind) AS event_kinds,
                       array_agg(DISTINCT instrument ORDER BY instrument) AS coalesced_instruments_raw,
                       jsonb_agg(jsonb_build_object(
                           'eventKind', event_kind,
                           'instrument', instrument,
                           'metric', metric,
                           'oldNumeric', old_numeric,
                           'newNumeric', new_numeric,
                           'oldRank', old_rank,
                           'newRank', new_rank)
                           ORDER BY CASE instrument
                               WHEN 'Solo_Guitar' THEN 10
                               WHEN 'Solo_Bass' THEN 20
                               WHEN 'Solo_Drums' THEN 30
                               WHEN 'Solo_Vocals' THEN 40
                               WHEN 'Solo_PeripheralGuitar' THEN 50
                               WHEN 'Solo_PeripheralBass' THEN 60
                               WHEN 'Solo_PeripheralVocals' THEN 70
                               WHEN 'Solo_PeripheralCymbals' THEN 80
                               WHEN 'Solo_PeripheralDrums' THEN 90
                               ELSE 100 END, CASE event_kind
                               WHEN 'player_first_score' THEN 10
                               WHEN 'player_score_pb' THEN 20
                               WHEN 'player_fc_achieved' THEN 30
                               WHEN 'player_gold_stars_achieved' THEN 40
                               WHEN 'player_stars_improved' THEN 50
                               WHEN 'player_song_rank_improved' THEN 60
                               WHEN 'player_difficulty_bumped' THEN 70
                               ELSE 100 END, event_kind) AS coalesced_events
                FROM event_rows
                GROUP BY account_id, song_id
            ), ordered_grouped_event_rows AS (
                SELECT account_id,
                       song_id,
                       event_count,
                       event_kinds,
                       ARRAY(
                           SELECT instrument
                           FROM unnest(coalesced_instruments_raw) instrument
                           ORDER BY CASE instrument
                               WHEN 'Solo_Guitar' THEN 10
                               WHEN 'Solo_Bass' THEN 20
                               WHEN 'Solo_Drums' THEN 30
                               WHEN 'Solo_Vocals' THEN 40
                               WHEN 'Solo_PeripheralGuitar' THEN 50
                               WHEN 'Solo_PeripheralBass' THEN 60
                               WHEN 'Solo_PeripheralVocals' THEN 70
                               WHEN 'Solo_PeripheralCymbals' THEN 80
                               WHEN 'Solo_PeripheralDrums' THEN 90
                               ELSE 100 END, instrument
                       ) AS coalesced_instruments,
                       coalesced_events
                FROM grouped_event_rows
            ), primary_event_rows AS (
                SELECT DISTINCT ON (account_id, song_id) *
                FROM event_rows
                ORDER BY account_id, song_id, CASE instrument
                    WHEN 'Solo_Guitar' THEN 10
                    WHEN 'Solo_Bass' THEN 20
                    WHEN 'Solo_Drums' THEN 30
                    WHEN 'Solo_Vocals' THEN 40
                    WHEN 'Solo_PeripheralGuitar' THEN 50
                    WHEN 'Solo_PeripheralBass' THEN 60
                    WHEN 'Solo_PeripheralVocals' THEN 70
                    WHEN 'Solo_PeripheralCymbals' THEN 80
                    WHEN 'Solo_PeripheralDrums' THEN 90
                    ELSE 100 END, CASE event_kind
                    WHEN 'player_first_score' THEN 10
                    WHEN 'player_score_pb' THEN 20
                    WHEN 'player_fc_achieved' THEN 30
                    WHEN 'player_gold_stars_achieved' THEN 40
                    WHEN 'player_stars_improved' THEN 50
                    WHEN 'player_song_rank_improved' THEN 60
                    WHEN 'player_difficulty_bumped' THEN 70
                    ELSE 100 END, event_kind
            ), coalesced_event_rows AS (
                SELECT p.account_id,
                       p.event_kind,
                       p.song_id,
                       p.instrument,
                       p.metric,
                       p.old_numeric,
                       p.new_numeric,
                       p.old_rank,
                       p.new_rank,
                       p.payload || jsonb_build_object(
                           'coalescedEventCount', g.event_count,
                           'coalescedEventKinds', g.event_kinds,
                           'coalescedInstruments', g.coalesced_instruments,
                           'coalescedEvents', g.coalesced_events) AS payload
                FROM primary_event_rows p
                JOIN ordered_grouped_event_rows g
                  ON g.account_id = p.account_id
                 AND g.song_id = p.song_id
            )
            """;

        if (!execute)
            return $"""
                {coalesced}
                SELECT COUNT(*) FROM coalesced_event_rows;
                """;

        return $"""
            {coalesced}, superseded AS (
                UPDATE player_improvement_events existing
                SET expires_at = LEAST(existing.expires_at, @detectedAt, now())
                FROM (SELECT DISTINCT account_id, song_id FROM coalesced_event_rows) lanes
                WHERE existing.account_id = lanes.account_id
                  AND existing.song_id = lanes.song_id
                  AND existing.expires_at > now()
                RETURNING 1
            ), inserted AS (
                INSERT INTO player_improvement_events (
                    run_id, account_id, event_kind, song_id, instrument, metric,
                    old_numeric, new_numeric, old_rank, new_rank, payload,
                    detected_at, expires_at, source)
                SELECT @runId, account_id, event_kind, song_id, instrument, metric,
                       old_numeric, new_numeric, old_rank, new_rank, payload,
                       @detectedAt, @expiresAt, @source
                FROM coalesced_event_rows
                RETURNING 1
            )
            SELECT COUNT(*) FROM inserted;
            """;
    }

    private static string PlayerRankEventSelectOrInsertSql(bool execute)
    {
        const string coalesced = """
            , grouped_event_rows AS (
                SELECT account_id,
                       instrument,
                       COUNT(*) AS event_count,
                       CASE
                           WHEN BOOL_OR(event_kind IN ('player_total_score_improved', 'player_fc_count_improved')) THEN 'instrumentAggregate'
                           ELSE 'aggregateRank'
                       END AS coalesced_group,
                       array_agg(event_kind ORDER BY CASE event_kind
                           WHEN 'player_total_score_improved' THEN 10
                           WHEN 'player_total_score_rank_improved' THEN 20
                           WHEN 'player_skill_rank_improved' THEN 30
                           WHEN 'player_weighted_rank_improved' THEN 40
                           WHEN 'player_fc_count_improved' THEN 50
                           WHEN 'player_fc_rate_rank_improved' THEN 60
                           ELSE 100 END, event_kind) AS event_kinds,
                       jsonb_agg(jsonb_build_object(
                           'eventKind', event_kind,
                           'metric', metric,
                           'oldNumeric', old_numeric,
                           'newNumeric', new_numeric,
                           'oldRank', old_rank,
                           'newRank', new_rank)
                           ORDER BY CASE event_kind
                               WHEN 'player_total_score_improved' THEN 10
                               WHEN 'player_total_score_rank_improved' THEN 20
                               WHEN 'player_skill_rank_improved' THEN 30
                               WHEN 'player_weighted_rank_improved' THEN 40
                               WHEN 'player_fc_count_improved' THEN 50
                               WHEN 'player_fc_rate_rank_improved' THEN 60
                               ELSE 100 END, event_kind) AS coalesced_events
                FROM event_rows
                GROUP BY account_id, instrument
            ), primary_event_rows AS (
                SELECT DISTINCT ON (account_id, instrument) *
                FROM event_rows
                ORDER BY account_id, instrument, CASE event_kind
                    WHEN 'player_total_score_improved' THEN 10
                    WHEN 'player_total_score_rank_improved' THEN 20
                    WHEN 'player_skill_rank_improved' THEN 30
                    WHEN 'player_weighted_rank_improved' THEN 40
                    WHEN 'player_fc_count_improved' THEN 50
                    WHEN 'player_fc_rate_rank_improved' THEN 60
                    ELSE 100 END, event_kind
            ), coalesced_event_rows AS (
                SELECT p.account_id,
                       p.event_kind,
                       p.instrument,
                       p.metric,
                       p.old_numeric,
                       p.new_numeric,
                       p.old_rank,
                       p.new_rank,
                       p.payload || jsonb_build_object(
                           'coalescedGroup', g.coalesced_group,
                           'coalescedEventCount', g.event_count,
                           'coalescedEventKinds', g.event_kinds,
                           'coalescedEvents', g.coalesced_events) AS payload
                FROM primary_event_rows p
                JOIN grouped_event_rows g
                  ON g.account_id = p.account_id
                 AND g.instrument = p.instrument
            )
            """;

        if (!execute)
            return $"""
                {coalesced}
                SELECT COUNT(*) FROM coalesced_event_rows;
                """;

        return $"""
            {coalesced}, superseded AS (
                UPDATE player_improvement_events existing
                SET expires_at = LEAST(existing.expires_at, @detectedAt, now())
                FROM (SELECT DISTINCT account_id, instrument FROM coalesced_event_rows) lanes
                WHERE existing.account_id = lanes.account_id
                  AND existing.song_id IS NULL
                  AND existing.instrument IS NOT DISTINCT FROM lanes.instrument
                  AND existing.event_kind IN (
                      'player_total_score_improved',
                      'player_total_score_rank_improved',
                      'player_fc_count_improved',
                      'player_fc_rate_rank_improved',
                      'player_skill_rank_improved',
                      'player_weighted_rank_improved')
                  AND existing.expires_at > now()
                RETURNING 1
            ), inserted AS (
                INSERT INTO player_improvement_events (
                    run_id, account_id, event_kind, instrument, metric,
                    old_numeric, new_numeric, old_rank, new_rank, payload,
                    detected_at, expires_at, source)
                SELECT @runId, account_id, event_kind, instrument, metric,
                       old_numeric, new_numeric, old_rank, new_rank, payload,
                       @detectedAt, @expiresAt, @source
                FROM coalesced_event_rows
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
            {BandCurrentRowsFromSql(registeredOnly)}
            GROUP BY c.band_type, c.team_key, c.team_members
            UNION ALL
            SELECT r.band_type, r.team_key, r.team_members, MIN(r.computed_at) AS first_seen_at, MAX(r.computed_at) AS last_seen_at
            FROM ({BandRankUnionSql(registeredOnly)}) r
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
            {BandCurrentRowsFromSql(registeredOnly)}
            JOIN band_improvement_subjects s ON s.band_type = c.band_type AND s.team_key = c.team_key
        ), event_rows AS (
            SELECT c.band_subject_id,
                   v.event_kind,
                   c.song_id,
                   c.ranking_scope,
                   COALESCE(c.scope_combo_id, '') AS combo_id,
                   c.score AS play_score,
                   COALESCE(c.entry_combo_id, '') AS play_combo_id,
                   COALESCE(c.entry_instrument_combo, '') AS play_instrument_combo,
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
                ('band_song_rank_improved', 'song_rank', NULL::NUMERIC, NULL::NUMERIC, s.rank, c.rank, s.score IS NOT NULL AND c.score IS NOT NULL AND c.score > s.score AND s.rank IS NOT NULL AND c.rank IS NOT NULL AND c.rank > 0 AND c.rank < s.rank),
                ('band_stars_improved', 'stars', s.stars::NUMERIC, c.stars::NUMERIC, NULL::INTEGER, NULL::INTEGER, s.band_subject_id IS NOT NULL AND c.stars IS NOT NULL AND s.stars IS NOT NULL AND c.stars > s.stars),
                ('band_gold_stars_achieved', 'stars', s.stars::NUMERIC, c.stars::NUMERIC, NULL::INTEGER, NULL::INTEGER, c.stars >= 6 AND (s.band_subject_id IS NULL OR COALESCE(s.stars, 0) < 6)),
                ('band_fc_achieved', 'full_combo', NULL::NUMERIC, NULL::NUMERIC, NULL::INTEGER, NULL::INTEGER, c.is_full_combo IS TRUE AND (s.band_subject_id IS NULL OR COALESCE(s.is_full_combo, false) = false)),
                ('band_member_difficulty_bumped', 'difficulty', s.difficulty::NUMERIC, c.difficulty::NUMERIC, NULL::INTEGER, NULL::INTEGER, s.band_subject_id IS NOT NULL AND c.difficulty IS NOT NULL AND s.difficulty IS NOT NULL AND c.difficulty > s.difficulty)
            ) AS v(event_kind, metric, old_numeric, new_numeric, old_rank, new_rank, should_emit)
            WHERE v.should_emit
        )
        {BandSongEventSelectOrInsertSql(execute)}
        """;

    private static string BandSongStateUpsertSql(bool registeredOnly) => $"""
        WITH current_rows AS (
            SELECT c.*, s.band_subject_id
            {BandCurrentRowsFromSql(registeredOnly)}
            JOIN band_improvement_subjects s ON s.band_type = c.band_type AND s.team_key = c.team_key
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
            FROM ({BandRankUnionSql(registeredOnly)}) r
            JOIN band_improvement_subjects s ON s.band_type = r.band_type AND s.team_key = r.team_key
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
                ('band_weighted_rank_improved', 'weighted_rank', NULL::NUMERIC, NULL::NUMERIC, s.weighted_rank, c.weighted_rank, s.weighted_rank IS NOT NULL AND c.weighted_rank IS NOT NULL AND c.weighted_rank > 0 AND c.weighted_rank < s.weighted_rank AND ((s.total_score IS NOT NULL AND c.total_score IS NOT NULL AND c.total_score > s.total_score) OR (s.full_combo_count IS NOT NULL AND c.full_combo_count IS NOT NULL AND c.full_combo_count > s.full_combo_count))),
                ('band_total_score_rank_improved', 'total_score_rank', NULL::NUMERIC, NULL::NUMERIC, s.total_score_rank, c.total_score_rank, s.total_score_rank IS NOT NULL AND c.total_score_rank IS NOT NULL AND c.total_score_rank > 0 AND c.total_score_rank < s.total_score_rank AND s.total_score IS NOT NULL AND c.total_score IS NOT NULL AND c.total_score > s.total_score),
                ('band_fc_rate_rank_improved', 'fc_rate_rank', NULL::NUMERIC, NULL::NUMERIC, s.fc_rate_rank, c.fc_rate_rank, s.fc_rate_rank IS NOT NULL AND c.fc_rate_rank IS NOT NULL AND c.fc_rate_rank > 0 AND c.fc_rate_rank < s.fc_rate_rank AND s.full_combo_count IS NOT NULL AND c.full_combo_count IS NOT NULL AND c.full_combo_count > s.full_combo_count),
                ('band_total_score_improved', 'total_score', s.total_score::NUMERIC, c.total_score::NUMERIC, NULL::INTEGER, NULL::INTEGER, s.total_score IS NOT NULL AND c.total_score IS NOT NULL AND c.total_score > s.total_score),
                ('band_fc_count_improved', 'full_combo_count', s.full_combo_count::NUMERIC, c.full_combo_count::NUMERIC, NULL::INTEGER, NULL::INTEGER, s.full_combo_count IS NOT NULL AND c.full_combo_count IS NOT NULL AND c.full_combo_count > s.full_combo_count)
            ) AS v(event_kind, metric, old_numeric, new_numeric, old_rank, new_rank, should_emit)
            WHERE v.should_emit
        )
        {BandRankEventSelectOrInsertSql(execute)}
        """;

    private static string BandSongEventSelectOrInsertSql(bool execute)
    {
        const string coalesced = """
            , keyed_event_rows AS (
                SELECT *,
                       CASE
                           WHEN event_kind = 'band_song_rank_improved' THEN event_kind || '|' || ranking_scope || '|' || combo_id
                           WHEN event_kind IN ('band_score_pb', 'band_combo_score_pb') THEN 'band_score_pb'
                           ELSE event_kind
                       END AS coalesced_event_key
                FROM event_rows
            ), deduplicated_event_rows AS (
                SELECT DISTINCT ON (band_subject_id, song_id, play_score, play_combo_id, play_instrument_combo, coalesced_event_key) *
                FROM keyed_event_rows
                ORDER BY band_subject_id,
                         song_id,
                         play_score,
                         play_combo_id,
                         play_instrument_combo,
                         coalesced_event_key,
                         CASE
                             WHEN event_kind = 'band_score_pb' THEN 0
                             WHEN event_kind = 'band_combo_score_pb' THEN 1
                             WHEN ranking_scope = 'overall' THEN 0
                             ELSE 1
                         END,
                         CASE event_kind
                             WHEN 'band_first_score' THEN 10
                             WHEN 'band_score_pb' THEN 20
                             WHEN 'band_combo_score_pb' THEN 20
                             WHEN 'band_fc_achieved' THEN 30
                             WHEN 'band_gold_stars_achieved' THEN 40
                             WHEN 'band_stars_improved' THEN 50
                             WHEN 'band_song_rank_improved' THEN 60
                             WHEN 'band_member_difficulty_bumped' THEN 70
                             ELSE 100 END,
                         event_kind
            ), grouped_event_rows AS (
                SELECT band_subject_id,
                       song_id,
                       play_score,
                       play_combo_id,
                       play_instrument_combo,
                       COUNT(*) AS event_count,
                       array_agg(event_kind ORDER BY CASE event_kind
                           WHEN 'band_first_score' THEN 10
                           WHEN 'band_score_pb' THEN 20
                           WHEN 'band_combo_score_pb' THEN 20
                           WHEN 'band_fc_achieved' THEN 30
                           WHEN 'band_gold_stars_achieved' THEN 40
                           WHEN 'band_stars_improved' THEN 50
                           WHEN 'band_song_rank_improved' THEN 60
                           WHEN 'band_member_difficulty_bumped' THEN 70
                           ELSE 100 END, event_kind, CASE WHEN ranking_scope = 'overall' THEN 0 ELSE 1 END, combo_id) AS event_kinds,
                       jsonb_agg(jsonb_build_object(
                           'eventKind', event_kind,
                           'metric', metric,
                           'oldNumeric', old_numeric,
                           'newNumeric', new_numeric,
                           'oldRank', old_rank,
                           'newRank', new_rank,
                           'rankingScope', ranking_scope,
                           'scopeComboId', combo_id,
                           'entryComboId', play_combo_id,
                           'entryInstrumentCombo', play_instrument_combo)
                           ORDER BY CASE event_kind
                               WHEN 'band_first_score' THEN 10
                               WHEN 'band_score_pb' THEN 20
                               WHEN 'band_combo_score_pb' THEN 20
                               WHEN 'band_fc_achieved' THEN 30
                               WHEN 'band_gold_stars_achieved' THEN 40
                               WHEN 'band_stars_improved' THEN 50
                               WHEN 'band_song_rank_improved' THEN 60
                               WHEN 'band_member_difficulty_bumped' THEN 70
                               ELSE 100 END, event_kind, CASE WHEN ranking_scope = 'overall' THEN 0 ELSE 1 END, combo_id) AS coalesced_events
                FROM deduplicated_event_rows
                GROUP BY band_subject_id, song_id, play_score, play_combo_id, play_instrument_combo
            ), primary_event_rows AS (
                SELECT DISTINCT ON (band_subject_id, song_id, play_score, play_combo_id, play_instrument_combo) *
                FROM deduplicated_event_rows
                ORDER BY band_subject_id, song_id, play_score, play_combo_id, play_instrument_combo, CASE event_kind
                    WHEN 'band_first_score' THEN 10
                    WHEN 'band_score_pb' THEN 20
                    WHEN 'band_combo_score_pb' THEN 20
                    WHEN 'band_fc_achieved' THEN 30
                    WHEN 'band_gold_stars_achieved' THEN 40
                    WHEN 'band_stars_improved' THEN 50
                    WHEN 'band_song_rank_improved' THEN 60
                    WHEN 'band_member_difficulty_bumped' THEN 70
                    ELSE 100 END,
                    CASE
                        WHEN event_kind = 'band_score_pb' THEN 0
                        WHEN event_kind = 'band_combo_score_pb' THEN 1
                        WHEN ranking_scope = 'overall' THEN 0
                        ELSE 1
                    END,
                    event_kind
            ), coalesced_event_rows AS (
                SELECT p.band_subject_id,
                       p.event_kind,
                       p.song_id,
                       p.ranking_scope,
                       p.combo_id,
                       p.metric,
                       p.old_numeric,
                       p.new_numeric,
                       p.old_rank,
                       p.new_rank,
                       p.payload || jsonb_build_object(
                           'coalescedEventCount', g.event_count,
                           'coalescedEventKinds', g.event_kinds,
                           'coalescedEvents', g.coalesced_events) AS payload
                FROM primary_event_rows p
                JOIN grouped_event_rows g
                  ON g.band_subject_id = p.band_subject_id
                 AND g.song_id = p.song_id
                                 AND g.play_score IS NOT DISTINCT FROM p.play_score
                                 AND g.play_combo_id = p.play_combo_id
                                 AND g.play_instrument_combo = p.play_instrument_combo
            )
            """;

        if (!execute)
            return $"""
                {coalesced}
                SELECT COUNT(*) FROM coalesced_event_rows;
                """;

        return $"""
            {coalesced}, superseded AS (
                UPDATE band_improvement_events existing
                SET expires_at = LEAST(existing.expires_at, @detectedAt, now())
                FROM (SELECT DISTINCT band_subject_id, song_id, ranking_scope, combo_id FROM deduplicated_event_rows) lanes
                WHERE existing.band_subject_id = lanes.band_subject_id
                  AND existing.song_id = lanes.song_id
                  AND existing.ranking_scope = lanes.ranking_scope
                  AND existing.combo_id = lanes.combo_id
                  AND existing.expires_at > now()
                RETURNING 1
            ), inserted AS (
                INSERT INTO band_improvement_events (
                    run_id, band_subject_id, event_kind, song_id, ranking_scope, combo_id, metric,
                    old_numeric, new_numeric, old_rank, new_rank, payload,
                    detected_at, expires_at, source)
                SELECT @runId, band_subject_id, event_kind, song_id, ranking_scope, combo_id, metric,
                       old_numeric, new_numeric, old_rank, new_rank, payload,
                       @detectedAt, @expiresAt, @source
                FROM coalesced_event_rows
                RETURNING 1
            )
            SELECT COUNT(*) FROM inserted;
            """;
    }

    private static string BandRankEventSelectOrInsertSql(bool execute)
    {
        if (!execute)
            return """
                , rank_event_rows AS (
                    SELECT *
                    FROM event_rows
                    WHERE event_kind IN (
                        'band_total_score_rank_improved',
                        'band_weighted_rank_improved',
                        'band_fc_rate_rank_improved')
                ), progress_event_rows AS (
                    SELECT *
                    FROM event_rows
                    WHERE event_kind NOT IN (
                        'band_total_score_rank_improved',
                        'band_weighted_rank_improved',
                        'band_fc_rate_rank_improved')
                ), grouped_rank_event_rows AS (
                    SELECT band_subject_id,
                           ranking_scope,
                           combo_id,
                           COUNT(*) AS event_count
                    FROM rank_event_rows
                    GROUP BY band_subject_id, ranking_scope, combo_id
                )
                SELECT
                    (SELECT COUNT(*) FROM grouped_rank_event_rows)
                    + (SELECT COUNT(*) FROM progress_event_rows);
                """;

        return """
            , rank_event_rows AS (
                SELECT *
                FROM event_rows
                WHERE event_kind IN (
                    'band_total_score_rank_improved',
                    'band_weighted_rank_improved',
                    'band_fc_rate_rank_improved')
            ), progress_event_rows AS (
                SELECT *
                FROM event_rows
                WHERE event_kind NOT IN (
                    'band_total_score_rank_improved',
                    'band_weighted_rank_improved',
                    'band_fc_rate_rank_improved')
            ), grouped_rank_event_rows AS (
                SELECT band_subject_id,
                       ranking_scope,
                       combo_id,
                       COUNT(*) AS event_count,
                       array_agg(event_kind ORDER BY CASE event_kind
                           WHEN 'band_total_score_rank_improved' THEN 10
                           WHEN 'band_weighted_rank_improved' THEN 30
                           WHEN 'band_fc_rate_rank_improved' THEN 40
                           ELSE 100 END, event_kind) AS event_kinds,
                       jsonb_agg(jsonb_build_object(
                           'eventKind', event_kind,
                           'metric', metric,
                           'oldNumeric', old_numeric,
                           'newNumeric', new_numeric,
                           'oldRank', old_rank,
                           'newRank', new_rank,
                           'rankingScope', ranking_scope,
                           'scopeComboId', combo_id,
                           'comboId', combo_id)
                           ORDER BY CASE event_kind
                               WHEN 'band_total_score_rank_improved' THEN 10
                               WHEN 'band_weighted_rank_improved' THEN 30
                               WHEN 'band_fc_rate_rank_improved' THEN 40
                               ELSE 100 END, event_kind) AS coalesced_events
                FROM rank_event_rows
                GROUP BY band_subject_id, ranking_scope, combo_id
            ), primary_rank_event_rows AS (
                SELECT DISTINCT ON (band_subject_id, ranking_scope, combo_id) *
                FROM rank_event_rows
                ORDER BY band_subject_id, ranking_scope, combo_id, CASE event_kind
                    WHEN 'band_total_score_rank_improved' THEN 10
                    WHEN 'band_weighted_rank_improved' THEN 30
                    WHEN 'band_fc_rate_rank_improved' THEN 40
                    ELSE 100 END, event_kind
            ), coalesced_rank_event_rows AS (
                SELECT p.band_subject_id,
                       p.event_kind,
                       p.ranking_scope,
                       p.combo_id,
                       p.metric,
                       p.old_numeric,
                       p.new_numeric,
                       p.old_rank,
                       p.new_rank,
                       p.payload || jsonb_build_object(
                           'coalescedGroup', 'aggregateRank',
                           'coalescedEventCount', g.event_count,
                           'coalescedEventKinds', g.event_kinds,
                           'coalescedEvents', g.coalesced_events) AS payload
                FROM primary_rank_event_rows p
                JOIN grouped_rank_event_rows g
                  ON g.band_subject_id = p.band_subject_id
                 AND g.ranking_scope = p.ranking_scope
                 AND g.combo_id = p.combo_id
            ), coalesced_event_rows AS (
                SELECT * FROM coalesced_rank_event_rows
                UNION ALL
                SELECT band_subject_id,
                       event_kind,
                       ranking_scope,
                       combo_id,
                       metric,
                       old_numeric,
                       new_numeric,
                       old_rank,
                       new_rank,
                       payload
                FROM progress_event_rows
            ), superseded_rank AS (
                UPDATE band_improvement_events existing
                SET expires_at = LEAST(existing.expires_at, @detectedAt, now())
                FROM (SELECT DISTINCT band_subject_id, ranking_scope, combo_id FROM rank_event_rows) lanes
                WHERE existing.band_subject_id = lanes.band_subject_id
                  AND existing.song_id IS NULL
                  AND existing.ranking_scope = lanes.ranking_scope
                  AND existing.combo_id = lanes.combo_id
                  AND existing.event_kind IN (
                      'band_total_score_rank_improved',
                      'band_weighted_rank_improved',
                      'band_fc_rate_rank_improved')
                  AND existing.expires_at > now()
                RETURNING 1
            ), superseded_progress AS (
                UPDATE band_improvement_events existing
                SET expires_at = LEAST(existing.expires_at, @detectedAt, now())
                FROM (SELECT DISTINCT band_subject_id, ranking_scope, combo_id, metric FROM progress_event_rows) lanes
                WHERE existing.band_subject_id = lanes.band_subject_id
                  AND existing.song_id IS NULL
                  AND existing.ranking_scope = lanes.ranking_scope
                  AND existing.combo_id = lanes.combo_id
                  AND existing.metric IS NOT DISTINCT FROM lanes.metric
                  AND existing.expires_at > now()
                RETURNING 1
            ), inserted AS (
                INSERT INTO band_improvement_events (
                    run_id, band_subject_id, event_kind, ranking_scope, combo_id, metric,
                    old_numeric, new_numeric, old_rank, new_rank, payload,
                    detected_at, expires_at, source)
                SELECT @runId, band_subject_id, event_kind, ranking_scope, combo_id, metric,
                       old_numeric, new_numeric, old_rank, new_rank, payload,
                       @detectedAt, @expiresAt, @source
                FROM coalesced_event_rows
                RETURNING 1
            )
            SELECT COUNT(*) FROM inserted;
            """;
    }

    private static string BandRankStateUpsertSql(bool registeredOnly) => $"""
        WITH current_rows AS (
            SELECT r.*, s.band_subject_id
            FROM ({BandRankUnionSql(registeredOnly)}) r
            JOIN band_improvement_subjects s ON s.band_type = r.band_type AND s.team_key = r.team_key
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
    Guid NotificationGuid,
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

public sealed record NewShopSongServiceNotification(
    string SongId,
    string Title,
    string Artist,
    string? AlbumArt,
    string SourceKey,
    DateTime? ShopInDateUtc);

public sealed record ImprovementNotificationsEnvelope(
    DateTime GeneratedAt,
    int ExpiresAfterHours,
    long? SourceRunId,
    DateTime? SourceCompletedAt,
    IReadOnlyList<ImprovementNotificationDto> Items);

public sealed record ImprovementNotificationSourceCursor(
    long? RunId,
    DateTime? CompletedAt);

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
    string Source = "precompute",
    long? PublishedScrapeId = null);

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
    long PlayerSongBaselineRows,
    long PlayerRankBaselineRows,
    long BandSongBaselineRows,
    long BandRankBaselineRows,
    string? ErrorMessage);

public sealed record ImprovementNotificationPublicationStatus(
    long? PublishedScrapeId,
    DateTime? PublishedAtUtc,
    bool PublicReadsFrozen,
    long? MarkerScrapeId,
    string? MarkerStatus,
    int AttemptCount,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    string? ErrorMessage,
    long? LatestPlayerScrapeId,
    long? LatestPlayerRunId,
    DateTime? LatestPlayerCompletedAtUtc,
    long? LatestBandScrapeId,
    long? LatestBandRunId,
    DateTime? LatestBandCompletedAtUtc)
{
    public static ImprovementNotificationPublicationStatus Empty { get; } = new(
        null, null, false, null, null, 0, null, null, null, null, null, null, null, null, null);

    public bool IsCompleteForPublishedScrape(bool includePlayers, bool includeBands)
    {
        if (!PublishedScrapeId.HasValue)
            return false;

        return (!includePlayers || LatestPlayerScrapeId == PublishedScrapeId)
            && (!includeBands || LatestBandScrapeId == PublishedScrapeId);
    }
}