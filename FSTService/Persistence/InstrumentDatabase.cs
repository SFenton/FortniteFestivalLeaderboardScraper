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
        MigrateDropColumn(conn, "Rank");
        MigrateDropColumn(conn, "PointsEarned");

        // ── Recreate IX_Song with Score DESC ──
        using var createNewIdx = conn.CreateCommand();
        createNewIdx.CommandText = "CREATE INDEX IF NOT EXISTS IX_Song ON LeaderboardEntries (SongId, Score DESC);";
        createNewIdx.ExecuteNonQuery();

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

        var now = DateTime.UtcNow.ToString("o");
        int affected = 0;

        var conn = GetPersistentConnection();
        using var tx = conn.BeginTransaction();

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO LeaderboardEntries
                (SongId, AccountId, Score, Accuracy, IsFullCombo, Stars, Season, Percentile, FirstSeenAt, LastUpdatedAt)
            VALUES
                (@songId, @accountId, @score, @accuracy, @fc, @stars, @season, @pct, @now, @now)
            ON CONFLICT(SongId, AccountId) DO UPDATE SET
                Score         = excluded.Score,
                Accuracy      = excluded.Accuracy,
                IsFullCombo   = excluded.IsFullCombo,
                Stars         = excluded.Stars,
                Season        = excluded.Season,
                Percentile    = excluded.Percentile,
                LastUpdatedAt = excluded.LastUpdatedAt
            WHERE Score != excluded.Score;
            """;

        var pSongId    = cmd.Parameters.Add("@songId", SqliteType.Text);
        var pAccountId = cmd.Parameters.Add("@accountId", SqliteType.Text);
        var pScore     = cmd.Parameters.Add("@score", SqliteType.Integer);
        var pAccuracy  = cmd.Parameters.Add("@accuracy", SqliteType.Integer);
        var pFc        = cmd.Parameters.Add("@fc", SqliteType.Integer);
        var pStars     = cmd.Parameters.Add("@stars", SqliteType.Integer);
        var pSeason    = cmd.Parameters.Add("@season", SqliteType.Integer);
        var pPct       = cmd.Parameters.Add("@pct", SqliteType.Real);
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
            pNow.Value       = now;

            affected += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return affected;
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
            SELECT Score, Accuracy, IsFullCombo, Stars, Season, Percentile
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
        };
    }

    /// <summary>Total row count across all songs (for status/reporting).</summary>
    public long GetTotalEntryCount()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM LeaderboardEntries;";
        return (long)(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// Get the leaderboard for a song, ordered by rank.
    /// Optionally limit to top N entries.
    /// </summary>
    public List<LeaderboardEntryDto> GetLeaderboard(string songId, int? top = null)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = top.HasValue
            ? "SELECT AccountId, Score, Accuracy, IsFullCombo, Stars, Season, Percentile FROM LeaderboardEntries WHERE SongId = @songId ORDER BY Score DESC LIMIT @top;"
            : "SELECT AccountId, Score, Accuracy, IsFullCombo, Stars, Season, Percentile FROM LeaderboardEntries WHERE SongId = @songId ORDER BY Score DESC;";
        cmd.Parameters.AddWithValue("@songId", songId);
        if (top.HasValue)
            cmd.Parameters.AddWithValue("@top", top.Value);

        var entries = new List<LeaderboardEntryDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadEntryDto(reader));
        }
        return entries;
    }

    /// <summary>
    /// Get all entries for a player across all songs on this instrument.
    /// </summary>
    public List<PlayerScoreDto> GetPlayerScores(string accountId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SongId, Score, Accuracy, IsFullCombo, Stars, Season, Percentile FROM LeaderboardEntries WHERE AccountId = @accountId ORDER BY SongId;";
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
            });
        }
        return scores;
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
}
