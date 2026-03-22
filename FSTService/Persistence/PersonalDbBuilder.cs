using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using Microsoft.Data.Sqlite;

namespace FSTService.Persistence;

/// <summary>
/// Builds small (~1–2 MB) per-device personal SQLite databases containing a
/// registered user's scores across all songs and instruments. The schema matches
/// the React Native app's <c>SqliteFestivalPersistence</c> (Songs + Scores tables)
/// so the mobile app can consume them directly.
///
/// Personal DBs are stored under <c>data/personal/{accountId}/{deviceId}.db</c> and shipped
/// whole (not deltas) since the file size is trivial.
/// </summary>
public class PersonalDbBuilder
{
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly FestivalService _festivalService;
    private readonly MetaDatabase _metaDb;
    private readonly Scraping.ScrapeProgressTracker _progress;
    private readonly string _personalDir;
    private readonly ILogger<PersonalDbBuilder> _log;

    /// <summary>
    /// Maps instrument DB key (e.g. "Solo_Guitar") to the RN Scores column prefix.
    /// </summary>
    private static readonly Dictionary<string, string> InstrumentPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Solo_Guitar"]           = "Guitar",
        ["Solo_Bass"]             = "Bass",
        ["Solo_Vocals"]           = "Vocals",
        ["Solo_Drums"]            = "Drums",
        ["Solo_PeripheralGuitar"] = "ProGuitar",
        ["Solo_PeripheralBass"]   = "ProBass",
    };

    public PersonalDbBuilder(
        GlobalLeaderboardPersistence persistence,
        FestivalService festivalService,
        MetaDatabase metaDb,
        Scraping.ScrapeProgressTracker progress,
        string dataDir,
        ILogger<PersonalDbBuilder> log)
    {
        _persistence = persistence;
        _festivalService = festivalService;
        _metaDb = metaDb;
        _progress = progress;
        _personalDir = Path.Combine(dataDir, "personal");
        _log = log;

        if (!Directory.Exists(_personalDir))
            Directory.CreateDirectory(_personalDir);
    }

    /// <summary>
    /// Get the path where a device's personal DB would live.
    /// </summary>
    public string GetPersonalDbPath(string accountId, string deviceId)
    {
        var accountDir = Path.Combine(_personalDir, accountId);
        if (!Directory.Exists(accountDir))
            Directory.CreateDirectory(accountDir);
        return Path.Combine(accountDir, $"{deviceId}.db");
    }

    /// <summary>
    /// Build (or rebuild) the personal DB for a device + account pair.
    /// Creates a temp file, populates it, then atomically replaces the output.
    /// Returns the output file path, or null if no songs are available.
    /// </summary>
    public virtual string? Build(string deviceId, string accountId)
    {
        var songs = _festivalService.Songs;
        if (songs.Count == 0)
        {
            _log.LogWarning("Cannot build personal DB for device {DeviceId}: no songs loaded.", deviceId);
            return null;
        }

        var outputPath = GetPersonalDbPath(accountId, deviceId);
        var tempPath = outputPath + ".tmp";

        try
        {
            // Remove stale temp file if present
            if (File.Exists(tempPath))
                File.Delete(tempPath);

            BuildDatabase(tempPath, accountId, songs);

            // Atomic replace: delete old, rename temp → final
            if (File.Exists(outputPath))
                File.Delete(outputPath);
            File.Move(tempPath, outputPath);

            var size = new FileInfo(outputPath).Length;
            _log.LogInformation(
                "Built personal DB for device {DeviceId} / account {AccountId}: {Size} bytes",
                deviceId, accountId, size);

            return outputPath;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to build personal DB for device {DeviceId}.", deviceId);

            // Clean up temp file on failure
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* best-effort cleanup */ }

            return null;
        }
    }

    /// <summary>
    /// Build personal DBs for all devices registered to any of the given account IDs.
    /// Since all devices for an account receive the same content, the DB is built once
    /// per account and then copied to each device path.
    /// Returns the number of device DBs written.
    /// </summary>
    public virtual int RebuildForAccounts(IReadOnlySet<string> changedAccountIds, MetaDatabase metaDb)
    {
        if (changedAccountIds.Count == 0) return 0;

        var deviceMappings = metaDb.GetDeviceAccountMappings();

        // Group devices by account so we build once per account
        var accountDevices = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (deviceId, accountId) in deviceMappings)
        {
            if (!changedAccountIds.Contains(accountId))
                continue;

            if (!accountDevices.TryGetValue(accountId, out var devices))
            {
                devices = new List<string>();
                accountDevices[accountId] = devices;
            }
            devices.Add(deviceId);
        }

        int rebuilt = 0;

        foreach (var (accountId, devices) in accountDevices)
        {
            // Build for the first device
            var firstDevice = devices[0];
            var result = Build(firstDevice, accountId);
            if (result is null)
                continue;

            rebuilt++;

            // Copy to remaining devices
            for (int i = 1; i < devices.Count; i++)
            {
                var destPath = GetPersonalDbPath(accountId, devices[i]);
                try
                {
                    File.Copy(result, destPath, overwrite: true);
                    rebuilt++;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to copy personal DB for device {DeviceId}.", devices[i]);
                }
            }

            _progress.ReportPhaseItemComplete();
        }

        return rebuilt;
    }

    /// <summary>
    /// Check whether a personal DB exists for a device and return its last-modified
    /// time as a version string (ISO 8601), or null if not yet built.
    /// </summary>
    public (string? version, long? sizeBytes) GetVersion(string accountId, string deviceId)
    {
        var path = GetPersonalDbPath(accountId, deviceId);
        if (!File.Exists(path))
            return (null, null);

        var info = new FileInfo(path);
        return (info.LastWriteTimeUtc.ToString("o"), info.Length);
    }

    // ─── Private implementation ─────────────────────────────────

    private void BuildDatabase(string dbPath, string accountId, IReadOnlyList<Song> songs)
    {
        // Disable connection pooling so the file handle is fully released
        // after conn.Dispose(), allowing the subsequent File.Move to succeed.
        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();

        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }

        CreateSchema(conn);
        PopulateSongs(conn, songs);
        PopulateScores(conn, accountId, songs);
        PopulateScoreHistory(conn, accountId);
        PopulateRivals(conn, accountId);
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Songs (
                SongId TEXT PRIMARY KEY,
                Title TEXT,
                Artist TEXT,
                ActiveDate TEXT,
                LastModified TEXT,
                ImagePath TEXT,
                LeadDiff INTEGER,
                BassDiff INTEGER,
                VocalsDiff INTEGER,
                DrumsDiff INTEGER,
                ProLeadDiff INTEGER,
                ProBassDiff INTEGER,
                ReleaseYear INTEGER,
                Tempo INTEGER,
                PlasticGuitarDiff INTEGER,
                PlasticBassDiff INTEGER,
                PlasticDrumsDiff INTEGER,
                ProVocalsDiff INTEGER
            );

            CREATE TABLE IF NOT EXISTS ScoreHistory (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                SongId      TEXT    NOT NULL,
                Instrument  TEXT    NOT NULL,
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

            CREATE INDEX IF NOT EXISTS IX_ScoreHist_Song ON ScoreHistory (SongId, Instrument);

            CREATE TABLE IF NOT EXISTS Scores (
                SongId TEXT PRIMARY KEY,
                GuitarScore INTEGER, GuitarDiff INTEGER, GuitarStars INTEGER, GuitarFC INTEGER, GuitarPct INTEGER, GuitarSeason INTEGER, GuitarRank INTEGER, GuitarGameDiff INTEGER,
                DrumsScore INTEGER, DrumsDiff INTEGER, DrumsStars INTEGER, DrumsFC INTEGER, DrumsPct INTEGER, DrumsSeason INTEGER, DrumsRank INTEGER, DrumsGameDiff INTEGER,
                BassScore INTEGER, BassDiff INTEGER, BassStars INTEGER, BassFC INTEGER, BassPct INTEGER, BassSeason INTEGER, BassRank INTEGER, BassGameDiff INTEGER,
                VocalsScore INTEGER, VocalsDiff INTEGER, VocalsStars INTEGER, VocalsFC INTEGER, VocalsPct INTEGER, VocalsSeason INTEGER, VocalsRank INTEGER, VocalsGameDiff INTEGER,
                ProGuitarScore INTEGER, ProGuitarDiff INTEGER, ProGuitarStars INTEGER, ProGuitarFC INTEGER, ProGuitarPct INTEGER, ProGuitarSeason INTEGER, ProGuitarRank INTEGER, ProGuitarGameDiff INTEGER,
                ProBassScore INTEGER, ProBassDiff INTEGER, ProBassStars INTEGER, ProBassFC INTEGER, ProBassPct INTEGER, ProBassSeason INTEGER, ProBassRank INTEGER, ProBassGameDiff INTEGER,
                GuitarTotal INTEGER, DrumsTotal INTEGER, BassTotal INTEGER, VocalsTotal INTEGER, ProGuitarTotal INTEGER, ProBassTotal INTEGER,
                GuitarRawPct REAL, DrumsRawPct REAL, BassRawPct REAL, VocalsRawPct REAL, ProGuitarRawPct REAL, ProBassRawPct REAL,
                GuitarCalcTotal INTEGER, DrumsCalcTotal INTEGER, BassCalcTotal INTEGER, VocalsCalcTotal INTEGER, ProGuitarCalcTotal INTEGER, ProBassCalcTotal INTEGER,
                FOREIGN KEY (SongId) REFERENCES Songs(SongId)
            );

            CREATE TABLE IF NOT EXISTS Rivals (
                RivalAccountId  TEXT    NOT NULL,
                DisplayName     TEXT,
                InstrumentCombo TEXT    NOT NULL,
                Direction       TEXT    NOT NULL,
                RivalScore      REAL    NOT NULL,
                AvgSignedDelta  REAL    NOT NULL,
                SharedSongCount INTEGER NOT NULL,
                AheadCount      INTEGER NOT NULL,
                BehindCount     INTEGER NOT NULL,
                PRIMARY KEY (RivalAccountId, InstrumentCombo)
            );

            CREATE TABLE IF NOT EXISTS RivalSongSamples (
                RivalAccountId  TEXT    NOT NULL,
                Instrument      TEXT    NOT NULL,
                SongId          TEXT    NOT NULL,
                UserRank        INTEGER NOT NULL,
                RivalRank       INTEGER NOT NULL,
                RankDelta       INTEGER NOT NULL,
                UserScore       INTEGER,
                RivalScore      INTEGER,
                PRIMARY KEY (RivalAccountId, Instrument, SongId)
            );

            CREATE TABLE IF NOT EXISTS RivalCombos (
                InstrumentCombo TEXT    PRIMARY KEY,
                AboveCount      INTEGER NOT NULL,
                BelowCount      INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static void PopulateSongs(SqliteConnection conn, IReadOnlyList<Song> songs)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO Songs
                (SongId, Title, Artist, ActiveDate, LastModified, LeadDiff, BassDiff,
                 VocalsDiff, DrumsDiff, ProLeadDiff, ProBassDiff, ReleaseYear, Tempo,
                 PlasticGuitarDiff, PlasticBassDiff, PlasticDrumsDiff, ProVocalsDiff)
            VALUES
                (@songId, @title, @artist, @activeDate, @lastModified, @leadDiff, @bassDiff,
                 @vocalsDiff, @drumsDiff, @proLeadDiff, @proBassDiff, @releaseYear, @tempo,
                 @plasticGuitarDiff, @plasticBassDiff, @plasticDrumsDiff, @proVocalsDiff);
            """;

        var pSongId           = cmd.Parameters.Add("@songId", SqliteType.Text);
        var pTitle            = cmd.Parameters.Add("@title", SqliteType.Text);
        var pArtist           = cmd.Parameters.Add("@artist", SqliteType.Text);
        var pActiveDate       = cmd.Parameters.Add("@activeDate", SqliteType.Text);
        var pLastModified     = cmd.Parameters.Add("@lastModified", SqliteType.Text);
        var pLeadDiff         = cmd.Parameters.Add("@leadDiff", SqliteType.Integer);
        var pBassDiff         = cmd.Parameters.Add("@bassDiff", SqliteType.Integer);
        var pVocalsDiff       = cmd.Parameters.Add("@vocalsDiff", SqliteType.Integer);
        var pDrumsDiff        = cmd.Parameters.Add("@drumsDiff", SqliteType.Integer);
        var pProLeadDiff      = cmd.Parameters.Add("@proLeadDiff", SqliteType.Integer);
        var pProBassDiff      = cmd.Parameters.Add("@proBassDiff", SqliteType.Integer);
        var pReleaseYear      = cmd.Parameters.Add("@releaseYear", SqliteType.Integer);
        var pTempo            = cmd.Parameters.Add("@tempo", SqliteType.Integer);
        var pPlasticGuitarDiff = cmd.Parameters.Add("@plasticGuitarDiff", SqliteType.Integer);
        var pPlasticBassDiff  = cmd.Parameters.Add("@plasticBassDiff", SqliteType.Integer);
        var pPlasticDrumsDiff = cmd.Parameters.Add("@plasticDrumsDiff", SqliteType.Integer);
        var pProVocalsDiff    = cmd.Parameters.Add("@proVocalsDiff", SqliteType.Integer);
        cmd.Prepare();

        foreach (var song in songs)
        {
            if (song.track?.su is null) continue;

            pSongId.Value       = song.track.su;
            pTitle.Value        = (object?)song.track.tt ?? DBNull.Value;
            pArtist.Value       = (object?)song.track.an ?? DBNull.Value;
            pActiveDate.Value   = song._activeDate != default
                ? song._activeDate.ToString("o")
                : (object)DBNull.Value;
            pLastModified.Value = song.lastModified != default
                ? song.lastModified.ToString("o")
                : (object)DBNull.Value;
            pLeadDiff.Value     = song.track.@in?.gr ?? 0;
            pBassDiff.Value     = song.track.@in?.ba ?? 0;
            pVocalsDiff.Value   = song.track.@in?.vl ?? 0;
            pDrumsDiff.Value    = song.track.@in?.ds ?? 0;
            pProLeadDiff.Value  = song.track.@in?.pg ?? 0;
            pProBassDiff.Value  = song.track.@in?.pb ?? 0;
            pReleaseYear.Value  = song.track.ry;
            pTempo.Value        = song.track.mt;
            pPlasticGuitarDiff.Value = song.track.@in?.pg ?? 0;
            pPlasticBassDiff.Value   = song.track.@in?.pb ?? 0;
            pPlasticDrumsDiff.Value  = song.track.@in?.pd ?? 0;
            pProVocalsDiff.Value     = song.track.@in?.bd ?? 0;

            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private void PopulateScores(SqliteConnection conn, string accountId, IReadOnlyList<Song> songs)
    {
        // Gather all scores for this account from every instrument DB
        var scoresByInstrument = new Dictionary<string, Dictionary<string, PlayerScoreDto>>(
            StringComparer.OrdinalIgnoreCase);

        // Also gather per-song rankings (local rank + total count in our DB)
        var rankingsByInstrument = new Dictionary<string, Dictionary<string, (int Rank, int Total)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var instrumentKey in _persistence.GetInstrumentKeys())
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrumentKey);
            var playerScores = db.GetPlayerScores(accountId);

            var bySong = new Dictionary<string, PlayerScoreDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var score in playerScores)
                bySong[score.SongId] = score;

            scoresByInstrument[instrumentKey] = bySong;
            rankingsByInstrument[instrumentKey] = db.GetPlayerRankings(accountId);
        }

        // Load real leaderboard population from PercentileService
        var population = _metaDb.GetAllLeaderboardPopulation();

        // Build INSERT statement matching the RN schema column order:
        //   Per-instrument inline: Score, Diff, Stars, FC, Pct, Season, Rank, GameDiff
        //   Then grouped:          {Inst}Total, {Inst}RawPct, {Inst}CalcTotal
        var prefixes = new[] { "Guitar", "Drums", "Bass", "Vocals", "ProGuitar", "ProBass" };

        var columns = new List<string> { "SongId" };
        var parameters = new List<string> { "@songId" };

        foreach (var prefix in prefixes)
        {
            foreach (var suffix in new[] { "Score", "Diff", "Stars", "FC", "Pct", "Season", "Rank", "GameDiff" })
            {
                columns.Add($"{prefix}{suffix}");
                parameters.Add($"@{prefix}{suffix}");
            }
        }

        // Grouped columns: Total, RawPct, CalcTotal — each group across all instruments
        foreach (var prefix in prefixes)
        {
            columns.Add($"{prefix}Total");
            parameters.Add($"@{prefix}Total");
        }
        foreach (var prefix in prefixes)
        {
            columns.Add($"{prefix}RawPct");
            parameters.Add($"@{prefix}RawPct");
        }
        foreach (var prefix in prefixes)
        {
            columns.Add($"{prefix}CalcTotal");
            parameters.Add($"@{prefix}CalcTotal");
        }

        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT OR REPLACE INTO Scores ({string.Join(", ", columns)}) VALUES ({string.Join(", ", parameters)});";

        // Pre-add all parameters
        var paramMap = new Dictionary<string, SqliteParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parameters)
        {
            var sqlParam = cmd.Parameters.Add(p, p.Contains("RawPct") ? SqliteType.Real : SqliteType.Text);
            paramMap[p] = sqlParam;
        }

        cmd.Prepare();

        int songsWithScores = 0;

        foreach (var song in songs)
        {
            if (song.track?.su is null) continue;

            var songId = song.track.su;
            bool hasAnyScore = false;

            // Check if this account has any scores for this song
            foreach (var (instrumentKey, bySong) in scoresByInstrument)
            {
                if (bySong.ContainsKey(songId))
                {
                    hasAnyScore = true;
                    break;
                }
            }

            if (!hasAnyScore) continue;

            // Reset all params to DBNull
            foreach (var p in paramMap.Values)
                p.Value = DBNull.Value;

            paramMap["@songId"].Value = songId;

            // Fill in scores per instrument
            foreach (var (instrumentKey, prefix) in InstrumentPrefixes)
            {
                if (!scoresByInstrument.TryGetValue(instrumentKey, out var bySong))
                    continue;
                if (!bySong.TryGetValue(songId, out var score))
                    continue;

                var diff = GetDifficultyForInstrument(song, instrumentKey);

                // Rank from V2 API enrichment (stored in instrument DB), or from local count
                int rank = score.Rank;
                if (rank <= 0 && rankingsByInstrument.TryGetValue(instrumentKey, out var rankings)
                    && rankings.TryGetValue(songId, out var rk))
                {
                    rank = rk.Rank;
                }

                // Total entries: prefer real population from PercentileService,
                // fall back to local DB entry count
                long total = 0;
                if (population.TryGetValue((songId, instrumentKey), out var realPop) && realPop > 0)
                {
                    total = realPop;
                }
                else if (rankingsByInstrument.TryGetValue(instrumentKey, out var fallbackRankings)
                    && fallbackRankings.TryGetValue(songId, out var fallbackRk))
                {
                    total = fallbackRk.Total;
                }

                // Reverse-calculate total from rank/percentile for CalcTotal
                double rawPct = score.Percentile > 0 ? score.Percentile : 0;
                long calcTotal = 0;
                if (rank > 0 && rawPct > 1e-9)
                {
                    calcTotal = (long)Math.Round(rank / rawPct);
                    if (calcTotal < rank) calcTotal = rank;
                    if (calcTotal > 10_000_000) calcTotal = 10_000_000;
                }

                paramMap[$"@{prefix}Score"].Value    = score.Score;
                paramMap[$"@{prefix}Diff"].Value     = diff;
                paramMap[$"@{prefix}Stars"].Value    = score.Stars;
                paramMap[$"@{prefix}FC"].Value       = score.IsFullCombo ? 1 : 0;
                paramMap[$"@{prefix}Pct"].Value      = score.Accuracy;
                paramMap[$"@{prefix}Season"].Value   = score.Season;
                paramMap[$"@{prefix}Rank"].Value     = rank;
                paramMap[$"@{prefix}GameDiff"].Value = -1;
                paramMap[$"@{prefix}Total"].Value    = total;
                paramMap[$"@{prefix}RawPct"].Value   = rawPct;
                paramMap[$"@{prefix}CalcTotal"].Value = calcTotal;
            }

            cmd.ExecuteNonQuery();
            songsWithScores++;
        }

        tx.Commit();
        _log.LogDebug("Populated {Count} Score rows for account {AccountId}.", songsWithScores, accountId);
    }

    private void PopulateScoreHistory(SqliteConnection conn, string accountId)
    {
        var history = _metaDb.GetScoreHistory(accountId, int.MaxValue);
        if (history.Count == 0) return;

        // GetScoreHistory returns newest-first; reverse for chronological insertion
        // so personal DB Ids match chronological order.
        history.Reverse();

        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO ScoreHistory
                (SongId, Instrument, OldScore, NewScore, OldRank, NewRank,
                 Accuracy, IsFullCombo, Stars, Percentile, Season, ScoreAchievedAt,
                 SeasonRank, AllTimeRank, ChangedAt)
            VALUES
                (@songId, @instrument, @oldScore, @newScore, @oldRank, @newRank,
                 @accuracy, @isFullCombo, @stars, @percentile, @season, @scoreAchievedAt,
                 @seasonRank, @allTimeRank, @changedAt);
            """;

        var pSongId      = cmd.Parameters.Add("@songId", SqliteType.Text);
        var pInstrument  = cmd.Parameters.Add("@instrument", SqliteType.Text);
        var pOldScore    = cmd.Parameters.Add("@oldScore", SqliteType.Integer);
        var pNewScore    = cmd.Parameters.Add("@newScore", SqliteType.Integer);
        var pOldRank     = cmd.Parameters.Add("@oldRank", SqliteType.Integer);
        var pNewRank     = cmd.Parameters.Add("@newRank", SqliteType.Integer);
        var pAccuracy    = cmd.Parameters.Add("@accuracy", SqliteType.Integer);
        var pIsFullCombo = cmd.Parameters.Add("@isFullCombo", SqliteType.Integer);
        var pStars       = cmd.Parameters.Add("@stars", SqliteType.Integer);
        var pPercentile  = cmd.Parameters.Add("@percentile", SqliteType.Real);
        var pSeason      = cmd.Parameters.Add("@season", SqliteType.Integer);
        var pScoreAt     = cmd.Parameters.Add("@scoreAchievedAt", SqliteType.Text);
        var pSeasonRank  = cmd.Parameters.Add("@seasonRank", SqliteType.Integer);
        var pAllTimeRank = cmd.Parameters.Add("@allTimeRank", SqliteType.Integer);
        var pChangedAt   = cmd.Parameters.Add("@changedAt", SqliteType.Text);
        cmd.Prepare();

        foreach (var entry in history)
        {
            pSongId.Value      = entry.SongId;
            pInstrument.Value  = entry.Instrument;
            pOldScore.Value    = (object?)entry.OldScore ?? DBNull.Value;
            pNewScore.Value    = entry.NewScore;
            pOldRank.Value     = (object?)entry.OldRank ?? DBNull.Value;
            pNewRank.Value     = entry.NewRank;
            pAccuracy.Value    = (object?)entry.Accuracy ?? DBNull.Value;
            pIsFullCombo.Value = entry.IsFullCombo.HasValue ? (entry.IsFullCombo.Value ? 1 : 0) : DBNull.Value;
            pStars.Value       = (object?)entry.Stars ?? DBNull.Value;
            pPercentile.Value  = (object?)entry.Percentile ?? DBNull.Value;
            pSeason.Value      = (object?)entry.Season ?? DBNull.Value;
            pScoreAt.Value     = (object?)entry.ScoreAchievedAt ?? DBNull.Value;
            pSeasonRank.Value  = (object?)entry.SeasonRank ?? DBNull.Value;
            pAllTimeRank.Value = (object?)entry.AllTimeRank ?? DBNull.Value;
            pChangedAt.Value   = entry.ChangedAt;

            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        _log.LogDebug("Populated {Count} ScoreHistory rows for account {AccountId}.",
            history.Count, accountId);
    }

    private void PopulateRivals(SqliteConnection conn, string accountId)
    {
        var combos = _metaDb.GetRivalCombos(accountId);
        if (combos.Count == 0) return;

        var rivals = _metaDb.GetUserRivals(accountId);
        if (rivals.Count == 0) return;

        using var tx = conn.BeginTransaction();

        // Populate RivalCombos
        using (var comboCmd = conn.CreateCommand())
        {
            comboCmd.Transaction = tx;
            comboCmd.CommandText = "INSERT INTO RivalCombos (InstrumentCombo, AboveCount, BelowCount) VALUES (@combo, @above, @below);";
            var pCombo = comboCmd.Parameters.Add("@combo", SqliteType.Text);
            var pAbove = comboCmd.Parameters.Add("@above", SqliteType.Integer);
            var pBelow = comboCmd.Parameters.Add("@below", SqliteType.Integer);

            foreach (var c in combos)
            {
                pCombo.Value = c.InstrumentCombo;
                pAbove.Value = c.AboveCount;
                pBelow.Value = c.BelowCount;
                comboCmd.ExecuteNonQuery();
            }
        }

        // Populate Rivals
        using (var rivalCmd = conn.CreateCommand())
        {
            rivalCmd.Transaction = tx;
            rivalCmd.CommandText = """
                INSERT INTO Rivals (RivalAccountId, DisplayName, InstrumentCombo, Direction,
                                    RivalScore, AvgSignedDelta, SharedSongCount, AheadCount, BehindCount)
                VALUES (@rid, @name, @combo, @dir, @score, @delta, @songs, @ahead, @behind);
                """;
            var pRid = rivalCmd.Parameters.Add("@rid", SqliteType.Text);
            var pName = rivalCmd.Parameters.Add("@name", SqliteType.Text);
            var pCombo2 = rivalCmd.Parameters.Add("@combo", SqliteType.Text);
            var pDir = rivalCmd.Parameters.Add("@dir", SqliteType.Text);
            var pScore = rivalCmd.Parameters.Add("@score", SqliteType.Real);
            var pDelta = rivalCmd.Parameters.Add("@delta", SqliteType.Real);
            var pSongs = rivalCmd.Parameters.Add("@songs", SqliteType.Integer);
            var pAhead = rivalCmd.Parameters.Add("@ahead", SqliteType.Integer);
            var pBehind = rivalCmd.Parameters.Add("@behind", SqliteType.Integer);

            foreach (var r in rivals)
            {
                pRid.Value = r.RivalAccountId;
                pName.Value = (object?)_metaDb.GetDisplayName(r.RivalAccountId) ?? DBNull.Value;
                pCombo2.Value = r.InstrumentCombo;
                pDir.Value = r.Direction;
                pScore.Value = r.RivalScore;
                pDelta.Value = r.AvgSignedDelta;
                pSongs.Value = r.SharedSongCount;
                pAhead.Value = r.AheadCount;
                pBehind.Value = r.BehindCount;
                rivalCmd.ExecuteNonQuery();
            }
        }

        // Populate RivalSongSamples for all distinct rivals
        var rivalIds = rivals.Select(r => r.RivalAccountId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        using (var sampleCmd = conn.CreateCommand())
        {
            sampleCmd.Transaction = tx;
            sampleCmd.CommandText = """
                INSERT INTO RivalSongSamples (RivalAccountId, Instrument, SongId,
                                              UserRank, RivalRank, RankDelta, UserScore, RivalScore)
                VALUES (@rid, @inst, @sid, @ur, @rr, @rd, @us, @rs);
                """;
            var sRid = sampleCmd.Parameters.Add("@rid", SqliteType.Text);
            var sInst = sampleCmd.Parameters.Add("@inst", SqliteType.Text);
            var sSid = sampleCmd.Parameters.Add("@sid", SqliteType.Text);
            var sUr = sampleCmd.Parameters.Add("@ur", SqliteType.Integer);
            var sRr = sampleCmd.Parameters.Add("@rr", SqliteType.Integer);
            var sRd = sampleCmd.Parameters.Add("@rd", SqliteType.Integer);
            var sUs = sampleCmd.Parameters.Add("@us", SqliteType.Integer);
            var sRs = sampleCmd.Parameters.Add("@rs", SqliteType.Integer);

            foreach (var rivalId in rivalIds)
            {
                var samples = _metaDb.GetRivalSongSamples(accountId, rivalId);
                foreach (var s in samples)
                {
                    sRid.Value = s.RivalAccountId;
                    sInst.Value = s.Instrument;
                    sSid.Value = s.SongId;
                    sUr.Value = s.UserRank;
                    sRr.Value = s.RivalRank;
                    sRd.Value = s.RankDelta;
                    sUs.Value = (object?)s.UserScore ?? DBNull.Value;
                    sRs.Value = (object?)s.RivalScore ?? DBNull.Value;
                    sampleCmd.ExecuteNonQuery();
                }
            }
        }

        tx.Commit();
        _log.LogDebug("Populated {Count} Rival rows and {SampleRids} rival accounts for {AccountId}.",
            rivals.Count, rivalIds.Count, accountId);
    }

    private static int GetDifficultyForInstrument(Song song, string instrumentKey)
    {
        if (song.track?.@in is null) return 0;
        var i = song.track.@in;

        return instrumentKey switch
        {
            "Solo_Guitar"           => i.gr,
            "Solo_Bass"             => i.ba,
            "Solo_Vocals"           => i.vl,
            "Solo_Drums"            => i.ds,
            "Solo_PeripheralGuitar" => i.pg,
            "Solo_PeripheralBass"   => i.pb,
            _ => 0,
        };
    }

    // ─── Paged JSON sync support ──────────────────────────────────

    /// <summary>
    /// Returns a page of songs for the JSON sync endpoint.
    /// </summary>
    public PagedResult? GetSongsAsJson(int page, int pageSize)
    {
        var songs = _festivalService.Songs;
        if (songs.Count == 0) return null;

        var allRows = new List<Dictionary<string, object?>>(songs.Count);
        foreach (var song in songs)
        {
            if (song.track?.su is null) continue;
            allRows.Add(new Dictionary<string, object?>
            {
                ["SongId"]       = song.track.su,
                ["Title"]        = song.track.tt,
                ["Artist"]       = song.track.an,
                ["ActiveDate"]   = song._activeDate != default ? song._activeDate.ToString("o") : null,
                ["LastModified"] = song.lastModified != default ? song.lastModified.ToString("o") : null,
                ["ImagePath"]    = null,
                ["LeadDiff"]     = song.track.@in?.gr ?? 0,
                ["BassDiff"]     = song.track.@in?.ba ?? 0,
                ["VocalsDiff"]   = song.track.@in?.vl ?? 0,
                ["DrumsDiff"]    = song.track.@in?.ds ?? 0,
                ["ProLeadDiff"]  = song.track.@in?.pg ?? 0,
                ["ProBassDiff"]  = song.track.@in?.pb ?? 0,
                ["ReleaseYear"]  = song.track.ry,
                ["Tempo"]        = song.track.mt,
            });
        }

        return PagedResult.FromAll(allRows, page, pageSize);
    }

    /// <summary>
    /// Returns a page of scores for the given account.
    /// </summary>
    public PagedResult? GetScoresAsJson(string accountId, int page, int pageSize)
    {
        var songs = _festivalService.Songs;
        if (songs.Count == 0) return null;

        // Gather scores per instrument (includes Rank and Percentile from DB)
        var scoresByInstrument = new Dictionary<string, Dictionary<string, PlayerScoreDto>>(
            StringComparer.OrdinalIgnoreCase);
        var rankingsByInstrument = new Dictionary<string, Dictionary<string, (int Rank, int Total)>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var instrumentKey in _persistence.GetInstrumentKeys())
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrumentKey);
            var playerScores = db.GetPlayerScores(accountId);
            var bySong = new Dictionary<string, PlayerScoreDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var score in playerScores)
                bySong[score.SongId] = score;
            scoresByInstrument[instrumentKey] = bySong;
            rankingsByInstrument[instrumentKey] = db.GetPlayerRankings(accountId);
        }

        // Load real leaderboard population from PercentileService
        var population = _metaDb.GetAllLeaderboardPopulation();

        var allRows = new List<Dictionary<string, object?>>();
        foreach (var song in songs)
        {
            if (song.track?.su is null) continue;
            var songId = song.track.su;

            bool hasAny = false;
            foreach (var (_, bySong) in scoresByInstrument)
            {
                if (bySong.ContainsKey(songId)) { hasAny = true; break; }
            }
            if (!hasAny) continue;

            var row = new Dictionary<string, object?> { ["SongId"] = songId, ["Title"] = song.track.tt, ["Artist"] = song.track.an };

            foreach (var (instrumentKey, prefix) in InstrumentPrefixes)
            {
                if (scoresByInstrument.TryGetValue(instrumentKey, out var bySong) && bySong.TryGetValue(songId, out var score))
                {
                    var diff = GetDifficultyForInstrument(song, instrumentKey);

                    // Rank from API enrichment (stored in instrument DB). 0 = not yet enriched.
                    int rank = score.Rank;
                    if (rank <= 0 && rankingsByInstrument.TryGetValue(instrumentKey, out var rankings)
                        && rankings.TryGetValue(songId, out var rk))
                    {
                        rank = rk.Rank;
                    }

                    // Percentile from V1 API with teamAccountIds (real fraction in (0,1]).
                    double rawPct = score.Percentile > 0 ? score.Percentile : 0;

                    // Total entries: prefer real population from PercentileService,
                    // fall back to local DB entry count
                    long total = 0;
                    if (population.TryGetValue((songId, instrumentKey), out var realPop) && realPop > 0)
                    {
                        total = realPop;
                    }
                    else if (rankingsByInstrument.TryGetValue(instrumentKey, out var fallbackRankings)
                        && fallbackRankings.TryGetValue(songId, out var fallbackRk))
                    {
                        total = fallbackRk.Total;
                    }

                    // Reverse-calculate total entries from rank/percentile
                    int calcTotal = 0;
                    if (rank > 0 && rawPct > 1e-9)
                    {
                        calcTotal = (int)Math.Round(rank / rawPct);
                        if (calcTotal < rank) calcTotal = rank;
                        if (calcTotal > 10_000_000) calcTotal = 10_000_000;
                    }

                    row[$"{prefix}Score"]    = score.Score;
                    row[$"{prefix}Diff"]     = diff;
                    row[$"{prefix}Stars"]    = score.Stars;
                    row[$"{prefix}FC"]       = score.IsFullCombo ? 1 : 0;
                    row[$"{prefix}Pct"]      = score.Accuracy;
                    row[$"{prefix}Season"]   = score.Season;
                    row[$"{prefix}Rank"]     = rank;
                    row[$"{prefix}GameDiff"] = -1;
                    row[$"{prefix}Total"]    = total;
                    row[$"{prefix}RawPct"]   = rawPct;
                    row[$"{prefix}CalcTotal"]= calcTotal;
                }
            }

            allRows.Add(row);
        }

        return PagedResult.FromAll(allRows, page, pageSize);
    }

    /// <summary>
    /// Returns a page of score history entries for the given account.
    /// </summary>
    public PagedResult? GetHistoryAsJson(string accountId, int page, int pageSize)
    {
        var history = _metaDb.GetScoreHistory(accountId, int.MaxValue);
        history.Reverse(); // newest-first → chronological

        var allRows = new List<Dictionary<string, object?>>(history.Count);
        foreach (var e in history)
        {
            allRows.Add(new Dictionary<string, object?>
            {
                ["SongId"]         = e.SongId,
                ["Instrument"]     = e.Instrument,
                ["OldScore"]       = e.OldScore,
                ["NewScore"]       = e.NewScore,
                ["OldRank"]        = e.OldRank,
                ["NewRank"]        = e.NewRank,
                ["Accuracy"]       = e.Accuracy,
                ["IsFullCombo"]    = e.IsFullCombo.HasValue ? (e.IsFullCombo.Value ? 1 : 0) : null,
                ["Stars"]          = e.Stars,
                ["Percentile"]     = e.Percentile,
                ["Season"]         = e.Season,
                ["ScoreAchievedAt"]= e.ScoreAchievedAt,
                ["SeasonRank"]     = e.SeasonRank,
                ["AllTimeRank"]    = e.AllTimeRank,
                ["ChangedAt"]      = e.ChangedAt,
            });
        }

        return PagedResult.FromAll(allRows, page, pageSize);
    }
}

/// <summary>
/// A single page of JSON sync results.
/// </summary>
public class PagedResult
{
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalItems { get; init; }
    public int TotalPages { get; init; }
    public List<Dictionary<string, object?>> Items { get; init; } = new();

    public static PagedResult FromAll(List<Dictionary<string, object?>> all, int page, int pageSize)
    {
        var totalItems = all.Count;
        var totalPages = (int)Math.Ceiling((double)totalItems / pageSize);
        var items = all.Skip(page * pageSize).Take(pageSize).ToList();

        return new PagedResult
        {
            Page = page,
            PageSize = pageSize,
            TotalItems = totalItems,
            TotalPages = totalPages,
            Items = items,
        };
    }
}
