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
/// Personal DBs are stored under <c>data/personal/{deviceId}.db</c> and shipped
/// whole (not deltas) since the file size is trivial.
/// </summary>
public class PersonalDbBuilder
{
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly FestivalService _festivalService;
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
        string dataDir,
        ILogger<PersonalDbBuilder> log)
    {
        _persistence = persistence;
        _festivalService = festivalService;
        _personalDir = Path.Combine(dataDir, "personal");
        _log = log;

        if (!Directory.Exists(_personalDir))
            Directory.CreateDirectory(_personalDir);
    }

    /// <summary>
    /// Get the path where a device's personal DB would live.
    /// </summary>
    public string GetPersonalDbPath(string deviceId)
        => Path.Combine(_personalDir, $"{deviceId}.db");

    /// <summary>
    /// Build (or rebuild) the personal DB for a device + account pair.
    /// Creates a temp file, populates it, then atomically replaces the output.
    /// Returns the output file path, or null if no songs are available.
    /// </summary>
    public string? Build(string deviceId, string accountId)
    {
        var songs = _festivalService.Songs;
        if (songs.Count == 0)
        {
            _log.LogWarning("Cannot build personal DB for device {DeviceId}: no songs loaded.", deviceId);
            return null;
        }

        var outputPath = GetPersonalDbPath(deviceId);
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
    /// Returns the number of DBs rebuilt.
    /// </summary>
    public virtual int RebuildForAccounts(IReadOnlySet<string> changedAccountIds, MetaDatabase metaDb)
    {
        if (changedAccountIds.Count == 0) return 0;

        var deviceMappings = metaDb.GetDeviceAccountMappings();
        int rebuilt = 0;

        foreach (var (deviceId, accountId) in deviceMappings)
        {
            if (!changedAccountIds.Contains(accountId))
                continue;

            var result = Build(deviceId, accountId);
            if (result is not null)
                rebuilt++;
        }

        return rebuilt;
    }

    /// <summary>
    /// Check whether a personal DB exists for a device and return its last-modified
    /// time as a version string (ISO 8601), or null if not yet built.
    /// </summary>
    public (string? version, long? sizeBytes) GetVersion(string deviceId)
    {
        var path = GetPersonalDbPath(deviceId);
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

        foreach (var instrumentKey in _persistence.GetInstrumentKeys())
        {
            var db = _persistence.GetOrCreateInstrumentDb(instrumentKey);
            var playerScores = db.GetPlayerScores(accountId);

            var bySong = new Dictionary<string, PlayerScoreDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var score in playerScores)
                bySong[score.SongId] = score;

            scoresByInstrument[instrumentKey] = bySong;
        }

        // Build INSERT statement matching the RN schema column order:
        //   Per-instrument inline: Score, Diff, Stars, FC, Pct, Season, GameDiff
        //   Then grouped:          {Inst}Total, {Inst}RawPct, {Inst}CalcTotal
        var prefixes = new[] { "Guitar", "Drums", "Bass", "Vocals", "ProGuitar", "ProBass" };

        var columns = new List<string> { "SongId" };
        var parameters = new List<string> { "@songId" };

        foreach (var prefix in prefixes)
        {
            foreach (var suffix in new[] { "Score", "Diff", "Stars", "FC", "Pct", "Season", "GameDiff" })
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

                paramMap[$"@{prefix}Score"].Value    = score.Score;
                paramMap[$"@{prefix}Diff"].Value     = diff;
                paramMap[$"@{prefix}Stars"].Value    = score.Stars;
                paramMap[$"@{prefix}FC"].Value       = score.IsFullCombo ? 1 : 0;
                paramMap[$"@{prefix}Pct"].Value      = score.Accuracy;
                paramMap[$"@{prefix}Season"].Value   = score.Season;
                paramMap[$"@{prefix}GameDiff"].Value = -1; // Not available from leaderboard data
                paramMap[$"@{prefix}Total"].Value    = 0;  // Total entry count not stored per-song
                paramMap[$"@{prefix}RawPct"].Value   = score.Percentile;
                paramMap[$"@{prefix}CalcTotal"].Value = 0;
            }

            cmd.ExecuteNonQuery();
            songsWithScores++;
        }

        tx.Commit();
        _log.LogDebug("Populated {Count} Score rows for account {AccountId}.", songsWithScores, accountId);
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
}
