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

        // ── Migration: add ApiRank column (real rank from Epic API, survives RecomputeAllRanks) ──
        MigrateAddColumn(conn, "ApiRank", "INTEGER");

        // ── Migration: add Source column (scrape / backfill / neighbor) ──
        MigrateAddColumn(conn, "Source", "TEXT NOT NULL DEFAULT 'scrape'");

        // ── Index for efficient Source-based filtering ──
        using var srcIdx = conn.CreateCommand();
        srcIdx.CommandText = "CREATE INDEX IF NOT EXISTS IX_Song_Source ON LeaderboardEntries (SongId, Source);";
        srcIdx.ExecuteNonQuery();

        _initialized = true;

        // ── Rankings tables (SongStats, AccountRankings, RankHistory) ──
        EnsureRankingsTables(conn);

        _log.LogDebug("Schema ensured for instrument DB: {Instrument}", _instrument);
    }

    /// <summary>Create ranking-related tables if they don't exist.</summary>
    private void EnsureRankingsTables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS SongStats (
                SongId             TEXT    PRIMARY KEY,
                EntryCount         INTEGER NOT NULL,
                PreviousEntryCount INTEGER NOT NULL DEFAULT 0,
                LogWeight          REAL    NOT NULL,
                MaxScore           INTEGER,
                ComputedAt         TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AccountRankings (
                AccountId            TEXT    PRIMARY KEY,
                SongsPlayed          INTEGER NOT NULL,
                TotalChartedSongs    INTEGER NOT NULL,
                Coverage             REAL    NOT NULL,

                RawSkillRating       REAL    NOT NULL,
                AdjustedSkillRating  REAL    NOT NULL,
                AdjustedSkillRank    INTEGER NOT NULL UNIQUE,

                WeightedRating       REAL    NOT NULL,
                WeightedRank         INTEGER NOT NULL UNIQUE,

                FcRate               REAL    NOT NULL,
                FcRateRank           INTEGER NOT NULL UNIQUE,

                TotalScore           INTEGER NOT NULL,
                TotalScoreRank       INTEGER NOT NULL UNIQUE,

                MaxScorePercent      REAL    NOT NULL,
                MaxScorePercentRank  INTEGER NOT NULL UNIQUE,

                AvgAccuracy          REAL    NOT NULL,
                FullComboCount       INTEGER NOT NULL,
                AvgStars             REAL    NOT NULL,
                BestRank             INTEGER NOT NULL,
                AvgRank              REAL    NOT NULL,

                ComputedAt           TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_AR_AdjustedSkill ON AccountRankings (AdjustedSkillRank);
            CREATE INDEX IF NOT EXISTS IX_AR_Weighted      ON AccountRankings (WeightedRank);
            CREATE INDEX IF NOT EXISTS IX_AR_FcRate        ON AccountRankings (FcRateRank);
            CREATE INDEX IF NOT EXISTS IX_AR_TotalScore    ON AccountRankings (TotalScoreRank);
            CREATE INDEX IF NOT EXISTS IX_AR_MaxScorePct   ON AccountRankings (MaxScorePercentRank);

            CREATE TABLE IF NOT EXISTS RankHistory (
                AccountId            TEXT    NOT NULL,
                SnapshotDate         TEXT    NOT NULL,
                AdjustedSkillRank    INTEGER NOT NULL,
                WeightedRank         INTEGER NOT NULL,
                FcRateRank           INTEGER NOT NULL,
                TotalScoreRank       INTEGER NOT NULL,
                MaxScorePercentRank  INTEGER NOT NULL,
                PRIMARY KEY (AccountId, SnapshotDate)
            );
            """;
        cmd.ExecuteNonQuery();
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
                (SongId, AccountId, Score, Accuracy, IsFullCombo, Stars, Season, Percentile, Rank, EndTime, ApiRank, Source, FirstSeenAt, LastUpdatedAt)
            VALUES
                (@songId, @accountId, @score, @accuracy, @fc, @stars, @season, @pct, @rank, @endTime, @apiRank, @source, @now, @now)
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
                ApiRank       = CASE
                                  WHEN excluded.ApiRank > 0 THEN excluded.ApiRank
                                  ELSE LeaderboardEntries.ApiRank
                                END,
                Source        = CASE
                                  WHEN LeaderboardEntries.Source = 'scrape' THEN 'scrape'
                                  WHEN excluded.Source = 'scrape' THEN 'scrape'
                                  WHEN LeaderboardEntries.Source = 'backfill' THEN 'backfill'
                                  WHEN excluded.Source = 'backfill' THEN 'backfill'
                                  ELSE excluded.Source
                                END,
                EndTime       = excluded.EndTime,
                LastUpdatedAt = excluded.LastUpdatedAt
            WHERE Score != excluded.Score
               OR (excluded.Rank > 0 AND (LeaderboardEntries.Rank IS NULL OR LeaderboardEntries.Rank = 0))
               OR (excluded.ApiRank > 0 AND (LeaderboardEntries.ApiRank IS NULL OR LeaderboardEntries.ApiRank = 0 OR LeaderboardEntries.ApiRank != excluded.ApiRank))
               OR (excluded.Percentile > 0 AND LeaderboardEntries.Percentile <= 0)
               OR (excluded.Source != LeaderboardEntries.Source AND (excluded.Source = 'scrape' OR (excluded.Source = 'backfill' AND LeaderboardEntries.Source = 'neighbor')));
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
        var pApiRank   = cmd.Parameters.Add("@apiRank", SqliteType.Integer);
        var pSource    = cmd.Parameters.Add("@source", SqliteType.Text);
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
            pApiRank.Value   = entry.ApiRank > 0 ? entry.ApiRank : DBNull.Value;
            pSource.Value    = entry.Source ?? "scrape";
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
            SELECT Score, Accuracy, IsFullCombo, Stars, Season, Percentile, EndTime, Rank, ApiRank, Source
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
            ApiRank      = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
            Source       = reader.IsDBNull(9) ? "scrape" : reader.GetString(9),
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
            SELECT AccountId, Score, Accuracy, IsFullCombo, Stars, Season, Percentile, EndTime, Rank, Source
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
                Source      = reader.IsDBNull(9) ? "scrape" : reader.GetString(9),
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
    /// Get the set of song IDs that a player has scores on for this instrument.
    /// Lightweight alternative to <see cref="GetPlayerScores"/> when only song IDs are needed.
    /// </summary>
    public HashSet<string> GetSongIdsForAccount(string accountId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SongId FROM LeaderboardEntries WHERE AccountId = @accountId;";
        cmd.Parameters.AddWithValue("@accountId", accountId);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    /// <summary>
    /// Get a player's scores for a specific subset of songs on this instrument.
    /// </summary>
    public List<PlayerScoreDto> GetPlayerScoresForSongs(string accountId, IReadOnlyCollection<string> songIds)
    {
        if (songIds.Count == 0)
            return new List<PlayerScoreDto>();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        // Build parameterized IN clause
        var placeholders = new string[songIds.Count];
        int idx = 0;
        foreach (var sid in songIds)
        {
            var pName = $"@s{idx}";
            placeholders[idx] = pName;
            cmd.Parameters.AddWithValue(pName, sid);
            idx++;
        }

        cmd.CommandText = $"SELECT SongId, Score, Accuracy, IsFullCombo, Stars, Season, Percentile, EndTime, Rank, ApiRank FROM LeaderboardEntries WHERE AccountId = @accountId AND SongId IN ({string.Join(",", placeholders)}) ORDER BY SongId;";
        cmd.Parameters.AddWithValue("@accountId", accountId);

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
                ApiRank      = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
            });
        }
        return scores;
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
        cmd.CommandText = $"SELECT SongId, Score, Accuracy, IsFullCombo, Stars, Season, Percentile, EndTime, Rank, ApiRank FROM LeaderboardEntries WHERE {where} ORDER BY SongId;";
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
                ApiRank      = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
            });
        }
        return scores;
    }

    /// <summary>
    /// For each song where the given account has a score, compute
    /// the rank from the leaderboard using a window function. Rank is 1-based.
    /// TotalEntries is no longer returned here — callers should use
    /// <see cref="MetaDatabase.GetAllLeaderboardPopulation"/> instead.
    /// </summary>
    public Dictionary<string, int> GetPlayerRankings(string accountId, string? songId = null)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var songFilter = songId is not null ? "AND le.SongId = @songId" : "";
        cmd.CommandText = $@"
            WITH player_songs AS (
                SELECT SongId FROM LeaderboardEntries
                WHERE AccountId = @accountId {songFilter}
            ),
            ranked AS (
                SELECT le.AccountId, le.SongId,
                       ROW_NUMBER() OVER (
                           PARTITION BY le.SongId
                           ORDER BY le.Score DESC, COALESCE(le.EndTime, le.FirstSeenAt) ASC
                       ) AS Rank
                FROM LeaderboardEntries le
                WHERE le.SongId IN (SELECT SongId FROM player_songs)
            )
            SELECT SongId, Rank FROM ranked
            WHERE AccountId = @accountId;";
        cmd.Parameters.AddWithValue("@accountId", accountId);
        if (songId is not null)
            cmd.Parameters.AddWithValue("@songId", songId);

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetInt32(1);
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
    /// Batch-recompute the Rank column for scraped entries (Source = 'scrape') in every song.
    /// Uses ROW_NUMBER window function to assign 1-based ranks ordered by
    /// Score DESC, EndTime ASC. Backfill and neighbor entries keep their ApiRank.
    /// Should be called post-scrape.
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
                        WHERE Source = 'scrape'
                    ) ranked
                    WHERE ranked.sid = LeaderboardEntries.SongId
                      AND ranked.aid = LeaderboardEntries.AccountId
                )
                WHERE Source = 'scrape';
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
                       COUNT(*) OVER () AS TotalCount,
                       ApiRank, Source
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
                       COUNT(*) OVER () AS TotalCount,
                       ApiRank, Source
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
            ApiRank      = reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetInt32(10) : 0,
            Source       = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : "scrape",
        };
    }

    // ─── Rankings computation ───────────────────────────────────

    /// <summary>
    /// Recompute <c>SongStats</c> (per-song entry counts and log weights).
    /// Uses monotonic MAX of (local COUNT, previous, real population) to prevent rank inflation.
    /// </summary>
    /// <param name="maxScoresByInstrument">CHOpt max scores keyed by SongId.</param>
    /// <param name="realPopulation">Real population data from LeaderboardPopulation, keyed by SongId.</param>
    public int ComputeSongStats(Dictionary<string, int?>? maxScoresByInstrument = null,
                                Dictionary<string, long>? realPopulation = null)
    {
        lock (_writeLock)
        {
            var now = DateTime.UtcNow.ToString("o");
            var conn = GetPersistentConnection();

            // Load current EntryCount values to enforce monotonicity
            var previous = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (var readCmd = conn.CreateCommand())
            {
                readCmd.CommandText = "SELECT SongId, EntryCount FROM SongStats;";
                using var reader = readCmd.ExecuteReader();
                while (reader.Read())
                    previous[reader.GetString(0)] = reader.GetInt32(1);
            }

            // Get fresh counts from LeaderboardEntries
            var freshCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = "SELECT SongId, COUNT(*) FROM LeaderboardEntries GROUP BY SongId;";
                using var reader = countCmd.ExecuteReader();
                while (reader.Read())
                    freshCounts[reader.GetString(0)] = reader.GetInt32(1);
            }

            // UPSERT with monotonic MAX
            using var tx = conn.BeginTransaction();
            using var upsert = conn.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO SongStats (SongId, EntryCount, PreviousEntryCount, LogWeight, MaxScore, ComputedAt)
                VALUES (@songId, @entryCount, @prevCount, @logWeight, @maxScore, @now)
                ON CONFLICT(SongId) DO UPDATE SET
                    PreviousEntryCount = SongStats.EntryCount,
                    EntryCount         = excluded.EntryCount,
                    LogWeight          = excluded.LogWeight,
                    MaxScore           = excluded.MaxScore,
                    ComputedAt         = excluded.ComputedAt;
                """;
            var pSongId = upsert.Parameters.Add("@songId", SqliteType.Text);
            var pEntryCount = upsert.Parameters.Add("@entryCount", SqliteType.Integer);
            var pPrevCount = upsert.Parameters.Add("@prevCount", SqliteType.Integer);
            var pLogWeight = upsert.Parameters.Add("@logWeight", SqliteType.Real);
            var pMaxScore = upsert.Parameters.Add("@maxScore", SqliteType.Integer);
            var pNow = upsert.Parameters.Add("@now", SqliteType.Text);
            upsert.Prepare();

            int rows = 0;
            foreach (var (songId, freshCount) in freshCounts)
            {
                var prevCount = previous.GetValueOrDefault(songId, 0);
                long realPop = 0;
                realPopulation?.TryGetValue(songId, out realPop);
                // Monotonic MAX of (fresh local count, previous count, real Epic population)
                var entryCount = (int)Math.Max(Math.Max(freshCount, prevCount), realPop > 0 ? realPop : 0);
                var logWeight = entryCount > 0 ? Math.Log2(entryCount) : 0.0;

                int? maxScore = null;
                maxScoresByInstrument?.TryGetValue(songId, out maxScore);

                pSongId.Value = songId;
                pEntryCount.Value = entryCount;
                pPrevCount.Value = prevCount;
                pLogWeight.Value = logWeight;
                pMaxScore.Value = maxScore.HasValue ? (object)maxScore.Value : DBNull.Value;
                pNow.Value = now;
                rows += upsert.ExecuteNonQuery();
            }

            tx.Commit();
            _log.LogDebug("Computed SongStats for {Instrument}: {Rows} songs.", _instrument, rows);
            return rows;
        }
    }

    /// <summary>
    /// Recompute <c>AccountRankings</c> for all accounts on this instrument.
    /// Applies CHOpt filter (exclude scores &gt; maxScore × 1.05) and Bayesian adjustment.
    /// </summary>
    /// <param name="totalChartedSongs">Total songs charted for this instrument (from song catalog).</param>
    /// <param name="credibilityThreshold">Bayesian m parameter (default 50).</param>
    /// <param name="populationMedian">Bayesian C parameter (default 0.5).</param>
    /// <returns>Number of accounts ranked.</returns>
    public int ComputeAccountRankings(int totalChartedSongs, int credibilityThreshold = 50, double populationMedian = 0.5)
    {
        lock (_writeLock)
        {
            var now = DateTime.UtcNow.ToString("o");
            var conn = GetPersistentConnection();
            using var tx = conn.BeginTransaction();

            // Clear existing rankings
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM AccountRankings;";
                del.ExecuteNonQuery();
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                WITH ValidEntries AS (
                    -- Filter out cheated scores (> 105% of CHOpt max)
                    -- Use ApiRank (real Epic rank) when available, falling back to local Rank
                    SELECT le.SongId, le.AccountId, le.Score, le.Accuracy, le.IsFullCombo, le.Stars,
                           COALESCE(NULLIF(le.ApiRank, 0), le.Rank) AS EffectiveRank,
                           ss.EntryCount, ss.LogWeight, ss.MaxScore
                    FROM LeaderboardEntries le
                    JOIN SongStats ss ON ss.SongId = le.SongId
                    WHERE le.Score <= COALESCE(CAST(ss.MaxScore * 1.05 AS INTEGER), le.Score + 1)
                      AND ss.EntryCount > 0
                      AND COALESCE(NULLIF(le.ApiRank, 0), le.Rank) > 0
                ),
                Aggregated AS (
                    SELECT
                        v.AccountId,
                        COUNT(*)                                                        AS SongsPlayed,
                        @totalCharted                                                   AS TotalChartedSongs,
                        CAST(COUNT(*) AS REAL) / @totalCharted                          AS Coverage,
                        AVG(CAST(v.EffectiveRank AS REAL) / v.EntryCount)               AS RawSkillRating,
                        SUM((CAST(v.EffectiveRank AS REAL) / v.EntryCount) * v.LogWeight) / NULLIF(SUM(v.LogWeight), 0)
                                                                                        AS WeightedRating,
                        CAST(SUM(v.IsFullCombo) AS REAL) / COUNT(*)                     AS FcRate,
                        SUM(v.Score)                                                    AS TotalScore,
                        AVG(CASE WHEN v.MaxScore IS NOT NULL AND v.MaxScore > 0
                                 THEN MIN(CAST(v.Score AS REAL) / v.MaxScore, 1.05)
                                 ELSE NULL END)                                         AS MaxScorePercent,
                        AVG(v.Accuracy)                                                 AS AvgAccuracy,
                        SUM(v.IsFullCombo)                                              AS FullComboCount,
                        AVG(v.Stars)                                                    AS AvgStars,
                        MIN(v.EffectiveRank)                                            AS BestRank,
                        AVG(CAST(v.EffectiveRank AS REAL))                              AS AvgRank
                    FROM ValidEntries v
                    GROUP BY v.AccountId
                ),
                WithBayesian AS (
                    -- Apply Bayesian credibility adjustment to ranked metrics.
                    -- This pulls accounts with few songs toward the population median (0.5),
                    -- preventing 1-song players from dominating the rankings.
                    -- Formula: (songs * raw + m * C) / (songs + m)  where m=50, C=0.5
                    SELECT *,
                        (SongsPlayed * RawSkillRating + @m * @C) / (SongsPlayed + @m)   AS AdjustedSkillRating,
                        (SongsPlayed * COALESCE(WeightedRating, 1.0) + @m * @C) / (SongsPlayed + @m) AS AdjustedWeightedRating,
                        (SongsPlayed * FcRate + @m * @C) / (SongsPlayed + @m)           AS AdjustedFcRate,
                        (SongsPlayed * COALESCE(MaxScorePercent, 0.5) + @m * @C) / (SongsPlayed + @m) AS AdjustedMaxScorePercent
                    FROM Aggregated
                ),
                Ranked AS (
                    SELECT *,
                        ROW_NUMBER() OVER (ORDER BY AdjustedSkillRating ASC, SongsPlayed DESC, TotalScore DESC, FullComboCount DESC, AccountId ASC) AS AdjustedSkillRank,
                        ROW_NUMBER() OVER (ORDER BY AdjustedWeightedRating ASC, SongsPlayed DESC, TotalScore DESC, FullComboCount DESC, AccountId ASC) AS WeightedRank,
                        ROW_NUMBER() OVER (ORDER BY AdjustedFcRate DESC, SongsPlayed DESC, AdjustedSkillRating ASC, AccountId ASC) AS FcRateRank,
                        ROW_NUMBER() OVER (ORDER BY TotalScore DESC, SongsPlayed DESC, AdjustedSkillRating ASC, AccountId ASC) AS TotalScoreRank,
                        ROW_NUMBER() OVER (ORDER BY AdjustedMaxScorePercent DESC, SongsPlayed DESC, AdjustedSkillRating ASC, AccountId ASC) AS MaxScorePercentRank
                    FROM WithBayesian
                )
                INSERT INTO AccountRankings
                SELECT AccountId, SongsPlayed, TotalChartedSongs, Coverage,
                       RawSkillRating, AdjustedSkillRating, AdjustedSkillRank,
                       AdjustedWeightedRating, WeightedRank,
                       AdjustedFcRate, FcRateRank,
                       TotalScore, TotalScoreRank,
                       AdjustedMaxScorePercent, MaxScorePercentRank,
                       AvgAccuracy, FullComboCount, AvgStars, BestRank, AvgRank,
                       @now
                FROM Ranked;
                """;
            cmd.Parameters.AddWithValue("@totalCharted", totalChartedSongs);
            cmd.Parameters.AddWithValue("@m", credibilityThreshold);
            cmd.Parameters.AddWithValue("@C", populationMedian);
            cmd.Parameters.AddWithValue("@now", now);

            var ranked = cmd.ExecuteNonQuery();
            tx.Commit();

            _log.LogInformation("Computed AccountRankings for {Instrument}: {Count:N0} accounts ranked.", _instrument, ranked);
            return ranked;
        }
    }

    /// <summary>
    /// Snapshot today's ranks for the top N accounts + specified additional accounts into <c>RankHistory</c>.
    /// Automatically purges snapshots older than <paramref name="retentionDays"/>.
    /// </summary>
    public int SnapshotRankHistory(int topN, IReadOnlySet<string>? additionalAccountIds = null, int retentionDays = 365)
    {
        lock (_writeLock)
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var conn = GetPersistentConnection();
            using var tx = conn.BeginTransaction();

            // Insert top N by AdjustedSkillRank
            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT OR REPLACE INTO RankHistory (AccountId, SnapshotDate, AdjustedSkillRank, WeightedRank, FcRateRank, TotalScoreRank, MaxScorePercentRank)
                    SELECT AccountId, @today, AdjustedSkillRank, WeightedRank, FcRateRank, TotalScoreRank, MaxScorePercentRank
                    FROM AccountRankings
                    WHERE AdjustedSkillRank <= @topN;
                    """;
                ins.Parameters.AddWithValue("@today", today);
                ins.Parameters.AddWithValue("@topN", topN);
                ins.ExecuteNonQuery();
            }

            // Insert additional accounts (registered users) that may not be in top N
            if (additionalAccountIds is { Count: > 0 })
            {
                using var ins2 = conn.CreateCommand();
                ins2.Transaction = tx;
                ins2.CommandText = """
                    INSERT OR REPLACE INTO RankHistory (AccountId, SnapshotDate, AdjustedSkillRank, WeightedRank, FcRateRank, TotalScoreRank, MaxScorePercentRank)
                    SELECT AccountId, @today, AdjustedSkillRank, WeightedRank, FcRateRank, TotalScoreRank, MaxScorePercentRank
                    FROM AccountRankings
                    WHERE AccountId = @aid;
                    """;
                ins2.Parameters.AddWithValue("@today", today);
                var pAid = ins2.Parameters.Add("@aid", SqliteType.Text);
                ins2.Prepare();

                foreach (var aid in additionalAccountIds)
                {
                    pAid.Value = aid;
                    ins2.ExecuteNonQuery();
                }
            }

            // Purge old snapshots
            using (var purge = conn.CreateCommand())
            {
                purge.Transaction = tx;
                purge.CommandText = "DELETE FROM RankHistory WHERE SnapshotDate < date(@today, @retention);";
                purge.Parameters.AddWithValue("@today", today);
                purge.Parameters.AddWithValue("@retention", $"-{retentionDays} days");
                purge.ExecuteNonQuery();
            }

            // Count today's snapshots
            int count;
            using (var cntCmd = conn.CreateCommand())
            {
                cntCmd.Transaction = tx;
                cntCmd.CommandText = "SELECT COUNT(*) FROM RankHistory WHERE SnapshotDate = @today;";
                cntCmd.Parameters.AddWithValue("@today", today);
                count = Convert.ToInt32(cntCmd.ExecuteScalar());
            }

            tx.Commit();
            _log.LogDebug("RankHistory snapshot for {Instrument}: {Count} accounts, date={Date}.", _instrument, count, today);
            return count;
        }
    }

    /// <summary>
    /// Get a paginated list of account rankings, sorted by the specified rank column.
    /// </summary>
    public (List<AccountRankingDto> Entries, int TotalCount) GetAccountRankings(
        string rankBy = "adjusted", int page = 1, int pageSize = 50)
    {
        var orderCol = rankBy.ToLowerInvariant() switch
        {
            "weighted" => "WeightedRank",
            "fcrate" => "FcRateRank",
            "totalscore" => "TotalScoreRank",
            "maxscore" => "MaxScorePercentRank",
            _ => "AdjustedSkillRank",
        };

        using var conn = OpenConnection();

        int totalCount;
        using (var cntCmd = conn.CreateCommand())
        {
            cntCmd.CommandText = "SELECT COUNT(*) FROM AccountRankings;";
            totalCount = Convert.ToInt32(cntCmd.ExecuteScalar());
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT AccountId, SongsPlayed, TotalChartedSongs, Coverage,
                   RawSkillRating, AdjustedSkillRating, AdjustedSkillRank,
                   WeightedRating, WeightedRank,
                   FcRate, FcRateRank,
                   TotalScore, TotalScoreRank,
                   MaxScorePercent, MaxScorePercentRank,
                   AvgAccuracy, FullComboCount, AvgStars, BestRank, AvgRank,
                   ComputedAt
            FROM AccountRankings
            ORDER BY {orderCol} ASC
            LIMIT @limit OFFSET @offset;
            """;
        cmd.Parameters.AddWithValue("@limit", pageSize);
        cmd.Parameters.AddWithValue("@offset", (page - 1) * pageSize);

        var entries = new List<AccountRankingDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadAccountRankingDto(reader, _instrument));
        }
        return (entries, totalCount);
    }

    /// <summary>
    /// Get a single account's ranking on this instrument.
    /// </summary>
    public AccountRankingDto? GetAccountRanking(string accountId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT AccountId, SongsPlayed, TotalChartedSongs, Coverage,
                   RawSkillRating, AdjustedSkillRating, AdjustedSkillRank,
                   WeightedRating, WeightedRank,
                   FcRate, FcRateRank,
                   TotalScore, TotalScoreRank,
                   MaxScorePercent, MaxScorePercentRank,
                   AvgAccuracy, FullComboCount, AvgStars, BestRank, AvgRank,
                   ComputedAt
            FROM AccountRankings
            WHERE AccountId = @accountId;
            """;
        cmd.Parameters.AddWithValue("@accountId", accountId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return ReadAccountRankingDto(reader, _instrument);
    }

    /// <summary>
    /// Get all account rankings as a lightweight list (for composite computation).
    /// Returns (AccountId, AdjustedSkillRating, SongsPlayed, AdjustedSkillRank).
    /// </summary>
    internal List<(string AccountId, double AdjustedSkillRating, int SongsPlayed, int AdjustedSkillRank)> GetAllRankingSummaries()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT AccountId, AdjustedSkillRating, SongsPlayed, AdjustedSkillRank FROM AccountRankings;";

        var result = new List<(string, double, int, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetDouble(1), reader.GetInt32(2), reader.GetInt32(3)));
        }
        return result;
    }

    /// <summary>
    /// Get rank history for an account over the last N days.
    /// </summary>
    public List<RankHistoryDto> GetRankHistory(string accountId, int days = 30)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SnapshotDate, AdjustedSkillRank, WeightedRank, FcRateRank, TotalScoreRank, MaxScorePercentRank
            FROM RankHistory
            WHERE AccountId = @accountId AND SnapshotDate >= @cutoff
            ORDER BY SnapshotDate ASC;
            """;
        cmd.Parameters.AddWithValue("@accountId", accountId);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        var result = new List<RankHistoryDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new RankHistoryDto
            {
                SnapshotDate = reader.GetString(0),
                AdjustedSkillRank = reader.GetInt32(1),
                WeightedRank = reader.GetInt32(2),
                FcRateRank = reader.GetInt32(3),
                TotalScoreRank = reader.GetInt32(4),
                MaxScorePercentRank = reader.GetInt32(5),
            });
        }
        return result;
    }

    /// <summary>
    /// Get the total number of ranked accounts on this instrument.
    /// </summary>
    public int GetRankedAccountCount()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM AccountRankings;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static AccountRankingDto ReadAccountRankingDto(SqliteDataReader reader, string instrument)
    {
        return new AccountRankingDto
        {
            AccountId = reader.GetString(0),
            SongsPlayed = reader.GetInt32(1),
            TotalChartedSongs = reader.GetInt32(2),
            Coverage = reader.GetDouble(3),
            RawSkillRating = reader.GetDouble(4),
            AdjustedSkillRating = reader.GetDouble(5),
            AdjustedSkillRank = reader.GetInt32(6),
            WeightedRating = reader.GetDouble(7),
            WeightedRank = reader.GetInt32(8),
            FcRate = reader.GetDouble(9),
            FcRateRank = reader.GetInt32(10),
            TotalScore = reader.GetInt64(11),
            TotalScoreRank = reader.GetInt32(12),
            MaxScorePercent = reader.GetDouble(13),
            MaxScorePercentRank = reader.GetInt32(14),
            AvgAccuracy = reader.GetDouble(15),
            FullComboCount = reader.GetInt32(16),
            AvgStars = reader.GetDouble(17),
            BestRank = reader.GetInt32(18),
            AvgRank = reader.GetDouble(19),
            ComputedAt = reader.GetString(20),
            Instrument = instrument,
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
