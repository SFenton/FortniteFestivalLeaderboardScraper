using Microsoft.Data.Sqlite;

namespace FSTService.Persistence;

/// <summary>
/// Manages the central <c>fst-meta.db</c> database containing cross-cutting
/// concerns: ScrapeLog, ScoreHistory, AccountNames, RegisteredUsers.
/// </summary>
public sealed class MetaDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<MetaDatabase> _log;
    private bool _initialized;

    public MetaDatabase(string dbPath, ILogger<MetaDatabase> log)
    {
        _log = log;

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    /// <summary>
    /// Create all meta-DB tables if they don't already exist.
    /// </summary>
    public void EnsureSchema()
    {
        if (_initialized) return;

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS ScrapeLog (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                StartedAt     TEXT    NOT NULL,
                CompletedAt   TEXT,
                SongsScraped  INTEGER,
                TotalEntries  INTEGER,
                TotalRequests INTEGER,
                TotalBytes    INTEGER
            );

            CREATE TABLE IF NOT EXISTS ScoreHistory (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                SongId      TEXT    NOT NULL,
                Instrument  TEXT    NOT NULL,
                AccountId   TEXT    NOT NULL,
                OldScore    INTEGER,
                NewScore    INTEGER,
                OldRank     INTEGER,
                NewRank     INTEGER,
                ChangedAt   TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_ScoreHist_Account ON ScoreHistory (AccountId);
            CREATE INDEX IF NOT EXISTS IX_ScoreHist_Song    ON ScoreHistory (SongId, Instrument);

            CREATE TABLE IF NOT EXISTS AccountNames (
                AccountId    TEXT PRIMARY KEY,
                DisplayName  TEXT,
                LastResolved TEXT
            );

            CREATE TABLE IF NOT EXISTS RegisteredUsers (
                DeviceId     TEXT NOT NULL,
                AccountId    TEXT NOT NULL,
                RegisteredAt TEXT NOT NULL,
                LastSyncAt   TEXT,
                PRIMARY KEY (DeviceId, AccountId)
            );

            CREATE INDEX IF NOT EXISTS IX_Reg_Account ON RegisteredUsers (AccountId);

            """;
        cmd.ExecuteNonQuery();
        _initialized = true;

        _log.LogDebug("Meta DB schema ensured.");
    }

    // ─── ScrapeLog ──────────────────────────────────────────────

    /// <summary>
    /// Start a new scrape run. Returns the auto-generated Id.
    /// </summary>
    public long StartScrapeRun()
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ScrapeLog (StartedAt)
            VALUES (@now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@now", now);
        return (long)(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// Mark a scrape run as completed with summary statistics.
    /// </summary>
    public void CompleteScrapeRun(long scrapeId, int songsScraped, long totalEntries,
                                  int totalRequests, long totalBytes)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE ScrapeLog
            SET CompletedAt   = @now,
                SongsScraped  = @songs,
                TotalEntries  = @entries,
                TotalRequests = @requests,
                TotalBytes    = @bytes
            WHERE Id = @id;
            """;
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@songs", songsScraped);
        cmd.Parameters.AddWithValue("@entries", totalEntries);
        cmd.Parameters.AddWithValue("@requests", totalRequests);
        cmd.Parameters.AddWithValue("@bytes", totalBytes);
        cmd.Parameters.AddWithValue("@id", scrapeId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Get the most recent completed scrape run, or null if none.
    /// </summary>
    public ScrapeRunInfo? GetLastCompletedScrapeRun()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, StartedAt, CompletedAt, SongsScraped, TotalEntries, TotalRequests, TotalBytes
            FROM ScrapeLog
            WHERE CompletedAt IS NOT NULL
            ORDER BY Id DESC
            LIMIT 1;
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new ScrapeRunInfo
        {
            Id            = reader.GetInt64(0),
            StartedAt     = reader.GetString(1),
            CompletedAt   = reader.IsDBNull(2) ? null : reader.GetString(2),
            SongsScraped  = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            TotalEntries  = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
            TotalRequests = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            TotalBytes    = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
        };
    }

    // ─── ScoreHistory ───────────────────────────────────────────

    /// <summary>
    /// Record a score change for a registered user.
    /// </summary>
    public void InsertScoreChange(string songId, string instrument, string accountId,
                                  int? oldScore, int newScore, int? oldRank, int newRank)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ScoreHistory (SongId, Instrument, AccountId, OldScore, NewScore, OldRank, NewRank, ChangedAt)
            VALUES (@songId, @instrument, @accountId, @oldScore, @newScore, @oldRank, @newRank, @now);
            """;
        cmd.Parameters.AddWithValue("@songId", songId);
        cmd.Parameters.AddWithValue("@instrument", instrument);
        cmd.Parameters.AddWithValue("@accountId", accountId);
        cmd.Parameters.AddWithValue("@oldScore", oldScore.HasValue ? oldScore.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@newScore", newScore);
        cmd.Parameters.AddWithValue("@oldRank", oldRank.HasValue ? oldRank.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@newRank", newRank);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    // ─── AccountNames ───────────────────────────────────────────

    /// <summary>
    /// Bulk insert account IDs seen during scraping.  New IDs are added with
    /// <c>DisplayName = NULL</c> and <c>LastResolved = NULL</c> so the name
    /// resolver can pick them up later.  Existing rows are left untouched.
    /// </summary>
    public int InsertAccountIds(IEnumerable<string> accountIds)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO AccountNames (AccountId, DisplayName, LastResolved)
            VALUES (@id, NULL, NULL);
            """;
        var pId = cmd.Parameters.Add("@id", SqliteType.Text);
        cmd.Prepare();

        int inserted = 0;
        foreach (var id in accountIds)
        {
            pId.Value = id;
            inserted += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return inserted;
    }

    /// <summary>
    /// Get all account IDs that have never been resolved (LastResolved IS NULL).
    /// These are accounts seen during scraping that the name resolver hasn't
    /// attempted yet.
    /// </summary>
    public List<string> GetUnresolvedAccountIds()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT AccountId FROM AccountNames WHERE LastResolved IS NULL;";

        var ids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>
    /// Get the count of account IDs that have never been resolved.
    /// </summary>
    public int GetUnresolvedAccountCount()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM AccountNames WHERE LastResolved IS NULL;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Get all known account IDs (for deduplication during
    /// post-pass name resolution).
    /// </summary>
    public HashSet<string> GetKnownAccountIds()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT AccountId FROM AccountNames;";

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>
    /// Bulk upsert resolved account names.  When an account ID already exists
    /// (e.g. pre-inserted during scraping with NULLs), the row is updated
    /// with the resolved display name and timestamp.
    /// </summary>
    public int InsertAccountNames(IReadOnlyList<(string AccountId, string? DisplayName)> accounts)
    {
        if (accounts.Count == 0) return 0;

        var now = DateTime.UtcNow.ToString("o");
        int affected = 0;

        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO AccountNames (AccountId, DisplayName, LastResolved)
                VALUES (@id, @name, @now)
                ON CONFLICT(AccountId) DO UPDATE SET
                    DisplayName  = excluded.DisplayName,
                    LastResolved = excluded.LastResolved;
                """;

            var pId   = cmd.Parameters.Add("@id", SqliteType.Text);
            var pName = cmd.Parameters.Add("@name", SqliteType.Text);
            var pNow  = cmd.Parameters.Add("@now", SqliteType.Text);
            cmd.Prepare();

            foreach (var (accountId, displayName) in accounts)
            {
                pId.Value   = accountId;
                pName.Value = displayName is not null ? displayName : DBNull.Value;
                pNow.Value  = now;
                affected += cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        return affected;
    }

    // ─── RegisteredUsers ────────────────────────────────────────

    /// <summary>
    /// Get all registered account IDs (for change detection during scrape).
    /// </summary>
    public HashSet<string> GetRegisteredAccountIds()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT AccountId FROM RegisteredUsers;";

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>
    /// Register a device + account pair. Returns true if newly inserted.
    /// </summary>
    public bool RegisterUser(string deviceId, string accountId)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO RegisteredUsers (DeviceId, AccountId, RegisteredAt)
            VALUES (@deviceId, @accountId, @now);
            """;
        cmd.Parameters.AddWithValue("@deviceId", deviceId);
        cmd.Parameters.AddWithValue("@accountId", accountId);
        cmd.Parameters.AddWithValue("@now", now);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Get score history for an account, newest first.
    /// </summary>
    public List<ScoreHistoryEntry> GetScoreHistory(string accountId, int limit = 100)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SongId, Instrument, OldScore, NewScore, OldRank, NewRank, ChangedAt
            FROM ScoreHistory
            WHERE AccountId = @accountId
            ORDER BY Id DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@accountId", accountId);
        cmd.Parameters.AddWithValue("@limit", limit);

        var entries = new List<ScoreHistoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new ScoreHistoryEntry
            {
                SongId     = reader.GetString(0),
                Instrument = reader.GetString(1),
                OldScore   = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                NewScore   = reader.GetInt32(3),
                OldRank    = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                NewRank    = reader.GetInt32(5),
                ChangedAt  = reader.GetString(6),
            });
        }
        return entries;
    }

    /// <summary>
    /// Resolve a display name for an account ID, or null if unknown.
    /// </summary>
    public string? GetDisplayName(string accountId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DisplayName FROM AccountNames WHERE AccountId = @id;";
        cmd.Parameters.AddWithValue("@id", accountId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Get all (DeviceId, AccountId) pairs from RegisteredUsers.
    /// Used by PersonalDbBuilder to determine which devices to rebuild.
    /// </summary>
    public List<(string DeviceId, string AccountId)> GetDeviceAccountMappings()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DeviceId, AccountId FROM RegisteredUsers;";

        var mappings = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            mappings.Add((reader.GetString(0), reader.GetString(1)));
        return mappings;
    }

    /// <summary>
    /// Get the AccountId registered for a specific device, or null if not registered.
    /// If multiple accounts are registered for a device, returns the most recent.
    /// </summary>
    public string? GetAccountForDevice(string deviceId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT AccountId FROM RegisteredUsers
            WHERE DeviceId = @deviceId
            ORDER BY RegisteredAt DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@deviceId", deviceId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Update the LastSyncAt timestamp for a device + account registration.
    /// </summary>
    public void UpdateLastSync(string deviceId, string accountId)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE RegisteredUsers
            SET LastSyncAt = @now
            WHERE DeviceId = @deviceId AND AccountId = @accountId;
            """;
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@deviceId", deviceId);
        cmd.Parameters.AddWithValue("@accountId", accountId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Check whether a device is registered (has at least one account).
    /// </summary>
    public bool IsDeviceRegistered(string deviceId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM RegisteredUsers WHERE DeviceId = @deviceId;";
        cmd.Parameters.AddWithValue("@deviceId", deviceId);
        return (long)(cmd.ExecuteScalar() ?? 0) > 0;
    }

    // ─── Helpers ────────────────────────────────────────────────

    private readonly object _writeLock = new();

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        return conn;
    }

    public void Dispose()
    {
        // No persistent connections to clean up.
    }
}

/// <summary>
/// Info about a completed scrape run from the ScrapeLog table.
/// </summary>
public sealed class ScrapeRunInfo
{
    public long Id { get; init; }
    public string StartedAt { get; init; } = "";
    public string? CompletedAt { get; init; }
    public int SongsScraped { get; init; }
    public long TotalEntries { get; init; }
    public int TotalRequests { get; init; }
    public long TotalBytes { get; init; }
}
