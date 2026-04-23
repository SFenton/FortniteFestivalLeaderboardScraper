using System.Diagnostics;
using FSTService.Scraping;
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

    internal const int DataCollectionVersion = 3;
    internal const string WebTrackerDeviceId = "web-tracker";

    public MetaDatabase(NpgsqlDataSource dataSource, ILogger<MetaDatabase> log)
    {
        _ds = dataSource;
        _log = log;
    }

    public void EnsureSchema() { } // Created by DatabaseInitializer

    // ── Scrape log ───────────────────────────────────────────────────

    public long StartScrapeRun()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO scrape_log (started_at) VALUES (@now) RETURNING id";
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        return (long)(int)cmd.ExecuteScalar()!;
    }

    public void CompleteScrapeRun(long scrapeId, int songsScraped, long totalEntries, int totalRequests, long totalBytes)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE scrape_log SET completed_at = @now, songs_scraped = @songs, total_entries = @entries, total_requests = @requests, total_bytes = @bytes WHERE id = @id";
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("songs", songsScraped);
        cmd.Parameters.AddWithValue("entries", (int)totalEntries);
        cmd.Parameters.AddWithValue("requests", totalRequests);
        cmd.Parameters.AddWithValue("bytes", totalBytes);
        cmd.Parameters.AddWithValue("id", (int)scrapeId);
        cmd.ExecuteNonQuery();
    }

    public ScrapeRunInfo? GetLastCompletedScrapeRun()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, started_at, completed_at, songs_scraped, total_entries, total_requests, total_bytes FROM scrape_log WHERE completed_at IS NOT NULL ORDER BY id DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ScrapeRunInfo
        {
            Id = r.GetInt32(0),
            StartedAt = r.GetDateTime(1).ToString("o"),
            CompletedAt = r.IsDBNull(2) ? null : r.GetDateTime(2).ToString("o"),
            SongsScraped = r.IsDBNull(3) ? 0 : r.GetInt32(3),
            TotalEntries = r.IsDBNull(4) ? 0 : r.GetInt32(4),
            TotalRequests = r.IsDBNull(5) ? 0 : r.GetInt32(5),
            TotalBytes = r.IsDBNull(6) ? 0 : r.GetInt64(6),
        };
    }

    // ── Score history ────────────────────────────────────────────────

    public void InsertScoreChange(string songId, string instrument, string accountId,
        int? oldScore, int newScore, int? oldRank, int newRank,
        int? accuracy = null, bool? isFullCombo = null, int? stars = null,
        double? percentile = null, int? season = null, string? scoreAchievedAt = null,
        int? seasonRank = null, int? allTimeRank = null, int? difficulty = null)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
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
        cmd.Parameters.AddWithValue("scoreAchievedAt", scoreAchievedAt is not null ? ParseUtc(scoreAchievedAt) : DBNull.Value);
        cmd.Parameters.AddWithValue("seasonRank", (object?)seasonRank ?? DBNull.Value);
        cmd.Parameters.AddWithValue("allTimeRank", (object?)allTimeRank ?? DBNull.Value);
        cmd.Parameters.AddWithValue("difficulty", (object?)difficulty ?? DBNull.Value);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
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
                c.CommandText =
                    "INSERT INTO score_history (song_id, instrument, account_id, old_score, new_score, old_rank, new_rank, accuracy, is_full_combo, stars, percentile, season, score_achieved_at, season_rank, all_time_rank, difficulty, changed_at) " +
                    "SELECT song_id, instrument, account_id, old_score, new_score, old_rank, new_rank, accuracy, is_full_combo, stars, percentile, season, score_achieved_at, season_rank, all_time_rank, difficulty, changed_at FROM _sh_staging " +
                    "ON CONFLICT(account_id, song_id, instrument, new_score, score_achieved_at) DO UPDATE SET " +
                    "season_rank = COALESCE(EXCLUDED.season_rank, score_history.season_rank), all_time_rank = COALESCE(EXCLUDED.all_time_rank, score_history.all_time_rank), " +
                    "old_score = COALESCE(EXCLUDED.old_score, score_history.old_score), old_rank = COALESCE(EXCLUDED.old_rank, score_history.old_rank), " +
                    "difficulty = COALESCE(EXCLUDED.difficulty, score_history.difficulty), changed_at = EXCLUDED.changed_at";
                inserted = c.ExecuteNonQuery();
            }
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
        tx.Commit();
        return loopInserted;
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
                SongId = r.GetString(0), Instrument = r.GetString(1),
                OldScore = r.IsDBNull(2) ? null : r.GetInt32(2), NewScore = r.GetInt32(3),
                OldRank = r.IsDBNull(4) ? null : r.GetInt32(4), NewRank = r.GetInt32(5),
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
    public List<(string AccountId, string DisplayName)> SearchAccountNames(string query, int limit = 10) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, display_name FROM account_names WHERE display_name IS NOT NULL AND display_name ILIKE @pattern ORDER BY CASE WHEN display_name ILIKE @prefix THEN 0 ELSE 1 END, LENGTH(display_name), display_name LIMIT @limit"; cmd.Parameters.AddWithValue("pattern", $"%{query}%"); cmd.Parameters.AddWithValue("prefix", $"{query}%"); cmd.Parameters.AddWithValue("limit", limit); var list = new List<(string, string)>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add((r.GetString(0), r.GetString(1))); return list; }

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

    public int PruneStaleWebRegistrations(DateTime staleBeforeUtc)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM registered_users WHERE device_id = @deviceId AND COALESCE(last_activity_at, registered_at) < @staleBeforeUtc";
        cmd.Parameters.AddWithValue("deviceId", WebTrackerDeviceId);
        cmd.Parameters.AddWithValue("staleBeforeUtc", staleBeforeUtc);
        return cmd.ExecuteNonQuery();
    }

    public string? GetAccountIdForUsername(string username) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id FROM account_names WHERE LOWER(display_name) = LOWER(@username) LIMIT 1"; cmd.Parameters.AddWithValue("username", username); var result = cmd.ExecuteScalar(); return result is DBNull or null ? null : (string)result; }

    // ── Backfill ─────────────────────────────────────────────────────

    public void EnqueueBackfill(string accountId, int totalSongsToCheck) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO backfill_status (account_id, status, total_songs_to_check) VALUES (@id, 'pending', @total) ON CONFLICT(account_id) DO UPDATE SET status = CASE WHEN backfill_status.status = 'complete' THEN backfill_status.status ELSE 'pending' END, total_songs_to_check = EXCLUDED.total_songs_to_check WHERE backfill_status.status != 'complete'"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("total", totalSongsToCheck); cmd.ExecuteNonQuery(); }
    public List<BackfillStatusInfo> GetPendingBackfills() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, status, songs_checked, entries_found, total_songs_to_check, started_at, completed_at, last_resumed_at, error_message FROM backfill_status WHERE status IN ('pending', 'in_progress')"; var list = new List<BackfillStatusInfo>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(ReadBackfillStatus(r)); return list; }
    public BackfillStatusInfo? GetBackfillStatus(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, status, songs_checked, entries_found, total_songs_to_check, started_at, completed_at, last_resumed_at, error_message FROM backfill_status WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); using var r = cmd.ExecuteReader(); return r.Read() ? ReadBackfillStatus(r) : null; }
    public void StartBackfill(string accountId) { SimpleUpdate("UPDATE backfill_status SET status = 'in_progress', started_at = COALESCE(started_at, @now), last_resumed_at = @now WHERE account_id = @id", accountId); }
    public void CompleteBackfill(string accountId) { SimpleUpdate("UPDATE backfill_status SET status = 'complete', completed_at = @now WHERE account_id = @id", accountId); }
    public void FailBackfill(string accountId, string errorMessage) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE backfill_status SET status = 'error', error_message = @err WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("err", errorMessage); cmd.ExecuteNonQuery(); }
    public void UpdateBackfillProgress(string accountId, int songsChecked, int entriesFound) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE backfill_status SET songs_checked = @checked, entries_found = @found WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("checked", songsChecked); cmd.Parameters.AddWithValue("found", entriesFound); cmd.ExecuteNonQuery(); }
    public void MarkBackfillSongChecked(string accountId, string songId, string instrument, bool entryFound) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO backfill_progress (account_id, song_id, instrument, checked, entry_found, checked_at) VALUES (@acct, @song, @inst, 1, @found, @now) ON CONFLICT(account_id, song_id, instrument) DO UPDATE SET checked = 1, entry_found = EXCLUDED.entry_found, checked_at = EXCLUDED.checked_at"; cmd.Parameters.AddWithValue("acct", accountId); cmd.Parameters.AddWithValue("song", songId); cmd.Parameters.AddWithValue("inst", instrument); cmd.Parameters.AddWithValue("found", entryFound ? 1 : 0); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    public HashSet<(string SongId, string Instrument)> GetCheckedBackfillPairs(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id, instrument FROM backfill_progress WHERE account_id = @acct AND checked = 1"; cmd.Parameters.AddWithValue("acct", accountId); var set = new HashSet<(string, string)>(); using var r = cmd.ExecuteReader(); while (r.Read()) set.Add((r.GetString(0), r.GetString(1))); return set; }

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
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
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

    public void SnapshotCompositeRankHistory(int retentionDays = 365)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var cutoff = today.AddDays(-retentionDays);
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

        // Step C: Look-back trim retention
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = @"
                DELETE FROM composite_rank_history crh
                WHERE crh.snapshot_date < @cutoff
                  AND EXISTS (
                    SELECT 1 FROM composite_rank_history crh2
                    WHERE crh2.account_id = crh.account_id
                      AND crh2.snapshot_date > crh.snapshot_date
                      AND crh2.snapshot_date <= @cutoff
                  )";
            c.Parameters.AddWithValue("cutoff", cutoff);
            c.ExecuteNonQuery();
        }

        tx.Commit();
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

    public (List<ComboLeaderboardEntry> Entries, int TotalAccounts) GetComboLeaderboard(string comboId, string rankBy = "adjusted", int page = 1, int pageSize = 50) { using var conn = _ds.OpenConnection(); int total; using (var c = conn.CreateCommand()) { c.CommandText = "SELECT total_accounts FROM combo_stats WHERE combo_id = @id"; c.Parameters.AddWithValue("id", comboId); var r2 = c.ExecuteScalar(); total = r2 is DBNull or null ? 0 : Convert.ToInt32(r2); } var (col, dir) = RankByColumn(rankBy); using var cmd = conn.CreateCommand(); cmd.CommandText = $"SELECT ROW_NUMBER() OVER (ORDER BY {col} {dir}, songs_played DESC, account_id ASC) AS rank, account_id, adjusted_rating, weighted_rating, fc_rate, total_score, max_score_percent, songs_played, full_combo_count, computed_at FROM combo_leaderboard WHERE combo_id = @id ORDER BY {col} {dir}, songs_played DESC, account_id ASC LIMIT @limit OFFSET @offset"; cmd.Parameters.AddWithValue("id", comboId); cmd.Parameters.AddWithValue("limit", pageSize); cmd.Parameters.AddWithValue("offset", (page - 1) * pageSize); var list = new List<ComboLeaderboardEntry>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(ReadComboEntry(r)); return (list, total); }
    public ComboLeaderboardEntry? GetComboRank(string comboId, string accountId, string rankBy = "adjusted") { var (col, dir) = RankByColumn(rankBy); using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = $"SELECT rank, account_id, adjusted_rating, weighted_rating, fc_rate, total_score, max_score_percent, songs_played, full_combo_count, computed_at FROM (SELECT ROW_NUMBER() OVER (ORDER BY {col} {dir}, songs_played DESC, account_id ASC) AS rank, account_id, adjusted_rating, weighted_rating, fc_rate, total_score, max_score_percent, songs_played, full_combo_count, computed_at FROM combo_leaderboard WHERE combo_id = @id) sub WHERE account_id = @aid"; cmd.Parameters.AddWithValue("id", comboId); cmd.Parameters.AddWithValue("aid", accountId); using var r = cmd.ExecuteReader(); return r.Read() ? ReadComboEntry(r) : null; }
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
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        try
        {
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
            var buildRankingTable = CreateBandRankingBuildTable(conn, tx, resolvedOptions, bandType, buildSuffix);
            resultRowCount = resolvedOptions.WriteMode switch
            {
                BandTeamRankingWriteMode.Monolithic => InsertBandTeamRankingRowsMonolithic(conn, tx, resolvedOptions, buildRankingTable),
                BandTeamRankingWriteMode.ComboBatched => InsertBandTeamRankingRowsComboBatched(conn, tx, resolvedOptions, buildRankingTable),
                BandTeamRankingWriteMode.Phased => InsertBandTeamRankingRowsMonolithic(conn, tx, resolvedOptions, buildRankingTable),
                _ => throw new ArgumentOutOfRangeException(nameof(resolvedOptions.WriteMode), resolvedOptions.WriteMode, "Unsupported band ranking write mode."),
            };
            CreateBandRankingIndexes(conn, tx, resolvedOptions, buildRankingTable);
            insertRankingsSw.Stop();
            insertRankingsMs = RoundElapsed(insertRankingsSw);
            LogBandRebuildStage(bandType, resolvedOptions, "insert_rankings", insertRankingsMs, rowCount: resultRowCount);
            lastCompletedStage = "insert_rankings";

            currentStage = "insert_stats";
            var insertStatsSw = Stopwatch.StartNew();
            var buildStatsTable = CreateBandRankingStatsBuildTable(conn, tx, resolvedOptions, bandType, buildSuffix);
            statsRowCount = InsertBandTeamRankingStatsRows(conn, tx, resolvedOptions, buildStatsTable);
            CreateBandRankingStatsIndexes(conn, tx, resolvedOptions, buildStatsTable);
            insertStatsSw.Stop();
            insertStatsMs = RoundElapsed(insertStatsSw);
            LogBandRebuildStage(bandType, resolvedOptions, "insert_stats", insertStatsMs, rowCount: statsRowCount);
            lastCompletedStage = "insert_stats";

            currentStage = "swap_current";
            var swapSw = Stopwatch.StartNew();
            SwapBandCurrentTables(conn, tx, resolvedOptions, bandType, buildRankingTable, buildStatsTable, buildSuffix);
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
                    full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating, computed_at)
                SELECT
                    band_type, ranking_scope, combo_id, team_key, team_members,
                    songs_played, total_charted_songs, coverage, raw_skill_rating,
                    adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                    fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                    full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating, computed_at
                FROM _band_rank_results
                {whereClause}
                {orderByClause};";

    private static int InsertBandTeamRankingRowsMonolithic(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string targetTable)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = BuildBandTeamRankingInsertSql(targetTable, string.Empty, "ORDER BY ranking_scope, combo_id, team_key");
        return cmd.ExecuteNonQuery();
    }

    private static int InsertBandTeamRankingRowsComboBatched(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string targetTable)
    {
        var insertedRows = 0;

        using (var cmd = conn.CreateCommand())
        {
            ConfigureBandRebuildCommand(cmd, tx, options);
            cmd.CommandText = BuildBandTeamRankingInsertSql(targetTable, "WHERE ranking_scope = 'overall'", "ORDER BY team_key");
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

        if (TableExists(conn, tx, backupRankingTable))
            statements.Add($"DROP TABLE {BandRankingStorageNames.QuoteIdentifier(backupRankingTable)}");

        if (TableExists(conn, tx, backupStatsTable))
            statements.Add($"DROP TABLE {BandRankingStorageNames.QuoteIdentifier(backupStatsTable)}");

        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = string.Join(";\n", statements) + ";";
        cmd.ExecuteNonQuery();
    }

    private static double RoundElapsed(Stopwatch sw) => Math.Round(sw.Elapsed.TotalMilliseconds, 3);

    public void SnapshotBandRankHistory(string bandType, int retentionDays = 365)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var cutoff = today.AddDays(-retentionDays);

        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        EnsureBandRankHistoryTables(conn, tx);

        var rankingsTable = ResolveBandRankingReadTable(conn, bandType);
        var statsTable = ResolveBandRankingStatsReadTable(conn, bandType);

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $@"
                CREATE TEMP TABLE _latest_band_rank_history ON COMMIT DROP AS
                SELECT DISTINCT ON (ranking_scope, combo_id, team_key)
                    ranking_scope,
                    combo_id,
                    team_key,
                    team_members,
                    songs_played,
                    total_charted_songs,
                    coverage,
                    raw_skill_rating,
                    adjusted_skill_rating,
                    adjusted_skill_rank,
                    weighted_rating,
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
                    raw_weighted_rating
                FROM band_team_rank_history
                WHERE band_type = @bandType
                ORDER BY ranking_scope, combo_id, team_key, snapshot_date DESC;

                CREATE TEMP TABLE _latest_band_rank_stats_history ON COMMIT DROP AS
                SELECT DISTINCT ON (ranking_scope, combo_id)
                    ranking_scope,
                    combo_id,
                    total_teams
                FROM band_team_ranking_stats_history
                WHERE band_type = @bandType
                ORDER BY ranking_scope, combo_id, snapshot_date DESC;";
            cmd.Parameters.AddWithValue("bandType", bandType);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $@"
                INSERT INTO band_team_rank_history (
                    band_type, ranking_scope, combo_id, team_key, team_members,
                    songs_played, total_charted_songs, coverage, raw_skill_rating,
                    adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                    fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                    full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating,
                    computed_at, snapshot_date)
                SELECT
                    src.band_type,
                    src.ranking_scope,
                    src.combo_id,
                    src.team_key,
                    src.team_members,
                    src.songs_played,
                    src.total_charted_songs,
                    src.coverage,
                    src.raw_skill_rating,
                    src.adjusted_skill_rating,
                    src.adjusted_skill_rank,
                    src.weighted_rating,
                    src.weighted_rank,
                    src.fc_rate,
                    src.fc_rate_rank,
                    src.total_score,
                    src.total_score_rank,
                    src.avg_accuracy,
                    src.full_combo_count,
                    src.avg_stars,
                    src.best_rank,
                    src.avg_rank,
                    src.raw_weighted_rating,
                    src.computed_at,
                    @today
                FROM {BandRankingStorageNames.QuoteIdentifier(rankingsTable)} src
                LEFT JOIN _latest_band_rank_history latest
                    ON latest.ranking_scope = src.ranking_scope
                   AND latest.combo_id = src.combo_id
                   AND latest.team_key = src.team_key
                WHERE src.band_type = @bandType
                  AND (
                    latest.team_key IS NULL
                    OR latest.team_members IS DISTINCT FROM src.team_members
                    OR latest.songs_played IS DISTINCT FROM src.songs_played
                    OR latest.total_charted_songs IS DISTINCT FROM src.total_charted_songs
                    OR latest.coverage IS DISTINCT FROM src.coverage
                    OR latest.raw_skill_rating IS DISTINCT FROM src.raw_skill_rating
                    OR latest.adjusted_skill_rating IS DISTINCT FROM src.adjusted_skill_rating
                    OR latest.adjusted_skill_rank IS DISTINCT FROM src.adjusted_skill_rank
                    OR latest.weighted_rating IS DISTINCT FROM src.weighted_rating
                    OR latest.weighted_rank IS DISTINCT FROM src.weighted_rank
                    OR latest.fc_rate IS DISTINCT FROM src.fc_rate
                    OR latest.fc_rate_rank IS DISTINCT FROM src.fc_rate_rank
                    OR latest.total_score IS DISTINCT FROM src.total_score
                    OR latest.total_score_rank IS DISTINCT FROM src.total_score_rank
                    OR latest.avg_accuracy IS DISTINCT FROM src.avg_accuracy
                    OR latest.full_combo_count IS DISTINCT FROM src.full_combo_count
                    OR latest.avg_stars IS DISTINCT FROM src.avg_stars
                    OR latest.best_rank IS DISTINCT FROM src.best_rank
                    OR latest.avg_rank IS DISTINCT FROM src.avg_rank
                    OR latest.raw_weighted_rating IS DISTINCT FROM src.raw_weighted_rating
                  )
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
                    computed_at = EXCLUDED.computed_at;";
            cmd.Parameters.AddWithValue("bandType", bandType);
            cmd.Parameters.AddWithValue("today", today);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $@"
                INSERT INTO band_team_ranking_stats_history (
                    band_type, ranking_scope, combo_id, total_teams, computed_at, snapshot_date)
                SELECT
                    src.band_type,
                    src.ranking_scope,
                    src.combo_id,
                    src.total_teams,
                    src.computed_at,
                    @today
                FROM {BandRankingStorageNames.QuoteIdentifier(statsTable)} src
                LEFT JOIN _latest_band_rank_stats_history latest
                    ON latest.ranking_scope = src.ranking_scope
                   AND latest.combo_id = src.combo_id
                WHERE src.band_type = @bandType
                  AND (
                    latest.combo_id IS NULL
                    OR latest.total_teams IS DISTINCT FROM src.total_teams
                  )
                ON CONFLICT (band_type, ranking_scope, combo_id, snapshot_date) DO UPDATE SET
                    total_teams = EXCLUDED.total_teams,
                    computed_at = EXCLUDED.computed_at;";
            cmd.Parameters.AddWithValue("bandType", bandType);
            cmd.Parameters.AddWithValue("today", today);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                DELETE FROM band_team_rank_history history
                WHERE history.band_type = @bandType
                  AND history.snapshot_date < @cutoff
                  AND EXISTS (
                    SELECT 1
                    FROM band_team_rank_history newer
                    WHERE newer.band_type = history.band_type
                      AND newer.ranking_scope = history.ranking_scope
                      AND newer.combo_id = history.combo_id
                      AND newer.team_key = history.team_key
                      AND newer.snapshot_date > history.snapshot_date
                      AND newer.snapshot_date <= @cutoff
                  );

                DELETE FROM band_team_ranking_stats_history history
                WHERE history.band_type = @bandType
                  AND history.snapshot_date < @cutoff
                  AND EXISTS (
                    SELECT 1
                    FROM band_team_ranking_stats_history newer
                    WHERE newer.band_type = history.band_type
                      AND newer.ranking_scope = history.ranking_scope
                      AND newer.combo_id = history.combo_id
                      AND newer.snapshot_date > history.snapshot_date
                      AND newer.snapshot_date <= @cutoff
                  );";
            cmd.Parameters.AddWithValue("bandType", bandType);
            cmd.Parameters.AddWithValue("cutoff", cutoff);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

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
                band_type, combo_id, team_key, team_members, songs_played, total_charted_songs,
                coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank,
                weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score,
                total_score_rank, avg_accuracy, full_combo_count, avg_stars, best_rank,
                avg_rank, raw_weighted_rating, computed_at
            FROM {BandRankingStorageNames.QuoteIdentifier(rankingsTable)}
            WHERE band_type = @bandType AND ranking_scope = @scope AND combo_id = @comboId
            ORDER BY {rankColumn} ASC
            LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("limit", pageSize);
        cmd.Parameters.AddWithValue("offset", (page - 1) * pageSize);

        var entries = new List<BandTeamRankingDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            entries.Add(ReadBandTeamRanking(reader, totalTeams));

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
                band_type, combo_id, team_key, team_members, songs_played, total_charted_songs,
                coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank,
                weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score,
                total_score_rank, avg_accuracy, full_combo_count, avg_stars, best_rank,
                avg_rank, raw_weighted_rating, computed_at
            FROM {BandRankingStorageNames.QuoteIdentifier(rankingsTable)}
            WHERE band_type = @bandType AND ranking_scope = @scope AND combo_id = @comboId AND team_key = @teamKey";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadBandTeamRanking(reader, totalTeams) : null;
    }

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
            "COPY leaderboard_staging (scrape_id, song_id, instrument, page_num, account_id, score, accuracy, " +
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

        // Delete staging rows in batches to avoid a single massive DELETE that
        // generates excessive WAL and exceeds command timeouts. Each batch
        // runs in its own transaction so progress is incremental.
        const int batchSize = 500_000;
        int deleted;
        do
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandTimeout = 0;
            cmd.CommandText =
                "DELETE FROM leaderboard_staging WHERE ctid = ANY(" +
                "ARRAY(SELECT ctid FROM leaderboard_staging WHERE scrape_id < @id LIMIT @limit))";
            cmd.Parameters.AddWithValue("id", (int)currentScrapeId);
            cmd.Parameters.AddWithValue("limit", batchSize);
            deleted = cmd.ExecuteNonQuery();
            tx.Commit();
            total += deleted;
        } while (deleted >= batchSize);

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
            tx.Commit();
        }
        return total;
    }

    public int DeleteStagedEntries(long scrapeId, string songId, string instrument)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM leaderboard_staging WHERE scrape_id = @scrapeId AND song_id = @songId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        return cmd.ExecuteNonQuery();
    }

    public int DeleteStagedEntriesForInstrument(long scrapeId, string instrument)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM leaderboard_staging WHERE scrape_id = @scrapeId AND instrument = @instrument";
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
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM leaderboard_staging WHERE scrape_id = @scrapeId AND song_id = @songId AND instrument = @instrument";
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

            CREATE INDEX IF NOT EXISTS ix_btrh_latest
                ON band_team_rank_history (band_type, ranking_scope, combo_id, team_key, snapshot_date DESC);

            CREATE TABLE IF NOT EXISTS band_team_ranking_stats_history (
                band_type      TEXT        NOT NULL,
                ranking_scope  TEXT        NOT NULL,
                combo_id       TEXT        NOT NULL DEFAULT '',
                total_teams    INT         NOT NULL,
                computed_at    TIMESTAMPTZ NOT NULL,
                snapshot_date  DATE        NOT NULL,
                PRIMARY KEY (band_type, ranking_scope, combo_id, snapshot_date)
            );

            CREATE INDEX IF NOT EXISTS ix_btrsh_latest
                ON band_team_ranking_stats_history (band_type, ranking_scope, combo_id, snapshot_date DESC);";
        cmd.ExecuteNonQuery();
    }

    private static string ResolveBandRankingReadTable(NpgsqlConnection conn, string bandType)
        => BandRankingStorageNames.GetCurrentRankingTable(bandType);

    private static string ResolveBandRankingStatsReadTable(NpgsqlConnection conn, string bandType)
        => BandRankingStorageNames.GetCurrentStatsTable(bandType);

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
    private static BackfillStatusInfo ReadBackfillStatus(NpgsqlDataReader r) => new() { AccountId = r.GetString(0), Status = r.GetString(1), SongsChecked = r.GetInt32(2), EntriesFound = r.GetInt32(3), TotalSongsToCheck = r.GetInt32(4), StartedAt = r.IsDBNull(5) ? null : r.GetDateTime(5).ToString("o"), CompletedAt = r.IsDBNull(6) ? null : r.GetDateTime(6).ToString("o"), LastResumedAt = r.IsDBNull(7) ? null : r.GetDateTime(7).ToString("o"), ErrorMessage = r.IsDBNull(8) ? null : r.GetString(8) };
    private static HistoryReconStatusInfo ReadHistoryReconStatus(NpgsqlDataReader r) => new() { AccountId = r.GetString(0), Status = r.GetString(1), SongsProcessed = r.GetInt32(2), TotalSongsToProcess = r.GetInt32(3), SeasonsQueried = r.GetInt32(4), HistoryEntriesFound = r.GetInt32(5), StartedAt = r.IsDBNull(6) ? null : r.GetDateTime(6).ToString("o"), CompletedAt = r.IsDBNull(7) ? null : r.GetDateTime(7).ToString("o"), ErrorMessage = r.IsDBNull(8) ? null : r.GetString(8) };
    private static CompositeRankingDto ReadCompositeRanking(NpgsqlDataReader r) => new() { AccountId = r.GetString(0), InstrumentsPlayed = r.GetInt32(1), TotalSongsPlayed = r.GetInt32(2), CompositeRating = r.GetDouble(3), CompositeRank = r.GetInt32(4), GuitarAdjustedSkill = r.IsDBNull(5) ? null : r.GetDouble(5), GuitarSkillRank = r.IsDBNull(6) ? null : r.GetInt32(6), BassAdjustedSkill = r.IsDBNull(7) ? null : r.GetDouble(7), BassSkillRank = r.IsDBNull(8) ? null : r.GetInt32(8), DrumsAdjustedSkill = r.IsDBNull(9) ? null : r.GetDouble(9), DrumsSkillRank = r.IsDBNull(10) ? null : r.GetInt32(10), VocalsAdjustedSkill = r.IsDBNull(11) ? null : r.GetDouble(11), VocalsSkillRank = r.IsDBNull(12) ? null : r.GetInt32(12), ProGuitarAdjustedSkill = r.IsDBNull(13) ? null : r.GetDouble(13), ProGuitarSkillRank = r.IsDBNull(14) ? null : r.GetInt32(14), ProBassAdjustedSkill = r.IsDBNull(15) ? null : r.GetDouble(15), ProBassSkillRank = r.IsDBNull(16) ? null : r.GetInt32(16), ProVocalsAdjustedSkill = r.IsDBNull(17) ? null : r.GetDouble(17), ProVocalsSkillRank = r.IsDBNull(18) ? null : r.GetInt32(18), ProCymbalsAdjustedSkill = r.IsDBNull(19) ? null : r.GetDouble(19), ProCymbalsSkillRank = r.IsDBNull(20) ? null : r.GetInt32(20), ProDrumsAdjustedSkill = r.IsDBNull(21) ? null : r.GetDouble(21), ProDrumsSkillRank = r.IsDBNull(22) ? null : r.GetInt32(22), CompositeRatingWeighted = r.IsDBNull(23) ? null : r.GetDouble(23), CompositeRankWeighted = r.IsDBNull(24) ? null : r.GetInt32(24), CompositeRatingFcRate = r.IsDBNull(25) ? null : r.GetDouble(25), CompositeRankFcRate = r.IsDBNull(26) ? null : r.GetInt32(26), CompositeRatingTotalScore = r.IsDBNull(27) ? null : r.GetDouble(27), CompositeRankTotalScore = r.IsDBNull(28) ? null : r.GetInt32(28), CompositeRatingMaxScore = r.IsDBNull(29) ? null : r.GetDouble(29), CompositeRankMaxScore = r.IsDBNull(30) ? null : r.GetInt32(30), ComputedAt = r.GetDateTime(31).ToString("o") };
    private static ComboLeaderboardEntry ReadComboEntry(NpgsqlDataReader r) => new() { Rank = (int)r.GetInt64(0), AccountId = r.GetString(1), AdjustedRating = r.GetDouble(2), WeightedRating = r.GetDouble(3), FcRate = r.GetDouble(4), TotalScore = r.GetInt32(5), MaxScorePercent = r.GetDouble(6), SongsPlayed = r.GetInt32(7), FullComboCount = r.GetInt32(8), ComputedAt = r.GetDateTime(9).ToString("o") };
    private static BandTeamRankingDto ReadBandTeamRanking(NpgsqlDataReader r, int totalRankedTeams) => new() { BandId = BandIdentity.CreateBandId(r.GetString(0), r.GetString(2)), BandType = r.GetString(0), ComboId = string.IsNullOrEmpty(r.GetString(1)) ? null : r.GetString(1), TeamKey = r.GetString(2), TeamMembers = r.GetFieldValue<string[]>(3), SongsPlayed = r.GetInt32(4), TotalChartedSongs = r.GetInt32(5), Coverage = r.GetDouble(6), RawSkillRating = r.GetDouble(7), AdjustedSkillRating = r.GetDouble(8), AdjustedSkillRank = r.GetInt32(9), WeightedRating = r.GetDouble(10), WeightedRank = r.GetInt32(11), FcRate = r.GetDouble(12), FcRateRank = r.GetInt32(13), TotalScore = r.GetInt64(14), TotalScoreRank = r.GetInt32(15), AvgAccuracy = r.GetDouble(16), FullComboCount = r.GetInt32(17), AvgStars = r.GetDouble(18), BestRank = r.GetInt32(19), AvgRank = r.GetDouble(20), RawWeightedRating = r.IsDBNull(21) ? null : r.GetDouble(21), ComputedAt = r.GetDateTime(22).ToString("o"), TotalRankedTeams = totalRankedTeams };
    private static (string Column, string Direction) RankByColumn(string rankBy) => rankBy switch { "weighted" => ("weighted_rating", "ASC"), "fcrate" => ("fc_rate", "DESC"), "totalscore" => ("total_score", "DESC"), "maxscore" => ("max_score_percent", "DESC"), _ => ("adjusted_rating", "ASC") };
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
