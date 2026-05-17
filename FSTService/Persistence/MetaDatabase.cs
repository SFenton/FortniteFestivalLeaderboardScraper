using System.Diagnostics;
using System.Text.Json;
using FSTService.Scraping;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

/// <summary>
/// Central metadata database (<see cref="IMetaDatabase"/> implementation).
/// Uses NpgsqlDataSource (connection pooling) — MVCC handles concurrent reads/writes natively.
/// </summary>
public sealed class MetaDatabase : IMetaDatabase
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<MetaDatabase> _log;
    private readonly BandRankHistoryOptions _bandRankHistoryOptions;
    private readonly object _bandRankHistoryPollingSchemaLock = new();
    private bool _bandRankHistoryPollingSchemaEnsured;

    internal const int DataCollectionVersion = 3;
    internal const string WebTrackerDeviceId = "web-tracker";
    internal const string WebBandTrackerDeviceId = "web-band-tracker";
    internal const string LegacyLeaderboardStagingTable = "leaderboard_staging";
    internal const string LeaderboardStagingTable = "leaderboard_staging_v2";
    private const string LeaderboardStagingReadColumns = "scrape_id, song_id, instrument, page_num, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, rank, end_time, api_rank, source, staged_at";

    public MetaDatabase(NpgsqlDataSource dataSource, ILogger<MetaDatabase> log, BandRankHistoryOptions? bandRankHistoryOptions = null)
    {
        _ds = dataSource;
        _log = log;
        _bandRankHistoryOptions = bandRankHistoryOptions ?? new BandRankHistoryOptions();
    }

    public MetaDatabase(NpgsqlDataSource dataSource, ILogger<MetaDatabase> log, IOptions<BandRankHistoryOptions> bandRankHistoryOptions)
        : this(dataSource, log, bandRankHistoryOptions.Value)
    {
    }

    public void EnsureSchema() { } // Created by DatabaseInitializer

    internal static string GetLeaderboardStagingReadSource(string alias) =>
        $"(SELECT {LeaderboardStagingReadColumns} FROM {LeaderboardStagingTable} " +
        $"UNION ALL SELECT {LeaderboardStagingReadColumns} FROM {LegacyLeaderboardStagingTable}) AS {alias}";

    // ── Scrape log ───────────────────────────────────────────────────

    public long StartScrapeRun()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO scrape_log (started_at) VALUES (@now) RETURNING id";
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        return (long)(int)cmd.ExecuteScalar()!;
    }

    public void CompleteScrapeRun(long scrapeId, int songsScraped, long totalEntries, int totalRequests, long totalBytes, bool epicReportedOver100Pages = false)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE scrape_log SET completed_at = @now, songs_scraped = @songs, total_entries = @entries, total_requests = @requests, total_bytes = @bytes, epic_reported_over_100_pages = @epicReportedOver100Pages WHERE id = @id";
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("songs", songsScraped);
        cmd.Parameters.AddWithValue("entries", (int)totalEntries);
        cmd.Parameters.AddWithValue("requests", totalRequests);
        cmd.Parameters.AddWithValue("bytes", totalBytes);
        cmd.Parameters.AddWithValue("epicReportedOver100Pages", epicReportedOver100Pages);
        cmd.Parameters.AddWithValue("id", (int)scrapeId);
        cmd.ExecuteNonQuery();
    }

    public ScrapeRunInfo? GetLastCompletedScrapeRun()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, started_at, completed_at, songs_scraped, total_entries, total_requests, total_bytes, epic_reported_over_100_pages FROM scrape_log WHERE completed_at IS NOT NULL ORDER BY id DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        return ReadScrapeRunInfo(r);
    }

    public ScrapeRunInfo? GetPublishedScrapeRun()
    {
                try
                {
                        using var conn = _ds.OpenConnection();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = """
                                SELECT scrape.id, scrape.started_at, scrape.completed_at, scrape.songs_scraped,
                                             scrape.total_entries, scrape.total_requests, scrape.total_bytes,
                                             scrape.epic_reported_over_100_pages
                                FROM scrape_publication_state publication
                                JOIN scrape_log scrape ON scrape.id = publication.published_scrape_id
                                WHERE publication.id = TRUE
                                    AND scrape.completed_at IS NOT NULL
                                UNION ALL
                                SELECT id, started_at, completed_at, songs_scraped, total_entries, total_requests,
                                             total_bytes, epic_reported_over_100_pages
                                FROM scrape_log
                                WHERE completed_at IS NOT NULL
                                    AND NOT EXISTS (SELECT 1 FROM scrape_publication_state WHERE id = TRUE AND published_scrape_id IS NOT NULL)
                                ORDER BY id DESC
                                LIMIT 1
                                """;
                        using var r = cmd.ExecuteReader();
                        return ReadScrapeRunInfo(r);
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
                {
                        return GetLastCompletedScrapeRun();
                }
    }

    public void PublishScrapeRun(long scrapeId, bool promoteCachedResponses = true)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        EnsureScrapePublicationStateTable(conn, tx);

        using (var verify = conn.CreateCommand())
        {
            verify.Transaction = tx;
            verify.CommandText = "SELECT completed_at IS NOT NULL FROM scrape_log WHERE id = @id";
            verify.Parameters.AddWithValue("id", (int)scrapeId);
            if (verify.ExecuteScalar() is not bool isCompleted || !isCompleted)
                throw new InvalidOperationException($"Scrape run {scrapeId} cannot be published before it is completed.");
        }

        if (promoteCachedResponses)
        {
            using var cache = conn.CreateCommand();
            cache.Transaction = tx;
            cache.CommandText = """
                TRUNCATE api_response_cache;
                INSERT INTO api_response_cache (cache_key, json_data, etag, cached_at)
                SELECT cache_key, json_data, etag, cached_at FROM api_response_cache_staging;
                TRUNCATE api_response_cache_staging;
                """;
            cache.ExecuteNonQuery();
        }

        using (var publish = conn.CreateCommand())
        {
            publish.Transaction = tx;
            publish.CommandText = """
                INSERT INTO scrape_publication_state (id, published_scrape_id, published_at, updated_at)
                VALUES (TRUE, @scrapeId, @now, @now)
                ON CONFLICT (id) DO UPDATE SET
                    published_scrape_id = EXCLUDED.published_scrape_id,
                    published_at = EXCLUDED.published_at,
                    public_reads_frozen = FALSE,
                    public_reads_frozen_at = NULL,
                    public_reads_frozen_scrape_id = NULL,
                    public_reads_frozen_reason = NULL,
                    updated_at = EXCLUDED.updated_at
                """;
            publish.Parameters.AddWithValue("scrapeId", (int)scrapeId);
            publish.Parameters.AddWithValue("now", DateTime.UtcNow);
            publish.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void SetPublicReadFreeze(bool frozen, long? scrapeId = null, string? reason = null)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        EnsureScrapePublicationStateTable(conn, tx);

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO scrape_publication_state (id, public_reads_frozen, public_reads_frozen_at,
                    public_reads_frozen_scrape_id, public_reads_frozen_reason, updated_at)
                VALUES (TRUE, @frozen, CASE WHEN @frozen THEN @now ELSE NULL END,
                    @scrapeId, @reason, @now)
                ON CONFLICT (id) DO UPDATE SET
                    public_reads_frozen = EXCLUDED.public_reads_frozen,
                    public_reads_frozen_at = EXCLUDED.public_reads_frozen_at,
                    public_reads_frozen_scrape_id = EXCLUDED.public_reads_frozen_scrape_id,
                    public_reads_frozen_reason = EXCLUDED.public_reads_frozen_reason,
                    updated_at = EXCLUDED.updated_at
                """;
            cmd.Parameters.AddWithValue("frozen", frozen);
            cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("scrapeId", scrapeId is null ? DBNull.Value : (int)scrapeId.Value);
            cmd.Parameters.AddWithValue("reason", string.IsNullOrWhiteSpace(reason) ? DBNull.Value : reason.Trim());
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public PublicReadFreezeState GetPublicReadFreezeState()
    {
        try
        {
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT public_reads_frozen, public_reads_frozen_at, public_reads_frozen_scrape_id,
                    public_reads_frozen_reason
                FROM scrape_publication_state
                WHERE id = TRUE
                """;

            using var r = cmd.ExecuteReader();
            if (!r.Read())
                return PublicReadFreezeState.NotFrozen;

            var frozen = r.GetBoolean(0);
            if (!frozen)
                return PublicReadFreezeState.NotFrozen;

            return new PublicReadFreezeState(
                true,
                r.IsDBNull(1) ? null : r.GetDateTime(1),
                r.IsDBNull(2) ? null : r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetString(3));
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        {
            return PublicReadFreezeState.NotFrozen;
        }
    }

    private static void EnsureScrapePublicationStateTable(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS scrape_publication_state (
                id                  BOOLEAN     PRIMARY KEY DEFAULT TRUE CHECK (id),
                published_scrape_id INTEGER     REFERENCES scrape_log(id),
                published_at        TIMESTAMPTZ,
                public_reads_frozen BOOLEAN     NOT NULL DEFAULT FALSE,
                public_reads_frozen_at TIMESTAMPTZ,
                public_reads_frozen_scrape_id INTEGER REFERENCES scrape_log(id),
                public_reads_frozen_reason TEXT,
                updated_at          TIMESTAMPTZ NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();

        using var alter = conn.CreateCommand();
        alter.Transaction = tx;
        alter.CommandText = """
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS public_reads_frozen BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS public_reads_frozen_at TIMESTAMPTZ;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS public_reads_frozen_scrape_id INTEGER REFERENCES scrape_log(id);
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS public_reads_frozen_reason TEXT;
            """;
        alter.ExecuteNonQuery();
    }

    private static ScrapeRunInfo? ReadScrapeRunInfo(NpgsqlDataReader r)
    {
        if (!r.Read()) return null;
        return new ScrapeRunInfo
        {
            Id = Convert.ToInt64(r.GetValue(0)),
            StartedAt = r.GetDateTime(1).ToString("o"),
            CompletedAt = r.IsDBNull(2) ? null : r.GetDateTime(2).ToString("o"),
            SongsScraped = r.IsDBNull(3) ? 0 : r.GetInt32(3),
            TotalEntries = r.IsDBNull(4) ? 0 : r.GetInt32(4),
            TotalRequests = r.IsDBNull(5) ? 0 : r.GetInt32(5),
            TotalBytes = r.IsDBNull(6) ? 0 : r.GetInt64(6),
            EpicReportedOver100Pages = !r.IsDBNull(7) && r.GetBoolean(7),
        };
    }

    public bool ShouldShowLeaderboardEntryTotals()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT epic_reported_over_100_pages FROM scrape_log WHERE completed_at IS NOT NULL ORDER BY id DESC LIMIT 1";
        var result = cmd.ExecuteScalar();
        return result is bool value && value;
    }

    public void RecordScrapePhaseTiming(ScrapePhaseTimingRecord timing)
    {
        if (timing.ScrapeId <= 0 || string.IsNullOrWhiteSpace(timing.Phase))
            return;

        try
        {
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO scrape_phase_timings (
                    scrape_id, phase, subphase, item_key, started_at, completed_at, duration_ms,
                    rows_read, rows_written, rows_deleted, scope_count, success, error_message)
                VALUES (
                    @scrapeId, @phase, @subphase, @itemKey, @startedAt, @completedAt, @durationMs,
                    @rowsRead, @rowsWritten, @rowsDeleted, @scopeCount, @success, @errorMessage)
                """;
            cmd.Parameters.AddWithValue("scrapeId", timing.ScrapeId);
            cmd.Parameters.AddWithValue("phase", timing.Phase);
            cmd.Parameters.AddWithValue("subphase", (object?)timing.Subphase ?? DBNull.Value);
            cmd.Parameters.AddWithValue("itemKey", (object?)timing.ItemKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("startedAt", timing.StartedAtUtc);
            cmd.Parameters.AddWithValue("completedAt", timing.CompletedAtUtc);
            cmd.Parameters.AddWithValue("durationMs", timing.DurationMs);
            cmd.Parameters.AddWithValue("rowsRead", (object?)timing.RowsRead ?? DBNull.Value);
            cmd.Parameters.AddWithValue("rowsWritten", (object?)timing.RowsWritten ?? DBNull.Value);
            cmd.Parameters.AddWithValue("rowsDeleted", (object?)timing.RowsDeleted ?? DBNull.Value);
            cmd.Parameters.AddWithValue("scopeCount", (object?)timing.ScopeCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("success", timing.Success);
            cmd.Parameters.AddWithValue("errorMessage", (object?)timing.ErrorMessage ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to record scrape phase timing for {Phase}. Continuing.", timing.Phase);
        }
    }

    // ── Score history ────────────────────────────────────────────────

    public void InsertScoreChange(string songId, string instrument, string accountId,
        int? oldScore, int newScore, int? oldRank, int newRank,
        int? accuracy = null, bool? isFullCombo = null, int? stars = null,
        double? percentile = null, int? season = null, string? scoreAchievedAt = null,
        int? seasonRank = null, int? allTimeRank = null, int? difficulty = null)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        var parsedScoreAchievedAt = scoreAchievedAt is not null ? ParseUtc(scoreAchievedAt) : (DateTime?)null;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO score_history (song_id, instrument, account_id, old_score, new_score, old_rank, new_rank, accuracy, is_full_combo, stars, percentile, season, score_achieved_at, season_rank, all_time_rank, difficulty, changed_at) " +
            "VALUES (@songId, @instrument, @accountId, @oldScore, @newScore, @oldRank, @newRank, @accuracy, @isFullCombo, @stars, @percentile, @season, @scoreAchievedAt, @seasonRank, @allTimeRank, @difficulty, @now) " +
            "ON CONFLICT (account_id, song_id, instrument, new_score, score_achieved_at) DO UPDATE SET " +
            "season_rank = COALESCE(EXCLUDED.season_rank, score_history.season_rank), all_time_rank = COALESCE(EXCLUDED.all_time_rank, score_history.all_time_rank)";
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("oldScore", (object?)oldScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("newScore", newScore);
        cmd.Parameters.AddWithValue("oldRank", (object?)oldRank ?? DBNull.Value);
        cmd.Parameters.AddWithValue("newRank", newRank);
        cmd.Parameters.AddWithValue("accuracy", (object?)accuracy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("isFullCombo", (object?)isFullCombo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("stars", (object?)stars ?? DBNull.Value);
        cmd.Parameters.AddWithValue("percentile", (object?)percentile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("season", (object?)season ?? DBNull.Value);
        cmd.Parameters.AddWithValue("scoreAchievedAt", parsedScoreAchievedAt.HasValue ? parsedScoreAchievedAt.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("seasonRank", (object?)seasonRank ?? DBNull.Value);
        cmd.Parameters.AddWithValue("allTimeRank", (object?)allTimeRank ?? DBNull.Value);
        cmd.Parameters.AddWithValue("difficulty", (object?)difficulty ?? DBNull.Value);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();

        UpsertSoloScoreObservation(
            conn, tx,
            songId, instrument, accountId, newScore, accuracy, isFullCombo, stars,
            percentile, season, parsedScoreAchievedAt, newRank, seasonRank, allTimeRank, difficulty);

        tx.Commit();
    }

    public void BackfillScoreHistoryDifficulty(string accountId, string songId, string instrument, int score, int difficulty)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE score_history SET difficulty = @difficulty WHERE account_id = @accountId AND song_id = @songId AND instrument = @instrument AND new_score = @score AND difficulty IS NULL";
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("difficulty", difficulty);
        cmd.ExecuteNonQuery();
    }

    public int InsertScoreChanges(IReadOnlyList<ScoreChangeRecord> changes)
    {
        if (changes.Count == 0) return 0;
        var now = DateTime.UtcNow;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        // Use COPY + merge for larger batches
        if (changes.Count > 20)
        {
            using (var c = conn.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText =
                    "CREATE TEMP TABLE _sh_staging (" +
                    "song_id TEXT, instrument TEXT, account_id TEXT, old_score INTEGER, new_score INTEGER, " +
                    "old_rank INTEGER, new_rank INTEGER, accuracy INTEGER, is_full_combo BOOLEAN, " +
                    "stars INTEGER, percentile DOUBLE PRECISION, season INTEGER, " +
                    "score_achieved_at TIMESTAMPTZ, season_rank INTEGER, all_time_rank INTEGER, " +
                    "difficulty INTEGER, changed_at TIMESTAMPTZ" +
                    ") ON COMMIT DROP";
                c.ExecuteNonQuery();
            }

            using (var writer = conn.BeginBinaryImport(
                "COPY _sh_staging (song_id, instrument, account_id, old_score, new_score, old_rank, new_rank, " +
                "accuracy, is_full_combo, stars, percentile, season, score_achieved_at, season_rank, " +
                "all_time_rank, difficulty, changed_at) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var c in changes)
                {
                    writer.StartRow();
                    writer.Write(c.SongId, NpgsqlDbType.Text);
                    writer.Write(c.Instrument, NpgsqlDbType.Text);
                    writer.Write(c.AccountId, NpgsqlDbType.Text);
                    WriteNullableInt(writer, c.OldScore);
                    writer.Write(c.NewScore, NpgsqlDbType.Integer);
                    WriteNullableInt(writer, c.OldRank);
                    writer.Write(c.NewRank, NpgsqlDbType.Integer);
                    WriteNullableInt(writer, c.Accuracy);
                    if (c.IsFullCombo.HasValue) writer.Write(c.IsFullCombo.Value, NpgsqlDbType.Boolean);
                    else writer.WriteNull();
                    WriteNullableInt(writer, c.Stars);
                    if (c.Percentile.HasValue) writer.Write(c.Percentile.Value, NpgsqlDbType.Double);
                    else writer.WriteNull();
                    WriteNullableInt(writer, c.Season);
                    if (c.ScoreAchievedAt is not null) writer.Write(ParseUtc(c.ScoreAchievedAt), NpgsqlDbType.TimestampTz);
                    else writer.WriteNull();
                    WriteNullableInt(writer, c.SeasonRank);
                    WriteNullableInt(writer, c.AllTimeRank);
                    WriteNullableInt(writer, c.Difficulty);
                    writer.Write(now, NpgsqlDbType.TimestampTz);
                }
                writer.Complete();
            }

            int inserted;
            using (var c = conn.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandTimeout = 0;
                c.CommandText = """
                    WITH source_rows AS (
                        SELECT
                            song_id,
                            instrument,
                            account_id,
                            (ARRAY_AGG(old_score ORDER BY (old_score IS NULL), changed_at DESC))[1] AS old_score,
                            new_score,
                            (ARRAY_AGG(old_rank ORDER BY (old_rank IS NULL), changed_at DESC))[1] AS old_rank,
                            COALESCE(
                                MIN(all_time_rank) FILTER (WHERE all_time_rank IS NOT NULL),
                                MIN(season_rank) FILTER (WHERE season_rank IS NOT NULL),
                                MIN(new_rank)
                            ) AS new_rank,
                            MAX(accuracy) AS accuracy,
                            BOOL_OR(is_full_combo) FILTER (WHERE is_full_combo IS NOT NULL) AS is_full_combo,
                            MAX(stars) AS stars,
                            MAX(percentile) AS percentile,
                            MIN(season) FILTER (WHERE season IS NOT NULL) AS season,
                            score_achieved_at,
                            MIN(season_rank) FILTER (WHERE season_rank IS NOT NULL) AS season_rank,
                            MIN(all_time_rank) FILTER (WHERE all_time_rank IS NOT NULL) AS all_time_rank,
                            MAX(difficulty) AS difficulty,
                            MAX(changed_at) AS changed_at
                        FROM _sh_staging
                        GROUP BY song_id, instrument, account_id, new_score, score_achieved_at
                    )
                    INSERT INTO score_history (song_id, instrument, account_id, old_score, new_score, old_rank, new_rank, accuracy, is_full_combo, stars, percentile, season, score_achieved_at, season_rank, all_time_rank, difficulty, changed_at)
                    SELECT song_id, instrument, account_id, old_score, new_score, old_rank, new_rank, accuracy, is_full_combo, stars, percentile, season, score_achieved_at, season_rank, all_time_rank, difficulty, changed_at FROM source_rows
                    ON CONFLICT(account_id, song_id, instrument, new_score, score_achieved_at) DO UPDATE SET
                    season_rank = COALESCE(EXCLUDED.season_rank, score_history.season_rank), all_time_rank = COALESCE(EXCLUDED.all_time_rank, score_history.all_time_rank),
                    old_score = COALESCE(EXCLUDED.old_score, score_history.old_score), old_rank = COALESCE(EXCLUDED.old_rank, score_history.old_rank),
                    difficulty = COALESCE(EXCLUDED.difficulty, score_history.difficulty), changed_at = EXCLUDED.changed_at
                    """;
                inserted = c.ExecuteNonQuery();
            }
            UpsertSoloScoreObservationsFromStaging(conn, tx, "_sh_staging");
            tx.Commit();
            return inserted;
        }

        // Small batch: prepared-statement loop
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO score_history (song_id, instrument, account_id, old_score, new_score, old_rank, new_rank, accuracy, is_full_combo, stars, percentile, season, score_achieved_at, season_rank, all_time_rank, difficulty, changed_at) " +
            "VALUES (@songId, @instrument, @accountId, @oldScore, @newScore, @oldRank, @newRank, @accuracy, @fc, @stars, @percentile, @season, @scoreAchievedAt, @seasonRank, @allTimeRank, @difficulty, @now) " +
            "ON CONFLICT(account_id, song_id, instrument, new_score, score_achieved_at) DO UPDATE SET " +
            "season_rank = COALESCE(EXCLUDED.season_rank, score_history.season_rank), all_time_rank = COALESCE(EXCLUDED.all_time_rank, score_history.all_time_rank), " +
            "old_score = COALESCE(EXCLUDED.old_score, score_history.old_score), old_rank = COALESCE(EXCLUDED.old_rank, score_history.old_rank), " +
            "difficulty = COALESCE(EXCLUDED.difficulty, score_history.difficulty), changed_at = EXCLUDED.changed_at";
        var pSongId = cmd.Parameters.Add("songId", NpgsqlDbType.Text);
        var pInstrument = cmd.Parameters.Add("instrument", NpgsqlDbType.Text);
        var pAccountId = cmd.Parameters.Add("accountId", NpgsqlDbType.Text);
        var pOldScore = cmd.Parameters.Add("oldScore", NpgsqlDbType.Integer);
        var pNewScore = cmd.Parameters.Add("newScore", NpgsqlDbType.Integer);
        var pOldRank = cmd.Parameters.Add("oldRank", NpgsqlDbType.Integer);
        var pNewRank = cmd.Parameters.Add("newRank", NpgsqlDbType.Integer);
        var pAccuracy = cmd.Parameters.Add("accuracy", NpgsqlDbType.Integer);
        var pFc = cmd.Parameters.Add("fc", NpgsqlDbType.Boolean);
        var pStars = cmd.Parameters.Add("stars", NpgsqlDbType.Integer);
        var pPercentile = cmd.Parameters.Add("percentile", NpgsqlDbType.Double);
        var pSeason = cmd.Parameters.Add("season", NpgsqlDbType.Integer);
        var pScoreAchievedAt = cmd.Parameters.Add("scoreAchievedAt", NpgsqlDbType.TimestampTz);
        var pSeasonRank = cmd.Parameters.Add("seasonRank", NpgsqlDbType.Integer);
        var pAllTimeRank = cmd.Parameters.Add("allTimeRank", NpgsqlDbType.Integer);
        var pDifficulty = cmd.Parameters.Add("difficulty", NpgsqlDbType.Integer);
        var pNow = cmd.Parameters.Add("now", NpgsqlDbType.TimestampTz);
        cmd.Prepare();
        int loopInserted = 0;
        foreach (var c in changes)
        {
            pSongId.Value = c.SongId; pInstrument.Value = c.Instrument; pAccountId.Value = c.AccountId;
            pOldScore.Value = c.OldScore.HasValue ? c.OldScore.Value : DBNull.Value;
            pNewScore.Value = c.NewScore;
            pOldRank.Value = c.OldRank.HasValue ? c.OldRank.Value : DBNull.Value;
            pNewRank.Value = c.NewRank;
            pAccuracy.Value = c.Accuracy.HasValue ? c.Accuracy.Value : DBNull.Value;
            pFc.Value = c.IsFullCombo.HasValue ? c.IsFullCombo.Value : DBNull.Value;
            pStars.Value = c.Stars.HasValue ? c.Stars.Value : DBNull.Value;
            pPercentile.Value = c.Percentile.HasValue ? c.Percentile.Value : DBNull.Value;
            pSeason.Value = c.Season.HasValue ? c.Season.Value : DBNull.Value;
            pScoreAchievedAt.Value = c.ScoreAchievedAt is not null ? ParseUtc(c.ScoreAchievedAt) : DBNull.Value;
            pSeasonRank.Value = c.SeasonRank.HasValue ? c.SeasonRank.Value : DBNull.Value;
            pAllTimeRank.Value = c.AllTimeRank.HasValue ? c.AllTimeRank.Value : DBNull.Value;
            pDifficulty.Value = c.Difficulty.HasValue ? c.Difficulty.Value : DBNull.Value;
            pNow.Value = now;
            loopInserted += cmd.ExecuteNonQuery();
        }
        CreateScoreObservationStaging(conn, tx, changes, now);
        UpsertSoloScoreObservationsFromStaging(conn, tx, "_pso_solo_staging");
        tx.Commit();
        return loopInserted;
    }

    private static void UpsertSoloScoreObservation(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string songId,
        string instrument,
        string accountId,
        int score,
        int? accuracy,
        bool? isFullCombo,
        int? stars,
        double? percentile,
        int? season,
        DateTime? scoreAchievedAt,
        int soloRank,
        int? seasonRank,
        int? allTimeRank,
        int? difficulty)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO player_score_observations (
                account_id, song_id, instrument, score, accuracy, is_full_combo, stars,
                difficulty, season, score_achieved_at, source_kind, source_id, source_scope,
                solo_rank, season_rank, all_time_rank, solo_percentile, observed_at)
            VALUES (
                @accountId, @songId, @instrument, @score, @accuracy, @isFullCombo, @stars,
                @difficulty, @season, @scoreAchievedAt, 'solo-history', @sourceId, @sourceScope,
                @soloRank, @seasonRank, @allTimeRank, @soloPercentile, @observedAt)
            ON CONFLICT (account_id, song_id, instrument, source_kind, source_id) DO UPDATE SET
                score = EXCLUDED.score,
                accuracy = COALESCE(EXCLUDED.accuracy, player_score_observations.accuracy),
                is_full_combo = COALESCE(EXCLUDED.is_full_combo, player_score_observations.is_full_combo),
                stars = COALESCE(EXCLUDED.stars, player_score_observations.stars),
                difficulty = COALESCE(EXCLUDED.difficulty, player_score_observations.difficulty),
                season = COALESCE(EXCLUDED.season, player_score_observations.season),
                score_achieved_at = COALESCE(EXCLUDED.score_achieved_at, player_score_observations.score_achieved_at),
                source_scope = COALESCE(NULLIF(EXCLUDED.source_scope, ''), player_score_observations.source_scope),
                solo_rank = COALESCE(EXCLUDED.solo_rank, player_score_observations.solo_rank),
                season_rank = COALESCE(EXCLUDED.season_rank, player_score_observations.season_rank),
                all_time_rank = COALESCE(EXCLUDED.all_time_rank, player_score_observations.all_time_rank),
                solo_percentile = COALESCE(EXCLUDED.solo_percentile, player_score_observations.solo_percentile),
                observed_at = GREATEST(player_score_observations.observed_at, EXCLUDED.observed_at)
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("accuracy", (object?)accuracy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("isFullCombo", (object?)isFullCombo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("stars", (object?)stars ?? DBNull.Value);
        cmd.Parameters.AddWithValue("difficulty", (object?)difficulty ?? DBNull.Value);
        cmd.Parameters.AddWithValue("season", (object?)season ?? DBNull.Value);
        cmd.Parameters.AddWithValue("scoreAchievedAt", scoreAchievedAt.HasValue ? scoreAchievedAt.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("sourceId", BuildSoloObservationSourceId(accountId, songId, instrument, score, scoreAchievedAt, difficulty, season));
        cmd.Parameters.AddWithValue("sourceScope", season.HasValue ? $"season:{season.Value}" : "alltime");
        cmd.Parameters.AddWithValue("soloRank", soloRank > 0 ? soloRank : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("seasonRank", (object?)seasonRank ?? DBNull.Value);
        cmd.Parameters.AddWithValue("allTimeRank", (object?)allTimeRank ?? DBNull.Value);
        cmd.Parameters.AddWithValue("soloPercentile", (object?)percentile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("observedAt", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private static void CreateScoreObservationStaging(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        IReadOnlyList<ScoreChangeRecord> changes,
        DateTime observedAt)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DROP TABLE IF EXISTS _pso_solo_staging";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TEMP TABLE _pso_solo_staging (
                    song_id TEXT, instrument TEXT, account_id TEXT, new_score INTEGER,
                    new_rank INTEGER, accuracy INTEGER, is_full_combo BOOLEAN, stars INTEGER,
                    percentile DOUBLE PRECISION, season INTEGER, score_achieved_at TIMESTAMPTZ,
                    season_rank INTEGER, all_time_rank INTEGER, difficulty INTEGER, changed_at TIMESTAMPTZ
                ) ON COMMIT DROP
                """;
            cmd.ExecuteNonQuery();
        }

        using var writer = conn.BeginBinaryImport(
            "COPY _pso_solo_staging (song_id, instrument, account_id, new_score, new_rank, accuracy, " +
            "is_full_combo, stars, percentile, season, score_achieved_at, season_rank, all_time_rank, " +
            "difficulty, changed_at) FROM STDIN (FORMAT BINARY)");
        foreach (var change in changes)
        {
            writer.StartRow();
            writer.Write(change.SongId, NpgsqlDbType.Text);
            writer.Write(change.Instrument, NpgsqlDbType.Text);
            writer.Write(change.AccountId, NpgsqlDbType.Text);
            writer.Write(change.NewScore, NpgsqlDbType.Integer);
            writer.Write(change.NewRank, NpgsqlDbType.Integer);
            WriteNullableInt(writer, change.Accuracy);
            if (change.IsFullCombo.HasValue) writer.Write(change.IsFullCombo.Value, NpgsqlDbType.Boolean);
            else writer.WriteNull();
            WriteNullableInt(writer, change.Stars);
            if (change.Percentile.HasValue) writer.Write(change.Percentile.Value, NpgsqlDbType.Double);
            else writer.WriteNull();
            WriteNullableInt(writer, change.Season);
            if (change.ScoreAchievedAt is not null) writer.Write(ParseUtc(change.ScoreAchievedAt), NpgsqlDbType.TimestampTz);
            else writer.WriteNull();
            WriteNullableInt(writer, change.SeasonRank);
            WriteNullableInt(writer, change.AllTimeRank);
            WriteNullableInt(writer, change.Difficulty);
            writer.Write(observedAt, NpgsqlDbType.TimestampTz);
        }
        writer.Complete();
    }

    private static void UpsertSoloScoreObservationsFromStaging(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string stagingTable)
    {
        if (stagingTable is not "_sh_staging" and not "_pso_solo_staging")
            throw new ArgumentOutOfRangeException(nameof(stagingTable));

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $$"""
            WITH source_rows AS (
                SELECT DISTINCT ON (account_id, song_id, instrument, source_id)
                    account_id,
                    song_id,
                    instrument,
                    new_score,
                    accuracy,
                    is_full_combo,
                    stars,
                    difficulty,
                    season,
                    score_achieved_at,
                    source_id,
                    CASE WHEN season IS NOT NULL THEN 'season:' || season::TEXT ELSE 'alltime' END AS source_scope,
                    NULLIF(new_rank, 0) AS solo_rank,
                    season_rank,
                    all_time_rank,
                    percentile,
                    changed_at
                FROM (
                    SELECT *,
                        CONCAT_WS(':',
                            'solo-history', account_id, song_id, instrument, new_score::TEXT,
                            COALESCE(TO_CHAR(score_achieved_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'), 'no-time'),
                            COALESCE(difficulty::TEXT, 'no-difficulty'),
                            COALESCE(season::TEXT, 'no-season')) AS source_id
                    FROM {{stagingTable}}
                ) staged
                ORDER BY account_id, song_id, instrument, source_id, changed_at DESC
            )
            INSERT INTO player_score_observations (
                account_id, song_id, instrument, score, accuracy, is_full_combo, stars,
                difficulty, season, score_achieved_at, source_kind, source_id, source_scope,
                solo_rank, season_rank, all_time_rank, solo_percentile, observed_at)
            SELECT
                account_id,
                song_id,
                instrument,
                new_score,
                accuracy,
                is_full_combo,
                stars,
                difficulty,
                season,
                score_achieved_at,
                'solo-history',
                source_id,
                source_scope,
                solo_rank,
                season_rank,
                all_time_rank,
                percentile,
                changed_at
            FROM source_rows
            ON CONFLICT (account_id, song_id, instrument, source_kind, source_id) DO UPDATE SET
                score = EXCLUDED.score,
                accuracy = COALESCE(EXCLUDED.accuracy, player_score_observations.accuracy),
                is_full_combo = COALESCE(EXCLUDED.is_full_combo, player_score_observations.is_full_combo),
                stars = COALESCE(EXCLUDED.stars, player_score_observations.stars),
                difficulty = COALESCE(EXCLUDED.difficulty, player_score_observations.difficulty),
                season = COALESCE(EXCLUDED.season, player_score_observations.season),
                score_achieved_at = COALESCE(EXCLUDED.score_achieved_at, player_score_observations.score_achieved_at),
                source_scope = COALESCE(NULLIF(EXCLUDED.source_scope, ''), player_score_observations.source_scope),
                solo_rank = COALESCE(EXCLUDED.solo_rank, player_score_observations.solo_rank),
                season_rank = COALESCE(EXCLUDED.season_rank, player_score_observations.season_rank),
                all_time_rank = COALESCE(EXCLUDED.all_time_rank, player_score_observations.all_time_rank),
                solo_percentile = COALESCE(EXCLUDED.solo_percentile, player_score_observations.solo_percentile),
                observed_at = GREATEST(player_score_observations.observed_at, EXCLUDED.observed_at)
            """;
        cmd.ExecuteNonQuery();
    }

    private static string BuildSoloObservationSourceId(
        string accountId,
        string songId,
        string instrument,
        int score,
        DateTime? scoreAchievedAt,
        int? difficulty,
        int? season)
    {
        var achievedAt = scoreAchievedAt.HasValue ? scoreAchievedAt.Value.ToString("O") : "no-time";
        return $"solo-history:{accountId}:{songId}:{instrument}:{score}:{achievedAt}:{difficulty?.ToString() ?? "no-difficulty"}:{season?.ToString() ?? "no-season"}";
    }

    public List<ScoreHistoryEntry> GetScoreHistory(string accountId, int limit = 100, string? songId = null, string? instrument = null)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        var where = "WHERE account_id = @accountId";
        cmd.Parameters.AddWithValue("accountId", accountId);
        if (songId is not null) { where += " AND song_id = @songId"; cmd.Parameters.AddWithValue("songId", songId); }
        if (instrument is not null) { where += " AND instrument = @instrument"; cmd.Parameters.AddWithValue("instrument", instrument); }
        cmd.CommandText = $"SELECT song_id, instrument, old_score, new_score, old_rank, new_rank, accuracy, is_full_combo, stars, percentile, season, score_achieved_at, changed_at, season_rank, all_time_rank, difficulty FROM score_history {where} ORDER BY id DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("limit", limit);
        var list = new List<ScoreHistoryEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ScoreHistoryEntry
            {
                SongId = r.GetString(0),
                Instrument = r.GetString(1),
                OldScore = r.IsDBNull(2) ? null : r.GetInt32(2),
                NewScore = r.GetInt32(3),
                OldRank = r.IsDBNull(4) ? null : r.GetInt32(4),
                NewRank = r.GetInt32(5),
                Accuracy = r.IsDBNull(6) ? null : r.GetInt32(6),
                IsFullCombo = r.IsDBNull(7) ? null : r.GetBoolean(7),
                Stars = r.IsDBNull(8) ? null : r.GetInt32(8),
                Percentile = r.IsDBNull(9) ? null : r.GetDouble(9),
                Season = r.IsDBNull(10) ? null : r.GetInt32(10),
                ScoreAchievedAt = r.IsDBNull(11) ? null : r.GetDateTime(11).ToString("o"),
                ChangedAt = r.GetDateTime(12).ToString("o"),
                SeasonRank = r.IsDBNull(13) ? null : r.GetInt32(13),
                AllTimeRank = r.IsDBNull(14) ? null : r.GetInt32(14),
                Difficulty = r.IsDBNull(15) ? null : r.GetInt32(15),
            });
        }
        return list;
    }

    public Dictionary<(string SongId, string Instrument), ValidScoreFallback> GetBestValidScores(string accountId, Dictionary<(string SongId, string Instrument), int> thresholds)
    {
        if (thresholds.Count == 0) return new();
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "CREATE TEMP TABLE _valid_thresholds (song_id TEXT, instrument TEXT, max_score INTEGER, PRIMARY KEY (song_id, instrument)) ON COMMIT DROP"; c.ExecuteNonQuery(); }
        using (var writer = conn.BeginBinaryImport("COPY _valid_thresholds (song_id, instrument, max_score) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var ((songId, instrument), maxScore) in thresholds)
            {
                writer.StartRow();
                writer.Write(songId, NpgsqlDbType.Text);
                writer.Write(instrument, NpgsqlDbType.Text);
                writer.Write(maxScore, NpgsqlDbType.Integer);
            }

            writer.Complete();
        }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "ANALYZE _valid_thresholds"; c.ExecuteNonQuery(); }
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT sh.song_id, sh.instrument, sh.new_score, sh.accuracy, sh.is_full_combo, sh.stars FROM score_history sh JOIN _valid_thresholds vt ON vt.song_id = sh.song_id AND vt.instrument = sh.instrument WHERE sh.account_id = @accountId AND sh.new_score <= vt.max_score AND sh.new_score = (SELECT MAX(sh2.new_score) FROM score_history sh2 WHERE sh2.account_id = @accountId AND sh2.song_id = sh.song_id AND sh2.instrument = sh.instrument AND sh2.new_score <= vt.max_score) GROUP BY sh.song_id, sh.instrument, sh.new_score, sh.accuracy, sh.is_full_combo, sh.stars";
        cmd.Parameters.AddWithValue("accountId", accountId);
        var result = new Dictionary<(string, string), ValidScoreFallback>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) result[(r.GetString(0), r.GetString(1))] = new ValidScoreFallback { Score = r.GetInt32(2), Accuracy = r.IsDBNull(3) ? null : r.GetInt32(3), IsFullCombo = r.IsDBNull(4) ? null : r.GetBoolean(4), Stars = r.IsDBNull(5) ? null : r.GetInt32(5) };
        }
        tx.Commit();
        return result;
    }

    public Dictionary<(string AccountId, string SongId), ValidScoreFallback> GetBulkBestValidScores(string instrument, Dictionary<(string AccountId, string SongId), int> entries)
    {
        if (entries.Count == 0) return new();
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "CREATE TEMP TABLE _bulk_thresholds (account_id TEXT, song_id TEXT, max_score INTEGER, PRIMARY KEY (account_id, song_id)) ON COMMIT DROP"; c.ExecuteNonQuery(); }
        using (var writer = conn.BeginBinaryImport("COPY _bulk_thresholds (account_id, song_id, max_score) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var ((accountId, songId), maxScore) in entries)
            {
                writer.StartRow();
                writer.Write(accountId, NpgsqlDbType.Text);
                writer.Write(songId, NpgsqlDbType.Text);
                writer.Write(maxScore, NpgsqlDbType.Integer);
            }

            writer.Complete();
        }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "ANALYZE _bulk_thresholds"; c.ExecuteNonQuery(); }
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT sh.account_id, sh.song_id, sh.new_score, sh.accuracy, sh.is_full_combo, sh.stars FROM score_history sh JOIN _bulk_thresholds bt ON bt.account_id = sh.account_id AND bt.song_id = sh.song_id WHERE sh.instrument = @instrument AND sh.new_score <= bt.max_score AND sh.new_score = (SELECT MAX(sh2.new_score) FROM score_history sh2 WHERE sh2.account_id = sh.account_id AND sh2.song_id = sh.song_id AND sh2.instrument = @instrument AND sh2.new_score <= bt.max_score) GROUP BY sh.account_id, sh.song_id, sh.new_score, sh.accuracy, sh.is_full_combo, sh.stars";
        cmd.Parameters.AddWithValue("instrument", instrument);
        var result = new Dictionary<(string, string), ValidScoreFallback>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) result[(r.GetString(0), r.GetString(1))] = new ValidScoreFallback { Score = r.GetInt32(2), Accuracy = r.IsDBNull(3) ? null : r.GetInt32(3), IsFullCombo = r.IsDBNull(4) ? null : r.GetBoolean(4), Stars = r.IsDBNull(5) ? null : r.GetInt32(5) };
        }
        tx.Commit();
        return result;
    }

    public Dictionary<(string SongId, string Instrument), List<ValidScoreFallback>> GetAllValidScoreTiers(
        string accountId, Dictionary<(string SongId, string Instrument), int> maxThresholds)
    {
        if (maxThresholds.Count == 0) return new();
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "CREATE TEMP TABLE _tier_thresholds (song_id TEXT, instrument TEXT, max_score INTEGER, PRIMARY KEY (song_id, instrument)) ON COMMIT DROP"; c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "INSERT INTO _tier_thresholds VALUES (@s, @i, @m)";
            var ps = c.Parameters.Add("s", NpgsqlTypes.NpgsqlDbType.Text);
            var pi = c.Parameters.Add("i", NpgsqlTypes.NpgsqlDbType.Text);
            var pm = c.Parameters.Add("m", NpgsqlTypes.NpgsqlDbType.Integer);
            c.Prepare();
            foreach (var ((s, i), m) in maxThresholds) { ps.Value = s; pi.Value = i; pm.Value = m; c.ExecuteNonQuery(); }
        }
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT sh.song_id, sh.instrument, sh.new_score, MAX(sh.accuracy), MAX(CASE WHEN sh.is_full_combo THEN 1 ELSE 0 END)::BOOLEAN, MAX(sh.stars) FROM score_history sh JOIN _tier_thresholds tt ON tt.song_id = sh.song_id AND tt.instrument = sh.instrument WHERE sh.account_id = @accountId AND sh.new_score <= tt.max_score GROUP BY sh.song_id, sh.instrument, sh.new_score ORDER BY sh.song_id, sh.instrument, sh.new_score DESC";
        cmd.Parameters.AddWithValue("accountId", accountId);
        var result = new Dictionary<(string, string), List<ValidScoreFallback>>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var key = (r.GetString(0), r.GetString(1));
                if (!result.TryGetValue(key, out var list)) { list = new List<ValidScoreFallback>(); result[key] = list; }
                list.Add(new ValidScoreFallback { Score = r.GetInt32(2), Accuracy = r.IsDBNull(3) ? null : r.GetInt32(3), IsFullCombo = r.IsDBNull(4) ? null : r.GetBoolean(4), Stars = r.IsDBNull(5) ? null : r.GetInt32(5) });
            }
        }
        tx.Commit();
        return result;
    }

    public Dictionary<(string SongId, string Instrument), string> GetLastPlayedDates(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT song_id, instrument, MAX(score_achieved_at) FROM score_history WHERE account_id = @accountId AND score_achieved_at IS NOT NULL GROUP BY song_id, instrument";
        cmd.Parameters.AddWithValue("accountId", accountId);
        var result = new Dictionary<(string, string), string>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var ts = r.GetDateTime(2).ToString("O");
            result[(r.GetString(0), r.GetString(1))] = ts;
        }
        return result;
    }

    public Dictionary<(string SongId, string Instrument), string> GetLastPlayedDates(
        string accountId, Dictionary<(string SongId, string Instrument), int> maxThresholds)
    {
        if (maxThresholds.Count == 0) return new();
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "CREATE TEMP TABLE _lp_thresholds (song_id TEXT, instrument TEXT, max_score INTEGER, PRIMARY KEY (song_id, instrument)) ON COMMIT DROP"; c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "INSERT INTO _lp_thresholds VALUES (@s, @i, @m)";
            var ps = c.Parameters.Add("s", NpgsqlTypes.NpgsqlDbType.Text);
            var pi = c.Parameters.Add("i", NpgsqlTypes.NpgsqlDbType.Text);
            var pm = c.Parameters.Add("m", NpgsqlTypes.NpgsqlDbType.Integer);
            c.Prepare();
            foreach (var ((s, i), m) in maxThresholds) { ps.Value = s; pi.Value = i; pm.Value = m; c.ExecuteNonQuery(); }
        }
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT sh.song_id, sh.instrument, MAX(sh.score_achieved_at) FROM score_history sh JOIN _lp_thresholds lt ON lt.song_id = sh.song_id AND lt.instrument = sh.instrument WHERE sh.account_id = @accountId AND sh.new_score <= lt.max_score AND sh.score_achieved_at IS NOT NULL GROUP BY sh.song_id, sh.instrument";
        cmd.Parameters.AddWithValue("accountId", accountId);
        var result = new Dictionary<(string, string), string>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var ts = r.GetDateTime(2).ToString("O");
                result[(r.GetString(0), r.GetString(1))] = ts;
            }
        }
        tx.Commit();
        return result;
    }

    // ── Account names ────────────────────────────────────────────────

    public int InsertAccountIds(IEnumerable<string> accountIds)
    {
        var idList = accountIds as IList<string> ?? accountIds.ToList();
        if (idList.Count == 0) return 0;

        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        // COPY + merge for larger batches
        if (idList.Count > 50)
        {
            using (var c = conn.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "CREATE TEMP TABLE _acct_staging (account_id TEXT) ON COMMIT DROP";
                c.ExecuteNonQuery();
            }

            using (var writer = conn.BeginBinaryImport("COPY _acct_staging (account_id) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var id in idList)
                {
                    writer.StartRow();
                    writer.Write(id, NpgsqlDbType.Text);
                }
                writer.Complete();
            }

            int inserted;
            using (var c = conn.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandTimeout = 0;
                c.CommandText = "INSERT INTO account_names (account_id) SELECT account_id FROM _acct_staging ON CONFLICT DO NOTHING";
                inserted = c.ExecuteNonQuery();
            }
            tx.Commit();
            return inserted;
        }

        // Small batch: prepared-statement loop
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO account_names (account_id) VALUES (@id) ON CONFLICT DO NOTHING";
        var pId = cmd.Parameters.Add("id", NpgsqlDbType.Text); cmd.Prepare();
        int loopInserted = 0;
        foreach (var id in idList) { pId.Value = id; loopInserted += cmd.ExecuteNonQuery(); }
        tx.Commit();
        return loopInserted;
    }

    public List<string> GetUnresolvedAccountIds() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id FROM account_names WHERE last_resolved IS NULL"; var ids = new List<string>(); using var r = cmd.ExecuteReader(); while (r.Read()) ids.Add(r.GetString(0)); return ids; }
    public int GetUnresolvedAccountCount() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM account_names WHERE last_resolved IS NULL"; return Convert.ToInt32(cmd.ExecuteScalar()); }

    public int InsertAccountNames(IReadOnlyList<(string AccountId, string? DisplayName)> accounts)
    {
        if (accounts.Count == 0) return 0;
        var now = DateTime.UtcNow;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO account_names (account_id, display_name, last_resolved) VALUES (@id, @name, @now) ON CONFLICT(account_id) DO UPDATE SET display_name = EXCLUDED.display_name, last_resolved = EXCLUDED.last_resolved";
        var pId = cmd.Parameters.Add("id", NpgsqlTypes.NpgsqlDbType.Text); var pName = cmd.Parameters.Add("name", NpgsqlTypes.NpgsqlDbType.Text); var pNow = cmd.Parameters.Add("now", NpgsqlTypes.NpgsqlDbType.TimestampTz); cmd.Prepare();
        int inserted = 0;
        foreach (var (accountId, displayName) in accounts) { pId.Value = accountId; pName.Value = displayName is not null ? displayName : DBNull.Value; pNow.Value = now; inserted += cmd.ExecuteNonQuery(); }
        tx.Commit();
        return inserted;
    }

    public string? GetDisplayName(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT display_name FROM account_names WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); var result = cmd.ExecuteScalar(); return result is DBNull or null ? null : (string)result; }
    public List<(string AccountId, string DisplayName)> SearchAccountNames(string query, int limit = 10)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        var normalizedQuery = query.Trim().ToLowerInvariant();
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return list;

        if (normalizedQuery.Length <= 2 && TryGetExclusiveUpperBound(normalizedQuery, out var upperBound))
        {
            cmd.CommandTimeout = 2;
            cmd.CommandText = @"
                SELECT account_id, display_name
                FROM account_names
                WHERE display_name IS NOT NULL
                  AND LOWER(display_name) >= @prefix
                  AND LOWER(display_name) < @upperBound
                ORDER BY LOWER(display_name), display_name
                LIMIT @limit";
            cmd.Parameters.AddWithValue("prefix", normalizedQuery);
            cmd.Parameters.AddWithValue("upperBound", upperBound);
            cmd.Parameters.AddWithValue("limit", limit);

            try
            {
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add((r.GetString(0), r.GetString(1)));
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                _log.LogWarning(ex, "Account prefix search timed out for query length {Length}; returning an empty fast-fail result.", normalizedQuery.Length);
            }

            return list;
        }

        var escapedQuery = EscapeLikePattern(normalizedQuery);
        cmd.CommandTimeout = 2;
        cmd.CommandText = "SELECT account_id, display_name FROM account_names WHERE display_name IS NOT NULL AND LOWER(display_name) LIKE @pattern ESCAPE '!' ORDER BY CASE WHEN LOWER(display_name) LIKE @prefix ESCAPE '!' THEN 0 ELSE 1 END, LENGTH(display_name), display_name LIMIT @limit";
        cmd.Parameters.AddWithValue("pattern", $"%{escapedQuery}%");
        cmd.Parameters.AddWithValue("prefix", $"{escapedQuery}%");
        cmd.Parameters.AddWithValue("limit", limit);
        try
        {
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetString(0), r.GetString(1)));
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _log.LogWarning(ex, "Account search timed out for query length {Length}; returning an empty fast-fail result.", normalizedQuery.Length);
        }
        return list;
    }

    private static bool TryGetExclusiveUpperBound(string prefix, out string upperBound)
    {
        var chars = prefix.ToCharArray();
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            if (chars[i] == char.MaxValue)
                continue;

            chars[i]++;
            upperBound = new string(chars, 0, i + 1);
            return true;
        }

        upperBound = string.Empty;
        return false;
    }

    private static string EscapeLikePattern(string value) => value
        .Replace("!", "!!", StringComparison.Ordinal)
        .Replace("%", "!%", StringComparison.Ordinal)
        .Replace("_", "!_", StringComparison.Ordinal);

    public Dictionary<string, string> GetDisplayNames(IEnumerable<string> accountIds)
    {
        var idList = accountIds as IList<string> ?? accountIds.ToList();
        if (idList.Count == 0) return new();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in idList.Chunk(500))
        {
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            var paramNames = new string[batch.Length];
            for (int i = 0; i < batch.Length; i++) { paramNames[i] = $"@id{i}"; cmd.Parameters.AddWithValue($"id{i}", batch[i]); }
            cmd.CommandText = $"SELECT account_id, display_name FROM account_names WHERE display_name IS NOT NULL AND account_id IN ({string.Join(',', paramNames)})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result[r.GetString(0)] = r.GetString(1);
        }
        return result;
    }

    // ── Registered users ─────────────────────────────────────────────

    public HashSet<string> GetRegisteredAccountIds() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT DISTINCT account_id FROM registered_users"; var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) ids.Add(r.GetString(0)); return ids; }
    public bool IsAccountRegistered(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM registered_users WHERE account_id = @accountId)"; cmd.Parameters.AddWithValue("accountId", accountId); return Convert.ToBoolean(cmd.ExecuteScalar() ?? false); }
    public bool RegisterUser(string deviceId, string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO registered_users (device_id, account_id, registered_at, last_activity_at) VALUES (@deviceId, @accountId, @now, @now) ON CONFLICT DO NOTHING";
        cmd.Parameters.AddWithValue("deviceId", deviceId);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        return cmd.ExecuteNonQuery() > 0;
    }
    public bool UnregisterUser(string deviceId, string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var delCmd = conn.CreateCommand();
        delCmd.Transaction = tx;
        delCmd.CommandText = "DELETE FROM registered_users WHERE device_id = @deviceId AND account_id = @accountId";
        delCmd.Parameters.AddWithValue("deviceId", deviceId);
        delCmd.Parameters.AddWithValue("accountId", accountId);
        bool removed = delCmd.ExecuteNonQuery() > 0;
        if (removed)
        {
            using var chk = conn.CreateCommand();
            chk.Transaction = tx;
            chk.CommandText = "SELECT COUNT(*) FROM registered_users WHERE account_id = @accountId";
            chk.Parameters.AddWithValue("accountId", accountId);
            int remaining = Convert.ToInt32(chk.ExecuteScalar());
            if (remaining == 0)
            {
                // Cascade-delete all per-account data (account_id column)
                foreach (var t in new[] { "player_stats", "player_stats_tiers", "backfill_status", "backfill_progress", "history_recon_status", "history_recon_progress", "rivals_status", "rivals_dirty_songs", "rival_song_fingerprints", "rival_instrument_state" })
                { using var c = conn.CreateCommand(); c.Transaction = tx; c.CommandText = $"DELETE FROM {t} WHERE account_id = @id"; c.Parameters.AddWithValue("id", accountId); c.ExecuteNonQuery(); }
                // Rivals tables use user_id column
                foreach (var t in new[] { "user_rivals", "rival_song_samples" })
                { using var c = conn.CreateCommand(); c.Transaction = tx; c.CommandText = $"DELETE FROM {t} WHERE user_id = @id"; c.Parameters.AddWithValue("id", accountId); c.ExecuteNonQuery(); }
            }
        }
        tx.Commit();
        return removed;
    }

    public void TouchWebRegistrationActivity(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE registered_users SET last_activity_at = @now WHERE device_id = @deviceId AND account_id = @accountId";
        cmd.Parameters.AddWithValue("deviceId", WebTrackerDeviceId);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    public SelectedBandRegistrationResult RegisterSelectedBandActivity(string bandType, string teamKey, string? bandId = null)
    {
        var normalizedBandType = bandType.Trim();
        var normalizedTeamKey = teamKey.Trim();
        if (string.IsNullOrWhiteSpace(normalizedBandType) || string.IsNullOrWhiteSpace(normalizedTeamKey))
            return new SelectedBandRegistrationResult(false, string.Empty, []);

        var canonicalBandId = BandIdentity.CreateBandId(normalizedBandType, normalizedTeamKey);
        if (!string.IsNullOrWhiteSpace(bandId)
            && !string.Equals(bandId.Trim(), canonicalBandId, StringComparison.OrdinalIgnoreCase))
        {
            return new SelectedBandRegistrationResult(false, canonicalBandId, []);
        }

        using var conn = _ds.OpenConnection();
        var memberAccountIds = GetBandMemberAccountIds(conn, normalizedBandType, normalizedTeamKey);
        if (memberAccountIds.Count == 0)
            return new SelectedBandRegistrationResult(false, canonicalBandId, []);

        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow;

        using (var bandCmd = conn.CreateCommand())
        {
            bandCmd.Transaction = tx;
            bandCmd.CommandText = """
                INSERT INTO registered_bands (source_id, band_type, team_key, band_id, registered_at, last_activity_at, last_member_sync_at)
                VALUES (@sourceId, @bandType, @teamKey, @bandId, @now, @now, @now)
                ON CONFLICT (source_id, band_type, team_key)
                DO UPDATE SET band_id = EXCLUDED.band_id,
                              last_activity_at = EXCLUDED.last_activity_at,
                              last_member_sync_at = EXCLUDED.last_member_sync_at
                """;
            bandCmd.Parameters.AddWithValue("sourceId", WebBandTrackerDeviceId);
            bandCmd.Parameters.AddWithValue("bandType", normalizedBandType);
            bandCmd.Parameters.AddWithValue("teamKey", normalizedTeamKey);
            bandCmd.Parameters.AddWithValue("bandId", canonicalBandId);
            bandCmd.Parameters.AddWithValue("now", now);
            bandCmd.ExecuteNonQuery();
        }

        if (TableExists(conn, tx, BandIdentityPersistence.TableName))
        {
            using var identityCmd = conn.CreateCommand();
            identityCmd.Transaction = tx;
            identityCmd.CommandText = """
                INSERT INTO band_identity (band_id, band_type, team_key, member_account_ids, appearance_count, first_seen_at, last_seen_at, updated_at, source)
                VALUES (@bandId, @bandType, @teamKey, @memberAccountIds, 0, @now, @now, @now, 'registered_bands')
                ON CONFLICT (band_id) DO UPDATE SET
                    band_type = EXCLUDED.band_type,
                    team_key = EXCLUDED.team_key,
                    member_account_ids = EXCLUDED.member_account_ids,
                    last_seen_at = COALESCE(GREATEST(band_identity.last_seen_at, EXCLUDED.last_seen_at), band_identity.last_seen_at, EXCLUDED.last_seen_at),
                    updated_at = EXCLUDED.updated_at,
                    source = EXCLUDED.source
                """;
            identityCmd.Parameters.AddWithValue("bandId", canonicalBandId);
            identityCmd.Parameters.AddWithValue("bandType", normalizedBandType);
            identityCmd.Parameters.AddWithValue("teamKey", normalizedTeamKey);
            identityCmd.Parameters.Add("memberAccountIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = memberAccountIds.ToArray();
            identityCmd.Parameters.AddWithValue("now", now);
            identityCmd.ExecuteNonQuery();
        }

        using (var membersCmd = conn.CreateCommand())
        {
            membersCmd.Transaction = tx;
            membersCmd.CommandText = """
                INSERT INTO registered_users (device_id, account_id, registered_at, last_activity_at)
                SELECT device_id, account_id, @now, @now
                FROM unnest(@memberAccountIds::text[]) AS selected_member(account_id)
                CROSS JOIN unnest(@deviceIds::text[]) AS selected_device(device_id)
                ON CONFLICT (device_id, account_id)
                DO UPDATE SET last_activity_at = EXCLUDED.last_activity_at
                """;
            membersCmd.Parameters.Add("deviceIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = new[]
            {
                WebBandTrackerDeviceId,
                WebTrackerDeviceId,
            };
            membersCmd.Parameters.AddWithValue("now", now);
            membersCmd.Parameters.Add("memberAccountIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = memberAccountIds.ToArray();
            membersCmd.ExecuteNonQuery();
        }

        using (var backfillCmd = conn.CreateCommand())
        {
            backfillCmd.Transaction = tx;
            backfillCmd.CommandText = """
                INSERT INTO backfill_status (account_id, status, total_songs_to_check)
                SELECT account_id, 'pending', 0
                FROM unnest(@memberAccountIds::text[]) AS selected_member(account_id)
                ON CONFLICT (account_id) DO UPDATE SET
                    status = CASE
                        WHEN backfill_status.status = 'complete' THEN backfill_status.status
                        ELSE 'pending'
                    END,
                    total_songs_to_check = CASE
                        WHEN backfill_status.status = 'complete' THEN backfill_status.total_songs_to_check
                        ELSE GREATEST(backfill_status.total_songs_to_check, EXCLUDED.total_songs_to_check)
                    END
                WHERE backfill_status.status != 'complete'
                """;
            backfillCmd.Parameters.Add("memberAccountIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = memberAccountIds.ToArray();
            backfillCmd.ExecuteNonQuery();
        }

        using (var processingCmd = conn.CreateCommand())
        {
            processingCmd.Transaction = tx;
            processingCmd.CommandText = """
                INSERT INTO registered_band_processing_status (source_id, band_type, team_key, status, total_lookups_to_check)
                VALUES (@sourceId, @bandType, @teamKey, 'pending', 0)
                ON CONFLICT (source_id, band_type, team_key) DO NOTHING
                """;
            processingCmd.Parameters.AddWithValue("sourceId", WebBandTrackerDeviceId);
            processingCmd.Parameters.AddWithValue("bandType", normalizedBandType);
            processingCmd.Parameters.AddWithValue("teamKey", normalizedTeamKey);
            processingCmd.ExecuteNonQuery();
        }

        tx.Commit();
        return new SelectedBandRegistrationResult(true, canonicalBandId, memberAccountIds);
    }

    public int RegisterKnownBandsForAccountActivity(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return 0;

        var normalizedAccountId = accountId.Trim();
        using var conn = _ds.OpenConnection();

        var knownBands = new List<(string BandType, string TeamKey)>();
        using (var lookupCmd = conn.CreateCommand())
        {
            lookupCmd.CommandText = """
                SELECT DISTINCT band_type, team_key
                FROM (
                    SELECT band_type, team_key
                    FROM band_team_membership
                    WHERE account_id = @accountId
                    UNION
                    SELECT band_type, team_key
                    FROM band_members
                    WHERE account_id = @accountId
                    UNION
                    SELECT band_type, team_key
                    FROM band_search_team_projection
                    WHERE @accountId = ANY(member_account_ids)
                ) AS known_band
                ORDER BY band_type, team_key
                """;
            lookupCmd.Parameters.AddWithValue("accountId", normalizedAccountId);

            using var reader = lookupCmd.ExecuteReader();
            while (reader.Read())
            {
                var bandType = reader.GetString(0);
                var teamKey = reader.GetString(1);
                var memberAccountIds = teamKey.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!memberAccountIds.Contains(normalizedAccountId, StringComparer.OrdinalIgnoreCase))
                    continue;

                knownBands.Add((bandType, teamKey));
            }
        }

        if (knownBands.Count == 0)
            return 0;

        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow;
        var registered = 0;

        foreach (var (bandType, teamKey) in knownBands)
        {
            var memberAccountIds = teamKey.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var bandId = BandIdentity.CreateBandId(bandType, teamKey);

            using (var bandCmd = conn.CreateCommand())
            {
                bandCmd.Transaction = tx;
                bandCmd.CommandText = """
                    INSERT INTO registered_bands (source_id, band_type, team_key, band_id, registered_at, last_activity_at, last_member_sync_at)
                    VALUES (@sourceId, @bandType, @teamKey, @bandId, @now, @now, @now)
                    ON CONFLICT (source_id, band_type, team_key)
                    DO UPDATE SET band_id = EXCLUDED.band_id,
                                  last_activity_at = EXCLUDED.last_activity_at,
                                  last_member_sync_at = EXCLUDED.last_member_sync_at
                    """;
                bandCmd.Parameters.AddWithValue("sourceId", WebBandTrackerDeviceId);
                bandCmd.Parameters.AddWithValue("bandType", bandType);
                bandCmd.Parameters.AddWithValue("teamKey", teamKey);
                bandCmd.Parameters.AddWithValue("bandId", bandId);
                bandCmd.Parameters.AddWithValue("now", now);
                bandCmd.ExecuteNonQuery();
            }

            if (TableExists(conn, tx, BandIdentityPersistence.TableName))
            {
                using var identityCmd = conn.CreateCommand();
                identityCmd.Transaction = tx;
                identityCmd.CommandText = """
                    INSERT INTO band_identity (band_id, band_type, team_key, member_account_ids, appearance_count, first_seen_at, last_seen_at, updated_at, source)
                    VALUES (@bandId, @bandType, @teamKey, @memberAccountIds, 0, @now, @now, @now, 'registered_player_bands')
                    ON CONFLICT (band_id) DO UPDATE SET
                        band_type = EXCLUDED.band_type,
                        team_key = EXCLUDED.team_key,
                        member_account_ids = EXCLUDED.member_account_ids,
                        last_seen_at = COALESCE(GREATEST(band_identity.last_seen_at, EXCLUDED.last_seen_at), band_identity.last_seen_at, EXCLUDED.last_seen_at),
                        updated_at = EXCLUDED.updated_at,
                        source = EXCLUDED.source
                    """;
                identityCmd.Parameters.AddWithValue("bandId", bandId);
                identityCmd.Parameters.AddWithValue("bandType", bandType);
                identityCmd.Parameters.AddWithValue("teamKey", teamKey);
                identityCmd.Parameters.Add("memberAccountIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = memberAccountIds;
                identityCmd.Parameters.AddWithValue("now", now);
                identityCmd.ExecuteNonQuery();
            }

            using (var processingCmd = conn.CreateCommand())
            {
                processingCmd.Transaction = tx;
                processingCmd.CommandText = """
                    INSERT INTO registered_band_processing_status (source_id, band_type, team_key, status, total_lookups_to_check)
                    VALUES (@sourceId, @bandType, @teamKey, 'pending', 0)
                    ON CONFLICT (source_id, band_type, team_key) DO NOTHING
                    """;
                processingCmd.Parameters.AddWithValue("sourceId", WebBandTrackerDeviceId);
                processingCmd.Parameters.AddWithValue("bandType", bandType);
                processingCmd.Parameters.AddWithValue("teamKey", teamKey);
                processingCmd.ExecuteNonQuery();
            }

            registered++;
        }

        tx.Commit();
        return registered;
    }

    public void RegisterDiscoveredBandActivity(string bandType, string teamKey, IReadOnlyList<string> memberAccountIds)
    {
        var normalizedBandType = bandType.Trim();
        var normalizedTeamKey = teamKey.Trim();
        if (string.IsNullOrWhiteSpace(normalizedBandType) || string.IsNullOrWhiteSpace(normalizedTeamKey) || memberAccountIds.Count == 0)
            return;

        var members = memberAccountIds
            .Where(static accountId => !string.IsNullOrWhiteSpace(accountId))
            .Select(static accountId => accountId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (members.Length == 0)
            return;

        var bandId = BandIdentity.CreateBandId(normalizedBandType, normalizedTeamKey);
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow;

        using (var bandCmd = conn.CreateCommand())
        {
            bandCmd.Transaction = tx;
            bandCmd.CommandText = """
                INSERT INTO registered_bands (source_id, band_type, team_key, band_id, registered_at, last_activity_at, last_member_sync_at)
                VALUES (@sourceId, @bandType, @teamKey, @bandId, @now, @now, @now)
                ON CONFLICT (source_id, band_type, team_key)
                DO UPDATE SET band_id = EXCLUDED.band_id,
                              last_activity_at = EXCLUDED.last_activity_at,
                              last_member_sync_at = EXCLUDED.last_member_sync_at
                """;
            bandCmd.Parameters.AddWithValue("sourceId", WebBandTrackerDeviceId);
            bandCmd.Parameters.AddWithValue("bandType", normalizedBandType);
            bandCmd.Parameters.AddWithValue("teamKey", normalizedTeamKey);
            bandCmd.Parameters.AddWithValue("bandId", bandId);
            bandCmd.Parameters.AddWithValue("now", now);
            bandCmd.ExecuteNonQuery();
        }

        if (TableExists(conn, tx, BandIdentityPersistence.TableName))
        {
            using var identityCmd = conn.CreateCommand();
            identityCmd.Transaction = tx;
            identityCmd.CommandText = """
                INSERT INTO band_identity (band_id, band_type, team_key, member_account_ids, appearance_count, first_seen_at, last_seen_at, updated_at, source)
                VALUES (@bandId, @bandType, @teamKey, @memberAccountIds, 0, @now, @now, @now, 'registered_player_band_discovery')
                ON CONFLICT (band_id) DO UPDATE SET
                    band_type = EXCLUDED.band_type,
                    team_key = EXCLUDED.team_key,
                    member_account_ids = EXCLUDED.member_account_ids,
                    last_seen_at = COALESCE(GREATEST(band_identity.last_seen_at, EXCLUDED.last_seen_at), band_identity.last_seen_at, EXCLUDED.last_seen_at),
                    updated_at = EXCLUDED.updated_at,
                    source = EXCLUDED.source
                """;
            identityCmd.Parameters.AddWithValue("bandId", bandId);
            identityCmd.Parameters.AddWithValue("bandType", normalizedBandType);
            identityCmd.Parameters.AddWithValue("teamKey", normalizedTeamKey);
            identityCmd.Parameters.Add("memberAccountIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = members;
            identityCmd.Parameters.AddWithValue("now", now);
            identityCmd.ExecuteNonQuery();
        }

        using (var processingCmd = conn.CreateCommand())
        {
            processingCmd.Transaction = tx;
            processingCmd.CommandText = """
                INSERT INTO registered_band_processing_status (source_id, band_type, team_key, status, total_lookups_to_check)
                VALUES (@sourceId, @bandType, @teamKey, 'pending', 0)
                ON CONFLICT (source_id, band_type, team_key) DO NOTHING
                """;
            processingCmd.Parameters.AddWithValue("sourceId", WebBandTrackerDeviceId);
            processingCmd.Parameters.AddWithValue("bandType", normalizedBandType);
            processingCmd.Parameters.AddWithValue("teamKey", normalizedTeamKey);
            processingCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public List<RegisteredBandInfo> GetRegisteredBands()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT source_id, band_type, team_key, band_id, registered_at, last_activity_at, last_member_sync_at
            FROM registered_bands
            ORDER BY source_id, band_type, team_key
            """;
        var bands = new List<RegisteredBandInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            bands.Add(new RegisteredBandInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6)));
        }
        return bands;
    }

    public void EnsureRegisteredBandProcessingStatus(string sourceId, string bandType, string teamKey, int totalLookupsToCheck)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO registered_band_processing_status (source_id, band_type, team_key, status, total_lookups_to_check)
            VALUES (@sourceId, @bandType, @teamKey, 'pending', @total)
            ON CONFLICT (source_id, band_type, team_key) DO UPDATE SET
                total_lookups_to_check = GREATEST(registered_band_processing_status.total_lookups_to_check, EXCLUDED.total_lookups_to_check),
                status = CASE
                    WHEN registered_band_processing_status.status = 'complete'
                         AND registered_band_processing_status.total_lookups_to_check >= EXCLUDED.total_lookups_to_check
                    THEN registered_band_processing_status.status
                    ELSE 'pending'
                END,
                completed_at = CASE
                    WHEN registered_band_processing_status.status = 'complete'
                         AND registered_band_processing_status.total_lookups_to_check >= EXCLUDED.total_lookups_to_check
                    THEN registered_band_processing_status.completed_at
                    ELSE NULL
                END,
                error_message = CASE
                    WHEN registered_band_processing_status.status = 'complete'
                         AND registered_band_processing_status.total_lookups_to_check >= EXCLUDED.total_lookups_to_check
                    THEN registered_band_processing_status.error_message
                    ELSE NULL
                END
            """;
        cmd.Parameters.AddWithValue("sourceId", sourceId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("total", totalLookupsToCheck);
        cmd.ExecuteNonQuery();
    }

    public RegisteredBandProcessingStatusInfo? GetRegisteredBandProcessingStatus(string sourceId, string bandType, string teamKey)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT source_id, band_type, team_key, status, lookups_checked, entries_found,
                   total_lookups_to_check, started_at, completed_at, last_resumed_at, error_message
            FROM registered_band_processing_status
            WHERE source_id = @sourceId AND band_type = @bandType AND team_key = @teamKey
            """;
        cmd.Parameters.AddWithValue("sourceId", sourceId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRegisteredBandProcessingStatus(reader) : null;
    }

    public void StartRegisteredBandProcessing(string sourceId, string bandType, string teamKey)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE registered_band_processing_status
            SET status = 'in_progress',
                started_at = COALESCE(started_at, @now),
                last_resumed_at = @now,
                error_message = NULL
            WHERE source_id = @sourceId AND band_type = @bandType AND team_key = @teamKey
            """;
        AddRegisteredBandKeyParameters(cmd, sourceId, bandType, teamKey);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    public void CompleteRegisteredBandProcessing(string sourceId, string bandType, string teamKey, int lookupsChecked, int entriesFound)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE registered_band_processing_status
            SET status = 'complete', lookups_checked = @checked, entries_found = @found,
                completed_at = @now, error_message = NULL
            WHERE source_id = @sourceId AND band_type = @bandType AND team_key = @teamKey
            """;
        AddRegisteredBandKeyParameters(cmd, sourceId, bandType, teamKey);
        cmd.Parameters.AddWithValue("checked", lookupsChecked);
        cmd.Parameters.AddWithValue("found", entriesFound);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    public void FailRegisteredBandProcessing(string sourceId, string bandType, string teamKey, string errorMessage)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE registered_band_processing_status
            SET status = 'error', error_message = @err, completed_at = @now
            WHERE source_id = @sourceId AND band_type = @bandType AND team_key = @teamKey
            """;
        AddRegisteredBandKeyParameters(cmd, sourceId, bandType, teamKey);
        cmd.Parameters.AddWithValue("err", errorMessage);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    public void UpdateRegisteredBandProcessingProgress(string sourceId, string bandType, string teamKey, int lookupsChecked, int entriesFound, int totalLookupsToCheck)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE registered_band_processing_status
            SET lookups_checked = @checked,
                entries_found = @found,
                total_lookups_to_check = GREATEST(total_lookups_to_check, @total)
            WHERE source_id = @sourceId AND band_type = @bandType AND team_key = @teamKey
            """;
        AddRegisteredBandKeyParameters(cmd, sourceId, bandType, teamKey);
        cmd.Parameters.AddWithValue("checked", lookupsChecked);
        cmd.Parameters.AddWithValue("found", entriesFound);
        cmd.Parameters.AddWithValue("total", totalLookupsToCheck);
        cmd.ExecuteNonQuery();
    }

    public void MarkRegisteredBandLookupChecked(string sourceId, string bandType, string teamKey, string songId, string scope, int season, bool entryFound)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO registered_band_processing_progress
                (source_id, band_type, team_key, song_id, scope, season, checked, entry_found, checked_at)
            VALUES (@sourceId, @bandType, @teamKey, @songId, @scope, @season, 1, @found, @now)
            ON CONFLICT (source_id, band_type, team_key, song_id, scope, season) DO UPDATE SET
                checked = 1,
                entry_found = EXCLUDED.entry_found,
                checked_at = EXCLUDED.checked_at
            """;
        AddRegisteredBandKeyParameters(cmd, sourceId, bandType, teamKey);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("scope", scope);
        cmd.Parameters.AddWithValue("season", season);
        cmd.Parameters.AddWithValue("found", entryFound ? 1 : 0);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    public List<RegisteredBandLookupProgressInfo> GetCheckedRegisteredBandLookups(string sourceId, string bandType, string teamKey)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT song_id, scope, season, entry_found
            FROM registered_band_processing_progress
            WHERE source_id = @sourceId AND band_type = @bandType AND team_key = @teamKey AND checked = 1
            """;
        AddRegisteredBandKeyParameters(cmd, sourceId, bandType, teamKey);
        var rows = new List<RegisteredBandLookupProgressInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new RegisteredBandLookupProgressInfo(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3) != 0));
        return rows;
    }

    public void MarkRegisteredPlayerBandDiscoveryChecked(string accountId, string songId, string bandType, string scope, int season, bool entryFound)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO registered_player_band_discovery_progress
                (account_id, song_id, band_type, scope, season, checked, entry_found, checked_at)
            VALUES (@accountId, @songId, @bandType, @scope, @season, 1, @found, @now)
            ON CONFLICT (account_id, song_id, band_type, scope, season) DO UPDATE SET
                checked = 1,
                entry_found = EXCLUDED.entry_found,
                checked_at = EXCLUDED.checked_at
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", scope);
        cmd.Parameters.AddWithValue("season", season);
        cmd.Parameters.AddWithValue("found", entryFound ? 1 : 0);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    public List<RegisteredPlayerBandDiscoveryProgressInfo> GetCheckedRegisteredPlayerBandDiscoveryLookups(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT song_id, band_type, scope, season, entry_found
            FROM registered_player_band_discovery_progress
            WHERE account_id = @accountId AND checked = 1
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        var rows = new List<RegisteredPlayerBandDiscoveryProgressInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new RegisteredPlayerBandDiscoveryProgressInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4) != 0));
        }
        return rows;
    }

    public int PruneStaleWebRegistrations(DateTime staleBeforeUtc)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        int prunedUsers;
        using (var usersCmd = conn.CreateCommand())
        {
            usersCmd.Transaction = tx;
            usersCmd.CommandText = """
                DELETE FROM registered_users
                WHERE device_id IN (@webTrackerDeviceId, @webBandTrackerDeviceId)
                  AND COALESCE(last_activity_at, registered_at) < @staleBeforeUtc
                """;
            usersCmd.Parameters.AddWithValue("webTrackerDeviceId", WebTrackerDeviceId);
            usersCmd.Parameters.AddWithValue("webBandTrackerDeviceId", WebBandTrackerDeviceId);
            usersCmd.Parameters.AddWithValue("staleBeforeUtc", staleBeforeUtc);
            prunedUsers = usersCmd.ExecuteNonQuery();
        }

        int prunedBands;
        using (var bandsCmd = conn.CreateCommand())
        {
            bandsCmd.Transaction = tx;
            bandsCmd.CommandText = """
                DELETE FROM registered_bands
                WHERE source_id = @sourceId
                  AND COALESCE(last_activity_at, registered_at) < @staleBeforeUtc
                """;
            bandsCmd.Parameters.AddWithValue("sourceId", WebBandTrackerDeviceId);
            bandsCmd.Parameters.AddWithValue("staleBeforeUtc", staleBeforeUtc);
            prunedBands = bandsCmd.ExecuteNonQuery();
        }

        using (var progressCleanupCmd = conn.CreateCommand())
        {
            progressCleanupCmd.Transaction = tx;
            progressCleanupCmd.CommandText = """
                DELETE FROM registered_band_processing_progress p
                WHERE NOT EXISTS (
                    SELECT 1 FROM registered_bands b
                    WHERE b.source_id = p.source_id AND b.band_type = p.band_type AND b.team_key = p.team_key
                )
                """;
            progressCleanupCmd.ExecuteNonQuery();
        }

        using (var statusCleanupCmd = conn.CreateCommand())
        {
            statusCleanupCmd.Transaction = tx;
            statusCleanupCmd.CommandText = """
                DELETE FROM registered_band_processing_status s
                WHERE NOT EXISTS (
                    SELECT 1 FROM registered_bands b
                    WHERE b.source_id = s.source_id AND b.band_type = s.band_type AND b.team_key = s.team_key
                )
                """;
            statusCleanupCmd.ExecuteNonQuery();
        }

        tx.Commit();
        return prunedUsers + prunedBands;
    }

    private static void AddRegisteredBandKeyParameters(NpgsqlCommand cmd, string sourceId, string bandType, string teamKey)
    {
        cmd.Parameters.AddWithValue("sourceId", sourceId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
    }

    private static RegisteredBandProcessingStatusInfo ReadRegisteredBandProcessingStatus(NpgsqlDataReader reader) => new()
    {
        SourceId = reader.GetString(0),
        BandType = reader.GetString(1),
        TeamKey = reader.GetString(2),
        Status = reader.GetString(3),
        LookupsChecked = reader.GetInt32(4),
        EntriesFound = reader.GetInt32(5),
        TotalLookupsToCheck = reader.GetInt32(6),
        StartedAt = reader.IsDBNull(7) ? null : reader.GetDateTime(7).ToString("o"),
        CompletedAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8).ToString("o"),
        LastResumedAt = reader.IsDBNull(9) ? null : reader.GetDateTime(9).ToString("o"),
        ErrorMessage = reader.IsDBNull(10) ? null : reader.GetString(10),
    };

    private static List<string> GetBandMemberAccountIds(NpgsqlConnection conn, string bandType, string teamKey)
    {
        using (var projectionCmd = conn.CreateCommand())
        {
            projectionCmd.CommandText = """
                SELECT member_account_ids
                FROM band_search_team_projection
                WHERE band_type = @bandType AND team_key = @teamKey
                LIMIT 1
                """;
            projectionCmd.Parameters.AddWithValue("bandType", bandType);
            projectionCmd.Parameters.AddWithValue("teamKey", teamKey);
            using var projectionReader = projectionCmd.ExecuteReader();
            if (projectionReader.Read())
            {
                var memberAccountIds = projectionReader.GetFieldValue<string[]>(0)
                    .Where(static accountId => !string.IsNullOrWhiteSpace(accountId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (memberAccountIds.Count > 0) return memberAccountIds;
            }
        }

        using var membersCmd = conn.CreateCommand();
        membersCmd.CommandText = """
            SELECT DISTINCT account_id
            FROM (
                SELECT account_id FROM band_team_membership WHERE band_type = @bandType AND team_key = @teamKey
                UNION
                SELECT account_id FROM band_members WHERE band_type = @bandType AND team_key = @teamKey
            ) AS members
            ORDER BY account_id
            """;
        membersCmd.Parameters.AddWithValue("bandType", bandType);
        membersCmd.Parameters.AddWithValue("teamKey", teamKey);
        var memberIds = new List<string>();
        using var membersReader = membersCmd.ExecuteReader();
        while (membersReader.Read())
            memberIds.Add(membersReader.GetString(0));
        return memberIds;
    }

    public string? GetAccountIdForUsername(string username) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id FROM account_names WHERE LOWER(display_name) = LOWER(@username) LIMIT 1"; cmd.Parameters.AddWithValue("username", username); var result = cmd.ExecuteScalar(); return result is DBNull or null ? null : (string)result; }

    // ── Backfill ─────────────────────────────────────────────────────

    private const string BackfillStatusColumns = "account_id, status, songs_checked, entries_found, total_songs_to_check, started_at, completed_at, last_resumed_at, error_message, rankings_pending, deferred_reason";

    public void EnqueueBackfill(string accountId, int totalSongsToCheck) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO backfill_status (account_id, status, total_songs_to_check, rankings_pending, deferred_reason) VALUES (@id, 'pending', @total, FALSE, NULL) ON CONFLICT(account_id) DO UPDATE SET status = CASE WHEN backfill_status.status = 'complete' THEN backfill_status.status ELSE 'pending' END, total_songs_to_check = EXCLUDED.total_songs_to_check, rankings_pending = CASE WHEN backfill_status.status = 'complete' THEN backfill_status.rankings_pending ELSE FALSE END, deferred_reason = CASE WHEN backfill_status.status = 'complete' THEN backfill_status.deferred_reason ELSE NULL END WHERE backfill_status.status != 'complete'"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("total", totalSongsToCheck); cmd.ExecuteNonQuery(); }
    public void DeferBackfill(string accountId, int totalSongsToCheck, string reason) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO backfill_status (account_id, status, total_songs_to_check, rankings_pending, deferred_reason) VALUES (@id, 'deferred', @total, FALSE, @reason) ON CONFLICT(account_id) DO UPDATE SET status = CASE WHEN backfill_status.status = 'complete' THEN backfill_status.status ELSE 'deferred' END, total_songs_to_check = EXCLUDED.total_songs_to_check, rankings_pending = CASE WHEN backfill_status.status = 'complete' THEN backfill_status.rankings_pending ELSE FALSE END, deferred_reason = CASE WHEN backfill_status.status = 'complete' THEN backfill_status.deferred_reason ELSE EXCLUDED.deferred_reason END WHERE backfill_status.status != 'complete'"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("total", totalSongsToCheck); cmd.Parameters.AddWithValue("reason", reason); cmd.ExecuteNonQuery(); }
    public List<BackfillStatusInfo> GetPendingBackfills() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = $"SELECT {BackfillStatusColumns} FROM backfill_status WHERE status IN ('pending', 'in_progress')"; var list = new List<BackfillStatusInfo>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(ReadBackfillStatus(r)); return list; }
    public List<BackfillStatusInfo> GetDeferredBackfills() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = $"SELECT {BackfillStatusColumns} FROM backfill_status WHERE status = 'deferred'"; var list = new List<BackfillStatusInfo>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(ReadBackfillStatus(r)); return list; }
    public BackfillStatusInfo? GetBackfillStatus(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = $"SELECT {BackfillStatusColumns} FROM backfill_status WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); using var r = cmd.ExecuteReader(); return r.Read() ? ReadBackfillStatus(r) : null; }
    public void StartBackfill(string accountId) { SimpleUpdate("UPDATE backfill_status SET status = 'in_progress', started_at = COALESCE(started_at, @now), last_resumed_at = @now, deferred_reason = NULL WHERE account_id = @id", accountId); }
    public void CompleteBackfill(string accountId, bool rankingsPending = false) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE backfill_status SET status = 'complete', completed_at = @now, rankings_pending = @rankingsPending, deferred_reason = NULL WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.Parameters.AddWithValue("rankingsPending", rankingsPending); cmd.ExecuteNonQuery(); }
    public void ClearBackfillRankingsPending(IEnumerable<string> accountIds) { var ids = accountIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if (ids.Length == 0) return; using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE backfill_status SET rankings_pending = FALSE WHERE rankings_pending = TRUE AND account_id = ANY(@ids)"; cmd.Parameters.AddWithValue("ids", ids); cmd.ExecuteNonQuery(); }
    public void FailBackfill(string accountId, string errorMessage) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE backfill_status SET status = 'error', error_message = @err, deferred_reason = NULL WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("err", errorMessage); cmd.ExecuteNonQuery(); }
    public void UpdateBackfillProgress(string accountId, int songsChecked, int entriesFound) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE backfill_status SET songs_checked = @checked, entries_found = @found WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("checked", songsChecked); cmd.Parameters.AddWithValue("found", entriesFound); cmd.ExecuteNonQuery(); }
    public void MarkBackfillSongChecked(string accountId, string songId, string instrument, bool entryFound) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO backfill_progress (account_id, song_id, instrument, checked, entry_found, checked_at) VALUES (@acct, @song, @inst, 1, @found, @now) ON CONFLICT(account_id, song_id, instrument) DO UPDATE SET checked = 1, entry_found = EXCLUDED.entry_found, checked_at = EXCLUDED.checked_at"; cmd.Parameters.AddWithValue("acct", accountId); cmd.Parameters.AddWithValue("song", songId); cmd.Parameters.AddWithValue("inst", instrument); cmd.Parameters.AddWithValue("found", entryFound ? 1 : 0); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    public HashSet<(string SongId, string Instrument)> GetCheckedBackfillPairs(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id, instrument FROM backfill_progress WHERE account_id = @acct AND checked = 1"; cmd.Parameters.AddWithValue("acct", accountId); var set = new HashSet<(string, string)>(); using var r = cmd.ExecuteReader(); while (r.Read()) set.Add((r.GetString(0), r.GetString(1))); return set; }
    public BackfillSongProgressInfo? GetBackfillSongProgress(string accountId, int checkedPairs, int totalPairs)
    {
        var instrumentCount = Math.Max(1, GlobalLeaderboardScraper.AllInstruments.Count);
        var totalSongs = EstimateBackfillSongCount(totalPairs, instrumentCount, roundUp: true);
        if (totalSongs <= 0 && checkedPairs <= 0) return null;

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM (
                SELECT song_id
                FROM backfill_progress
                WHERE account_id = @acct AND checked = 1
                GROUP BY song_id
                HAVING COUNT(DISTINCT instrument) >= @instrumentCount
            ) completed_songs
            """;
        cmd.Parameters.AddWithValue("acct", accountId);
        cmd.Parameters.AddWithValue("instrumentCount", instrumentCount);
        var completedSongs = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        var estimatedCheckedSongs = EstimateBackfillSongCount(checkedPairs, instrumentCount, roundUp: false);
        var songsChecked = Math.Max(completedSongs, estimatedCheckedSongs);
        if (totalSongs <= 0) totalSongs = songsChecked;

        return new BackfillSongProgressInfo
        {
            SongsChecked = Math.Min(songsChecked, totalSongs),
            TotalSongs = totalSongs,
        };
    }

    // ── History reconstruction ───────────────────────────────────────

    public void EnqueueHistoryRecon(string accountId, int totalSongsToProcess) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO history_recon_status (account_id, status, total_songs_to_process) VALUES (@id, 'pending', @total) ON CONFLICT(account_id) DO UPDATE SET status = CASE WHEN history_recon_status.status = 'complete' THEN history_recon_status.status ELSE 'pending' END, total_songs_to_process = EXCLUDED.total_songs_to_process WHERE history_recon_status.status != 'complete'"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("total", totalSongsToProcess); cmd.ExecuteNonQuery(); }
    public List<HistoryReconStatusInfo> GetPendingHistoryRecons() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, status, songs_processed, total_songs_to_process, seasons_queried, history_entries_found, started_at, completed_at, error_message FROM history_recon_status WHERE status IN ('pending', 'in_progress')"; var list = new List<HistoryReconStatusInfo>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(ReadHistoryReconStatus(r)); return list; }
    public HistoryReconStatusInfo? GetHistoryReconStatus(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, status, songs_processed, total_songs_to_process, seasons_queried, history_entries_found, started_at, completed_at, error_message FROM history_recon_status WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); using var r = cmd.ExecuteReader(); return r.Read() ? ReadHistoryReconStatus(r) : null; }
    public void StartHistoryRecon(string accountId) { SimpleUpdate("UPDATE history_recon_status SET status = 'in_progress', started_at = COALESCE(started_at, @now) WHERE account_id = @id", accountId); }
    public void CompleteHistoryRecon(string accountId) { SimpleUpdate("UPDATE history_recon_status SET status = 'complete', completed_at = @now WHERE account_id = @id", accountId); }
    public void FailHistoryRecon(string accountId, string errorMessage) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE history_recon_status SET status = 'error', error_message = @err WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("err", errorMessage); cmd.ExecuteNonQuery(); }
    public void UpdateHistoryReconProgress(string accountId, int songsProcessed, int seasonsQueried, int historyEntriesFound) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE history_recon_status SET songs_processed = @songs, seasons_queried = @seasons, history_entries_found = @entries WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("songs", songsProcessed); cmd.Parameters.AddWithValue("seasons", seasonsQueried); cmd.Parameters.AddWithValue("entries", historyEntriesFound); cmd.ExecuteNonQuery(); }
    public void MarkHistoryReconSongProcessed(string accountId, string songId, string instrument) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO history_recon_progress (account_id, song_id, instrument, processed, processed_at) VALUES (@acct, @song, @inst, 1, @now) ON CONFLICT(account_id, song_id, instrument) DO UPDATE SET processed = 1, processed_at = EXCLUDED.processed_at"; cmd.Parameters.AddWithValue("acct", accountId); cmd.Parameters.AddWithValue("song", songId); cmd.Parameters.AddWithValue("inst", instrument); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    public HashSet<(string SongId, string Instrument)> GetProcessedHistoryReconPairs(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id, instrument FROM history_recon_progress WHERE account_id = @acct AND processed = 1"; cmd.Parameters.AddWithValue("acct", accountId); var set = new HashSet<(string, string)>(); using var r = cmd.ExecuteReader(); while (r.Read()) set.Add((r.GetString(0), r.GetString(1))); return set; }

    // ── Season windows ───────────────────────────────────────────────

    public void UpsertSeasonWindow(int seasonNumber, string eventId, string windowId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO season_windows (season_number, event_id, window_id, discovered_at) VALUES (@season, @eventId, @windowId, @now) ON CONFLICT(season_number) DO UPDATE SET event_id = EXCLUDED.event_id, window_id = EXCLUDED.window_id"; cmd.Parameters.AddWithValue("season", seasonNumber); cmd.Parameters.AddWithValue("eventId", eventId); cmd.Parameters.AddWithValue("windowId", windowId); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    public List<SeasonWindowInfo> GetSeasonWindows() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT season_number, event_id, window_id, discovered_at FROM season_windows ORDER BY season_number"; var list = new List<SeasonWindowInfo>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(new SeasonWindowInfo { SeasonNumber = r.GetInt32(0), EventId = r.GetString(1), WindowId = r.GetString(2), DiscoveredAt = r.GetDateTime(3).ToString("o") }); return list; }
    public SeasonWindowInfo? GetSeasonWindow(int seasonNumber) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT season_number, event_id, window_id, discovered_at FROM season_windows WHERE season_number = @season"; cmd.Parameters.AddWithValue("season", seasonNumber); using var r = cmd.ExecuteReader(); if (!r.Read()) return null; return new SeasonWindowInfo { SeasonNumber = r.GetInt32(0), EventId = r.GetString(1), WindowId = r.GetString(2), DiscoveredAt = r.GetDateTime(3).ToString("o") }; }
    public int GetCurrentSeason() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT COALESCE(MAX(season_number), 0) FROM season_windows"; return Convert.ToInt32(cmd.ExecuteScalar()); }

    // ── Player stats ─────────────────────────────────────────────────

    public void UpsertPlayerStats(PlayerStatsDto stats) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO player_stats (account_id, instrument, songs_played, full_combo_count, gold_star_count, avg_accuracy, best_rank, best_rank_song_id, total_score, percentile_dist, avg_percentile, overall_percentile, updated_at) VALUES (@accountId, @instrument, @songsPlayed, @fcCount, @goldStars, @avgAcc, @bestRank, @bestRankSongId, @totalScore, @pctDist, @avgPct, @overallPct, @now) ON CONFLICT(account_id, instrument) DO UPDATE SET songs_played = EXCLUDED.songs_played, full_combo_count = EXCLUDED.full_combo_count, gold_star_count = EXCLUDED.gold_star_count, avg_accuracy = EXCLUDED.avg_accuracy, best_rank = EXCLUDED.best_rank, best_rank_song_id = EXCLUDED.best_rank_song_id, total_score = EXCLUDED.total_score, percentile_dist = EXCLUDED.percentile_dist, avg_percentile = EXCLUDED.avg_percentile, overall_percentile = EXCLUDED.overall_percentile, updated_at = EXCLUDED.updated_at"; cmd.Parameters.AddWithValue("accountId", stats.AccountId); cmd.Parameters.AddWithValue("instrument", stats.Instrument); cmd.Parameters.AddWithValue("songsPlayed", stats.SongsPlayed); cmd.Parameters.AddWithValue("fcCount", stats.FullComboCount); cmd.Parameters.AddWithValue("goldStars", stats.GoldStarCount); cmd.Parameters.AddWithValue("avgAcc", stats.AvgAccuracy); cmd.Parameters.AddWithValue("bestRank", stats.BestRank); cmd.Parameters.AddWithValue("bestRankSongId", (object?)stats.BestRankSongId ?? DBNull.Value); cmd.Parameters.AddWithValue("totalScore", stats.TotalScore); cmd.Parameters.AddWithValue("pctDist", (object?)stats.PercentileDist ?? DBNull.Value); cmd.Parameters.AddWithValue("avgPct", (object?)stats.AvgPercentile ?? DBNull.Value); cmd.Parameters.AddWithValue("overallPct", (object?)stats.OverallPercentile ?? DBNull.Value); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    public List<PlayerStatsDto> GetPlayerStats(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT instrument, songs_played, full_combo_count, gold_star_count, avg_accuracy, best_rank, best_rank_song_id, total_score, percentile_dist, avg_percentile, overall_percentile FROM player_stats WHERE account_id = @accountId"; cmd.Parameters.AddWithValue("accountId", accountId); var list = new List<PlayerStatsDto>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(new PlayerStatsDto { AccountId = accountId, Instrument = r.GetString(0), SongsPlayed = r.GetInt32(1), FullComboCount = r.GetInt32(2), GoldStarCount = r.GetInt32(3), AvgAccuracy = r.GetDouble(4), BestRank = r.GetInt32(5), BestRankSongId = r.IsDBNull(6) ? null : r.GetString(6), TotalScore = r.GetInt64(7), PercentileDist = r.IsDBNull(8) ? null : r.GetString(8), AvgPercentile = r.IsDBNull(9) ? null : r.GetString(9), OverallPercentile = r.IsDBNull(10) ? null : r.GetString(10) }); return list; }

    // ── Player stats tiers ───────────────────────────────────────────

    private const int PlayerStatsTiersCopyThreshold = 32;

    public void UpsertPlayerStatsTiers(string accountId, string instrument, string tiersJson)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO player_stats_tiers (account_id, instrument, tiers_json, updated_at) VALUES (@accountId, @instrument, @tiers::jsonb, @now) ON CONFLICT(account_id, instrument) DO UPDATE SET tiers_json = EXCLUDED.tiers_json, updated_at = EXCLUDED.updated_at";
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("tiers", tiersJson);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    public void UpsertPlayerStatsTiersBatch(IReadOnlyList<PlayerStatsTiersRow> rows)
    {
        if (rows.Count == 0) return;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        if (rows.Count >= PlayerStatsTiersCopyThreshold)
        {
            using (var createCmd = conn.CreateCommand())
            {
                createCmd.Transaction = tx;
                createCmd.CommandText = "CREATE TEMP TABLE _player_stats_tiers_staging (account_id TEXT NOT NULL, instrument TEXT NOT NULL, tiers_json TEXT NOT NULL, updated_at TIMESTAMPTZ NOT NULL) ON COMMIT DROP";
                createCmd.ExecuteNonQuery();
            }

            var copyNow = DateTime.UtcNow;
            using (var writer = conn.BeginBinaryImport("COPY _player_stats_tiers_staging (account_id, instrument, tiers_json, updated_at) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var row in rows)
                {
                    writer.StartRow();
                    writer.Write(row.AccountId, NpgsqlDbType.Text);
                    writer.Write(row.Instrument, NpgsqlDbType.Text);
                    writer.Write(row.TiersJson, NpgsqlDbType.Text);
                    writer.Write(copyNow, NpgsqlDbType.TimestampTz);
                }
                writer.Complete();
            }

            using (var mergeCmd = conn.CreateCommand())
            {
                mergeCmd.Transaction = tx;
                mergeCmd.CommandTimeout = 0;
                mergeCmd.CommandText = """
                    INSERT INTO player_stats_tiers (account_id, instrument, tiers_json, updated_at)
                    SELECT DISTINCT ON (account_id, instrument)
                           account_id,
                           instrument,
                           tiers_json::jsonb,
                           updated_at
                    FROM _player_stats_tiers_staging
                    ORDER BY account_id, instrument, updated_at DESC
                    ON CONFLICT(account_id, instrument) DO UPDATE SET
                        tiers_json = EXCLUDED.tiers_json,
                        updated_at = EXCLUDED.updated_at
                    """;
                mergeCmd.ExecuteNonQuery();
            }

            tx.Commit();
            return;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        cmd.CommandText = "INSERT INTO player_stats_tiers (account_id, instrument, tiers_json, updated_at) VALUES (@accountId, @instrument, @tiers::jsonb, @now) ON CONFLICT(account_id, instrument) DO UPDATE SET tiers_json = EXCLUDED.tiers_json, updated_at = EXCLUDED.updated_at";
        var pAcct = cmd.Parameters.Add("accountId", NpgsqlTypes.NpgsqlDbType.Text);
        var pInst = cmd.Parameters.Add("instrument", NpgsqlTypes.NpgsqlDbType.Text);
        var pTiers = cmd.Parameters.Add("tiers", NpgsqlTypes.NpgsqlDbType.Text);
        var pNow = cmd.Parameters.Add("now", NpgsqlTypes.NpgsqlDbType.TimestampTz);
        cmd.Prepare();
        var now = DateTime.UtcNow;
        foreach (var r in rows)
        {
            pAcct.Value = r.AccountId;
            pInst.Value = r.Instrument;
            pTiers.Value = r.TiersJson;
            pNow.Value = now;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<PlayerStatsTiersRow> GetPlayerStatsTiers(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT instrument, tiers_json, updated_at FROM player_stats_tiers WHERE account_id = @accountId";
        cmd.Parameters.AddWithValue("accountId", accountId);
        var list = new List<PlayerStatsTiersRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PlayerStatsTiersRow
            {
                AccountId = accountId,
                Instrument = r.GetString(0),
                TiersJson = r.GetString(1),
                UpdatedAt = r.GetDateTime(2).ToString("o"),
            });
        }
        return list;
    }

    // ── First seen season ────────────────────────────────────────────

    public HashSet<string> GetSongIdsWithFirstSeenVersion(int currentVersion) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id FROM song_first_seen_season WHERE calculation_version = @ver"; cmd.Parameters.AddWithValue("ver", currentVersion); var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) set.Add(r.GetString(0)); return set; }
    public void UpsertFirstSeenSeason(string songId, int? firstSeenSeason, int? minObservedSeason, int estimatedSeason, string? probeResult, int calculationVersion) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO song_first_seen_season (song_id, first_seen_season, min_observed_season, estimated_season, probe_result, calculated_at, calculation_version) VALUES (@songId, @firstSeen, @minObserved, @estimated, @probeResult, @now, @ver) ON CONFLICT(song_id) DO UPDATE SET first_seen_season = EXCLUDED.first_seen_season, min_observed_season = EXCLUDED.min_observed_season, estimated_season = EXCLUDED.estimated_season, probe_result = EXCLUDED.probe_result, calculated_at = EXCLUDED.calculated_at, calculation_version = EXCLUDED.calculation_version"; cmd.Parameters.AddWithValue("songId", songId); cmd.Parameters.AddWithValue("firstSeen", (object?)firstSeenSeason ?? DBNull.Value); cmd.Parameters.AddWithValue("minObserved", (object?)minObservedSeason ?? DBNull.Value); cmd.Parameters.AddWithValue("estimated", estimatedSeason); cmd.Parameters.AddWithValue("probeResult", (object?)probeResult ?? DBNull.Value); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.Parameters.AddWithValue("ver", calculationVersion); cmd.ExecuteNonQuery(); }
    public Dictionary<string, (int? FirstSeenSeason, int EstimatedSeason, int? CalculationVersion)> GetAllFirstSeenSeasons() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id, first_seen_season, estimated_season, calculation_version FROM song_first_seen_season"; var dict = new Dictionary<string, (int?, int, int?)>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) dict[r.GetString(0)] = (r.IsDBNull(1) ? null : r.GetInt32(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt32(3)); return dict; }

    // ── Leaderboard population ───────────────────────────────────────

    public void RaiseLeaderboardPopulationFloor(string songId, string instrument, long floor) { if (floor <= 0) return; using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO leaderboard_population (song_id, instrument, total_entries, updated_at) VALUES (@songId, @instrument, @floor, @now) ON CONFLICT (song_id, instrument) DO UPDATE SET total_entries = GREATEST(leaderboard_population.total_entries, EXCLUDED.total_entries), updated_at = CASE WHEN EXCLUDED.total_entries > leaderboard_population.total_entries THEN EXCLUDED.updated_at ELSE leaderboard_population.updated_at END"; cmd.Parameters.AddWithValue("songId", songId); cmd.Parameters.AddWithValue("instrument", instrument); cmd.Parameters.AddWithValue("floor", (int)floor); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }

    public void UpsertLeaderboardPopulation(IReadOnlyList<(string SongId, string Instrument, long TotalEntries)> items)
    {
        if (items.Count == 0) return;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO leaderboard_population (song_id, instrument, total_entries, updated_at) VALUES (@songId, @instrument, @totalEntries, @now) ON CONFLICT (song_id, instrument) DO UPDATE SET total_entries = EXCLUDED.total_entries, updated_at = EXCLUDED.updated_at";
        var pSong = cmd.Parameters.Add("songId", NpgsqlTypes.NpgsqlDbType.Text); var pInst = cmd.Parameters.Add("instrument", NpgsqlTypes.NpgsqlDbType.Text); var pTotal = cmd.Parameters.Add("totalEntries", NpgsqlTypes.NpgsqlDbType.Integer); var pNow = cmd.Parameters.Add("now", NpgsqlTypes.NpgsqlDbType.TimestampTz); cmd.Prepare();
        var now = DateTime.UtcNow;
        foreach (var (songId, instrument, totalEntries) in items) { pSong.Value = songId; pInst.Value = instrument; pTotal.Value = (int)totalEntries; pNow.Value = now; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    public long GetLeaderboardPopulation(string songId, string instrument) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT total_entries FROM leaderboard_population WHERE song_id = @s AND instrument = @i"; cmd.Parameters.AddWithValue("s", songId); cmd.Parameters.AddWithValue("i", instrument); var result = cmd.ExecuteScalar(); return result is DBNull or null ? -1 : Convert.ToInt64(result); }
    public Dictionary<(string SongId, string Instrument), long> GetAllLeaderboardPopulation() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id, instrument, total_entries FROM leaderboard_population"; var dict = new Dictionary<(string, string), long>(); using var r = cmd.ExecuteReader(); while (r.Read()) dict[(r.GetString(0), r.GetString(1))] = r.GetInt32(2); return dict; }

    // ── Rivals ───────────────────────────────────────────────────────

    public void EnsureRivalsStatus(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO rivals_status (account_id, status) VALUES (@id, 'pending') ON CONFLICT DO NOTHING"; cmd.Parameters.AddWithValue("id", accountId); cmd.ExecuteNonQuery(); }
    public void StartRivals(string accountId, int totalCombosToCompute = 0) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE rivals_status SET status = 'in_progress', started_at = @now, total_combos_to_compute = @total, combos_computed = 0, rivals_found = 0, error_message = NULL WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.Parameters.AddWithValue("total", totalCombosToCompute); cmd.ExecuteNonQuery(); }
    public void CompleteRivals(string accountId, int combosComputed, int rivalsFound) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE rivals_status SET status = 'complete', combos_computed = @combos, rivals_found = @rivals, algorithm_version = @version, completed_at = @now, error_message = NULL WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("combos", combosComputed); cmd.Parameters.AddWithValue("rivals", rivalsFound); cmd.Parameters.AddWithValue("version", RivalsAlgorithmVersion.SongRivals); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    public void FailRivals(string accountId, string errorMessage) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE rivals_status SET status = 'error', error_message = @err, completed_at = @now WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("err", errorMessage); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    public RivalsStatusInfo? GetRivalsStatus(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, status, combos_computed, total_combos_to_compute, rivals_found, algorithm_version, started_at, completed_at, error_message FROM rivals_status WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); using var r = cmd.ExecuteReader(); if (!r.Read()) return null; return new RivalsStatusInfo { AccountId = r.GetString(0), Status = r.GetString(1), CombosComputed = r.GetInt32(2), TotalCombosToCompute = r.GetInt32(3), RivalsFound = r.GetInt32(4), AlgorithmVersion = r.GetInt32(5), StartedAt = r.IsDBNull(6) ? null : r.GetDateTime(6).ToString("o"), CompletedAt = r.IsDBNull(7) ? null : r.GetDateTime(7).ToString("o"), ErrorMessage = r.IsDBNull(8) ? null : r.GetString(8) }; }
    public List<string> GetPendingRivalsAccounts() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id FROM rivals_status WHERE status IN ('pending', 'in_progress')"; var list = new List<string>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(r.GetString(0)); return list; }
    public int ResetStaleRivals() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE rivals_status SET status = 'pending', combos_computed = 0, rivals_found = 0, error_message = NULL WHERE status = 'complete' AND (rivals_found = 0 OR algorithm_version < @version)"; cmd.Parameters.AddWithValue("version", RivalsAlgorithmVersion.SongRivals); return cmd.ExecuteNonQuery(); }

    public void UpsertDirtyRivalSongs(IReadOnlyList<RivalDirtySongRow> dirtySongs)
    {
        if (dirtySongs.Count == 0)
            return;

        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO rivals_dirty_songs (account_id, instrument, song_id, dirty_reason, detected_at) VALUES (@accountId, @instrument, @songId, @reason, @detectedAt) ON CONFLICT (account_id, instrument, song_id) DO UPDATE SET dirty_reason = EXCLUDED.dirty_reason, detected_at = EXCLUDED.detected_at";
        var pAccountId = cmd.Parameters.Add("accountId", NpgsqlDbType.Text);
        var pInstrument = cmd.Parameters.Add("instrument", NpgsqlDbType.Text);
        var pSongId = cmd.Parameters.Add("songId", NpgsqlDbType.Text);
        var pReason = cmd.Parameters.Add("reason", NpgsqlDbType.Text);
        var pDetectedAt = cmd.Parameters.Add("detectedAt", NpgsqlDbType.TimestampTz);
        cmd.Prepare();

        foreach (var dirtySong in dirtySongs)
        {
            pAccountId.Value = dirtySong.AccountId;
            pInstrument.Value = dirtySong.Instrument;
            pSongId.Value = dirtySong.SongId;
            pReason.Value = dirtySong.DirtyReason;
            pDetectedAt.Value = ParseUtc(dirtySong.DetectedAt);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public List<string> GetDirtyRivalAccounts()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT account_id FROM rivals_dirty_songs ORDER BY account_id";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(r.GetString(0));
        return list;
    }

    public List<RivalDirtySongRow> GetDirtyRivalSongs(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id, instrument, song_id, dirty_reason, detected_at FROM rivals_dirty_songs WHERE account_id = @id ORDER BY instrument, song_id";
        cmd.Parameters.AddWithValue("id", accountId);
        var list = new List<RivalDirtySongRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new RivalDirtySongRow
            {
                AccountId = r.GetString(0),
                Instrument = r.GetString(1),
                SongId = r.GetString(2),
                DirtyReason = r.GetString(3),
                DetectedAt = r.GetDateTime(4).ToString("o"),
            });
        }

        return list;
    }

    public void ClearDirtyRivalSongs(string accountId, string instrument, IReadOnlyCollection<string> songIds)
    {
        if (songIds.Count == 0)
            return;

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM rivals_dirty_songs WHERE account_id = @id AND instrument = @instrument AND song_id = ANY(@songIds)";
        cmd.Parameters.AddWithValue("id", accountId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("songIds", songIds.ToArray());
        cmd.ExecuteNonQuery();
    }

    public void ClearAllDirtyRivalSongs(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM rivals_dirty_songs WHERE account_id = @id";
        cmd.Parameters.AddWithValue("id", accountId);
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, RivalSongFingerprintRow> GetRivalSongFingerprints(string accountId, string instrument, IReadOnlyCollection<string> songIds)
    {
        var dict = new Dictionary<string, RivalSongFingerprintRow>(StringComparer.OrdinalIgnoreCase);
        if (songIds.Count == 0)
            return dict;

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id, instrument, song_id, user_rank, neighborhood_signature, computed_at FROM rival_song_fingerprints WHERE account_id = @id AND instrument = @instrument AND song_id = ANY(@songIds)";
        cmd.Parameters.AddWithValue("id", accountId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("songIds", songIds.ToArray());
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            dict[r.GetString(2)] = new RivalSongFingerprintRow
            {
                AccountId = r.GetString(0),
                Instrument = r.GetString(1),
                SongId = r.GetString(2),
                UserRank = r.GetInt32(3),
                NeighborhoodSignature = r.GetString(4),
                ComputedAt = r.GetDateTime(5).ToString("o"),
            };
        }

        return dict;
    }

    public Dictionary<string, RivalInstrumentStateRow> GetRivalInstrumentStates(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id, instrument, song_count, is_eligible, computed_at FROM rival_instrument_state WHERE account_id = @id";
        cmd.Parameters.AddWithValue("id", accountId);
        var dict = new Dictionary<string, RivalInstrumentStateRow>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            dict[r.GetString(1)] = new RivalInstrumentStateRow
            {
                AccountId = r.GetString(0),
                Instrument = r.GetString(1),
                SongCount = r.GetInt32(2),
                IsEligible = r.GetBoolean(3),
                ComputedAt = r.GetDateTime(4).ToString("o"),
            };
        }

        return dict;
    }

    public void ReplaceRivalSelectionState(string accountId, IReadOnlyList<RivalSongFingerprintRow> fingerprints, IReadOnlyList<RivalInstrumentStateRow> instrumentStates)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "SET LOCAL synchronous_commit = off"; c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM rival_song_fingerprints WHERE account_id = @id"; c.Parameters.AddWithValue("id", accountId); c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM rival_instrument_state WHERE account_id = @id"; c.Parameters.AddWithValue("id", accountId); c.ExecuteNonQuery(); }

        if (fingerprints.Count > 0)
        {
            using var writer = conn.BeginBinaryImport(
                "COPY rival_song_fingerprints (account_id, instrument, song_id, user_rank, neighborhood_signature, computed_at) FROM STDIN (FORMAT BINARY)");
            foreach (var row in fingerprints)
            {
                writer.StartRow();
                writer.Write(row.AccountId, NpgsqlDbType.Text);
                writer.Write(row.Instrument, NpgsqlDbType.Text);
                writer.Write(row.SongId, NpgsqlDbType.Text);
                writer.Write(row.UserRank, NpgsqlDbType.Integer);
                writer.Write(row.NeighborhoodSignature, NpgsqlDbType.Text);
                writer.Write(ParseUtc(row.ComputedAt), NpgsqlDbType.TimestampTz);
            }

            writer.Complete();
        }

        if (instrumentStates.Count > 0)
        {
            using var writer = conn.BeginBinaryImport(
                "COPY rival_instrument_state (account_id, instrument, song_count, is_eligible, computed_at) FROM STDIN (FORMAT BINARY)");
            foreach (var row in instrumentStates)
            {
                writer.StartRow();
                writer.Write(row.AccountId, NpgsqlDbType.Text);
                writer.Write(row.Instrument, NpgsqlDbType.Text);
                writer.Write(row.SongCount, NpgsqlDbType.Integer);
                writer.Write(row.IsEligible, NpgsqlDbType.Boolean);
                writer.Write(ParseUtc(row.ComputedAt), NpgsqlDbType.TimestampTz);
            }

            writer.Complete();
        }

        tx.Commit();
    }

    public void ReplaceRivalsData(string userId, IReadOnlyList<UserRivalRow> rivals, IReadOnlyList<RivalSongSampleRow> samples)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "SET LOCAL synchronous_commit = off"; c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM rival_song_samples WHERE user_id = @uid"; c.Parameters.AddWithValue("uid", userId); c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM user_rivals WHERE user_id = @uid"; c.Parameters.AddWithValue("uid", userId); c.ExecuteNonQuery(); }
        if (rivals.Count > 0)
        {
            using var writer = conn.BeginBinaryImport(
                "COPY user_rivals (user_id, rival_account_id, instrument_combo, direction, rival_score, avg_signed_delta, shared_song_count, ahead_count, behind_count, computed_at) FROM STDIN (FORMAT BINARY)");
            foreach (var rv in rivals)
            {
                writer.StartRow();
                writer.Write(rv.UserId, NpgsqlDbType.Text);
                writer.Write(rv.RivalAccountId, NpgsqlDbType.Text);
                writer.Write(rv.InstrumentCombo, NpgsqlDbType.Text);
                writer.Write(rv.Direction, NpgsqlDbType.Text);
                writer.Write((float)rv.RivalScore, NpgsqlDbType.Real);
                writer.Write((float)rv.AvgSignedDelta, NpgsqlDbType.Real);
                writer.Write(rv.SharedSongCount, NpgsqlDbType.Integer);
                writer.Write(rv.AheadCount, NpgsqlDbType.Integer);
                writer.Write(rv.BehindCount, NpgsqlDbType.Integer);
                writer.Write(ParseUtc(rv.ComputedAt), NpgsqlDbType.TimestampTz);
            }

            writer.Complete();
        }
        if (samples.Count > 0)
        {
            using var writer = conn.BeginBinaryImport(
                "COPY rival_song_samples (user_id, rival_account_id, instrument, song_id, user_rank, rival_rank, rank_delta, user_score, rival_score) FROM STDIN (FORMAT BINARY)");
            foreach (var s in samples)
            {
                writer.StartRow();
                writer.Write(s.UserId, NpgsqlDbType.Text);
                writer.Write(s.RivalAccountId, NpgsqlDbType.Text);
                writer.Write(s.Instrument, NpgsqlDbType.Text);
                writer.Write(s.SongId, NpgsqlDbType.Text);
                writer.Write(s.UserRank, NpgsqlDbType.Integer);
                writer.Write(s.RivalRank, NpgsqlDbType.Integer);
                writer.Write(s.RankDelta, NpgsqlDbType.Integer);
                WriteNullableInt(writer, s.UserScore);
                WriteNullableInt(writer, s.RivalScore);
            }

            writer.Complete();
        }
        tx.Commit();
    }

    public List<UserRivalRow> GetUserRivals(string userId, string? instrumentCombo = null, string? direction = null)
    {
        var normalizedRequestedCombo = instrumentCombo is null
            ? null
            : ComboIds.NormalizeSupportedRivalComboParam(instrumentCombo);
        if (instrumentCombo is not null && normalizedRequestedCombo is null)
            return new List<UserRivalRow>();

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        var where = "WHERE user_id = @uid";
        cmd.Parameters.AddWithValue("uid", userId);
        if (direction is not null)
        {
            where += " AND direction = @dir";
            cmd.Parameters.AddWithValue("dir", direction);
        }

        cmd.CommandText = $"SELECT user_id, rival_account_id, instrument_combo, direction, rival_score, avg_signed_delta, shared_song_count, ahead_count, behind_count, computed_at FROM user_rivals {where} ORDER BY rival_score DESC";

        var list = new List<UserRivalRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var rawCombo = r.GetString(2);
            var normalizedStoredCombo = ComboIds.NormalizeSupportedRivalComboParam(rawCombo);
            if (normalizedStoredCombo is null)
                continue;
            if (normalizedRequestedCombo is not null && !normalizedStoredCombo.Equals(normalizedRequestedCombo, StringComparison.OrdinalIgnoreCase))
                continue;

            list.Add(new UserRivalRow
            {
                UserId = r.GetString(0),
                RivalAccountId = r.GetString(1),
                InstrumentCombo = rawCombo,
                Direction = r.GetString(3),
                RivalScore = r.GetDouble(4),
                AvgSignedDelta = r.GetDouble(5),
                SharedSongCount = r.GetInt32(6),
                AheadCount = r.GetInt32(7),
                BehindCount = r.GetInt32(8),
                ComputedAt = r.GetDateTime(9).ToString("o"),
            });
        }

        return list;
    }

    public List<RivalComboSummary> GetRivalCombos(string userId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT instrument_combo, SUM(CASE WHEN direction = 'above' THEN 1 ELSE 0 END), SUM(CASE WHEN direction = 'below' THEN 1 ELSE 0 END) FROM user_rivals WHERE user_id = @uid GROUP BY instrument_combo ORDER BY instrument_combo";
        cmd.Parameters.AddWithValue("uid", userId);

        var list = new List<RivalComboSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var combo = r.GetString(0);
            if (ComboIds.NormalizeSupportedRivalComboParam(combo) is null)
                continue;

            list.Add(new RivalComboSummary
            {
                InstrumentCombo = combo,
                AboveCount = (int)r.GetInt64(1),
                BelowCount = (int)r.GetInt64(2),
            });
        }

        return list;
    }
    public List<RivalSongSampleRow> GetRivalSongSamples(string userId, string rivalAccountId, string? instrument = null) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); var where = "WHERE user_id = @uid AND rival_account_id = @rid"; cmd.Parameters.AddWithValue("uid", userId); cmd.Parameters.AddWithValue("rid", rivalAccountId); if (instrument is not null) { where += " AND instrument = @inst"; cmd.Parameters.AddWithValue("inst", instrument); } cmd.CommandText = $"SELECT user_id, rival_account_id, instrument, song_id, user_rank, rival_rank, rank_delta, user_score, rival_score FROM rival_song_samples {where} ORDER BY ABS(rank_delta) ASC"; return ReadRivalSamples(cmd); }
    public Dictionary<string, List<RivalSongSampleRow>> GetAllRivalSongSamplesForUser(string userId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT user_id, rival_account_id, instrument, song_id, user_rank, rival_rank, rank_delta, user_score, rival_score FROM rival_song_samples WHERE user_id = @uid ORDER BY rival_account_id, ABS(rank_delta) ASC"; cmd.Parameters.AddWithValue("uid", userId); var dict = new Dictionary<string, List<RivalSongSampleRow>>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) { var sample = ReadRivalSample(r); if (!dict.TryGetValue(sample.RivalAccountId, out var list)) { list = new(); dict[sample.RivalAccountId] = list; } list.Add(sample); } return dict; }

    // ── Leaderboard Rivals ───────────────────────────────────────────

    public void ReplaceLeaderboardRivalsData(string userId, string instrument,
        IReadOnlyList<LeaderboardRivalRow> rivals, IReadOnlyList<LeaderboardRivalSongSampleRow> samples)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "SET LOCAL synchronous_commit = off"; c.ExecuteNonQuery(); }

        // Delete existing rivals + samples for this user/instrument
        using (var d = conn.CreateCommand()) { d.Transaction = tx; d.CommandText = "DELETE FROM leaderboard_rival_song_samples WHERE user_id = @uid AND instrument = @inst"; d.Parameters.AddWithValue("uid", userId); d.Parameters.AddWithValue("inst", instrument); d.ExecuteNonQuery(); }
        using (var d = conn.CreateCommand()) { d.Transaction = tx; d.CommandText = "DELETE FROM leaderboard_rivals WHERE user_id = @uid AND instrument = @inst"; d.Parameters.AddWithValue("uid", userId); d.Parameters.AddWithValue("inst", instrument); d.ExecuteNonQuery(); }

        // Insert rivals
        if (rivals.Count > 0)
        {
            using var writer = conn.BeginBinaryImport(
                "COPY leaderboard_rivals (user_id, rival_account_id, instrument, rank_method, direction, user_rank, rival_rank, shared_song_count, ahead_count, behind_count, avg_signed_delta, computed_at) FROM STDIN (FORMAT BINARY)");
            foreach (var r in rivals)
            {
                writer.StartRow();
                writer.Write(r.UserId, NpgsqlDbType.Text);
                writer.Write(r.RivalAccountId, NpgsqlDbType.Text);
                writer.Write(r.Instrument, NpgsqlDbType.Text);
                writer.Write(r.RankMethod, NpgsqlDbType.Text);
                writer.Write(r.Direction, NpgsqlDbType.Text);
                writer.Write(r.UserRank, NpgsqlDbType.Integer);
                writer.Write(r.RivalRank, NpgsqlDbType.Integer);
                writer.Write(r.SharedSongCount, NpgsqlDbType.Integer);
                writer.Write(r.AheadCount, NpgsqlDbType.Integer);
                writer.Write(r.BehindCount, NpgsqlDbType.Integer);
                writer.Write((float)r.AvgSignedDelta, NpgsqlDbType.Real);
                writer.Write(ParseUtc(r.ComputedAt), NpgsqlDbType.TimestampTz);
            }

            writer.Complete();
        }

        // Insert samples
        if (samples.Count > 0)
        {
            using var writer = conn.BeginBinaryImport(
                "COPY leaderboard_rival_song_samples (user_id, rival_account_id, instrument, rank_method, song_id, user_rank, rival_rank, rank_delta, user_score, rival_score) FROM STDIN (FORMAT BINARY)");
            foreach (var s in samples)
            {
                writer.StartRow();
                writer.Write(s.UserId, NpgsqlDbType.Text);
                writer.Write(s.RivalAccountId, NpgsqlDbType.Text);
                writer.Write(s.Instrument, NpgsqlDbType.Text);
                writer.Write(s.RankMethod, NpgsqlDbType.Text);
                writer.Write(s.SongId, NpgsqlDbType.Text);
                writer.Write(s.UserRank, NpgsqlDbType.Integer);
                writer.Write(s.RivalRank, NpgsqlDbType.Integer);
                writer.Write(s.RankDelta, NpgsqlDbType.Integer);
                WriteNullableInt(writer, s.UserScore);
                WriteNullableInt(writer, s.RivalScore);
            }

            writer.Complete();
        }

        tx.Commit();
    }

    public List<LeaderboardRivalRow> GetLeaderboardRivals(string userId, string? instrument = null, string? rankMethod = null, string? direction = null)
    {
        using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand();
        var where = "WHERE user_id = @uid"; cmd.Parameters.AddWithValue("uid", userId);
        if (instrument is not null) { where += " AND instrument = @inst"; cmd.Parameters.AddWithValue("inst", instrument); }
        if (rankMethod is not null) { where += " AND rank_method = @rm"; cmd.Parameters.AddWithValue("rm", rankMethod); }
        if (direction is not null) { where += " AND direction = @dir"; cmd.Parameters.AddWithValue("dir", direction); }
        cmd.CommandText = $"SELECT user_id, rival_account_id, instrument, rank_method, direction, user_rank, rival_rank, shared_song_count, ahead_count, behind_count, avg_signed_delta, computed_at FROM leaderboard_rivals {where} ORDER BY rank_method, direction";
        var list = new List<LeaderboardRivalRow>(); using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new LeaderboardRivalRow { UserId = r.GetString(0), RivalAccountId = r.GetString(1), Instrument = r.GetString(2), RankMethod = r.GetString(3), Direction = r.GetString(4), UserRank = r.GetInt32(5), RivalRank = r.GetInt32(6), SharedSongCount = r.GetInt32(7), AheadCount = r.GetInt32(8), BehindCount = r.GetInt32(9), AvgSignedDelta = r.GetDouble(10), ComputedAt = r.GetDateTime(11).ToString("o") });
        return list;
    }

    public List<LeaderboardRivalSongSampleRow> GetLeaderboardRivalSongSamples(string userId, string rivalAccountId, string instrument, string rankMethod)
    {
        using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_id, rival_account_id, instrument, rank_method, song_id, user_rank, rival_rank, rank_delta, user_score, rival_score FROM leaderboard_rival_song_samples WHERE user_id = @uid AND rival_account_id = @rid AND instrument = @inst AND rank_method = @rm ORDER BY ABS(rank_delta) ASC";
        cmd.Parameters.AddWithValue("uid", userId); cmd.Parameters.AddWithValue("rid", rivalAccountId); cmd.Parameters.AddWithValue("inst", instrument); cmd.Parameters.AddWithValue("rm", rankMethod);
        var list = new List<LeaderboardRivalSongSampleRow>(); using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new LeaderboardRivalSongSampleRow { UserId = r.GetString(0), RivalAccountId = r.GetString(1), Instrument = r.GetString(2), RankMethod = r.GetString(3), SongId = r.GetString(4), UserRank = r.GetInt32(5), RivalRank = r.GetInt32(6), RankDelta = r.GetInt32(7), UserScore = r.IsDBNull(8) ? null : r.GetInt32(8), RivalScore = r.IsDBNull(9) ? null : r.GetInt32(9) });
        return list;
    }

    // ── Item shop ────────────────────────────────────────────────────

    public void SaveItemShopTracks(IReadOnlySet<string> songIds, IReadOnlySet<string> leavingTomorrow, DateTime scrapedAt)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM item_shop_tracks"; c.ExecuteNonQuery(); }
        if (songIds.Count > 0) { using var c = conn.CreateCommand(); c.Transaction = tx; c.CommandText = "INSERT INTO item_shop_tracks (song_id, scraped_at, leaving_tomorrow) VALUES (@songId, @ts, @leaving)"; var pSong = c.Parameters.Add("songId", NpgsqlTypes.NpgsqlDbType.Text); var pTs = c.Parameters.Add("ts", NpgsqlTypes.NpgsqlDbType.TimestampTz); var pLeaving = c.Parameters.Add("leaving", NpgsqlTypes.NpgsqlDbType.Boolean); c.Prepare(); foreach (var songId in songIds) { pSong.Value = songId; pTs.Value = scrapedAt; pLeaving.Value = leavingTomorrow.Contains(songId); c.ExecuteNonQuery(); } }
        tx.Commit();
    }

    public (HashSet<string> InShop, HashSet<string> LeavingTomorrow) LoadItemShopTracks() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id, leaving_tomorrow FROM item_shop_tracks"; var inShop = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var leaving = new HashSet<string>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) { inShop.Add(r.GetString(0)); if (r.GetBoolean(1)) leaving.Add(r.GetString(0)); } return (inShop, leaving); }

    // ── Composite rankings ───────────────────────────────────────────

    public void ReplaceCompositeRankings(IReadOnlyList<CompositeRankingDto> rankings)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "TRUNCATE composite_rankings"; c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "SET LOCAL synchronous_commit = off"; c.ExecuteNonQuery(); }
        if (rankings.Count > 0)
        {
            var now = DateTime.UtcNow;
            using var writer = conn.BeginBinaryImport(
                "COPY composite_rankings (account_id, instruments_played, total_songs_played, composite_rating, composite_rank, guitar_adjusted_skill, guitar_skill_rank, bass_adjusted_skill, bass_skill_rank, drums_adjusted_skill, drums_skill_rank, vocals_adjusted_skill, vocals_skill_rank, pro_guitar_adjusted_skill, pro_guitar_skill_rank, pro_bass_adjusted_skill, pro_bass_skill_rank, pro_vocals_adjusted_skill, pro_vocals_skill_rank, pro_cymbals_adjusted_skill, pro_cymbals_skill_rank, pro_drums_adjusted_skill, pro_drums_skill_rank, composite_rating_weighted, composite_rank_weighted, composite_rating_fcrate, composite_rank_fcrate, composite_rating_totalscore, composite_rank_totalscore, composite_rating_maxscore, composite_rank_maxscore, computed_at) FROM STDIN (FORMAT BINARY)");
            foreach (var rv in rankings)
            {
                writer.StartRow();
                writer.Write(rv.AccountId, NpgsqlDbType.Text);
                writer.Write(rv.InstrumentsPlayed, NpgsqlDbType.Integer);
                writer.Write(rv.TotalSongsPlayed, NpgsqlDbType.Integer);
                writer.Write((float)rv.CompositeRating, NpgsqlDbType.Real);
                writer.Write(rv.CompositeRank, NpgsqlDbType.Integer);
                WriteNullableReal(writer, rv.GuitarAdjustedSkill);
                WriteNullableInt(writer, rv.GuitarSkillRank);
                WriteNullableReal(writer, rv.BassAdjustedSkill);
                WriteNullableInt(writer, rv.BassSkillRank);
                WriteNullableReal(writer, rv.DrumsAdjustedSkill);
                WriteNullableInt(writer, rv.DrumsSkillRank);
                WriteNullableReal(writer, rv.VocalsAdjustedSkill);
                WriteNullableInt(writer, rv.VocalsSkillRank);
                WriteNullableReal(writer, rv.ProGuitarAdjustedSkill);
                WriteNullableInt(writer, rv.ProGuitarSkillRank);
                WriteNullableReal(writer, rv.ProBassAdjustedSkill);
                WriteNullableInt(writer, rv.ProBassSkillRank);
                WriteNullableReal(writer, rv.ProVocalsAdjustedSkill);
                WriteNullableInt(writer, rv.ProVocalsSkillRank);
                WriteNullableReal(writer, rv.ProCymbalsAdjustedSkill);
                WriteNullableInt(writer, rv.ProCymbalsSkillRank);
                WriteNullableReal(writer, rv.ProDrumsAdjustedSkill);
                WriteNullableInt(writer, rv.ProDrumsSkillRank);
                WriteNullableReal(writer, rv.CompositeRatingWeighted);
                WriteNullableInt(writer, rv.CompositeRankWeighted);
                WriteNullableReal(writer, rv.CompositeRatingFcRate);
                WriteNullableInt(writer, rv.CompositeRankFcRate);
                WriteNullableReal(writer, rv.CompositeRatingTotalScore);
                WriteNullableInt(writer, rv.CompositeRankTotalScore);
                WriteNullableReal(writer, rv.CompositeRatingMaxScore);
                WriteNullableInt(writer, rv.CompositeRankMaxScore);
                writer.Write(now, NpgsqlDbType.TimestampTz);
            }

            writer.Complete();
        }
        tx.Commit();
    }

    public (List<CompositeRankingDto> Entries, int TotalCount) GetCompositeRankings(int page = 1, int pageSize = 50) { using var conn = _ds.OpenConnection(); int total; using (var c = conn.CreateCommand()) { c.CommandText = "SELECT COUNT(*) FROM composite_rankings"; total = Convert.ToInt32(c.ExecuteScalar()); } using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, instruments_played, total_songs_played, composite_rating, composite_rank, guitar_adjusted_skill, guitar_skill_rank, bass_adjusted_skill, bass_skill_rank, drums_adjusted_skill, drums_skill_rank, vocals_adjusted_skill, vocals_skill_rank, pro_guitar_adjusted_skill, pro_guitar_skill_rank, pro_bass_adjusted_skill, pro_bass_skill_rank, pro_vocals_adjusted_skill, pro_vocals_skill_rank, pro_cymbals_adjusted_skill, pro_cymbals_skill_rank, pro_drums_adjusted_skill, pro_drums_skill_rank, composite_rating_weighted, composite_rank_weighted, composite_rating_fcrate, composite_rank_fcrate, composite_rating_totalscore, composite_rank_totalscore, composite_rating_maxscore, composite_rank_maxscore, computed_at FROM composite_rankings ORDER BY composite_rank ASC LIMIT @limit OFFSET @offset"; cmd.Parameters.AddWithValue("limit", pageSize); cmd.Parameters.AddWithValue("offset", (page - 1) * pageSize); var list = new List<CompositeRankingDto>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(ReadCompositeRanking(r)); return (list, total); }
    public CompositeRankingDto? GetCompositeRanking(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, instruments_played, total_songs_played, composite_rating, composite_rank, guitar_adjusted_skill, guitar_skill_rank, bass_adjusted_skill, bass_skill_rank, drums_adjusted_skill, drums_skill_rank, vocals_adjusted_skill, vocals_skill_rank, pro_guitar_adjusted_skill, pro_guitar_skill_rank, pro_bass_adjusted_skill, pro_bass_skill_rank, pro_vocals_adjusted_skill, pro_vocals_skill_rank, pro_cymbals_adjusted_skill, pro_cymbals_skill_rank, pro_drums_adjusted_skill, pro_drums_skill_rank, composite_rating_weighted, composite_rank_weighted, composite_rating_fcrate, composite_rank_fcrate, composite_rating_totalscore, composite_rank_totalscore, composite_rating_maxscore, composite_rank_maxscore, computed_at FROM composite_rankings WHERE account_id = @accountId"; cmd.Parameters.AddWithValue("accountId", accountId); using var r = cmd.ExecuteReader(); return r.Read() ? ReadCompositeRanking(r) : null; }

    // ── Solo family rankings ────────────────────────────────────────

    public void ReplaceSoloFamilyRankings(IReadOnlyList<SoloFamilyRankingDto> rankings)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "TRUNCATE solo_family_rankings"; c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "SET LOCAL synchronous_commit = off"; c.ExecuteNonQuery(); }

        if (rankings.Count > 0)
        {
            var now = DateTime.UtcNow;
            using var writer = conn.BeginBinaryImport(
                "COPY solo_family_rankings (scope_id, account_id, songs_played, total_charted_songs, coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score, total_score_rank, max_score_percent, max_score_percent_rank, full_combo_count, raw_max_score_percent, raw_weighted_rating, computed_at) FROM STDIN (FORMAT BINARY)");
            foreach (var ranking in rankings)
            {
                writer.StartRow();
                writer.Write(ranking.ScopeId, NpgsqlDbType.Text);
                writer.Write(ranking.AccountId, NpgsqlDbType.Text);
                writer.Write(ranking.SongsPlayed, NpgsqlDbType.Integer);
                writer.Write(ranking.TotalChartedSongs, NpgsqlDbType.Integer);
                writer.Write((float)ranking.Coverage, NpgsqlDbType.Real);
                writer.Write((float)ranking.RawSkillRating, NpgsqlDbType.Real);
                writer.Write((float)ranking.AdjustedSkillRating, NpgsqlDbType.Real);
                writer.Write(ranking.AdjustedSkillRank, NpgsqlDbType.Integer);
                writer.Write((float)ranking.WeightedRating, NpgsqlDbType.Real);
                writer.Write(ranking.WeightedRank, NpgsqlDbType.Integer);
                writer.Write((float)ranking.FcRate, NpgsqlDbType.Real);
                writer.Write(ranking.FcRateRank, NpgsqlDbType.Integer);
                writer.Write(ranking.TotalScore, NpgsqlDbType.Bigint);
                writer.Write(ranking.TotalScoreRank, NpgsqlDbType.Integer);
                writer.Write((float)ranking.MaxScorePercent, NpgsqlDbType.Real);
                writer.Write(ranking.MaxScorePercentRank, NpgsqlDbType.Integer);
                writer.Write(ranking.FullComboCount, NpgsqlDbType.Integer);
                WriteNullableReal(writer, ranking.RawMaxScorePercent);
                WriteNullableReal(writer, ranking.RawWeightedRating);
                writer.Write(now, NpgsqlDbType.TimestampTz);
            }

            writer.Complete();
        }

        tx.Commit();
    }

    public (List<SoloFamilyRankingDto> Entries, int TotalCount) GetSoloFamilyRankings(string scopeId, string rankBy = "adjusted", int page = 1, int pageSize = 50)
    {
        var rankColumn = SoloFamilyRankColumn(rankBy);
        using var conn = _ds.OpenConnection();
        int total;
        using (var count = conn.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM solo_family_rankings WHERE scope_id = @scopeId";
            count.Parameters.AddWithValue("scopeId", scopeId);
            total = Convert.ToInt32(count.ExecuteScalar());
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT scope_id, account_id, songs_played, total_charted_songs, coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score, total_score_rank, max_score_percent, max_score_percent_rank, full_combo_count, raw_max_score_percent, raw_weighted_rating, computed_at FROM solo_family_rankings WHERE scope_id = @scopeId ORDER BY {rankColumn} ASC LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("scopeId", scopeId);
        cmd.Parameters.AddWithValue("limit", pageSize);
        cmd.Parameters.AddWithValue("offset", (page - 1) * pageSize);
        var entries = new List<SoloFamilyRankingDto>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) entries.Add(ReadSoloFamilyRanking(r, total));
        return (entries, total);
    }

    public SoloFamilyRankingDto? GetSoloFamilyRanking(string scopeId, string accountId)
    {
        using var conn = _ds.OpenConnection();
        var total = GetSoloFamilyTotalAccounts(conn, scopeId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT scope_id, account_id, songs_played, total_charted_songs, coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score, total_score_rank, max_score_percent, max_score_percent_rank, full_combo_count, raw_max_score_percent, raw_weighted_rating, computed_at FROM solo_family_rankings WHERE scope_id = @scopeId AND account_id = @accountId";
        cmd.Parameters.AddWithValue("scopeId", scopeId);
        cmd.Parameters.AddWithValue("accountId", accountId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSoloFamilyRanking(r, total) : null;
    }

    public Dictionary<string, SoloFamilyRankingDto> GetSoloFamilyRankingsForAccount(string accountId)
    {
        using var conn = _ds.OpenConnection();
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var count = conn.CreateCommand())
        {
            count.CommandText = "SELECT scope_id, COUNT(*) FROM solo_family_rankings GROUP BY scope_id";
            using var reader = count.ExecuteReader();
            while (reader.Read()) totals[reader.GetString(0)] = Convert.ToInt32(reader.GetValue(1));
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT scope_id, account_id, songs_played, total_charted_songs, coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score, total_score_rank, max_score_percent, max_score_percent_rank, full_combo_count, raw_max_score_percent, raw_weighted_rating, computed_at FROM solo_family_rankings WHERE account_id = @accountId";
        cmd.Parameters.AddWithValue("accountId", accountId);
        var result = new Dictionary<string, SoloFamilyRankingDto>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var scopeId = r.GetString(0);
            result[scopeId] = ReadSoloFamilyRanking(r, totals.GetValueOrDefault(scopeId));
        }

        return result;
    }

    public (List<CompositeRankingDto> Above, CompositeRankingDto? Self, List<CompositeRankingDto> Below) GetCompositeRankingNeighborhood(string accountId, int radius = 5)
    {
        var self = GetCompositeRanking(accountId);
        if (self is null) return (new(), null, new());
        using var conn = _ds.OpenConnection();
        var above = new List<CompositeRankingDto>();
        using (var cmd = conn.CreateCommand()) { cmd.CommandText = "SELECT account_id, instruments_played, total_songs_played, composite_rating, composite_rank, guitar_adjusted_skill, guitar_skill_rank, bass_adjusted_skill, bass_skill_rank, drums_adjusted_skill, drums_skill_rank, vocals_adjusted_skill, vocals_skill_rank, pro_guitar_adjusted_skill, pro_guitar_skill_rank, pro_bass_adjusted_skill, pro_bass_skill_rank, pro_vocals_adjusted_skill, pro_vocals_skill_rank, pro_cymbals_adjusted_skill, pro_cymbals_skill_rank, pro_drums_adjusted_skill, pro_drums_skill_rank, composite_rating_weighted, composite_rank_weighted, composite_rating_fcrate, composite_rank_fcrate, composite_rating_totalscore, composite_rank_totalscore, composite_rating_maxscore, composite_rank_maxscore, computed_at FROM composite_rankings WHERE composite_rank < @selfRank ORDER BY composite_rank DESC LIMIT @radius"; cmd.Parameters.AddWithValue("selfRank", self.CompositeRank); cmd.Parameters.AddWithValue("radius", radius); using var r = cmd.ExecuteReader(); while (r.Read()) above.Add(ReadCompositeRanking(r)); }
        above.Reverse();
        var below = new List<CompositeRankingDto>();
        using (var cmd = conn.CreateCommand()) { cmd.CommandText = "SELECT account_id, instruments_played, total_songs_played, composite_rating, composite_rank, guitar_adjusted_skill, guitar_skill_rank, bass_adjusted_skill, bass_skill_rank, drums_adjusted_skill, drums_skill_rank, vocals_adjusted_skill, vocals_skill_rank, pro_guitar_adjusted_skill, pro_guitar_skill_rank, pro_bass_adjusted_skill, pro_bass_skill_rank, pro_vocals_adjusted_skill, pro_vocals_skill_rank, pro_cymbals_adjusted_skill, pro_cymbals_skill_rank, pro_drums_adjusted_skill, pro_drums_skill_rank, composite_rating_weighted, composite_rank_weighted, composite_rating_fcrate, composite_rank_fcrate, composite_rating_totalscore, composite_rank_totalscore, composite_rating_maxscore, composite_rank_maxscore, computed_at FROM composite_rankings WHERE composite_rank > @selfRank ORDER BY composite_rank ASC LIMIT @radius"; cmd.Parameters.AddWithValue("selfRank", self.CompositeRank); cmd.Parameters.AddWithValue("radius", radius); using var r = cmd.ExecuteReader(); while (r.Read()) below.Add(ReadCompositeRanking(r)); }
        return (above, self, below);
    }

    public void SnapshotCompositeRankHistory(int retentionDays = 365, bool cleanupRetention = true)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        // Step A: Build temp table of each account's latest composite snapshot
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = @"
                CREATE TEMP TABLE _latest_composite ON COMMIT DROP AS
                SELECT DISTINCT ON (account_id)
                    account_id, composite_rank, composite_rating, instruments_played, total_songs_played
                FROM composite_rank_history
                ORDER BY account_id, snapshot_date DESC";
            c.ExecuteNonQuery();
        }

        // Step B: Insert only changed or new accounts
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = @"
                INSERT INTO composite_rank_history (account_id, snapshot_date, composite_rank,
                    composite_rating, instruments_played, total_songs_played)
                SELECT cr.account_id, @today, cr.composite_rank,
                    cr.composite_rating, cr.instruments_played, cr.total_songs_played
                FROM composite_rankings cr
                LEFT JOIN _latest_composite lc ON lc.account_id = cr.account_id
                WHERE lc.account_id IS NULL
                  OR lc.composite_rank IS DISTINCT FROM cr.composite_rank
                  OR lc.composite_rating IS DISTINCT FROM cr.composite_rating
                  OR lc.instruments_played IS DISTINCT FROM cr.instruments_played
                  OR lc.total_songs_played IS DISTINCT FROM cr.total_songs_played
                ON CONFLICT (account_id, snapshot_date) DO UPDATE SET
                    composite_rank = EXCLUDED.composite_rank,
                    composite_rating = EXCLUDED.composite_rating,
                    instruments_played = EXCLUDED.instruments_played,
                    total_songs_played = EXCLUDED.total_songs_played";
            c.Parameters.AddWithValue("today", today);
            c.ExecuteNonQuery();
        }

        if (cleanupRetention)
            CleanupCompositeRankHistoryRetention(conn, tx, retentionDays);

        tx.Commit();
    }

    public int CleanupCompositeRankHistoryRetention(
        int retentionDays = 365,
        int batchSize = 5000,
        int maxBatches = 1,
        int commandTimeoutSeconds = 0,
        CancellationToken ct = default)
    {
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (maxBatches <= 0) throw new ArgumentOutOfRangeException(nameof(maxBatches));

        using var conn = _ds.OpenConnection();
        var totalDeleted = 0;

        for (var batch = 0; batch < maxBatches; batch++)
        {
            ct.ThrowIfCancellationRequested();
            using var tx = conn.BeginTransaction();
            var deleted = CleanupCompositeRankHistoryRetentionBatch(
                conn,
                tx,
                retentionDays,
                batchSize,
                commandTimeoutSeconds,
                ct);
            tx.Commit();
            totalDeleted += deleted;

            if (deleted < batchSize)
                break;
        }

        return totalDeleted;
    }

    private static int CleanupCompositeRankHistoryRetention(NpgsqlConnection conn, NpgsqlTransaction tx, int retentionDays)
        => CleanupCompositeRankHistoryRetentionBatch(
            conn,
            tx,
            retentionDays,
            FSTService.DatabaseMaintenanceOptions.DefaultCleanupBatchSize,
            commandTimeoutSeconds: 0,
            ct: default);

    private static int CleanupCompositeRankHistoryRetentionBatch(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        int retentionDays,
        int batchSize,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        if (retentionDays <= 0)
            return 0;

        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-retentionDays);
        using var c = conn.CreateCommand();
        c.Transaction = tx;
        ConfigureCommandTimeout(c, commandTimeoutSeconds);
        c.CommandText = @"
            WITH doomed AS (
                SELECT crh.ctid
                FROM composite_rank_history crh
                WHERE crh.snapshot_date < @cutoff
                  AND EXISTS (
                    SELECT 1 FROM composite_rank_history crh2
                    WHERE crh2.account_id = crh.account_id
                      AND crh2.snapshot_date > crh.snapshot_date
                      AND crh2.snapshot_date <= @cutoff
                  )
                ORDER BY crh.snapshot_date ASC, crh.account_id ASC
                LIMIT @batchSize
            )
            DELETE FROM composite_rank_history crh
            USING doomed
            WHERE crh.ctid = doomed.ctid";
        c.Parameters.AddWithValue("cutoff", cutoff);
        c.Parameters.AddWithValue("batchSize", batchSize);
        return ExecuteNonQueryWithCancellation(c, ct);
    }

    // ── Composite ranking deltas ─────────────────────────────────────

    public void TruncateCompositeRankingDeltas()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE composite_ranking_deltas";
        cmd.ExecuteNonQuery();
    }

    public void WriteCompositeRankingDeltas(IReadOnlyList<(string AccountId, double LeewayBucket,
        double AdjustedRating, double WeightedRating, double FcRateRating,
        double TotalScore, double MaxScoreRating, int InstrumentsPlayed, int TotalSongsPlayed)> deltas)
    {
        if (deltas.Count == 0) return;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO composite_ranking_deltas (account_id, leeway_bucket, adjusted_rating, weighted_rating, " +
            "fc_rate_rating, total_score, max_score_rating, instruments_played, total_songs_played) " +
            "VALUES (@aid, @bucket, @adj, @wgt, @fc, @ts, @ms, @inst, @songs)";
        cmd.Parameters.Add("aid", NpgsqlTypes.NpgsqlDbType.Text);
        cmd.Parameters.Add("bucket", NpgsqlTypes.NpgsqlDbType.Real);
        cmd.Parameters.Add("adj", NpgsqlTypes.NpgsqlDbType.Real);
        cmd.Parameters.Add("wgt", NpgsqlTypes.NpgsqlDbType.Real);
        cmd.Parameters.Add("fc", NpgsqlTypes.NpgsqlDbType.Real);
        cmd.Parameters.Add("ts", NpgsqlTypes.NpgsqlDbType.Real);
        cmd.Parameters.Add("ms", NpgsqlTypes.NpgsqlDbType.Real);
        cmd.Parameters.Add("inst", NpgsqlTypes.NpgsqlDbType.Integer);
        cmd.Parameters.Add("songs", NpgsqlTypes.NpgsqlDbType.Integer);
        cmd.Prepare();
        foreach (var d in deltas)
        {
            cmd.Parameters["aid"].Value = d.AccountId;
            cmd.Parameters["bucket"].Value = (float)d.LeewayBucket;
            cmd.Parameters["adj"].Value = (float)d.AdjustedRating;
            cmd.Parameters["wgt"].Value = (float)d.WeightedRating;
            cmd.Parameters["fc"].Value = (float)d.FcRateRating;
            cmd.Parameters["ts"].Value = (float)d.TotalScore;
            cmd.Parameters["ms"].Value = (float)d.MaxScoreRating;
            cmd.Parameters["inst"].Value = d.InstrumentsPlayed;
            cmd.Parameters["songs"].Value = d.TotalSongsPlayed;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ── Combo leaderboard ────────────────────────────────────────────

    public void ReplaceComboLeaderboard(string comboId, IReadOnlyList<(string AccountId, double AdjustedRating, double WeightedRating, double FcRate, long TotalScore, double MaxScorePercent, int SongsPlayed, int FullComboCount)> entries, int totalAccounts)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM combo_leaderboard WHERE combo_id = @id"; c.Parameters.AddWithValue("id", comboId); c.ExecuteNonQuery(); }
        var now = DateTime.UtcNow;
        if (entries.Count > 0) { using var c = conn.CreateCommand(); c.Transaction = tx; c.CommandText = "INSERT INTO combo_leaderboard (combo_id, account_id, adjusted_rating, weighted_rating, fc_rate, total_score, max_score_percent, songs_played, full_combo_count, computed_at) VALUES (@id, @aid, @adj, @wgt, @fc, @ts, @ms, @songs, @fcc, @now)"; c.Parameters.Add("id", NpgsqlTypes.NpgsqlDbType.Text); c.Parameters.Add("aid", NpgsqlTypes.NpgsqlDbType.Text); c.Parameters.Add("adj", NpgsqlTypes.NpgsqlDbType.Double); c.Parameters.Add("wgt", NpgsqlTypes.NpgsqlDbType.Double); c.Parameters.Add("fc", NpgsqlTypes.NpgsqlDbType.Double); c.Parameters.Add("ts", NpgsqlTypes.NpgsqlDbType.Integer); c.Parameters.Add("ms", NpgsqlTypes.NpgsqlDbType.Double); c.Parameters.Add("songs", NpgsqlTypes.NpgsqlDbType.Integer); c.Parameters.Add("fcc", NpgsqlTypes.NpgsqlDbType.Integer); c.Parameters.Add("now", NpgsqlTypes.NpgsqlDbType.TimestampTz); c.Prepare(); foreach (var e in entries) { c.Parameters["id"].Value = comboId; c.Parameters["aid"].Value = e.AccountId; c.Parameters["adj"].Value = e.AdjustedRating; c.Parameters["wgt"].Value = e.WeightedRating; c.Parameters["fc"].Value = e.FcRate; c.Parameters["ts"].Value = (int)e.TotalScore; c.Parameters["ms"].Value = e.MaxScorePercent; c.Parameters["songs"].Value = e.SongsPlayed; c.Parameters["fcc"].Value = e.FullComboCount; c.Parameters["now"].Value = now; c.ExecuteNonQuery(); } }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "INSERT INTO combo_stats (combo_id, total_accounts, computed_at) VALUES (@id, @total, @now) ON CONFLICT(combo_id) DO UPDATE SET total_accounts = EXCLUDED.total_accounts, computed_at = EXCLUDED.computed_at"; c.Parameters.AddWithValue("id", comboId); c.Parameters.AddWithValue("total", totalAccounts); c.Parameters.AddWithValue("now", now); c.ExecuteNonQuery(); }
        tx.Commit();
    }

    public (List<ComboLeaderboardEntry> Entries, int TotalAccounts) GetComboLeaderboard(string comboId, string rankBy = "adjusted", int page = 1, int pageSize = 50) { using var conn = _ds.OpenConnection(); int total; using (var c = conn.CreateCommand()) { c.CommandText = "SELECT total_accounts FROM combo_stats WHERE combo_id = @id"; c.Parameters.AddWithValue("id", comboId); var r2 = c.ExecuteScalar(); total = r2 is DBNull or null ? 0 : Convert.ToInt32(r2); } var orderBy = ComboRankOrderBy(rankBy); using var cmd = conn.CreateCommand(); cmd.CommandText = $"SELECT ROW_NUMBER() OVER (ORDER BY {orderBy}) AS rank, account_id, adjusted_rating, weighted_rating, fc_rate, total_score, max_score_percent, songs_played, full_combo_count, computed_at FROM combo_leaderboard WHERE combo_id = @id ORDER BY {orderBy} LIMIT @limit OFFSET @offset"; cmd.Parameters.AddWithValue("id", comboId); cmd.Parameters.AddWithValue("limit", pageSize); cmd.Parameters.AddWithValue("offset", (page - 1) * pageSize); var list = new List<ComboLeaderboardEntry>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(ReadComboEntry(r)); return (list, total); }
    public ComboLeaderboardEntry? GetComboRank(string comboId, string accountId, string rankBy = "adjusted")
    {
        var rankPredicate = ComboRankPrecedesPredicate(rankBy);
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            WITH target AS (
                SELECT account_id, adjusted_rating, weighted_rating, fc_rate, total_score,
                       max_score_percent, songs_played, full_combo_count, computed_at
                FROM combo_leaderboard
                WHERE combo_id = @id AND account_id = @aid
            )
            SELECT
                (
                    SELECT COUNT(*) + 1
                    FROM combo_leaderboard other, target
                    WHERE other.combo_id = @id
                      AND ({rankPredicate})
                ) AS rank,
                target.account_id,
                target.adjusted_rating,
                target.weighted_rating,
                target.fc_rate,
                target.total_score,
                target.max_score_percent,
                target.songs_played,
                target.full_combo_count,
                target.computed_at
            FROM target";
        cmd.Parameters.AddWithValue("id", comboId);
        cmd.Parameters.AddWithValue("aid", accountId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadComboEntry(r) : null;
    }
    public int GetComboTotalAccounts(string comboId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT total_accounts FROM combo_stats WHERE combo_id = @id"; cmd.Parameters.AddWithValue("id", comboId); var result = cmd.ExecuteScalar(); return result is DBNull or null ? 0 : Convert.ToInt32(result); }

    // ── Band team rankings ──────────────────────────────────────────

    public void RebuildBandTeamRankings(string bandType, int totalChartedSongs, int credibilityThreshold = 50, double populationMedian = 0.5, BandTeamRankingRebuildOptions? options = null)
    {
        RebuildBandTeamRankingsMeasured(bandType, totalChartedSongs, credibilityThreshold, populationMedian, options);
    }

    public BandTeamRankingRebuildMetrics RebuildBandTeamRankingsMeasured(string bandType, int totalChartedSongs, int credibilityThreshold = 50, double populationMedian = 0.5, BandTeamRankingRebuildOptions? options = null)
    {
        var resolvedOptions = ResolveBandTeamRankingRebuildOptions(options);
        var expectedMembers = BandInstrumentMapping.ExpectedMemberCount(bandType);
        var totalSw = Stopwatch.StartNew();
        var lastCompletedStage = "open_connection";
        var currentStage = resolvedOptions.DisableSynchronousCommit
            ? "disable_synchronous_commit"
            : "materialize_results";
        var syncCommitMs = 0d;
        var materializeMs = 0d;
        var analyzeMs = 0d;
        var distinctComboCount = 0;
        var deleteExistingMs = 0d;
        var insertRankingsMs = 0d;
        var insertStatsMs = 0d;
        var resultRowCount = 0;
        var statsRowCount = 0;
        var rankingGeneration = 0L;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        try
        {
            currentStage = "ensure_vnext_schema";
            EnsureBandRankHistoryTables(conn, tx);
            lastCompletedStage = "ensure_vnext_schema";

            if (resolvedOptions.DisableSynchronousCommit)
            {
                var syncCommitSw = Stopwatch.StartNew();
                using var cmd = conn.CreateCommand();
                ConfigureBandRebuildCommand(cmd, tx, resolvedOptions);
                cmd.CommandText = "SET LOCAL synchronous_commit = off";
                cmd.ExecuteNonQuery();
                syncCommitSw.Stop();
                syncCommitMs = RoundElapsed(syncCommitSw);
                LogBandRebuildStage(bandType, resolvedOptions, "disable_synchronous_commit", syncCommitMs);
                lastCompletedStage = "disable_synchronous_commit";
            }

            currentStage = "materialize_results";
            var materializeSw = Stopwatch.StartNew();
            var computedAt = DateTime.UtcNow;
            rankingGeneration = CreateBandRankingGeneration(conn, tx, resolvedOptions, bandType, computedAt);
            switch (resolvedOptions.WriteMode)
            {
                case BandTeamRankingWriteMode.Monolithic:
                case BandTeamRankingWriteMode.ComboBatched:
                    MaterializeBandTeamRankingResultsMonolithic(
                        conn,
                        tx,
                        resolvedOptions,
                        bandType,
                        totalChartedSongs,
                        expectedMembers,
                        credibilityThreshold,
                        populationMedian,
                        computedAt);
                    currentStage = "materialize_results";
                    break;
                case BandTeamRankingWriteMode.Phased:
                    MaterializeBandTeamRankingResultsPhased(
                        conn,
                        tx,
                        resolvedOptions,
                        bandType,
                        totalChartedSongs,
                        expectedMembers,
                        credibilityThreshold,
                        populationMedian,
                        computedAt,
                        ref currentStage);
                    currentStage = "materialize_results";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(resolvedOptions.WriteMode), resolvedOptions.WriteMode, "Unsupported band ranking materialization mode.");
            }
            materializeSw.Stop();
            materializeMs = RoundElapsed(materializeSw);
            LogBandRebuildStage(bandType, resolvedOptions, "materialize_results", materializeMs);
            lastCompletedStage = "materialize_results";

            if (resolvedOptions.AnalyzeStagingTable)
            {
                currentStage = "analyze_results";
                var analyzeSw = Stopwatch.StartNew();
                using var cmd = conn.CreateCommand();
                ConfigureBandRebuildCommand(cmd, tx, resolvedOptions);
                cmd.CommandText = "ANALYZE _band_rank_results";
                cmd.ExecuteNonQuery();
                analyzeSw.Stop();
                analyzeMs = RoundElapsed(analyzeSw);
                LogBandRebuildStage(bandType, resolvedOptions, "analyze_results", analyzeMs);
                lastCompletedStage = "analyze_results";
            }

            currentStage = "count_distinct_combos";
            using (var cmd = conn.CreateCommand())
            {
                ConfigureBandRebuildCommand(cmd, tx, resolvedOptions);
                cmd.CommandText = @"
                SELECT COUNT(*)::INT
                FROM (
                    SELECT combo_id
                    FROM _band_rank_results
                    WHERE ranking_scope = 'combo'
                    GROUP BY combo_id
                ) combos;";
                distinctComboCount = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
            LogBandRebuildStage(bandType, resolvedOptions, "count_distinct_combos", 0d, distinctComboCount);
            lastCompletedStage = "count_distinct_combos";

            currentStage = "insert_rankings";
            var insertRankingsSw = Stopwatch.StartNew();
            var buildSuffix = Guid.NewGuid().ToString("N")[..8];

            currentStage = "create_ranking_build_table";
            var createRankingTableSw = Stopwatch.StartNew();
            var buildRankingTable = CreateBandRankingBuildTable(conn, tx, resolvedOptions, bandType, buildSuffix);
            createRankingTableSw.Stop();
            LogBandRebuildStage(bandType, resolvedOptions, "create_ranking_build_table", RoundElapsed(createRankingTableSw));

            currentStage = "insert_ranking_rows";
            var insertRankingRowsSw = Stopwatch.StartNew();
            resultRowCount = resolvedOptions.WriteMode switch
            {
                BandTeamRankingWriteMode.Monolithic => InsertBandTeamRankingRowsMonolithic(conn, tx, resolvedOptions, buildRankingTable, rankingGeneration),
                BandTeamRankingWriteMode.ComboBatched => InsertBandTeamRankingRowsComboBatched(conn, tx, resolvedOptions, buildRankingTable, rankingGeneration),
                BandTeamRankingWriteMode.Phased => InsertBandTeamRankingRowsMonolithic(conn, tx, resolvedOptions, buildRankingTable, rankingGeneration),
                _ => throw new ArgumentOutOfRangeException(nameof(resolvedOptions.WriteMode), resolvedOptions.WriteMode, "Unsupported band ranking write mode."),
            };
            insertRankingRowsSw.Stop();
            LogBandRebuildStage(bandType, resolvedOptions, "insert_ranking_rows", RoundElapsed(insertRankingRowsSw), rowCount: resultRowCount);

            currentStage = "create_ranking_indexes";
            var createRankingIndexesSw = Stopwatch.StartNew();
            CreateBandRankingIndexes(conn, tx, resolvedOptions, buildRankingTable);
            createRankingIndexesSw.Stop();
            LogBandRebuildStage(bandType, resolvedOptions, "create_ranking_indexes", RoundElapsed(createRankingIndexesSw));

            insertRankingsSw.Stop();
            insertRankingsMs = RoundElapsed(insertRankingsSw);
            LogBandRebuildStage(bandType, resolvedOptions, "insert_rankings", insertRankingsMs, rowCount: resultRowCount);
            lastCompletedStage = "insert_rankings";

            currentStage = "insert_stats";
            var insertStatsSw = Stopwatch.StartNew();

            currentStage = "create_stats_build_table";
            var createStatsTableSw = Stopwatch.StartNew();
            var buildStatsTable = CreateBandRankingStatsBuildTable(conn, tx, resolvedOptions, bandType, buildSuffix);
            createStatsTableSw.Stop();
            LogBandRebuildStage(bandType, resolvedOptions, "create_stats_build_table", RoundElapsed(createStatsTableSw));

            currentStage = "insert_stats_rows";
            var insertStatsRowsSw = Stopwatch.StartNew();
            statsRowCount = InsertBandTeamRankingStatsRows(conn, tx, resolvedOptions, buildStatsTable);
            insertStatsRowsSw.Stop();
            LogBandRebuildStage(bandType, resolvedOptions, "insert_stats_rows", RoundElapsed(insertStatsRowsSw), rowCount: statsRowCount);

            currentStage = "create_stats_indexes";
            var createStatsIndexesSw = Stopwatch.StartNew();
            CreateBandRankingStatsIndexes(conn, tx, resolvedOptions, buildStatsTable);
            createStatsIndexesSw.Stop();
            LogBandRebuildStage(bandType, resolvedOptions, "create_stats_indexes", RoundElapsed(createStatsIndexesSw));

            insertStatsSw.Stop();
            insertStatsMs = RoundElapsed(insertStatsSw);
            LogBandRebuildStage(bandType, resolvedOptions, "insert_stats", insertStatsMs, rowCount: statsRowCount);
            lastCompletedStage = "insert_stats";

            currentStage = "swap_current";
            var swapSw = Stopwatch.StartNew();
            SwapBandCurrentTables(conn, tx, resolvedOptions, bandType, buildRankingTable, buildStatsTable, buildSuffix);
            CompleteBandRankingGeneration(conn, tx, resolvedOptions, rankingGeneration, bandType, resultRowCount, statsRowCount);
            swapSw.Stop();
            deleteExistingMs = RoundElapsed(swapSw);
            LogBandRebuildStage(bandType, resolvedOptions, "swap_current", deleteExistingMs);
            lastCompletedStage = "swap_current";

            currentStage = "commit";
            tx.Commit();
            LogBandRebuildStage(bandType, resolvedOptions, "commit", 0d);
            lastCompletedStage = "commit";

            totalSw.Stop();
            var metrics = new BandTeamRankingRebuildMetrics(
                bandType,
                resolvedOptions.WriteMode,
                resultRowCount,
                statsRowCount,
                distinctComboCount,
                materializeMs,
                analyzeMs,
                deleteExistingMs,
                insertRankingsMs,
                insertStatsMs,
                RoundElapsed(totalSw));

            _log.LogInformation(
                "Rebuilt band team rankings for {BandType} using {WriteMode}: rows={ResultRowCount}, stats={StatsRowCount}, combos={DistinctComboCount}, materializeMs={MaterializeResultsMs}, analyzeMs={AnalyzeResultsMs}, deleteMs={DeleteExistingMs}, insertMs={InsertRankingsMs}, statsMs={InsertStatsMs}, totalMs={TotalElapsedMs}",
                metrics.BandType,
                metrics.WriteMode,
                metrics.ResultRowCount,
                metrics.StatsRowCount,
                metrics.DistinctComboCount,
                metrics.MaterializeResultsMs,
                metrics.AnalyzeResultsMs,
                metrics.DeleteExistingMs,
                metrics.InsertRankingsMs,
                metrics.InsertStatsMs,
                metrics.TotalElapsedMs);

            return metrics;
        }
        catch
        {
            totalSw.Stop();
            _log.LogWarning(
                "Band team ranking rebuild failed for {BandType} using {WriteMode}: timeoutSeconds={CommandTimeoutSeconds}, lastCompletedStage={LastCompletedStage}, failingStage={FailingStage}, syncCommitMs={SyncCommitMs}, materializeMs={MaterializeResultsMs}, analyzeMs={AnalyzeResultsMs}, deleteMs={DeleteExistingMs}, insertMs={InsertRankingsMs}, statsMs={InsertStatsMs}, distinctCombos={DistinctComboCount}, resultRows={ResultRowCount}, statsRows={StatsRowCount}, totalMs={TotalElapsedMs}",
                bandType,
                resolvedOptions.WriteMode,
                resolvedOptions.CommandTimeoutSeconds,
                lastCompletedStage,
                currentStage,
                syncCommitMs,
                materializeMs,
                analyzeMs,
                deleteExistingMs,
                insertRankingsMs,
                insertStatsMs,
                distinctComboCount,
                resultRowCount,
                statsRowCount,
                RoundElapsed(totalSw));
            throw;
        }
    }

    private void LogBandRebuildStage(string bandType, BandTeamRankingRebuildOptions options, string stage, double elapsedMs, int? rowCount = null, string? comboId = null)
    {
        _log.LogInformation(
            "[BandRankings.Stage] band_type={BandType} write_mode={WriteMode} timeout_seconds={CommandTimeoutSeconds} stage={Stage} combo_id={ComboId} elapsed_ms={ElapsedMs} row_count={RowCount}",
            bandType,
            options.WriteMode,
            options.CommandTimeoutSeconds,
            stage,
            comboId ?? "-",
            elapsedMs,
            rowCount?.ToString() ?? "-");
    }

    private static BandTeamRankingRebuildOptions ResolveBandTeamRankingRebuildOptions(BandTeamRankingRebuildOptions? options)
    {
        var resolved = options ?? BandTeamRankingRebuildOptions.Default;
        if (resolved.CommandTimeoutSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "CommandTimeoutSeconds must be zero or greater.");

        return resolved;
    }

    private static void ConfigureBandRebuildCommand(NpgsqlCommand cmd, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options)
    {
        cmd.Transaction = tx;
        cmd.CommandTimeout = options.CommandTimeoutSeconds;
    }

    private static long CreateBandRankingGeneration(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        BandTeamRankingRebuildOptions options,
        string bandType,
        DateTime computedAt)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = @"
            INSERT INTO band_team_ranking_generation (band_type, status, computed_at)
            VALUES (@bandType, 'building', @computedAt)
            RETURNING generation_id;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("computedAt", computedAt);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void CompleteBandRankingGeneration(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        BandTeamRankingRebuildOptions options,
        long generationId,
        string bandType,
        int rowCount,
        int scopeCount)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = @"
            UPDATE band_team_ranking_generation
            SET status = 'published',
                published_at = now(),
                ranking_table = @rankingTable,
                stats_table = @statsTable,
                row_count = @rowCount,
                scope_count = @scopeCount,
                updated_at = now()
            WHERE generation_id = @generationId;";
        cmd.Parameters.AddWithValue("generationId", generationId);
        cmd.Parameters.AddWithValue("rankingTable", BandRankingStorageNames.GetCurrentRankingTable(bandType));
        cmd.Parameters.AddWithValue("statsTable", BandRankingStorageNames.GetCurrentStatsTable(bandType));
        cmd.Parameters.AddWithValue("rowCount", rowCount);
        cmd.Parameters.AddWithValue("scopeCount", scopeCount);
        cmd.ExecuteNonQuery();
    }

    private static void MaterializeBandTeamRankingResultsMonolithic(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        BandTeamRankingRebuildOptions options,
        string bandType,
        int totalChartedSongs,
        int expectedMembers,
        int credibilityThreshold,
        double populationMedian,
        DateTime computedAt)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = @"
                CREATE TEMP TABLE _band_rank_results ON COMMIT DROP AS
                WITH NormalizedEntries AS (
                    SELECT
                        be.song_id,
                        be.team_key,
                        be.score,
                        COALESCE(be.accuracy, 0) AS accuracy,
                        COALESCE(be.is_full_combo, FALSE) AS is_full_combo,
                        COALESCE(be.stars, 0) AS stars,
                        COALESCE(be.end_time, '') AS end_time,
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
                        ), '') AS combo_id
                    FROM band_entries be
                    WHERE be.band_type = @bandType
                      AND NOT be.is_over_threshold
                ),
                OverallChoice AS (
                    SELECT *
                    FROM (
                        SELECT
                            ne.*,
                            ROW_NUMBER() OVER (
                                PARTITION BY ne.song_id, ne.team_key
                                ORDER BY ne.score DESC, ne.end_time ASC, ne.combo_id ASC, ne.team_key ASC
                            ) AS choice_rank
                        FROM NormalizedEntries ne
                    ) ranked
                    WHERE choice_rank = 1
                ),
                OverallValidEntries AS (
                    SELECT
                        oc.song_id,
                        oc.team_key,
                        oc.score,
                        oc.accuracy,
                        oc.is_full_combo,
                        oc.stars,
                        COUNT(*) OVER (PARTITION BY oc.song_id) AS entry_count,
                        CASE
                            WHEN COUNT(*) OVER (PARTITION BY oc.song_id) > 0
                                THEN LN(COUNT(*) OVER (PARTITION BY oc.song_id)::DOUBLE PRECISION) / LN(2.0)
                            ELSE 0.0
                        END AS log_weight,
                        ROW_NUMBER() OVER (
                            PARTITION BY oc.song_id
                            ORDER BY oc.score DESC, oc.end_time ASC, oc.team_key ASC
                        ) AS effective_rank
                    FROM OverallChoice oc
                ),
                OverallAggregated AS (
                    SELECT
                        'overall'::TEXT AS ranking_scope,
                        ''::TEXT AS combo_id,
                        team_key,
                        COUNT(*) AS songs_played,
                        @totalCharted AS total_charted_songs,
                        COUNT(*)::DOUBLE PRECISION / @totalCharted AS coverage,
                        AVG(effective_rank::DOUBLE PRECISION / entry_count) AS raw_skill_rating,
                        SUM((effective_rank::DOUBLE PRECISION / entry_count) * log_weight) / NULLIF(SUM(log_weight), 0) AS raw_weighted_rating,
                        SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END)::DOUBLE PRECISION / @totalCharted AS fc_rate,
                        SUM(score)::BIGINT AS total_score,
                        COALESCE(AVG(accuracy::DOUBLE PRECISION), 0.0) AS avg_accuracy,
                        SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END) AS full_combo_count,
                        COALESCE(AVG(stars::DOUBLE PRECISION), 0.0) AS avg_stars,
                        MIN(effective_rank) AS best_rank,
                        AVG(effective_rank::DOUBLE PRECISION) AS avg_rank
                    FROM OverallValidEntries
                    GROUP BY team_key
                ),
                OverallWithBayesian AS (
                    SELECT *,
                        (songs_played * raw_skill_rating + @m * @c) / (songs_played + @m) AS adjusted_skill_rating,
                        (songs_played * COALESCE(raw_weighted_rating, 1.0) + @m * @c) / (songs_played + @m) AS adjusted_weighted_rating
                    FROM OverallAggregated
                ),
                OverallRanked AS (
                    SELECT *,
                        ROW_NUMBER() OVER (ORDER BY adjusted_skill_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, team_key ASC) AS adjusted_skill_rank,
                        ROW_NUMBER() OVER (ORDER BY adjusted_weighted_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, team_key ASC) AS weighted_rank,
                        ROW_NUMBER() OVER (ORDER BY fc_rate DESC, total_score DESC, songs_played DESC, adjusted_skill_rating ASC, team_key ASC) AS fc_rate_rank,
                        ROW_NUMBER() OVER (ORDER BY total_score DESC, songs_played DESC, adjusted_skill_rating ASC, team_key ASC) AS total_score_rank
                    FROM OverallWithBayesian
                ),
                ComboValidEntries AS (
                    SELECT
                        ne.combo_id,
                        ne.song_id,
                        ne.team_key,
                        ne.score,
                        ne.accuracy,
                        ne.is_full_combo,
                        ne.stars,
                        COUNT(*) OVER (PARTITION BY ne.combo_id, ne.song_id) AS entry_count,
                        CASE
                            WHEN COUNT(*) OVER (PARTITION BY ne.combo_id, ne.song_id) > 0
                                THEN LN(COUNT(*) OVER (PARTITION BY ne.combo_id, ne.song_id)::DOUBLE PRECISION) / LN(2.0)
                            ELSE 0.0
                        END AS log_weight,
                        ROW_NUMBER() OVER (
                            PARTITION BY ne.combo_id, ne.song_id
                            ORDER BY ne.score DESC, ne.end_time ASC, ne.team_key ASC
                        ) AS effective_rank
                    FROM NormalizedEntries ne
                    WHERE ne.combo_id <> ''
                      AND array_length(string_to_array(ne.combo_id, '+'), 1) = @expectedMembers
                ),
                ComboAggregated AS (
                    SELECT
                        'combo'::TEXT AS ranking_scope,
                        combo_id,
                        team_key,
                        COUNT(*) AS songs_played,
                        @totalCharted AS total_charted_songs,
                        COUNT(*)::DOUBLE PRECISION / @totalCharted AS coverage,
                        AVG(effective_rank::DOUBLE PRECISION / entry_count) AS raw_skill_rating,
                        SUM((effective_rank::DOUBLE PRECISION / entry_count) * log_weight) / NULLIF(SUM(log_weight), 0) AS raw_weighted_rating,
                        SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END)::DOUBLE PRECISION / @totalCharted AS fc_rate,
                        SUM(score)::BIGINT AS total_score,
                        COALESCE(AVG(accuracy::DOUBLE PRECISION), 0.0) AS avg_accuracy,
                        SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END) AS full_combo_count,
                        COALESCE(AVG(stars::DOUBLE PRECISION), 0.0) AS avg_stars,
                        MIN(effective_rank) AS best_rank,
                        AVG(effective_rank::DOUBLE PRECISION) AS avg_rank
                    FROM ComboValidEntries
                    GROUP BY combo_id, team_key
                ),
                ComboWithBayesian AS (
                    SELECT *,
                        (songs_played * raw_skill_rating + @m * @c) / (songs_played + @m) AS adjusted_skill_rating,
                        (songs_played * COALESCE(raw_weighted_rating, 1.0) + @m * @c) / (songs_played + @m) AS adjusted_weighted_rating
                    FROM ComboAggregated
                ),
                ComboRanked AS (
                    SELECT *,
                        ROW_NUMBER() OVER (PARTITION BY combo_id ORDER BY adjusted_skill_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, team_key ASC) AS adjusted_skill_rank,
                        ROW_NUMBER() OVER (PARTITION BY combo_id ORDER BY adjusted_weighted_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, team_key ASC) AS weighted_rank,
                        ROW_NUMBER() OVER (PARTITION BY combo_id ORDER BY fc_rate DESC, total_score DESC, songs_played DESC, adjusted_skill_rating ASC, team_key ASC) AS fc_rate_rank,
                        ROW_NUMBER() OVER (PARTITION BY combo_id ORDER BY total_score DESC, songs_played DESC, adjusted_skill_rating ASC, team_key ASC) AS total_score_rank
                    FROM ComboWithBayesian
                )
                SELECT
                    @bandType AS band_type,
                    ranking_scope,
                    combo_id,
                    team_key,
                    string_to_array(team_key, ':') AS team_members,
                    songs_played,
                    total_charted_songs,
                    coverage,
                    raw_skill_rating,
                    adjusted_skill_rating,
                    adjusted_skill_rank,
                    adjusted_weighted_rating AS weighted_rating,
                    weighted_rank,
                    fc_rate,
                    fc_rate_rank,
                    total_score,
                    total_score_rank,
                    avg_accuracy,
                    full_combo_count,
                    avg_stars,
                    best_rank,
                    avg_rank,
                    raw_weighted_rating,
                    @now AS computed_at
                FROM OverallRanked
                UNION ALL
                SELECT
                    @bandType AS band_type,
                    ranking_scope,
                    combo_id,
                    team_key,
                    string_to_array(team_key, ':') AS team_members,
                    songs_played,
                    total_charted_songs,
                    coverage,
                    raw_skill_rating,
                    adjusted_skill_rating,
                    adjusted_skill_rank,
                    adjusted_weighted_rating AS weighted_rating,
                    weighted_rank,
                    fc_rate,
                    fc_rate_rank,
                    total_score,
                    total_score_rank,
                    avg_accuracy,
                    full_combo_count,
                    avg_stars,
                    best_rank,
                    avg_rank,
                    raw_weighted_rating,
                    @now AS computed_at
                FROM ComboRanked;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("totalCharted", totalChartedSongs);
        cmd.Parameters.AddWithValue("expectedMembers", expectedMembers);
        cmd.Parameters.AddWithValue("m", credibilityThreshold);
        cmd.Parameters.AddWithValue("c", populationMedian);
        cmd.Parameters.AddWithValue("now", computedAt);
        cmd.ExecuteNonQuery();
    }

    private void MaterializeBandTeamRankingResultsPhased(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        BandTeamRankingRebuildOptions options,
        string bandType,
        int totalChartedSongs,
        int expectedMembers,
        int credibilityThreshold,
        double populationMedian,
        DateTime computedAt,
        ref string currentStage)
    {
        currentStage = "materialize_source";
        var sourceSw = Stopwatch.StartNew();
        using (var cmd = conn.CreateCommand())
        {
            ConfigureBandRebuildCommand(cmd, tx, options);
            cmd.CommandText = @"
                CREATE TEMP TABLE _band_rank_source ON COMMIT DROP AS
                SELECT
                    be.song_id,
                    be.team_key,
                    be.score,
                    COALESCE(be.accuracy, 0) AS accuracy,
                    COALESCE(be.is_full_combo, FALSE) AS is_full_combo,
                    COALESCE(be.stars, 0) AS stars,
                    COALESCE(be.end_time, '') AS end_time,
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
                    ), '') AS combo_id
                FROM band_entries be
                WHERE be.band_type = @bandType
                  AND NOT be.is_over_threshold;";
            cmd.Parameters.AddWithValue("bandType", bandType);
            cmd.ExecuteNonQuery();
        }
        sourceSw.Stop();
        LogBandRebuildStage(bandType, options, "materialize_source", RoundElapsed(sourceSw));

        currentStage = "index_source";
        var indexSw = Stopwatch.StartNew();
        using (var cmd = conn.CreateCommand())
        {
            ConfigureBandRebuildCommand(cmd, tx, options);
            cmd.CommandText = @"
                CREATE INDEX _band_rank_source_overall_idx ON _band_rank_source (song_id, team_key, score DESC, end_time ASC, combo_id ASC);
                CREATE INDEX _band_rank_source_combo_idx ON _band_rank_source (combo_id, song_id, score DESC, end_time ASC, team_key ASC);
                ANALYZE _band_rank_source;";
            cmd.ExecuteNonQuery();
        }
        indexSw.Stop();
        LogBandRebuildStage(bandType, options, "index_source", RoundElapsed(indexSw));

        currentStage = "create_results_stage";
        var createResultsSw = Stopwatch.StartNew();
        CreateEmptyBandRankResultsTable(conn, tx, options);
        createResultsSw.Stop();
        LogBandRebuildStage(bandType, options, "create_results_stage", RoundElapsed(createResultsSw));

        currentStage = "materialize_overall_phase";
        var overallSw = Stopwatch.StartNew();
        var overallRows = InsertBandRankOverallPhase(conn, tx, options, bandType, totalChartedSongs, credibilityThreshold, populationMedian, computedAt);
        overallSw.Stop();
        LogBandRebuildStage(bandType, options, "materialize_overall_phase", RoundElapsed(overallSw), overallRows);

        currentStage = "load_combo_catalog";
        var comboCatalogSw = Stopwatch.StartNew();
        var comboIds = LoadBandRankSourceComboIds(conn, tx, options, expectedMembers);
        comboCatalogSw.Stop();
        LogBandRebuildStage(bandType, options, "load_combo_catalog", RoundElapsed(comboCatalogSw), comboIds.Count);

        currentStage = "materialize_combo_phases";
        var comboSw = Stopwatch.StartNew();
        var comboRows = 0;
        foreach (var comboId in comboIds)
        {
            currentStage = $"materialize_combo_phase:{comboId}";
            var comboPhaseSw = Stopwatch.StartNew();
            var comboPhaseRows = InsertBandRankComboPhase(conn, tx, options, bandType, comboId, totalChartedSongs, credibilityThreshold, populationMedian, computedAt);
            comboPhaseSw.Stop();
            comboRows += comboPhaseRows;
            LogBandRebuildStage(bandType, options, "materialize_combo_phase", RoundElapsed(comboPhaseSw), comboPhaseRows, comboId);
        }
        comboSw.Stop();
        currentStage = "materialize_combo_phases";
        LogBandRebuildStage(bandType, options, "materialize_combo_phases", RoundElapsed(comboSw), comboRows);
    }

    private static void CreateEmptyBandRankResultsTable(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = BandRankingStorageNames.GetCreateRankingTableSql(
            "_band_rank_results",
            includePrimaryKey: false,
            temporary: true,
            onCommitDrop: true);
        cmd.ExecuteNonQuery();
    }

    private static int InsertBandRankOverallPhase(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        BandTeamRankingRebuildOptions options,
        string bandType,
        int totalChartedSongs,
        int credibilityThreshold,
        double populationMedian,
        DateTime computedAt)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = @"
            INSERT INTO _band_rank_results (
                band_type, ranking_scope, combo_id, team_key, team_members,
                songs_played, total_charted_songs, coverage, raw_skill_rating,
                adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating, computed_at)
            WITH OverallChoice AS (
                SELECT *
                FROM (
                    SELECT
                        src.*,
                        ROW_NUMBER() OVER (
                            PARTITION BY src.song_id, src.team_key
                            ORDER BY src.score DESC, src.end_time ASC, src.combo_id ASC, src.team_key ASC
                        ) AS choice_rank
                    FROM _band_rank_source src
                ) ranked
                WHERE choice_rank = 1
            ),
            OverallValidEntries AS (
                SELECT
                    oc.song_id,
                    oc.team_key,
                    oc.score,
                    oc.accuracy,
                    oc.is_full_combo,
                    oc.stars,
                    COUNT(*) OVER (PARTITION BY oc.song_id) AS entry_count,
                    CASE
                        WHEN COUNT(*) OVER (PARTITION BY oc.song_id) > 0
                            THEN LN(COUNT(*) OVER (PARTITION BY oc.song_id)::DOUBLE PRECISION) / LN(2.0)
                        ELSE 0.0
                    END AS log_weight,
                    ROW_NUMBER() OVER (
                        PARTITION BY oc.song_id
                        ORDER BY oc.score DESC, oc.end_time ASC, oc.team_key ASC
                    ) AS effective_rank
                FROM OverallChoice oc
            ),
            OverallAggregated AS (
                SELECT
                    team_key,
                    COUNT(*) AS songs_played,
                    @totalCharted AS total_charted_songs,
                    COUNT(*)::DOUBLE PRECISION / @totalCharted AS coverage,
                    AVG(effective_rank::DOUBLE PRECISION / entry_count) AS raw_skill_rating,
                    SUM((effective_rank::DOUBLE PRECISION / entry_count) * log_weight) / NULLIF(SUM(log_weight), 0) AS raw_weighted_rating,
                    SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END)::DOUBLE PRECISION / @totalCharted AS fc_rate,
                    SUM(score)::BIGINT AS total_score,
                    COALESCE(AVG(accuracy::DOUBLE PRECISION), 0.0) AS avg_accuracy,
                    SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END) AS full_combo_count,
                    COALESCE(AVG(stars::DOUBLE PRECISION), 0.0) AS avg_stars,
                    MIN(effective_rank) AS best_rank,
                    AVG(effective_rank::DOUBLE PRECISION) AS avg_rank
                FROM OverallValidEntries
                GROUP BY team_key
            ),
            OverallWithBayesian AS (
                SELECT *,
                    (songs_played * raw_skill_rating + @m * @c) / (songs_played + @m) AS adjusted_skill_rating,
                    (songs_played * COALESCE(raw_weighted_rating, 1.0) + @m * @c) / (songs_played + @m) AS adjusted_weighted_rating
                FROM OverallAggregated
            ),
            OverallRanked AS (
                SELECT *,
                    ROW_NUMBER() OVER (ORDER BY adjusted_skill_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, team_key ASC) AS adjusted_skill_rank,
                    ROW_NUMBER() OVER (ORDER BY adjusted_weighted_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, team_key ASC) AS weighted_rank,
                    ROW_NUMBER() OVER (ORDER BY fc_rate DESC, total_score DESC, songs_played DESC, adjusted_skill_rating ASC, team_key ASC) AS fc_rate_rank,
                    ROW_NUMBER() OVER (ORDER BY total_score DESC, songs_played DESC, adjusted_skill_rating ASC, team_key ASC) AS total_score_rank
                FROM OverallWithBayesian
            )
            SELECT
                @bandType AS band_type,
                'overall'::TEXT AS ranking_scope,
                ''::TEXT AS combo_id,
                team_key,
                string_to_array(team_key, ':') AS team_members,
                songs_played,
                total_charted_songs,
                coverage,
                raw_skill_rating,
                adjusted_skill_rating,
                adjusted_skill_rank,
                adjusted_weighted_rating AS weighted_rating,
                weighted_rank,
                fc_rate,
                fc_rate_rank,
                total_score,
                total_score_rank,
                avg_accuracy,
                full_combo_count,
                avg_stars,
                best_rank,
                avg_rank,
                raw_weighted_rating,
                @now AS computed_at
            FROM OverallRanked;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("totalCharted", totalChartedSongs);
        cmd.Parameters.AddWithValue("m", credibilityThreshold);
        cmd.Parameters.AddWithValue("c", populationMedian);
        cmd.Parameters.AddWithValue("now", computedAt);
        return cmd.ExecuteNonQuery();
    }

    private static List<string> LoadBandRankSourceComboIds(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, int expectedMembers)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = @"
            SELECT combo_id
            FROM _band_rank_source
            WHERE combo_id <> ''
              AND array_length(string_to_array(combo_id, '+'), 1) = @expectedMembers
            GROUP BY combo_id
            ORDER BY combo_id;";
        cmd.Parameters.AddWithValue("expectedMembers", expectedMembers);

        var comboIds = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            comboIds.Add(reader.GetString(0));

        return comboIds;
    }

    private static int InsertBandRankComboPhase(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        BandTeamRankingRebuildOptions options,
        string bandType,
        string comboId,
        int totalChartedSongs,
        int credibilityThreshold,
        double populationMedian,
        DateTime computedAt)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = @"
            INSERT INTO _band_rank_results (
                band_type, ranking_scope, combo_id, team_key, team_members,
                songs_played, total_charted_songs, coverage, raw_skill_rating,
                adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating, computed_at)
            WITH ComboValidEntries AS (
                SELECT
                    src.song_id,
                    src.team_key,
                    src.score,
                    src.accuracy,
                    src.is_full_combo,
                    src.stars,
                    COUNT(*) OVER (PARTITION BY src.song_id) AS entry_count,
                    CASE
                        WHEN COUNT(*) OVER (PARTITION BY src.song_id) > 0
                            THEN LN(COUNT(*) OVER (PARTITION BY src.song_id)::DOUBLE PRECISION) / LN(2.0)
                        ELSE 0.0
                    END AS log_weight,
                    ROW_NUMBER() OVER (
                        PARTITION BY src.song_id
                        ORDER BY src.score DESC, src.end_time ASC, src.team_key ASC
                    ) AS effective_rank
                FROM _band_rank_source src
                WHERE src.combo_id = @comboId
            ),
            ComboAggregated AS (
                SELECT
                    team_key,
                    COUNT(*) AS songs_played,
                    @totalCharted AS total_charted_songs,
                    COUNT(*)::DOUBLE PRECISION / @totalCharted AS coverage,
                    AVG(effective_rank::DOUBLE PRECISION / entry_count) AS raw_skill_rating,
                    SUM((effective_rank::DOUBLE PRECISION / entry_count) * log_weight) / NULLIF(SUM(log_weight), 0) AS raw_weighted_rating,
                    SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END)::DOUBLE PRECISION / @totalCharted AS fc_rate,
                    SUM(score)::BIGINT AS total_score,
                    COALESCE(AVG(accuracy::DOUBLE PRECISION), 0.0) AS avg_accuracy,
                    SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END) AS full_combo_count,
                    COALESCE(AVG(stars::DOUBLE PRECISION), 0.0) AS avg_stars,
                    MIN(effective_rank) AS best_rank,
                    AVG(effective_rank::DOUBLE PRECISION) AS avg_rank
                FROM ComboValidEntries
                GROUP BY team_key
            ),
            ComboWithBayesian AS (
                SELECT *,
                    (songs_played * raw_skill_rating + @m * @c) / (songs_played + @m) AS adjusted_skill_rating,
                    (songs_played * COALESCE(raw_weighted_rating, 1.0) + @m * @c) / (songs_played + @m) AS adjusted_weighted_rating
                FROM ComboAggregated
            ),
            ComboRanked AS (
                SELECT *,
                    ROW_NUMBER() OVER (ORDER BY adjusted_skill_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, team_key ASC) AS adjusted_skill_rank,
                    ROW_NUMBER() OVER (ORDER BY adjusted_weighted_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, team_key ASC) AS weighted_rank,
                    ROW_NUMBER() OVER (ORDER BY fc_rate DESC, total_score DESC, songs_played DESC, adjusted_skill_rating ASC, team_key ASC) AS fc_rate_rank,
                    ROW_NUMBER() OVER (ORDER BY total_score DESC, songs_played DESC, adjusted_skill_rating ASC, team_key ASC) AS total_score_rank
                FROM ComboWithBayesian
            )
            SELECT
                @bandType AS band_type,
                'combo'::TEXT AS ranking_scope,
                @comboId AS combo_id,
                team_key,
                string_to_array(team_key, ':') AS team_members,
                songs_played,
                total_charted_songs,
                coverage,
                raw_skill_rating,
                adjusted_skill_rating,
                adjusted_skill_rank,
                adjusted_weighted_rating AS weighted_rating,
                weighted_rank,
                fc_rate,
                fc_rate_rank,
                total_score,
                total_score_rank,
                avg_accuracy,
                full_combo_count,
                avg_stars,
                best_rank,
                avg_rank,
                raw_weighted_rating,
                @now AS computed_at
            FROM ComboRanked;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("comboId", comboId);
        cmd.Parameters.AddWithValue("totalCharted", totalChartedSongs);
        cmd.Parameters.AddWithValue("m", credibilityThreshold);
        cmd.Parameters.AddWithValue("c", populationMedian);
        cmd.Parameters.AddWithValue("now", computedAt);
        return cmd.ExecuteNonQuery();
    }

    private static string BuildBandTeamRankingInsertSql(string targetTable, string whereClause, string orderByClause) => $@"
                INSERT INTO {BandRankingStorageNames.QuoteIdentifier(targetTable)} (
                    band_type, ranking_scope, combo_id, team_key, team_members,
                    songs_played, total_charted_songs, coverage, raw_skill_rating,
                    adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                    fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                    full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating, computed_at,
                    ranking_generation, row_fingerprint)
                SELECT
                    source.band_type, source.ranking_scope, source.combo_id, source.team_key, source.team_members,
                    source.songs_played, source.total_charted_songs, source.coverage, source.raw_skill_rating,
                    source.adjusted_skill_rating, source.adjusted_skill_rank, source.weighted_rating, source.weighted_rank,
                    source.fc_rate, source.fc_rate_rank, source.total_score, source.total_score_rank, source.avg_accuracy,
                    source.full_combo_count, source.avg_stars, source.best_rank, source.avg_rank, source.raw_weighted_rating, source.computed_at,
                    @rankingGeneration, {BandRankHistoryFingerprintExpression("source")}
                FROM _band_rank_results source
                {whereClause}
                {orderByClause};";

    private static int InsertBandTeamRankingRowsMonolithic(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string targetTable, long rankingGeneration)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = BuildBandTeamRankingInsertSql(targetTable, string.Empty, "ORDER BY ranking_scope, combo_id, team_key");
        cmd.Parameters.AddWithValue("rankingGeneration", rankingGeneration);
        return cmd.ExecuteNonQuery();
    }

    private static int InsertBandTeamRankingRowsComboBatched(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string targetTable, long rankingGeneration)
    {
        var insertedRows = 0;

        using (var cmd = conn.CreateCommand())
        {
            ConfigureBandRebuildCommand(cmd, tx, options);
            cmd.CommandText = BuildBandTeamRankingInsertSql(targetTable, "WHERE ranking_scope = 'overall'", "ORDER BY team_key");
            cmd.Parameters.AddWithValue("rankingGeneration", rankingGeneration);
            insertedRows += cmd.ExecuteNonQuery();
        }

        var comboIds = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            ConfigureBandRebuildCommand(cmd, tx, options);
            cmd.CommandText = @"
                SELECT combo_id
                FROM _band_rank_results
                WHERE ranking_scope = 'combo'
                GROUP BY combo_id
                ORDER BY combo_id;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                comboIds.Add(reader.GetString(0));
        }

        if (comboIds.Count == 0)
            return insertedRows;

        using var insertCmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(insertCmd, tx, options);
        insertCmd.CommandText = BuildBandTeamRankingInsertSql(targetTable, "WHERE ranking_scope = 'combo' AND combo_id = @comboId", "ORDER BY team_key");
        insertCmd.Parameters.AddWithValue("rankingGeneration", rankingGeneration);
        var comboIdParam = insertCmd.Parameters.Add("comboId", NpgsqlDbType.Text);

        foreach (var comboId in comboIds)
        {
            comboIdParam.Value = comboId;
            insertedRows += insertCmd.ExecuteNonQuery();
        }

        return insertedRows;
    }

    private static int InsertBandTeamRankingStatsRows(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string targetTable)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = $@"
                INSERT INTO {BandRankingStorageNames.QuoteIdentifier(targetTable)} (band_type, ranking_scope, combo_id, total_teams, computed_at)
                SELECT band_type, ranking_scope, combo_id, COUNT(*), MAX(computed_at)
                FROM _band_rank_results
                GROUP BY band_type, ranking_scope, combo_id;";
        return cmd.ExecuteNonQuery();
    }

    private static string CreateBandRankingBuildTable(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string bandType, string buildSuffix)
    {
        var tableName = $"band_team_rankings_build_{bandType.ToLowerInvariant()}_{buildSuffix}".Replace('-', '_');
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = BandRankingStorageNames.GetCreateRankingTableSql(tableName, includePrimaryKey: false);
        cmd.ExecuteNonQuery();
        return tableName;
    }

    private static string CreateBandRankingStatsBuildTable(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string bandType, string buildSuffix)
    {
        var tableName = $"band_team_ranking_stats_build_{bandType.ToLowerInvariant()}_{buildSuffix}".Replace('-', '_');
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = BandRankingStorageNames.GetCreateStatsTableSql(tableName, includePrimaryKey: false);
        cmd.ExecuteNonQuery();
        return tableName;
    }

    private static void CreateBandRankingIndexes(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string tableName)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        var quotedTable = BandRankingStorageNames.QuoteIdentifier(tableName);
        cmd.CommandText = $@"
                CREATE UNIQUE INDEX {BandRankingStorageNames.QuoteIdentifier(tableName + "_pkey")} ON {quotedTable} (band_type, ranking_scope, combo_id, team_key);
                CREATE INDEX {BandRankingStorageNames.QuoteIdentifier(tableName + "_ix_adjusted")} ON {quotedTable} (band_type, ranking_scope, combo_id, adjusted_skill_rank);
                CREATE INDEX {BandRankingStorageNames.QuoteIdentifier(tableName + "_ix_weighted")} ON {quotedTable} (band_type, ranking_scope, combo_id, weighted_rank);
                CREATE INDEX {BandRankingStorageNames.QuoteIdentifier(tableName + "_ix_fcrate")} ON {quotedTable} (band_type, ranking_scope, combo_id, fc_rate_rank);
                CREATE INDEX {BandRankingStorageNames.QuoteIdentifier(tableName + "_ix_totalscore")} ON {quotedTable} (band_type, ranking_scope, combo_id, total_score_rank);";
        cmd.ExecuteNonQuery();
    }

    private static void CreateBandRankingStatsIndexes(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string tableName)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = $"CREATE UNIQUE INDEX {BandRankingStorageNames.QuoteIdentifier(tableName + "_pkey")} ON {BandRankingStorageNames.QuoteIdentifier(tableName)} (band_type, ranking_scope, combo_id);";
        cmd.ExecuteNonQuery();
    }

    private static void SwapBandCurrentTables(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string bandType, string buildRankingTable, string buildStatsTable, string buildSuffix)
    {
        var currentRankingTable = BandRankingStorageNames.GetCurrentRankingTable(bandType);
        var currentStatsTable = BandRankingStorageNames.GetCurrentStatsTable(bandType);
        var backupRankingTable = $"{currentRankingTable}_old_{buildSuffix}";
        var backupStatsTable = $"{currentStatsTable}_old_{buildSuffix}";
        var statements = new List<string>();

        if (TableExists(conn, tx, currentRankingTable))
            statements.Add($"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(currentRankingTable)} RENAME TO {BandRankingStorageNames.QuoteIdentifier(backupRankingTable)}");

        if (TableExists(conn, tx, currentStatsTable))
            statements.Add($"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(currentStatsTable)} RENAME TO {BandRankingStorageNames.QuoteIdentifier(backupStatsTable)}");

        statements.Add($"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(buildRankingTable)} RENAME TO {BandRankingStorageNames.QuoteIdentifier(currentRankingTable)}");
        statements.Add($"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(buildStatsTable)} RENAME TO {BandRankingStorageNames.QuoteIdentifier(currentStatsTable)}");

        // The backup tables were just created by the RENAMEs above in this same
        // batch, so TableExists() executed before the batch cannot see them.
        // Use IF EXISTS so Postgres evaluates existence at statement time and
        // drops the backup regardless of whether the first RENAME ran (no-op on
        // first-ever build when currentRankingTable did not exist).
        statements.Add($"DROP TABLE IF EXISTS {BandRankingStorageNames.QuoteIdentifier(backupRankingTable)}");
        statements.Add($"DROP TABLE IF EXISTS {BandRankingStorageNames.QuoteIdentifier(backupStatsTable)}");

        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = string.Join(";\n", statements) + ";";
        cmd.ExecuteNonQuery();
    }

    private static double RoundElapsed(Stopwatch sw) => Math.Round(sw.Elapsed.TotalMilliseconds, 3);

    public void SnapshotBandRankHistory(string bandType, int retentionDays = 365)
    {
        SnapshotBandRankHistoryChunked(bandType, new BandRankHistorySnapshotOptions
        {
            UseLatestState = true,
            UseNarrowHistory = true,
            UseWideHistoryCompatibilityWrite = true,
            RetentionDays = retentionDays,
        });
    }

    public BandRankHistorySnapshotResult SnapshotBandRankHistoryChunked(
        string bandType,
        BandRankHistorySnapshotOptions options,
        long? jobId = null,
        CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        using var conn = _ds.OpenConnection();
        EnsureBandRankHistoryPollingSchema(conn);

        var rankingsTable = ResolveBandRankingReadTable(conn, bandType);
        var statsTable = ResolveBandRankingStatsReadTable(conn, bandType);

        if (options.UseLatestState)
            SeedBandRankHistoryLatestState(conn, bandType, options.CommandTimeoutSeconds, ct);

        var chunks = jobId.HasValue
            ? EnsureAndGetBandRankHistoryJobChunks(conn, jobId.Value, bandType, rankingsTable, statsTable, options, options.CommandTimeoutSeconds)
            : GetBandRankHistoryChunks(conn, bandType, rankingsTable, statsTable, options, options.CommandTimeoutSeconds)
                .Select(chunk => new BandRankHistoryChunkInfo
                {
                    JobId = 0,
                    BandType = bandType,
                    RankingScope = chunk.RankingScope,
                    ComboId = chunk.ComboId,
                    ChunkOrdinal = chunk.ChunkOrdinal,
                    TeamKeyStart = chunk.TeamKeyStart,
                    TeamKeyEnd = chunk.TeamKeyEnd,
                    EstimatedRows = chunk.EstimatedRows,
                    SourceGeneration = chunk.SourceGeneration,
                    Status = "queued",
                })
                .ToList();

        long scanned = 0;
        long inserted = 0;
        int completed = 0;
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            if (jobId.HasValue)
                MarkBandRankHistoryChunkRunning(conn, jobId.Value, chunk, options.CommandTimeoutSeconds);

            var chunkResult = SnapshotBandRankHistoryChunk(
                conn,
                rankingsTable,
                statsTable,
                bandType,
                chunk.RankingScope,
                chunk.ComboId,
                chunk.TeamKeyStart,
                chunk.TeamKeyEnd,
                chunk.SourceGeneration,
                today,
                options,
                ct);

            scanned += chunkResult.RowsScanned;
            inserted += chunkResult.RowsInserted;
            completed++;

            if (jobId.HasValue)
                CompleteBandRankHistoryChunk(
                    conn,
                    jobId.Value,
                    chunk,
                    chunkResult.RowsScanned,
                    chunkResult.RowsInserted,
                    Math.Max(0, chunkResult.RowsScanned - chunkResult.RowsInserted),
                    options.CommandTimeoutSeconds);
        }

        if (options.CleanupRetention)
            CleanupBandRankHistoryRetention(conn, bandType, options.RetentionDays, options.CommandTimeoutSeconds, ct);

        if (jobId.HasValue)
            return ReadBandRankHistoryJobSnapshotResult(conn, jobId.Value, options.CommandTimeoutSeconds);

        return new BandRankHistorySnapshotResult
        {
            RowsScanned = scanned,
            RowsInserted = inserted,
            RowsSkipped = Math.Max(0, scanned - inserted),
            ChunksCompleted = completed,
            ChunksTotal = chunks.Count,
        };
    }

    public BandRankHistoryV2BackfillResult BackfillBandRankHistoryV2FromLegacy(
        string bandType,
        BandRankHistoryV2BackfillOptions options,
        CancellationToken ct = default)
    {
        using var conn = _ds.OpenConnection();
        using (var tx = conn.BeginTransaction())
        {
            EnsureBandRankHistoryTables(conn, tx);
            tx.Commit();
        }

        var slices = ReadBandRankHistoryV2BackfillSlices(conn, bandType, options, ct);
        var resultSlices = new List<BandRankHistoryV2BackfillSlice>(slices.Count);

        foreach (var slice in slices)
        {
            ct.ThrowIfCancellationRequested();
            if (!options.Execute || (slice.MissingV2Rows <= 0 && slice.CompleteSnapshots > 0))
            {
                resultSlices.Add(slice.ToDto());
                continue;
            }

            resultSlices.Add(BackfillBandRankHistoryV2Slice(conn, slice, options, ct));
        }

        return new BandRankHistoryV2BackfillResult
        {
            BandType = bandType,
            StartDate = options.StartDate?.ToString("yyyy-MM-dd"),
            EndDate = options.EndDate?.ToString("yyyy-MM-dd"),
            Execute = options.Execute,
            LegacyRows = resultSlices.Sum(static slice => slice.LegacyRows),
            ExistingV2Rows = resultSlices.Sum(static slice => slice.ExistingV2Rows),
            MissingV2Rows = resultSlices.Sum(static slice => slice.MissingV2Rows),
            SnapshotRowsUpserted = resultSlices.Sum(static slice => slice.SnapshotRowsUpserted),
            PointRowsInserted = resultSlices.Sum(static slice => slice.PointRowsInserted),
            LatestRowsUpserted = resultSlices.Sum(static slice => slice.LatestRowsUpserted),
            SlicesTotal = resultSlices.Count,
            SlicesBackfilled = resultSlices.Count(static slice => slice.SnapshotRowsUpserted > 0 || slice.PointRowsInserted > 0 || slice.LatestRowsUpserted > 0),
            Slices = resultSlices,
        };
    }

    public BandRankHistoryWideNarrowParitySummary GetBandRankHistoryWideNarrowParity(
        string bandType,
        DateOnly snapshotDate,
        string? rankingScope = null,
        string? comboId = null,
        int sampleLimit = 10,
        bool ensureSchema = true)
    {
        using var conn = _ds.OpenConnection();
        if (ensureSchema)
        {
            using var tx = conn.BeginTransaction();
            EnsureBandRankHistoryTables(conn, tx);
            tx.Commit();
        }

        using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                        WITH wide_rows AS (
                                SELECT count(*)::bigint AS row_count
                                FROM band_team_rank_history w
                                WHERE w.band_type = @bandType
                                    AND w.snapshot_date = @snapshotDate
                                    AND (@scope IS NULL OR w.ranking_scope = @scope)
                                    AND (@comboId IS NULL OR w.combo_id = @comboId)
                        ), narrow_rows AS (
                                SELECT count(*)::bigint AS row_count
                                FROM band_team_rank_history_points n
                                WHERE n.band_type = @bandType
                                    AND n.snapshot_date = @snapshotDate
                                    AND (@scope IS NULL OR n.ranking_scope = @scope)
                                    AND (@comboId IS NULL OR n.combo_id = @comboId)
                        ), matched AS (
                                SELECT
                                        count(*)::bigint AS row_count,
                                        count(*) FILTER (WHERE
                                                w.computed_at IS DISTINCT FROM n.snapshot_taken_at OR
                                                w.adjusted_skill_rank IS DISTINCT FROM n.adjusted_skill_rank OR
                                                w.weighted_rank IS DISTINCT FROM n.weighted_rank OR
                                                w.fc_rate_rank IS DISTINCT FROM n.fc_rate_rank OR
                                                w.total_score_rank IS DISTINCT FROM n.total_score_rank OR
                                                w.adjusted_skill_rating IS DISTINCT FROM n.adjusted_skill_rating OR
                                                w.weighted_rating IS DISTINCT FROM n.weighted_rating OR
                                                w.fc_rate IS DISTINCT FROM n.fc_rate OR
                                                w.total_score IS DISTINCT FROM n.total_score OR
                                                w.songs_played IS DISTINCT FROM n.songs_played OR
                                                w.coverage IS DISTINCT FROM n.coverage OR
                                                w.full_combo_count IS DISTINCT FROM n.full_combo_count OR
                                                w.total_charted_songs IS DISTINCT FROM n.total_charted_songs OR
                                                w.raw_weighted_rating IS DISTINCT FROM n.raw_weighted_rating OR
                                                w.raw_skill_rating IS DISTINCT FROM n.raw_skill_rating OR
                                                n.total_ranked_teams IS DISTINCT FROM stats.total_teams)::bigint AS value_mismatches
                                FROM band_team_rank_history w
                                INNER JOIN band_team_rank_history_points n
                                        ON n.band_type = @bandType
                                     AND n.snapshot_date = @snapshotDate
                                     AND n.ranking_scope = w.ranking_scope
                                     AND n.combo_id = w.combo_id
                                     AND n.team_key = w.team_key
                                LEFT JOIN band_team_ranking_stats_history stats
                                        ON stats.band_type = @bandType
                                     AND stats.snapshot_date = @snapshotDate
                                     AND stats.ranking_scope = w.ranking_scope
                                     AND stats.combo_id = w.combo_id
                                WHERE w.band_type = @bandType
                                    AND w.snapshot_date = @snapshotDate
                                    AND (@scope IS NULL OR w.ranking_scope = @scope)
                                    AND (@comboId IS NULL OR w.combo_id = @comboId)
                        ), missing_from_narrow AS (
                                SELECT count(*)::bigint AS row_count
                                FROM band_team_rank_history w
                                WHERE w.band_type = @bandType
                                    AND w.snapshot_date = @snapshotDate
                                    AND (@scope IS NULL OR w.ranking_scope = @scope)
                                    AND (@comboId IS NULL OR w.combo_id = @comboId)
                                    AND NOT EXISTS (
                                        SELECT 1
                                        FROM band_team_rank_history_points n
                                        WHERE n.band_type = @bandType
                                            AND n.snapshot_date = @snapshotDate
                                            AND n.ranking_scope = w.ranking_scope
                                            AND n.combo_id = w.combo_id
                                            AND n.team_key = w.team_key)
                        ), missing_from_wide AS (
                                SELECT count(*)::bigint AS row_count
                                FROM band_team_rank_history_points n
                                WHERE n.band_type = @bandType
                                    AND n.snapshot_date = @snapshotDate
                                    AND (@scope IS NULL OR n.ranking_scope = @scope)
                                    AND (@comboId IS NULL OR n.combo_id = @comboId)
                                    AND NOT EXISTS (
                                        SELECT 1
                                        FROM band_team_rank_history w
                                        WHERE w.band_type = @bandType
                                            AND w.snapshot_date = @snapshotDate
                                            AND w.ranking_scope = n.ranking_scope
                                            AND w.combo_id = n.combo_id
                                            AND w.team_key = n.team_key)
                        )
                        SELECT wide_rows.row_count,
                                     narrow_rows.row_count,
                                     matched.row_count,
                                     missing_from_narrow.row_count,
                                     missing_from_wide.row_count,
                                     matched.value_mismatches
                        FROM wide_rows
                        CROSS JOIN narrow_rows
                        CROSS JOIN matched
                        CROSS JOIN missing_from_narrow
                        CROSS JOIN missing_from_wide;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.Parameters.Add("scope", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(rankingScope) ? DBNull.Value : rankingScope;
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = comboId is null ? DBNull.Value : comboId;

        long wideRows;
        long narrowRows;
        long matchingRows;
        long missingFromNarrow;
        long missingFromWide;
        long valueMismatches;
        using (var reader = cmd.ExecuteReader())
        {
            reader.Read();
            wideRows = reader.GetInt64(0);
            narrowRows = reader.GetInt64(1);
            matchingRows = reader.GetInt64(2);
            missingFromNarrow = reader.GetInt64(3);
            missingFromWide = reader.GetInt64(4);
            valueMismatches = reader.GetInt64(5);
        }

        var effectiveSampleLimit = Math.Max(0, sampleLimit);
        var samples = effectiveSampleLimit > 0 && (missingFromNarrow > 0 || missingFromWide > 0 || valueMismatches > 0)
            ? ReadBandRankHistoryWideNarrowParitySamples(conn, bandType, snapshotDate, rankingScope, comboId, effectiveSampleLimit)
            : [];
        return new BandRankHistoryWideNarrowParitySummary
        {
            BandType = bandType,
            RankingScope = rankingScope,
            ComboId = comboId,
            SnapshotDate = snapshotDate.ToString("yyyy-MM-dd"),
            WideRows = wideRows,
            NarrowRows = narrowRows,
            MatchingRows = matchingRows,
            MissingFromNarrow = missingFromNarrow,
            MissingFromWide = missingFromWide,
            ValueMismatches = valueMismatches,
            Samples = samples,
        };
    }

    public BandRankHistoryV2ParitySummary GetBandRankHistoryV2Parity(
        string bandType,
        DateOnly snapshotDate,
        string? rankingScope = null,
        string? comboId = null,
        int sampleLimit = 10,
        bool ensureSchema = true)
    {
        using var conn = _ds.OpenConnection();
        if (ensureSchema)
        {
            using var tx = conn.BeginTransaction();
            EnsureBandRankHistoryTables(conn, tx);
            tx.Commit();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH legacy AS (
                SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date,
                       snapshot_taken_at, adjusted_skill_rank, weighted_rank, fc_rate_rank,
                       total_score_rank, adjusted_skill_rating, weighted_rating, fc_rate,
                       total_score, songs_played, coverage, full_combo_count,
                       total_charted_songs, total_ranked_teams, raw_weighted_rating,
                       raw_skill_rating
                FROM band_team_rank_history_points
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            ), v2 AS (
                SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date,
                       snapshot_taken_at, adjusted_skill_rank, weighted_rank, fc_rate_rank,
                       total_score_rank, adjusted_skill_rating, weighted_rating, fc_rate,
                       total_score, songs_played, coverage, full_combo_count,
                       total_charted_songs, total_ranked_teams, raw_weighted_rating,
                       raw_skill_rating
                FROM band_team_rank_history_points_v2
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            ), matching AS (
                SELECT legacy.*,
                       v2.snapshot_taken_at AS v2_snapshot_taken_at,
                       v2.adjusted_skill_rank AS v2_adjusted_skill_rank,
                       v2.weighted_rank AS v2_weighted_rank,
                       v2.fc_rate_rank AS v2_fc_rate_rank,
                       v2.total_score_rank AS v2_total_score_rank,
                       v2.adjusted_skill_rating AS v2_adjusted_skill_rating,
                       v2.weighted_rating AS v2_weighted_rating,
                       v2.fc_rate AS v2_fc_rate,
                       v2.total_score AS v2_total_score,
                       v2.songs_played AS v2_songs_played,
                       v2.coverage AS v2_coverage,
                       v2.full_combo_count AS v2_full_combo_count,
                       v2.total_charted_songs AS v2_total_charted_songs,
                       v2.total_ranked_teams AS v2_total_ranked_teams,
                       v2.raw_weighted_rating AS v2_raw_weighted_rating,
                       v2.raw_skill_rating AS v2_raw_skill_rating
                FROM legacy
                INNER JOIN v2
                    ON v2.band_type = legacy.band_type
                   AND v2.ranking_scope = legacy.ranking_scope
                   AND v2.combo_id = legacy.combo_id
                   AND v2.team_key = legacy.team_key
                   AND v2.snapshot_date = legacy.snapshot_date
            ), value_mismatches AS (
                SELECT count(*)::bigint AS row_count
                FROM matching
                WHERE snapshot_taken_at IS DISTINCT FROM v2_snapshot_taken_at
                   OR adjusted_skill_rank IS DISTINCT FROM v2_adjusted_skill_rank
                   OR weighted_rank IS DISTINCT FROM v2_weighted_rank
                   OR fc_rate_rank IS DISTINCT FROM v2_fc_rate_rank
                   OR total_score_rank IS DISTINCT FROM v2_total_score_rank
                   OR adjusted_skill_rating IS DISTINCT FROM v2_adjusted_skill_rating
                   OR weighted_rating IS DISTINCT FROM v2_weighted_rating
                   OR fc_rate IS DISTINCT FROM v2_fc_rate
                   OR total_score IS DISTINCT FROM v2_total_score
                   OR songs_played IS DISTINCT FROM v2_songs_played
                   OR coverage IS DISTINCT FROM v2_coverage
                   OR full_combo_count IS DISTINCT FROM v2_full_combo_count
                   OR total_charted_songs IS DISTINCT FROM v2_total_charted_songs
                   OR total_ranked_teams IS DISTINCT FROM v2_total_ranked_teams
                   OR raw_weighted_rating IS DISTINCT FROM v2_raw_weighted_rating
                   OR raw_skill_rating IS DISTINCT FROM v2_raw_skill_rating
            ), snapshots AS (
                SELECT
                    count(*) FILTER (WHERE status = 'complete')::bigint AS complete_snapshots,
                    count(*) FILTER (WHERE status <> 'complete')::bigint AS incomplete_snapshots,
                    COALESCE(sum(source_row_count), 0)::bigint AS source_rows
                FROM band_team_rank_history_snapshot_v2
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            ), stats AS (
                SELECT COALESCE(sum(total_teams), 0)::bigint AS legacy_stats_rows
                FROM band_team_ranking_stats_history
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            )
            SELECT
                (SELECT count(*) FROM legacy),
                (SELECT count(*) FROM v2),
                (SELECT count(*) FROM matching),
                                (SELECT count(*) FROM legacy l WHERE NOT EXISTS (
                                        SELECT 1 FROM v2
                                        WHERE v2.band_type = l.band_type
                                            AND v2.ranking_scope = l.ranking_scope
                                            AND v2.combo_id = l.combo_id
                                            AND v2.team_key = l.team_key
                                            AND v2.snapshot_date = l.snapshot_date)),
                                (SELECT count(*) FROM v2 WHERE NOT EXISTS (
                                        SELECT 1 FROM legacy l
                                        WHERE l.band_type = v2.band_type
                                            AND l.ranking_scope = v2.ranking_scope
                                            AND l.combo_id = v2.combo_id
                                            AND l.team_key = v2.team_key
                                            AND l.snapshot_date = v2.snapshot_date)),
                (SELECT row_count FROM value_mismatches),
                snapshots.complete_snapshots,
                snapshots.incomplete_snapshots,
                snapshots.source_rows,
                stats.legacy_stats_rows
            FROM snapshots
            CROSS JOIN stats;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.Parameters.Add("scope", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(rankingScope) ? DBNull.Value : rankingScope;
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = comboId is null ? DBNull.Value : comboId;

        long legacyRows;
        long v2Rows;
        long matchingRows;
        long missingFromV2;
        long missingFromLegacy;
        long valueMismatches;
        long completeSnapshots;
        long incompleteSnapshots;
        long v2SnapshotSourceRows;
        long legacyStatsRows;
        using (var reader = cmd.ExecuteReader())
        {
            reader.Read();
            legacyRows = reader.GetInt64(0);
            v2Rows = reader.GetInt64(1);
            matchingRows = reader.GetInt64(2);
            missingFromV2 = reader.GetInt64(3);
            missingFromLegacy = reader.GetInt64(4);
            valueMismatches = reader.GetInt64(5);
            completeSnapshots = reader.GetInt64(6);
            incompleteSnapshots = reader.GetInt64(7);
            v2SnapshotSourceRows = reader.GetInt64(8);
            legacyStatsRows = reader.GetInt64(9);
        }

        var effectiveSampleLimit = Math.Max(0, sampleLimit);
        var samples = effectiveSampleLimit > 0 && (missingFromV2 > 0 || missingFromLegacy > 0 || valueMismatches > 0)
            ? ReadBandRankHistoryV2ParitySamples(conn, bandType, snapshotDate, rankingScope, comboId, effectiveSampleLimit)
            : [];

        return new BandRankHistoryV2ParitySummary
        {
            BandType = bandType,
            RankingScope = rankingScope,
            ComboId = comboId,
            SnapshotDate = snapshotDate.ToString("yyyy-MM-dd"),
            LegacyRows = legacyRows,
            V2Rows = v2Rows,
            MatchingRows = matchingRows,
            MissingFromV2 = missingFromV2,
            MissingFromLegacy = missingFromLegacy,
            ValueMismatches = valueMismatches,
            CompleteSnapshots = completeSnapshots,
            IncompleteSnapshots = incompleteSnapshots,
            V2SnapshotSourceRows = v2SnapshotSourceRows,
            LegacyStatsRows = legacyStatsRows,
            Samples = samples,
        };
    }

    public BandRankHistoryV2LatestParitySummary GetBandRankHistoryV2LatestParity(
        string bandType,
        DateOnly snapshotDate,
        string? rankingScope = null,
        string? comboId = null,
        int sampleLimit = 10,
        bool ensureSchema = true)
    {
        using var conn = _ds.OpenConnection();
        if (ensureSchema)
        {
            using var tx = conn.BeginTransaction();
            EnsureBandRankHistoryTables(conn, tx);
            tx.Commit();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH points AS (
                SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date,
                       snapshot_id, generation_id, row_fingerprint
                FROM band_team_rank_history_points_v2
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            ), latest AS (
                SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date,
                       snapshot_id, generation_id, row_fingerprint
                FROM band_team_rank_history_latest_v2
                WHERE band_type = @bandType
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            ), latest_for_snapshot AS (
                SELECT *
                FROM latest
                WHERE snapshot_date = @snapshotDate
            ), matching AS (
                SELECT points.*
                FROM points
                INNER JOIN latest
                    ON latest.band_type = points.band_type
                   AND latest.ranking_scope = points.ranking_scope
                   AND latest.combo_id = points.combo_id
                   AND latest.team_key = points.team_key
                   AND latest.snapshot_date = points.snapshot_date
                   AND latest.snapshot_id = points.snapshot_id
                   AND latest.generation_id = points.generation_id
                   AND latest.row_fingerprint = points.row_fingerprint
            ), mismatched AS (
                SELECT points.*
                FROM points
                INNER JOIN latest
                    ON latest.band_type = points.band_type
                   AND latest.ranking_scope = points.ranking_scope
                   AND latest.combo_id = points.combo_id
                   AND latest.team_key = points.team_key
                WHERE latest.snapshot_date < points.snapshot_date
                   OR (latest.snapshot_date = points.snapshot_date AND (
                        latest.snapshot_id IS DISTINCT FROM points.snapshot_id
                        OR latest.generation_id IS DISTINCT FROM points.generation_id
                        OR latest.row_fingerprint IS DISTINCT FROM points.row_fingerprint))
            )
            SELECT
                (SELECT count(*) FROM points),
                (SELECT count(*) FROM latest_for_snapshot),
                (SELECT count(*) FROM matching),
                (SELECT count(*) FROM points p WHERE NOT EXISTS (
                    SELECT 1 FROM latest l
                    WHERE l.band_type = p.band_type
                      AND l.ranking_scope = p.ranking_scope
                      AND l.combo_id = p.combo_id
                      AND l.team_key = p.team_key)),
                (SELECT count(*) FROM mismatched),
                (SELECT count(*) FROM latest_for_snapshot l WHERE NOT EXISTS (
                    SELECT 1 FROM points p
                    WHERE p.band_type = l.band_type
                      AND p.ranking_scope = l.ranking_scope
                      AND p.combo_id = l.combo_id
                      AND p.team_key = l.team_key
                      AND p.snapshot_date = l.snapshot_date));";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.Parameters.Add("scope", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(rankingScope) ? DBNull.Value : rankingScope;
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = comboId is null ? DBNull.Value : comboId;

        long pointRows;
        long latestRowsForSnapshot;
        long matchingLatestRows;
        long missingFromLatest;
        long latestMismatches;
        long extraLatestRows;
        using (var reader = cmd.ExecuteReader())
        {
            reader.Read();
            pointRows = reader.GetInt64(0);
            latestRowsForSnapshot = reader.GetInt64(1);
            matchingLatestRows = reader.GetInt64(2);
            missingFromLatest = reader.GetInt64(3);
            latestMismatches = reader.GetInt64(4);
            extraLatestRows = reader.GetInt64(5);
        }

        var effectiveSampleLimit = Math.Max(0, sampleLimit);
        var samples = effectiveSampleLimit > 0 && (missingFromLatest > 0 || latestMismatches > 0 || extraLatestRows > 0)
            ? ReadBandRankHistoryV2LatestParitySamples(conn, bandType, snapshotDate, rankingScope, comboId, effectiveSampleLimit)
            : [];

        return new BandRankHistoryV2LatestParitySummary
        {
            BandType = bandType,
            RankingScope = rankingScope,
            ComboId = comboId,
            SnapshotDate = snapshotDate.ToString("yyyy-MM-dd"),
            V2PointRows = pointRows,
            LatestRowsForSnapshot = latestRowsForSnapshot,
            MatchingLatestRows = matchingLatestRows,
            MissingFromLatest = missingFromLatest,
            LatestMismatches = latestMismatches,
            ExtraLatestRowsForSnapshot = extraLatestRows,
            Samples = samples,
        };
    }

    public BandRankHistoryV2ReadPreview GetBandRankHistoryV2ReadPreview(
        string bandType,
        string teamKey,
        string? comboId = null,
        int days = 30,
        bool ensureSchema = true)
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-Math.Max(days, 1)));

        using var conn = _ds.OpenConnection();
        if (ensureSchema)
        {
            using var tx = conn.BeginTransaction();
            EnsureBandRankHistoryTables(conn, tx);
            tx.Commit();
        }

        var narrow = TableExists(conn, null, "band_team_rank_history_points")
            ? GetBandRankHistoryFromPoints(conn, bandType, teamKey, rankingScope, normalizedComboId, cutoff)
            : [];
        var wide = TableExists(conn, null, "band_team_rank_history") && TableExists(conn, null, "band_team_ranking_stats_history")
            ? GetBandRankHistoryFromWide(conn, bandType, teamKey, rankingScope, normalizedComboId, cutoff)
            : [];
        var legacy = narrow.Count > 0 ? narrow : wide;
        var v2 = TableExists(conn, null, "band_team_rank_history_points_v2")
            ? GetBandRankHistoryFromV2Points(conn, bandType, teamKey, rankingScope, normalizedComboId, cutoff)
            : [];
        var currentFallback = v2.Count > 0 ? v2 : legacy;
        var merged = MergeBandRankHistoryByDate(v2, legacy);

        var legacyDates = ReadSnapshotDates(legacy);
        var v2Dates = ReadSnapshotDates(v2);
        var currentFallbackDates = ReadSnapshotDates(currentFallback);
        var mergedDates = ReadSnapshotDates(merged);
        var hiddenByCurrentFallback = v2.Count > 0 ? MissingDates(legacyDates, currentFallbackDates) : [];

        return new BandRankHistoryV2ReadPreview
        {
            BandType = bandType,
            RankingScope = rankingScope,
            ComboId = normalizedComboId,
            TeamKey = teamKey,
            Days = days,
            LegacyRows = legacy.Count,
            V2OnlyRows = v2.Count,
            CurrentV2FallbackRows = currentFallback.Count,
            MergedRows = merged.Count,
            CurrentV2FallbackWouldHideLegacyDates = hiddenByCurrentFallback.Count > 0,
            LegacyDates = legacyDates,
            V2Dates = v2Dates,
            CurrentV2FallbackDates = currentFallbackDates,
            MergedDates = mergedDates,
            LegacyDatesHiddenByCurrentV2Fallback = hiddenByCurrentFallback,
            LegacyDatesMissingFromV2 = MissingDates(legacyDates, v2Dates),
        };
    }

    private static List<BandRankHistoryParityMismatchSample> ReadBandRankHistoryV2ParitySamples(
        NpgsqlConnection conn,
        string bandType,
        DateOnly snapshotDate,
        string? rankingScope,
        string? comboId,
        int sampleLimit)
    {
        if (sampleLimit <= 0)
            return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH value_mismatches AS (
                SELECT legacy.band_type,
                       legacy.ranking_scope,
                       legacy.combo_id,
                       legacy.team_key,
                       legacy.snapshot_date,
                       diff.mismatched_columns
                FROM band_team_rank_history_points legacy
                INNER JOIN band_team_rank_history_points_v2 v2
                    ON v2.band_type = legacy.band_type
                   AND v2.ranking_scope = legacy.ranking_scope
                   AND v2.combo_id = legacy.combo_id
                   AND v2.team_key = legacy.team_key
                   AND v2.snapshot_date = legacy.snapshot_date
                CROSS JOIN LATERAL (
                    SELECT array_remove(ARRAY[
                        CASE WHEN legacy.snapshot_taken_at IS DISTINCT FROM v2.snapshot_taken_at THEN 'snapshot_taken_at' END,
                        CASE WHEN legacy.adjusted_skill_rank IS DISTINCT FROM v2.adjusted_skill_rank THEN 'adjusted_skill_rank' END,
                        CASE WHEN legacy.weighted_rank IS DISTINCT FROM v2.weighted_rank THEN 'weighted_rank' END,
                        CASE WHEN legacy.fc_rate_rank IS DISTINCT FROM v2.fc_rate_rank THEN 'fc_rate_rank' END,
                        CASE WHEN legacy.total_score_rank IS DISTINCT FROM v2.total_score_rank THEN 'total_score_rank' END,
                        CASE WHEN legacy.adjusted_skill_rating IS DISTINCT FROM v2.adjusted_skill_rating THEN 'adjusted_skill_rating' END,
                        CASE WHEN legacy.weighted_rating IS DISTINCT FROM v2.weighted_rating THEN 'weighted_rating' END,
                        CASE WHEN legacy.fc_rate IS DISTINCT FROM v2.fc_rate THEN 'fc_rate' END,
                        CASE WHEN legacy.total_score IS DISTINCT FROM v2.total_score THEN 'total_score' END,
                        CASE WHEN legacy.songs_played IS DISTINCT FROM v2.songs_played THEN 'songs_played' END,
                        CASE WHEN legacy.coverage IS DISTINCT FROM v2.coverage THEN 'coverage' END,
                        CASE WHEN legacy.full_combo_count IS DISTINCT FROM v2.full_combo_count THEN 'full_combo_count' END,
                        CASE WHEN legacy.total_charted_songs IS DISTINCT FROM v2.total_charted_songs THEN 'total_charted_songs' END,
                        CASE WHEN legacy.total_ranked_teams IS DISTINCT FROM v2.total_ranked_teams THEN 'total_ranked_teams' END,
                        CASE WHEN legacy.raw_weighted_rating IS DISTINCT FROM v2.raw_weighted_rating THEN 'raw_weighted_rating' END,
                        CASE WHEN legacy.raw_skill_rating IS DISTINCT FROM v2.raw_skill_rating THEN 'raw_skill_rating' END
                    ], NULL)::text[] AS mismatched_columns
                ) diff
                WHERE legacy.band_type = @bandType
                  AND legacy.snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR legacy.ranking_scope = @scope)
                  AND (@comboId IS NULL OR legacy.combo_id = @comboId)
                  AND cardinality(diff.mismatched_columns) > 0
            ), samples AS (
                SELECT legacy.band_type, legacy.ranking_scope, legacy.combo_id, legacy.team_key, legacy.snapshot_date,
                       'missing_from_v2' AS mismatch_kind, ARRAY[]::text[] AS mismatched_columns
                FROM band_team_rank_history_points legacy
                WHERE legacy.band_type = @bandType
                  AND legacy.snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR legacy.ranking_scope = @scope)
                  AND (@comboId IS NULL OR legacy.combo_id = @comboId)
                  AND NOT EXISTS (
                    SELECT 1
                    FROM band_team_rank_history_points_v2 v2
                    WHERE v2.band_type = legacy.band_type
                      AND v2.snapshot_date = legacy.snapshot_date
                      AND v2.ranking_scope = legacy.ranking_scope
                      AND v2.combo_id = legacy.combo_id
                      AND v2.team_key = legacy.team_key)
                UNION ALL
                SELECT v2.band_type, v2.ranking_scope, v2.combo_id, v2.team_key, v2.snapshot_date,
                       'missing_from_legacy' AS mismatch_kind, ARRAY[]::text[] AS mismatched_columns
                FROM band_team_rank_history_points_v2 v2
                WHERE v2.band_type = @bandType
                  AND v2.snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR v2.ranking_scope = @scope)
                  AND (@comboId IS NULL OR v2.combo_id = @comboId)
                  AND NOT EXISTS (
                    SELECT 1
                    FROM band_team_rank_history_points legacy
                    WHERE legacy.band_type = v2.band_type
                      AND legacy.snapshot_date = v2.snapshot_date
                      AND legacy.ranking_scope = v2.ranking_scope
                      AND legacy.combo_id = v2.combo_id
                      AND legacy.team_key = v2.team_key)
                UNION ALL
                SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date,
                       'value_mismatch' AS mismatch_kind, mismatched_columns
                FROM value_mismatches
            )
            SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date, mismatch_kind, mismatched_columns
            FROM samples
            ORDER BY mismatch_kind, ranking_scope, combo_id, team_key
            LIMIT @sampleLimit;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.Parameters.Add("scope", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(rankingScope) ? DBNull.Value : rankingScope;
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = comboId is null ? DBNull.Value : comboId;
        cmd.Parameters.AddWithValue("sampleLimit", sampleLimit);

        return ReadBandRankHistoryParitySamples(cmd);
    }

    private static List<BandRankHistoryParityMismatchSample> ReadBandRankHistoryV2LatestParitySamples(
        NpgsqlConnection conn,
        string bandType,
        DateOnly snapshotDate,
        string? rankingScope,
        string? comboId,
        int sampleLimit)
    {
        if (sampleLimit <= 0)
            return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH points AS (
                SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date,
                       snapshot_id, generation_id, row_fingerprint
                FROM band_team_rank_history_points_v2
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            ), latest AS (
                SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date,
                       snapshot_id, generation_id, row_fingerprint
                FROM band_team_rank_history_latest_v2
                WHERE band_type = @bandType
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            ), samples AS (
                SELECT points.band_type, points.ranking_scope, points.combo_id, points.team_key, points.snapshot_date,
                       'missing_from_latest' AS mismatch_kind, ARRAY[]::text[] AS mismatched_columns
                FROM points
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM latest
                    WHERE latest.band_type = points.band_type
                      AND latest.ranking_scope = points.ranking_scope
                      AND latest.combo_id = points.combo_id
                      AND latest.team_key = points.team_key)
                UNION ALL
                SELECT points.band_type, points.ranking_scope, points.combo_id, points.team_key, points.snapshot_date,
                       'latest_mismatch' AS mismatch_kind,
                       array_remove(ARRAY[
                           CASE WHEN latest.snapshot_date < points.snapshot_date THEN 'snapshot_date' END,
                           CASE WHEN latest.snapshot_date = points.snapshot_date AND latest.snapshot_id IS DISTINCT FROM points.snapshot_id THEN 'snapshot_id' END,
                           CASE WHEN latest.snapshot_date = points.snapshot_date AND latest.generation_id IS DISTINCT FROM points.generation_id THEN 'generation_id' END,
                           CASE WHEN latest.snapshot_date = points.snapshot_date AND latest.row_fingerprint IS DISTINCT FROM points.row_fingerprint THEN 'row_fingerprint' END
                       ], NULL)::text[] AS mismatched_columns
                FROM points
                INNER JOIN latest
                    ON latest.band_type = points.band_type
                   AND latest.ranking_scope = points.ranking_scope
                   AND latest.combo_id = points.combo_id
                   AND latest.team_key = points.team_key
                WHERE latest.snapshot_date < points.snapshot_date
                   OR (latest.snapshot_date = points.snapshot_date AND (
                        latest.snapshot_id IS DISTINCT FROM points.snapshot_id
                        OR latest.generation_id IS DISTINCT FROM points.generation_id
                        OR latest.row_fingerprint IS DISTINCT FROM points.row_fingerprint))
                UNION ALL
                SELECT latest.band_type, latest.ranking_scope, latest.combo_id, latest.team_key, latest.snapshot_date,
                       'extra_latest_for_snapshot' AS mismatch_kind, ARRAY[]::text[] AS mismatched_columns
                FROM latest
                WHERE latest.snapshot_date = @snapshotDate
                  AND NOT EXISTS (
                    SELECT 1
                    FROM points
                    WHERE points.band_type = latest.band_type
                      AND points.ranking_scope = latest.ranking_scope
                      AND points.combo_id = latest.combo_id
                      AND points.team_key = latest.team_key
                      AND points.snapshot_date = latest.snapshot_date)
            )
            SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date, mismatch_kind, mismatched_columns
            FROM samples
            ORDER BY mismatch_kind, ranking_scope, combo_id, team_key
            LIMIT @sampleLimit;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.Parameters.Add("scope", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(rankingScope) ? DBNull.Value : rankingScope;
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = comboId is null ? DBNull.Value : comboId;
        cmd.Parameters.AddWithValue("sampleLimit", sampleLimit);

        return ReadBandRankHistoryParitySamples(cmd);
    }

    private static List<BandRankHistoryParityMismatchSample> ReadBandRankHistoryParitySamples(NpgsqlCommand cmd)
    {
        var samples = new List<BandRankHistoryParityMismatchSample>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            samples.Add(new BandRankHistoryParityMismatchSample
            {
                BandType = reader.GetString(0),
                RankingScope = reader.GetString(1),
                ComboId = reader.GetString(2),
                TeamKey = reader.GetString(3),
                SnapshotDate = DateOnly.FromDateTime(reader.GetDateTime(4)).ToString("yyyy-MM-dd"),
                MismatchKind = reader.GetString(5),
                MismatchedColumns = reader.GetFieldValue<string[]>(6),
            });
        }

        return samples;
    }

    private static List<BandRankHistoryDto> MergeBandRankHistoryByDate(IReadOnlyList<BandRankHistoryDto> v2, IReadOnlyList<BandRankHistoryDto> legacy)
    {
        var byDate = legacy
            .Concat(v2)
            .GroupBy(static row => row.SnapshotDate, StringComparer.Ordinal)
            .Select(static group => group.Last())
            .OrderBy(static row => row.SnapshotDate, StringComparer.Ordinal)
            .ToList();
        return byDate;
    }

    private static IReadOnlyList<string> ReadSnapshotDates(IReadOnlyList<BandRankHistoryDto> rows) =>
        rows.Select(static row => row.SnapshotDate)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static date => date, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> MissingDates(IReadOnlyList<string> expectedDates, IReadOnlyList<string> actualDates)
    {
        var actual = actualDates.ToHashSet(StringComparer.Ordinal);
        return expectedDates.Where(date => !actual.Contains(date)).ToArray();
    }

    private static List<BandRankHistoryParityMismatchSample> ReadBandRankHistoryWideNarrowParitySamples(
        NpgsqlConnection conn,
        string bandType,
        DateOnly snapshotDate,
        string? rankingScope,
        string? comboId,
        int sampleLimit)
    {
        if (sampleLimit <= 0)
            return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH value_mismatches AS (
                SELECT w.band_type,
                       w.ranking_scope,
                       w.combo_id,
                       w.team_key,
                       w.snapshot_date,
                       diff.mismatched_columns
                FROM band_team_rank_history w
                INNER JOIN band_team_rank_history_points n
                    ON n.band_type = @bandType
                   AND n.snapshot_date = @snapshotDate
                   AND n.ranking_scope = w.ranking_scope
                   AND n.combo_id = w.combo_id
                   AND n.team_key = w.team_key
                LEFT JOIN band_team_ranking_stats_history stats
                    ON stats.band_type = @bandType
                   AND stats.snapshot_date = @snapshotDate
                   AND stats.ranking_scope = w.ranking_scope
                   AND stats.combo_id = w.combo_id
                CROSS JOIN LATERAL (
                    SELECT array_remove(ARRAY[
                        CASE WHEN w.computed_at IS DISTINCT FROM n.snapshot_taken_at THEN 'snapshot_taken_at' END,
                        CASE WHEN w.adjusted_skill_rank IS DISTINCT FROM n.adjusted_skill_rank THEN 'adjusted_skill_rank' END,
                        CASE WHEN w.weighted_rank IS DISTINCT FROM n.weighted_rank THEN 'weighted_rank' END,
                        CASE WHEN w.fc_rate_rank IS DISTINCT FROM n.fc_rate_rank THEN 'fc_rate_rank' END,
                        CASE WHEN w.total_score_rank IS DISTINCT FROM n.total_score_rank THEN 'total_score_rank' END,
                        CASE WHEN w.adjusted_skill_rating IS DISTINCT FROM n.adjusted_skill_rating THEN 'adjusted_skill_rating' END,
                        CASE WHEN w.weighted_rating IS DISTINCT FROM n.weighted_rating THEN 'weighted_rating' END,
                        CASE WHEN w.fc_rate IS DISTINCT FROM n.fc_rate THEN 'fc_rate' END,
                        CASE WHEN w.total_score IS DISTINCT FROM n.total_score THEN 'total_score' END,
                        CASE WHEN w.songs_played IS DISTINCT FROM n.songs_played THEN 'songs_played' END,
                        CASE WHEN w.coverage IS DISTINCT FROM n.coverage THEN 'coverage' END,
                        CASE WHEN w.full_combo_count IS DISTINCT FROM n.full_combo_count THEN 'full_combo_count' END,
                        CASE WHEN w.total_charted_songs IS DISTINCT FROM n.total_charted_songs THEN 'total_charted_songs' END,
                        CASE WHEN w.raw_weighted_rating IS DISTINCT FROM n.raw_weighted_rating THEN 'raw_weighted_rating' END,
                        CASE WHEN w.raw_skill_rating IS DISTINCT FROM n.raw_skill_rating THEN 'raw_skill_rating' END,
                        CASE WHEN n.total_ranked_teams IS DISTINCT FROM stats.total_teams THEN 'total_ranked_teams' END
                    ], NULL)::text[] AS mismatched_columns
                ) diff
                WHERE w.band_type = @bandType
                  AND w.snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR w.ranking_scope = @scope)
                  AND (@comboId IS NULL OR w.combo_id = @comboId)
                  AND cardinality(diff.mismatched_columns) > 0
            ), samples AS (
                SELECT w.band_type, w.ranking_scope, w.combo_id, w.team_key, w.snapshot_date,
                       'missing_from_narrow' AS mismatch_kind, ARRAY[]::text[] AS mismatched_columns
                FROM band_team_rank_history w
                WHERE w.band_type = @bandType
                  AND w.snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR w.ranking_scope = @scope)
                  AND (@comboId IS NULL OR w.combo_id = @comboId)
                  AND NOT EXISTS (
                    SELECT 1
                    FROM band_team_rank_history_points n
                    WHERE n.band_type = @bandType
                      AND n.snapshot_date = @snapshotDate
                      AND n.ranking_scope = w.ranking_scope
                      AND n.combo_id = w.combo_id
                      AND n.team_key = w.team_key)
                UNION ALL
                SELECT n.band_type, n.ranking_scope, n.combo_id, n.team_key, n.snapshot_date,
                       'missing_from_wide' AS mismatch_kind, ARRAY[]::text[] AS mismatched_columns
                FROM band_team_rank_history_points n
                WHERE n.band_type = @bandType
                  AND n.snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR n.ranking_scope = @scope)
                  AND (@comboId IS NULL OR n.combo_id = @comboId)
                  AND NOT EXISTS (
                    SELECT 1
                    FROM band_team_rank_history w
                    WHERE w.band_type = @bandType
                      AND w.snapshot_date = @snapshotDate
                      AND w.ranking_scope = n.ranking_scope
                      AND w.combo_id = n.combo_id
                      AND w.team_key = n.team_key)
                UNION ALL
                SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date,
                       'value_mismatch' AS mismatch_kind, mismatched_columns
                FROM value_mismatches
            )
            SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date, mismatch_kind, mismatched_columns
            FROM samples
            ORDER BY mismatch_kind, ranking_scope, combo_id, team_key
            LIMIT @sampleLimit;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.Parameters.Add("scope", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(rankingScope) ? DBNull.Value : rankingScope;
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = comboId is null ? DBNull.Value : comboId;
        cmd.Parameters.AddWithValue("sampleLimit", sampleLimit);

        var samples = new List<BandRankHistoryParityMismatchSample>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            samples.Add(new BandRankHistoryParityMismatchSample
            {
                BandType = reader.GetString(0),
                RankingScope = reader.GetString(1),
                ComboId = reader.GetString(2),
                TeamKey = reader.GetString(3),
                SnapshotDate = DateOnly.FromDateTime(reader.GetDateTime(4)).ToString("yyyy-MM-dd"),
                MismatchKind = reader.GetString(5),
                MismatchedColumns = reader.GetFieldValue<string[]>(6),
            });
        }

        return samples;
    }

    private static BandRankHistorySnapshotResult ReadBandRankHistoryJobSnapshotResult(
        NpgsqlConnection conn,
        long jobId,
        int commandTimeoutSeconds)
    {
        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, commandTimeoutSeconds);
        cmd.CommandText = @"
            SELECT count(*)::int AS chunks_total,
                   count(*) FILTER (WHERE status = 'complete')::int AS chunks_completed,
                   COALESCE(sum(rows_scanned), 0)::bigint AS rows_scanned,
                   COALESCE(sum(rows_inserted), 0)::bigint AS rows_inserted,
                   COALESCE(sum(rows_skipped), 0)::bigint AS rows_skipped
            FROM band_rank_history_job_chunks
            WHERE job_id = @jobId";
        cmd.Parameters.AddWithValue("jobId", jobId);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new BandRankHistorySnapshotResult
        {
            ChunksTotal = reader.GetInt32(0),
            ChunksCompleted = reader.GetInt32(1),
            RowsScanned = reader.GetInt64(2),
            RowsInserted = reader.GetInt64(3),
            RowsSkipped = reader.GetInt64(4),
        };
    }

    private sealed record BandRankHistoryChunkKey(
        string RankingScope,
        string ComboId,
        int ChunkOrdinal,
        string? TeamKeyStart,
        string? TeamKeyEnd,
        long EstimatedRows,
        long SourceGeneration);

    private sealed record BandRankHistoryChunkResult(long RowsScanned, long RowsInserted);

    private sealed record BandRankHistoryV2BackfillSliceInfo(
        string BandType,
        DateOnly SnapshotDate,
        string RankingScope,
        string ComboId,
        DateTime ComputedAt,
        long LegacyRows,
        long ExistingV2Rows,
        long MissingV2Rows,
        long CompleteSnapshots)
    {
        public BandRankHistoryV2BackfillSlice ToDto() => new()
        {
            BandType = BandType,
            SnapshotDate = SnapshotDate.ToString("yyyy-MM-dd"),
            RankingScope = RankingScope,
            ComboId = ComboId,
            LegacyRows = LegacyRows,
            ExistingV2Rows = ExistingV2Rows,
            MissingV2Rows = MissingV2Rows,
            CompleteSnapshots = CompleteSnapshots,
        };
    }

    private static string BandRankHistoryFingerprintExpression(string alias) => $@"md5(concat_ws('|',
                    {alias}.team_members::text,
                    {alias}.songs_played::text,
                    {alias}.total_charted_songs::text,
                    {alias}.coverage::text,
                    {alias}.raw_skill_rating::text,
                    {alias}.adjusted_skill_rating::text,
                    {alias}.adjusted_skill_rank::text,
                    {alias}.weighted_rating::text,
                    {alias}.weighted_rank::text,
                    {alias}.fc_rate::text,
                    {alias}.fc_rate_rank::text,
                    {alias}.total_score::text,
                    {alias}.total_score_rank::text,
                    {alias}.avg_accuracy::text,
                    {alias}.full_combo_count::text,
                    {alias}.avg_stars::text,
                    {alias}.best_rank::text,
                    {alias}.avg_rank::text,
                    COALESCE({alias}.raw_weighted_rating::text, '')))";

    private static string BandRankHistoryPointFingerprintExpression(string alias) => $@"md5(concat_ws('|',
                    {alias}.snapshot_taken_at::text,
                    {alias}.adjusted_skill_rank::text,
                    {alias}.weighted_rank::text,
                    {alias}.fc_rate_rank::text,
                    {alias}.total_score_rank::text,
                    COALESCE({alias}.adjusted_skill_rating::text, ''),
                    COALESCE({alias}.weighted_rating::text, ''),
                    COALESCE({alias}.fc_rate::text, ''),
                    COALESCE({alias}.total_score::text, ''),
                    COALESCE({alias}.songs_played::text, ''),
                    COALESCE({alias}.coverage::text, ''),
                    COALESCE({alias}.full_combo_count::text, ''),
                    COALESCE({alias}.total_charted_songs::text, ''),
                    COALESCE({alias}.total_ranked_teams::text, ''),
                    COALESCE({alias}.raw_weighted_rating::text, ''),
                    COALESCE({alias}.raw_skill_rating::text, '')))";

    private static List<BandRankHistoryV2BackfillSliceInfo> ReadBandRankHistoryV2BackfillSlices(
        NpgsqlConnection conn,
        string bandType,
        BandRankHistoryV2BackfillOptions options,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, options.CommandTimeoutSeconds);
        cmd.CommandText = @"
            WITH legacy AS (
                SELECT
                    band_type,
                    snapshot_date,
                    ranking_scope,
                    combo_id,
                    count(*)::bigint AS legacy_rows,
                    max(snapshot_taken_at) AS computed_at
                FROM band_team_rank_history_points
                WHERE band_type = @bandType
                  AND (@startDate IS NULL OR snapshot_date >= @startDate)
                  AND (@endDate IS NULL OR snapshot_date <= @endDate)
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
                GROUP BY band_type, snapshot_date, ranking_scope, combo_id
            ), v2 AS (
                SELECT
                    band_type,
                    snapshot_date,
                    ranking_scope,
                    combo_id,
                    count(*)::bigint AS v2_rows
                FROM band_team_rank_history_points_v2
                WHERE band_type = @bandType
                  AND (@startDate IS NULL OR snapshot_date >= @startDate)
                  AND (@endDate IS NULL OR snapshot_date <= @endDate)
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
                GROUP BY band_type, snapshot_date, ranking_scope, combo_id
            ), snapshots AS (
                SELECT
                    band_type,
                    snapshot_date,
                    ranking_scope,
                    combo_id,
                    count(*) FILTER (WHERE status = 'complete')::bigint AS complete_snapshots
                FROM band_team_rank_history_snapshot_v2
                WHERE band_type = @bandType
                  AND (@startDate IS NULL OR snapshot_date >= @startDate)
                  AND (@endDate IS NULL OR snapshot_date <= @endDate)
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
                GROUP BY band_type, snapshot_date, ranking_scope, combo_id
            ), stats AS (
                SELECT band_type, snapshot_date, ranking_scope, combo_id, computed_at
                FROM band_team_ranking_stats_history
                WHERE band_type = @bandType
                  AND (@startDate IS NULL OR snapshot_date >= @startDate)
                  AND (@endDate IS NULL OR snapshot_date <= @endDate)
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            )
            SELECT
                legacy.band_type,
                legacy.snapshot_date,
                legacy.ranking_scope,
                legacy.combo_id,
                COALESCE(stats.computed_at, legacy.computed_at) AS computed_at,
                legacy.legacy_rows,
                COALESCE(v2.v2_rows, 0) AS existing_v2_rows,
                GREATEST(legacy.legacy_rows - COALESCE(v2.v2_rows, 0), 0) AS missing_v2_rows,
                COALESCE(snapshots.complete_snapshots, 0) AS complete_snapshots
            FROM legacy
            LEFT JOIN v2
              ON v2.band_type = legacy.band_type
             AND v2.snapshot_date = legacy.snapshot_date
             AND v2.ranking_scope = legacy.ranking_scope
             AND v2.combo_id = legacy.combo_id
            LEFT JOIN snapshots
              ON snapshots.band_type = legacy.band_type
             AND snapshots.snapshot_date = legacy.snapshot_date
             AND snapshots.ranking_scope = legacy.ranking_scope
             AND snapshots.combo_id = legacy.combo_id
            LEFT JOIN stats
              ON stats.band_type = legacy.band_type
             AND stats.snapshot_date = legacy.snapshot_date
             AND stats.ranking_scope = legacy.ranking_scope
             AND stats.combo_id = legacy.combo_id
            WHERE legacy.legacy_rows <> COALESCE(v2.v2_rows, 0)
               OR COALESCE(snapshots.complete_snapshots, 0) = 0
            ORDER BY legacy.snapshot_date, legacy.ranking_scope, legacy.combo_id;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.Add("startDate", NpgsqlDbType.Date).Value = options.StartDate.HasValue ? options.StartDate.Value : (object)DBNull.Value;
        cmd.Parameters.Add("endDate", NpgsqlDbType.Date).Value = options.EndDate.HasValue ? options.EndDate.Value : (object)DBNull.Value;
        cmd.Parameters.Add("scope", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(options.RankingScope) ? DBNull.Value : options.RankingScope;
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = options.ComboId is null ? DBNull.Value : options.ComboId;

        ct.ThrowIfCancellationRequested();
        using var registration = ct.Register(static state => ((NpgsqlCommand)state!).Cancel(), cmd);
        var slices = new List<BandRankHistoryV2BackfillSliceInfo>();
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                slices.Add(new BandRankHistoryV2BackfillSliceInfo(
                    reader.GetString(0),
                    DateOnly.FromDateTime(reader.GetDateTime(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetDateTime(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8)));
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        return slices;
    }

    private static BandRankHistoryV2BackfillSlice BackfillBandRankHistoryV2Slice(
        NpgsqlConnection conn,
        BandRankHistoryV2BackfillSliceInfo slice,
        BandRankHistoryV2BackfillOptions options,
        CancellationToken ct)
    {
        using var tx = conn.BeginTransaction();
        if (options.SynchronousCommitOff)
        {
            using var syncCmd = conn.CreateCommand();
            syncCmd.Transaction = tx;
            syncCmd.CommandText = "SET LOCAL synchronous_commit = off";
            syncCmd.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, options.CommandTimeoutSeconds);
        cmd.Transaction = tx;
        cmd.CommandText = $@"
            WITH target_snapshot AS (
                INSERT INTO band_team_rank_history_snapshot_v2 (
                    generation_id, band_type, ranking_scope, combo_id, snapshot_date,
                    computed_at, source_row_count, changed_row_count, status, completed_at, updated_at)
                VALUES (0, @bandType, @scope, @comboId, @snapshotDate,
                    @computedAt, @legacyRows, @missingRows, 'complete', now(), now())
                ON CONFLICT (band_type, ranking_scope, combo_id, snapshot_date) DO UPDATE SET
                    computed_at = EXCLUDED.computed_at,
                    source_row_count = EXCLUDED.source_row_count,
                    changed_row_count = EXCLUDED.changed_row_count,
                    status = EXCLUDED.status,
                    completed_at = EXCLUDED.completed_at,
                    updated_at = now()
                RETURNING snapshot_id, generation_id
            ), legacy AS (
                SELECT
                    points.*,
                    {BandRankHistoryPointFingerprintExpression("points")} AS row_fingerprint
                FROM band_team_rank_history_points points
                WHERE points.band_type = @bandType
                  AND points.snapshot_date = @snapshotDate
                  AND points.ranking_scope = @scope
                  AND points.combo_id = @comboId
            ), missing AS (
                SELECT legacy.*
                FROM legacy
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM band_team_rank_history_points_v2 existing
                    WHERE existing.band_type = legacy.band_type
                      AND existing.snapshot_date = legacy.snapshot_date
                      AND existing.ranking_scope = legacy.ranking_scope
                      AND existing.combo_id = legacy.combo_id
                      AND existing.team_key = legacy.team_key)
            ), inserted_points AS (
                INSERT INTO band_team_rank_history_points_v2 (
                    band_type, ranking_scope, combo_id, team_key, snapshot_date, snapshot_id, generation_id,
                    snapshot_taken_at, row_fingerprint, adjusted_skill_rank, weighted_rank, fc_rate_rank,
                    total_score_rank, adjusted_skill_rating, weighted_rating, fc_rate, total_score,
                    songs_played, coverage, full_combo_count, total_charted_songs, total_ranked_teams,
                    raw_weighted_rating, raw_skill_rating)
                SELECT
                    band_type,
                    ranking_scope,
                    combo_id,
                    team_key,
                    snapshot_date,
                    (SELECT snapshot_id FROM target_snapshot),
                    (SELECT generation_id FROM target_snapshot),
                    snapshot_taken_at,
                    row_fingerprint,
                    adjusted_skill_rank,
                    weighted_rank,
                    fc_rate_rank,
                    total_score_rank,
                    adjusted_skill_rating,
                    weighted_rating,
                    fc_rate,
                    total_score,
                    songs_played,
                    coverage,
                    full_combo_count,
                    total_charted_songs,
                    total_ranked_teams,
                    raw_weighted_rating,
                    raw_skill_rating
                FROM missing
                ON CONFLICT (band_type, ranking_scope, combo_id, team_key, snapshot_date) DO NOTHING
                RETURNING band_type, ranking_scope, combo_id, team_key, snapshot_date, snapshot_id, generation_id, row_fingerprint
            ), latest_v2 AS (
                INSERT INTO band_team_rank_history_latest_v2 (
                    band_type, ranking_scope, combo_id, team_key, generation_id,
                    snapshot_id, snapshot_date, row_fingerprint, updated_at)
                SELECT
                    band_type,
                    ranking_scope,
                    combo_id,
                    team_key,
                    generation_id,
                    snapshot_id,
                    snapshot_date,
                    row_fingerprint,
                    now()
                FROM inserted_points
                ON CONFLICT (band_type, ranking_scope, combo_id, team_key) DO UPDATE SET
                    generation_id = EXCLUDED.generation_id,
                    snapshot_id = EXCLUDED.snapshot_id,
                    snapshot_date = EXCLUDED.snapshot_date,
                    row_fingerprint = EXCLUDED.row_fingerprint,
                    updated_at = now()
                WHERE band_team_rank_history_latest_v2.snapshot_date <= EXCLUDED.snapshot_date
                RETURNING 1
            )
            SELECT
                (SELECT count(*) FROM target_snapshot),
                (SELECT count(*) FROM inserted_points),
                (SELECT count(*) FROM latest_v2);";
        cmd.Parameters.AddWithValue("bandType", slice.BandType);
        cmd.Parameters.AddWithValue("snapshotDate", slice.SnapshotDate);
        cmd.Parameters.AddWithValue("scope", slice.RankingScope);
        cmd.Parameters.AddWithValue("comboId", slice.ComboId);
        cmd.Parameters.AddWithValue("computedAt", slice.ComputedAt);
        cmd.Parameters.AddWithValue("legacyRows", slice.LegacyRows);
        cmd.Parameters.AddWithValue("missingRows", slice.MissingV2Rows);

        ct.ThrowIfCancellationRequested();
        using var registration = ct.Register(static state => ((NpgsqlCommand)state!).Cancel(), cmd);
        try
        {
            BandRankHistoryV2BackfillSlice result;
            using (var reader = cmd.ExecuteReader())
            {
                reader.Read();
                result = new BandRankHistoryV2BackfillSlice
                {
                    BandType = slice.BandType,
                    SnapshotDate = slice.SnapshotDate.ToString("yyyy-MM-dd"),
                    RankingScope = slice.RankingScope,
                    ComboId = slice.ComboId,
                    LegacyRows = slice.LegacyRows,
                    ExistingV2Rows = slice.ExistingV2Rows,
                    MissingV2Rows = slice.MissingV2Rows,
                    CompleteSnapshots = slice.CompleteSnapshots,
                    SnapshotRowsUpserted = reader.GetInt64(0),
                    PointRowsInserted = reader.GetInt64(1),
                    LatestRowsUpserted = reader.GetInt64(2),
                };
            }
            tx.Commit();
            return result;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    private static void ConfigureCommandTimeout(NpgsqlCommand cmd, int commandTimeoutSeconds)
    {
        if (commandTimeoutSeconds > 0)
            cmd.CommandTimeout = commandTimeoutSeconds;
    }

    private static List<BandRankHistoryChunkKey> GetBandRankHistoryChunks(
        NpgsqlConnection conn,
        string bandType,
        string rankingsTable,
        string statsTable,
        BandRankHistorySnapshotOptions options,
        int commandTimeoutSeconds)
    {
        var effectiveChunkSize = Math.Max(1, options.ChunkSize);
        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, commandTimeoutSeconds);
        if (!options.RangeChunkingEnabled)
        {
            cmd.CommandText = $@"
                SELECT ranking_scope, combo_id, 0 AS chunk_ordinal, NULL::text AS team_key_start,
                       NULL::text AS team_key_end, total_teams::bigint AS estimated_rows, 0::bigint AS source_generation
                FROM {BandRankingStorageNames.QuoteIdentifier(statsTable)}
                WHERE band_type = @bandType
                ORDER BY ranking_scope, combo_id";
        }
        else
        {
            cmd.CommandText = $@"
                WITH scoped_stats AS (
                    SELECT ranking_scope, combo_id, GREATEST(total_teams, 0)::bigint AS estimated_rows
                    FROM {BandRankingStorageNames.QuoteIdentifier(statsTable)}
                    WHERE band_type = @bandType
                ), numbered AS (
                    SELECT
                        src.ranking_scope,
                        src.combo_id,
                        ((row_number() OVER (PARTITION BY src.ranking_scope, src.combo_id ORDER BY src.team_key) - 1) / @chunkSize)::int AS chunk_ordinal,
                        src.team_key,
                        NULLIF(src.ranking_generation, 0) AS ranking_generation
                    FROM {BandRankingStorageNames.QuoteIdentifier(rankingsTable)} src
                    JOIN scoped_stats stats
                      ON stats.ranking_scope = src.ranking_scope
                     AND stats.combo_id = src.combo_id
                    WHERE src.band_type = @bandType
                ), range_chunks AS (
                    SELECT
                        ranking_scope,
                        combo_id,
                        chunk_ordinal,
                        min(team_key) AS team_key_start,
                        max(team_key) AS team_key_end,
                        count(*)::bigint AS estimated_rows,
                        COALESCE(max(ranking_generation), 0)::bigint AS source_generation
                    FROM numbered
                    GROUP BY ranking_scope, combo_id, chunk_ordinal
                ), empty_chunks AS (
                    SELECT
                        stats.ranking_scope,
                        stats.combo_id,
                        0 AS chunk_ordinal,
                        NULL::text AS team_key_start,
                        NULL::text AS team_key_end,
                        stats.estimated_rows,
                        0::bigint AS source_generation
                    FROM scoped_stats stats
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM {BandRankingStorageNames.QuoteIdentifier(rankingsTable)} src
                        WHERE src.band_type = @bandType
                          AND src.ranking_scope = stats.ranking_scope
                          AND src.combo_id = stats.combo_id)
                )
                SELECT ranking_scope, combo_id, chunk_ordinal, team_key_start, team_key_end, estimated_rows, source_generation
                FROM range_chunks
                UNION ALL
                SELECT ranking_scope, combo_id, chunk_ordinal, team_key_start, team_key_end, estimated_rows, source_generation
                FROM empty_chunks
                ORDER BY ranking_scope, combo_id, chunk_ordinal";
            cmd.Parameters.AddWithValue("chunkSize", effectiveChunkSize);
        }
        cmd.Parameters.AddWithValue("bandType", bandType);

        var chunks = new List<BandRankHistoryChunkKey>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            chunks.Add(new BandRankHistoryChunkKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt64(6)));
        }
        return chunks;
    }

    private static void SeedBandRankHistoryLatestState(
        NpgsqlConnection conn,
        string bandType,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, commandTimeoutSeconds);
        cmd.CommandText = $@"
            INSERT INTO band_team_rank_history_latest (
                band_type, ranking_scope, combo_id, team_key, team_members,
                songs_played, total_charted_songs, coverage, raw_skill_rating,
                adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating,
                computed_at, snapshot_date, fingerprint, updated_at)
            SELECT DISTINCT ON (band_type, ranking_scope, combo_id, team_key)
                h.band_type,
                h.ranking_scope,
                h.combo_id,
                h.team_key,
                h.team_members,
                h.songs_played,
                h.total_charted_songs,
                h.coverage,
                h.raw_skill_rating,
                h.adjusted_skill_rating,
                h.adjusted_skill_rank,
                h.weighted_rating,
                h.weighted_rank,
                h.fc_rate,
                h.fc_rate_rank,
                h.total_score,
                h.total_score_rank,
                h.avg_accuracy,
                h.full_combo_count,
                h.avg_stars,
                h.best_rank,
                h.avg_rank,
                h.raw_weighted_rating,
                h.computed_at,
                h.snapshot_date,
                {BandRankHistoryFingerprintExpression("h")},
                now()
            FROM band_team_rank_history h
            WHERE h.band_type = @bandType
              AND NOT EXISTS (
                SELECT 1 FROM band_team_rank_history_latest latest
                WHERE latest.band_type = h.band_type
                  AND latest.ranking_scope = h.ranking_scope
                  AND latest.combo_id = h.combo_id
                  AND latest.team_key = h.team_key)
            ORDER BY band_type, ranking_scope, combo_id, team_key, snapshot_date DESC
            ON CONFLICT (band_type, ranking_scope, combo_id, team_key) DO NOTHING";
        cmd.Parameters.AddWithValue("bandType", bandType);
        ct.ThrowIfCancellationRequested();
        using var registration = ct.Register(static state => ((NpgsqlCommand)state!).Cancel(), cmd);
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    private static BandRankHistoryChunkResult SnapshotBandRankHistoryChunk(
        NpgsqlConnection conn,
        string rankingsTable,
        string statsTable,
        string bandType,
        string rankingScope,
        string comboId,
        string? teamKeyStart,
        string? teamKeyEnd,
        long sourceGeneration,
        DateOnly today,
        BandRankHistorySnapshotOptions options,
        CancellationToken ct)
    {
        using var tx = conn.BeginTransaction();
        if (options.SynchronousCommitOff)
        {
            using var syncCmd = conn.CreateCommand();
            syncCmd.Transaction = tx;
            syncCmd.CommandText = "SET LOCAL synchronous_commit = off";
            syncCmd.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, options.CommandTimeoutSeconds);
        cmd.Transaction = tx;
        cmd.CommandText = $@"
            WITH src AS (
                SELECT
                    src.*,
                    COALESCE(NULLIF(src.row_fingerprint, ''), {BandRankHistoryFingerprintExpression("src")}) AS fingerprint
                FROM {BandRankingStorageNames.QuoteIdentifier(rankingsTable)} src
                WHERE src.band_type = @bandType
                  AND src.ranking_scope = @scope
                  AND src.combo_id = @comboId
                                    AND (@teamKeyStart IS NULL OR src.team_key >= @teamKeyStart)
                                    AND (@teamKeyEnd IS NULL OR src.team_key <= @teamKeyEnd)
            ), stats AS (
                SELECT total_teams, computed_at
                FROM {BandRankingStorageNames.QuoteIdentifier(statsTable)}
                WHERE band_type = @bandType
                  AND ranking_scope = @scope
                  AND combo_id = @comboId
            ), changed AS (
                SELECT src.*, stats.total_teams
                FROM src
                CROSS JOIN stats
                LEFT JOIN band_team_rank_history_latest latest
                    ON latest.band_type = src.band_type
                   AND latest.ranking_scope = src.ranking_scope
                   AND latest.combo_id = src.combo_id
                   AND latest.team_key = src.team_key
                LEFT JOIN band_team_rank_history_latest_v2 latest_v2
                    ON latest_v2.band_type = src.band_type
                   AND latest_v2.ranking_scope = src.ranking_scope
                   AND latest_v2.combo_id = src.combo_id
                   AND latest_v2.team_key = src.team_key
                WHERE NOT @useLatestState
                   OR (
                       @useV2LatestState
                       AND (
                           latest_v2.team_key IS NULL
                           OR latest_v2.row_fingerprint IS DISTINCT FROM src.fingerprint
                       )
                   )
                   OR (
                       NOT @useV2LatestState
                       AND (
                           latest.team_key IS NULL
                           OR latest.fingerprint IS DISTINCT FROM src.fingerprint
                       )
                   )
            ), wide AS (
                INSERT INTO band_team_rank_history (
                    band_type, ranking_scope, combo_id, team_key, team_members,
                    songs_played, total_charted_songs, coverage, raw_skill_rating,
                    adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                    fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                    full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating,
                    computed_at, snapshot_date)
                SELECT
                    band_type, ranking_scope, combo_id, team_key, team_members,
                    songs_played, total_charted_songs, coverage, raw_skill_rating,
                    adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                    fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                    full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating,
                    computed_at, @today
                FROM changed
                WHERE @writeWide
                ON CONFLICT (band_type, ranking_scope, combo_id, team_key, snapshot_date) DO UPDATE SET
                    team_members = EXCLUDED.team_members,
                    songs_played = EXCLUDED.songs_played,
                    total_charted_songs = EXCLUDED.total_charted_songs,
                    coverage = EXCLUDED.coverage,
                    raw_skill_rating = EXCLUDED.raw_skill_rating,
                    adjusted_skill_rating = EXCLUDED.adjusted_skill_rating,
                    adjusted_skill_rank = EXCLUDED.adjusted_skill_rank,
                    weighted_rating = EXCLUDED.weighted_rating,
                    weighted_rank = EXCLUDED.weighted_rank,
                    fc_rate = EXCLUDED.fc_rate,
                    fc_rate_rank = EXCLUDED.fc_rate_rank,
                    total_score = EXCLUDED.total_score,
                    total_score_rank = EXCLUDED.total_score_rank,
                    avg_accuracy = EXCLUDED.avg_accuracy,
                    full_combo_count = EXCLUDED.full_combo_count,
                    avg_stars = EXCLUDED.avg_stars,
                    best_rank = EXCLUDED.best_rank,
                    avg_rank = EXCLUDED.avg_rank,
                    raw_weighted_rating = EXCLUDED.raw_weighted_rating,
                    computed_at = EXCLUDED.computed_at
                RETURNING 1
            ), points AS (
                INSERT INTO band_team_rank_history_points (
                    band_type, ranking_scope, combo_id, team_key, snapshot_date, snapshot_taken_at,
                    adjusted_skill_rank, weighted_rank, fc_rate_rank, total_score_rank,
                    adjusted_skill_rating, weighted_rating, fc_rate, total_score,
                    songs_played, coverage, full_combo_count, total_charted_songs,
                    total_ranked_teams, raw_weighted_rating, raw_skill_rating)
                SELECT
                    band_type, ranking_scope, combo_id, team_key, @today, computed_at,
                    adjusted_skill_rank, weighted_rank, fc_rate_rank, total_score_rank,
                    adjusted_skill_rating, weighted_rating, fc_rate, total_score,
                    songs_played, coverage, full_combo_count, total_charted_songs,
                    total_teams, raw_weighted_rating, raw_skill_rating
                FROM changed
                WHERE @writeNarrow
                ON CONFLICT (band_type, ranking_scope, combo_id, team_key, snapshot_date) DO UPDATE SET
                    snapshot_taken_at = EXCLUDED.snapshot_taken_at,
                    adjusted_skill_rank = EXCLUDED.adjusted_skill_rank,
                    weighted_rank = EXCLUDED.weighted_rank,
                    fc_rate_rank = EXCLUDED.fc_rate_rank,
                    total_score_rank = EXCLUDED.total_score_rank,
                    adjusted_skill_rating = EXCLUDED.adjusted_skill_rating,
                    weighted_rating = EXCLUDED.weighted_rating,
                    fc_rate = EXCLUDED.fc_rate,
                    total_score = EXCLUDED.total_score,
                    songs_played = EXCLUDED.songs_played,
                    coverage = EXCLUDED.coverage,
                    full_combo_count = EXCLUDED.full_combo_count,
                    total_charted_songs = EXCLUDED.total_charted_songs,
                    total_ranked_teams = EXCLUDED.total_ranked_teams,
                    raw_weighted_rating = EXCLUDED.raw_weighted_rating,
                    raw_skill_rating = EXCLUDED.raw_skill_rating
                RETURNING 1
            ), latest AS (
                INSERT INTO band_team_rank_history_latest (
                    band_type, ranking_scope, combo_id, team_key, team_members,
                    songs_played, total_charted_songs, coverage, raw_skill_rating,
                    adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                    fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                    full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating,
                    computed_at, snapshot_date, fingerprint, updated_at)
                SELECT
                    band_type, ranking_scope, combo_id, team_key, team_members,
                    songs_played, total_charted_songs, coverage, raw_skill_rating,
                    adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                    fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                    full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating,
                    computed_at, @today, fingerprint, now()
                FROM changed
                WHERE @writeLegacyLatest
                ON CONFLICT (band_type, ranking_scope, combo_id, team_key) DO UPDATE SET
                    team_members = EXCLUDED.team_members,
                    songs_played = EXCLUDED.songs_played,
                    total_charted_songs = EXCLUDED.total_charted_songs,
                    coverage = EXCLUDED.coverage,
                    raw_skill_rating = EXCLUDED.raw_skill_rating,
                    adjusted_skill_rating = EXCLUDED.adjusted_skill_rating,
                    adjusted_skill_rank = EXCLUDED.adjusted_skill_rank,
                    weighted_rating = EXCLUDED.weighted_rating,
                    weighted_rank = EXCLUDED.weighted_rank,
                    fc_rate = EXCLUDED.fc_rate,
                    fc_rate_rank = EXCLUDED.fc_rate_rank,
                    total_score = EXCLUDED.total_score,
                    total_score_rank = EXCLUDED.total_score_rank,
                    avg_accuracy = EXCLUDED.avg_accuracy,
                    full_combo_count = EXCLUDED.full_combo_count,
                    avg_stars = EXCLUDED.avg_stars,
                    best_rank = EXCLUDED.best_rank,
                    avg_rank = EXCLUDED.avg_rank,
                    raw_weighted_rating = EXCLUDED.raw_weighted_rating,
                    computed_at = EXCLUDED.computed_at,
                    snapshot_date = EXCLUDED.snapshot_date,
                    fingerprint = EXCLUDED.fingerprint,
                    updated_at = now()
                RETURNING 1
            ), stats_history AS (
                INSERT INTO band_team_ranking_stats_history (
                    band_type, ranking_scope, combo_id, total_teams, computed_at, snapshot_date)
                SELECT @bandType, @scope, @comboId, total_teams, computed_at, @today
                FROM stats
                WHERE @writeWide OR @writeNarrow
                ON CONFLICT (band_type, ranking_scope, combo_id, snapshot_date) DO UPDATE SET
                    total_teams = EXCLUDED.total_teams,
                    computed_at = EXCLUDED.computed_at
                RETURNING 1
            ), snapshot_v2 AS (
                INSERT INTO band_team_rank_history_snapshot_v2 (
                    generation_id, band_type, ranking_scope, combo_id, snapshot_date,
                    computed_at, source_row_count, changed_row_count, status, completed_at, updated_at)
                SELECT
                    COALESCE(NULLIF(@sourceGeneration, 0), NULLIF((SELECT max(ranking_generation) FROM src), 0), 0),
                    @bandType,
                    @scope,
                    @comboId,
                    @today,
                    stats.computed_at,
                    (SELECT count(*) FROM src),
                    (SELECT count(*) FROM changed),
                    'complete',
                    now(),
                    now()
                FROM stats
                WHERE @writeV2
                ON CONFLICT (band_type, ranking_scope, combo_id, snapshot_date) DO UPDATE SET
                    generation_id = EXCLUDED.generation_id,
                    computed_at = EXCLUDED.computed_at,
                    source_row_count = EXCLUDED.source_row_count,
                    changed_row_count = EXCLUDED.changed_row_count,
                    status = EXCLUDED.status,
                    completed_at = EXCLUDED.completed_at,
                    updated_at = now()
                RETURNING snapshot_id, generation_id
            ), points_v2 AS (
                INSERT INTO band_team_rank_history_points_v2 (
                    band_type, ranking_scope, combo_id, team_key, snapshot_date, snapshot_id, generation_id,
                    snapshot_taken_at, row_fingerprint, adjusted_skill_rank, weighted_rank, fc_rate_rank,
                    total_score_rank, adjusted_skill_rating, weighted_rating, fc_rate, total_score,
                    songs_played, coverage, full_combo_count, total_charted_songs, total_ranked_teams,
                    raw_weighted_rating, raw_skill_rating)
                SELECT
                    band_type, ranking_scope, combo_id, team_key, @today,
                    (SELECT snapshot_id FROM snapshot_v2),
                    COALESCE(NULLIF(ranking_generation, 0), (SELECT generation_id FROM snapshot_v2)),
                    computed_at,
                    fingerprint,
                    adjusted_skill_rank,
                    weighted_rank,
                    fc_rate_rank,
                    total_score_rank,
                    adjusted_skill_rating,
                    weighted_rating,
                    fc_rate,
                    total_score,
                    songs_played,
                    coverage,
                    full_combo_count,
                    total_charted_songs,
                    total_teams,
                    raw_weighted_rating,
                    raw_skill_rating
                FROM changed
                WHERE @writeV2
                ON CONFLICT (band_type, ranking_scope, combo_id, team_key, snapshot_date) DO UPDATE SET
                    snapshot_id = EXCLUDED.snapshot_id,
                    generation_id = EXCLUDED.generation_id,
                    snapshot_taken_at = EXCLUDED.snapshot_taken_at,
                    row_fingerprint = EXCLUDED.row_fingerprint,
                    adjusted_skill_rank = EXCLUDED.adjusted_skill_rank,
                    weighted_rank = EXCLUDED.weighted_rank,
                    fc_rate_rank = EXCLUDED.fc_rate_rank,
                    total_score_rank = EXCLUDED.total_score_rank,
                    adjusted_skill_rating = EXCLUDED.adjusted_skill_rating,
                    weighted_rating = EXCLUDED.weighted_rating,
                    fc_rate = EXCLUDED.fc_rate,
                    total_score = EXCLUDED.total_score,
                    songs_played = EXCLUDED.songs_played,
                    coverage = EXCLUDED.coverage,
                    full_combo_count = EXCLUDED.full_combo_count,
                    total_charted_songs = EXCLUDED.total_charted_songs,
                    total_ranked_teams = EXCLUDED.total_ranked_teams,
                    raw_weighted_rating = EXCLUDED.raw_weighted_rating,
                    raw_skill_rating = EXCLUDED.raw_skill_rating
                RETURNING 1
            ), latest_v2 AS (
                INSERT INTO band_team_rank_history_latest_v2 (
                    band_type, ranking_scope, combo_id, team_key, generation_id,
                    snapshot_id, snapshot_date, row_fingerprint, updated_at)
                SELECT
                    band_type,
                    ranking_scope,
                    combo_id,
                    team_key,
                    COALESCE(NULLIF(ranking_generation, 0), (SELECT generation_id FROM snapshot_v2)),
                    (SELECT snapshot_id FROM snapshot_v2),
                    @today,
                    fingerprint,
                    now()
                FROM changed
                WHERE @writeV2
                ON CONFLICT (band_type, ranking_scope, combo_id, team_key) DO UPDATE SET
                    generation_id = EXCLUDED.generation_id,
                    snapshot_id = EXCLUDED.snapshot_id,
                    snapshot_date = EXCLUDED.snapshot_date,
                    row_fingerprint = EXCLUDED.row_fingerprint,
                    updated_at = now()
                WHERE band_team_rank_history_latest_v2.snapshot_date <= EXCLUDED.snapshot_date
                RETURNING 1
            )
            SELECT
                (SELECT count(*) FROM src) AS rows_scanned,
                (SELECT count(*) FROM changed) AS rows_inserted;";

        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", comboId);
        cmd.Parameters.Add("teamKeyStart", NpgsqlDbType.Text).Value = string.IsNullOrEmpty(teamKeyStart) ? DBNull.Value : teamKeyStart;
        cmd.Parameters.Add("teamKeyEnd", NpgsqlDbType.Text).Value = string.IsNullOrEmpty(teamKeyEnd) ? DBNull.Value : teamKeyEnd;
        cmd.Parameters.AddWithValue("sourceGeneration", sourceGeneration);
        cmd.Parameters.AddWithValue("today", today);
        cmd.Parameters.AddWithValue("useLatestState", options.UseLatestState);
        var writeMode = options.WriteMode;
        var (writeLegacy, writeV2, useV2LatestState) = writeMode switch
        {
            BandRankHistoryWriteMode.Legacy => (true, false, false),
            BandRankHistoryWriteMode.Dual => (true, true, false),
            BandRankHistoryWriteMode.V2Only => (false, true, true),
            _ => throw new ArgumentOutOfRangeException(nameof(options.WriteMode), options.WriteMode, "Unsupported band rank-history write mode."),
        };
        cmd.Parameters.AddWithValue("useV2LatestState", useV2LatestState);
        cmd.Parameters.AddWithValue("writeWide", writeLegacy && options.UseWideHistoryCompatibilityWrite);
        cmd.Parameters.AddWithValue("writeNarrow", writeLegacy && options.UseNarrowHistory);
        cmd.Parameters.AddWithValue("writeLegacyLatest", writeLegacy);
        cmd.Parameters.AddWithValue("writeV2", writeV2);

        ct.ThrowIfCancellationRequested();
        using var registration = ct.Register(static state => ((NpgsqlCommand)state!).Cancel(), cmd);
        BandRankHistoryChunkResult result;
        try
        {
            using var reader = cmd.ExecuteReader();
            reader.Read();
            result = new BandRankHistoryChunkResult(reader.GetInt64(0), reader.GetInt64(1));
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        tx.Commit();
        return result;
    }

    public int CleanupBandRankHistoryRetention(
        string bandType,
        int retentionDays = 365,
        int commandTimeoutSeconds = 0,
        CancellationToken ct = default,
        int batchSize = 5000,
        int maxBatches = 1)
    {
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (maxBatches <= 0) throw new ArgumentOutOfRangeException(nameof(maxBatches));

        using var conn = _ds.OpenConnection();
        return CleanupBandRankHistoryRetention(conn, bandType, retentionDays, commandTimeoutSeconds, ct, batchSize, maxBatches);
    }

    private static int CleanupBandRankHistoryRetention(
        NpgsqlConnection conn,
        string bandType,
        int retentionDays,
        int commandTimeoutSeconds,
        CancellationToken ct,
        int batchSize = FSTService.DatabaseMaintenanceOptions.DefaultCleanupBatchSize,
        int maxBatches = FSTService.DatabaseMaintenanceOptions.DefaultCleanupMaxBatches)
    {
        if (retentionDays <= 0)
            return 0;

        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-retentionDays);
        var totalDeleted = 0;
        totalDeleted += CleanupBandRankHistoryRetentionTable(
            conn,
            "band_team_rank_history_points",
            bandType,
            cutoff,
            true,
            batchSize,
            maxBatches,
            commandTimeoutSeconds,
            ct);
        totalDeleted += CleanupBandRankHistoryRetentionTable(
            conn,
            "band_team_rank_history",
            bandType,
            cutoff,
            true,
            batchSize,
            maxBatches,
            commandTimeoutSeconds,
            ct);
        totalDeleted += CleanupBandRankHistoryRetentionTable(
            conn,
            "band_team_ranking_stats_history",
            bandType,
            cutoff,
            false,
            batchSize,
            maxBatches,
            commandTimeoutSeconds,
            ct);
        return totalDeleted;
    }

    private static int CleanupBandRankHistoryRetentionTable(
        NpgsqlConnection conn,
        string tableName,
        string bandType,
        DateOnly cutoff,
        bool hasTeamKey,
        int batchSize,
        int maxBatches,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        var totalDeleted = 0;
        var teamKeyPredicate = hasTeamKey ? "AND newer.team_key = history.team_key" : string.Empty;
        var orderByTeamKey = hasTeamKey ? ", history.team_key ASC" : string.Empty;

        for (var batch = 0; batch < maxBatches; batch++)
        {
            ct.ThrowIfCancellationRequested();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            ConfigureCommandTimeout(cmd, commandTimeoutSeconds);
            cmd.CommandText = $@"
                WITH doomed AS (
                    SELECT history.ctid
                    FROM {tableName} history
                    WHERE history.band_type = @bandType
                      AND history.snapshot_date < @cutoff
                      AND EXISTS (
                        SELECT 1
                        FROM {tableName} newer
                        WHERE newer.band_type = history.band_type
                          AND newer.ranking_scope = history.ranking_scope
                          AND newer.combo_id = history.combo_id
                          {teamKeyPredicate}
                          AND newer.snapshot_date > history.snapshot_date
                          AND newer.snapshot_date <= @cutoff
                      )
                    ORDER BY history.snapshot_date ASC, history.ranking_scope ASC, history.combo_id ASC{orderByTeamKey}
                    LIMIT @batchSize
                )
                DELETE FROM {tableName} history
                USING doomed
                WHERE history.ctid = doomed.ctid";
            cmd.Parameters.AddWithValue("bandType", bandType);
            cmd.Parameters.AddWithValue("cutoff", cutoff);
            cmd.Parameters.AddWithValue("batchSize", batchSize);
            var deleted = ExecuteNonQueryWithCancellation(cmd, ct);
            tx.Commit();
            totalDeleted += deleted;

            if (deleted < batchSize)
                break;
        }

        return totalDeleted;
    }

    private static int ExecuteNonQueryWithCancellation(NpgsqlCommand cmd, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var registration = ct.Register(static state => ((NpgsqlCommand)state!).Cancel(), cmd);
        try
        {
            return cmd.ExecuteNonQuery();
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    public BandRankHistoryJobInfo EnqueueBandRankHistoryJob(long scrapeId, string bandType, DateOnly snapshotDate, string mode, bool coalesceSameDay = true)
    {
        using var conn = _ds.OpenConnection();
        EnsureBandRankHistoryPollingSchema(conn);
        var sourceGeneration = ReadCurrentBandRankingGeneration(conn, bandType);

        if (coalesceSameDay)
        {
            using var supersede = conn.CreateCommand();
            supersede.CommandText = @"
                UPDATE band_rank_history_jobs
                SET status = 'superseded', superseded_at = now(), updated_at = now(), last_error = 'Superseded by newer same-day history job.'
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                  AND scrape_id < @scrapeId
                  AND status IN ('queued', 'running', 'paused', 'failed')";
            supersede.Parameters.AddWithValue("bandType", bandType);
            supersede.Parameters.AddWithValue("snapshotDate", snapshotDate);
            supersede.Parameters.AddWithValue("scrapeId", scrapeId);
            supersede.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO band_rank_history_jobs (scrape_id, snapshot_date, band_type, mode, status, source_generation, updated_at)
            VALUES (@scrapeId, @snapshotDate, @bandType, @mode, 'queued', @sourceGeneration, now())
            ON CONFLICT (scrape_id, band_type, snapshot_date) DO UPDATE SET
                mode = EXCLUDED.mode,
                status = CASE
                    WHEN band_rank_history_jobs.status = 'complete' THEN band_rank_history_jobs.status
                    ELSE 'queued'
                END,
                source_generation = CASE
                    WHEN band_rank_history_jobs.status = 'complete' THEN band_rank_history_jobs.source_generation
                    ELSE EXCLUDED.source_generation
                END,
                updated_at = now(),
                last_error = NULL
            RETURNING job_id, scrape_id, snapshot_date, band_type, mode, status, started_at, completed_at,
                      failed_at, paused_at, superseded_at, last_error, attempts, chunks_total,
                      chunks_completed, rows_scanned, rows_inserted, rows_skipped,
                      current_ranking_scope, current_combo_id, updated_at";
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("mode", mode);
        cmd.Parameters.AddWithValue("sourceGeneration", sourceGeneration);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return ReadBandRankHistoryJob(reader);
    }

    public BandRankHistoryJobInfo? GetNextBandRankHistoryJob(int maxAttempts = int.MaxValue, TimeSpan? retryDelay = null)
    {
        var effectiveMaxAttempts = Math.Max(1, maxAttempts);
        var effectiveRetryDelay = retryDelay ?? TimeSpan.Zero;

        using var conn = _ds.OpenConnection();
        using (var tx = conn.BeginTransaction())
        {
            EnsureBandRankHistoryTables(conn, tx);
            tx.Commit();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT job_id, scrape_id, snapshot_date, band_type, mode, status, started_at, completed_at,
                   failed_at, paused_at, superseded_at, last_error, attempts, chunks_total,
                   chunks_completed, rows_scanned, rows_inserted, rows_skipped,
                   current_ranking_scope, current_combo_id, updated_at
            FROM band_rank_history_jobs
            WHERE status IN ('queued', 'paused')
               OR (
                   status = 'failed'
                   AND attempts < @maxAttempts
                   AND updated_at <= now() - @retryDelay
               )
            ORDER BY CASE WHEN status IN ('queued', 'paused') THEN 0 ELSE 1 END,
                     snapshot_date DESC, scrape_id DESC, job_id ASC
            LIMIT 1";
        cmd.Parameters.AddWithValue("maxAttempts", effectiveMaxAttempts);
        cmd.Parameters.AddWithValue("retryDelay", effectiveRetryDelay);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadBandRankHistoryJob(reader) : null;
    }

    public int RecoverStaleBandRankHistoryJobs(TimeSpan staleAfter, TimeSpan maxCatchupAge)
    {
        using var conn = _ds.OpenConnection();
        using (var tx = conn.BeginTransaction())
        {
            EnsureBandRankHistoryTables(conn, tx);
            tx.Commit();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH stale_running AS (
                UPDATE band_rank_history_jobs
                SET status = 'paused',
                    paused_at = now(),
                    updated_at = now(),
                    last_error = 'Recovered stale running job after worker inactivity.'
                WHERE status = 'running'
                  AND updated_at < now() - @staleAfter::interval
                RETURNING job_id
            ), stale_chunks AS (
                UPDATE band_rank_history_job_chunks chunks
                SET status = 'queued',
                    updated_at = now(),
                    last_error = 'Recovered stale running chunk after worker inactivity.'
                FROM stale_running jobs
                WHERE chunks.job_id = jobs.job_id
                  AND chunks.status = 'running'
                RETURNING chunks.job_id
            ), aged_jobs AS (
                UPDATE band_rank_history_jobs
                SET status = 'superseded',
                    superseded_at = now(),
                    updated_at = now(),
                    last_error = 'Superseded because catch-up age exceeded the configured window.'
                WHERE status IN ('queued', 'paused', 'failed')
                  AND snapshot_date < (CURRENT_DATE - @maxCatchupAge::interval)::date
                RETURNING job_id
            )
            SELECT (SELECT count(*) FROM stale_running) + (SELECT count(*) FROM aged_jobs)";
        cmd.Parameters.AddWithValue("staleAfter", staleAfter);
        cmd.Parameters.AddWithValue("maxCatchupAge", maxCatchupAge);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public bool TryStartBandRankHistoryJob(long jobId, int maxAttempts = int.MaxValue)
    {
        var effectiveMaxAttempts = Math.Max(1, maxAttempts);
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE band_rank_history_jobs
            SET status = 'running',
                started_at = COALESCE(started_at, now()),
                failed_at = NULL,
                paused_at = NULL,
                last_error = NULL,
                current_ranking_scope = NULL,
                current_combo_id = NULL,
                attempts = attempts + 1,
                updated_at = now()
            WHERE job_id = @jobId
              AND (
                  status IN ('queued', 'paused')
                  OR (status = 'failed' AND attempts < @maxAttempts)
              )";
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.Parameters.AddWithValue("maxAttempts", effectiveMaxAttempts);
        return cmd.ExecuteNonQuery() == 1;
    }

    public void CompleteBandRankHistoryJob(long jobId, BandRankHistorySnapshotResult result)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH counters AS (
                SELECT count(*)::int AS chunks_total,
                       count(*) FILTER (WHERE status = 'complete')::int AS chunks_completed,
                       COALESCE(sum(rows_scanned), 0)::bigint AS rows_scanned,
                       COALESCE(sum(rows_inserted), 0)::bigint AS rows_inserted,
                       COALESCE(sum(rows_skipped), 0)::bigint AS rows_skipped
                FROM band_rank_history_job_chunks
                WHERE job_id = @jobId
            )
            UPDATE band_rank_history_jobs
            SET status = 'complete', completed_at = now(), updated_at = now(), last_error = NULL,
                chunks_total = CASE WHEN counters.chunks_total > 0 THEN counters.chunks_total ELSE @chunksTotal END,
                chunks_completed = CASE WHEN counters.chunks_total > 0 THEN counters.chunks_completed ELSE @chunksCompleted END,
                rows_scanned = CASE WHEN counters.chunks_total > 0 THEN counters.rows_scanned ELSE @rowsScanned END,
                rows_inserted = CASE WHEN counters.chunks_total > 0 THEN counters.rows_inserted ELSE @rowsInserted END,
                rows_skipped = CASE WHEN counters.chunks_total > 0 THEN counters.rows_skipped ELSE @rowsSkipped END,
                current_ranking_scope = NULL, current_combo_id = NULL
            FROM counters
            WHERE job_id = @jobId";
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.Parameters.AddWithValue("chunksTotal", result.ChunksTotal);
        cmd.Parameters.AddWithValue("chunksCompleted", result.ChunksCompleted);
        cmd.Parameters.AddWithValue("rowsScanned", result.RowsScanned);
        cmd.Parameters.AddWithValue("rowsInserted", result.RowsInserted);
        cmd.Parameters.AddWithValue("rowsSkipped", result.RowsSkipped);
        cmd.ExecuteNonQuery();
    }

    public void PauseBandRankHistoryJob(long jobId, string? reason = null)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE band_rank_history_jobs
            SET status = 'paused', paused_at = now(), updated_at = now(), last_error = @reason
            WHERE job_id = @jobId AND status = 'running';

            UPDATE band_rank_history_job_chunks
            SET status = 'queued', updated_at = now(), last_error = @reason
            WHERE job_id = @jobId AND status = 'running';";
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void FailBandRankHistoryJob(long jobId, string error)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE band_rank_history_jobs
            SET status = 'failed', failed_at = now(), updated_at = now(), last_error = @error
            WHERE job_id = @jobId;

            UPDATE band_rank_history_job_chunks
            SET status = 'failed', updated_at = now(), last_error = @error
            WHERE job_id = @jobId AND status = 'running';";
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.Parameters.AddWithValue("error", error);
        cmd.ExecuteNonQuery();
    }

    private static BandRankHistoryJobInfo ReadBandRankHistoryJob(NpgsqlDataReader reader) => new()
    {
        JobId = reader.GetInt64(0),
        ScrapeId = reader.GetInt64(1),
        SnapshotDate = reader.GetDateTime(2).ToString("yyyy-MM-dd"),
        BandType = reader.GetString(3),
        Mode = reader.GetString(4),
        Status = reader.GetString(5),
        StartedAt = reader.IsDBNull(6) ? null : reader.GetDateTime(6).ToString("o"),
        CompletedAt = reader.IsDBNull(7) ? null : reader.GetDateTime(7).ToString("o"),
        FailedAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8).ToString("o"),
        PausedAt = reader.IsDBNull(9) ? null : reader.GetDateTime(9).ToString("o"),
        SupersededAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10).ToString("o"),
        LastError = reader.IsDBNull(11) ? null : reader.GetString(11),
        Attempts = reader.GetInt32(12),
        ChunksTotal = reader.GetInt32(13),
        ChunksCompleted = reader.GetInt32(14),
        RowsScanned = reader.GetInt64(15),
        RowsInserted = reader.GetInt64(16),
        RowsSkipped = reader.GetInt64(17),
        CurrentRankingScope = reader.IsDBNull(18) ? null : reader.GetString(18),
        CurrentComboId = reader.IsDBNull(19) ? null : reader.GetString(19),
        UpdatedAt = reader.GetDateTime(20).ToString("o"),
    };

    private static List<BandRankHistoryChunkInfo> EnsureAndGetBandRankHistoryJobChunks(
        NpgsqlConnection conn,
        long jobId,
        string bandType,
        string rankingsTable,
        string statsTable,
        BandRankHistorySnapshotOptions options,
        int commandTimeoutSeconds)
    {
        var jobSourceGeneration = ReadBandRankHistoryJobSourceGeneration(conn, jobId, commandTimeoutSeconds);
        if (!BandRankHistoryJobHasChunks(conn, jobId, commandTimeoutSeconds))
        {
            var chunks = GetBandRankHistoryChunks(conn, bandType, rankingsTable, statsTable, options, commandTimeoutSeconds);

            using var insert = conn.CreateCommand();
            ConfigureCommandTimeout(insert, commandTimeoutSeconds);
            insert.CommandText = @"
                INSERT INTO band_rank_history_job_chunks (
                    job_id, band_type, ranking_scope, combo_id, chunk_ordinal,
                    team_key_start, team_key_end, estimated_rows, source_generation, status, updated_at)
                VALUES (
                    @jobId, @bandType, @scope, @comboId, @chunkOrdinal,
                    @teamKeyStart, @teamKeyEnd, @estimatedRows, @sourceGeneration, 'queued', now())
                ON CONFLICT (job_id, ranking_scope, combo_id, chunk_ordinal) DO NOTHING";
            insert.Parameters.AddWithValue("jobId", jobId);
            insert.Parameters.AddWithValue("bandType", bandType);
            var scopeParam = insert.Parameters.Add("scope", NpgsqlDbType.Text);
            var comboParam = insert.Parameters.Add("comboId", NpgsqlDbType.Text);
            var ordinalParam = insert.Parameters.Add("chunkOrdinal", NpgsqlDbType.Integer);
            var startParam = insert.Parameters.Add("teamKeyStart", NpgsqlDbType.Text);
            var endParam = insert.Parameters.Add("teamKeyEnd", NpgsqlDbType.Text);
            var estimatedRowsParam = insert.Parameters.Add("estimatedRows", NpgsqlDbType.Bigint);
            var generationParam = insert.Parameters.Add("sourceGeneration", NpgsqlDbType.Bigint);
            foreach (var chunk in chunks)
            {
                scopeParam.Value = chunk.RankingScope;
                comboParam.Value = chunk.ComboId;
                ordinalParam.Value = chunk.ChunkOrdinal;
                startParam.Value = string.IsNullOrEmpty(chunk.TeamKeyStart) ? DBNull.Value : chunk.TeamKeyStart;
                endParam.Value = string.IsNullOrEmpty(chunk.TeamKeyEnd) ? DBNull.Value : chunk.TeamKeyEnd;
                estimatedRowsParam.Value = chunk.EstimatedRows;
                generationParam.Value = chunk.SourceGeneration > 0 ? chunk.SourceGeneration : jobSourceGeneration;
                insert.ExecuteNonQuery();
            }
        }
        else if (jobSourceGeneration > 0)
        {
            using var updateGeneration = conn.CreateCommand();
            ConfigureCommandTimeout(updateGeneration, commandTimeoutSeconds);
            updateGeneration.CommandText = @"
                UPDATE band_rank_history_job_chunks
                SET source_generation = @sourceGeneration
                WHERE job_id = @jobId
                  AND source_generation = 0";
            updateGeneration.Parameters.AddWithValue("jobId", jobId);
            updateGeneration.Parameters.AddWithValue("sourceGeneration", jobSourceGeneration);
            updateGeneration.ExecuteNonQuery();
        }

        using (var update = conn.CreateCommand())
        {
            ConfigureCommandTimeout(update, commandTimeoutSeconds);
            update.CommandText = @"
                UPDATE band_rank_history_jobs job
                SET chunks_total = counts.total_count,
                    chunks_completed = counts.completed_count,
                    updated_at = now()
                FROM (
                    SELECT job_id,
                           count(*)::int AS total_count,
                           count(*) FILTER (WHERE status = 'complete')::int AS completed_count
                    FROM band_rank_history_job_chunks
                    WHERE job_id = @jobId
                    GROUP BY job_id
                ) counts
                WHERE job.job_id = counts.job_id";
            update.Parameters.AddWithValue("jobId", jobId);
            update.ExecuteNonQuery();
        }

        using var select = conn.CreateCommand();
        ConfigureCommandTimeout(select, commandTimeoutSeconds);
        select.CommandText = @"
            SELECT job_id, band_type, ranking_scope, combo_id, chunk_ordinal,
                   team_key_start, team_key_end, estimated_rows, source_generation, status
            FROM band_rank_history_job_chunks
            WHERE job_id = @jobId AND status IN ('queued', 'failed')
            ORDER BY estimated_rows NULLS LAST, ranking_scope, combo_id, chunk_ordinal";
        select.Parameters.AddWithValue("jobId", jobId);
        var pending = new List<BandRankHistoryChunkInfo>();
        using var reader = select.ExecuteReader();
        while (reader.Read())
        {
            pending.Add(new BandRankHistoryChunkInfo
            {
                JobId = reader.GetInt64(0),
                BandType = reader.GetString(1),
                RankingScope = reader.GetString(2),
                ComboId = reader.GetString(3),
                ChunkOrdinal = reader.GetInt32(4),
                TeamKeyStart = reader.IsDBNull(5) ? null : reader.GetString(5),
                TeamKeyEnd = reader.IsDBNull(6) ? null : reader.GetString(6),
                EstimatedRows = reader.GetInt64(7),
                SourceGeneration = reader.GetInt64(8),
                Status = reader.GetString(9),
            });
        }

        return pending;
    }

    private static bool BandRankHistoryJobHasChunks(NpgsqlConnection conn, long jobId, int commandTimeoutSeconds)
    {
        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, commandTimeoutSeconds);
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM band_rank_history_job_chunks WHERE job_id = @jobId)";
        cmd.Parameters.AddWithValue("jobId", jobId);
        return Convert.ToBoolean(cmd.ExecuteScalar() ?? false);
    }

    private static long ReadBandRankHistoryJobSourceGeneration(NpgsqlConnection conn, long jobId, int commandTimeoutSeconds)
    {
        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, commandTimeoutSeconds);
        cmd.CommandText = "SELECT source_generation FROM band_rank_history_jobs WHERE job_id = @jobId";
        cmd.Parameters.AddWithValue("jobId", jobId);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private static void MarkBandRankHistoryChunkRunning(NpgsqlConnection conn, long jobId, BandRankHistoryChunkInfo chunk, int commandTimeoutSeconds)
    {
        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, commandTimeoutSeconds);
        cmd.CommandText = @"
            UPDATE band_rank_history_job_chunks
            SET status = 'running', started_at = COALESCE(started_at, now()), updated_at = now(), last_error = NULL
            WHERE job_id = @jobId AND ranking_scope = @scope AND combo_id = @comboId AND chunk_ordinal = @chunkOrdinal;

            UPDATE band_rank_history_jobs
            SET current_ranking_scope = @scope, current_combo_id = @comboId, updated_at = now()
            WHERE job_id = @jobId";
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.Parameters.AddWithValue("scope", chunk.RankingScope);
        cmd.Parameters.AddWithValue("comboId", chunk.ComboId);
        cmd.Parameters.AddWithValue("chunkOrdinal", chunk.ChunkOrdinal);
        cmd.ExecuteNonQuery();
    }

    private static void CompleteBandRankHistoryChunk(
        NpgsqlConnection conn,
        long jobId,
        BandRankHistoryChunkInfo chunk,
        long rowsScanned,
        long rowsInserted,
        long rowsSkipped,
        int commandTimeoutSeconds)
    {
        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, commandTimeoutSeconds);
        cmd.CommandText = @"
            UPDATE band_rank_history_job_chunks
            SET status = 'complete', completed_at = now(), updated_at = now(),
                rows_scanned = @rowsScanned, rows_inserted = @rowsInserted, rows_skipped = @rowsSkipped,
                last_error = NULL
            WHERE job_id = @jobId AND ranking_scope = @scope AND combo_id = @comboId AND chunk_ordinal = @chunkOrdinal;

            UPDATE band_rank_history_jobs job
            SET chunks_completed = counts.completed_count,
                rows_scanned = counters.rows_scanned,
                rows_inserted = counters.rows_inserted,
                rows_skipped = counters.rows_skipped,
                updated_at = now()
            FROM (
                SELECT job_id, count(*) FILTER (WHERE status = 'complete')::int AS completed_count
                FROM band_rank_history_job_chunks
                WHERE job_id = @jobId
                GROUP BY job_id
            ) counts,
            (
                SELECT job_id,
                       COALESCE(sum(rows_scanned), 0)::bigint AS rows_scanned,
                       COALESCE(sum(rows_inserted), 0)::bigint AS rows_inserted,
                       COALESCE(sum(rows_skipped), 0)::bigint AS rows_skipped
                FROM band_rank_history_job_chunks
                WHERE job_id = @jobId
                GROUP BY job_id
            ) counters
            WHERE job.job_id = counts.job_id AND job.job_id = counters.job_id";
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.Parameters.AddWithValue("scope", chunk.RankingScope);
        cmd.Parameters.AddWithValue("comboId", chunk.ComboId);
        cmd.Parameters.AddWithValue("chunkOrdinal", chunk.ChunkOrdinal);
        cmd.Parameters.AddWithValue("rowsScanned", rowsScanned);
        cmd.Parameters.AddWithValue("rowsInserted", rowsInserted);
        cmd.Parameters.AddWithValue("rowsSkipped", rowsSkipped);
        cmd.ExecuteNonQuery();
    }

    public BandRankHistoryStatusDto GetBandRankHistoryStatus(string bandType, string? comboId = null)
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;

        using var conn = _ds.OpenConnection();
        var readSource = _bandRankHistoryOptions.ApiReadSource;
        var historyJobsExists = TableExists(conn, null, "band_rank_history_jobs");

        string? currentComputedAt = null;
        try
        {
            var statsTable = ResolveBandRankingStatsReadTable(conn, bandType);
            if (TableExists(conn, null, statsTable))
            {
                using var current = conn.CreateCommand();
                current.CommandText = $@"
                    SELECT max(computed_at)
                    FROM {BandRankingStorageNames.QuoteIdentifier(statsTable)}
                    WHERE band_type = @bandType
                      AND ranking_scope = @scope
                      AND combo_id = @comboId";
                current.Parameters.AddWithValue("bandType", bandType);
                current.Parameters.AddWithValue("scope", rankingScope);
                current.Parameters.AddWithValue("comboId", normalizedComboId);
                var result = current.ExecuteScalar();
                if (result is DateTime dt)
                    currentComputedAt = dt.ToString("o");
            }
        }
        catch
        {
            // Current ranking tables are created lazily by the ranking publisher.
        }

        string? historyThrough = null;
        if (readSource is BandRankHistoryApiReadSource.V2NarrowOnly or BandRankHistoryApiReadSource.V2NarrowWithLegacyFallback)
        {
            historyThrough = GetBandRankHistoryThroughFromV2(conn, bandType, rankingScope, normalizedComboId);
        }

        if (historyThrough is null && readSource != BandRankHistoryApiReadSource.V2NarrowOnly)
        {
            historyThrough = GetBandRankHistoryThroughFromLegacy(conn, bandType, rankingScope, normalizedComboId, readSource);
        }

        BandRankHistoryJobInfo? job = null;
        if (historyJobsExists)
        {
            using var jobs = conn.CreateCommand();
            jobs.CommandText = @"
                SELECT job_id, scrape_id, snapshot_date, band_type, mode, status, started_at, completed_at,
                       failed_at, paused_at, superseded_at, last_error, attempts, chunks_total,
                       chunks_completed, rows_scanned, rows_inserted, rows_skipped,
                       current_ranking_scope, current_combo_id, updated_at
                FROM band_rank_history_jobs
                WHERE band_type = @bandType
                ORDER BY snapshot_date DESC, scrape_id DESC, job_id DESC
                LIMIT 1";
            jobs.Parameters.AddWithValue("bandType", bandType);
            using var reader = jobs.ExecuteReader();
            if (reader.Read())
                job = ReadBandRankHistoryJob(reader);
        }

        if (job is null)
        {
            return new BandRankHistoryStatusDto
            {
                HistoryStatus = historyThrough is null ? "stale" : "current",
                CurrentRankingsComputedAt = currentComputedAt,
                HistoryComputedThrough = historyThrough,
                HistoryMessage = historyThrough is null ? "No band rank history has been written yet." : null,
            };
        }

        var status = job.Status switch
        {
            "queued" or "running" or "paused" => "catching_up",
            "failed" => "failed",
            "disabled" => "disabled",
            "superseded" => historyThrough is null ? "stale" : "current",
            _ => historyThrough is null ? "stale" : "current",
        };

        return new BandRankHistoryStatusDto
        {
            HistoryStatus = status,
            CurrentRankingsComputedAt = currentComputedAt,
            HistoryComputedThrough = historyThrough,
            HistoryJobUpdatedAt = job.UpdatedAt,
            HistoryMessage = job.Status switch
            {
                "queued" => "Band rank history is queued for background catch-up.",
                "running" => $"Band rank history is catching up ({job.ChunksCompleted}/{job.ChunksTotal} chunks).",
                "paused" => "Band rank history is paused while current scrape work has priority.",
                "failed" => job.LastError ?? "Band rank history catch-up failed.",
                "disabled" => "Band rank history writes are disabled.",
                _ => null,
            },
        };
    }

    private static string? GetBandRankHistoryThroughFromV2(NpgsqlConnection conn, string bandType, string rankingScope, string normalizedComboId)
    {
        if (!TableExists(conn, null, "band_team_rank_history_snapshot_v2"))
            return null;

        using var hist = conn.CreateCommand();
        hist.CommandText = @"
            SELECT max(snapshot_date)
            FROM band_team_rank_history_snapshot_v2
            WHERE band_type = @bandType
              AND ranking_scope = @scope
              AND combo_id = @comboId
              AND status = 'complete'";
        hist.Parameters.AddWithValue("bandType", bandType);
        hist.Parameters.AddWithValue("scope", rankingScope);
        hist.Parameters.AddWithValue("comboId", normalizedComboId);
        return FormatSnapshotDate(hist.ExecuteScalar());
    }

    private static string? GetBandRankHistoryThroughFromLegacy(
        NpgsqlConnection conn,
        string bandType,
        string rankingScope,
        string normalizedComboId,
        BandRankHistoryApiReadSource readSource)
    {
        if (readSource != BandRankHistoryApiReadSource.Wide
            && TableExists(conn, null, "band_team_rank_history_points"))
        {
            var narrowDate = ReadBandRankHistoryMaxSnapshotDate(conn, "band_team_rank_history_points", bandType, rankingScope, normalizedComboId);
            if (narrowDate is not null || readSource == BandRankHistoryApiReadSource.Narrow)
                return narrowDate;
        }

        if (readSource != BandRankHistoryApiReadSource.Narrow
            && TableExists(conn, null, "band_team_ranking_stats_history"))
        {
            return ReadBandRankHistoryMaxSnapshotDate(conn, "band_team_ranking_stats_history", bandType, rankingScope, normalizedComboId);
        }

        return null;
    }

    private static string? ReadBandRankHistoryMaxSnapshotDate(
        NpgsqlConnection conn,
        string tableName,
        string bandType,
        string rankingScope,
        string normalizedComboId)
    {
        using var hist = conn.CreateCommand();
        hist.CommandText = $@"
            SELECT max(snapshot_date)
            FROM {BandRankingStorageNames.QuoteIdentifier(tableName)}
            WHERE band_type = @bandType
              AND ranking_scope = @scope
              AND combo_id = @comboId";
        hist.Parameters.AddWithValue("bandType", bandType);
        hist.Parameters.AddWithValue("scope", rankingScope);
        hist.Parameters.AddWithValue("comboId", normalizedComboId);
        return FormatSnapshotDate(hist.ExecuteScalar());
    }

    private static string? FormatSnapshotDate(object? result) => result switch
    {
        DateOnly date => date.ToString("yyyy-MM-dd"),
        DateTime dt => dt.ToString("yyyy-MM-dd"),
        _ => null,
    };

    public (List<BandTeamRankingDto> Entries, int TotalTeams) GetBandTeamRankings(string bandType, string? comboId = null, string rankBy = "adjusted", int page = 1, int pageSize = 50)
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;
        var totalTeams = GetBandRankingTotalTeams(bandType, rankingScope, normalizedComboId);
        var rankColumn = BandRankColumn(rankBy);

        using var conn = _ds.OpenConnection();
        var rankingsTable = ResolveBandRankingReadTable(conn, bandType);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                r.band_type, r.combo_id, r.team_key, r.team_members, r.songs_played, r.total_charted_songs,
                r.coverage, r.raw_skill_rating, r.adjusted_skill_rating, r.adjusted_skill_rank,
                r.weighted_rating, r.weighted_rank, r.fc_rate, r.fc_rate_rank, r.total_score,
                r.total_score_rank, r.avg_accuracy, r.full_combo_count, r.avg_stars, r.best_rank,
                r.avg_rank, r.raw_weighted_rating, r.computed_at,
                projection.member_instruments_json::text AS member_instruments_json
            FROM {BandRankingStorageNames.QuoteIdentifier(rankingsTable)} r
            LEFT JOIN {BandSearchProjectionBuilder.TeamProjectionTable} projection
              ON projection.band_type = r.band_type
             AND projection.team_key = r.team_key
            WHERE r.band_type = @bandType AND r.ranking_scope = @scope AND r.combo_id = @comboId
            ORDER BY r.{rankColumn} ASC
            LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("limit", pageSize);
        cmd.Parameters.AddWithValue("offset", (page - 1) * pageSize);

        var entries = new List<BandTeamRankingDto>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                entries.Add(ReadBandTeamRanking(reader, totalTeams));
        }

        AttachBandRankingConfigurations(conn, entries, bandType, normalizedComboId);

        return (entries, totalTeams);
    }

    public BandTeamRankingDto? GetBandTeamRanking(string bandType, string teamKey, string? comboId = null)
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;
        var totalTeams = GetBandRankingTotalTeams(bandType, rankingScope, normalizedComboId);

        using var conn = _ds.OpenConnection();
        var rankingsTable = ResolveBandRankingReadTable(conn, bandType);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                r.band_type, r.combo_id, r.team_key, r.team_members, r.songs_played, r.total_charted_songs,
                r.coverage, r.raw_skill_rating, r.adjusted_skill_rating, r.adjusted_skill_rank,
                r.weighted_rating, r.weighted_rank, r.fc_rate, r.fc_rate_rank, r.total_score,
                r.total_score_rank, r.avg_accuracy, r.full_combo_count, r.avg_stars, r.best_rank,
                r.avg_rank, r.raw_weighted_rating, r.computed_at,
                projection.member_instruments_json::text AS member_instruments_json
            FROM {BandRankingStorageNames.QuoteIdentifier(rankingsTable)} r
            LEFT JOIN {BandSearchProjectionBuilder.TeamProjectionTable} projection
              ON projection.band_type = r.band_type
             AND projection.team_key = r.team_key
            WHERE r.band_type = @bandType AND r.ranking_scope = @scope AND r.combo_id = @comboId AND r.team_key = @teamKey";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        BandTeamRankingDto? ranking;
        using (var reader = cmd.ExecuteReader())
        {
            ranking = reader.Read() ? ReadBandTeamRanking(reader, totalTeams) : null;
        }
        if (ranking is not null)
            AttachBandRankingConfigurations(conn, [ranking], bandType, normalizedComboId);
        return ranking;
    }

    public BandTeamRankingDto? GetBandTeamRankingForAccount(string bandType, string accountId, string? comboId = null, string rankBy = "adjusted")
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;
        var totalTeams = GetBandRankingTotalTeams(bandType, rankingScope, normalizedComboId);
        var rankColumn = BandRankColumn(rankBy);

        using var conn = _ds.OpenConnection();
        var rankingsTable = ResolveBandRankingReadTable(conn, bandType);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            WITH candidate_teams AS (
                SELECT DISTINCT team_key
                FROM band_team_membership
                WHERE account_id = @accountId
                  AND band_type = @bandType
            )
            SELECT
                r.band_type, r.combo_id, r.team_key, r.team_members, r.songs_played, r.total_charted_songs,
                r.coverage, r.raw_skill_rating, r.adjusted_skill_rating, r.adjusted_skill_rank,
                r.weighted_rating, r.weighted_rank, r.fc_rate, r.fc_rate_rank, r.total_score,
                r.total_score_rank, r.avg_accuracy, r.full_combo_count, r.avg_stars, r.best_rank,
                r.avg_rank, r.raw_weighted_rating, r.computed_at,
                projection.member_instruments_json::text AS member_instruments_json
            FROM candidate_teams candidate
            JOIN {BandRankingStorageNames.QuoteIdentifier(rankingsTable)} r
              ON r.band_type = @bandType
             AND r.ranking_scope = @scope
             AND r.combo_id = @comboId
             AND r.team_key = candidate.team_key
            LEFT JOIN {BandSearchProjectionBuilder.TeamProjectionTable} projection
              ON projection.band_type = r.band_type
             AND projection.team_key = r.team_key
            ORDER BY r.{rankColumn} ASC
            LIMIT 1";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("accountId", accountId);

        BandTeamRankingDto? ranking;
        using (var reader = cmd.ExecuteReader())
        {
            ranking = reader.Read() ? ReadBandTeamRanking(reader, totalTeams) : null;
        }
        if (ranking is not null)
            AttachBandRankingConfigurations(conn, [ranking], bandType, normalizedComboId);
        return ranking;
    }

    public List<BandRankHistoryDto> GetBandRankHistory(string bandType, string teamKey, string? comboId = null, int days = 30)
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-Math.Max(days, 1)));
        var readSource = _bandRankHistoryOptions.ApiReadSource;

        using var conn = _ds.OpenConnection();

        if (readSource is BandRankHistoryApiReadSource.V2NarrowOnly or BandRankHistoryApiReadSource.V2NarrowWithLegacyFallback
            && TableExists(conn, null, "band_team_rank_history_points_v2"))
        {
            var v2 = GetBandRankHistoryFromV2Points(conn, bandType, teamKey, rankingScope, normalizedComboId, cutoff);
            if (v2.Count > 0 || readSource == BandRankHistoryApiReadSource.V2NarrowOnly)
                return v2;
        }

        if (readSource == BandRankHistoryApiReadSource.V2NarrowOnly)
            return [];

        if (readSource is BandRankHistoryApiReadSource.Narrow or BandRankHistoryApiReadSource.NarrowWithWideFallback or BandRankHistoryApiReadSource.V2NarrowWithLegacyFallback
            && TableExists(conn, null, "band_team_rank_history_points"))
        {
            var narrow = GetBandRankHistoryFromPoints(conn, bandType, teamKey, rankingScope, normalizedComboId, cutoff);
            if (narrow.Count > 0 || readSource == BandRankHistoryApiReadSource.Narrow)
                return narrow;
        }

        if (readSource == BandRankHistoryApiReadSource.Narrow)
            return [];

        return GetBandRankHistoryFromWide(conn, bandType, teamKey, rankingScope, normalizedComboId, cutoff);
    }

    private static List<BandRankHistoryDto> GetBandRankHistoryFromWide(
        NpgsqlConnection conn,
        string bandType,
        string teamKey,
        string rankingScope,
        string normalizedComboId,
        DateOnly cutoff)
    {
        if (!TableExists(conn, null, "band_team_rank_history") || !TableExists(conn, null, "band_team_ranking_stats_history"))
            return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                h.snapshot_date,
                h.computed_at,
                h.adjusted_skill_rank,
                h.weighted_rank,
                h.fc_rate_rank,
                h.total_score_rank,
                h.adjusted_skill_rating,
                h.weighted_rating,
                h.fc_rate,
                h.total_score,
                h.songs_played,
                h.coverage,
                h.full_combo_count,
                h.raw_weighted_rating,
                h.raw_skill_rating,
                h.total_charted_songs,
                stats.total_teams
            FROM (
                SELECT DISTINCT ON (snapshot_date)
                    snapshot_date,
                    computed_at,
                    adjusted_skill_rank,
                    weighted_rank,
                    fc_rate_rank,
                    total_score_rank,
                    adjusted_skill_rating,
                    weighted_rating,
                    fc_rate,
                    total_score,
                    songs_played,
                    coverage,
                    full_combo_count,
                    raw_weighted_rating,
                    raw_skill_rating,
                    total_charted_songs
                FROM band_team_rank_history
                WHERE band_type = @bandType
                  AND ranking_scope = @scope
                  AND combo_id = @comboId
                  AND team_key = @teamKey
                  AND snapshot_date >= @cutoff
                ORDER BY snapshot_date DESC
            ) h
            LEFT JOIN band_team_ranking_stats_history stats
                ON stats.band_type = @bandType
               AND stats.ranking_scope = @scope
               AND stats.combo_id = @comboId
               AND stats.snapshot_date = h.snapshot_date
            ORDER BY h.snapshot_date;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("cutoff", cutoff);

        var history = new List<BandRankHistoryDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            history.Add(new BandRankHistoryDto
            {
                SnapshotDate = reader.GetDateTime(0).ToString("yyyy-MM-dd"),
                SnapshotTakenAt = reader.GetDateTime(1).ToString("o"),
                AdjustedSkillRank = reader.GetInt32(2),
                WeightedRank = reader.GetInt32(3),
                FcRateRank = reader.GetInt32(4),
                TotalScoreRank = reader.GetInt32(5),
                AdjustedSkillRating = reader.GetDouble(6),
                WeightedRating = reader.GetDouble(7),
                FcRate = reader.GetDouble(8),
                TotalScore = reader.GetInt64(9),
                SongsPlayed = reader.GetInt32(10),
                Coverage = reader.GetDouble(11),
                FullComboCount = reader.GetInt32(12),
                RawWeightedRating = reader.IsDBNull(13) ? null : reader.GetDouble(13),
                RawSkillRating = reader.GetDouble(14),
                TotalChartedSongs = reader.GetInt32(15),
                TotalRankedTeams = reader.IsDBNull(16) ? null : reader.GetInt32(16),
            });
        }

        return history;
    }

    private static List<BandRankHistoryDto> GetBandRankHistoryFromPoints(
        NpgsqlConnection conn,
        string bandType,
        string teamKey,
        string rankingScope,
        string normalizedComboId,
        DateOnly cutoff) => GetBandRankHistoryFromPointsTable(
            conn,
            "band_team_rank_history_points",
            bandType,
            teamKey,
            rankingScope,
            normalizedComboId,
            cutoff);

    private static List<BandRankHistoryDto> GetBandRankHistoryFromV2Points(
        NpgsqlConnection conn,
        string bandType,
        string teamKey,
        string rankingScope,
        string normalizedComboId,
        DateOnly cutoff) => GetBandRankHistoryFromPointsTable(
            conn,
            "band_team_rank_history_points_v2",
            bandType,
            teamKey,
            rankingScope,
            normalizedComboId,
            cutoff);

    private static List<BandRankHistoryDto> GetBandRankHistoryFromPointsTable(
        NpgsqlConnection conn,
        string tableName,
        string bandType,
        string teamKey,
        string rankingScope,
        string normalizedComboId,
        DateOnly cutoff)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT DISTINCT ON (snapshot_date)
                snapshot_date,
                snapshot_taken_at,
                adjusted_skill_rank,
                weighted_rank,
                fc_rate_rank,
                total_score_rank,
                adjusted_skill_rating,
                weighted_rating,
                fc_rate,
                total_score,
                songs_played,
                coverage,
                full_combo_count,
                raw_weighted_rating,
                raw_skill_rating,
                total_charted_songs,
                total_ranked_teams
                        FROM {BandRankingStorageNames.QuoteIdentifier(tableName)}
            WHERE band_type = @bandType
              AND ranking_scope = @scope
              AND combo_id = @comboId
              AND team_key = @teamKey
              AND snapshot_date >= @cutoff
            ORDER BY snapshot_date DESC, snapshot_taken_at DESC";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("cutoff", cutoff);

        var history = new List<BandRankHistoryDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            history.Add(new BandRankHistoryDto
            {
                SnapshotDate = reader.GetDateTime(0).ToString("yyyy-MM-dd"),
                SnapshotTakenAt = reader.GetDateTime(1).ToString("o"),
                AdjustedSkillRank = reader.GetInt32(2),
                WeightedRank = reader.GetInt32(3),
                FcRateRank = reader.GetInt32(4),
                TotalScoreRank = reader.GetInt32(5),
                AdjustedSkillRating = reader.GetDouble(6),
                WeightedRating = reader.GetDouble(7),
                FcRate = reader.GetDouble(8),
                TotalScore = reader.GetInt64(9),
                SongsPlayed = reader.GetInt32(10),
                Coverage = reader.GetDouble(11),
                FullComboCount = reader.GetInt32(12),
                RawWeightedRating = reader.IsDBNull(13) ? null : reader.GetDouble(13),
                RawSkillRating = reader.GetDouble(14),
                TotalChartedSongs = reader.GetInt32(15),
                TotalRankedTeams = reader.GetInt32(16),
            });
        }

        history.Reverse();
        return history;
    }

    private const string LegacyBandSongTeamRankingsTable = "band_song_team_rankings";

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

    public BandSongTeamRankingRebuildMetrics RebuildBandSongTeamRankings(string bandType, BandTeamRankingRebuildOptions? options = null)
    {
        var resolvedOptions = ResolveBandTeamRankingRebuildOptions(options);
        var expectedMembers = BandInstrumentMapping.ExpectedMemberCount(bandType);
        var computedAt = DateTime.UtcNow;
        var totalSw = Stopwatch.StartNew();
        var materializeMs = 0d;
        var swapMs = 0d;
        var overallRows = 0;
        var comboRows = 0;

        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        try
        {
            if (resolvedOptions.DisableSynchronousCommit)
            {
                using var syncCmd = conn.CreateCommand();
                ConfigureBandRebuildCommand(syncCmd, tx, resolvedOptions);
                syncCmd.CommandText = "SET LOCAL synchronous_commit = off";
                syncCmd.ExecuteNonQuery();
            }

            var materializeSw = Stopwatch.StartNew();
            using (var cmd = conn.CreateCommand())
            {
                ConfigureBandRebuildCommand(cmd, tx, resolvedOptions);
                cmd.CommandText = $@"
                    CREATE TEMP TABLE _band_song_rank_results ON COMMIT DROP AS
                    WITH NormalizedEntries AS (
                        SELECT
                            be.song_id,
                            be.team_key,
                            be.score,
                            be.accuracy,
                            be.is_full_combo,
                            be.stars,
                            be.season,
                            COALESCE(be.end_time, '') AS end_time,
                            {BandSongComboIdExpression} AS combo_id
                        FROM band_entries be
                        WHERE be.band_type = @bandType
                          AND NOT be.is_over_threshold
                    ),
                    OverallChoice AS (
                        SELECT *
                        FROM (
                            SELECT
                                ne.*,
                                ROW_NUMBER() OVER (
                                    PARTITION BY ne.song_id, ne.team_key
                                    ORDER BY ne.score DESC, ne.end_time ASC, ne.combo_id ASC, ne.team_key ASC
                                ) AS choice_rank
                            FROM NormalizedEntries ne
                        ) ranked
                        WHERE choice_rank = 1
                    ),
                    OverallRanked AS (
                        SELECT
                            @bandType AS band_type,
                            'overall'::TEXT AS ranking_scope,
                            ''::TEXT AS scope_combo_id,
                            team_key,
                            song_id,
                            combo_id AS entry_combo_id,
                            (ROW_NUMBER() OVER (
                                PARTITION BY song_id
                                ORDER BY score DESC, end_time ASC, team_key ASC
                            ))::INT AS rank,
                            (COUNT(*) OVER (PARTITION BY song_id))::INT AS total_entries,
                            score,
                            accuracy,
                            is_full_combo,
                            stars,
                            season,
                            NULLIF(end_time, '') AS end_time,
                            @computedAt AS computed_at
                        FROM OverallChoice
                    ),
                    ComboRanked AS (
                        SELECT
                            @bandType AS band_type,
                            'combo'::TEXT AS ranking_scope,
                            combo_id AS scope_combo_id,
                            team_key,
                            song_id,
                            combo_id AS entry_combo_id,
                            (ROW_NUMBER() OVER (
                                PARTITION BY combo_id, song_id
                                ORDER BY score DESC, end_time ASC, team_key ASC
                            ))::INT AS rank,
                            (COUNT(*) OVER (PARTITION BY combo_id, song_id))::INT AS total_entries,
                            score,
                            accuracy,
                            is_full_combo,
                            stars,
                            season,
                            NULLIF(end_time, '') AS end_time,
                            @computedAt AS computed_at
                        FROM NormalizedEntries
                        WHERE combo_id <> ''
                          AND array_length(string_to_array(combo_id, '+'), 1) = @expectedMembers
                    )
                    SELECT
                        band_type,
                        ranking_scope,
                        scope_combo_id,
                        team_key,
                        song_id,
                        entry_combo_id,
                        rank,
                        total_entries,
                        (rank::DOUBLE PRECISION / NULLIF(total_entries, 0)) * 100.0 AS percentile,
                        score,
                        accuracy,
                        is_full_combo,
                        stars,
                        season,
                        end_time,
                        computed_at
                    FROM OverallRanked
                    UNION ALL
                    SELECT
                        band_type,
                        ranking_scope,
                        scope_combo_id,
                        team_key,
                        song_id,
                        entry_combo_id,
                        rank,
                        total_entries,
                        (rank::DOUBLE PRECISION / NULLIF(total_entries, 0)) * 100.0 AS percentile,
                        score,
                        accuracy,
                        is_full_combo,
                        stars,
                        season,
                        end_time,
                        computed_at
                    FROM ComboRanked;";
                cmd.Parameters.AddWithValue("bandType", bandType);
                cmd.Parameters.AddWithValue("expectedMembers", expectedMembers);
                cmd.Parameters.AddWithValue("computedAt", computedAt);
                cmd.ExecuteNonQuery();
            }
            materializeSw.Stop();
            materializeMs = RoundElapsed(materializeSw);
            LogBandSongRebuildStage(bandType, resolvedOptions, "materialize_results", materializeMs);

            using (var cmd = conn.CreateCommand())
            {
                ConfigureBandRebuildCommand(cmd, tx, resolvedOptions);
                cmd.CommandText = @"
                    SELECT
                        COUNT(*) FILTER (WHERE ranking_scope = 'overall')::INT AS overall_rows,
                        COUNT(*) FILTER (WHERE ranking_scope = 'combo')::INT AS combo_rows
                    FROM _band_song_rank_results;";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    overallRows = reader.GetInt32(0);
                    comboRows = reader.GetInt32(1);
                }
            }

            var publishSw = Stopwatch.StartNew();
            var buildSuffix = Guid.NewGuid().ToString("N")[..8];

            var createBuildSw = Stopwatch.StartNew();
            var buildTable = CreateBandSongRankingBuildTable(conn, tx, resolvedOptions, bandType, buildSuffix);
            createBuildSw.Stop();
            LogBandSongRebuildStage(bandType, resolvedOptions, "create_build_table", RoundElapsed(createBuildSw));

            var insertRowsSw = Stopwatch.StartNew();
            var insertedRows = InsertBandSongRankingRows(conn, tx, resolvedOptions, buildTable);
            insertRowsSw.Stop();
            LogBandSongRebuildStage(bandType, resolvedOptions, "insert_build_rows", RoundElapsed(insertRowsSw), insertedRows);

            var createIndexesSw = Stopwatch.StartNew();
            CreateBandSongRankingIndexes(conn, tx, resolvedOptions, buildTable);
            createIndexesSw.Stop();
            LogBandSongRebuildStage(bandType, resolvedOptions, "create_build_indexes", RoundElapsed(createIndexesSw));

            var swapCurrentSw = Stopwatch.StartNew();
            SwapBandSongCurrentTable(conn, tx, resolvedOptions, bandType, buildTable, buildSuffix);
            swapCurrentSw.Stop();
            LogBandSongRebuildStage(bandType, resolvedOptions, "swap_current", RoundElapsed(swapCurrentSw));

            publishSw.Stop();
            swapMs = RoundElapsed(publishSw);
            LogBandSongRebuildStage(bandType, resolvedOptions, "publish_current", swapMs, overallRows + comboRows);

            tx.Commit();
            totalSw.Stop();
            var metrics = new BandSongTeamRankingRebuildMetrics(
                bandType,
                overallRows + comboRows,
                overallRows,
                comboRows,
                materializeMs,
                swapMs,
                RoundElapsed(totalSw));

            _log.LogInformation(
                "Rebuilt band song team rankings for {BandType}: rows={RowCount}, overallRows={OverallRows}, comboRows={ComboRows}, materializeMs={MaterializeMs}, swapMs={SwapMs}, totalMs={TotalElapsedMs}",
                metrics.BandType,
                metrics.RowCount,
                metrics.OverallRows,
                metrics.ComboRows,
                metrics.MaterializeMs,
                metrics.SwapMs,
                metrics.TotalElapsedMs);

            return metrics;
        }
        catch
        {
            totalSw.Stop();
            _log.LogWarning(
                "Band song team ranking rebuild failed for {BandType}: rows={RowCount}, materializeMs={MaterializeMs}, swapMs={SwapMs}, totalMs={TotalElapsedMs}",
                bandType,
                overallRows + comboRows,
                materializeMs,
                swapMs,
                RoundElapsed(totalSw));
            throw;
        }
    }

    private void LogBandSongRebuildStage(string bandType, BandTeamRankingRebuildOptions options, string stage, double elapsedMs, int? rowCount = null)
    {
        _log.LogInformation(
            "[BandSongRankings.Stage] band_type={BandType} write_mode={WriteMode} timeout_seconds={CommandTimeoutSeconds} stage={Stage} elapsed_ms={ElapsedMs} row_count={RowCount}",
            bandType,
            options.WriteMode,
            options.CommandTimeoutSeconds,
            stage,
            elapsedMs,
            rowCount?.ToString() ?? "-");
    }

    private static string CreateBandSongRankingBuildTable(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string bandType, string buildSuffix)
    {
        var tableName = BandRankingStorageNames.GetBandSongRankingBuildTable(bandType, buildSuffix);
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = BandRankingStorageNames.GetCreateBandSongRankingTableSql(tableName, includePrimaryKey: false);
        cmd.ExecuteNonQuery();
        return tableName;
    }

    private static int InsertBandSongRankingRows(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string targetTable)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = $@"
            INSERT INTO {BandRankingStorageNames.QuoteIdentifier(targetTable)} (
                band_type, ranking_scope, scope_combo_id, team_key, song_id,
                entry_combo_id, rank, total_entries, percentile, score, accuracy,
                is_full_combo, stars, season, end_time, computed_at)
            SELECT
                band_type, ranking_scope, scope_combo_id, team_key, song_id,
                entry_combo_id, rank, total_entries, percentile, score, accuracy,
                is_full_combo, stars, season, end_time, computed_at
            FROM _band_song_rank_results
            ORDER BY ranking_scope, scope_combo_id, team_key, percentile ASC, rank ASC, score DESC, song_id ASC;";
        return cmd.ExecuteNonQuery();
    }

    private static void CreateBandSongRankingIndexes(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string tableName)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = BandRankingStorageNames.GetCreateBandSongRankingIndexesSql(tableName, includeUnique: true);
        cmd.ExecuteNonQuery();
    }

    private static void SwapBandSongCurrentTable(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string bandType, string buildTable, string buildSuffix)
    {
        var currentTable = BandRankingStorageNames.GetCurrentBandSongRankingTable(bandType);
        var backupTable = $"{currentTable}_old_{buildSuffix}";
        var statements = new List<string>();

        if (TableExists(conn, tx, currentTable))
            statements.Add($"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(currentTable)} RENAME TO {BandRankingStorageNames.QuoteIdentifier(backupTable)}");

        statements.Add($"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(buildTable)} RENAME TO {BandRankingStorageNames.QuoteIdentifier(currentTable)}");
        statements.Add($"DROP TABLE IF EXISTS {BandRankingStorageNames.QuoteIdentifier(backupTable)}");

        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = string.Join(";\n", statements) + ";";
        cmd.ExecuteNonQuery();
    }

    public List<BandSongPerformanceDto> GetBandSongPerformances(string bandType, string teamKey, string? comboId = null)
    {
        if (TryGetBandSongPerformancesFromCurrentBandSongRanking(bandType, teamKey, comboId, out var currentBandSongPerformances))
        {
            if (currentBandSongPerformances.Count > 0)
                return currentBandSongPerformances;

            if (TryGetBandSongPerformancesFromCurrentProjection(bandType, teamKey, comboId, out var currentPerformances)
                && currentPerformances.Count > 0)
                return currentPerformances;

            return currentBandSongPerformances;
        }

        if (TryGetBandSongPerformancesFromLegacyBandSongRanking(bandType, teamKey, comboId, out var legacyPerformances))
        {
            if (legacyPerformances.Count > 0)
                return legacyPerformances;

            if (TryGetBandSongPerformancesFromCurrentProjection(bandType, teamKey, comboId, out var currentPerformances)
                && currentPerformances.Count > 0)
                return currentPerformances;

            return legacyPerformances;
        }

        if (TryGetBandSongPerformancesFromCurrentProjection(bandType, teamKey, comboId, out var projectedPerformances))
            return projectedPerformances;

        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH NormalizedEntries AS (
                SELECT
                    be.song_id,
                    be.team_key,
                    be.score,
                    be.accuracy,
                    be.is_full_combo,
                    be.stars,
                    be.season,
                    COALESCE(be.end_time, '') AS end_time,
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
                    ), '') AS combo_id
                FROM band_entries be
                WHERE be.band_type = @bandType
                  AND NOT be.is_over_threshold
            ),
            ScopedEntries AS (
                SELECT *
                FROM NormalizedEntries
                WHERE @scope = 'overall' OR combo_id = @comboId
            ),
            ChosenEntries AS (
                SELECT *
                FROM (
                    SELECT
                        se.*,
                        ROW_NUMBER() OVER (
                            PARTITION BY se.song_id, se.team_key
                            ORDER BY se.score DESC, se.end_time ASC, se.combo_id ASC, se.team_key ASC
                        ) AS choice_rank
                    FROM ScopedEntries se
                ) ranked
                WHERE @scope = 'combo' OR choice_rank = 1
            ),
            RankedEntries AS (
                SELECT
                    ce.*,
                    (COUNT(*) OVER (PARTITION BY ce.song_id))::INT AS total_entries,
                    (ROW_NUMBER() OVER (
                        PARTITION BY ce.song_id
                        ORDER BY ce.score DESC, ce.end_time ASC, ce.team_key ASC
                    ))::INT AS effective_rank
                FROM ChosenEntries ce
            )
            SELECT
                song_id,
                NULLIF(combo_id, '') AS combo_id,
                effective_rank,
                total_entries,
                (effective_rank::DOUBLE PRECISION / NULLIF(total_entries, 0)) * 100.0 AS percentile,
                score,
                accuracy,
                is_full_combo,
                stars,
                season,
                NULLIF(end_time, '') AS end_time
            FROM RankedEntries
            WHERE team_key = @teamKey
            ORDER BY percentile ASC, effective_rank ASC, score DESC, song_id ASC;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);

        var performances = new List<BandSongPerformanceDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            performances.Add(ReadBandSongPerformance(reader));

        return performances;
    }

    private bool TryGetBandSongPerformancesFromCurrentBandSongRanking(
        string bandType,
        string teamKey,
        string? comboId,
        out List<BandSongPerformanceDto> performances) =>
        TryGetBandSongPerformancesFromBandSongRankingTable(
            BandRankingStorageNames.GetCurrentBandSongRankingTable(bandType),
            bandType,
            teamKey,
            comboId,
            out performances);

    private bool TryGetBandSongPerformancesFromLegacyBandSongRanking(
        string bandType,
        string teamKey,
        string? comboId,
        out List<BandSongPerformanceDto> performances) =>
        TryGetBandSongPerformancesFromBandSongRankingTable(
            LegacyBandSongTeamRankingsTable,
            bandType,
            teamKey,
            comboId,
            out performances);

    private bool TryGetBandSongPerformancesFromBandSongRankingTable(
        string tableName,
        string bandType,
        string teamKey,
        string? comboId,
        out List<BandSongPerformanceDto> performances)
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;
        performances = [];

        using var conn = _ds.OpenConnection();

        if (!TableExists(conn, null, tableName))
            return false;

        var quotedTable = BandRankingStorageNames.QuoteIdentifier(tableName);

        using (var scopeCmd = conn.CreateCommand())
        {
            scopeCmd.CommandText = $@"
                SELECT EXISTS (
                    SELECT 1
                    FROM {quotedTable}
                    WHERE band_type = @bandType
                      AND ranking_scope = @scope
                      AND scope_combo_id = @comboId
                );";
            scopeCmd.Parameters.AddWithValue("bandType", bandType);
            scopeCmd.Parameters.AddWithValue("scope", rankingScope);
            scopeCmd.Parameters.AddWithValue("comboId", normalizedComboId);
            if (scopeCmd.ExecuteScalar() is not bool hasScopeRows || !hasScopeRows)
                return false;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                song_id,
                NULLIF(entry_combo_id, '') AS combo_id,
                rank AS effective_rank,
                total_entries,
                percentile,
                score,
                accuracy,
                is_full_combo,
                stars,
                season,
                end_time
                        FROM {quotedTable}
            WHERE band_type = @bandType
              AND ranking_scope = @scope
              AND scope_combo_id = @comboId
              AND team_key = @teamKey
            ORDER BY percentile ASC, effective_rank ASC, score DESC, song_id ASC;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            performances.Add(ReadBandSongPerformance(reader));

        return true;
    }

    private bool TryGetBandSongPerformancesFromCurrentProjection(
        string bandType,
        string teamKey,
        string? comboId,
        out List<BandSongPerformanceDto> performances)
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;
        performances = [];

        using var conn = _ds.OpenConnection();

        using (var tableCmd = conn.CreateCommand())
        {
            tableCmd.CommandText = "SELECT to_regclass('public.current_band_leaderboard_entries') IS NOT NULL;";
            if (tableCmd.ExecuteScalar() is not bool tableExists || !tableExists)
                return false;
        }

        using (var scopeCmd = conn.CreateCommand())
        {
            scopeCmd.CommandText = @"
                SELECT EXISTS (
                    SELECT 1
                    FROM band_current_projection_scope
                    WHERE band_type = @bandType
                      AND ranking_scope = @scope
                      AND scope_combo_id = @comboId
                      AND published_generation IS NOT NULL
                );";
            scopeCmd.Parameters.AddWithValue("bandType", bandType);
            scopeCmd.Parameters.AddWithValue("scope", rankingScope);
            scopeCmd.Parameters.AddWithValue("comboId", normalizedComboId);
            if (scopeCmd.ExecuteScalar() is not bool hasScopeRows || !hasScopeRows)
                return false;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                cble.song_id,
                NULLIF(cble.entry_combo_id, '') AS combo_id,
                cble.rank AS effective_rank,
                cble.total_entries,
                cble.percentile,
                cble.score,
                cble.accuracy,
                cble.is_full_combo,
                cble.stars,
                cble.season,
                cble.end_time
            FROM current_band_leaderboard_entries cble
            JOIN band_current_projection_scope scope
              ON scope.song_id = cble.song_id
             AND scope.band_type = cble.band_type
             AND scope.ranking_scope = cble.ranking_scope
             AND scope.scope_combo_id = cble.scope_combo_id
             AND scope.published_generation = cble.projection_generation
            WHERE cble.band_type = @bandType
              AND cble.ranking_scope = @scope
              AND cble.scope_combo_id = @comboId
              AND cble.team_key = @teamKey
            ORDER BY cble.percentile ASC, cble.rank ASC, cble.score DESC, cble.song_id ASC;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            performances.Add(ReadBandSongPerformance(reader));

        return true;
    }

    public (List<BandSongPerformanceDto> Best, List<BandSongPerformanceDto> Worst) GetBandSongPerformanceExtremes(string bandType, string teamKey, string? comboId = null, int limit = 5)
    {
        if (TryGetBandSongPerformanceExtremesFromCurrentBandSongRanking(bandType, teamKey, comboId, limit, out var extremes))
            return extremes;

        if (TryGetBandSongPerformanceExtremesFromLegacyBandSongRanking(bandType, teamKey, comboId, limit, out extremes))
            return extremes;

        _log.LogInformation(
            "Band song team ranking projection is unavailable for band_type={BandType}, combo_id={ComboId}; falling back to live computation.",
            bandType,
            comboId ?? string.Empty);

        return GetBandSongPerformanceExtremesLive(bandType, teamKey, comboId, limit);
    }

    private bool TryGetBandSongPerformanceExtremesFromCurrentBandSongRanking(
        string bandType,
        string teamKey,
        string? comboId,
        int limit,
        out (List<BandSongPerformanceDto> Best, List<BandSongPerformanceDto> Worst) extremes) =>
        TryGetBandSongPerformanceExtremesFromBandSongRankingTable(
            BandRankingStorageNames.GetCurrentBandSongRankingTable(bandType),
            bandType,
            teamKey,
            comboId,
            limit,
            out extremes);

    private bool TryGetBandSongPerformanceExtremesFromLegacyBandSongRanking(
        string bandType,
        string teamKey,
        string? comboId,
        int limit,
        out (List<BandSongPerformanceDto> Best, List<BandSongPerformanceDto> Worst) extremes) =>
        TryGetBandSongPerformanceExtremesFromBandSongRankingTable(
            LegacyBandSongTeamRankingsTable,
            bandType,
            teamKey,
            comboId,
            limit,
            out extremes);

    private bool TryGetBandSongPerformanceExtremesFromBandSongRankingTable(
        string tableName,
        string bandType,
        string teamKey,
        string? comboId,
        int limit,
        out (List<BandSongPerformanceDto> Best, List<BandSongPerformanceDto> Worst) extremes)
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;
        var effectiveLimit = Math.Clamp(limit, 1, 20);

        var best = new List<BandSongPerformanceDto>();
        var worst = new List<BandSongPerformanceDto>();
        extremes = (best, worst);

        using var conn = _ds.OpenConnection();

        if (!TableExists(conn, null, tableName))
            return false;

        var quotedTable = BandRankingStorageNames.QuoteIdentifier(tableName);

        using (var scopeCmd = conn.CreateCommand())
        {
            scopeCmd.CommandText = $@"
                SELECT EXISTS (
                    SELECT 1
                    FROM {quotedTable}
                    WHERE band_type = @bandType
                      AND ranking_scope = @scope
                      AND scope_combo_id = @comboId
                );";
            scopeCmd.Parameters.AddWithValue("bandType", bandType);
            scopeCmd.Parameters.AddWithValue("scope", rankingScope);
            scopeCmd.Parameters.AddWithValue("comboId", normalizedComboId);
            if (scopeCmd.ExecuteScalar() is not bool hasScopeRows || !hasScopeRows)
                return false;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            WITH TeamRows AS MATERIALIZED (
                SELECT
                    song_id,
                    NULLIF(entry_combo_id, '') AS combo_id,
                    rank AS effective_rank,
                    total_entries,
                    percentile,
                    score,
                    accuracy,
                    is_full_combo,
                    stars,
                    season,
                    end_time
                                FROM {quotedTable}
                WHERE band_type = @bandType
                  AND ranking_scope = @scope
                  AND scope_combo_id = @comboId
                  AND team_key = @teamKey
            ),
            TeamPerformanceCount AS (
                SELECT COUNT(*)::INT AS total FROM TeamRows
            )
            SELECT *
            FROM (
                SELECT *
                FROM (
                    SELECT
                        0 AS bucket_order,
                        song_id,
                        combo_id,
                        effective_rank,
                        total_entries,
                        percentile,
                        score,
                        accuracy,
                        is_full_combo,
                        stars,
                        season,
                        end_time
                    FROM TeamRows
                    ORDER BY percentile ASC, effective_rank ASC, score DESC, song_id ASC
                    LIMIT @limit
                ) best
                UNION ALL
                SELECT *
                FROM (
                    SELECT
                        1 AS bucket_order,
                        song_id,
                        combo_id,
                        effective_rank,
                        total_entries,
                        percentile,
                        score,
                        accuracy,
                        is_full_combo,
                        stars,
                        season,
                        end_time
                    FROM TeamRows
                    WHERE (SELECT total FROM TeamPerformanceCount) > @limit
                    ORDER BY percentile DESC, effective_rank DESC, score ASC, song_id ASC
                    LIMIT @limit
                ) worst
            ) combined
            ORDER BY bucket_order,
                CASE WHEN bucket_order = 0 THEN percentile END ASC,
                CASE WHEN bucket_order = 0 THEN effective_rank END ASC,
                CASE WHEN bucket_order = 0 THEN score END DESC,
                CASE WHEN bucket_order = 1 THEN percentile END DESC,
                CASE WHEN bucket_order = 1 THEN effective_rank END DESC,
                CASE WHEN bucket_order = 1 THEN score END ASC,
                song_id ASC;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("limit", effectiveLimit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var performance = ReadBandSongPerformance(reader, offset: 1);
            if (reader.GetInt32(0) == 0)
                best.Add(performance);
            else
                worst.Add(performance);
        }

        return true;
    }

    private (List<BandSongPerformanceDto> Best, List<BandSongPerformanceDto> Worst) GetBandSongPerformanceExtremesLive(string bandType, string teamKey, string? comboId = null, int limit = 5)
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;
        var effectiveLimit = Math.Clamp(limit, 1, 20);

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            WITH TargetSongs AS (
                SELECT DISTINCT be.song_id
                FROM band_entries be
                WHERE be.band_type = @bandType
                  AND be.team_key = @teamKey
                  AND NOT be.is_over_threshold
                  AND (@scope = 'overall' OR {BandSongComboIdExpression} = @comboId)
            ),
            NormalizedEntries AS (
                SELECT
                    be.song_id,
                    be.team_key,
                    be.score,
                    be.accuracy,
                    be.is_full_combo,
                    be.stars,
                    be.season,
                    COALESCE(be.end_time, '') AS end_time,
                    {BandSongComboIdExpression} AS combo_id
                FROM band_entries be
                INNER JOIN TargetSongs ts ON ts.song_id = be.song_id
                WHERE be.band_type = @bandType
                  AND NOT be.is_over_threshold
            ),
            ScopedEntries AS (
                SELECT *
                FROM NormalizedEntries
                WHERE @scope = 'overall' OR combo_id = @comboId
            ),
            ChosenEntries AS (
                SELECT *
                FROM (
                    SELECT
                        se.*,
                        ROW_NUMBER() OVER (
                            PARTITION BY se.song_id, se.team_key
                            ORDER BY se.score DESC, se.end_time ASC, se.combo_id ASC, se.team_key ASC
                        ) AS choice_rank
                    FROM ScopedEntries se
                ) ranked
                WHERE @scope = 'combo' OR choice_rank = 1
            ),
            RankedEntries AS (
                SELECT
                    ce.*,
                    (COUNT(*) OVER (PARTITION BY ce.song_id))::INT AS total_entries,
                    (ROW_NUMBER() OVER (
                        PARTITION BY ce.song_id
                        ORDER BY ce.score DESC, ce.end_time ASC, ce.team_key ASC
                    ))::INT AS effective_rank
                FROM ChosenEntries ce
            ),
            TeamPerformances AS MATERIALIZED (
                SELECT
                    song_id,
                    NULLIF(combo_id, '') AS combo_id,
                    effective_rank,
                    total_entries,
                    (effective_rank::DOUBLE PRECISION / NULLIF(total_entries, 0)) * 100.0 AS percentile,
                    score,
                    accuracy,
                    is_full_combo,
                    stars,
                    season,
                    NULLIF(end_time, '') AS end_time
                FROM RankedEntries
                WHERE team_key = @teamKey
            ),
            TeamPerformanceCount AS (
                SELECT count(*)::INT AS total FROM TeamPerformances
            )
            SELECT *
            FROM (
                SELECT *
                FROM (
                    SELECT
                        0 AS bucket_order,
                        song_id,
                        combo_id,
                        effective_rank,
                        total_entries,
                        percentile,
                        score,
                        accuracy,
                        is_full_combo,
                        stars,
                        season,
                        end_time
                    FROM TeamPerformances
                    ORDER BY percentile ASC, effective_rank ASC, score DESC, song_id ASC
                    LIMIT @limit
                ) best
                UNION ALL
                SELECT *
                FROM (
                    SELECT
                        1 AS bucket_order,
                        song_id,
                        combo_id,
                        effective_rank,
                        total_entries,
                        percentile,
                        score,
                        accuracy,
                        is_full_combo,
                        stars,
                        season,
                        end_time
                    FROM TeamPerformances
                    WHERE (SELECT total FROM TeamPerformanceCount) > @limit
                    ORDER BY percentile DESC, effective_rank DESC, score ASC, song_id ASC
                    LIMIT @limit
                ) worst
            ) combined
            ORDER BY bucket_order,
                CASE WHEN bucket_order = 0 THEN percentile END ASC,
                CASE WHEN bucket_order = 0 THEN effective_rank END ASC,
                CASE WHEN bucket_order = 0 THEN score END DESC,
                CASE WHEN bucket_order = 1 THEN percentile END DESC,
                CASE WHEN bucket_order = 1 THEN effective_rank END DESC,
                CASE WHEN bucket_order = 1 THEN score END ASC,
                song_id ASC;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("limit", effectiveLimit);

        var best = new List<BandSongPerformanceDto>();
        var worst = new List<BandSongPerformanceDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var performance = ReadBandSongPerformance(reader, offset: 1);
            if (reader.GetInt32(0) == 0)
                best.Add(performance);
            else
                worst.Add(performance);
        }

        return (best, worst);
    }

    private static BandSongPerformanceDto ReadBandSongPerformance(NpgsqlDataReader reader, int offset = 0) => new()
    {
        SongId = reader.GetString(offset),
        ComboId = reader.IsDBNull(offset + 1) ? null : reader.GetString(offset + 1),
        Rank = reader.GetInt32(offset + 2),
        TotalEntries = reader.GetInt32(offset + 3),
        Percentile = reader.IsDBNull(offset + 4) ? 0 : reader.GetDouble(offset + 4),
        Score = reader.GetInt32(offset + 5),
        Accuracy = reader.IsDBNull(offset + 6) ? null : reader.GetInt32(offset + 6),
        IsFullCombo = reader.IsDBNull(offset + 7) ? null : reader.GetBoolean(offset + 7),
        Stars = reader.IsDBNull(offset + 8) ? null : reader.GetInt32(offset + 8),
        Season = reader.IsDBNull(offset + 9) ? null : reader.GetInt32(offset + 9),
        EndTime = reader.IsDBNull(offset + 10) ? null : reader.GetString(offset + 10),
    };

    private const string SongBandLeaderboardBaseCtes = """
        ScopedEntries AS (
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
                COALESCE(be.end_time, '') AS end_time
            FROM band_entries be
            WHERE be.song_id = @songId
              AND be.band_type = @bandType
              AND (@comboId IS NULL OR be.instrument_combo = ANY(@comboRawIds))
              AND NOT be.is_over_threshold
        ),
        ChosenEntries AS (
            SELECT *
            FROM (
                SELECT
                    se.*,
                    ROW_NUMBER() OVER (
                        PARTITION BY se.team_key
                        ORDER BY se.score DESC, se.end_time ASC, se.instrument_combo ASC, se.team_key ASC
                    ) AS choice_rank
                FROM ScopedEntries se
            ) ranked
            WHERE choice_rank = 1
        )
        """;

    private const string SongBandLeaderboardEntryRowsSql = """
            SELECT
                pe.team_key,
                pe.instrument_combo,
                pe.score,
                pe.accuracy,
                pe.is_full_combo,
                pe.stars,
                pe.difficulty,
                pe.season,
                pe.effective_rank,
                pe.total_entries,
                (pe.effective_rank::DOUBLE PRECISION / NULLIF(pe.total_entries, 0)) * 100.0 AS percentile,
                NULLIF(pe.end_time, '') AS end_time,
                pe.team_members,
                COALESCE(
                    ARRAY_AGG(bms.account_id ORDER BY bms.member_index) FILTER (WHERE bms.account_id IS NOT NULL),
                    ARRAY[]::TEXT[]
                ) AS account_ids,
                COALESCE(
                    ARRAY_AGG(COALESCE(bms.instrument_id, -1) ORDER BY bms.member_index) FILTER (WHERE bms.account_id IS NOT NULL),
                    ARRAY[]::INT[]
                ) AS instrument_ids,
                COALESCE(
                    ARRAY_AGG(COALESCE(bms.score, -1) ORDER BY bms.member_index) FILTER (WHERE bms.account_id IS NOT NULL),
                    ARRAY[]::INT[]
                ) AS member_scores,
                COALESCE(
                    ARRAY_AGG(COALESCE(bms.accuracy, -1) ORDER BY bms.member_index) FILTER (WHERE bms.account_id IS NOT NULL),
                    ARRAY[]::INT[]
                ) AS member_accuracies,
                COALESCE(
                    ARRAY_AGG(
                        CASE
                            WHEN bms.is_full_combo IS TRUE THEN 1
                            WHEN bms.is_full_combo IS FALSE THEN 0
                            ELSE -1
                        END
                        ORDER BY bms.member_index
                    ) FILTER (WHERE bms.account_id IS NOT NULL),
                    ARRAY[]::INT[]
                ) AS member_full_combos,
                COALESCE(
                    ARRAY_AGG(COALESCE(bms.stars, -1) ORDER BY bms.member_index) FILTER (WHERE bms.account_id IS NOT NULL),
                    ARRAY[]::INT[]
                ) AS member_stars,
                COALESCE(
                    ARRAY_AGG(COALESCE(bms.difficulty, -1) ORDER BY bms.member_index) FILTER (WHERE bms.account_id IS NOT NULL),
                    ARRAY[]::INT[]
                ) AS member_difficulties
            FROM PagedEntries pe
            LEFT JOIN band_member_stats bms
                ON bms.song_id = pe.song_id
               AND bms.band_type = pe.band_type
               AND bms.team_key = pe.team_key
               AND bms.instrument_combo = pe.instrument_combo
            GROUP BY
                pe.song_id,
                pe.band_type,
                pe.team_key,
                pe.instrument_combo,
                pe.team_members,
                pe.score,
                pe.accuracy,
                pe.is_full_combo,
                pe.stars,
                pe.difficulty,
                pe.season,
                pe.end_time,
                pe.total_entries,
                pe.effective_rank
            ORDER BY pe.effective_rank ASC
        """;

    public (List<SongBandLeaderboardEntryDto> Entries, int TotalEntries) GetSongBandLeaderboard(string songId, string bandType, int limit = 25, int offset = 0, string? comboId = null, bool requireCurrentProjection = false)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 200);
        var effectiveOffset = Math.Max(0, offset);

        using var conn = _ds.OpenConnection();
        if (TryGetSongBandLeaderboardFromCurrentProjection(conn, songId, bandType, effectiveLimit, effectiveOffset, comboId, out var projected))
            return projected;

        if (requireCurrentProjection)
            return ([], 0);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH {SongBandLeaderboardBaseCtes}
            SELECT COUNT(*)::INT FROM ChosenEntries;

            WITH {SongBandLeaderboardBaseCtes},
            RankedEntries AS (
                SELECT
                    ce.*,
                    (COUNT(*) OVER ())::INT AS total_entries,
                    (ROW_NUMBER() OVER (
                        ORDER BY ce.score DESC, ce.end_time ASC, ce.team_key ASC
                    ))::INT AS effective_rank
                FROM ChosenEntries ce
            ),
            PagedEntries AS (
                SELECT *
                FROM RankedEntries
                ORDER BY effective_rank ASC
                LIMIT @limit OFFSET @offset
            )
            {SongBandLeaderboardEntryRowsSql};
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("limit", effectiveLimit);
        cmd.Parameters.AddWithValue("offset", effectiveOffset);
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = (object?)comboId ?? DBNull.Value;
        cmd.Parameters.Add("comboRawIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = BandComboIds.ToEpicRawComboCandidates(comboId).ToArray();

        var entries = new List<SongBandLeaderboardEntryDto>();
        var totalEntries = 0;
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
            totalEntries = reader.GetInt32(0);

        if (!reader.NextResult())
            return (entries, totalEntries);

        entries.AddRange(ReadSongBandLeaderboardEntries(reader, bandType));

        return (entries, totalEntries);
    }

    public SongBandLeaderboardEntryDto? GetSongBandLeaderboardEntryForAccount(string songId, string bandType, string accountId, string? comboId = null, bool requireCurrentProjection = false)
    {
        using var conn = _ds.OpenConnection();
        if (TryGetSongBandLeaderboardEntryForAccountFromCurrentProjection(conn, songId, bandType, accountId, comboId, out var projected))
            return projected;

        if (requireCurrentProjection)
            return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH {SongBandLeaderboardBaseCtes},
            RankedEntries AS (
                SELECT
                    ce.*,
                    (COUNT(*) OVER ())::INT AS total_entries,
                    (ROW_NUMBER() OVER (
                        ORDER BY ce.score DESC, ce.end_time ASC, ce.team_key ASC
                    ))::INT AS effective_rank
                FROM ChosenEntries ce
            ),
            PagedEntries AS (
                SELECT *
                FROM RankedEntries
                WHERE @accountId = ANY(team_members)
                ORDER BY effective_rank ASC
                LIMIT 1
            )
            {SongBandLeaderboardEntryRowsSql};
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = (object?)comboId ?? DBNull.Value;
        cmd.Parameters.Add("comboRawIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = BandComboIds.ToEpicRawComboCandidates(comboId).ToArray();

        using var reader = cmd.ExecuteReader();
        return ReadSongBandLeaderboardEntries(reader, bandType).FirstOrDefault();
    }

    public SongBandLeaderboardEntryDto? GetSongBandLeaderboardEntryForTeam(string songId, string bandType, string teamKey, string? comboId = null, bool requireCurrentProjection = false)
    {
        using var conn = _ds.OpenConnection();
        if (TryGetSongBandLeaderboardEntryForTeamFromCurrentProjection(conn, songId, bandType, teamKey, comboId, out var projected))
            return projected;

        if (requireCurrentProjection)
            return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH {SongBandLeaderboardBaseCtes},
            RankedEntries AS (
                SELECT
                    ce.*,
                    (COUNT(*) OVER ())::INT AS total_entries,
                    (ROW_NUMBER() OVER (
                        ORDER BY ce.score DESC, ce.end_time ASC, ce.team_key ASC
                    ))::INT AS effective_rank
                FROM ChosenEntries ce
            ),
            PagedEntries AS (
                SELECT *
                FROM RankedEntries
                WHERE team_key = @teamKey
                ORDER BY effective_rank ASC
                LIMIT 1
            )
            {SongBandLeaderboardEntryRowsSql};
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = (object?)comboId ?? DBNull.Value;
        cmd.Parameters.Add("comboRawIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = BandComboIds.ToEpicRawComboCandidates(comboId).ToArray();

        using var reader = cmd.ExecuteReader();
        return ReadSongBandLeaderboardEntries(reader, bandType).FirstOrDefault();
    }

    private bool TryGetSongBandLeaderboardFromCurrentProjection(
        NpgsqlConnection conn,
        string songId,
        string bandType,
        int limit,
        int offset,
        string? comboId,
        out (List<SongBandLeaderboardEntryDto> Entries, int TotalEntries) result)
    {
        result = ([], 0);

        if (!TryGetCurrentBandProjectionScope(bandType, comboId, out var rankingScope, out var scopeComboId) ||
            !TryGetPublishedCurrentBandProjectionScope(conn, songId, bandType, rankingScope, scopeComboId, out var totalEntries, out var projectionGeneration))
            return false;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH PagedEntries AS (
                SELECT
                    cble.song_id,
                    cble.band_type,
                    cble.team_key,
                    cble.entry_instrument_combo AS instrument_combo,
                    cble.team_members,
                    cble.score,
                    cble.accuracy,
                    cble.is_full_combo,
                    cble.stars,
                    cble.difficulty,
                    cble.season,
                    COALESCE(cble.end_time, '') AS end_time,
                    cble.rank AS effective_rank,
                    cble.total_entries
                FROM current_band_leaderboard_entries cble
                WHERE cble.song_id = @songId
                  AND cble.band_type = @bandType
                  AND cble.ranking_scope = @rankingScope
                  AND cble.scope_combo_id = @scopeComboId
                  AND cble.projection_generation = @projectionGeneration
                ORDER BY cble.rank ASC
                LIMIT @limit OFFSET @offset
            )
            {SongBandLeaderboardEntryRowsSql};
            """;
        AddCurrentBandProjectionScopeParameters(cmd, songId, bandType, rankingScope, scopeComboId);
        cmd.Parameters.AddWithValue("projectionGeneration", projectionGeneration);
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("offset", offset);

        using var reader = cmd.ExecuteReader();
        result = (ReadSongBandLeaderboardEntries(reader, bandType), totalEntries);
        return true;
    }

    private bool TryGetSongBandLeaderboardEntryForAccountFromCurrentProjection(
        NpgsqlConnection conn,
        string songId,
        string bandType,
        string accountId,
        string? comboId,
        out SongBandLeaderboardEntryDto? entry)
    {
        entry = null;

        if (!TryGetCurrentBandProjectionScope(bandType, comboId, out var rankingScope, out var scopeComboId) ||
            !TryGetPublishedCurrentBandProjectionScope(conn, songId, bandType, rankingScope, scopeComboId, out _, out var projectionGeneration))
            return false;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH PagedEntries AS (
                SELECT
                    cble.song_id,
                    cble.band_type,
                    cble.team_key,
                    cble.entry_instrument_combo AS instrument_combo,
                    cble.team_members,
                    cble.score,
                    cble.accuracy,
                    cble.is_full_combo,
                    cble.stars,
                    cble.difficulty,
                    cble.season,
                    COALESCE(cble.end_time, '') AS end_time,
                    cble.rank AS effective_rank,
                    cble.total_entries
                FROM current_band_leaderboard_entries cble
                WHERE cble.song_id = @songId
                  AND cble.band_type = @bandType
                  AND cble.ranking_scope = @rankingScope
                  AND cble.scope_combo_id = @scopeComboId
                  AND cble.projection_generation = @projectionGeneration
                  AND @accountId = ANY(cble.team_members)
                ORDER BY cble.rank ASC
                LIMIT 1
            )
            {SongBandLeaderboardEntryRowsSql};
            """;
        AddCurrentBandProjectionScopeParameters(cmd, songId, bandType, rankingScope, scopeComboId);
        cmd.Parameters.AddWithValue("projectionGeneration", projectionGeneration);
        cmd.Parameters.AddWithValue("accountId", accountId);

        using var reader = cmd.ExecuteReader();
        entry = ReadSongBandLeaderboardEntries(reader, bandType).FirstOrDefault();
        return true;
    }

    private bool TryGetSongBandLeaderboardEntryForTeamFromCurrentProjection(
        NpgsqlConnection conn,
        string songId,
        string bandType,
        string teamKey,
        string? comboId,
        out SongBandLeaderboardEntryDto? entry)
    {
        entry = null;

        if (!TryGetCurrentBandProjectionScope(bandType, comboId, out var rankingScope, out var scopeComboId) ||
            !TryGetPublishedCurrentBandProjectionScope(conn, songId, bandType, rankingScope, scopeComboId, out _, out var projectionGeneration))
            return false;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH PagedEntries AS (
                SELECT
                    cble.song_id,
                    cble.band_type,
                    cble.team_key,
                    cble.entry_instrument_combo AS instrument_combo,
                    cble.team_members,
                    cble.score,
                    cble.accuracy,
                    cble.is_full_combo,
                    cble.stars,
                    cble.difficulty,
                    cble.season,
                    COALESCE(cble.end_time, '') AS end_time,
                    cble.rank AS effective_rank,
                    cble.total_entries
                FROM current_band_leaderboard_entries cble
                WHERE cble.song_id = @songId
                  AND cble.band_type = @bandType
                  AND cble.ranking_scope = @rankingScope
                  AND cble.scope_combo_id = @scopeComboId
                  AND cble.projection_generation = @projectionGeneration
                  AND cble.team_key = @teamKey
                ORDER BY cble.rank ASC
                LIMIT 1
            )
            {SongBandLeaderboardEntryRowsSql};
            """;
        AddCurrentBandProjectionScopeParameters(cmd, songId, bandType, rankingScope, scopeComboId);
        cmd.Parameters.AddWithValue("projectionGeneration", projectionGeneration);
        cmd.Parameters.AddWithValue("teamKey", teamKey);

        using var reader = cmd.ExecuteReader();
        entry = ReadSongBandLeaderboardEntries(reader, bandType).FirstOrDefault();
        return true;
    }

    private static bool TryGetCurrentBandProjectionScope(string bandType, string? comboId, out string rankingScope, out string scopeComboId)
    {
        rankingScope = "overall";
        scopeComboId = string.Empty;

        if (!IsCurrentBandProjectionReadBandType(bandType))
            return false;

        if (string.IsNullOrWhiteSpace(comboId))
            return true;

        var normalized = BandComboIds.TryNormalizeForBandType(bandType, comboId);
        if (normalized.Error is not null || string.IsNullOrWhiteSpace(normalized.ComboId))
            return false;

        rankingScope = "combo";
        scopeComboId = normalized.ComboId;
        return true;
    }

    private static bool IsCurrentBandProjectionReadBandType(string bandType) =>
        string.Equals(bandType, "Band_Duets", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(bandType, "Band_Trios", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(bandType, "Band_Quad", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetPublishedCurrentBandProjectionScope(
        NpgsqlConnection conn,
        string songId,
        string bandType,
        string rankingScope,
        string scopeComboId,
        out int rowCount,
        out long projectionGeneration)
    {
        rowCount = 0;
        projectionGeneration = 0;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT published_row_count, published_generation
            FROM band_current_projection_scope
            WHERE song_id = @songId
              AND band_type = @bandType
              AND ranking_scope = @rankingScope
              AND scope_combo_id = @scopeComboId
              AND published_generation IS NOT NULL
            LIMIT 1;
            """;
        AddCurrentBandProjectionScopeParameters(cmd, songId, bandType, rankingScope, scopeComboId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return false;

        var count = reader.GetInt64(0);
        rowCount = count > int.MaxValue ? int.MaxValue : (int)count;
        projectionGeneration = reader.GetInt64(1);
        return true;
    }

    private static void AddCurrentBandProjectionScopeParameters(
        NpgsqlCommand cmd,
        string songId,
        string bandType,
        string rankingScope,
        string scopeComboId)
    {
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("rankingScope", rankingScope);
        cmd.Parameters.AddWithValue("scopeComboId", scopeComboId);
    }

    public IReadOnlyList<string> GetBandLeaderboardSongIds()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT song_id
            FROM band_entries
            WHERE NOT is_over_threshold
            ORDER BY song_id
            """;
        var songIds = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            songIds.Add(reader.GetString(0));
        return songIds;
    }

    private static List<SongBandLeaderboardEntryDto> ReadSongBandLeaderboardEntries(NpgsqlDataReader reader, string bandType)
    {
        var entries = new List<SongBandLeaderboardEntryDto>();
        while (reader.Read())
        {
            var teamKey = reader.GetString(0);
            var rawCombo = reader.GetString(1);
            var teamMembers = reader.GetFieldValue<string[]>(12);
            var memberAccountIds = reader.GetFieldValue<string[]>(13);
            var memberInstrumentIds = reader.GetFieldValue<int[]>(14);
            var memberScores = reader.GetFieldValue<int[]>(15);
            var memberAccuracies = reader.GetFieldValue<int[]>(16);
            var memberFullComboValues = reader.GetFieldValue<int[]>(17);
            var memberStars = reader.GetFieldValue<int[]>(18);
            var memberDifficulties = reader.GetFieldValue<int[]>(19);
            var entrySeason = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7);
            var members = new List<PlayerBandMemberDto>();

            if (memberAccountIds.Length > 0)
            {
                for (var i = 0; i < memberAccountIds.Length; i++)
                {
                    var instrument = i < memberInstrumentIds.Length
                        ? BandInstrumentMapping.ToLeaderboardType(memberInstrumentIds[i])
                        : null;
                    members.Add(new PlayerBandMemberDto
                    {
                        AccountId = memberAccountIds[i],
                        Instruments = instrument is null ? [] : [instrument],
                        Score = ReadOptionalNonNegative(memberScores, i),
                        Accuracy = ReadOptionalNonNegative(memberAccuracies, i),
                        IsFullCombo = ReadOptionalBool(memberFullComboValues, i),
                        Stars = ReadOptionalNonNegative(memberStars, i),
                        Difficulty = ReadOptionalNonNegative(memberDifficulties, i),
                        Season = entrySeason,
                    });
                }
            }
            else
            {
                members.AddRange(teamMembers.Select(accountId => new PlayerBandMemberDto { AccountId = accountId }));
            }

            entries.Add(new SongBandLeaderboardEntryDto
            {
                BandId = BandIdentity.CreateBandId(bandType, teamKey),
                BandType = bandType,
                TeamKey = teamKey,
                ComboId = string.IsNullOrWhiteSpace(rawCombo) ? null : BandComboIds.FromEpicRawCombo(rawCombo),
                Members = members,
                Score = reader.GetInt32(2),
                Accuracy = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                IsFullCombo = !reader.IsDBNull(4) && reader.GetBoolean(4),
                Stars = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                Difficulty = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                Season = entrySeason ?? 0,
                Rank = reader.GetInt32(8),
                Percentile = reader.IsDBNull(10) ? 0 : reader.GetDouble(10),
                EndTime = reader.IsDBNull(11) ? null : reader.GetString(11),
            });
        }

        return entries;
    }

    private static int? ReadOptionalNonNegative(IReadOnlyList<int> values, int index) =>
        index < values.Count && values[index] >= 0 ? values[index] : null;

    private static bool? ReadOptionalBool(IReadOnlyList<int> values, int index) =>
        index < values.Count && values[index] >= 0 ? values[index] == 1 : null;

    public List<BandComboCatalogEntry> GetBandRankingCombos(string bandType)
    {
        using var conn = _ds.OpenConnection();
        var statsTable = ResolveBandRankingStatsReadTable(conn, bandType);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT combo_id, total_teams
            FROM {BandRankingStorageNames.QuoteIdentifier(statsTable)}
            WHERE band_type = @bandType AND ranking_scope = 'combo'
            ORDER BY total_teams DESC, combo_id ASC";
        cmd.Parameters.AddWithValue("bandType", bandType);

        var combos = new List<BandComboCatalogEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            combos.Add(new BandComboCatalogEntry
            {
                ComboId = reader.GetString(0),
                TeamCount = reader.GetInt32(1),
            });
        }

        return combos;
    }

    // ── Combo ranking deltas ─────────────────────────────────────────

    public void TruncateComboRankingDeltas()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE combo_ranking_deltas";
        cmd.ExecuteNonQuery();
    }

    public void WriteComboRankingDeltas(IReadOnlyList<(string ComboId, string AccountId, double LeewayBucket,
        double AdjustedRating, double WeightedRating, double FcRate,
        long TotalScore, double MaxScorePct, int SongsPlayed, int FullComboCount)> deltas)
    {
        if (deltas.Count == 0) return;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO combo_ranking_deltas (combo_id, account_id, leeway_bucket, adjusted_rating, " +
            "weighted_rating, fc_rate, total_score, max_score_pct, songs_played, full_combo_count) " +
            "VALUES (@cid, @aid, @bucket, @adj, @wgt, @fc, @ts, @ms, @songs, @fcc)";
        cmd.Parameters.Add("cid", NpgsqlTypes.NpgsqlDbType.Text);
        cmd.Parameters.Add("aid", NpgsqlTypes.NpgsqlDbType.Text);
        cmd.Parameters.Add("bucket", NpgsqlTypes.NpgsqlDbType.Real);
        cmd.Parameters.Add("adj", NpgsqlTypes.NpgsqlDbType.Double);
        cmd.Parameters.Add("wgt", NpgsqlTypes.NpgsqlDbType.Double);
        cmd.Parameters.Add("fc", NpgsqlTypes.NpgsqlDbType.Double);
        cmd.Parameters.Add("ts", NpgsqlTypes.NpgsqlDbType.Bigint);
        cmd.Parameters.Add("ms", NpgsqlTypes.NpgsqlDbType.Double);
        cmd.Parameters.Add("songs", NpgsqlTypes.NpgsqlDbType.Integer);
        cmd.Parameters.Add("fcc", NpgsqlTypes.NpgsqlDbType.Integer);
        cmd.Prepare();
        foreach (var d in deltas)
        {
            cmd.Parameters["cid"].Value = d.ComboId;
            cmd.Parameters["aid"].Value = d.AccountId;
            cmd.Parameters["bucket"].Value = (float)d.LeewayBucket;
            cmd.Parameters["adj"].Value = d.AdjustedRating;
            cmd.Parameters["wgt"].Value = d.WeightedRating;
            cmd.Parameters["fc"].Value = d.FcRate;
            cmd.Parameters["ts"].Value = d.TotalScore;
            cmd.Parameters["ms"].Value = d.MaxScorePct;
            cmd.Parameters["songs"].Value = d.SongsPlayed;
            cmd.Parameters["fcc"].Value = d.FullComboCount;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ── Maintenance ──────────────────────────────────────────────────
    public void Checkpoint() { }

    // ── API response cache ───────────────────────────────────────────

    public (byte[] Json, string ETag)? GetCachedResponse(string cacheKey)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json_data, etag FROM api_response_cache WHERE cache_key = @key";
        cmd.Parameters.AddWithValue("key", cacheKey);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ((byte[])r[0], r.GetString(1));
    }

    public void BulkSetCachedResponses(IEnumerable<(string Key, byte[] Json, string ETag)> entries)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO api_response_cache (cache_key, json_data, etag, cached_at)
            VALUES (@key, @json, @etag, now())
            ON CONFLICT (cache_key) DO UPDATE SET json_data = EXCLUDED.json_data, etag = EXCLUDED.etag, cached_at = now()
            """;
        cmd.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text));
        cmd.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Bytea));
        cmd.Parameters.Add(new NpgsqlParameter("etag", NpgsqlDbType.Text));
        cmd.Prepare();
        foreach (var (key, json, etag) in entries)
        {
            cmd.Parameters["key"].Value = key;
            cmd.Parameters["json"].Value = json;
            cmd.Parameters["etag"].Value = etag;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void ClearCachedResponses()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE api_response_cache";
        cmd.ExecuteNonQuery();
    }

    public void BulkSetCachedResponsesStaging(IEnumerable<(string Key, byte[] Json, string ETag)> entries)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        // Start with a clean staging table
        using (var trunc = conn.CreateCommand())
        {
            trunc.Transaction = tx;
            trunc.CommandText = "TRUNCATE api_response_cache_staging";
            trunc.ExecuteNonQuery();
        }

        cmd.CommandText = """
            INSERT INTO api_response_cache_staging (cache_key, json_data, etag, cached_at)
            VALUES (@key, @json, @etag, now())
            ON CONFLICT (cache_key) DO UPDATE SET json_data = EXCLUDED.json_data, etag = EXCLUDED.etag, cached_at = now()
            """;
        cmd.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text));
        cmd.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Bytea));
        cmd.Parameters.Add(new NpgsqlParameter("etag", NpgsqlDbType.Text));
        cmd.Prepare();
        foreach (var (key, json, etag) in entries)
        {
            cmd.Parameters["key"].Value = key;
            cmd.Parameters["json"].Value = json;
            cmd.Parameters["etag"].Value = etag;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void SwapCachedResponsesFromStaging()
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            TRUNCATE api_response_cache;
            INSERT INTO api_response_cache (cache_key, json_data, etag, cached_at)
            SELECT cache_key, json_data, etag, cached_at FROM api_response_cache_staging;
            TRUNCATE api_response_cache_staging;
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    // ── Private helpers ──────────────────────────────────────────────

    private static string? GetLeaderboardStagingPartitionName(string instrument) => instrument switch
    {
        "Solo_Guitar" => "leaderboard_staging_v2_solo_guitar",
        "Solo_Bass" => "leaderboard_staging_v2_solo_bass",
        "Solo_Drums" => "leaderboard_staging_v2_solo_drums",
        "Solo_Vocals" => "leaderboard_staging_v2_solo_vocals",
        "Solo_PeripheralGuitar" => "leaderboard_staging_v2_pro_guitar",
        "Solo_PeripheralBass" => "leaderboard_staging_v2_pro_bass",
        "Solo_PeripheralVocals" => "leaderboard_staging_v2_pro_vocals",
        "Solo_PeripheralCymbals" => "leaderboard_staging_v2_pro_cymbals",
        "Solo_PeripheralDrums" => "leaderboard_staging_v2_pro_drums",
        _ => null,
    };

    // ── Leaderboard staging ──────────────────────────────────────────

    public void StageChunk(long scrapeId, string songId, string instrument,
        IReadOnlyList<(int PageNum, LeaderboardEntry Entry)> entries)
    {
        if (entries.Count == 0) return;
        var now = DateTime.UtcNow;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var sc = conn.CreateCommand()) { sc.Transaction = tx; sc.CommandText = "SET LOCAL synchronous_commit = off"; sc.ExecuteNonQuery(); }

        using (var writer = conn.BeginBinaryImport(
            $"COPY {LeaderboardStagingTable} (scrape_id, song_id, instrument, page_num, account_id, score, accuracy, " +
            "is_full_combo, stars, season, difficulty, percentile, rank, end_time, api_rank, source, staged_at) " +
            "FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var (pageNum, e) in entries)
            {
                writer.StartRow();
                writer.Write((int)scrapeId, NpgsqlDbType.Integer);
                writer.Write(songId, NpgsqlDbType.Text);
                writer.Write(instrument, NpgsqlDbType.Text);
                writer.Write(pageNum, NpgsqlDbType.Integer);
                writer.Write(e.AccountId, NpgsqlDbType.Text);
                writer.Write(e.Score, NpgsqlDbType.Integer);
                writer.Write(e.Accuracy, NpgsqlDbType.Integer);
                writer.Write(e.IsFullCombo, NpgsqlDbType.Boolean);
                writer.Write(e.Stars, NpgsqlDbType.Integer);
                writer.Write(e.Season, NpgsqlDbType.Integer);
                writer.Write(e.Difficulty, NpgsqlDbType.Integer);
                writer.Write(e.Percentile, NpgsqlDbType.Double);
                writer.Write(e.Rank, NpgsqlDbType.Integer);
                if (e.EndTime is not null) writer.Write(e.EndTime, NpgsqlDbType.Text);
                else writer.WriteNull();
                if (e.ApiRank > 0) writer.Write(e.ApiRank, NpgsqlDbType.Integer);
                else writer.WriteNull();
                writer.Write(e.Source ?? "scrape", NpgsqlDbType.Text);
                writer.Write(now, NpgsqlDbType.TimestampTz);
            }
            writer.Complete();
        }
        tx.Commit();
    }

    public void UpsertStagingMeta(long scrapeId, string songId, string instrument, StagingMetaUpdate update)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO leaderboard_staging_meta (scrape_id, song_id, instrument, reported_pages, pages_scraped, entries_staged, valid_entry_count, requests, bytes_received, deep_scrape_status) " +
            "VALUES (@scrapeId, @songId, @instrument, @reportedPages, @pagesScraped, @entriesStaged, @validEntryCount, @requests, @bytesReceived, @deepScrapeStatus) " +
            "ON CONFLICT (scrape_id, song_id, instrument) DO UPDATE SET " +
            "reported_pages = GREATEST(leaderboard_staging_meta.reported_pages, EXCLUDED.reported_pages), " +
            "pages_scraped = leaderboard_staging_meta.pages_scraped + EXCLUDED.pages_scraped, " +
            "entries_staged = leaderboard_staging_meta.entries_staged + EXCLUDED.entries_staged, " +
            "valid_entry_count = COALESCE(EXCLUDED.valid_entry_count, leaderboard_staging_meta.valid_entry_count), " +
            "requests = leaderboard_staging_meta.requests + EXCLUDED.requests, " +
            "bytes_received = leaderboard_staging_meta.bytes_received + EXCLUDED.bytes_received, " +
            "deep_scrape_status = COALESCE(EXCLUDED.deep_scrape_status, leaderboard_staging_meta.deep_scrape_status)";
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("reportedPages", update.ReportedPages);
        cmd.Parameters.AddWithValue("pagesScraped", update.PagesScraped);
        cmd.Parameters.AddWithValue("entriesStaged", update.EntriesStaged);
        cmd.Parameters.AddWithValue("validEntryCount", (object?)update.ValidEntryCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("requests", update.Requests);
        cmd.Parameters.AddWithValue("bytesReceived", update.BytesReceived);
        cmd.Parameters.AddWithValue("deepScrapeStatus", (object?)update.DeepScrapeStatus ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<StagingMetaRow> GetStagingMeta(long scrapeId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT scrape_id, song_id, instrument, reported_pages, pages_scraped, entries_staged, " +
            "valid_entry_count, requests, bytes_received, deep_scrape_status, wave1_finalized_at, wave2_finalized_at " +
            "FROM leaderboard_staging_meta WHERE scrape_id = @scrapeId";
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        var list = new List<StagingMetaRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new StagingMetaRow
            {
                ScrapeId = r.GetInt32(0),
                SongId = r.GetString(1),
                Instrument = r.GetString(2),
                ReportedPages = r.GetInt32(3),
                PagesScraped = r.GetInt32(4),
                EntriesStaged = r.GetInt32(5),
                ValidEntryCount = r.IsDBNull(6) ? null : r.GetInt32(6),
                Requests = r.GetInt32(7),
                BytesReceived = r.GetInt64(8),
                DeepScrapeStatus = r.IsDBNull(9) ? null : r.GetString(9),
                Wave1FinalizedAt = r.IsDBNull(10) ? null : r.GetDateTime(10),
                Wave2FinalizedAt = r.IsDBNull(11) ? null : r.GetDateTime(11),
            });
        }
        return list;
    }

    public void MarkWaveFinalized(long scrapeId, string songId, string instrument, int wave)
    {
        var column = wave == 1 ? "wave1_finalized_at" : "wave2_finalized_at";
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE leaderboard_staging_meta SET {column} = @now WHERE scrape_id = @scrapeId AND song_id = @songId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.ExecuteNonQuery();
    }

    public void EnqueueDeepScrapeJob(DeepScrapeJobInfo job)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO deep_scrape_queue (scrape_id, song_id, instrument, label, valid_cutoff, valid_entry_target, " +
            "wave2_start_page, reported_pages, initial_valid_count, status) " +
            "VALUES (@scrapeId, @songId, @instrument, @label, @validCutoff, @validEntryTarget, " +
            "@wave2StartPage, @reportedPages, @initialValidCount, 'pending') " +
            "ON CONFLICT (scrape_id, song_id, instrument) DO NOTHING";
        cmd.Parameters.AddWithValue("scrapeId", (int)job.ScrapeId);
        cmd.Parameters.AddWithValue("songId", job.SongId);
        cmd.Parameters.AddWithValue("instrument", job.Instrument);
        cmd.Parameters.AddWithValue("label", (object?)job.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("validCutoff", job.ValidCutoff);
        cmd.Parameters.AddWithValue("validEntryTarget", job.ValidEntryTarget);
        cmd.Parameters.AddWithValue("wave2StartPage", job.Wave2StartPage);
        cmd.Parameters.AddWithValue("reportedPages", job.ReportedPages);
        cmd.Parameters.AddWithValue("initialValidCount", job.InitialValidCount);
        cmd.ExecuteNonQuery();
    }

    public List<DeepScrapeQueueRow> GetDeepScrapeJobs(long scrapeId, string? status = null)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        var filter = status is not null ? " AND status = @status" : "";
        cmd.CommandText =
            "SELECT scrape_id, song_id, instrument, label, valid_cutoff, valid_entry_target, " +
            "wave2_start_page, reported_pages, initial_valid_count, status, cursor_page, " +
            "current_valid_count, created_at, completed_at " +
            $"FROM deep_scrape_queue WHERE scrape_id = @scrapeId{filter} ORDER BY created_at";
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        if (status is not null) cmd.Parameters.AddWithValue("status", status);
        var list = new List<DeepScrapeQueueRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new DeepScrapeQueueRow
            {
                ScrapeId = r.GetInt32(0),
                SongId = r.GetString(1),
                Instrument = r.GetString(2),
                Label = r.IsDBNull(3) ? null : r.GetString(3),
                ValidCutoff = r.GetInt32(4),
                ValidEntryTarget = r.GetInt32(5),
                Wave2StartPage = r.GetInt32(6),
                ReportedPages = r.GetInt32(7),
                InitialValidCount = r.GetInt32(8),
                Status = r.GetString(9),
                CursorPage = r.IsDBNull(10) ? null : r.GetInt32(10),
                CurrentValidCount = r.IsDBNull(11) ? null : r.GetInt32(11),
                CreatedAt = r.GetDateTime(12),
                CompletedAt = r.IsDBNull(13) ? null : r.GetDateTime(13),
            });
        }
        return list;
    }

    public void UpdateDeepScrapeJobCursor(long scrapeId, string songId, string instrument,
        int cursorPage, int currentValidCount)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE deep_scrape_queue SET cursor_page = @cursor, current_valid_count = @valid, status = 'running' " +
            "WHERE scrape_id = @scrapeId AND song_id = @songId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("cursor", cursorPage);
        cmd.Parameters.AddWithValue("valid", currentValidCount);
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.ExecuteNonQuery();
    }

    public void CompleteDeepScrapeJob(long scrapeId, string songId, string instrument, string status)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE deep_scrape_queue SET status = @status, completed_at = @now " +
            "WHERE scrape_id = @scrapeId AND song_id = @songId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.ExecuteNonQuery();
    }

    public int CleanupAbandonedStaging(long currentScrapeId)
    {
        using var conn = _ds.OpenConnection();
        int total = 0;

        total += CleanupAbandonedStagingTable(conn, LeaderboardStagingTable, currentScrapeId);
        total += CleanupAbandonedStagingTable(conn, LegacyLeaderboardStagingTable, currentScrapeId);

        // staging_meta and deep_scrape_queue are tiny — single deletes are fine
        using (var tx = conn.BeginTransaction())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM leaderboard_staging_meta WHERE scrape_id < @id";
                cmd.Parameters.AddWithValue("id", (int)currentScrapeId);
                total += cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM deep_scrape_queue WHERE scrape_id < @id";
                cmd.Parameters.AddWithValue("id", (int)currentScrapeId);
                total += cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    DELETE FROM scrape_log log
                    WHERE log.id < @id
                      AND log.completed_at IS NULL
                      AND NOT EXISTS (
                          SELECT 1
                          FROM scrape_publication_state state
                          WHERE state.public_reads_frozen_scrape_id = log.id
                      )
                    """;
                cmd.Parameters.AddWithValue("id", (int)currentScrapeId);
                total += cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return total;
    }

    private static int CleanupAbandonedStagingTable(NpgsqlConnection conn, string tableName, long currentScrapeId)
    {
        int total = 0;

        int staleRows;
        bool hasCurrentRows;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"SELECT COUNT(*) FILTER (WHERE scrape_id < @id), COUNT(*) FILTER (WHERE scrape_id >= @id) FROM {tableName}";
            cmd.Parameters.AddWithValue("id", (int)currentScrapeId);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            staleRows = reader.GetInt32(0);
            hasCurrentRows = reader.GetInt32(1) > 0;
        }

        // Delete staging rows in batches to avoid a single massive DELETE that
        // generates excessive WAL and exceeds command timeouts. Each batch
        // runs in its own transaction so progress is incremental.
        if (staleRows > 0 && !hasCurrentRows)
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"TRUNCATE {tableName}";
            cmd.ExecuteNonQuery();
            tx.Commit();
            total += staleRows;
        }
        else
        {
            const int batchSize = 500_000;
            int deleted;
            do
            {
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = 0;
                cmd.CommandText =
                    $"DELETE FROM {tableName} WHERE ctid = ANY(" +
                    $"ARRAY(SELECT ctid FROM {tableName} WHERE scrape_id < @id LIMIT @limit))";
                cmd.Parameters.AddWithValue("id", (int)currentScrapeId);
                cmd.Parameters.AddWithValue("limit", batchSize);
                deleted = cmd.ExecuteNonQuery();
                tx.Commit();
                total += deleted;
            } while (deleted >= batchSize);
        }
        return total;
    }

    public int DeleteStagedEntries(long scrapeId, string songId, string instrument)
    {
        using var conn = _ds.OpenConnection();
        return DeleteStagedEntries(conn, LeaderboardStagingTable, scrapeId, songId, instrument)
            + DeleteStagedEntries(conn, LegacyLeaderboardStagingTable, scrapeId, songId, instrument);
    }

    private static int DeleteStagedEntries(NpgsqlConnection conn, string tableName, long scrapeId, string songId, string instrument)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {tableName} WHERE scrape_id = @scrapeId AND song_id = @songId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        return cmd.ExecuteNonQuery();
    }

    public int DeleteStagedEntriesForInstrument(long scrapeId, string instrument)
    {
        using var conn = _ds.OpenConnection();
        var partitionName = GetLeaderboardStagingPartitionName(instrument);
        var deleted = 0;

        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM {LeaderboardStagingTable} WHERE scrape_id = @scrapeId AND instrument = @instrument";
            countCmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
            countCmd.Parameters.AddWithValue("instrument", instrument);
            var stagedRows = Convert.ToInt32(countCmd.ExecuteScalar());
            if (stagedRows > 0 && partitionName is not null)
            {
                using var probeCmd = conn.CreateCommand();
                probeCmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {partitionName} WHERE scrape_id <> @scrapeId)";
                probeCmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
                var hasOtherScrapeRows = Convert.ToBoolean(probeCmd.ExecuteScalar());
                if (!hasOtherScrapeRows)
                {
                    using var tx = conn.BeginTransaction();
                    using var truncateCmd = conn.CreateCommand();
                    truncateCmd.Transaction = tx;
                    truncateCmd.CommandText = $"TRUNCATE {partitionName}";
                    truncateCmd.ExecuteNonQuery();
                    tx.Commit();
                    deleted += stagedRows;
                }
                else
                {
                    deleted += DeleteStagedEntriesForInstrument(conn, LeaderboardStagingTable, scrapeId, instrument);
                }
            }
            else if (stagedRows > 0)
            {
                deleted += DeleteStagedEntriesForInstrument(conn, LeaderboardStagingTable, scrapeId, instrument);
            }
        }

        using (var legacyCountCmd = conn.CreateCommand())
        {
            legacyCountCmd.CommandText = $"SELECT COUNT(*) FROM {LegacyLeaderboardStagingTable} WHERE scrape_id = @scrapeId AND instrument = @instrument";
            legacyCountCmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
            legacyCountCmd.Parameters.AddWithValue("instrument", instrument);
            var legacyRows = Convert.ToInt32(legacyCountCmd.ExecuteScalar());
            if (legacyRows > 0)
            {
                using var legacyProbeCmd = conn.CreateCommand();
                legacyProbeCmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {LegacyLeaderboardStagingTable} WHERE scrape_id <> @scrapeId OR instrument <> @instrument)";
                legacyProbeCmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
                legacyProbeCmd.Parameters.AddWithValue("instrument", instrument);
                var hasOtherLegacyRows = Convert.ToBoolean(legacyProbeCmd.ExecuteScalar());
                if (!hasOtherLegacyRows)
                {
                    using var tx = conn.BeginTransaction();
                    using var truncateCmd = conn.CreateCommand();
                    truncateCmd.Transaction = tx;
                    truncateCmd.CommandText = $"TRUNCATE {LegacyLeaderboardStagingTable}";
                    truncateCmd.ExecuteNonQuery();
                    tx.Commit();
                    deleted += legacyRows;
                }
                else
                {
                    deleted += DeleteStagedEntriesForInstrument(conn, LegacyLeaderboardStagingTable, scrapeId, instrument);
                }
            }
        }

        return deleted;
    }

    private static int DeleteStagedEntriesForInstrument(NpgsqlConnection conn, string tableName, long scrapeId, string instrument)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {tableName} WHERE scrape_id = @scrapeId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        return cmd.ExecuteNonQuery();
    }

    public void MarkWaveFinalizedForInstrument(long scrapeId, string instrument, int wave)
    {
        var column = wave == 1 ? "wave1_finalized_at" : "wave2_finalized_at";
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE leaderboard_staging_meta SET {column} = @now WHERE scrape_id = @scrapeId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.ExecuteNonQuery();
    }

    public int GetStagedEntryCount(long scrapeId, string songId, string instrument)
    {
        using var conn = _ds.OpenConnection();
        return GetStagedEntryCount(conn, LeaderboardStagingTable, scrapeId, songId, instrument)
            + GetStagedEntryCount(conn, LegacyLeaderboardStagingTable, scrapeId, songId, instrument);
    }

    private static int GetStagedEntryCount(NpgsqlConnection conn, string tableName, long scrapeId, string songId, string instrument)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {tableName} WHERE scrape_id = @scrapeId AND song_id = @songId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int GetBandRankingTotalTeams(string bandType, string rankingScope, string comboId)
    {
        using var conn = _ds.OpenConnection();
        var statsTable = ResolveBandRankingStatsReadTable(conn, bandType);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT total_teams
            FROM {BandRankingStorageNames.QuoteIdentifier(statsTable)}
            WHERE band_type = @bandType AND ranking_scope = @scope AND combo_id = @comboId";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", comboId);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? 0 : Convert.ToInt32(result);
    }

    private static void EnsureBandRankHistoryTables(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS band_team_rank_history (
                band_type             TEXT             NOT NULL,
                ranking_scope         TEXT             NOT NULL,
                combo_id              TEXT             NOT NULL DEFAULT '',
                team_key              TEXT             NOT NULL,
                team_members          TEXT[]           NOT NULL,
                songs_played          INT              NOT NULL,
                total_charted_songs   INT              NOT NULL,
                coverage              DOUBLE PRECISION NOT NULL,
                raw_skill_rating      DOUBLE PRECISION NOT NULL,
                adjusted_skill_rating DOUBLE PRECISION NOT NULL,
                adjusted_skill_rank   INT              NOT NULL,
                weighted_rating       DOUBLE PRECISION NOT NULL,
                weighted_rank         INT              NOT NULL,
                fc_rate               DOUBLE PRECISION NOT NULL,
                fc_rate_rank          INT              NOT NULL,
                total_score           BIGINT           NOT NULL,
                total_score_rank      INT              NOT NULL,
                avg_accuracy          DOUBLE PRECISION NOT NULL,
                full_combo_count      INT              NOT NULL,
                avg_stars             DOUBLE PRECISION NOT NULL,
                best_rank             INT              NOT NULL,
                avg_rank              DOUBLE PRECISION NOT NULL,
                raw_weighted_rating   DOUBLE PRECISION,
                computed_at           TIMESTAMPTZ      NOT NULL,
                snapshot_date         DATE             NOT NULL,
                PRIMARY KEY (band_type, ranking_scope, combo_id, team_key, snapshot_date)
            );

            CREATE TABLE IF NOT EXISTS band_team_rank_history_latest (
                band_type             TEXT             NOT NULL,
                ranking_scope         TEXT             NOT NULL,
                combo_id              TEXT             NOT NULL DEFAULT '',
                team_key              TEXT             NOT NULL,
                team_members          TEXT[]           NOT NULL,
                songs_played          INT              NOT NULL,
                total_charted_songs   INT              NOT NULL,
                coverage              DOUBLE PRECISION NOT NULL,
                raw_skill_rating      DOUBLE PRECISION NOT NULL,
                adjusted_skill_rating DOUBLE PRECISION NOT NULL,
                adjusted_skill_rank   INT              NOT NULL,
                weighted_rating       DOUBLE PRECISION NOT NULL,
                weighted_rank         INT              NOT NULL,
                fc_rate               DOUBLE PRECISION NOT NULL,
                fc_rate_rank          INT              NOT NULL,
                total_score           BIGINT           NOT NULL,
                total_score_rank      INT              NOT NULL,
                avg_accuracy          DOUBLE PRECISION NOT NULL,
                full_combo_count      INT              NOT NULL,
                avg_stars             DOUBLE PRECISION NOT NULL,
                best_rank             INT              NOT NULL,
                avg_rank              DOUBLE PRECISION NOT NULL,
                raw_weighted_rating   DOUBLE PRECISION,
                computed_at           TIMESTAMPTZ      NOT NULL,
                snapshot_date         DATE             NOT NULL,
                fingerprint           TEXT             NOT NULL,
                updated_at            TIMESTAMPTZ      NOT NULL DEFAULT now(),
                PRIMARY KEY (band_type, ranking_scope, combo_id, team_key)
            );

            CREATE TABLE IF NOT EXISTS band_team_rank_history_points (
                band_type             TEXT             NOT NULL,
                ranking_scope         TEXT             NOT NULL,
                combo_id              TEXT             NOT NULL DEFAULT '',
                team_key              TEXT             NOT NULL,
                snapshot_date         DATE             NOT NULL,
                snapshot_taken_at     TIMESTAMPTZ      NOT NULL,
                adjusted_skill_rank   INT              NOT NULL,
                weighted_rank         INT              NOT NULL,
                fc_rate_rank          INT              NOT NULL,
                total_score_rank      INT              NOT NULL,
                adjusted_skill_rating DOUBLE PRECISION,
                weighted_rating       DOUBLE PRECISION,
                fc_rate               DOUBLE PRECISION,
                total_score           BIGINT,
                songs_played          INT,
                coverage              DOUBLE PRECISION,
                full_combo_count      INT,
                total_charted_songs   INT,
                total_ranked_teams    INT,
                raw_weighted_rating   DOUBLE PRECISION,
                raw_skill_rating      DOUBLE PRECISION,
                PRIMARY KEY (band_type, ranking_scope, combo_id, team_key, snapshot_date)
            );

            CREATE TABLE IF NOT EXISTS band_team_ranking_stats_history (
                band_type      TEXT        NOT NULL,
                ranking_scope  TEXT        NOT NULL,
                combo_id       TEXT        NOT NULL DEFAULT '',
                total_teams    INT         NOT NULL,
                computed_at    TIMESTAMPTZ NOT NULL,
                snapshot_date  DATE        NOT NULL,
                PRIMARY KEY (band_type, ranking_scope, combo_id, snapshot_date)
            );

            CREATE TABLE IF NOT EXISTS band_rank_history_jobs (
                job_id                 BIGSERIAL PRIMARY KEY,
                scrape_id              BIGINT      NOT NULL,
                snapshot_date          DATE        NOT NULL,
                band_type              TEXT        NOT NULL,
                mode                   TEXT        NOT NULL,
                status                 TEXT        NOT NULL,
                started_at             TIMESTAMPTZ,
                completed_at           TIMESTAMPTZ,
                failed_at              TIMESTAMPTZ,
                paused_at              TIMESTAMPTZ,
                superseded_at          TIMESTAMPTZ,
                last_error             TEXT,
                attempts               INT         NOT NULL DEFAULT 0,
                chunks_total           INT         NOT NULL DEFAULT 0,
                chunks_completed       INT         NOT NULL DEFAULT 0,
                rows_scanned           BIGINT      NOT NULL DEFAULT 0,
                rows_inserted          BIGINT      NOT NULL DEFAULT 0,
                rows_skipped           BIGINT      NOT NULL DEFAULT 0,
                source_generation      BIGINT      NOT NULL DEFAULT 0,
                current_ranking_scope  TEXT,
                current_combo_id       TEXT,
                updated_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
                UNIQUE (scrape_id, band_type, snapshot_date)
            );

            CREATE INDEX IF NOT EXISTS ix_brhj_status
                ON band_rank_history_jobs (status, snapshot_date DESC, scrape_id DESC);

            CREATE INDEX IF NOT EXISTS ix_brhj_band_snapshot
                ON band_rank_history_jobs (band_type, snapshot_date DESC, scrape_id DESC, job_id DESC);

            CREATE TABLE IF NOT EXISTS band_rank_history_job_chunks (
                job_id          BIGINT      NOT NULL REFERENCES band_rank_history_jobs(job_id) ON DELETE CASCADE,
                band_type       TEXT        NOT NULL,
                ranking_scope   TEXT        NOT NULL,
                combo_id        TEXT        NOT NULL DEFAULT '',
                chunk_ordinal   INT         NOT NULL DEFAULT 0,
                team_key_start  TEXT,
                team_key_end    TEXT,
                estimated_rows  BIGINT      NOT NULL DEFAULT 0,
                source_generation BIGINT    NOT NULL DEFAULT 0,
                status          TEXT        NOT NULL,
                started_at      TIMESTAMPTZ,
                completed_at    TIMESTAMPTZ,
                rows_scanned    BIGINT      NOT NULL DEFAULT 0,
                rows_inserted   BIGINT      NOT NULL DEFAULT 0,
                rows_skipped    BIGINT      NOT NULL DEFAULT 0,
                last_error      TEXT,
                updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (job_id, ranking_scope, combo_id, chunk_ordinal)
            );

            ALTER TABLE IF EXISTS band_rank_history_jobs
                ADD COLUMN IF NOT EXISTS source_generation BIGINT NOT NULL DEFAULT 0;

            ALTER TABLE IF EXISTS band_rank_history_job_chunks
                ADD COLUMN IF NOT EXISTS chunk_ordinal INT NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS team_key_start TEXT,
                ADD COLUMN IF NOT EXISTS team_key_end TEXT,
                ADD COLUMN IF NOT EXISTS estimated_rows BIGINT NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS source_generation BIGINT NOT NULL DEFAULT 0;

            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_constraint con
                    JOIN pg_class rel ON rel.oid = con.conrelid
                    JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
                    CROSS JOIN LATERAL (
                        SELECT array_agg(att.attname ORDER BY keys.ordinality) AS key_columns
                        FROM unnest(con.conkey) WITH ORDINALITY AS keys(attnum, ordinality)
                        JOIN pg_attribute att ON att.attrelid = rel.oid AND att.attnum = keys.attnum
                    ) cols
                    WHERE nsp.nspname = 'public'
                      AND rel.relname = 'band_rank_history_job_chunks'
                      AND con.conname = 'band_rank_history_job_chunks_pkey'
                      AND con.contype = 'p'
                      AND cols.key_columns = ARRAY['job_id', 'ranking_scope', 'combo_id', 'chunk_ordinal']::name[]
                ) THEN
                    ALTER TABLE band_rank_history_job_chunks DROP CONSTRAINT IF EXISTS band_rank_history_job_chunks_pkey;
                    ALTER TABLE band_rank_history_job_chunks ADD CONSTRAINT band_rank_history_job_chunks_pkey PRIMARY KEY (job_id, ranking_scope, combo_id, chunk_ordinal);
                END IF;
            END $$;

            CREATE INDEX IF NOT EXISTS ix_brhjc_job_status_weight
                ON band_rank_history_job_chunks (job_id, status, estimated_rows, ranking_scope, combo_id, chunk_ordinal);

            CREATE TABLE IF NOT EXISTS band_team_ranking_generation (
                generation_id BIGSERIAL PRIMARY KEY,
                scrape_id      BIGINT,
                band_type      TEXT        NOT NULL,
                status         TEXT        NOT NULL,
                computed_at    TIMESTAMPTZ NOT NULL,
                published_at   TIMESTAMPTZ,
                ranking_table  TEXT,
                stats_table    TEXT,
                row_count      BIGINT      NOT NULL DEFAULT 0,
                scope_count    INT         NOT NULL DEFAULT 0,
                created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at     TIMESTAMPTZ NOT NULL DEFAULT now()
            );

            CREATE INDEX IF NOT EXISTS ix_btrg_band_status
                ON band_team_ranking_generation (band_type, status, generation_id DESC);

            CREATE TABLE IF NOT EXISTS band_team_rank_history_snapshot_v2 (
                snapshot_id      BIGSERIAL PRIMARY KEY,
                generation_id    BIGINT      NOT NULL,
                band_type        TEXT        NOT NULL,
                ranking_scope    TEXT        NOT NULL,
                combo_id         TEXT        NOT NULL DEFAULT '',
                snapshot_date    DATE        NOT NULL,
                computed_at      TIMESTAMPTZ NOT NULL,
                source_row_count BIGINT      NOT NULL DEFAULT 0,
                changed_row_count BIGINT     NOT NULL DEFAULT 0,
                status           TEXT        NOT NULL DEFAULT 'complete',
                completed_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
                created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
                UNIQUE (band_type, ranking_scope, combo_id, snapshot_date)
            );

            CREATE INDEX IF NOT EXISTS ix_btrhsv2_generation
                ON band_team_rank_history_snapshot_v2 (generation_id, band_type, ranking_scope, combo_id);

            CREATE TABLE IF NOT EXISTS band_team_rank_history_points_v2 (
                band_type             TEXT             NOT NULL,
                ranking_scope         TEXT             NOT NULL,
                combo_id              TEXT             NOT NULL DEFAULT '',
                team_key              TEXT             NOT NULL,
                snapshot_date         DATE             NOT NULL,
                snapshot_id           BIGINT           NOT NULL,
                generation_id         BIGINT           NOT NULL,
                snapshot_taken_at     TIMESTAMPTZ      NOT NULL,
                row_fingerprint       TEXT             NOT NULL,
                adjusted_skill_rank   INT              NOT NULL,
                weighted_rank         INT              NOT NULL,
                fc_rate_rank          INT              NOT NULL,
                total_score_rank      INT              NOT NULL,
                adjusted_skill_rating DOUBLE PRECISION,
                weighted_rating       DOUBLE PRECISION,
                fc_rate               DOUBLE PRECISION,
                total_score           BIGINT,
                songs_played          INT,
                coverage              DOUBLE PRECISION,
                full_combo_count      INT,
                total_charted_songs   INT,
                total_ranked_teams    INT,
                raw_weighted_rating   DOUBLE PRECISION,
                raw_skill_rating      DOUBLE PRECISION,
                PRIMARY KEY (band_type, ranking_scope, combo_id, team_key, snapshot_date)
            ) PARTITION BY LIST (band_type);

            CREATE TABLE IF NOT EXISTS band_team_rank_history_points_v2_duets
                PARTITION OF band_team_rank_history_points_v2 FOR VALUES IN ('Band_Duets');

            CREATE TABLE IF NOT EXISTS band_team_rank_history_points_v2_trios
                PARTITION OF band_team_rank_history_points_v2 FOR VALUES IN ('Band_Trios');

            CREATE TABLE IF NOT EXISTS band_team_rank_history_points_v2_quad
                PARTITION OF band_team_rank_history_points_v2 FOR VALUES IN ('Band_Quad');

            CREATE INDEX IF NOT EXISTS ix_btrhpv2_snapshot
                ON band_team_rank_history_points_v2 (snapshot_id, band_type);

            CREATE INDEX IF NOT EXISTS ix_btrhpv2_team_date
                ON band_team_rank_history_points_v2 (band_type, ranking_scope, combo_id, team_key, snapshot_date DESC);

            CREATE TABLE IF NOT EXISTS band_team_rank_history_latest_v2 (
                band_type       TEXT        NOT NULL,
                ranking_scope   TEXT        NOT NULL,
                combo_id        TEXT        NOT NULL DEFAULT '',
                team_key        TEXT        NOT NULL,
                generation_id   BIGINT      NOT NULL,
                snapshot_id     BIGINT      NOT NULL,
                snapshot_date   DATE        NOT NULL,
                row_fingerprint TEXT        NOT NULL,
                updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (band_type, ranking_scope, combo_id, team_key)
            ) PARTITION BY LIST (band_type);

            CREATE TABLE IF NOT EXISTS band_team_rank_history_latest_v2_duets
                PARTITION OF band_team_rank_history_latest_v2 FOR VALUES IN ('Band_Duets');

            CREATE TABLE IF NOT EXISTS band_team_rank_history_latest_v2_trios
                PARTITION OF band_team_rank_history_latest_v2 FOR VALUES IN ('Band_Trios');

            CREATE TABLE IF NOT EXISTS band_team_rank_history_latest_v2_quad
                PARTITION OF band_team_rank_history_latest_v2 FOR VALUES IN ('Band_Quad');

            CREATE INDEX IF NOT EXISTS ix_btrhlv2_snapshot
                ON band_team_rank_history_latest_v2 (snapshot_id, band_type);";
        cmd.ExecuteNonQuery();

        using var metadataCmd = conn.CreateCommand();
        metadataCmd.Transaction = tx;
        metadataCmd.CommandText = string.Join(
            Environment.NewLine,
            BandRankingStorageNames.AllBandTypes.Select(bandType =>
                BandRankingStorageNames.GetEnsureRankingMetadataColumnsSql(BandRankingStorageNames.GetCurrentRankingTable(bandType))));
        metadataCmd.ExecuteNonQuery();
    }

    private void EnsureBandRankHistoryPollingSchema(NpgsqlConnection conn)
    {
        if (_bandRankHistoryPollingSchemaEnsured)
            return;

        lock (_bandRankHistoryPollingSchemaLock)
        {
            if (_bandRankHistoryPollingSchemaEnsured)
                return;

            using var tx = conn.BeginTransaction();
            EnsureBandRankHistoryTables(conn, tx);
            tx.Commit();
            _bandRankHistoryPollingSchemaEnsured = true;
        }
    }

    private static string ResolveBandRankingReadTable(NpgsqlConnection conn, string bandType)
        => BandRankingStorageNames.GetCurrentRankingTable(bandType);

    private static string ResolveBandRankingStatsReadTable(NpgsqlConnection conn, string bandType)
        => BandRankingStorageNames.GetCurrentStatsTable(bandType);

    private static long ReadCurrentBandRankingGeneration(NpgsqlConnection conn, string bandType)
    {
        var rankingsTable = ResolveBandRankingReadTable(conn, bandType);
        if (!TableExists(conn, null, rankingsTable))
            return 0;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT COALESCE(NULLIF(max(ranking_generation), 0), 0)
            FROM {BandRankingStorageNames.QuoteIdentifier(rankingsTable)}
            WHERE band_type = @bandType";
        cmd.Parameters.AddWithValue("bandType", bandType);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private static bool TableExists(NpgsqlConnection conn, NpgsqlTransaction? transaction, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT to_regclass(@tableName) IS NOT NULL";
        cmd.Parameters.AddWithValue("tableName", $"public.{tableName}");
        return Convert.ToBoolean(cmd.ExecuteScalar() ?? false);
    }

    // ── Private helpers ──────────────────────────────────────────────

    private void SimpleUpdate(string sql, string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = sql; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    private static int EstimateBackfillSongCount(int pairCount, int instrumentCount, bool roundUp)
    {
        if (pairCount <= 0 || instrumentCount <= 0) return 0;
        return roundUp ? (pairCount + instrumentCount - 1) / instrumentCount : pairCount / instrumentCount;
    }
    private static BackfillStatusInfo ReadBackfillStatus(NpgsqlDataReader r) => new() { AccountId = r.GetString(0), Status = r.GetString(1), SongsChecked = r.GetInt32(2), EntriesFound = r.GetInt32(3), TotalSongsToCheck = r.GetInt32(4), StartedAt = r.IsDBNull(5) ? null : r.GetDateTime(5).ToString("o"), CompletedAt = r.IsDBNull(6) ? null : r.GetDateTime(6).ToString("o"), LastResumedAt = r.IsDBNull(7) ? null : r.GetDateTime(7).ToString("o"), ErrorMessage = r.IsDBNull(8) ? null : r.GetString(8), RankingsPending = !r.IsDBNull(9) && r.GetBoolean(9), DeferredReason = r.IsDBNull(10) ? null : r.GetString(10) };
    private static HistoryReconStatusInfo ReadHistoryReconStatus(NpgsqlDataReader r) => new() { AccountId = r.GetString(0), Status = r.GetString(1), SongsProcessed = r.GetInt32(2), TotalSongsToProcess = r.GetInt32(3), SeasonsQueried = r.GetInt32(4), HistoryEntriesFound = r.GetInt32(5), StartedAt = r.IsDBNull(6) ? null : r.GetDateTime(6).ToString("o"), CompletedAt = r.IsDBNull(7) ? null : r.GetDateTime(7).ToString("o"), ErrorMessage = r.IsDBNull(8) ? null : r.GetString(8) };
    private static CompositeRankingDto ReadCompositeRanking(NpgsqlDataReader r) => new() { AccountId = r.GetString(0), InstrumentsPlayed = r.GetInt32(1), TotalSongsPlayed = r.GetInt32(2), CompositeRating = r.GetDouble(3), CompositeRank = r.GetInt32(4), GuitarAdjustedSkill = r.IsDBNull(5) ? null : r.GetDouble(5), GuitarSkillRank = r.IsDBNull(6) ? null : r.GetInt32(6), BassAdjustedSkill = r.IsDBNull(7) ? null : r.GetDouble(7), BassSkillRank = r.IsDBNull(8) ? null : r.GetInt32(8), DrumsAdjustedSkill = r.IsDBNull(9) ? null : r.GetDouble(9), DrumsSkillRank = r.IsDBNull(10) ? null : r.GetInt32(10), VocalsAdjustedSkill = r.IsDBNull(11) ? null : r.GetDouble(11), VocalsSkillRank = r.IsDBNull(12) ? null : r.GetInt32(12), ProGuitarAdjustedSkill = r.IsDBNull(13) ? null : r.GetDouble(13), ProGuitarSkillRank = r.IsDBNull(14) ? null : r.GetInt32(14), ProBassAdjustedSkill = r.IsDBNull(15) ? null : r.GetDouble(15), ProBassSkillRank = r.IsDBNull(16) ? null : r.GetInt32(16), ProVocalsAdjustedSkill = r.IsDBNull(17) ? null : r.GetDouble(17), ProVocalsSkillRank = r.IsDBNull(18) ? null : r.GetInt32(18), ProCymbalsAdjustedSkill = r.IsDBNull(19) ? null : r.GetDouble(19), ProCymbalsSkillRank = r.IsDBNull(20) ? null : r.GetInt32(20), ProDrumsAdjustedSkill = r.IsDBNull(21) ? null : r.GetDouble(21), ProDrumsSkillRank = r.IsDBNull(22) ? null : r.GetInt32(22), CompositeRatingWeighted = r.IsDBNull(23) ? null : r.GetDouble(23), CompositeRankWeighted = r.IsDBNull(24) ? null : r.GetInt32(24), CompositeRatingFcRate = r.IsDBNull(25) ? null : r.GetDouble(25), CompositeRankFcRate = r.IsDBNull(26) ? null : r.GetInt32(26), CompositeRatingTotalScore = r.IsDBNull(27) ? null : r.GetDouble(27), CompositeRankTotalScore = r.IsDBNull(28) ? null : r.GetInt32(28), CompositeRatingMaxScore = r.IsDBNull(29) ? null : r.GetDouble(29), CompositeRankMaxScore = r.IsDBNull(30) ? null : r.GetInt32(30), ComputedAt = r.GetDateTime(31).ToString("o") };
    private static string SoloFamilyRankColumn(string rankBy) => (rankBy ?? "adjusted").ToLowerInvariant() switch
    {
        "weighted" => "weighted_rank",
        "fcrate" or "fc" or "fc_rate" => "fc_rate_rank",
        "totalscore" or "total_score" => "total_score_rank",
        "maxscore" or "max_score" => "max_score_percent_rank",
        _ => "adjusted_skill_rank",
    };
    private static int GetSoloFamilyTotalAccounts(NpgsqlConnection conn, string scopeId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM solo_family_rankings WHERE scope_id = @scopeId";
        cmd.Parameters.AddWithValue("scopeId", scopeId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
    private static SoloFamilyRankingDto ReadSoloFamilyRanking(NpgsqlDataReader r, int totalRankedAccounts = 0) => new()
    {
        ScopeId = r.GetString(0),
        AccountId = r.GetString(1),
        SongsPlayed = r.GetInt32(2),
        TotalChartedSongs = r.GetInt32(3),
        Coverage = r.GetDouble(4),
        RawSkillRating = r.GetDouble(5),
        AdjustedSkillRating = r.GetDouble(6),
        AdjustedSkillRank = r.GetInt32(7),
        WeightedRating = r.GetDouble(8),
        WeightedRank = r.GetInt32(9),
        FcRate = r.GetDouble(10),
        FcRateRank = r.GetInt32(11),
        TotalScore = r.GetInt64(12),
        TotalScoreRank = r.GetInt32(13),
        MaxScorePercent = r.GetDouble(14),
        MaxScorePercentRank = r.GetInt32(15),
        FullComboCount = r.GetInt32(16),
        RawMaxScorePercent = r.IsDBNull(17) ? null : r.GetDouble(17),
        RawWeightedRating = r.IsDBNull(18) ? null : r.GetDouble(18),
        ComputedAt = r.GetDateTime(19).ToString("o"),
        TotalRankedAccounts = totalRankedAccounts,
    };
    private static ComboLeaderboardEntry ReadComboEntry(NpgsqlDataReader r) => new() { Rank = (int)r.GetInt64(0), AccountId = r.GetString(1), AdjustedRating = r.GetDouble(2), WeightedRating = r.GetDouble(3), FcRate = r.GetDouble(4), TotalScore = r.GetInt32(5), MaxScorePercent = r.GetDouble(6), SongsPlayed = r.GetInt32(7), FullComboCount = r.GetInt32(8), ComputedAt = r.GetDateTime(9).ToString("o") };
    private static BandTeamRankingDto ReadBandTeamRanking(NpgsqlDataReader r, int totalRankedTeams)
    {
        var teamMembers = r.GetFieldValue<string[]>(3);
        return new()
        {
            BandId = BandIdentity.CreateBandId(r.GetString(0), r.GetString(2)),
            BandType = r.GetString(0),
            ComboId = string.IsNullOrEmpty(r.GetString(1)) ? null : r.GetString(1),
            TeamKey = r.GetString(2),
            TeamMembers = teamMembers,
            Members = ReadBandTeamRankingMembers(teamMembers, r.FieldCount > 23 && !r.IsDBNull(23) ? r.GetString(23) : null),
            SongsPlayed = r.GetInt32(4),
            TotalChartedSongs = r.GetInt32(5),
            Coverage = r.GetDouble(6),
            RawSkillRating = r.GetDouble(7),
            AdjustedSkillRating = r.GetDouble(8),
            AdjustedSkillRank = r.GetInt32(9),
            WeightedRating = r.GetDouble(10),
            WeightedRank = r.GetInt32(11),
            FcRate = r.GetDouble(12),
            FcRateRank = r.GetInt32(13),
            TotalScore = r.GetInt64(14),
            TotalScoreRank = r.GetInt32(15),
            AvgAccuracy = r.GetDouble(16),
            FullComboCount = r.GetInt32(17),
            AvgStars = r.GetDouble(18),
            BestRank = r.GetInt32(19),
            AvgRank = r.GetDouble(20),
            RawWeightedRating = r.IsDBNull(21) ? null : r.GetDouble(21),
            ComputedAt = r.GetDateTime(22).ToString("o"),
            TotalRankedTeams = totalRankedTeams,
        };
    }

    private static List<PlayerBandMemberDto> ReadBandTeamRankingMembers(string[] teamMembers, string? memberInstrumentsJson)
    {
        var instrumentsByMember = ParseMemberInstrumentsJson(memberInstrumentsJson);
        return teamMembers.Select(accountId => new PlayerBandMemberDto
        {
            AccountId = accountId,
            Instruments = instrumentsByMember.TryGetValue(accountId, out var instruments) ? instruments : [],
        }).ToList();
    }

    private static void AttachBandRankingConfigurations(NpgsqlConnection conn, IReadOnlyCollection<BandTeamRankingDto> rankings, string bandType, string comboId)
    {
        if (!ShouldAttachBandRankingConfigurations(bandType, comboId) || rankings.Count == 0)
            return;

        var rawCombos = BandComboIds.ToEpicRawComboCandidates(comboId).ToArray();
        if (rawCombos.Length == 0)
            return;

        var rankingsByTeamKey = rankings
            .GroupBy(static ranking => ranking.TeamKey, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);
        var teamKeys = rankingsByTeamKey.Keys.ToArray();
        if (teamKeys.Length == 0)
            return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT team_key, instrument_combo, assignment_key, appearance_count, member_assignments_json::text
            FROM {BandLeaderboardPersistence.BandTeamConfigurationTable}
            WHERE band_type = @bandType
              AND team_key = ANY(@teamKeys)
              AND instrument_combo = ANY(@rawCombos)
            ORDER BY team_key, appearance_count DESC, assignment_key
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKeys", NpgsqlDbType.Array | NpgsqlDbType.Text, teamKeys);
        cmd.Parameters.AddWithValue("rawCombos", NpgsqlDbType.Array | NpgsqlDbType.Text, rawCombos);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var teamKey = reader.GetString(0);
            if (!rankingsByTeamKey.TryGetValue(teamKey, out var matchingRankings))
                continue;

            var rawCombo = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var observedComboId = BandComboIds.FromEpicRawCombo(rawCombo);
            var configuration = new BandConfigurationDto
            {
                RawInstrumentCombo = rawCombo,
                ComboId = observedComboId,
                Instruments = BandComboIds.ToInstruments(observedComboId).ToList(),
                AssignmentKey = reader.GetString(2),
                AppearanceCount = reader.GetInt32(3),
                MemberInstruments = ParseMemberAssignmentJson(reader.IsDBNull(4) ? "{}" : reader.GetString(4)),
            };

            foreach (var ranking in matchingRankings)
                ranking.Configurations.Add(configuration);
        }
    }

    private static bool ShouldAttachBandRankingConfigurations(string bandType, string comboId) =>
        !string.IsNullOrWhiteSpace(comboId)
        && string.Equals(bandType, "Band_Duets", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, List<string>> ParseMemberInstrumentsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var instruments = property.Value.ValueKind == JsonValueKind.Array
                ? property.Value.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString())
                    .Where(static instrument => !string.IsNullOrWhiteSpace(instrument))
                    .Select(static instrument => instrument!)
                    .ToList()
                : [];

            result[property.Name] = instruments;
        }

        return result;
    }

    private static Dictionary<string, string> ParseMemberAssignmentJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;

            var instrument = property.Value.GetString();
            if (!string.IsNullOrWhiteSpace(instrument))
                result[property.Name] = instrument;
        }

        return result;
    }
    private static (string Column, string Direction) RankByColumn(string rankBy) => rankBy.ToLowerInvariant() switch { "weighted" => ("weighted_rating", "ASC"), "fcrate" => ("fc_rate", "DESC"), "totalscore" => ("total_score", "DESC"), "maxscore" => ("max_score_percent", "DESC"), _ => ("adjusted_rating", "ASC") };
    private static string ComboRankOrderBy(string rankBy) { var (col, dir) = RankByColumn(rankBy); return rankBy.Equals("fcrate", StringComparison.OrdinalIgnoreCase) ? $"{col} {dir}, total_score DESC, songs_played DESC, account_id ASC" : $"{col} {dir}, songs_played DESC, account_id ASC"; }
    private static string ComboRankPrecedesPredicate(string rankBy)
    {
        var (column, direction) = RankByColumn(rankBy);
        if (rankBy.Equals("fcrate", StringComparison.OrdinalIgnoreCase))
        {
            return """
                other.fc_rate > target.fc_rate
                OR (other.fc_rate = target.fc_rate AND other.total_score > target.total_score)
                OR (other.fc_rate = target.fc_rate AND other.total_score = target.total_score AND other.songs_played > target.songs_played)
                OR (other.fc_rate = target.fc_rate AND other.total_score = target.total_score AND other.songs_played = target.songs_played AND other.account_id < target.account_id)
                """;
        }

        var comparison = direction.Equals("ASC", StringComparison.OrdinalIgnoreCase) ? "<" : ">";
        return $"""
            other.{column} {comparison} target.{column}
            OR (other.{column} = target.{column} AND other.songs_played > target.songs_played)
            OR (other.{column} = target.{column} AND other.songs_played = target.songs_played AND other.account_id < target.account_id)
            """;
    }
    private static string BandRankColumn(string rankBy) => rankBy switch { "weighted" => "weighted_rank", "fcrate" => "fc_rate_rank", "totalscore" => "total_score_rank", _ => "adjusted_skill_rank" };
    private static List<RivalSongSampleRow> ReadRivalSamples(NpgsqlCommand cmd) { var list = new List<RivalSongSampleRow>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(ReadRivalSample(r)); return list; }
    private static RivalSongSampleRow ReadRivalSample(NpgsqlDataReader r) => new() { UserId = r.GetString(0), RivalAccountId = r.GetString(1), Instrument = r.GetString(2), SongId = r.GetString(3), UserRank = r.GetInt32(4), RivalRank = r.GetInt32(5), RankDelta = r.GetInt32(6), UserScore = r.IsDBNull(7) ? null : r.GetInt32(7), RivalScore = r.IsDBNull(8) ? null : r.GetInt32(8) };

    /// <summary>Parse an ISO 8601 string to UTC DateTime (required by Npgsql for TIMESTAMPTZ).</summary>
    private static DateTime ParseUtc(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    /// <summary>Write a nullable int to a binary importer, or NULL if absent.</summary>
    private static void WriteNullableInt(NpgsqlBinaryImporter writer, int? value)
    {
        if (value.HasValue) writer.Write(value.Value, NpgsqlDbType.Integer);
        else writer.WriteNull();
    }

    private static void WriteNullableReal(NpgsqlBinaryImporter writer, double? value)
    {
        if (value.HasValue) writer.Write((float)value.Value, NpgsqlDbType.Real);
        else writer.WriteNull();
    }

    public void Dispose() { }
}
