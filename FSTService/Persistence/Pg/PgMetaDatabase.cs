using Npgsql;

namespace FSTService.Persistence.Pg;

/// <summary>
/// PostgreSQL implementation of <see cref="IMetaDatabase"/>.
/// Uses NpgsqlDataSource (connection pooling) — no manual _writeLock or _persistentConn.
/// MVCC handles concurrent reads/writes natively.
/// </summary>
public sealed class PgMetaDatabase : IMetaDatabase
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<PgMetaDatabase> _log;

    internal const int DataCollectionVersion = 3;

    public PgMetaDatabase(NpgsqlDataSource dataSource, ILogger<PgMetaDatabase> log)
    {
        _ds = dataSource;
        _log = log;
    }

    public void EnsureSchema() { } // Created by PgDatabaseInitializer

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
        int? accuracy, bool? isFullCombo, int? stars,
        double? percentile, int? season, string? scoreAchievedAt,
        int? seasonRank, int? allTimeRank, int? difficulty)
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
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO score_history (song_id, instrument, account_id, old_score, new_score, old_rank, new_rank, accuracy, is_full_combo, stars, percentile, season, score_achieved_at, season_rank, all_time_rank, difficulty, changed_at) " +
            "VALUES (@songId, @instrument, @accountId, @oldScore, @newScore, @oldRank, @newRank, @accuracy, @fc, @stars, @percentile, @season, @scoreAchievedAt, @seasonRank, @allTimeRank, @difficulty, @now) " +
            "ON CONFLICT(account_id, song_id, instrument, new_score, score_achieved_at) DO UPDATE SET " +
            "season_rank = COALESCE(EXCLUDED.season_rank, score_history.season_rank), all_time_rank = COALESCE(EXCLUDED.all_time_rank, score_history.all_time_rank), " +
            "old_score = COALESCE(EXCLUDED.old_score, score_history.old_score), old_rank = COALESCE(EXCLUDED.old_rank, score_history.old_rank), " +
            "difficulty = COALESCE(EXCLUDED.difficulty, score_history.difficulty), changed_at = EXCLUDED.changed_at";
        var pSongId = cmd.Parameters.Add("songId", NpgsqlTypes.NpgsqlDbType.Text);
        var pInstrument = cmd.Parameters.Add("instrument", NpgsqlTypes.NpgsqlDbType.Text);
        var pAccountId = cmd.Parameters.Add("accountId", NpgsqlTypes.NpgsqlDbType.Text);
        var pOldScore = cmd.Parameters.Add("oldScore", NpgsqlTypes.NpgsqlDbType.Integer);
        var pNewScore = cmd.Parameters.Add("newScore", NpgsqlTypes.NpgsqlDbType.Integer);
        var pOldRank = cmd.Parameters.Add("oldRank", NpgsqlTypes.NpgsqlDbType.Integer);
        var pNewRank = cmd.Parameters.Add("newRank", NpgsqlTypes.NpgsqlDbType.Integer);
        var pAccuracy = cmd.Parameters.Add("accuracy", NpgsqlTypes.NpgsqlDbType.Integer);
        var pFc = cmd.Parameters.Add("fc", NpgsqlTypes.NpgsqlDbType.Boolean);
        var pStars = cmd.Parameters.Add("stars", NpgsqlTypes.NpgsqlDbType.Integer);
        var pPercentile = cmd.Parameters.Add("percentile", NpgsqlTypes.NpgsqlDbType.Double);
        var pSeason = cmd.Parameters.Add("season", NpgsqlTypes.NpgsqlDbType.Integer);
        var pScoreAchievedAt = cmd.Parameters.Add("scoreAchievedAt", NpgsqlTypes.NpgsqlDbType.TimestampTz);
        var pSeasonRank = cmd.Parameters.Add("seasonRank", NpgsqlTypes.NpgsqlDbType.Integer);
        var pAllTimeRank = cmd.Parameters.Add("allTimeRank", NpgsqlTypes.NpgsqlDbType.Integer);
        var pDifficulty = cmd.Parameters.Add("difficulty", NpgsqlTypes.NpgsqlDbType.Integer);
        var pNow = cmd.Parameters.Add("now", NpgsqlTypes.NpgsqlDbType.TimestampTz);
        cmd.Prepare();
        int inserted = 0;
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
            inserted += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return inserted;
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
        using (var c = conn.CreateCommand()) { c.CommandText = "CREATE TEMP TABLE _valid_thresholds (song_id TEXT, instrument TEXT, max_score INTEGER) ON COMMIT DROP"; c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand()) { c.CommandText = "INSERT INTO _valid_thresholds VALUES (@s, @i, @m)"; var ps = c.Parameters.Add("s", NpgsqlTypes.NpgsqlDbType.Text); var pi = c.Parameters.Add("i", NpgsqlTypes.NpgsqlDbType.Text); var pm = c.Parameters.Add("m", NpgsqlTypes.NpgsqlDbType.Integer); c.Prepare(); foreach (var ((s, i), m) in thresholds) { ps.Value = s; pi.Value = i; pm.Value = m; c.ExecuteNonQuery(); } }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sh.song_id, sh.instrument, sh.new_score, sh.accuracy, sh.is_full_combo, sh.stars FROM score_history sh JOIN _valid_thresholds vt ON vt.song_id = sh.song_id AND vt.instrument = sh.instrument WHERE sh.account_id = @accountId AND sh.new_score <= vt.max_score AND sh.new_score = (SELECT MAX(sh2.new_score) FROM score_history sh2 WHERE sh2.account_id = @accountId AND sh2.song_id = sh.song_id AND sh2.instrument = sh.instrument AND sh2.new_score <= vt.max_score) GROUP BY sh.song_id, sh.instrument, sh.new_score, sh.accuracy, sh.is_full_combo, sh.stars";
        cmd.Parameters.AddWithValue("accountId", accountId);
        var result = new Dictionary<(string, string), ValidScoreFallback>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result[(r.GetString(0), r.GetString(1))] = new ValidScoreFallback { Score = r.GetInt32(2), Accuracy = r.IsDBNull(3) ? null : r.GetInt32(3), IsFullCombo = r.IsDBNull(4) ? null : r.GetBoolean(4), Stars = r.IsDBNull(5) ? null : r.GetInt32(5) };
        return result;
    }

    public Dictionary<(string AccountId, string SongId), ValidScoreFallback> GetBulkBestValidScores(string instrument, Dictionary<(string AccountId, string SongId), int> entries)
    {
        if (entries.Count == 0) return new();
        using var conn = _ds.OpenConnection();
        using (var c = conn.CreateCommand()) { c.CommandText = "CREATE TEMP TABLE _bulk_thresholds (account_id TEXT, song_id TEXT, max_score INTEGER) ON COMMIT DROP"; c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand()) { c.CommandText = "INSERT INTO _bulk_thresholds VALUES (@a, @s, @m)"; var pa = c.Parameters.Add("a", NpgsqlTypes.NpgsqlDbType.Text); var ps = c.Parameters.Add("s", NpgsqlTypes.NpgsqlDbType.Text); var pm = c.Parameters.Add("m", NpgsqlTypes.NpgsqlDbType.Integer); c.Prepare(); foreach (var ((a, s), m) in entries) { pa.Value = a; ps.Value = s; pm.Value = m; c.ExecuteNonQuery(); } }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sh.account_id, sh.song_id, sh.new_score, sh.accuracy, sh.is_full_combo, sh.stars FROM score_history sh JOIN _bulk_thresholds bt ON bt.account_id = sh.account_id AND bt.song_id = sh.song_id WHERE sh.instrument = @instrument AND sh.new_score <= bt.max_score AND sh.new_score = (SELECT MAX(sh2.new_score) FROM score_history sh2 WHERE sh2.account_id = sh.account_id AND sh2.song_id = sh.song_id AND sh2.instrument = @instrument AND sh2.new_score <= bt.max_score) GROUP BY sh.account_id, sh.song_id, sh.new_score, sh.accuracy, sh.is_full_combo, sh.stars";
        cmd.Parameters.AddWithValue("instrument", instrument);
        var result = new Dictionary<(string, string), ValidScoreFallback>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result[(r.GetString(0), r.GetString(1))] = new ValidScoreFallback { Score = r.GetInt32(2), Accuracy = r.IsDBNull(3) ? null : r.GetInt32(3), IsFullCombo = r.IsDBNull(4) ? null : r.GetBoolean(4), Stars = r.IsDBNull(5) ? null : r.GetInt32(5) };
        return result;
    }

    public Dictionary<(string SongId, string Instrument), List<ValidScoreFallback>> GetAllValidScoreTiers(
        string accountId, Dictionary<(string SongId, string Instrument), int> maxThresholds)
    {
        if (maxThresholds.Count == 0) return new();
        using var conn = _ds.OpenConnection();
        using (var c = conn.CreateCommand()) { c.CommandText = "CREATE TEMP TABLE _tier_thresholds (song_id TEXT, instrument TEXT, max_score INTEGER, PRIMARY KEY (song_id, instrument)) ON COMMIT DROP"; c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "INSERT INTO _tier_thresholds VALUES (@s, @i, @m)";
            var ps = c.Parameters.Add("s", NpgsqlTypes.NpgsqlDbType.Text);
            var pi = c.Parameters.Add("i", NpgsqlTypes.NpgsqlDbType.Text);
            var pm = c.Parameters.Add("m", NpgsqlTypes.NpgsqlDbType.Integer);
            c.Prepare();
            foreach (var ((s, i), m) in maxThresholds) { ps.Value = s; pi.Value = i; pm.Value = m; c.ExecuteNonQuery(); }
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sh.song_id, sh.instrument, sh.new_score, MAX(sh.accuracy), MAX(CASE WHEN sh.is_full_combo THEN 1 ELSE 0 END)::BOOLEAN, MAX(sh.stars) FROM score_history sh JOIN _tier_thresholds tt ON tt.song_id = sh.song_id AND tt.instrument = sh.instrument WHERE sh.account_id = @accountId AND sh.new_score <= tt.max_score GROUP BY sh.song_id, sh.instrument, sh.new_score ORDER BY sh.song_id, sh.instrument, sh.new_score DESC";
        cmd.Parameters.AddWithValue("accountId", accountId);
        var result = new Dictionary<(string, string), List<ValidScoreFallback>>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var key = (r.GetString(0), r.GetString(1));
            if (!result.TryGetValue(key, out var list)) { list = new List<ValidScoreFallback>(); result[key] = list; }
            list.Add(new ValidScoreFallback { Score = r.GetInt32(2), Accuracy = r.IsDBNull(3) ? null : r.GetInt32(3), IsFullCombo = r.IsDBNull(4) ? null : r.GetBoolean(4), Stars = r.IsDBNull(5) ? null : r.GetInt32(5) });
        }
        return result;
    }

    // ── Account names ────────────────────────────────────────────────

    public int InsertAccountIds(IEnumerable<string> accountIds)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO account_names (account_id) VALUES (@id) ON CONFLICT DO NOTHING";
        var pId = cmd.Parameters.Add("id", NpgsqlTypes.NpgsqlDbType.Text); cmd.Prepare();
        int inserted = 0;
        foreach (var id in accountIds) { pId.Value = id; inserted += cmd.ExecuteNonQuery(); }
        tx.Commit();
        return inserted;
    }

    public List<string> GetUnresolvedAccountIds() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id FROM account_names WHERE last_resolved IS NULL"; var ids = new List<string>(); using var r = cmd.ExecuteReader(); while (r.Read()) ids.Add(r.GetString(0)); return ids; }
    public int GetUnresolvedAccountCount() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM account_names WHERE last_resolved IS NULL"; return Convert.ToInt32(cmd.ExecuteScalar()); }
    public HashSet<string> GetKnownAccountIds() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id FROM account_names"; var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) ids.Add(r.GetString(0)); return ids; }

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
    public bool RegisterUser(string deviceId, string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO registered_users (device_id, account_id, registered_at) VALUES (@deviceId, @accountId, @now) ON CONFLICT DO NOTHING"; cmd.Parameters.AddWithValue("deviceId", deviceId); cmd.Parameters.AddWithValue("accountId", accountId); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); return cmd.ExecuteNonQuery() > 0; }
    public bool UnregisterUser(string deviceId, string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "DELETE FROM registered_users WHERE device_id = @deviceId AND account_id = @accountId"; cmd.Parameters.AddWithValue("deviceId", deviceId); cmd.Parameters.AddWithValue("accountId", accountId); return cmd.ExecuteNonQuery() > 0; }

    public List<string> UnregisterAccount(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        var deviceIds = new List<string>();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "SELECT device_id FROM registered_users WHERE account_id = @id"; c.Parameters.AddWithValue("id", accountId); using var r = c.ExecuteReader(); while (r.Read()) deviceIds.Add(r.GetString(0)); }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM registered_users WHERE account_id = @id"; c.Parameters.AddWithValue("id", accountId); c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM rival_song_samples WHERE user_id = @id"; c.Parameters.AddWithValue("id", accountId); c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM user_rivals WHERE user_id = @id"; c.Parameters.AddWithValue("id", accountId); c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM rivals_status WHERE account_id = @id"; c.Parameters.AddWithValue("id", accountId); c.ExecuteNonQuery(); }
        tx.Commit();
        return deviceIds;
    }

    public bool RegisterOrUpdateUser(string deviceId, string accountId, string? displayName, string? platform) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO registered_users (device_id, account_id, registered_at, display_name, platform, last_login_at) VALUES (@deviceId, @accountId, @now, @displayName, @platform, @now) ON CONFLICT(device_id, account_id) DO UPDATE SET display_name = @displayName, platform = @platform, last_login_at = @now"; cmd.Parameters.AddWithValue("deviceId", deviceId); cmd.Parameters.AddWithValue("accountId", accountId); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.Parameters.AddWithValue("displayName", (object?)displayName ?? DBNull.Value); cmd.Parameters.AddWithValue("platform", (object?)platform ?? DBNull.Value); cmd.ExecuteNonQuery(); return true; }
    public string? GetAccountIdForUsername(string username) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id FROM account_names WHERE LOWER(display_name) = LOWER(@username) LIMIT 1"; cmd.Parameters.AddWithValue("username", username); var result = cmd.ExecuteScalar(); return result is DBNull or null ? null : (string)result; }
    public RegisteredUserInfo? GetRegistrationInfo(string username, string deviceId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, display_name, registered_at, last_login_at FROM registered_users WHERE account_id = @username AND device_id = @deviceId LIMIT 1"; cmd.Parameters.AddWithValue("username", username); cmd.Parameters.AddWithValue("deviceId", deviceId); using var r = cmd.ExecuteReader(); if (!r.Read()) return null; return new RegisteredUserInfo { AccountId = r.GetString(0), DisplayName = r.IsDBNull(1) ? null : r.GetString(1), RegisteredAt = r.GetDateTime(2).ToString("o"), LastLoginAt = r.IsDBNull(3) ? null : r.GetDateTime(3).ToString("o") }; }
    public string? GetAccountForDevice(string deviceId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id FROM registered_users WHERE device_id = @deviceId ORDER BY registered_at DESC LIMIT 1"; cmd.Parameters.AddWithValue("deviceId", deviceId); var result = cmd.ExecuteScalar(); return result is DBNull or null ? null : (string)result; }
    public void UpdateLastSync(string deviceId, string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE registered_users SET last_sync_at = @now WHERE device_id = @deviceId AND account_id = @accountId"; cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.Parameters.AddWithValue("deviceId", deviceId); cmd.Parameters.AddWithValue("accountId", accountId); cmd.ExecuteNonQuery(); }
    public bool IsDeviceRegistered(string deviceId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM registered_users WHERE device_id = @deviceId"; cmd.Parameters.AddWithValue("deviceId", deviceId); return (long)(cmd.ExecuteScalar() ?? 0) > 0; }

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

    // ── Epic user tokens ─────────────────────────────────────────────

    public void UpsertEpicUserToken(string accountId, byte[] encryptedAccessToken, byte[] encryptedRefreshToken, DateTimeOffset tokenExpiresAt, DateTimeOffset refreshExpiresAt, byte[] nonce) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO epic_user_tokens (account_id, encrypted_access_token, encrypted_refresh_token, token_expires_at, refresh_expires_at, nonce, updated_at) VALUES (@accountId, @accessToken, @refreshToken, @tokenExp, @refreshExp, @nonce, @now) ON CONFLICT(account_id) DO UPDATE SET encrypted_access_token = EXCLUDED.encrypted_access_token, encrypted_refresh_token = EXCLUDED.encrypted_refresh_token, token_expires_at = EXCLUDED.token_expires_at, refresh_expires_at = EXCLUDED.refresh_expires_at, nonce = EXCLUDED.nonce, updated_at = EXCLUDED.updated_at"; cmd.Parameters.AddWithValue("accountId", accountId); cmd.Parameters.AddWithValue("accessToken", encryptedAccessToken); cmd.Parameters.AddWithValue("refreshToken", encryptedRefreshToken); cmd.Parameters.AddWithValue("tokenExp", tokenExpiresAt.UtcDateTime); cmd.Parameters.AddWithValue("refreshExp", refreshExpiresAt.UtcDateTime); cmd.Parameters.AddWithValue("nonce", nonce); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    public StoredEpicUserToken? GetEpicUserToken(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT encrypted_access_token, encrypted_refresh_token, token_expires_at, refresh_expires_at, nonce, updated_at FROM epic_user_tokens WHERE account_id = @accountId"; cmd.Parameters.AddWithValue("accountId", accountId); using var r = cmd.ExecuteReader(); if (!r.Read()) return null; return new StoredEpicUserToken { AccountId = accountId, EncryptedAccessToken = (byte[])r[0], EncryptedRefreshToken = (byte[])r[1], TokenExpiresAt = new DateTimeOffset(r.GetDateTime(2), TimeSpan.Zero), RefreshExpiresAt = new DateTimeOffset(r.GetDateTime(3), TimeSpan.Zero), Nonce = (byte[])r[4], UpdatedAt = r.GetDateTime(5).ToString("o") }; }
    public void DeleteEpicUserToken(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "DELETE FROM epic_user_tokens WHERE account_id = @accountId"; cmd.Parameters.AddWithValue("accountId", accountId); cmd.ExecuteNonQuery(); }

    // ── Player stats ─────────────────────────────────────────────────

    public void UpsertPlayerStats(PlayerStatsDto stats) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO player_stats (account_id, instrument, songs_played, full_combo_count, gold_star_count, avg_accuracy, best_rank, best_rank_song_id, total_score, percentile_dist, avg_percentile, overall_percentile, updated_at) VALUES (@accountId, @instrument, @songsPlayed, @fcCount, @goldStars, @avgAcc, @bestRank, @bestRankSongId, @totalScore, @pctDist, @avgPct, @overallPct, @now) ON CONFLICT(account_id, instrument) DO UPDATE SET songs_played = EXCLUDED.songs_played, full_combo_count = EXCLUDED.full_combo_count, gold_star_count = EXCLUDED.gold_star_count, avg_accuracy = EXCLUDED.avg_accuracy, best_rank = EXCLUDED.best_rank, best_rank_song_id = EXCLUDED.best_rank_song_id, total_score = EXCLUDED.total_score, percentile_dist = EXCLUDED.percentile_dist, avg_percentile = EXCLUDED.avg_percentile, overall_percentile = EXCLUDED.overall_percentile, updated_at = EXCLUDED.updated_at"; cmd.Parameters.AddWithValue("accountId", stats.AccountId); cmd.Parameters.AddWithValue("instrument", stats.Instrument); cmd.Parameters.AddWithValue("songsPlayed", stats.SongsPlayed); cmd.Parameters.AddWithValue("fcCount", stats.FullComboCount); cmd.Parameters.AddWithValue("goldStars", stats.GoldStarCount); cmd.Parameters.AddWithValue("avgAcc", stats.AvgAccuracy); cmd.Parameters.AddWithValue("bestRank", stats.BestRank); cmd.Parameters.AddWithValue("bestRankSongId", (object?)stats.BestRankSongId ?? DBNull.Value); cmd.Parameters.AddWithValue("totalScore", stats.TotalScore); cmd.Parameters.AddWithValue("pctDist", (object?)stats.PercentileDist ?? DBNull.Value); cmd.Parameters.AddWithValue("avgPct", (object?)stats.AvgPercentile ?? DBNull.Value); cmd.Parameters.AddWithValue("overallPct", (object?)stats.OverallPercentile ?? DBNull.Value); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    public List<PlayerStatsDto> GetPlayerStats(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT instrument, songs_played, full_combo_count, gold_star_count, avg_accuracy, best_rank, best_rank_song_id, total_score, percentile_dist, avg_percentile, overall_percentile FROM player_stats WHERE account_id = @accountId"; cmd.Parameters.AddWithValue("accountId", accountId); var list = new List<PlayerStatsDto>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(new PlayerStatsDto { AccountId = accountId, Instrument = r.GetString(0), SongsPlayed = r.GetInt32(1), FullComboCount = r.GetInt32(2), GoldStarCount = r.GetInt32(3), AvgAccuracy = r.GetDouble(4), BestRank = r.GetInt32(5), BestRankSongId = r.IsDBNull(6) ? null : r.GetString(6), TotalScore = r.GetInt64(7), PercentileDist = r.IsDBNull(8) ? null : r.GetString(8), AvgPercentile = r.IsDBNull(9) ? null : r.GetString(9), OverallPercentile = r.IsDBNull(10) ? null : r.GetString(10) }); return list; }

    // ── First seen season ────────────────────────────────────────────

    public HashSet<string> GetSongsWithFirstSeenSeason() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id FROM song_first_seen_season"; var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) set.Add(r.GetString(0)); return set; }
    public int? GetFirstSeenSeason(string songId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT first_seen_season FROM song_first_seen_season WHERE song_id = @songId"; cmd.Parameters.AddWithValue("songId", songId); var result = cmd.ExecuteScalar(); return result is DBNull or null ? null : Convert.ToInt32(result); }
    public void UpsertFirstSeenSeason(string songId, int? firstSeenSeason, int? minObservedSeason, int estimatedSeason, string? probeResult) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO song_first_seen_season (song_id, first_seen_season, min_observed_season, estimated_season, probe_result, calculated_at) VALUES (@songId, @firstSeen, @minObserved, @estimated, @probeResult, @now) ON CONFLICT(song_id) DO UPDATE SET first_seen_season = EXCLUDED.first_seen_season, min_observed_season = EXCLUDED.min_observed_season, estimated_season = EXCLUDED.estimated_season, probe_result = EXCLUDED.probe_result, calculated_at = EXCLUDED.calculated_at"; cmd.Parameters.AddWithValue("songId", songId); cmd.Parameters.AddWithValue("firstSeen", (object?)firstSeenSeason ?? DBNull.Value); cmd.Parameters.AddWithValue("minObserved", (object?)minObservedSeason ?? DBNull.Value); cmd.Parameters.AddWithValue("estimated", estimatedSeason); cmd.Parameters.AddWithValue("probeResult", (object?)probeResult ?? DBNull.Value); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    public Dictionary<string, (int? FirstSeenSeason, int EstimatedSeason)> GetAllFirstSeenSeasons() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id, first_seen_season, estimated_season FROM song_first_seen_season"; var dict = new Dictionary<string, (int?, int)>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) dict[r.GetString(0)] = (r.IsDBNull(1) ? null : r.GetInt32(1), r.GetInt32(2)); return dict; }

    // ── Leaderboard population ───────────────────────────────────────

    public void RaiseLeaderboardPopulationFloor(string songId, string instrument, long floor) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO leaderboard_population (song_id, instrument, total_entries, updated_at) VALUES (@songId, @instrument, @floor, @now) ON CONFLICT (song_id, instrument) DO UPDATE SET total_entries = GREATEST(leaderboard_population.total_entries, EXCLUDED.total_entries), updated_at = CASE WHEN EXCLUDED.total_entries > leaderboard_population.total_entries THEN EXCLUDED.updated_at ELSE leaderboard_population.updated_at END"; cmd.Parameters.AddWithValue("songId", songId); cmd.Parameters.AddWithValue("instrument", instrument); cmd.Parameters.AddWithValue("floor", (int)floor); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }

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
    public void CompleteRivals(string accountId, int combosComputed, int rivalsFound) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE rivals_status SET status = 'complete', combos_computed = @combos, rivals_found = @rivals, completed_at = @now, error_message = NULL WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("combos", combosComputed); cmd.Parameters.AddWithValue("rivals", rivalsFound); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    public void FailRivals(string accountId, string errorMessage) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE rivals_status SET status = 'error', error_message = @err, completed_at = @now WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("err", errorMessage); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    public RivalsStatusInfo? GetRivalsStatus(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, status, combos_computed, total_combos_to_compute, rivals_found, started_at, completed_at, error_message FROM rivals_status WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); using var r = cmd.ExecuteReader(); if (!r.Read()) return null; return new RivalsStatusInfo { AccountId = r.GetString(0), Status = r.GetString(1), CombosComputed = r.GetInt32(2), TotalCombosToCompute = r.GetInt32(3), RivalsFound = r.GetInt32(4), StartedAt = r.IsDBNull(5) ? null : r.GetDateTime(5).ToString("o"), CompletedAt = r.IsDBNull(6) ? null : r.GetDateTime(6).ToString("o"), ErrorMessage = r.IsDBNull(7) ? null : r.GetString(7) }; }
    public List<string> GetPendingRivalsAccounts() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id FROM rivals_status WHERE status IN ('pending', 'in_progress')"; var list = new List<string>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(r.GetString(0)); return list; }

    public void ReplaceRivalsData(string userId, IReadOnlyList<UserRivalRow> rivals, IReadOnlyList<RivalSongSampleRow> samples)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM rival_song_samples WHERE user_id = @uid"; c.Parameters.AddWithValue("uid", userId); c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM user_rivals WHERE user_id = @uid"; c.Parameters.AddWithValue("uid", userId); c.ExecuteNonQuery(); }
        if (rivals.Count > 0) { using var c = conn.CreateCommand(); c.Transaction = tx; c.CommandText = "INSERT INTO user_rivals (user_id, rival_account_id, instrument_combo, direction, rival_score, avg_signed_delta, shared_song_count, ahead_count, behind_count, computed_at) VALUES (@uid, @rid, @combo, @dir, @score, @delta, @songs, @ahead, @behind, @at)"; var pUid = c.Parameters.Add("uid", NpgsqlTypes.NpgsqlDbType.Text); var pRid = c.Parameters.Add("rid", NpgsqlTypes.NpgsqlDbType.Text); var pCombo = c.Parameters.Add("combo", NpgsqlTypes.NpgsqlDbType.Text); var pDir = c.Parameters.Add("dir", NpgsqlTypes.NpgsqlDbType.Text); var pScore = c.Parameters.Add("score", NpgsqlTypes.NpgsqlDbType.Double); var pDelta = c.Parameters.Add("delta", NpgsqlTypes.NpgsqlDbType.Double); var pSongs = c.Parameters.Add("songs", NpgsqlTypes.NpgsqlDbType.Integer); var pAhead = c.Parameters.Add("ahead", NpgsqlTypes.NpgsqlDbType.Integer); var pBehind = c.Parameters.Add("behind", NpgsqlTypes.NpgsqlDbType.Integer); var pAt = c.Parameters.Add("at", NpgsqlTypes.NpgsqlDbType.TimestampTz); c.Prepare(); foreach (var rv in rivals) { pUid.Value = rv.UserId; pRid.Value = rv.RivalAccountId; pCombo.Value = rv.InstrumentCombo; pDir.Value = rv.Direction; pScore.Value = rv.RivalScore; pDelta.Value = rv.AvgSignedDelta; pSongs.Value = rv.SharedSongCount; pAhead.Value = rv.AheadCount; pBehind.Value = rv.BehindCount; pAt.Value = ParseUtc(rv.ComputedAt); c.ExecuteNonQuery(); } }
        if (samples.Count > 0) { using var c = conn.CreateCommand(); c.Transaction = tx; c.CommandText = "INSERT INTO rival_song_samples (user_id, rival_account_id, instrument, song_id, user_rank, rival_rank, rank_delta, user_score, rival_score) VALUES (@uid, @rid, @inst, @sid, @ur, @rr, @rd, @us, @rs)"; var pUid = c.Parameters.Add("uid", NpgsqlTypes.NpgsqlDbType.Text); var pRid = c.Parameters.Add("rid", NpgsqlTypes.NpgsqlDbType.Text); var pInst = c.Parameters.Add("inst", NpgsqlTypes.NpgsqlDbType.Text); var pSid = c.Parameters.Add("sid", NpgsqlTypes.NpgsqlDbType.Text); var pUr = c.Parameters.Add("ur", NpgsqlTypes.NpgsqlDbType.Integer); var pRr = c.Parameters.Add("rr", NpgsqlTypes.NpgsqlDbType.Integer); var pRd = c.Parameters.Add("rd", NpgsqlTypes.NpgsqlDbType.Integer); var pUs = c.Parameters.Add("us", NpgsqlTypes.NpgsqlDbType.Integer); var pRs = c.Parameters.Add("rs", NpgsqlTypes.NpgsqlDbType.Integer); c.Prepare(); foreach (var s in samples) { pUid.Value = s.UserId; pRid.Value = s.RivalAccountId; pInst.Value = s.Instrument; pSid.Value = s.SongId; pUr.Value = s.UserRank; pRr.Value = s.RivalRank; pRd.Value = s.RankDelta; pUs.Value = (object?)s.UserScore ?? DBNull.Value; pRs.Value = (object?)s.RivalScore ?? DBNull.Value; c.ExecuteNonQuery(); } }
        tx.Commit();
    }

    public List<UserRivalRow> GetUserRivals(string userId, string? instrumentCombo = null, string? direction = null) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); var where = "WHERE user_id = @uid"; cmd.Parameters.AddWithValue("uid", userId); if (instrumentCombo is not null) { where += " AND instrument_combo = @combo"; cmd.Parameters.AddWithValue("combo", instrumentCombo); } if (direction is not null) { where += " AND direction = @dir"; cmd.Parameters.AddWithValue("dir", direction); } cmd.CommandText = $"SELECT user_id, rival_account_id, instrument_combo, direction, rival_score, avg_signed_delta, shared_song_count, ahead_count, behind_count, computed_at FROM user_rivals {where} ORDER BY rival_score DESC"; var list = new List<UserRivalRow>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(new UserRivalRow { UserId = r.GetString(0), RivalAccountId = r.GetString(1), InstrumentCombo = r.GetString(2), Direction = r.GetString(3), RivalScore = r.GetDouble(4), AvgSignedDelta = r.GetDouble(5), SharedSongCount = r.GetInt32(6), AheadCount = r.GetInt32(7), BehindCount = r.GetInt32(8), ComputedAt = r.GetDateTime(9).ToString("o") }); return list; }
    public List<RivalComboSummary> GetRivalCombos(string userId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT instrument_combo, SUM(CASE WHEN direction = 'above' THEN 1 ELSE 0 END), SUM(CASE WHEN direction = 'below' THEN 1 ELSE 0 END) FROM user_rivals WHERE user_id = @uid GROUP BY instrument_combo ORDER BY instrument_combo"; cmd.Parameters.AddWithValue("uid", userId); var list = new List<RivalComboSummary>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(new RivalComboSummary { InstrumentCombo = r.GetString(0), AboveCount = (int)r.GetInt64(1), BelowCount = (int)r.GetInt64(2) }); return list; }
    public List<RivalSongSampleRow> GetRivalSongSamples(string userId, string rivalAccountId, string? instrument = null) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); var where = "WHERE user_id = @uid AND rival_account_id = @rid"; cmd.Parameters.AddWithValue("uid", userId); cmd.Parameters.AddWithValue("rid", rivalAccountId); if (instrument is not null) { where += " AND instrument = @inst"; cmd.Parameters.AddWithValue("inst", instrument); } cmd.CommandText = $"SELECT user_id, rival_account_id, instrument, song_id, user_rank, rival_rank, rank_delta, user_score, rival_score FROM rival_song_samples {where} ORDER BY ABS(rank_delta) ASC"; return ReadRivalSamples(cmd); }
    public Dictionary<string, List<RivalSongSampleRow>> GetAllRivalSongSamplesForUser(string userId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT user_id, rival_account_id, instrument, song_id, user_rank, rival_rank, rank_delta, user_score, rival_score FROM rival_song_samples WHERE user_id = @uid ORDER BY rival_account_id, ABS(rank_delta) ASC"; cmd.Parameters.AddWithValue("uid", userId); var dict = new Dictionary<string, List<RivalSongSampleRow>>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) { var sample = ReadRivalSample(r); if (!dict.TryGetValue(sample.RivalAccountId, out var list)) { list = new(); dict[sample.RivalAccountId] = list; } list.Add(sample); } return dict; }

    // ── Leaderboard Rivals ───────────────────────────────────────────

    public void ReplaceLeaderboardRivalsData(string userId, string instrument,
        IReadOnlyList<LeaderboardRivalRow> rivals, IReadOnlyList<LeaderboardRivalSongSampleRow> samples)
    {
        // PG leaderboard rivals tables not yet created — stub for interface compliance.
    }

    public List<LeaderboardRivalRow> GetLeaderboardRivals(string userId, string? instrument = null, string? rankMethod = null, string? direction = null)
        => new();

    public List<LeaderboardRivalSongSampleRow> GetLeaderboardRivalSongSamples(string userId, string rivalAccountId, string instrument, string rankMethod)
        => new();

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
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM composite_rankings"; c.ExecuteNonQuery(); }
        if (rankings.Count > 0) { using var c = conn.CreateCommand(); c.Transaction = tx; c.CommandText = "INSERT INTO composite_rankings (account_id, instruments_played, total_songs_played, composite_rating, composite_rank, guitar_adjusted_skill, guitar_skill_rank, bass_adjusted_skill, bass_skill_rank, drums_adjusted_skill, drums_skill_rank, vocals_adjusted_skill, vocals_skill_rank, pro_guitar_adjusted_skill, pro_guitar_skill_rank, pro_bass_adjusted_skill, pro_bass_skill_rank, computed_at) VALUES (@aid, @instPlayed, @totalSongs, @rating, @rank, @gSkill, @gRank, @bSkill, @bRank, @dSkill, @dRank, @vSkill, @vRank, @pgSkill, @pgRank, @pbSkill, @pbRank, @now)"; var now = DateTime.UtcNow; c.Parameters.Add("aid", NpgsqlTypes.NpgsqlDbType.Text); c.Parameters.Add("instPlayed", NpgsqlTypes.NpgsqlDbType.Integer); c.Parameters.Add("totalSongs", NpgsqlTypes.NpgsqlDbType.Integer); c.Parameters.Add("rating", NpgsqlTypes.NpgsqlDbType.Double); c.Parameters.Add("rank", NpgsqlTypes.NpgsqlDbType.Integer); c.Parameters.Add("gSkill", NpgsqlTypes.NpgsqlDbType.Double); c.Parameters.Add("gRank", NpgsqlTypes.NpgsqlDbType.Integer); c.Parameters.Add("bSkill", NpgsqlTypes.NpgsqlDbType.Double); c.Parameters.Add("bRank", NpgsqlTypes.NpgsqlDbType.Integer); c.Parameters.Add("dSkill", NpgsqlTypes.NpgsqlDbType.Double); c.Parameters.Add("dRank", NpgsqlTypes.NpgsqlDbType.Integer); c.Parameters.Add("vSkill", NpgsqlTypes.NpgsqlDbType.Double); c.Parameters.Add("vRank", NpgsqlTypes.NpgsqlDbType.Integer); c.Parameters.Add("pgSkill", NpgsqlTypes.NpgsqlDbType.Double); c.Parameters.Add("pgRank", NpgsqlTypes.NpgsqlDbType.Integer); c.Parameters.Add("pbSkill", NpgsqlTypes.NpgsqlDbType.Double); c.Parameters.Add("pbRank", NpgsqlTypes.NpgsqlDbType.Integer); c.Parameters.Add("now", NpgsqlTypes.NpgsqlDbType.TimestampTz); c.Prepare(); foreach (var rv in rankings) { c.Parameters["aid"].Value = rv.AccountId; c.Parameters["instPlayed"].Value = rv.InstrumentsPlayed; c.Parameters["totalSongs"].Value = rv.TotalSongsPlayed; c.Parameters["rating"].Value = rv.CompositeRating; c.Parameters["rank"].Value = rv.CompositeRank; c.Parameters["gSkill"].Value = (object?)rv.GuitarAdjustedSkill ?? DBNull.Value; c.Parameters["gRank"].Value = (object?)rv.GuitarSkillRank ?? DBNull.Value; c.Parameters["bSkill"].Value = (object?)rv.BassAdjustedSkill ?? DBNull.Value; c.Parameters["bRank"].Value = (object?)rv.BassSkillRank ?? DBNull.Value; c.Parameters["dSkill"].Value = (object?)rv.DrumsAdjustedSkill ?? DBNull.Value; c.Parameters["dRank"].Value = (object?)rv.DrumsSkillRank ?? DBNull.Value; c.Parameters["vSkill"].Value = (object?)rv.VocalsAdjustedSkill ?? DBNull.Value; c.Parameters["vRank"].Value = (object?)rv.VocalsSkillRank ?? DBNull.Value; c.Parameters["pgSkill"].Value = (object?)rv.ProGuitarAdjustedSkill ?? DBNull.Value; c.Parameters["pgRank"].Value = (object?)rv.ProGuitarSkillRank ?? DBNull.Value; c.Parameters["pbSkill"].Value = (object?)rv.ProBassAdjustedSkill ?? DBNull.Value; c.Parameters["pbRank"].Value = (object?)rv.ProBassSkillRank ?? DBNull.Value; c.Parameters["now"].Value = now; c.ExecuteNonQuery(); } }
        tx.Commit();
    }

    public (List<CompositeRankingDto> Entries, int TotalCount) GetCompositeRankings(int page = 1, int pageSize = 50) { using var conn = _ds.OpenConnection(); int total; using (var c = conn.CreateCommand()) { c.CommandText = "SELECT COUNT(*) FROM composite_rankings"; total = Convert.ToInt32(c.ExecuteScalar()); } using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, instruments_played, total_songs_played, composite_rating, composite_rank, guitar_adjusted_skill, guitar_skill_rank, bass_adjusted_skill, bass_skill_rank, drums_adjusted_skill, drums_skill_rank, vocals_adjusted_skill, vocals_skill_rank, pro_guitar_adjusted_skill, pro_guitar_skill_rank, pro_bass_adjusted_skill, pro_bass_skill_rank, computed_at FROM composite_rankings ORDER BY composite_rank ASC LIMIT @limit OFFSET @offset"; cmd.Parameters.AddWithValue("limit", pageSize); cmd.Parameters.AddWithValue("offset", (page - 1) * pageSize); var list = new List<CompositeRankingDto>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(ReadCompositeRanking(r)); return (list, total); }
    public CompositeRankingDto? GetCompositeRanking(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, instruments_played, total_songs_played, composite_rating, composite_rank, guitar_adjusted_skill, guitar_skill_rank, bass_adjusted_skill, bass_skill_rank, drums_adjusted_skill, drums_skill_rank, vocals_adjusted_skill, vocals_skill_rank, pro_guitar_adjusted_skill, pro_guitar_skill_rank, pro_bass_adjusted_skill, pro_bass_skill_rank, computed_at FROM composite_rankings WHERE account_id = @accountId"; cmd.Parameters.AddWithValue("accountId", accountId); using var r = cmd.ExecuteReader(); return r.Read() ? ReadCompositeRanking(r) : null; }

    public (List<CompositeRankingDto> Above, CompositeRankingDto? Self, List<CompositeRankingDto> Below) GetCompositeRankingNeighborhood(string accountId, int radius = 5)
    {
        var self = GetCompositeRanking(accountId);
        if (self is null) return (new(), null, new());
        using var conn = _ds.OpenConnection();
        var above = new List<CompositeRankingDto>();
        using (var cmd = conn.CreateCommand()) { cmd.CommandText = "SELECT account_id, instruments_played, total_songs_played, composite_rating, composite_rank, guitar_adjusted_skill, guitar_skill_rank, bass_adjusted_skill, bass_skill_rank, drums_adjusted_skill, drums_skill_rank, vocals_adjusted_skill, vocals_skill_rank, pro_guitar_adjusted_skill, pro_guitar_skill_rank, pro_bass_adjusted_skill, pro_bass_skill_rank, computed_at FROM composite_rankings WHERE composite_rank < @selfRank ORDER BY composite_rank DESC LIMIT @radius"; cmd.Parameters.AddWithValue("selfRank", self.CompositeRank); cmd.Parameters.AddWithValue("radius", radius); using var r = cmd.ExecuteReader(); while (r.Read()) above.Add(ReadCompositeRanking(r)); }
        above.Reverse();
        var below = new List<CompositeRankingDto>();
        using (var cmd = conn.CreateCommand()) { cmd.CommandText = "SELECT account_id, instruments_played, total_songs_played, composite_rating, composite_rank, guitar_adjusted_skill, guitar_skill_rank, bass_adjusted_skill, bass_skill_rank, drums_adjusted_skill, drums_skill_rank, vocals_adjusted_skill, vocals_skill_rank, pro_guitar_adjusted_skill, pro_guitar_skill_rank, pro_bass_adjusted_skill, pro_bass_skill_rank, computed_at FROM composite_rankings WHERE composite_rank > @selfRank ORDER BY composite_rank ASC LIMIT @radius"; cmd.Parameters.AddWithValue("selfRank", self.CompositeRank); cmd.Parameters.AddWithValue("radius", radius); using var r = cmd.ExecuteReader(); while (r.Read()) below.Add(ReadCompositeRanking(r)); }
        return (above, self, below);
    }

    public void SnapshotCompositeRankHistory(int topN, IReadOnlySet<string>? additionalAccountIds = null, int retentionDays = 365)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "INSERT INTO composite_rank_history (account_id, snapshot_date, composite_rank, composite_rating, instruments_played, total_songs_played) SELECT account_id, @today, composite_rank, composite_rating, instruments_played, total_songs_played FROM composite_rankings WHERE composite_rank <= @topN ON CONFLICT (account_id, snapshot_date) DO UPDATE SET composite_rank = EXCLUDED.composite_rank, composite_rating = EXCLUDED.composite_rating, instruments_played = EXCLUDED.instruments_played, total_songs_played = EXCLUDED.total_songs_played"; c.Parameters.AddWithValue("today", today); c.Parameters.AddWithValue("topN", topN); c.ExecuteNonQuery(); }
        if (additionalAccountIds is { Count: > 0 }) { using var c = conn.CreateCommand(); c.Transaction = tx; c.CommandText = "INSERT INTO composite_rank_history (account_id, snapshot_date, composite_rank, composite_rating, instruments_played, total_songs_played) SELECT account_id, @today, composite_rank, composite_rating, instruments_played, total_songs_played FROM composite_rankings WHERE account_id = @aid ON CONFLICT (account_id, snapshot_date) DO UPDATE SET composite_rank = EXCLUDED.composite_rank, composite_rating = EXCLUDED.composite_rating, instruments_played = EXCLUDED.instruments_played, total_songs_played = EXCLUDED.total_songs_played"; c.Parameters.Add("today", NpgsqlTypes.NpgsqlDbType.Date); c.Parameters.Add("aid", NpgsqlTypes.NpgsqlDbType.Text); c.Prepare(); foreach (var aid in additionalAccountIds) { c.Parameters["today"].Value = today; c.Parameters["aid"].Value = aid; c.ExecuteNonQuery(); } }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM composite_rank_history WHERE snapshot_date < @cutoff"; c.Parameters.AddWithValue("cutoff", today.AddDays(-retentionDays)); c.ExecuteNonQuery(); }
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

    // ── Maintenance ──────────────────────────────────────────────────
    public void Checkpoint() { }

    // ── Private helpers ──────────────────────────────────────────────

    private void SimpleUpdate(string sql, string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = sql; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    private static BackfillStatusInfo ReadBackfillStatus(NpgsqlDataReader r) => new() { AccountId = r.GetString(0), Status = r.GetString(1), SongsChecked = r.GetInt32(2), EntriesFound = r.GetInt32(3), TotalSongsToCheck = r.GetInt32(4), StartedAt = r.IsDBNull(5) ? null : r.GetDateTime(5).ToString("o"), CompletedAt = r.IsDBNull(6) ? null : r.GetDateTime(6).ToString("o"), LastResumedAt = r.IsDBNull(7) ? null : r.GetDateTime(7).ToString("o"), ErrorMessage = r.IsDBNull(8) ? null : r.GetString(8) };
    private static HistoryReconStatusInfo ReadHistoryReconStatus(NpgsqlDataReader r) => new() { AccountId = r.GetString(0), Status = r.GetString(1), SongsProcessed = r.GetInt32(2), TotalSongsToProcess = r.GetInt32(3), SeasonsQueried = r.GetInt32(4), HistoryEntriesFound = r.GetInt32(5), StartedAt = r.IsDBNull(6) ? null : r.GetDateTime(6).ToString("o"), CompletedAt = r.IsDBNull(7) ? null : r.GetDateTime(7).ToString("o"), ErrorMessage = r.IsDBNull(8) ? null : r.GetString(8) };
    private static CompositeRankingDto ReadCompositeRanking(NpgsqlDataReader r) => new() { AccountId = r.GetString(0), InstrumentsPlayed = r.GetInt32(1), TotalSongsPlayed = r.GetInt32(2), CompositeRating = r.GetDouble(3), CompositeRank = r.GetInt32(4), GuitarAdjustedSkill = r.IsDBNull(5) ? null : r.GetDouble(5), GuitarSkillRank = r.IsDBNull(6) ? null : r.GetInt32(6), BassAdjustedSkill = r.IsDBNull(7) ? null : r.GetDouble(7), BassSkillRank = r.IsDBNull(8) ? null : r.GetInt32(8), DrumsAdjustedSkill = r.IsDBNull(9) ? null : r.GetDouble(9), DrumsSkillRank = r.IsDBNull(10) ? null : r.GetInt32(10), VocalsAdjustedSkill = r.IsDBNull(11) ? null : r.GetDouble(11), VocalsSkillRank = r.IsDBNull(12) ? null : r.GetInt32(12), ProGuitarAdjustedSkill = r.IsDBNull(13) ? null : r.GetDouble(13), ProGuitarSkillRank = r.IsDBNull(14) ? null : r.GetInt32(14), ProBassAdjustedSkill = r.IsDBNull(15) ? null : r.GetDouble(15), ProBassSkillRank = r.IsDBNull(16) ? null : r.GetInt32(16), ComputedAt = r.GetDateTime(17).ToString("o") };
    private static ComboLeaderboardEntry ReadComboEntry(NpgsqlDataReader r) => new() { Rank = (int)r.GetInt64(0), AccountId = r.GetString(1), AdjustedRating = r.GetDouble(2), WeightedRating = r.GetDouble(3), FcRate = r.GetDouble(4), TotalScore = r.GetInt32(5), MaxScorePercent = r.GetDouble(6), SongsPlayed = r.GetInt32(7), FullComboCount = r.GetInt32(8), ComputedAt = r.GetDateTime(9).ToString("o") };
    private static (string Column, string Direction) RankByColumn(string rankBy) => rankBy switch { "weighted" => ("weighted_rating", "ASC"), "fcrate" => ("fc_rate", "DESC"), "totalscore" => ("total_score", "DESC"), "maxscore" => ("max_score_percent", "DESC"), _ => ("adjusted_rating", "ASC") };
    private static List<RivalSongSampleRow> ReadRivalSamples(NpgsqlCommand cmd) { var list = new List<RivalSongSampleRow>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(ReadRivalSample(r)); return list; }
    private static RivalSongSampleRow ReadRivalSample(NpgsqlDataReader r) => new() { UserId = r.GetString(0), RivalAccountId = r.GetString(1), Instrument = r.GetString(2), SongId = r.GetString(3), UserRank = r.GetInt32(4), RivalRank = r.GetInt32(5), RankDelta = r.GetInt32(6), UserScore = r.IsDBNull(7) ? null : r.GetInt32(7), RivalScore = r.IsDBNull(8) ? null : r.GetInt32(8) };

    /// <summary>Parse an ISO 8601 string to UTC DateTime (required by Npgsql for TIMESTAMPTZ).</summary>
    private static DateTime ParseUtc(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    public void Dispose() { }
}
