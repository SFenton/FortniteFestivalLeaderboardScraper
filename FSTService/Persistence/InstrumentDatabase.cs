using Microsoft.Data.Sqlite;
using FSTService.Scraping;

namespace FSTService.Persistence;

/// <summary>
/// Manages a single per-instrument SQLite database (e.g. <c>fst-Solo_Guitar.db</c>).
/// Each file has an identical schema containing LeaderboardEntries — the latest
/// state only, UPSERTed on each scrape pass.
///
/// Keeps a long-lived connection open in WAL mode for the duration of the
/// application lifetime, avoiding per-call connection setup overhead.
/// </summary>
public sealed class InstrumentDatabase : IDisposable
{
    private readonly string _instrument;
    private readonly string _connectionString;
    private readonly ILogger<InstrumentDatabase> _log;
    private bool _initialized;

    /// <summary>Long-lived connection used for all write operations.</summary>
    private SqliteConnection? _persistentConn;
    private readonly object _connLock = new();
    private readonly object _writeLock = new();

    public string Instrument => _instrument;

    public InstrumentDatabase(string instrument, string dbPath, ILogger<InstrumentDatabase> log)
    {
        _instrument = instrument;
        _log = log;

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    /// <summary>
    /// Create the schema if it doesn't already exist. Called once at startup.
    /// </summary>
    public void EnsureSchema()
    {
        if (_initialized) return;

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS LeaderboardEntries (
                SongId        TEXT    NOT NULL,
                AccountId     TEXT    NOT NULL,
                Score         INTEGER NOT NULL,
                Accuracy      INTEGER,
                IsFullCombo   INTEGER,
                Stars         INTEGER,
                Season        INTEGER,
                Percentile    REAL,
                EndTime       TEXT,
                FirstSeenAt   TEXT    NOT NULL,
                LastUpdatedAt TEXT    NOT NULL,
                PRIMARY KEY (SongId, AccountId)
            );

            CREATE INDEX IF NOT EXISTS IX_Song      ON LeaderboardEntries (SongId, Score DESC);
            CREATE INDEX IF NOT EXISTS IX_Account   ON LeaderboardEntries (AccountId);
            """;
        cmd.ExecuteNonQuery();

        // ── Migration: drop old IX_Song index (references Rank) before dropping columns ──
        using var dropOldIdx = conn.CreateCommand();
        dropOldIdx.CommandText = "DROP INDEX IF EXISTS IX_Song;";
        dropOldIdx.ExecuteNonQuery();

        // ── Migration: drop deprecated columns from existing DBs ──
        MigrateDropColumn(conn, "PointsEarned");

        // ── Migration: add EndTime column to existing DBs ──
        MigrateAddColumn(conn, "EndTime", "TEXT");

        // ── Migration: re-add Rank column (was dropped, now needed for V2 API enrichment) ──
        MigrateAddColumn(conn, "Rank", "INTEGER DEFAULT 0");

        // ── Recreate IX_Song with Score DESC ──
        using var createNewIdx = conn.CreateCommand();
        createNewIdx.CommandText = "CREATE INDEX IF NOT EXISTS IX_Song ON LeaderboardEntries (SongId, Score DESC);";
        createNewIdx.ExecuteNonQuery();

        // ── Composite index for player + song lookups ──
        using var compIdx = conn.CreateCommand();
        compIdx.CommandText = "CREATE INDEX IF NOT EXISTS IX_Account_Song ON LeaderboardEntries (AccountId, SongId);";
        compIdx.ExecuteNonQuery();

        _initialized = true;

        _log.LogDebug("Schema ensured for instrument DB: {Instrument}", _instrument);
    }

    /// <summary>
    /// UPSERT all entries from a single <see cref="GlobalLeaderboardResult"/>
    /// (one song + this instrument) inside a single transaction.
    /// Returns the number of rows affected.
    /// </summary>
    public int UpsertEntries(string songId, IReadOnlyList<LeaderboardEntry> entries)
    {
        if (entries.Count == 0) return 0;

        lock (_writeLock)
        {
        var now = DateTime.UtcNow.ToString("o");
        int affected = 0;

        var conn = GetPersistentConnection();
        using var tx = conn.BeginTransaction();

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO LeaderboardEntries
                (SongId, AccountId, Score, Accuracy, IsFullCombo, Stars, Season, Percentile, Rank, EndTime, FirstSeenAt, LastUpdatedAt)
            VALUES
                (@songId, @accountId, @score, @accuracy, @fc, @stars, @season, @pct, @rank, @endTime, @now, @now)
            ON CONFLICT(SongId, AccountId) DO UPDATE SET
                Score         = excluded.Score,
                Accuracy      = excluded.Accuracy,
                IsFullCombo   = excluded.IsFullCombo,
                Stars         = excluded.Stars,
                Season        = excluded.Season,
                Percentile    = CASE
                                  WHEN excluded.Score != LeaderboardEntries.Score THEN excluded.Percentile
                                  WHEN excluded.Percentile > 0 AND LeaderboardEntries.Percentile <= 0 THEN excluded.Percentile
                                  ELSE LeaderboardEntries.Percentile
                                END,
                Rank          = CASE
                                  WHEN excluded.Rank > 0 THEN excluded.Rank
                                  ELSE LeaderboardEntries.Rank
                                END,
                EndTime       = excluded.EndTime,
                LastUpdatedAt = excluded.LastUpdatedAt
            WHERE Score != excluded.Score
               OR (excluded.Rank > 0 AND (LeaderboardEntries.Rank IS NULL OR LeaderboardEntries.Rank = 0))
               OR (excluded.Percentile > 0 AND LeaderboardEntries.Percentile <= 0);
            """;

        var pSongId    = cmd.Parameters.Add("@songId", SqliteType.Text);
        var pAccountId = cmd.Parameters.Add("@accountId", SqliteType.Text);
        var pScore     = cmd.Parameters.Add("@score", SqliteType.Integer);
        var pAccuracy  = cmd.Parameters.Add("@accuracy", SqliteType.Integer);
        var pFc        = cmd.Parameters.Add("@fc", SqliteType.Integer);
        var pStars     = cmd.Parameters.Add("@stars", SqliteType.Integer);
        var pSeason    = cmd.Parameters.Add("@season", SqliteType.Integer);
        var pPct       = cmd.Parameters.Add("@pct", SqliteType.Real);
        var pRank      = cmd.Parameters.Add("@rank", SqliteType.Integer);
        var pEndTime   = cmd.Parameters.Add("@endTime", SqliteType.Text);
        var pNow       = cmd.Parameters.Add("@now", SqliteType.Text);

        cmd.Prepare();

        foreach (var entry in entries)
        {
            pSongId.Value    = songId;
            pAccountId.Value = entry.AccountId;
            pScore.Value     = entry.Score;
            pAccuracy.Value  = entry.Accuracy;
            pFc.Value        = entry.IsFullCombo ? 1 : 0;
            pStars.Value     = entry.Stars;
            pSeason.Value    = entry.Season;
            pPct.Value       = entry.Percentile;
            pRank.Value      = entry.Rank;
            pEndTime.Value   = (object?)entry.EndTime ?? DBNull.Value;
            pNow.Value       = now;

            affected += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return affected;
        } // lock
    }

    /// <summary>
    /// Look up a specific account's entry for a given song. Used for
    /// change detection on registered users.
    /// </summary>
    public LeaderboardEntry? GetEntry(string songId, string accountId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Score, Accuracy, IsFullCombo, Stars, Season, Percentile, EndTime, Rank
            FROM LeaderboardEntries
            WHERE SongId = @songId AND AccountId = @accountId;
            """;
        cmd.Parameters.AddWithValue("@songId", songId);
        cmd.Parameters.AddWithValue("@accountId", accountId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new LeaderboardEntry
        {
            AccountId    = accountId,
            Score        = reader.GetInt32(0),
            Accuracy     = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            IsFullCombo  = !reader.IsDBNull(2) && reader.GetInt32(2) == 1,
            Stars        = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            Season       = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            Percentile   = reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
            EndTime      = reader.IsDBNull(6) ? null : reader.GetString(6),
            Rank         = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
        };
    }

    /// <summary>
    /// Batch-load entries for multiple accounts on a single song.
    /// Uses the persistent connection to avoid per-call connection overhead.
    /// Returns a dictionary keyed by AccountId (case-insensitive).
    /// </summary>
    public Dictionary<string, LeaderboardEntry> GetEntriesForAccounts(
        string songId, IReadOnlyCollection<string> accountIds)
    {
        var result = new Dictionary<string, LeaderboardEntry>(StringComparer.OrdinalIgnoreCase);
        if (accountIds.Count == 0) return result;

        var conn = GetPersistentConnection();
        using var cmd = conn.CreateCommand();

        // Build parameterized IN clause: @a0, @a1, @a2, ...
        var placeholders = new string[accountIds.Count];
        int i = 0;
        foreach (var accountId in accountIds)
        {
            var paramName = $"@a{i}";
            placeholders[i] = paramName;
            cmd.Parameters.AddWithValue(paramName, accountId);
            i++;
        }

        cmd.CommandText = $"""
            SELECT AccountId, Score, Accuracy, IsFullCombo, Stars, Season, Percentile, EndTime, Rank
            FROM LeaderboardEntries
            WHERE SongId = @songId AND AccountId IN ({string.Join(", ", placeholders)});
            """;
        cmd.Parameters.AddWithValue("@songId", songId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var entry = new LeaderboardEntry
            {
                AccountId   = reader.GetString(0),
                Score       = reader.GetInt32(1),
                Accuracy    = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                IsFullCombo = !reader.IsDBNull(3) && reader.GetInt32(3) == 1,
                Stars       = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                Season      = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                Percentile  = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                EndTime     = reader.IsDBNull(7) ? null : reader.GetString(7),
                Rank        = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
            };
            result[entry.AccountId] = entry;
        }

        return result;
    }

    /// <summary>
    /// Get the minimum Season value across all entries for a given song, or null if no entries exist.
    /// </summary>
    public int? GetMinSeason(string songId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(Season) FROM LeaderboardEntries WHERE SongId = @songId AND Season > 0;";
        cmd.Parameters.AddWithValue("@songId", songId);
        var result = cmd.ExecuteScalar();
        return result is long val ? (int)val : null;
    }

    /// <summary>Get the maximum season number stored in this instrument DB, or null if empty.</summary>
    public int? GetMaxSeason()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(Season) FROM LeaderboardEntries WHERE Season > 0;";
        var result = cmd.ExecuteScalar();
        return result is long val ? (int)val : null;
    }

    /// <summary>Total row count across all songs (for status/reporting).</summary>
    public long GetTotalEntryCount()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM LeaderboardEntries;";
        return (long)(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>Get any single SongId from the database (for probing). Returns null if empty.</summary>
    public string? GetAnySongId()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SongId FROM LeaderboardEntries LIMIT 1;";
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Get the leaderboard for a song, ordered by rank.
    /// Optionally limit to top N entries.
    /// </summary>
    public List<LeaderboardEntryDto> GetLeaderboard(string songId, int? top = null, int offset = 0)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        if (top.HasValue)
        {
            cmd.CommandText = @"
                SELECT AccountId, Score, Accuracy, IsFullCombo, Stars, Season, Percentile, EndTime,
                       ROW_NUMBER() OVER (ORDER BY Score DESC, COALESCE(EndTime, FirstSeenAt) ASC) AS Rank
                FROM LeaderboardEntries WHERE SongId = @songId
                ORDER BY Score DESC, COALESCE(EndTime, FirstSeenAt) ASC
                LIMIT @top OFFSET @offset;";
            cmd.Parameters.AddWithValue("@top", top.Value);
            cmd.Parameters.AddWithValue("@offset", offset);
        }
        else
        {
            cmd.CommandText = @"
                SELECT AccountId, Score, Accuracy, IsFullCombo, Stars, Season, Percentile, EndTime,
                       ROW_NUMBER() OVER (ORDER BY Score DESC, COALESCE(EndTime, FirstSeenAt) ASC) AS Rank
                FROM LeaderboardEntries WHERE SongId = @songId
                ORDER BY Score DESC, COALESCE(EndTime, FirstSeenAt) ASC;";
        }
        cmd.Parameters.AddWithValue("@songId", songId);

        var entries = new List<LeaderboardEntryDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadEntryDto(reader));
        }
        return entries;
    }

    /// <summary>
    /// Get the total number of leaderboard entries for a song.
    /// </summary>
    public int GetLeaderboardCount(string songId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM LeaderboardEntries WHERE SongId = @songId;";
        cmd.Parameters.AddWithValue("@songId", songId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Get the total entry count per song on this instrument.
    /// </summary>
    public Dictionary<string, int> GetAllSongCounts()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SongId, COUNT(*) FROM LeaderboardEntries GROUP BY SongId;";

        var result = new Dictionary<string, int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetInt32(1);
        }
        return result;
    }

    /// <summary>
    /// Get entries within ±<paramref name="rankRadius"/> of a given rank on a song.
    /// Uses the pre-computed Rank column. Returns (AccountId, Rank, Score) tuples.
    /// </summary>
    public List<(string AccountId, int Rank, int Score)> GetNeighborhood(
        string songId, int centerRank, int rankRadius, string excludeAccountId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT AccountId, Rank, Score
            FROM LeaderboardEntries
            WHERE SongId = @songId
              AND Rank BETWEEN @lo AND @hi
              AND AccountId != @exclude;
            """;
        cmd.Parameters.AddWithValue("@songId", songId);
        cmd.Parameters.AddWithValue("@lo", Math.Max(1, centerRank - rankRadius));
        cmd.Parameters.AddWithValue("@hi", centerRank + rankRadius);
        cmd.Parameters.AddWithValue("@exclude", excludeAccountId);

        var list = new List<(string, int, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
        }
        return list;
    }

    /// <summary>
    /// Get all entries for a player across all songs on this instrument.
    /// </summary>
    public List<PlayerScoreDto> GetPlayerScores(string accountId, string? songId = null)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var where = "AccountId = @accountId";
        if (songId is not null)
            where += " AND SongId = @songId";
        cmd.CommandText = $"SELECT SongId, Score, Accuracy, IsFullCombo, Stars, Season, Percentile, EndTime, Rank FROM LeaderboardEntries WHERE {where} ORDER BY SongId;";
        cmd.Parameters.AddWithValue("@accountId", accountId);
        if (songId is not null)
            cmd.Parameters.AddWithValue("@songId", songId);

        var scores = new List<PlayerScoreDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            scores.Add(new PlayerScoreDto
            {
                SongId       = reader.GetString(0),
                Instrument   = _instrument,
                Score        = reader.GetInt32(1),
                Accuracy     = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                IsFullCombo  = !reader.IsDBNull(3) && reader.GetInt32(3) == 1,
                Stars        = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                Season       = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                Percentile   = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                EndTime      = reader.IsDBNull(7) ? null : reader.GetString(7),
                Rank         = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
            });
        }
        return scores;
    }

    /// <summary>
    /// For each song where the given account has a score, compute
    /// (rank, totalEntries) from the leaderboard. Rank is 1-based.
    /// Always uses the correlated subquery for correctness (not the stored Rank column).
    /// </summary>
    public Dictionary<string, (int Rank, int Total)> GetPlayerRankings(string accountId, string? songId = null)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var where = "le.AccountId = @accountId";
        if (songId is not null)
            where += " AND le.SongId = @songId";
        cmd.CommandText = $@"
            SELECT
                le.SongId,
                (SELECT COUNT(*) + 1 FROM LeaderboardEntries x
                 WHERE x.SongId = le.SongId
                   AND (x.Score > le.Score
                        OR (x.Score = le.Score AND COALESCE(x.EndTime, x.FirstSeenAt) < COALESCE(le.EndTime, le.FirstSeenAt)))) AS Rank,
                (SELECT COUNT(*) FROM LeaderboardEntries x
                 WHERE x.SongId = le.SongId) AS TotalEntries
            FROM LeaderboardEntries le
            WHERE {where};";
        cmd.Parameters.AddWithValue("@accountId", accountId);
        if (songId is not null)
            cmd.Parameters.AddWithValue("@songId", songId);

        var result = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var sid = reader.GetString(0);
            var rank = reader.GetInt32(1);
            var total = reader.GetInt32(2);
            result[sid] = (rank, total);
        }
        return result;
    }

    /// <summary>
    /// For each song where the given account has a score, return
    /// the pre-computed stored Rank and TotalEntries. Much faster than
    /// <see cref="GetPlayerRankings"/> but requires <see cref="RecomputeAllRanks"/>
    /// to have been called after the most recent scrape pass.
    /// Falls back to 0 for rank if not yet computed.
    /// </summary>
    public Dictionary<string, (int Rank, int Total)> GetPlayerStoredRankings(string accountId, string? songId = null)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var where = "le.AccountId = @accountId";
        if (songId is not null)
            where += " AND le.SongId = @songId";
        cmd.CommandText = $@"
            SELECT
                le.SongId,
                COALESCE(le.Rank, 0) AS Rank,
                (SELECT COUNT(*) FROM LeaderboardEntries x
                 WHERE x.SongId = le.SongId) AS TotalEntries
            FROM LeaderboardEntries le
            WHERE {where};";
        cmd.Parameters.AddWithValue("@accountId", accountId);
        if (songId is not null)
            cmd.Parameters.AddWithValue("@songId", songId);

        var result = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var sid = reader.GetString(0);
            var rank = reader.GetInt32(1);
            var total = reader.GetInt32(2);
            result[sid] = (rank, total);
        }
        return result;
    }

    /// <summary>
    /// Batch-recompute the Rank column for every entry in every song.
    /// Uses ROW_NUMBER window function to assign 1-based ranks ordered by
    /// Score DESC, EndTime ASC. Should be called post-scrape.
    /// </summary>
    /// <returns>Number of rows updated.</returns>
    public int RecomputeAllRanks()
    {
        lock (_writeLock)
        {
            var conn = GetPersistentConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE LeaderboardEntries
                SET Rank = (
                    SELECT cnt FROM (
                        SELECT AccountId AS aid, SongId AS sid,
                               ROW_NUMBER() OVER (
                                   PARTITION BY SongId
                                   ORDER BY Score DESC, COALESCE(EndTime, FirstSeenAt) ASC
                               ) AS cnt
                        FROM LeaderboardEntries
                    ) ranked
                    WHERE ranked.sid = LeaderboardEntries.SongId
                      AND ranked.aid = LeaderboardEntries.AccountId
                );
            ";
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Prune entries for a song down to the top <paramref name="maxEntries"/> by score,
    /// while always preserving entries for accounts in <paramref name="preserveAccountIds"/>.
    /// Returns the number of rows deleted.
    /// </summary>
    public int PruneExcessEntries(string songId, int maxEntries, IReadOnlySet<string> preserveAccountIds)
    {
        lock (_writeLock)
        {
            var conn = GetPersistentConnection();
            using var tx = conn.BeginTransaction();

            // Build a temp table of preserved account IDs for efficient lookups
            using (var createTemp = conn.CreateCommand())
            {
                createTemp.Transaction = tx;
                createTemp.CommandText = "CREATE TEMP TABLE IF NOT EXISTS _preserve (AccountId TEXT PRIMARY KEY);";
                createTemp.ExecuteNonQuery();
            }
            using (var clearTemp = conn.CreateCommand())
            {
                clearTemp.Transaction = tx;
                clearTemp.CommandText = "DELETE FROM _preserve;";
                clearTemp.ExecuteNonQuery();
            }
            if (preserveAccountIds.Count > 0)
            {
                using var insertTemp = conn.CreateCommand();
                insertTemp.Transaction = tx;
                insertTemp.CommandText = "INSERT OR IGNORE INTO _preserve (AccountId) VALUES (@id);";
                var pId = insertTemp.Parameters.Add("@id", SqliteType.Text);
                foreach (var id in preserveAccountIds)
                {
                    pId.Value = id;
                    insertTemp.ExecuteNonQuery();
                }
            }

            using var deleteCmd = conn.CreateCommand();
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = """
                DELETE FROM LeaderboardEntries
                WHERE SongId = @songId
                  AND AccountId NOT IN (SELECT AccountId FROM _preserve)
                  AND rowid NOT IN (
                      SELECT rowid FROM LeaderboardEntries
                      WHERE SongId = @songId
                      ORDER BY Score DESC
                      LIMIT @maxEntries
                  );
                """;
            deleteCmd.Parameters.AddWithValue("@songId", songId);
            deleteCmd.Parameters.AddWithValue("@maxEntries", maxEntries);
            var deleted = deleteCmd.ExecuteNonQuery();

            tx.Commit();
            return deleted;
        }
    }

    /// <summary>
    /// Prune all songs on this instrument down to <paramref name="maxEntriesPerSong"/> each.
    /// Returns total rows deleted across all songs.
    /// </summary>
    public int PruneAllSongs(int maxEntriesPerSong, IReadOnlySet<string> preserveAccountIds)
    {
        if (maxEntriesPerSong <= 0) return 0;

        // Get all distinct song IDs
        var songIds = new List<string>();
        using (var conn = OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT SongId FROM LeaderboardEntries;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                songIds.Add(reader.GetString(0));
        }

        int totalDeleted = 0;
        foreach (var songId in songIds)
        {
            totalDeleted += PruneExcessEntries(songId, maxEntriesPerSong, preserveAccountIds);
        }

        return totalDeleted;
    }

    /// <summary>
    /// Get the leaderboard for a song including the total count in a single query.
    /// Returns (entries, totalCount) — avoids a separate COUNT(*) round-trip.
    /// </summary>
    public (List<LeaderboardEntryDto> Entries, int TotalCount) GetLeaderboardWithCount(
        string songId, int? top = null, int offset = 0, int? maxScore = null)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        var whereClause = maxScore.HasValue
            ? "WHERE SongId = @songId AND Score <= @maxScore"
            : "WHERE SongId = @songId";

        if (top.HasValue)
        {
            cmd.CommandText = $@"
                SELECT AccountId, Score, Accuracy, IsFullCombo, Stars, Season, Percentile, EndTime,
                       ROW_NUMBER() OVER (ORDER BY Score DESC, COALESCE(EndTime, FirstSeenAt) ASC) AS Rank,
                       COUNT(*) OVER () AS TotalCount
                FROM LeaderboardEntries {whereClause}
                ORDER BY Score DESC, COALESCE(EndTime, FirstSeenAt) ASC
                LIMIT @top OFFSET @offset;";
            cmd.Parameters.AddWithValue("@top", top.Value);
            cmd.Parameters.AddWithValue("@offset", offset);
        }
        else
        {
            cmd.CommandText = $@"
                SELECT AccountId, Score, Accuracy, IsFullCombo, Stars, Season, Percentile, EndTime,
                       ROW_NUMBER() OVER (ORDER BY Score DESC, COALESCE(EndTime, FirstSeenAt) ASC) AS Rank,
                       COUNT(*) OVER () AS TotalCount
                FROM LeaderboardEntries {whereClause}
                ORDER BY Score DESC, COALESCE(EndTime, FirstSeenAt) ASC;";
        }
        cmd.Parameters.AddWithValue("@songId", songId);
        if (maxScore.HasValue)
            cmd.Parameters.AddWithValue("@maxScore", maxScore.Value);

        var entries = new List<LeaderboardEntryDto>();
        int totalCount = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadEntryDto(reader));
            if (totalCount == 0)
                totalCount = reader.GetInt32(9);
        }
        return (entries, totalCount);
    }

    private static LeaderboardEntryDto ReadEntryDto(SqliteDataReader reader)
    {
        return new LeaderboardEntryDto
        {
            AccountId    = reader.GetString(0),
            Score        = reader.GetInt32(1),
            Accuracy     = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            IsFullCombo  = !reader.IsDBNull(3) && reader.GetInt32(3) == 1,
            Stars        = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            Season       = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            Percentile   = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
            EndTime      = reader.IsDBNull(7) ? null : reader.GetString(7),
            Rank         = reader.FieldCount > 8 && !reader.IsDBNull(8) ? reader.GetInt32(8) : 0,
        };
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();

        return conn;
    }

    /// <summary>
    /// Get the long-lived persistent connection (for write operations).
    /// Creates it on first call. Thread-safe via external per-instrument writer.
    /// </summary>
    private SqliteConnection GetPersistentConnection()
    {
        if (_persistentConn is not null)
            return _persistentConn;

        lock (_connLock)
        {
            if (_persistentConn is not null)
                return _persistentConn;

            _persistentConn = OpenConnection();
            _log.LogDebug("Opened persistent connection for {Instrument}", _instrument);
        }

        return _persistentConn;
    }

    public void Dispose()
    {
        if (_persistentConn is not null)
        {
            _persistentConn.Dispose();
            _persistentConn = null;
        }
    }

    /// <summary>
    /// Drop a column from LeaderboardEntries if it still exists.
    /// Safe to call repeatedly — no-ops when the column is already gone.
    /// Requires SQLite 3.35.0+ (ALTER TABLE DROP COLUMN).
    /// </summary>
    private void MigrateDropColumn(SqliteConnection conn, string columnName)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('LeaderboardEntries') WHERE name = '{columnName}';";
        var exists = (long)(check.ExecuteScalar() ?? 0);
        if (exists == 0) return;

        using var drop = conn.CreateCommand();
        drop.CommandText = $"ALTER TABLE LeaderboardEntries DROP COLUMN {columnName};";
        drop.ExecuteNonQuery();

        _log.LogInformation("Migrated {Instrument}: dropped column {Column}", _instrument, columnName);
    }

    /// <summary>
    /// Add a column to LeaderboardEntries if it doesn't already exist.
    /// Safe to call repeatedly — no-ops when the column is already present.
    /// </summary>
    private void MigrateAddColumn(SqliteConnection conn, string columnName, string columnType)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('LeaderboardEntries') WHERE name = '{columnName}';";
        var exists = (long)(check.ExecuteScalar() ?? 0);
        if (exists > 0) return;

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE LeaderboardEntries ADD COLUMN {columnName} {columnType};";
        alter.ExecuteNonQuery();

        _log.LogInformation("Migrated {Instrument}: added column {Column} ({Type})", _instrument, columnName, columnType);
    }
}
