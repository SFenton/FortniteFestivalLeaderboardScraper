using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace FortniteFestival.Core.Persistence
{
    public static class PersistenceLog
    {
        private static readonly object _lock = new object();
        private static string _logPath;

        internal static void Init(string path)
        {
            try
            {
                _logPath = Path.Combine(Path.GetDirectoryName(path) ?? ".", "persistence.log");
                var header = $"=== Persistence Log Start {DateTime.Now:o} ===";
                File.AppendAllText(_logPath, header + Environment.NewLine);
                Debug.WriteLine("[PersistenceLog] Init path=" + _logPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[PersistenceLog] Init failed: " + ex.Message);
            }
        }

        public static void Write(string msg)
        {
            try
            {
                if (_logPath == null)
                    return;
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
                lock (_lock)
                    File.AppendAllText(_logPath, line + Environment.NewLine);
                Debug.WriteLine("[Persistence] " + line);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[PersistenceLog] Write failed: " + ex.Message);
            }
        }
    }

    public class SqlitePersistence : IFestivalPersistence
    {
        private readonly string _dbPath;

        public SqlitePersistence(string dbPath)
        {
            _dbPath = dbPath;
            PersistenceLog.Init(dbPath);
            PersistenceLog.Write($"SqlitePersistence ctor path={_dbPath}");
            EnsureDatabase();
        }

        // Expose absolute database file path for ancillary storage (e.g., images directory)
        public string DatabasePath => _dbPath;

        private string ConnectionString =>
            new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();

        private void EnsureDatabase()
        {
            try
            {
                var dir = Path.GetDirectoryName(_dbPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    PersistenceLog.Write($"Created directory {dir}");
                }
                using (var conn = new SqliteConnection(ConnectionString))
                {
                    conn.Open();
                    using (var prag = conn.CreateCommand())
                    {
                        prag.CommandText = "PRAGMA journal_mode=WAL;";
                        prag.ExecuteNonQuery();
                        prag.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
                        prag.ExecuteNonQuery();
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            @"CREATE TABLE IF NOT EXISTS Songs (
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
    GuitarScore INTEGER, GuitarDiff INTEGER, GuitarStars INTEGER, GuitarFC INTEGER, GuitarPct INTEGER, GuitarSeason INTEGER, GuitarRank INTEGER, GuitarTotal INTEGER, GuitarPercentile INTEGER,
    DrumsScore INTEGER, DrumsDiff INTEGER, DrumsStars INTEGER, DrumsFC INTEGER, DrumsPct INTEGER, DrumsSeason INTEGER, DrumsRank INTEGER, DrumsTotal INTEGER, DrumsPercentile INTEGER,
    BassScore INTEGER, BassDiff INTEGER, BassStars INTEGER, BassFC INTEGER, BassPct INTEGER, BassSeason INTEGER, BassRank INTEGER, BassTotal INTEGER, BassPercentile INTEGER,
    VocalsScore INTEGER, VocalsDiff INTEGER, VocalsStars INTEGER, VocalsFC INTEGER, VocalsPct INTEGER, VocalsSeason INTEGER, VocalsRank INTEGER, VocalsTotal INTEGER, VocalsPercentile INTEGER,
    ProGuitarScore INTEGER, ProGuitarDiff INTEGER, ProGuitarStars INTEGER, ProGuitarFC INTEGER, ProGuitarPct INTEGER, ProGuitarSeason INTEGER, ProGuitarRank INTEGER, ProGuitarTotal INTEGER, ProGuitarPercentile INTEGER,
    ProBassScore INTEGER, ProBassDiff INTEGER, ProBassStars INTEGER, ProBassFC INTEGER, ProBassPct INTEGER, ProBassSeason INTEGER, ProBassRank INTEGER, ProBassTotal INTEGER, ProBassPercentile INTEGER,
    GuitarRawPct REAL, DrumsRawPct REAL, BassRawPct REAL, VocalsRawPct REAL, ProGuitarRawPct REAL, ProBassRawPct REAL,
    GuitarCalcTotal INTEGER, DrumsCalcTotal INTEGER, BassCalcTotal INTEGER, VocalsCalcTotal INTEGER, ProGuitarCalcTotal INTEGER, ProBassCalcTotal INTEGER,
    FOREIGN KEY (SongId) REFERENCES Songs(SongId)
);";
                        cmd.ExecuteNonQuery();
                    }
                }
                // Lightweight migrations: add columns if missing
                try
                {
                    using (var conn2 = new SqliteConnection(ConnectionString))
                    {
                        conn2.Open();
                        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        using (var check = conn2.CreateCommand())
                        {
                            check.CommandText = "PRAGMA table_info(Songs)";
                            using (var r = check.ExecuteReader())
                            {
                                while (r.Read())
                                {
                                    if (!r.IsDBNull(1)) existing.Add(r.GetString(1));
                                }
                            }
                        }

                        void AddColumn(string name, string type)
                        {
                            if (existing.Contains(name)) return;
                            try
                            {
                                using (var alter = conn2.CreateCommand())
                                {
                                    alter.CommandText = $"ALTER TABLE Songs ADD COLUMN {name} {type}";
                                    alter.ExecuteNonQuery();
                                    PersistenceLog.Write($"Migrated DB: added Songs.{name}");
                                }
                            }
                            catch (Exception aex)
                            {
                                PersistenceLog.Write($"Migration add column {name} failed: {aex.Message}");
                            }
                        }

                        AddColumn("ImagePath", "TEXT");
                        AddColumn("ReleaseYear", "INTEGER");
                        AddColumn("Tempo", "INTEGER");
                        AddColumn("PlasticGuitarDiff", "INTEGER");
                        AddColumn("PlasticBassDiff", "INTEGER");
                        AddColumn("PlasticDrumsDiff", "INTEGER");
                        AddColumn("ProVocalsDiff", "INTEGER");
                        // Path generation: max attainable scores per instrument
                        AddColumn("MaxLeadScore", "INTEGER");
                        AddColumn("MaxBassScore", "INTEGER");
                        AddColumn("MaxDrumsScore", "INTEGER");
                        AddColumn("MaxVocalsScore", "INTEGER");
                        AddColumn("MaxProLeadScore", "INTEGER");
                        AddColumn("MaxProBassScore", "INTEGER");
                        AddColumn("DatFileHash", "TEXT");
                        AddColumn("SongLastModified", "TEXT");
                        AddColumn("PathsGeneratedAt", "TEXT");
                        AddColumn("CHOptVersion", "TEXT");
                        // Scores table rank migrations
                        try
                        {
                            var scoreExisting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            using (var sc = conn2.CreateCommand())
                            {
                                sc.CommandText = "PRAGMA table_info(Scores)";
                                using (var r = sc.ExecuteReader())
                                {
                                    while (r.Read())
                                    {
                                        if (!r.IsDBNull(1)) scoreExisting.Add(r.GetString(1));
                                    }
                                }
                            }
                            void AddScoreColumn(string name)
                            {
                                if (scoreExisting.Contains(name)) return;
                                try
                                {
                                    using (var alter = conn2.CreateCommand())
                                    {
                                        alter.CommandText = $"ALTER TABLE Scores ADD COLUMN {name} INTEGER";
                                        alter.ExecuteNonQuery();
                                        PersistenceLog.Write($"Migrated DB: added Scores.{name}");
                                    }
                                }
                                catch (Exception aex)
                                {
                                    PersistenceLog.Write($"Migration add score column {name} failed: {aex.Message}");
                                }
                            }
                            AddScoreColumn("GuitarRank");
                            AddScoreColumn("DrumsRank");
                            AddScoreColumn("BassRank");
                            AddScoreColumn("VocalsRank");
                            AddScoreColumn("ProGuitarRank");
                            AddScoreColumn("ProBassRank");
                            AddScoreColumn("GuitarTotal");
                            AddScoreColumn("DrumsTotal");
                            AddScoreColumn("BassTotal");
                            AddScoreColumn("VocalsTotal");
                            AddScoreColumn("ProGuitarTotal");
                            AddScoreColumn("ProBassTotal");
                            AddScoreColumn("GuitarPercentile");
                            AddScoreColumn("DrumsPercentile");
                            AddScoreColumn("BassPercentile");
                            AddScoreColumn("VocalsPercentile");
                            AddScoreColumn("ProGuitarPercentile");
                            AddScoreColumn("ProBassPercentile");
                            AddScoreColumn("GuitarRawPct");
                            AddScoreColumn("DrumsRawPct");
                            AddScoreColumn("BassRawPct");
                            AddScoreColumn("VocalsRawPct");
                            AddScoreColumn("ProGuitarRawPct");
                            AddScoreColumn("ProBassRawPct");
                            AddScoreColumn("GuitarCalcTotal");
                            AddScoreColumn("DrumsCalcTotal");
                            AddScoreColumn("BassCalcTotal");
                            AddScoreColumn("VocalsCalcTotal");
                            AddScoreColumn("ProGuitarCalcTotal");
                            AddScoreColumn("ProBassCalcTotal");
                        }
                        catch (Exception rex)
                        {
                            PersistenceLog.Write("Rank column migration failed: " + rex.Message);
                        }
                    }
                }
                catch (Exception mex)
                {
                    PersistenceLog.Write("Migration check failed: " + mex.Message);
                }
                PersistenceLog.Write("EnsureDatabase complete");
            }
            catch (Exception ex)
            {
                PersistenceLog.Write("EnsureDatabase failed: " + ex);
                throw;
            }
        }

        public async Task<IList<LeaderboardData>> LoadScoresAsync()
        {
            var list = new List<LeaderboardData>();
            try
            {
                using (var conn = new SqliteConnection(ConnectionString))
                {
                    await conn.OpenAsync();
                    using (var prag = conn.CreateCommand())
                    {
                        prag.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                        prag.ExecuteNonQuery();
                    }
                        var sql =
                        @"SELECT s.SongId, s.Title, s.Artist,
    sc.GuitarScore, sc.GuitarDiff, sc.GuitarStars, sc.GuitarFC, sc.GuitarPct, sc.GuitarSeason, sc.GuitarRank,
    sc.DrumsScore, sc.DrumsDiff, sc.DrumsStars, sc.DrumsFC, sc.DrumsPct, sc.DrumsSeason, sc.DrumsRank,
    sc.BassScore, sc.BassDiff, sc.BassStars, sc.BassFC, sc.BassPct, sc.BassSeason, sc.BassRank,
    sc.VocalsScore, sc.VocalsDiff, sc.VocalsStars, sc.VocalsFC, sc.VocalsPct, sc.VocalsSeason, sc.VocalsRank,
    sc.ProGuitarScore, sc.ProGuitarDiff, sc.ProGuitarStars, sc.ProGuitarFC, sc.ProGuitarPct, sc.ProGuitarSeason, sc.ProGuitarRank,
    sc.ProBassScore, sc.ProBassDiff, sc.ProBassStars, sc.ProBassFC, sc.ProBassPct, sc.ProBassSeason, sc.ProBassRank,
    sc.GuitarTotal, sc.DrumsTotal, sc.BassTotal, sc.VocalsTotal, sc.ProGuitarTotal, sc.ProBassTotal,
    sc.GuitarPercentile, sc.DrumsPercentile, sc.BassPercentile, sc.VocalsPercentile, sc.ProGuitarPercentile, sc.ProBassPercentile,
    sc.GuitarRawPct, sc.DrumsRawPct, sc.BassRawPct, sc.VocalsRawPct, sc.ProGuitarRawPct, sc.ProBassRawPct,
    sc.GuitarCalcTotal, sc.DrumsCalcTotal, sc.BassCalcTotal, sc.VocalsCalcTotal, sc.ProGuitarCalcTotal, sc.ProBassCalcTotal
FROM Songs s LEFT JOIN Scores sc ON s.SongId = sc.SongId";
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = sql;
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                var ld = new LeaderboardData
                                {
                                    songId = r.GetString(0),
                                    title = r.IsDBNull(1) ? null : r.GetString(1),
                                    artist = r.IsDBNull(2) ? null : r.GetString(2),
                                };
                                int ord = 3;
                                Func<ScoreTracker> readTracker = () =>
                                {
                                    if (r.IsDBNull(ord))
                                    {
                                        ord += 7; // skip base fields
                                        // Skip total column if exists after groups later (handled separately)
                                        return null;
                                    }
                                    var t = new ScoreTracker
                                    {
                                        maxScore = r.IsDBNull(ord) ? 0 : r.GetInt32(ord),
                                        difficulty = r.IsDBNull(ord + 1) ? 0 : r.GetInt32(ord + 1),
                                        numStars = r.IsDBNull(ord + 2) ? 0 : r.GetInt32(ord + 2),
                                        isFullCombo =
                                            !r.IsDBNull(ord + 3) && r.GetInt32(ord + 3) == 1,
                                        percentHit = r.IsDBNull(ord + 4) ? 0 : r.GetInt32(ord + 4),
                                        seasonAchieved = r.IsDBNull(ord + 5)
                                            ? 0
                                            : r.GetInt32(ord + 5),
                                        rank = r.IsDBNull(ord + 6) ? 0 : r.GetInt32(ord + 6),
                                        initialized = !r.IsDBNull(ord) && r.GetInt32(ord) > 0,
                                    };
                                    t.RefreshDerived();
                                    ord += 7;
                                    return t;
                                };
                                ld.guitar = readTracker();
                                ld.drums = readTracker();
                                ld.bass = readTracker();
                                ld.vocals = readTracker();
                                ld.pro_guitar = readTracker();
                                ld.pro_bass = readTracker();
                                // After reading 6 trackers (6*7 = 42 columns after initial 3), map totals then percentiles then raw percentiles
                                // The SELECT adds 6 total columns, 6 percentile columns, 6 raw percentile columns
                                try
                                {
                                    if (!r.IsDBNull(ord)) ld.guitar.totalEntries = r.GetInt32(ord); ord++;
                                    if (!r.IsDBNull(ord)) ld.drums.totalEntries = r.GetInt32(ord); ord++;
                                    if (!r.IsDBNull(ord)) ld.bass.totalEntries = r.GetInt32(ord); ord++;
                                    if (!r.IsDBNull(ord)) ld.vocals.totalEntries = r.GetInt32(ord); ord++;
                                    if (!r.IsDBNull(ord)) ld.pro_guitar.totalEntries = r.GetInt32(ord); ord++;
                                    if (!r.IsDBNull(ord)) ld.pro_bass.totalEntries = r.GetInt32(ord); ord++;
                                    // Skip legacy basis-point percentile columns (advance ord by 6)
                                    ord += 6;
                                    // Raw percentiles (REAL)
                                    if (!r.IsDBNull(ord) && ld.guitar!=null) { ld.guitar.rawPercentile = r.GetDouble(ord); } ord++;
                                    if (!r.IsDBNull(ord) && ld.drums!=null) { ld.drums.rawPercentile = r.GetDouble(ord); } ord++;
                                    if (!r.IsDBNull(ord) && ld.bass!=null) { ld.bass.rawPercentile = r.GetDouble(ord); } ord++;
                                    if (!r.IsDBNull(ord) && ld.vocals!=null) { ld.vocals.rawPercentile = r.GetDouble(ord); } ord++;
                                    if (!r.IsDBNull(ord) && ld.pro_guitar!=null) { ld.pro_guitar.rawPercentile = r.GetDouble(ord); } ord++;
                                    if (!r.IsDBNull(ord) && ld.pro_bass!=null) { ld.pro_bass.rawPercentile = r.GetDouble(ord); } ord++;
                                    // Calculated totals (INTEGER)
                                    if (!r.IsDBNull(ord) && ld.guitar!=null) { ld.guitar.calculatedNumEntries = r.GetInt32(ord); } ord++;
                                    if (!r.IsDBNull(ord) && ld.drums!=null) { ld.drums.calculatedNumEntries = r.GetInt32(ord); } ord++;
                                    if (!r.IsDBNull(ord) && ld.bass!=null) { ld.bass.calculatedNumEntries = r.GetInt32(ord); } ord++;
                                    if (!r.IsDBNull(ord) && ld.vocals!=null) { ld.vocals.calculatedNumEntries = r.GetInt32(ord); } ord++;
                                    if (!r.IsDBNull(ord) && ld.pro_guitar!=null) { ld.pro_guitar.calculatedNumEntries = r.GetInt32(ord); } ord++;
                                    if (!r.IsDBNull(ord) && ld.pro_bass!=null) { ld.pro_bass.calculatedNumEntries = r.GetInt32(ord); } ord++;
                                    ld.guitar?.RefreshDerived(); ld.drums?.RefreshDerived(); ld.bass?.RefreshDerived(); ld.vocals?.RefreshDerived(); ld.pro_guitar?.RefreshDerived(); ld.pro_bass?.RefreshDerived();
                                }
                                catch { }
                                list.Add(ld);
                            }
                        }
                    }
                }
                PersistenceLog.Write($"LoadScoresAsync loaded {list.Count} rows");
            }
            catch (Exception ex)
            {
                PersistenceLog.Write("LoadScoresAsync failed: " + ex);
                throw;
            }
            return list;
        }

        public async Task SaveScoresAsync(IEnumerable<LeaderboardData> scores)
        {
            try
            {
                using (var conn = new SqliteConnection(ConnectionString))
                {
                    await conn.OpenAsync();
                    using (var tx = conn.BeginTransaction())
                    {
                        var songCmd = conn.CreateCommand();
                        songCmd.CommandText =
                            @"INSERT INTO Songs (SongId, Title, Artist, ActiveDate, LastModified, LeadDiff, BassDiff, VocalsDiff, DrumsDiff, ProLeadDiff, ProBassDiff)
VALUES ($id,$title,$artist,'','','0','0','0','0','0','0')
ON CONFLICT(SongId) DO UPDATE SET Title=$title, Artist=$artist";
                        songCmd.Parameters.Add(new SqliteParameter("$id", ""));
                        songCmd.Parameters.Add(new SqliteParameter("$title", ""));
                        songCmd.Parameters.Add(new SqliteParameter("$artist", ""));

                        var scoreCmd = conn.CreateCommand();
                        scoreCmd.CommandText =
                            @"INSERT INTO Scores (SongId,
                            GuitarScore,GuitarDiff,GuitarStars,GuitarFC,GuitarPct,GuitarSeason,GuitarRank,GuitarTotal,GuitarPercentile,
                            DrumsScore,DrumsDiff,DrumsStars,DrumsFC,DrumsPct,DrumsSeason,DrumsRank,DrumsTotal,DrumsPercentile,
                            BassScore,BassDiff,BassStars,BassFC,BassPct,BassSeason,BassRank,BassTotal,BassPercentile,
                            VocalsScore,VocalsDiff,VocalsStars,VocalsFC,VocalsPct,VocalsSeason,VocalsRank,VocalsTotal,VocalsPercentile,
                            ProGuitarScore,ProGuitarDiff,ProGuitarStars,ProGuitarFC,ProGuitarPct,ProGuitarSeason,ProGuitarRank,ProGuitarTotal,ProGuitarPercentile,
                            ProBassScore,ProBassDiff,ProBassStars,ProBassFC,ProBassPct,ProBassSeason,ProBassRank,ProBassTotal,ProBassPercentile,
                            GuitarRawPct,DrumsRawPct,BassRawPct,VocalsRawPct,ProGuitarRawPct,ProBassRawPct,
                            GuitarCalcTotal,DrumsCalcTotal,BassCalcTotal,VocalsCalcTotal,ProGuitarCalcTotal,ProBassCalcTotal)
                            VALUES ($id,
                            $gScore,$gDiff,$gStars,$gFC,$gPct,$gSeason,$gRank,$gTotal,$gPctile,
                            $dScore,$dDiff,$dStars,$dFC,$dPct,$dSeason,$dRank,$dTotal,$dPctile,
                            $bScore,$bDiff,$bStars,$bFC,$bPct,$bSeason,$bRank,$bTotal,$bPctile,
                            $vScore,$vDiff,$vStars,$vFC,$vPct,$vSeason,$vRank,$vTotal,$vPctile,
                            $pgScore,$pgDiff,$pgStars,$pgFC,$pgPct,$pgSeason,$pgRank,$pgTotal,$pgPctile,
                            $pbScore,$pbDiff,$pbStars,$pbFC,$pbPct,$pbSeason,$pbRank,$pbTotal,$pbPctile,
                            $gRaw,$dRaw,$bRaw,$vRaw,$pgRaw,$pbRaw,
                            $gCalc,$dCalc,$bCalc,$vCalc,$pgCalc,$pbCalc)
                            ON CONFLICT(SongId) DO UPDATE SET
                            GuitarScore=$gScore,GuitarDiff=$gDiff,GuitarStars=$gStars,GuitarFC=$gFC,GuitarPct=$gPct,GuitarSeason=$gSeason,GuitarRank=$gRank,GuitarTotal=$gTotal,GuitarPercentile=$gPctile,GuitarRawPct=$gRaw,GuitarCalcTotal=$gCalc,
                            DrumsScore=$dScore,DrumsDiff=$dDiff,DrumsStars=$dStars,DrumsFC=$dFC,DrumsPct=$dPct,DrumsSeason=$dSeason,DrumsRank=$dRank,DrumsTotal=$dTotal,DrumsPercentile=$dPctile,DrumsRawPct=$dRaw,DrumsCalcTotal=$dCalc,
                            BassScore=$bScore,BassDiff=$bDiff,BassStars=$bStars,BassFC=$bFC,BassPct=$bPct,BassSeason=$bSeason,BassRank=$bRank,BassTotal=$bTotal,BassPercentile=$bPctile,BassRawPct=$bRaw,BassCalcTotal=$bCalc,
                            VocalsScore=$vScore,VocalsDiff=$vDiff,VocalsStars=$vStars,VocalsFC=$vFC,VocalsPct=$vPct,VocalsSeason=$vSeason,VocalsRank=$vRank,VocalsTotal=$vTotal,VocalsPercentile=$vPctile,VocalsRawPct=$vRaw,VocalsCalcTotal=$vCalc,
                            ProGuitarScore=$pgScore,ProGuitarDiff=$pgDiff,ProGuitarStars=$pgStars,ProGuitarFC=$pgFC,ProGuitarPct=$pgPct,ProGuitarSeason=$pgSeason,ProGuitarRank=$pgRank,ProGuitarTotal=$pgTotal,ProGuitarPercentile=$pgPctile,ProGuitarRawPct=$pgRaw,ProGuitarCalcTotal=$pgCalc,
                            ProBassScore=$pbScore,ProBassDiff=$pbDiff,ProBassStars=$pbStars,ProBassFC=$pbFC,ProBassPct=$pbPct,ProBassSeason=$pbSeason,ProBassRank=$pbRank,ProBassTotal=$pbTotal,ProBassPercentile=$pbPctile,ProBassRawPct=$pbRaw,ProBassCalcTotal=$pbCalc";
                        string[] names =
                        {
                            "$gScore",
                            "$gDiff",
                            "$gStars",
                            "$gFC",
                            "$gPct",
                            "$gSeason",
                            "$gRank",
                            "$dScore",
                            "$dDiff",
                            "$dStars",
                            "$dFC",
                            "$dPct",
                            "$dSeason",
                            "$dRank",
                            "$bScore",
                            "$bDiff",
                            "$bStars",
                            "$bFC",
                            "$bPct",
                            "$bSeason",
                            "$bRank",
                            "$vScore",
                            "$vDiff",
                            "$vStars",
                            "$vFC",
                            "$vPct",
                            "$vSeason",
                            "$vRank",
                            "$pgScore",
                            "$pgDiff",
                            "$pgStars",
                            "$pgFC",
                            "$pgPct",
                            "$pgSeason",
                            "$pgRank",
                            "$pbScore",
                            "$pbDiff",
                            "$pbStars",
                            "$pbFC",
                            "$pbPct",
                            "$pbSeason",
                            "$pbRank",
                            "$gTotal",
                            "$dTotal",
                            "$bTotal",
                            "$vTotal",
                            "$pgTotal",
                            "$pbTotal",
                            "$gPctile",
                            "$dPctile",
                            "$bPctile",
                            "$vPctile",
                            "$pgPctile",
                            "$pbPctile",
                            "$gRaw",
                            "$dRaw",
                            "$bRaw",
                            "$vRaw",
                            "$pgRaw",
                            "$pbRaw",
                            "$gCalc",
                            "$dCalc",
                            "$bCalc",
                            "$vCalc",
                            "$pgCalc",
                            "$pbCalc",
                        };
                        foreach (var p in new[] { "$id" }.Concat(names))
                            scoreCmd.Parameters.Add(new SqliteParameter(p, 0));

                        int persisted = 0;
                        foreach (var ld in scores)
                        {
                            if (!HasAnyScore(ld))
                                continue;
                            songCmd.Parameters[0].Value = ld.songId;
                            songCmd.Parameters[1].Value = ld.title ?? string.Empty;
                            songCmd.Parameters[2].Value = ld.artist ?? string.Empty;
                            await songCmd.ExecuteNonQueryAsync();

                            scoreCmd.Parameters[0].Value = ld.songId;
                            Fill(scoreCmd, 1, ld.guitar);
                            Fill(scoreCmd, 8, ld.drums);
                            Fill(scoreCmd, 15, ld.bass);
                            Fill(scoreCmd, 22, ld.vocals);
                            Fill(scoreCmd, 29, ld.pro_guitar);
                            Fill(scoreCmd, 36, ld.pro_bass);
                            // Totals parameters start after 43 base params (1 id + 42 score fields) => index 43
                            int totalsStart = 43; // after 1 id + 42 base instrument fields
                            scoreCmd.Parameters[totalsStart + 0].Value = ld.guitar?.totalEntries ?? 0; // $gTotal
                            scoreCmd.Parameters[totalsStart + 1].Value = ld.drums?.totalEntries ?? 0; // $dTotal
                            scoreCmd.Parameters[totalsStart + 2].Value = ld.bass?.totalEntries ?? 0; // $bTotal
                            scoreCmd.Parameters[totalsStart + 3].Value = ld.vocals?.totalEntries ?? 0; // $vTotal
                            scoreCmd.Parameters[totalsStart + 4].Value = ld.pro_guitar?.totalEntries ?? 0; // $pgTotal
                            scoreCmd.Parameters[totalsStart + 5].Value = ld.pro_bass?.totalEntries ?? 0; // $pbTotal
                            // Percentiles follow totals then raw percentiles
                            // Legacy basis-point percentile columns now always 0
                            scoreCmd.Parameters[totalsStart + 6].Value = 0; // $gPctile
                            scoreCmd.Parameters[totalsStart + 7].Value = 0; // $dPctile
                            scoreCmd.Parameters[totalsStart + 8].Value = 0; // $bPctile
                            scoreCmd.Parameters[totalsStart + 9].Value = 0; // $vPctile
                            scoreCmd.Parameters[totalsStart + 10].Value = 0; // $pgPctile
                            scoreCmd.Parameters[totalsStart + 11].Value = 0; // $pbPctile
                            scoreCmd.Parameters[totalsStart + 12].Value = ld.guitar?.rawPercentile ?? 0; // $gRaw
                            scoreCmd.Parameters[totalsStart + 13].Value = ld.drums?.rawPercentile ?? 0; // $dRaw
                            scoreCmd.Parameters[totalsStart + 14].Value = ld.bass?.rawPercentile ?? 0; // $bRaw
                            scoreCmd.Parameters[totalsStart + 15].Value = ld.vocals?.rawPercentile ?? 0; // $vRaw
                            scoreCmd.Parameters[totalsStart + 16].Value = ld.pro_guitar?.rawPercentile ?? 0; // $pgRaw
                            scoreCmd.Parameters[totalsStart + 17].Value = ld.pro_bass?.rawPercentile ?? 0; // $pbRaw
                            // Calculated totals start after raw percentile params
                            scoreCmd.Parameters[totalsStart + 18].Value = ld.guitar?.calculatedNumEntries ?? 0; // $gCalc
                            scoreCmd.Parameters[totalsStart + 19].Value = ld.drums?.calculatedNumEntries ?? 0; // $dCalc
                            scoreCmd.Parameters[totalsStart + 20].Value = ld.bass?.calculatedNumEntries ?? 0; // $bCalc
                            scoreCmd.Parameters[totalsStart + 21].Value = ld.vocals?.calculatedNumEntries ?? 0; // $vCalc
                            scoreCmd.Parameters[totalsStart + 22].Value = ld.pro_guitar?.calculatedNumEntries ?? 0; // $pgCalc
                            scoreCmd.Parameters[totalsStart + 23].Value = ld.pro_bass?.calculatedNumEntries ?? 0; // $pbCalc
                            await scoreCmd.ExecuteNonQueryAsync();
                            persisted++;
                        }
                        tx.Commit();
                        PersistenceLog.Write($"SaveScoresAsync persisted {persisted} rows");
                    }
                }
            }
            catch (Exception ex)
            {
                PersistenceLog.Write("SaveScoresAsync failed: " + ex);
                throw;
            }
        }

        private static bool HasAnyScore(LeaderboardData ld)
        {
            return (ld.guitar?.initialized == true)
                || (ld.drums?.initialized == true)
                || (ld.bass?.initialized == true)
                || (ld.vocals?.initialized == true)
                || (ld.pro_guitar?.initialized == true)
                || (ld.pro_bass?.initialized == true);
        }

    private static void Fill(SqliteCommand cmd, int startIndex, ScoreTracker t)
        {
            if (t == null)
            {
        for (int i = 0; i < 7; i++)
                    cmd.Parameters[startIndex + i].Value = 0;
                return;
            }
            cmd.Parameters[startIndex + 0].Value = t.maxScore;
            cmd.Parameters[startIndex + 1].Value = t.difficulty;
            cmd.Parameters[startIndex + 2].Value = t.numStars;
            cmd.Parameters[startIndex + 3].Value = t.isFullCombo ? 1 : 0;
            cmd.Parameters[startIndex + 4].Value = t.percentHit;
            cmd.Parameters[startIndex + 5].Value = t.seasonAchieved;
        cmd.Parameters[startIndex + 6].Value = t.rank;
        }

        public async Task<IList<Song>> LoadSongsAsync()
        {
            var list = new List<Song>();
            try
            {
                using (var conn = new SqliteConnection(ConnectionString))
                {
                    await conn.OpenAsync();
                    using (var prag = conn.CreateCommand())
                    {
                        prag.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                        prag.ExecuteNonQuery();
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT SongId, Title, Artist, ActiveDate, LastModified, ImagePath, LeadDiff, BassDiff, VocalsDiff, DrumsDiff, ProLeadDiff, ProBassDiff, ReleaseYear, Tempo, PlasticGuitarDiff, PlasticBassDiff, PlasticDrumsDiff, ProVocalsDiff FROM Songs";
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                var song = new Song
                                {
                                    track = new Track
                                    {
                                        su = r.IsDBNull(0) ? null : r.GetString(0),
                                        tt = r.IsDBNull(1) ? null : r.GetString(1),
                                        an = r.IsDBNull(2) ? null : r.GetString(2),
                                        @in = new In
                                        {
                                            gr = r.IsDBNull(6) ? 0 : r.GetInt32(6), // LeadDiff
                                            ba = r.IsDBNull(7) ? 0 : r.GetInt32(7), // BassDiff
                                            vl = r.IsDBNull(8) ? 0 : r.GetInt32(8), // VocalsDiff
                                            ds = r.IsDBNull(9) ? 0 : r.GetInt32(9), // DrumsDiff
                                            pg = r.IsDBNull(10) ? 0 : r.GetInt32(10), // ProLeadDiff
                                            pb = r.IsDBNull(11) ? 0 : r.GetInt32(11), // ProBassDiff
                                        },
                                        ry = r.IsDBNull(12) ? 0 : r.GetInt32(12), // ReleaseYear
                                        mt = r.IsDBNull(13) ? 0 : r.GetInt32(13), // Tempo
                                    },
                                    _activeDate = ParseDate(r, 3),
                                    lastModified = ParseDate(r, 4),
                                    imagePath = r.IsDBNull(5) ? null : r.GetString(5),
                                };
                                // Map plastic difficulties & pro vocals
                                if (song.track?.@in != null)
                                {
                                    if (!r.IsDBNull(14)) song.track.@in.pg = r.GetInt32(14); // PlasticGuitarDiff
                                    if (!r.IsDBNull(15)) song.track.@in.pb = r.GetInt32(15); // PlasticBassDiff
                                    if (!r.IsDBNull(16)) song.track.@in.pd = r.GetInt32(16); // PlasticDrumsDiff
                                    if (!r.IsDBNull(17))
                                    {
                                        var pv = r.GetInt32(17);
                                        song.track.@in.bd = Track.HasChartedDifficulty(pv) ? pv : 99;
                                    }
                                }
                                list.Add(song);
                            }
                        }
                    }
                }
                PersistenceLog.Write($"LoadSongsAsync loaded {list.Count} songs");
            }
            catch (Exception ex)
            {
                PersistenceLog.Write("LoadSongsAsync failed: " + ex);
                throw;
            }
            return list;
        }

        private static System.DateTime ParseDate(SqliteDataReader r, int ord)
        {
            if (r.IsDBNull(ord))
                return System.DateTime.MinValue;
            if (System.DateTime.TryParse(r.GetString(ord), out var dt))
                return dt;
            return System.DateTime.MinValue;
        }

        public async Task SaveSongsAsync(IEnumerable<Song> songs)
        {
            try
            {
                using (var conn = new SqliteConnection(ConnectionString))
                {
                    await conn.OpenAsync();
                    using (var prag = conn.CreateCommand())
                    {
                        prag.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                        prag.ExecuteNonQuery();
                    }
                    using (var tx = conn.BeginTransaction())
                    {
                        var cmd = conn.CreateCommand();
                        cmd.CommandText =
                            @"INSERT INTO Songs (SongId, Title, Artist, ActiveDate, LastModified, ImagePath, LeadDiff, BassDiff, VocalsDiff, DrumsDiff, ProLeadDiff, ProBassDiff, ReleaseYear, Tempo, PlasticGuitarDiff, PlasticBassDiff, PlasticDrumsDiff, ProVocalsDiff)
VALUES ($id,$title,$artist,$active,$modified,$image,$lead,$bass,$vocals,$drums,$plead,$pbass,$ry,$tempo,$plGtr,$plBass,$plDrums,$proVocals)
ON CONFLICT(SongId) DO UPDATE SET Title=$title, Artist=$artist, ActiveDate=$active, LastModified=$modified, ImagePath=$image, LeadDiff=$lead, BassDiff=$bass, VocalsDiff=$vocals, DrumsDiff=$drums, ProLeadDiff=$plead, ProBassDiff=$pbass, ReleaseYear=$ry, Tempo=$tempo, PlasticGuitarDiff=$plGtr, PlasticBassDiff=$plBass, PlasticDrumsDiff=$plDrums, ProVocalsDiff=$proVocals";
                        var parms = new[]
                        {
                            "$id",
                            "$title",
                            "$artist",
                            "$active",
                            "$modified",
                            "$image",
                            "$lead",
                            "$bass",
                            "$vocals",
                            "$drums",
                            "$plead",
                            "$pbass",
                            "$ry",
                            "$tempo",
                            "$plGtr",
                            "$plBass",
                            "$plDrums",
                            "$proVocals",
                        };
                        foreach (var p in parms)
                            cmd.Parameters.Add(new SqliteParameter(p, 0));
                        int count = 0;
                        foreach (var s in songs)
                        {
                            cmd.Parameters[0].Value = s.track?.su ?? string.Empty;
                            cmd.Parameters[1].Value = s.track?.tt ?? string.Empty;
                            cmd.Parameters[2].Value = s.track?.an ?? string.Empty;
                            cmd.Parameters[3].Value =
                                s._activeDate == System.DateTime.MinValue
                                    ? ""
                                    : s._activeDate.ToString("o");
                            cmd.Parameters[4].Value =
                                s.lastModified == System.DateTime.MinValue
                                    ? ""
                                    : s.lastModified.ToString("o");
                            cmd.Parameters[5].Value = s.imagePath ?? string.Empty;
                            cmd.Parameters[6].Value = s.track?.@in?.gr ?? 0;
                            cmd.Parameters[7].Value = s.track?.@in?.ba ?? 0;
                            cmd.Parameters[8].Value = s.track?.@in?.vl ?? 0;
                            cmd.Parameters[9].Value = s.track?.@in?.ds ?? 0;
                            cmd.Parameters[10].Value = s.track?.@in?.pg ?? 0;
                            cmd.Parameters[11].Value = s.track?.@in?.pb ?? 0;
                            cmd.Parameters[12].Value = s.track?.ry ?? 0; // ReleaseYear
                            cmd.Parameters[13].Value = s.track?.mt ?? 0; // Tempo
                            // Plastic instrument difficulties: treat original intensity codes as provided by server.
                            cmd.Parameters[14].Value = s.track?.@in?.pg ?? 0; // PlasticGuitarDiff
                            cmd.Parameters[15].Value = s.track?.@in?.pb ?? 0; // PlasticBassDiff (re-using pb until clarified)
                            cmd.Parameters[16].Value = s.track?.@in?.pd ?? 0; // PlasticDrumsDiff
                            var rawProVocals = s.track?.@in?.bd;
                            var proVocals = Track.HasChartedDifficulty(rawProVocals) ? rawProVocals!.Value : 99;
                            cmd.Parameters[17].Value = proVocals; // ProVocalsDiff from bd
                            await cmd.ExecuteNonQueryAsync();
                            count++;
                        }
                        tx.Commit();
                        PersistenceLog.Write($"SaveSongsAsync persisted {count} songs");
                    }
                }
            }
            catch (Exception ex)
            {
                PersistenceLog.Write("SaveSongsAsync failed: " + ex);
                throw;
            }
        }
    }
}
