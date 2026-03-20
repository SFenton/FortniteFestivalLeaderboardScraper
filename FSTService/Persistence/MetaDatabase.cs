using Microsoft.Data.Sqlite;

namespace FSTService.Persistence;

/// <summary>
/// Manages the central <c>fst-meta.db</c> database containing cross-cutting
/// concerns: ScrapeLog, ScoreHistory, AccountNames, RegisteredUsers.
/// </summary>
public sealed class MetaDatabase : IDisposable
{
    /// <summary>
    /// Bump this when data-collection logic changes in a way that requires
    /// re-running backfill / history reconstruction for all users.
    /// </summary>
    internal const int DataCollectionVersion = 2;

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

    /// <summary>Allow tests to re-run <see cref="EnsureSchema"/> after mutating version rows.</summary>
    internal void ResetInitialized() => _initialized = false;

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
                Accuracy    INTEGER,
                IsFullCombo INTEGER,
                Stars       INTEGER,
                Percentile  REAL,
                Season      INTEGER,
                ScoreAchievedAt TEXT,
                SeasonRank  INTEGER,
                AllTimeRank INTEGER,
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

            CREATE TABLE IF NOT EXISTS UserSessions (
                Id               INTEGER PRIMARY KEY AUTOINCREMENT,
                Username         TEXT    NOT NULL,
                DeviceId         TEXT    NOT NULL,
                RefreshTokenHash TEXT    NOT NULL UNIQUE,
                Platform         TEXT,
                IssuedAt         TEXT    NOT NULL,
                ExpiresAt        TEXT    NOT NULL,
                LastRefreshedAt  TEXT,
                RevokedAt        TEXT
            );

            CREATE INDEX IF NOT EXISTS IX_Sessions_Username ON UserSessions (Username);
            CREATE INDEX IF NOT EXISTS IX_Sessions_Token    ON UserSessions (RefreshTokenHash) WHERE RevokedAt IS NULL;

            CREATE TABLE IF NOT EXISTS BackfillStatus (
                AccountId         TEXT    PRIMARY KEY,
                Status            TEXT    NOT NULL DEFAULT 'pending',
                SongsChecked      INTEGER NOT NULL DEFAULT 0,
                EntriesFound      INTEGER NOT NULL DEFAULT 0,
                TotalSongsToCheck INTEGER NOT NULL DEFAULT 0,
                StartedAt         TEXT,
                CompletedAt       TEXT,
                LastResumedAt     TEXT,
                ErrorMessage      TEXT
            );

            CREATE TABLE IF NOT EXISTS BackfillProgress (
                AccountId   TEXT    NOT NULL,
                SongId      TEXT    NOT NULL,
                Instrument  TEXT    NOT NULL,
                Checked     INTEGER NOT NULL DEFAULT 0,
                EntryFound  INTEGER NOT NULL DEFAULT 0,
                CheckedAt   TEXT,
                PRIMARY KEY (AccountId, SongId, Instrument)
            );

            CREATE INDEX IF NOT EXISTS IX_BfProgress_Account ON BackfillProgress (AccountId);

            CREATE TABLE IF NOT EXISTS HistoryReconStatus (
                AccountId             TEXT    PRIMARY KEY,
                Status                TEXT    NOT NULL DEFAULT 'pending',
                SongsProcessed        INTEGER NOT NULL DEFAULT 0,
                TotalSongsToProcess   INTEGER NOT NULL DEFAULT 0,
                SeasonsQueried        INTEGER NOT NULL DEFAULT 0,
                HistoryEntriesFound   INTEGER NOT NULL DEFAULT 0,
                StartedAt             TEXT,
                CompletedAt           TEXT,
                ErrorMessage          TEXT
            );

            CREATE TABLE IF NOT EXISTS HistoryReconProgress (
                AccountId   TEXT    NOT NULL,
                SongId      TEXT    NOT NULL,
                Instrument  TEXT    NOT NULL,
                Processed   INTEGER NOT NULL DEFAULT 0,
                ProcessedAt TEXT,
                PRIMARY KEY (AccountId, SongId, Instrument)
            );

            CREATE INDEX IF NOT EXISTS IX_HrProgress_Account ON HistoryReconProgress (AccountId);

            CREATE TABLE IF NOT EXISTS SeasonWindows (
                SeasonNumber INTEGER PRIMARY KEY,
                EventId      TEXT    NOT NULL,
                WindowId     TEXT    NOT NULL,
                DiscoveredAt TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS SongFirstSeenSeason (
                SongId           TEXT    PRIMARY KEY,
                FirstSeenSeason  INTEGER,
                MinObservedSeason INTEGER,
                EstimatedSeason  INTEGER NOT NULL,
                ProbeResult      TEXT,
                CalculatedAt     TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS EpicUserTokens (
                AccountId              TEXT PRIMARY KEY,
                EncryptedAccessToken   BLOB NOT NULL,
                EncryptedRefreshToken  BLOB NOT NULL,
                TokenExpiresAt         TEXT NOT NULL,
                RefreshExpiresAt       TEXT NOT NULL,
                Nonce                  BLOB NOT NULL,
                UpdatedAt              TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS LeaderboardPopulation (
                SongId       TEXT    NOT NULL,
                Instrument   TEXT    NOT NULL,
                TotalEntries INTEGER NOT NULL DEFAULT -1,
                UpdatedAt    TEXT    NOT NULL,
                PRIMARY KEY (SongId, Instrument)
            );

            CREATE TABLE IF NOT EXISTS PlayerStats (
                AccountId       TEXT    NOT NULL,
                Instrument      TEXT    NOT NULL,
                SongsPlayed     INTEGER NOT NULL DEFAULT 0,
                FullComboCount  INTEGER NOT NULL DEFAULT 0,
                GoldStarCount   INTEGER NOT NULL DEFAULT 0,
                AvgAccuracy     REAL    NOT NULL DEFAULT 0,
                BestRank        INTEGER NOT NULL DEFAULT 0,
                BestRankSongId  TEXT,
                TotalScore      INTEGER NOT NULL DEFAULT 0,
                PercentileDist  TEXT,
                AvgPercentile   TEXT,
                OverallPercentile TEXT,
                UpdatedAt       TEXT    NOT NULL,
                PRIMARY KEY (AccountId, Instrument)
            );

            CREATE TABLE IF NOT EXISTS DataVersion (
                Key     TEXT PRIMARY KEY,
                Version INTEGER NOT NULL
            );

            """;
        cmd.ExecuteNonQuery();

        // ── Migrations: add snapshot columns to ScoreHistory (existing DBs) ──
        MigrateAddColumn(conn, "ScoreHistory", "Accuracy", "INTEGER");
        MigrateAddColumn(conn, "ScoreHistory", "IsFullCombo", "INTEGER");

        // SongFirstSeenSeason: allow NULL FirstSeenSeason/MinObservedSeason + add EstimatedSeason
        MigrateAddColumn(conn, "SongFirstSeenSeason", "EstimatedSeason", "INTEGER");
        MigrateSongFirstSeenSeasonSchema(conn);
        MigrateAddColumn(conn, "ScoreHistory", "Stars", "INTEGER");
        MigrateAddColumn(conn, "ScoreHistory", "Percentile", "REAL");
        MigrateAddColumn(conn, "ScoreHistory", "Season", "INTEGER");
        MigrateAddColumn(conn, "ScoreHistory", "ScoreAchievedAt", "TEXT");
        MigrateAddColumn(conn, "ScoreHistory", "SeasonRank", "INTEGER");
        MigrateAddColumn(conn, "ScoreHistory", "AllTimeRank", "INTEGER");

        // ── Migration: dedup index on ScoreHistory ──
        using (var idxCmd = conn.CreateCommand())
        {
            // Remove any pre-existing duplicates before creating the unique index
            idxCmd.CommandText = """
                DELETE FROM ScoreHistory
                WHERE Id NOT IN (
                    SELECT MIN(Id) FROM ScoreHistory
                    GROUP BY AccountId, SongId, Instrument, NewScore, ScoreAchievedAt
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_ScoreHist_Dedup
                    ON ScoreHistory (AccountId, SongId, Instrument, NewScore, ScoreAchievedAt);
                """;
            idxCmd.ExecuteNonQuery();
        }

        // ── Migrations: add columns to RegisteredUsers (existing DBs) ──
        MigrateAddColumn(conn, "RegisteredUsers", "DisplayName", "TEXT");
        MigrateAddColumn(conn, "RegisteredUsers", "Platform", "TEXT");
        MigrateAddColumn(conn, "RegisteredUsers", "LastLoginAt", "TEXT");

        // ── Migration: re-queue backfill / history recon when data-collection version bumps ──
        MigrateDataCollectionVersion(conn);

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
                                  int? oldScore, int newScore, int? oldRank, int newRank,
                                  int? accuracy = null, bool? isFullCombo = null,
                                  int? stars = null, double? percentile = null,
                                  int? season = null, string? scoreAchievedAt = null,
                                  int? seasonRank = null, int? allTimeRank = null)
    {
        var now = DateTime.UtcNow.ToString("o");
        lock (_writeLock)
        {
        var conn = GetPersistentConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ScoreHistory (SongId, Instrument, AccountId, OldScore, NewScore, OldRank, NewRank,
                                     Accuracy, IsFullCombo, Stars, Percentile, Season, ScoreAchievedAt,
                                     SeasonRank, AllTimeRank, ChangedAt)
            VALUES (@songId, @instrument, @accountId, @oldScore, @newScore, @oldRank, @newRank,
                    @accuracy, @fc, @stars, @percentile, @season, @scoreAchievedAt,
                    @seasonRank, @allTimeRank, @now)
            ON CONFLICT(AccountId, SongId, Instrument, NewScore, ScoreAchievedAt) DO UPDATE SET
                SeasonRank  = COALESCE(excluded.SeasonRank,  ScoreHistory.SeasonRank),
                AllTimeRank = COALESCE(excluded.AllTimeRank, ScoreHistory.AllTimeRank),
                OldScore    = COALESCE(excluded.OldScore,    ScoreHistory.OldScore),
                OldRank     = COALESCE(excluded.OldRank,     ScoreHistory.OldRank),
                ChangedAt   = excluded.ChangedAt;
            """;
        cmd.Parameters.AddWithValue("@songId", songId);
        cmd.Parameters.AddWithValue("@instrument", instrument);
        cmd.Parameters.AddWithValue("@accountId", accountId);
        cmd.Parameters.AddWithValue("@oldScore", oldScore.HasValue ? oldScore.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@newScore", newScore);
        cmd.Parameters.AddWithValue("@oldRank", oldRank.HasValue ? oldRank.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@newRank", newRank);
        cmd.Parameters.AddWithValue("@accuracy", accuracy.HasValue ? accuracy.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@fc", isFullCombo.HasValue ? (isFullCombo.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("@stars", stars.HasValue ? stars.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@percentile", percentile.HasValue ? percentile.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@season", season.HasValue ? season.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@scoreAchievedAt", (object?)scoreAchievedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@seasonRank", seasonRank.HasValue ? seasonRank.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@allTimeRank", allTimeRank.HasValue ? allTimeRank.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
        } // lock
    }

    /// <summary>
    /// Batch-insert multiple score changes in a single transaction.
    /// Avoids per-call connection overhead when called from the scrape pipeline.
    /// </summary>
    public int InsertScoreChanges(IReadOnlyList<ScoreChangeRecord> changes)
    {
        if (changes.Count == 0) return 0;

        lock (_writeLock)
        {
        var now = DateTime.UtcNow.ToString("o");
        var conn = GetPersistentConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO ScoreHistory (SongId, Instrument, AccountId, OldScore, NewScore, OldRank, NewRank,
                                     Accuracy, IsFullCombo, Stars, Percentile, Season, ScoreAchievedAt,
                                     SeasonRank, AllTimeRank, ChangedAt)
            VALUES (@songId, @instrument, @accountId, @oldScore, @newScore, @oldRank, @newRank,
                    @accuracy, @fc, @stars, @percentile, @season, @scoreAchievedAt,
                    @seasonRank, @allTimeRank, @now)
            ON CONFLICT(AccountId, SongId, Instrument, NewScore, ScoreAchievedAt) DO UPDATE SET
                SeasonRank  = COALESCE(excluded.SeasonRank,  ScoreHistory.SeasonRank),
                AllTimeRank = COALESCE(excluded.AllTimeRank, ScoreHistory.AllTimeRank),
                OldScore    = COALESCE(excluded.OldScore,    ScoreHistory.OldScore),
                OldRank     = COALESCE(excluded.OldRank,     ScoreHistory.OldRank),
                ChangedAt   = excluded.ChangedAt;
            """;

        var pSongId         = cmd.Parameters.Add("@songId", SqliteType.Text);
        var pInstrument     = cmd.Parameters.Add("@instrument", SqliteType.Text);
        var pAccountId      = cmd.Parameters.Add("@accountId", SqliteType.Text);
        var pOldScore       = cmd.Parameters.Add("@oldScore", SqliteType.Integer);
        var pNewScore       = cmd.Parameters.Add("@newScore", SqliteType.Integer);
        var pOldRank        = cmd.Parameters.Add("@oldRank", SqliteType.Integer);
        var pNewRank        = cmd.Parameters.Add("@newRank", SqliteType.Integer);
        var pAccuracy       = cmd.Parameters.Add("@accuracy", SqliteType.Integer);
        var pFc             = cmd.Parameters.Add("@fc", SqliteType.Integer);
        var pStars          = cmd.Parameters.Add("@stars", SqliteType.Integer);
        var pPercentile     = cmd.Parameters.Add("@percentile", SqliteType.Real);
        var pSeason         = cmd.Parameters.Add("@season", SqliteType.Integer);
        var pScoreAchievedAt = cmd.Parameters.Add("@scoreAchievedAt", SqliteType.Text);
        var pSeasonRank     = cmd.Parameters.Add("@seasonRank", SqliteType.Integer);
        var pAllTimeRank    = cmd.Parameters.Add("@allTimeRank", SqliteType.Integer);
        var pNow            = cmd.Parameters.Add("@now", SqliteType.Text);
        cmd.Prepare();

        int inserted = 0;
        foreach (var c in changes)
        {
            pSongId.Value         = c.SongId;
            pInstrument.Value     = c.Instrument;
            pAccountId.Value      = c.AccountId;
            pOldScore.Value       = c.OldScore.HasValue ? c.OldScore.Value : DBNull.Value;
            pNewScore.Value       = c.NewScore;
            pOldRank.Value        = c.OldRank.HasValue ? c.OldRank.Value : DBNull.Value;
            pNewRank.Value        = c.NewRank;
            pAccuracy.Value       = c.Accuracy.HasValue ? c.Accuracy.Value : DBNull.Value;
            pFc.Value             = c.IsFullCombo.HasValue ? (c.IsFullCombo.Value ? 1 : 0) : DBNull.Value;
            pStars.Value          = c.Stars.HasValue ? c.Stars.Value : DBNull.Value;
            pPercentile.Value     = c.Percentile.HasValue ? c.Percentile.Value : DBNull.Value;
            pSeason.Value         = c.Season.HasValue ? c.Season.Value : DBNull.Value;
            pScoreAchievedAt.Value = (object?)c.ScoreAchievedAt ?? DBNull.Value;
            pSeasonRank.Value     = c.SeasonRank.HasValue ? c.SeasonRank.Value : DBNull.Value;
            pAllTimeRank.Value    = c.AllTimeRank.HasValue ? c.AllTimeRank.Value : DBNull.Value;
            pNow.Value            = now;
            inserted += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return inserted;
        } // lock
    }

    // ─── AccountNames ───────────────────────────────────────────

    /// <summary>
    /// Bulk insert account IDs seen during scraping.  New IDs are added with
    /// <c>DisplayName = NULL</c> and <c>LastResolved = NULL</c> so the name
    /// resolver can pick them up later.  Existing rows are left untouched.
    /// </summary>
    public int InsertAccountIds(IEnumerable<string> accountIds)
    {
        lock (_writeLock)
        {
        var conn = GetPersistentConnection();
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
        } // lock
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
            var conn = GetPersistentConnection();
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
    /// Unregister a device + account pair. Returns true if a row was deleted.
    /// </summary>
    public bool UnregisterUser(string deviceId, string accountId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM RegisteredUsers
            WHERE DeviceId = @deviceId AND AccountId = @accountId;
            """;
        cmd.Parameters.AddWithValue("@deviceId", deviceId);
        cmd.Parameters.AddWithValue("@accountId", accountId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Unregister ALL device registrations for an account.
    /// Returns the list of device IDs that were removed (for personal DB cleanup).
    /// </summary>
    public List<string> UnregisterAccount(string accountId)
    {
        using var conn = OpenConnection();

        // First, collect the device IDs so the caller can clean up personal DBs.
        using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = "SELECT DeviceId FROM RegisteredUsers WHERE AccountId = @accountId;";
        selectCmd.Parameters.AddWithValue("@accountId", accountId);

        var deviceIds = new List<string>();
        using (var reader = selectCmd.ExecuteReader())
        {
            while (reader.Read())
                deviceIds.Add(reader.GetString(0));
        }

        if (deviceIds.Count == 0) return deviceIds;

        // Delete all registrations for this account.
        using var deleteCmd = conn.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM RegisteredUsers WHERE AccountId = @accountId;";
        deleteCmd.Parameters.AddWithValue("@accountId", accountId);
        deleteCmd.ExecuteNonQuery();

        return deviceIds;
    }

    /// <summary>
    /// Find registered accounts that have no active (non-revoked, non-expired) sessions.
    /// These accounts' refresh tokens have all expired — they're no longer using the app.
    /// Only returns accounts that previously had at least one session (safety guard).
    /// </summary>
    public List<string> GetOrphanedRegisteredAccounts()
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT ru.AccountId
            FROM RegisteredUsers ru
            JOIN AccountNames an ON an.AccountId = ru.AccountId
            WHERE NOT EXISTS (
                SELECT 1 FROM UserSessions us
                WHERE us.Username = an.DisplayName
                  AND us.RevokedAt IS NULL
                  AND us.ExpiresAt > @now
            )
            AND EXISTS (
                SELECT 1 FROM UserSessions us
                WHERE us.Username = an.DisplayName
            );
            """;
        cmd.Parameters.AddWithValue("@now", now);

        var accounts = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            accounts.Add(reader.GetString(0));
        return accounts;
    }

    /// <summary>
    /// Get score history for an account, newest first.
    /// </summary>
    public List<ScoreHistoryEntry> GetScoreHistory(string accountId, int limit = 100, string? songId = null, string? instrument = null)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var where = "AccountId = @accountId";
        if (songId is not null)
            where += " AND SongId = @songId";
        if (instrument is not null)
            where += " AND Instrument = @instrument";

        cmd.CommandText = $"""
            SELECT SongId, Instrument, OldScore, NewScore, OldRank, NewRank,
                   Accuracy, IsFullCombo, Stars, Percentile, Season, ScoreAchievedAt, ChangedAt,
                   SeasonRank, AllTimeRank
            FROM ScoreHistory
            WHERE {where}
            ORDER BY Id DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@accountId", accountId);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (songId is not null)
            cmd.Parameters.AddWithValue("@songId", songId);
        if (instrument is not null)
            cmd.Parameters.AddWithValue("@instrument", instrument);

        var entries = new List<ScoreHistoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new ScoreHistoryEntry
            {
                SongId      = reader.GetString(0),
                Instrument  = reader.GetString(1),
                OldScore    = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                NewScore    = reader.GetInt32(3),
                OldRank     = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                NewRank     = reader.GetInt32(5),
                Accuracy    = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                IsFullCombo = reader.IsDBNull(7) ? null : reader.GetInt32(7) == 1,
                Stars       = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                Percentile  = reader.IsDBNull(9) ? null : reader.GetDouble(9),
                Season      = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                ScoreAchievedAt = reader.IsDBNull(11) ? null : reader.GetString(11),
                ChangedAt   = reader.GetString(12),
                SeasonRank  = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                AllTimeRank = reader.IsDBNull(14) ? null : reader.GetInt32(14),
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
    /// Search for account display names matching a query string.
    /// Results are ordered: prefix matches first, then substring matches, alphabetically within each group.
    /// </summary>
    public List<(string AccountId, string DisplayName)> SearchAccountNames(string query, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<(string, string)>();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT AccountId, DisplayName
            FROM AccountNames
            WHERE DisplayName IS NOT NULL AND DisplayName LIKE @pattern
            ORDER BY
                CASE WHEN DisplayName LIKE @prefix THEN 0 ELSE 1 END,
                LENGTH(DisplayName),
                DisplayName COLLATE NOCASE
            LIMIT @limit;
        ";
        cmd.Parameters.AddWithValue("@pattern", $"%{query}%");
        cmd.Parameters.AddWithValue("@prefix", $"{query}%");
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), reader.GetString(1)));
        }
        return results;
    }

    /// <summary>
    /// Bulk-resolve display names for a set of account IDs.
    /// Returns a dictionary mapping AccountId → DisplayName (only for accounts with a known name).
    /// </summary>
    public Dictionary<string, string> GetDisplayNames(IEnumerable<string> accountIds)
    {
        var ids = accountIds.ToList();
        if (ids.Count == 0) return new Dictionary<string, string>();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var conn = OpenConnection();

        // Process in batches to stay within SQLite variable limits
        const int batchSize = 500;
        for (int i = 0; i < ids.Count; i += batchSize)
        {
            var batch = ids.Skip(i).Take(batchSize).ToList();
            using var cmd = conn.CreateCommand();
            var paramNames = new List<string>(batch.Count);
            for (int j = 0; j < batch.Count; j++)
            {
                var pName = $"@id{j}";
                paramNames.Add(pName);
                cmd.Parameters.AddWithValue(pName, batch[j]);
            }
            cmd.CommandText = $"SELECT AccountId, DisplayName FROM AccountNames WHERE DisplayName IS NOT NULL AND AccountId IN ({string.Join(",", paramNames)});";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetString(0)] = reader.GetString(1);
            }
        }
        return result;
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

    // ─── UserSessions ───────────────────────────────────────────

    /// <summary>
    /// Insert a new session. Returns the auto-generated session ID.
    /// </summary>
    public long InsertSession(string username, string deviceId, string refreshTokenHash,
                              string? platform, DateTime expiresAt)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO UserSessions (Username, DeviceId, RefreshTokenHash, Platform, IssuedAt, ExpiresAt)
            VALUES (@username, @deviceId, @hash, @platform, @now, @expiresAt);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@username", username);
        cmd.Parameters.AddWithValue("@deviceId", deviceId);
        cmd.Parameters.AddWithValue("@hash", refreshTokenHash);
        cmd.Parameters.AddWithValue("@platform", (object?)platform ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@expiresAt", expiresAt.ToString("o"));
        return (long)(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// Find an active (non-revoked, non-expired) session by refresh token hash.
    /// </summary>
    public UserSessionInfo? GetActiveSession(string refreshTokenHash)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Username, DeviceId, Platform, IssuedAt, ExpiresAt
            FROM UserSessions
            WHERE RefreshTokenHash = @hash
              AND RevokedAt IS NULL
              AND ExpiresAt > @now
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@hash", refreshTokenHash);
        cmd.Parameters.AddWithValue("@now", now);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new UserSessionInfo
        {
            Id       = reader.GetInt64(0),
            Username = reader.GetString(1),
            DeviceId = reader.GetString(2),
            Platform = reader.IsDBNull(3) ? null : reader.GetString(3),
            IssuedAt = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
            ExpiresAt = DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
        };
    }

    /// <summary>
    /// Revoke a session by its refresh token hash.
    /// </summary>
    public void RevokeSession(string refreshTokenHash)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE UserSessions
            SET RevokedAt = @now
            WHERE RefreshTokenHash = @hash AND RevokedAt IS NULL;
            """;
        cmd.Parameters.AddWithValue("@hash", refreshTokenHash);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Revoke all sessions for a username (e.g., "sign out everywhere").
    /// </summary>
    public void RevokeAllSessions(string username)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE UserSessions
            SET RevokedAt = @now
            WHERE Username = @username AND RevokedAt IS NULL;
            """;
        cmd.Parameters.AddWithValue("@username", username);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Delete expired and revoked sessions older than a cutoff (cleanup).
    /// Returns the number of rows deleted.
    /// </summary>
    public int CleanupExpiredSessions(DateTime cutoff)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM UserSessions
            WHERE (RevokedAt IS NOT NULL AND RevokedAt < @cutoff)
               OR (ExpiresAt < @cutoff);
            """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("o"));
        return cmd.ExecuteNonQuery();
    }

    // ─── Backfill Tracking ──────────────────────────────────────

    /// <summary>
    /// Create or reset a backfill status entry for an account. Sets status to 'pending'.
    /// </summary>
    public void EnqueueBackfill(string accountId, int totalSongsToCheck)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO BackfillStatus (AccountId, Status, TotalSongsToCheck)
            VALUES (@id, 'pending', @total)
            ON CONFLICT(AccountId) DO UPDATE SET
                Status            = CASE WHEN Status = 'complete' THEN Status ELSE 'pending' END,
                TotalSongsToCheck = @total
            WHERE Status != 'complete';
            """;
        cmd.Parameters.AddWithValue("@id", accountId);
        cmd.Parameters.AddWithValue("@total", totalSongsToCheck);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Get all backfill requests that are pending or in_progress.
    /// </summary>
    public List<BackfillStatusInfo> GetPendingBackfills()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT AccountId, Status, SongsChecked, EntriesFound, TotalSongsToCheck,
                   StartedAt, CompletedAt, LastResumedAt, ErrorMessage
            FROM BackfillStatus
            WHERE Status IN ('pending', 'in_progress')
            ORDER BY rowid;
            """;
        var list = new List<BackfillStatusInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadBackfillStatus(reader));
        }
        return list;
    }

    /// <summary>
    /// Get the backfill status for a specific account.
    /// </summary>
    public BackfillStatusInfo? GetBackfillStatus(string accountId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT AccountId, Status, SongsChecked, EntriesFound, TotalSongsToCheck,
                   StartedAt, CompletedAt, LastResumedAt, ErrorMessage
            FROM BackfillStatus
            WHERE AccountId = @id;
            """;
        cmd.Parameters.AddWithValue("@id", accountId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadBackfillStatus(reader) : null;
    }

    /// <summary>
    /// Mark a backfill as in_progress with a start/resume timestamp.
    /// </summary>
    public void StartBackfill(string accountId)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE BackfillStatus
            SET Status = 'in_progress', StartedAt = COALESCE(StartedAt, @now), LastResumedAt = @now
            WHERE AccountId = @id;
            """;
        cmd.Parameters.AddWithValue("@id", accountId);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Mark a backfill as complete.
    /// </summary>
    public void CompleteBackfill(string accountId)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE BackfillStatus
            SET Status = 'complete', CompletedAt = @now
            WHERE AccountId = @id;
            """;
        cmd.Parameters.AddWithValue("@id", accountId);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Mark a backfill as errored.
    /// </summary>
    public void FailBackfill(string accountId, string errorMessage)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE BackfillStatus
            SET Status = 'error', ErrorMessage = @err
            WHERE AccountId = @id;
            """;
        cmd.Parameters.AddWithValue("@id", accountId);
        cmd.Parameters.AddWithValue("@err", errorMessage);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Update the progress counters for a backfill.
    /// </summary>
    public void UpdateBackfillProgress(string accountId, int songsChecked, int entriesFound)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE BackfillStatus
            SET SongsChecked = @checked, EntriesFound = @found
            WHERE AccountId = @id;
            """;
        cmd.Parameters.AddWithValue("@id", accountId);
        cmd.Parameters.AddWithValue("@checked", songsChecked);
        cmd.Parameters.AddWithValue("@found", entriesFound);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Mark a specific song/instrument as checked for an account's backfill.
    /// </summary>
    public void MarkBackfillSongChecked(string accountId, string songId, string instrument, bool entryFound)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO BackfillProgress (AccountId, SongId, Instrument, Checked, EntryFound, CheckedAt)
            VALUES (@acct, @song, @inst, 1, @found, @now)
            ON CONFLICT(AccountId, SongId, Instrument) DO UPDATE SET
                Checked    = 1,
                EntryFound = @found,
                CheckedAt  = @now;
            """;
        cmd.Parameters.AddWithValue("@acct", accountId);
        cmd.Parameters.AddWithValue("@song", songId);
        cmd.Parameters.AddWithValue("@inst", instrument);
        cmd.Parameters.AddWithValue("@found", entryFound ? 1 : 0);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Get the set of song/instrument pairs already checked for an account (for resumption).
    /// </summary>
    public HashSet<(string SongId, string Instrument)> GetCheckedBackfillPairs(string accountId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SongId, Instrument
            FROM BackfillProgress
            WHERE AccountId = @acct AND Checked = 1;
            """;
        cmd.Parameters.AddWithValue("@acct", accountId);

        var set = new HashSet<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            set.Add((reader.GetString(0), reader.GetString(1)));
        }
        return set;
    }

    private static BackfillStatusInfo ReadBackfillStatus(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        return new BackfillStatusInfo
        {
            AccountId         = reader.GetString(0),
            Status            = reader.GetString(1),
            SongsChecked      = reader.GetInt32(2),
            EntriesFound      = reader.GetInt32(3),
            TotalSongsToCheck = reader.GetInt32(4),
            StartedAt         = reader.IsDBNull(5) ? null : reader.GetString(5),
            CompletedAt       = reader.IsDBNull(6) ? null : reader.GetString(6),
            LastResumedAt     = reader.IsDBNull(7) ? null : reader.GetString(7),
            ErrorMessage      = reader.IsDBNull(8) ? null : reader.GetString(8),
        };
    }

    // ─── RegisteredUsers (enhanced) ─────────────────────────────

    /// <summary>
    /// Register or update a user+device pair. Sets DisplayName, Platform, LastLoginAt.
    /// Returns true if newly inserted.
    /// </summary>
    public bool RegisterOrUpdateUser(string deviceId, string accountId,
                                     string? displayName, string? platform)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO RegisteredUsers (DeviceId, AccountId, RegisteredAt, DisplayName, Platform, LastLoginAt)
            VALUES (@deviceId, @accountId, @now, @displayName, @platform, @now)
            ON CONFLICT(DeviceId, AccountId) DO UPDATE SET
                DisplayName = @displayName,
                Platform    = @platform,
                LastLoginAt = @now;
            """;
        cmd.Parameters.AddWithValue("@deviceId", deviceId);
        cmd.Parameters.AddWithValue("@accountId", accountId);
        cmd.Parameters.AddWithValue("@displayName", (object?)displayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@platform", (object?)platform ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        // INSERT OR IGNORE returns 0 if conflict; ON CONFLICT DO UPDATE always returns 1,
        // but we need to distinguish. Use a separate check.
        cmd.ExecuteNonQuery();

        // Check if just inserted (RegisteredAt == LastLoginAt == now)
        using var check = conn.CreateCommand();
        check.CommandText = """
            SELECT RegisteredAt FROM RegisteredUsers
            WHERE DeviceId = @deviceId AND AccountId = @accountId;
            """;
        check.Parameters.AddWithValue("@deviceId", deviceId);
        check.Parameters.AddWithValue("@accountId", accountId);
        var registeredAt = check.ExecuteScalar() as string;
        return registeredAt == now;
    }

    /// <summary>
    /// Look up an Epic account ID by display name (username) from AccountNames.
    /// Returns null if the username hasn't been resolved by the scraper yet.
    /// </summary>
    public string? GetAccountIdForUsername(string username)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT AccountId FROM AccountNames
            WHERE DisplayName = @username COLLATE NOCASE
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@username", username);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Get registration info for a specific username + device.
    /// </summary>
    public RegisteredUserInfo? GetRegistrationInfo(string username, string deviceId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT AccountId, DisplayName, RegisteredAt, LastLoginAt
            FROM RegisteredUsers
            WHERE AccountId = @username AND DeviceId = @deviceId
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@username", username);
        cmd.Parameters.AddWithValue("@deviceId", deviceId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new RegisteredUserInfo
        {
            AccountId    = reader.GetString(0),
            DisplayName  = reader.IsDBNull(1) ? null : reader.GetString(1),
            RegisteredAt = reader.GetString(2),
            LastLoginAt  = reader.IsDBNull(3) ? null : reader.GetString(3),
        };
    }

    // ─── History Reconstruction Tracking ───────────────────────

    /// <summary>
    /// Create or reset a history reconstruction status entry. Sets status to 'pending'.
    /// </summary>
    public void EnqueueHistoryRecon(string accountId, int totalSongsToProcess)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO HistoryReconStatus (AccountId, Status, TotalSongsToProcess)
            VALUES (@id, 'pending', @total)
            ON CONFLICT(AccountId) DO UPDATE SET
                Status              = CASE WHEN Status = 'complete' THEN Status ELSE 'pending' END,
                TotalSongsToProcess = @total
            WHERE Status != 'complete';
            """;
        cmd.Parameters.AddWithValue("@id", accountId);
        cmd.Parameters.AddWithValue("@total", totalSongsToProcess);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Get all history recon requests that are pending or in_progress.
    /// </summary>
    public List<HistoryReconStatusInfo> GetPendingHistoryRecons()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT AccountId, Status, SongsProcessed, TotalSongsToProcess,
                   SeasonsQueried, HistoryEntriesFound, StartedAt, CompletedAt, ErrorMessage
            FROM HistoryReconStatus
            WHERE Status IN ('pending', 'in_progress')
            ORDER BY rowid;
            """;
        var list = new List<HistoryReconStatusInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(ReadHistoryReconStatus(reader));
        return list;
    }

    /// <summary>
    /// Get history recon status for a specific account.
    /// </summary>
    public HistoryReconStatusInfo? GetHistoryReconStatus(string accountId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT AccountId, Status, SongsProcessed, TotalSongsToProcess,
                   SeasonsQueried, HistoryEntriesFound, StartedAt, CompletedAt, ErrorMessage
            FROM HistoryReconStatus
            WHERE AccountId = @id;
            """;
        cmd.Parameters.AddWithValue("@id", accountId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadHistoryReconStatus(reader) : null;
    }

    /// <summary>
    /// Mark a history recon as in_progress.
    /// </summary>
    public void StartHistoryRecon(string accountId)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE HistoryReconStatus
            SET Status = 'in_progress', StartedAt = COALESCE(StartedAt, @now)
            WHERE AccountId = @id;
            """;
        cmd.Parameters.AddWithValue("@id", accountId);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Mark a history recon as complete.
    /// </summary>
    public void CompleteHistoryRecon(string accountId)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE HistoryReconStatus
            SET Status = 'complete', CompletedAt = @now
            WHERE AccountId = @id;
            """;
        cmd.Parameters.AddWithValue("@id", accountId);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Mark a history recon as errored.
    /// </summary>
    public void FailHistoryRecon(string accountId, string errorMessage)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE HistoryReconStatus
            SET Status = 'error', ErrorMessage = @err
            WHERE AccountId = @id;
            """;
        cmd.Parameters.AddWithValue("@id", accountId);
        cmd.Parameters.AddWithValue("@err", errorMessage);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Update the progress counters for a history recon.
    /// </summary>
    public void UpdateHistoryReconProgress(string accountId, int songsProcessed,
                                            int seasonsQueried, int historyEntriesFound)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE HistoryReconStatus
            SET SongsProcessed = @songs, SeasonsQueried = @seasons, HistoryEntriesFound = @entries
            WHERE AccountId = @id;
            """;
        cmd.Parameters.AddWithValue("@id", accountId);
        cmd.Parameters.AddWithValue("@songs", songsProcessed);
        cmd.Parameters.AddWithValue("@seasons", seasonsQueried);
        cmd.Parameters.AddWithValue("@entries", historyEntriesFound);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Mark a specific song/instrument as processed for history recon.
    /// </summary>
    public void MarkHistoryReconSongProcessed(string accountId, string songId, string instrument)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO HistoryReconProgress (AccountId, SongId, Instrument, Processed, ProcessedAt)
            VALUES (@acct, @song, @inst, 1, @now)
            ON CONFLICT(AccountId, SongId, Instrument) DO UPDATE SET
                Processed   = 1,
                ProcessedAt = @now;
            """;
        cmd.Parameters.AddWithValue("@acct", accountId);
        cmd.Parameters.AddWithValue("@song", songId);
        cmd.Parameters.AddWithValue("@inst", instrument);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Get the set of song/instrument pairs already processed for history recon (for resumption).
    /// </summary>
    public HashSet<(string SongId, string Instrument)> GetProcessedHistoryReconPairs(string accountId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SongId, Instrument
            FROM HistoryReconProgress
            WHERE AccountId = @acct AND Processed = 1;
            """;
        cmd.Parameters.AddWithValue("@acct", accountId);

        var set = new HashSet<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            set.Add((reader.GetString(0), reader.GetString(1)));
        return set;
    }

    // ─── SeasonWindows ──────────────────────────────────────────

    /// <summary>
    /// Insert or update a season window.
    /// </summary>
    public void UpsertSeasonWindow(int seasonNumber, string eventId, string windowId)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SeasonWindows (SeasonNumber, EventId, WindowId, DiscoveredAt)
            VALUES (@season, @eventId, @windowId, @now)
            ON CONFLICT(SeasonNumber) DO UPDATE SET
                EventId  = @eventId,
                WindowId = @windowId;
            """;
        cmd.Parameters.AddWithValue("@season", seasonNumber);
        cmd.Parameters.AddWithValue("@eventId", eventId);
        cmd.Parameters.AddWithValue("@windowId", windowId);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Get all known season windows, ordered by season number.
    /// </summary>
    public List<SeasonWindowInfo> GetSeasonWindows()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SeasonNumber, EventId, WindowId, DiscoveredAt
            FROM SeasonWindows
            ORDER BY SeasonNumber;
            """;

        var list = new List<SeasonWindowInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new SeasonWindowInfo
            {
                SeasonNumber = reader.GetInt32(0),
                EventId      = reader.GetString(1),
                WindowId     = reader.GetString(2),
                DiscoveredAt = reader.GetString(3),
            });
        }
        return list;
    }

    /// <summary>
    /// Get a specific season window by season number.
    /// </summary>
    public SeasonWindowInfo? GetSeasonWindow(int seasonNumber)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SeasonNumber, EventId, WindowId, DiscoveredAt
            FROM SeasonWindows
            WHERE SeasonNumber = @season;
            """;
        cmd.Parameters.AddWithValue("@season", seasonNumber);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new SeasonWindowInfo
        {
            SeasonNumber = reader.GetInt32(0),
            EventId      = reader.GetString(1),
            WindowId     = reader.GetString(2),
            DiscoveredAt = reader.GetString(3),
        };
    }

    /// <summary>
    /// Get the highest known season number, or 0 if none are tracked.
    /// </summary>
    public int GetCurrentSeason()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(SeasonNumber), 0) FROM SeasonWindows;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static HistoryReconStatusInfo ReadHistoryReconStatus(SqliteDataReader reader)
    {
        return new HistoryReconStatusInfo
        {
            AccountId           = reader.GetString(0),
            Status              = reader.GetString(1),
            SongsProcessed      = reader.GetInt32(2),
            TotalSongsToProcess = reader.GetInt32(3),
            SeasonsQueried      = reader.GetInt32(4),
            HistoryEntriesFound = reader.GetInt32(5),
            StartedAt           = reader.IsDBNull(6) ? null : reader.GetString(6),
            CompletedAt         = reader.IsDBNull(7) ? null : reader.GetString(7),
            ErrorMessage        = reader.IsDBNull(8) ? null : reader.GetString(8),
        };
    }

    // ─── EpicUserTokens ────────────────────────────────────────

    /// <summary>
    /// Store (or update) an encrypted Epic token pair for a user.
    /// Both the access and refresh tokens are encrypted with AES-256-GCM
    /// before being passed to this method.
    /// </summary>
    public void UpsertEpicUserToken(
        string accountId,
        byte[] encryptedAccessToken,
        byte[] encryptedRefreshToken,
        DateTimeOffset tokenExpiresAt,
        DateTimeOffset refreshExpiresAt,
        byte[] nonce)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO EpicUserTokens (AccountId, EncryptedAccessToken, EncryptedRefreshToken,
                                        TokenExpiresAt, RefreshExpiresAt, Nonce, UpdatedAt)
            VALUES (@accountId, @accessToken, @refreshToken, @tokenExp, @refreshExp, @nonce, @now)
            ON CONFLICT(AccountId) DO UPDATE SET
                EncryptedAccessToken  = excluded.EncryptedAccessToken,
                EncryptedRefreshToken = excluded.EncryptedRefreshToken,
                TokenExpiresAt        = excluded.TokenExpiresAt,
                RefreshExpiresAt      = excluded.RefreshExpiresAt,
                Nonce                 = excluded.Nonce,
                UpdatedAt             = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("@accountId", accountId);
        cmd.Parameters.AddWithValue("@accessToken", encryptedAccessToken);
        cmd.Parameters.AddWithValue("@refreshToken", encryptedRefreshToken);
        cmd.Parameters.AddWithValue("@tokenExp", tokenExpiresAt.ToString("o"));
        cmd.Parameters.AddWithValue("@refreshExp", refreshExpiresAt.ToString("o"));
        cmd.Parameters.AddWithValue("@nonce", nonce);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Retrieve the encrypted token data for a user.
    /// Returns null if no tokens are stored for this account.
    /// </summary>
    public StoredEpicUserToken? GetEpicUserToken(string accountId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EncryptedAccessToken, EncryptedRefreshToken, TokenExpiresAt,
                   RefreshExpiresAt, Nonce, UpdatedAt
            FROM EpicUserTokens
            WHERE AccountId = @accountId;
            """;
        cmd.Parameters.AddWithValue("@accountId", accountId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new StoredEpicUserToken
        {
            AccountId = accountId,
            EncryptedAccessToken = (byte[])reader["EncryptedAccessToken"],
            EncryptedRefreshToken = (byte[])reader["EncryptedRefreshToken"],
            TokenExpiresAt = DateTimeOffset.Parse((string)reader["TokenExpiresAt"]),
            RefreshExpiresAt = DateTimeOffset.Parse((string)reader["RefreshExpiresAt"]),
            Nonce = (byte[])reader["Nonce"],
            UpdatedAt = (string)reader["UpdatedAt"],
        };
    }

    /// <summary>
    /// Delete all stored Epic tokens for a user (e.g. on logout or revocation).
    /// </summary>
    public void DeleteEpicUserToken(string accountId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM EpicUserTokens WHERE AccountId = @accountId;";
        cmd.Parameters.AddWithValue("@accountId", accountId);
        cmd.ExecuteNonQuery();
    }

    // ─── PlayerStats ────────────────────────────────────────────

    /// <summary>
    /// Upsert pre-computed player stats for a specific instrument (or "Overall").
    /// </summary>
    public void UpsertPlayerStats(PlayerStatsDto stats)
    {
        var now = DateTime.UtcNow.ToString("o");
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO PlayerStats (AccountId, Instrument, SongsPlayed, FullComboCount,
                    GoldStarCount, AvgAccuracy, BestRank, BestRankSongId, TotalScore,
                    PercentileDist, AvgPercentile, OverallPercentile, UpdatedAt)
                VALUES (@accountId, @instrument, @songsPlayed, @fcCount,
                    @goldStars, @avgAcc, @bestRank, @bestRankSongId, @totalScore,
                    @pctDist, @avgPct, @overallPct, @now)
                ON CONFLICT(AccountId, Instrument) DO UPDATE SET
                    SongsPlayed       = excluded.SongsPlayed,
                    FullComboCount    = excluded.FullComboCount,
                    GoldStarCount     = excluded.GoldStarCount,
                    AvgAccuracy       = excluded.AvgAccuracy,
                    BestRank          = excluded.BestRank,
                    BestRankSongId    = excluded.BestRankSongId,
                    TotalScore        = excluded.TotalScore,
                    PercentileDist    = excluded.PercentileDist,
                    AvgPercentile     = excluded.AvgPercentile,
                    OverallPercentile = excluded.OverallPercentile,
                    UpdatedAt         = excluded.UpdatedAt;
                """;
            cmd.Parameters.AddWithValue("@accountId", stats.AccountId);
            cmd.Parameters.AddWithValue("@instrument", stats.Instrument);
            cmd.Parameters.AddWithValue("@songsPlayed", stats.SongsPlayed);
            cmd.Parameters.AddWithValue("@fcCount", stats.FullComboCount);
            cmd.Parameters.AddWithValue("@goldStars", stats.GoldStarCount);
            cmd.Parameters.AddWithValue("@avgAcc", stats.AvgAccuracy);
            cmd.Parameters.AddWithValue("@bestRank", stats.BestRank);
            cmd.Parameters.AddWithValue("@bestRankSongId", (object?)stats.BestRankSongId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@totalScore", stats.TotalScore);
            cmd.Parameters.AddWithValue("@pctDist", (object?)stats.PercentileDist ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@avgPct", (object?)stats.AvgPercentile ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@overallPct", (object?)stats.OverallPercentile ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Get all pre-computed stats for a player (one row per instrument + "Overall").
    /// </summary>
    public List<PlayerStatsDto> GetPlayerStats(string accountId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Instrument, SongsPlayed, FullComboCount, GoldStarCount, AvgAccuracy,
                   BestRank, BestRankSongId, TotalScore, PercentileDist,
                   AvgPercentile, OverallPercentile, UpdatedAt
            FROM PlayerStats
            WHERE AccountId = @accountId;
            """;
        cmd.Parameters.AddWithValue("@accountId", accountId);

        var list = new List<PlayerStatsDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new PlayerStatsDto
            {
                AccountId = accountId,
                Instrument = reader.GetString(0),
                SongsPlayed = reader.GetInt32(1),
                FullComboCount = reader.GetInt32(2),
                GoldStarCount = reader.GetInt32(3),
                AvgAccuracy = reader.GetDouble(4),
                BestRank = reader.GetInt32(5),
                BestRankSongId = reader.IsDBNull(6) ? null : reader.GetString(6),
                TotalScore = reader.GetInt64(7),
                PercentileDist = reader.IsDBNull(8) ? null : reader.GetString(8),
                AvgPercentile = reader.IsDBNull(9) ? null : reader.GetString(9),
                OverallPercentile = reader.IsDBNull(10) ? null : reader.GetString(10),
            });
        }
        return list;
    }

    // ─── Helpers ────────────────────────────────────────────────

    private readonly object _writeLock = new();

    /// <summary>Long-lived connection used for write operations (same pattern as InstrumentDatabase).</summary>
    private SqliteConnection? _persistentConn;
    private readonly object _connLock = new();

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        return conn;
    }

    /// <summary>
    /// Get the long-lived persistent connection for write operations.
    /// Avoids per-call connection setup + pragma overhead.
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

    // ─── SongFirstSeenSeason ────────────────────────────────────

    /// <summary>
    /// Get the set of song IDs that already have a calculated FirstSeenSeason.
    /// </summary>
    public HashSet<string> GetSongsWithFirstSeenSeason()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SongId FROM SongFirstSeenSeason;";

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            set.Add(reader.GetString(0));
        return set;
    }

    /// <summary>
    /// Get the FirstSeenSeason for a specific song, or null if not yet calculated.
    /// </summary>
    public int? GetFirstSeenSeason(string songId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FirstSeenSeason FROM SongFirstSeenSeason WHERE SongId = @songId;";
        cmd.Parameters.AddWithValue("@songId", songId);
        var result = cmd.ExecuteScalar();
        return result is long val ? (int)val : null;
    }

    /// <summary>
    /// Store the calculated FirstSeenSeason for a song.
    /// </summary>
    public void UpsertFirstSeenSeason(string songId, int? firstSeenSeason, int? minObservedSeason, int estimatedSeason, string? probeResult)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SongFirstSeenSeason (SongId, FirstSeenSeason, MinObservedSeason, EstimatedSeason, ProbeResult, CalculatedAt)
            VALUES (@songId, @firstSeen, @minObserved, @estimated, @probeResult, @calculatedAt)
            ON CONFLICT(SongId) DO UPDATE SET
                FirstSeenSeason   = excluded.FirstSeenSeason,
                MinObservedSeason = excluded.MinObservedSeason,
                EstimatedSeason   = excluded.EstimatedSeason,
                ProbeResult       = excluded.ProbeResult,
                CalculatedAt      = excluded.CalculatedAt;
            """;
        cmd.Parameters.AddWithValue("@songId", songId);
        cmd.Parameters.AddWithValue("@firstSeen", (object?)firstSeenSeason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@minObserved", (object?)minObservedSeason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@estimated", estimatedSeason);
        cmd.Parameters.AddWithValue("@probeResult", (object?)probeResult ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@calculatedAt", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Get all FirstSeenSeason values as a dictionary for bulk lookups.
    /// </summary>
    public Dictionary<string, (int? FirstSeenSeason, int EstimatedSeason)> GetAllFirstSeenSeasons()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SongId, FirstSeenSeason, EstimatedSeason FROM SongFirstSeenSeason;";

        var dict = new Dictionary<string, (int? FirstSeenSeason, int EstimatedSeason)>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var songId = reader.GetString(0);
            int? firstSeen = reader.IsDBNull(1) ? null : reader.GetInt32(1);
            int estimated = reader.GetInt32(2);
            dict[songId] = (firstSeen, estimated);
        }
        return dict;
    }

    // ─── LeaderboardPopulation ──────────────────────────────────

    /// <summary>
    /// Raise the leaderboard population floor for a song/instrument.
    /// A user's rank is a guaranteed minimum — if they're ranked N, there are at least N entries.
    /// Only updates when the new value is higher than the existing value (one-way ratchet).
    /// </summary>
    public void RaiseLeaderboardPopulationFloor(string songId, string instrument, long floor)
    {
        if (floor <= 0) return;

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO LeaderboardPopulation (SongId, Instrument, TotalEntries, UpdatedAt)
            VALUES (@songId, @instrument, @floor, @now)
            ON CONFLICT (SongId, Instrument) DO UPDATE SET
                TotalEntries = MAX(TotalEntries, excluded.TotalEntries),
                UpdatedAt    = CASE WHEN excluded.TotalEntries > TotalEntries
                                    THEN excluded.UpdatedAt ELSE UpdatedAt END;
            """;
        cmd.Parameters.AddWithValue("@songId", songId);
        cmd.Parameters.AddWithValue("@instrument", instrument);
        cmd.Parameters.AddWithValue("@floor", floor);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Upsert leaderboard population entries (total entries per song/instrument).
    /// </summary>
    public void UpsertLeaderboardPopulation(IReadOnlyList<(string SongId, string Instrument, long TotalEntries)> items)
    {
        if (items.Count == 0) return;

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        cmd.CommandText = """
            INSERT INTO LeaderboardPopulation (SongId, Instrument, TotalEntries, UpdatedAt)
            VALUES (@songId, @instrument, @totalEntries, @now)
            ON CONFLICT (SongId, Instrument) DO UPDATE SET
                TotalEntries = @totalEntries,
                UpdatedAt    = @now;
            """;

        var pSongId = cmd.Parameters.Add("@songId", SqliteType.Text);
        var pInstrument = cmd.Parameters.Add("@instrument", SqliteType.Text);
        var pTotal = cmd.Parameters.Add("@totalEntries", SqliteType.Integer);
        var pNow = cmd.Parameters.Add("@now", SqliteType.Text);

        var now = DateTime.UtcNow.ToString("o");

        foreach (var (songId, instrument, totalEntries) in items)
        {
            pSongId.Value = songId;
            pInstrument.Value = instrument;
            pTotal.Value = totalEntries;
            pNow.Value = now;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        _log.LogDebug("Upserted {Count} LeaderboardPopulation entries.", items.Count);
    }

    /// <summary>
    /// Get the total leaderboard entries for a specific song/instrument. Returns -1 if not yet populated.
    /// </summary>
    public long GetLeaderboardPopulation(string songId, string instrument)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TotalEntries FROM LeaderboardPopulation WHERE SongId = @s AND Instrument = @i;";
        cmd.Parameters.AddWithValue("@s", songId);
        cmd.Parameters.AddWithValue("@i", instrument);
        var result = cmd.ExecuteScalar();
        return result is long l ? l : -1;
    }

    /// <summary>
    /// Get all leaderboard population data.
    /// </summary>
    public Dictionary<(string SongId, string Instrument), long> GetAllLeaderboardPopulation()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SongId, Instrument, TotalEntries FROM LeaderboardPopulation;";
        using var reader = cmd.ExecuteReader();
        var dict = new Dictionary<(string, string), long>();
        while (reader.Read())
        {
            var songId = reader.GetString(0);
            var instrument = reader.GetString(1);
            var total = reader.GetInt64(2);
            dict[(songId, instrument)] = total;
        }
        return dict;
    }

    /// <summary>
    /// Migrate SongFirstSeenSeason: if the old schema has NOT NULL on FirstSeenSeason,
    /// recreate the table with nullable columns. Idempotent — checks column nullability first.
    /// </summary>
    private void MigrateSongFirstSeenSeasonSchema(SqliteConnection conn)
    {
        // Check if FirstSeenSeason column is NOT NULL (notnull=1 in pragma_table_info)
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT \"notnull\" FROM pragma_table_info('SongFirstSeenSeason') WHERE name = 'FirstSeenSeason';";
        var notnull = check.ExecuteScalar();
        if (notnull is not long nn || nn == 0)
            return; // Already nullable or table doesn't exist

        _log.LogInformation("Migrating SongFirstSeenSeason: relaxing NOT NULL constraints + adding EstimatedSeason.");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS SongFirstSeenSeason_new (
                SongId           TEXT    PRIMARY KEY,
                FirstSeenSeason  INTEGER,
                MinObservedSeason INTEGER,
                EstimatedSeason  INTEGER NOT NULL DEFAULT 0,
                ProbeResult      TEXT,
                CalculatedAt     TEXT    NOT NULL
            );
            INSERT OR IGNORE INTO SongFirstSeenSeason_new (SongId, FirstSeenSeason, MinObservedSeason, EstimatedSeason, ProbeResult, CalculatedAt)
                SELECT SongId, FirstSeenSeason, MinObservedSeason, COALESCE(EstimatedSeason, FirstSeenSeason), ProbeResult, CalculatedAt
                FROM SongFirstSeenSeason;
            DROP TABLE SongFirstSeenSeason;
            ALTER TABLE SongFirstSeenSeason_new RENAME TO SongFirstSeenSeason;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// If the stored data-collection version is behind <see cref="DataCollectionVersion"/>,
    /// reset all completed backfill and history-reconstruction work so it re-runs
    /// with the latest collection logic.
    /// </summary>
    private void MigrateDataCollectionVersion(SqliteConnection conn)
    {
        // Read current stored version (0 if row doesn't exist yet)
        int storedVersion;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT Version FROM DataVersion WHERE Key = 'DataCollection';";
            var result = read.ExecuteScalar();
            storedVersion = result is long v ? (int)v : 0;
        }

        if (storedVersion >= DataCollectionVersion)
            return;

        _log.LogInformation(
            "Data-collection version upgraded {Old} → {New}. Re-queuing completed backfill/history-recon work.",
            storedVersion, DataCollectionVersion);

        using (var reset = conn.CreateCommand())
        {
            reset.CommandText = """
                UPDATE BackfillStatus
                   SET Status = 'pending', SongsChecked = 0, EntriesFound = 0,
                       StartedAt = NULL, CompletedAt = NULL, LastResumedAt = NULL, ErrorMessage = NULL
                 WHERE Status = 'complete';

                DELETE FROM BackfillProgress
                 WHERE AccountId IN (SELECT AccountId FROM BackfillStatus WHERE Status = 'pending');

                UPDATE HistoryReconStatus
                   SET Status = 'pending', SongsProcessed = 0, TotalSongsToProcess = 0,
                       SeasonsQueried = 0, HistoryEntriesFound = 0,
                       StartedAt = NULL, CompletedAt = NULL, ErrorMessage = NULL
                 WHERE Status = 'complete';

                DELETE FROM HistoryReconProgress
                 WHERE AccountId IN (SELECT AccountId FROM HistoryReconStatus WHERE Status = 'pending');
                """;
            reset.ExecuteNonQuery();
        }

        // Store the new version
        using (var upsert = conn.CreateCommand())
        {
            upsert.CommandText = """
                INSERT INTO DataVersion (Key, Version) VALUES ('DataCollection', @ver)
                ON CONFLICT(Key) DO UPDATE SET Version = @ver;
                """;
            upsert.Parameters.AddWithValue("@ver", DataCollectionVersion);
            upsert.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Returns the stored data-collection version, or 0 if not yet recorded.
    /// </summary>
    internal int GetDataCollectionVersion()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Version FROM DataVersion WHERE Key = 'DataCollection';";
        var result = cmd.ExecuteScalar();
        return result is long v ? (int)v : 0;
    }

    /// <summary>
    /// Idempotent migration: adds a column to a table if it doesn't already exist.
    /// </summary>
    private void MigrateAddColumn(SqliteConnection conn, string table, string column, string type)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";
        var exists = (long)(check.ExecuteScalar() ?? 0);
        if (exists > 0) return;

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
        alter.ExecuteNonQuery();

        _log.LogInformation("Migrated {Table}: added column {Column} ({Type})", table, column, type);
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

/// <summary>
/// Encrypted Epic Games user token pair retrieved from the <c>EpicUserTokens</c> table.
/// The access and refresh token fields contain AES-256-GCM ciphertext that must be
/// decrypted with the <see cref="FSTService.Auth.TokenVault"/> before use.
/// </summary>
public sealed class StoredEpicUserToken
{
    public required string AccountId { get; init; }
    public required byte[] EncryptedAccessToken { get; init; }
    public required byte[] EncryptedRefreshToken { get; init; }
    public required DateTimeOffset TokenExpiresAt { get; init; }
    public required DateTimeOffset RefreshExpiresAt { get; init; }
    public required byte[] Nonce { get; init; }
    public required string UpdatedAt { get; init; }
}
