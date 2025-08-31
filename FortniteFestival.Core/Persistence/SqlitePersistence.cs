using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace FortniteFestival.Core.Persistence
{
    public static class PersistenceLog
    {
        private static readonly object _lock = new object();
        private static string _logPath;
        internal static void Init(string path)
        {
            try { _logPath = Path.Combine(Path.GetDirectoryName(path) ?? ".", "persistence.log"); var header = $"=== Persistence Log Start {DateTime.Now:o} ==="; File.AppendAllText(_logPath, header+Environment.NewLine); Debug.WriteLine("[PersistenceLog] Init path="+_logPath); } catch (Exception ex) { Debug.WriteLine("[PersistenceLog] Init failed: "+ex.Message); }
        }
        public static void Write(string msg)
        {
            try { if(_logPath==null) return; var line=$"[{DateTime.Now:HH:mm:ss.fff}] {msg}"; lock(_lock) File.AppendAllText(_logPath, line+Environment.NewLine); Debug.WriteLine("[Persistence] "+line); } catch (Exception ex) { Debug.WriteLine("[PersistenceLog] Write failed: "+ex.Message); }
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

        private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();

        private void EnsureDatabase()
        {
            try
            {
                var dir = Path.GetDirectoryName(_dbPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) { Directory.CreateDirectory(dir); PersistenceLog.Write($"Created directory {dir}"); }
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
                        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS Songs (
    SongId TEXT PRIMARY KEY,
    Title TEXT,
    Artist TEXT,
    ActiveDate TEXT,
    LastModified TEXT,
    LeadDiff INTEGER,
    BassDiff INTEGER,
    VocalsDiff INTEGER,
    DrumsDiff INTEGER,
    ProLeadDiff INTEGER,
    ProBassDiff INTEGER
);
CREATE TABLE IF NOT EXISTS Scores (
    SongId TEXT PRIMARY KEY,
    GuitarScore INTEGER, GuitarDiff INTEGER, GuitarStars INTEGER, GuitarFC INTEGER, GuitarPct INTEGER, GuitarSeason INTEGER,
    DrumsScore INTEGER, DrumsDiff INTEGER, DrumsStars INTEGER, DrumsFC INTEGER, DrumsPct INTEGER, DrumsSeason INTEGER,
    BassScore INTEGER, BassDiff INTEGER, BassStars INTEGER, BassFC INTEGER, BassPct INTEGER, BassSeason INTEGER,
    VocalsScore INTEGER, VocalsDiff INTEGER, VocalsStars INTEGER, VocalsFC INTEGER, VocalsPct INTEGER, VocalsSeason INTEGER,
    ProGuitarScore INTEGER, ProGuitarDiff INTEGER, ProGuitarStars INTEGER, ProGuitarFC INTEGER, ProGuitarPct INTEGER, ProGuitarSeason INTEGER,
    ProBassScore INTEGER, ProBassDiff INTEGER, ProBassStars INTEGER, ProBassFC INTEGER, ProBassPct INTEGER, ProBassSeason INTEGER,
    FOREIGN KEY (SongId) REFERENCES Songs(SongId)
);";
                        cmd.ExecuteNonQuery();
                    }
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
                    using (var prag = conn.CreateCommand()) { prag.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;"; prag.ExecuteNonQuery(); }
                    var sql = @"SELECT s.SongId, s.Title, s.Artist,
    sc.GuitarScore, sc.GuitarDiff, sc.GuitarStars, sc.GuitarFC, sc.GuitarPct, sc.GuitarSeason,
    sc.DrumsScore, sc.DrumsDiff, sc.DrumsStars, sc.DrumsFC, sc.DrumsPct, sc.DrumsSeason,
    sc.BassScore, sc.BassDiff, sc.BassStars, sc.BassFC, sc.BassPct, sc.BassSeason,
    sc.VocalsScore, sc.VocalsDiff, sc.VocalsStars, sc.VocalsFC, sc.VocalsPct, sc.VocalsSeason,
    sc.ProGuitarScore, sc.ProGuitarDiff, sc.ProGuitarStars, sc.ProGuitarFC, sc.ProGuitarPct, sc.ProGuitarSeason,
    sc.ProBassScore, sc.ProBassDiff, sc.ProBassStars, sc.ProBassFC, sc.ProBassPct, sc.ProBassSeason
FROM Songs s LEFT JOIN Scores sc ON s.SongId = sc.SongId";
                    using (var cmd = conn.CreateCommand()) { cmd.CommandText = sql; using (var r = await cmd.ExecuteReaderAsync()) { while (await r.ReadAsync()) { var ld = new LeaderboardData { songId = r.GetString(0), title = r.IsDBNull(1)?null:r.GetString(1), artist = r.IsDBNull(2)?null:r.GetString(2) }; int ord = 3; Func<ScoreTracker> readTracker = () => { if (r.IsDBNull(ord)) { ord += 6; return null; } var t = new ScoreTracker { maxScore = r.IsDBNull(ord)?0:r.GetInt32(ord), difficulty = r.IsDBNull(ord+1)?0:r.GetInt32(ord+1), numStars = r.IsDBNull(ord+2)?0:r.GetInt32(ord+2), isFullCombo = !r.IsDBNull(ord+3) && r.GetInt32(ord+3)==1, percentHit = r.IsDBNull(ord+4)?0:r.GetInt32(ord+4), seasonAchieved = r.IsDBNull(ord+5)?0:r.GetInt32(ord+5), initialized = !r.IsDBNull(ord) && r.GetInt32(ord)>0 }; t.RefreshDerived(); ord+=6; return t; }; ld.guitar = readTracker(); ld.drums = readTracker(); ld.bass = readTracker(); ld.vocals = readTracker(); ld.pro_guitar = readTracker(); ld.pro_bass = readTracker(); list.Add(ld); } } }
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
                        songCmd.CommandText = @"INSERT INTO Songs (SongId, Title, Artist, ActiveDate, LastModified, LeadDiff, BassDiff, VocalsDiff, DrumsDiff, ProLeadDiff, ProBassDiff)
VALUES ($id,$title,$artist,'','','0','0','0','0','0','0')
ON CONFLICT(SongId) DO UPDATE SET Title=$title, Artist=$artist";
                        songCmd.Parameters.Add(new SqliteParameter("$id", ""));
                        songCmd.Parameters.Add(new SqliteParameter("$title", ""));
                        songCmd.Parameters.Add(new SqliteParameter("$artist", ""));

                        var scoreCmd = conn.CreateCommand();
                        scoreCmd.CommandText = @"INSERT INTO Scores (SongId,
GuitarScore,GuitarDiff,GuitarStars,GuitarFC,GuitarPct,GuitarSeason,
DrumsScore,DrumsDiff,DrumsStars,DrumsFC,DrumsPct,DrumsSeason,
BassScore,BassDiff,BassStars,BassFC,BassPct,BassSeason,
VocalsScore,VocalsDiff,VocalsStars,VocalsFC,VocalsPct,VocalsSeason,
ProGuitarScore,ProGuitarDiff,ProGuitarStars,ProGuitarFC,ProGuitarPct,ProGuitarSeason,
ProBassScore,ProBassDiff,ProBassStars,ProBassFC,ProBassPct,ProBassSeason)
VALUES ($id,
$gScore,$gDiff,$gStars,$gFC,$gPct,$gSeason,
$dScore,$dDiff,$dStars,$dFC,$dPct,$dSeason,
$bScore,$bDiff,$bStars,$bFC,$bPct,$bSeason,
$vScore,$vDiff,$vStars,$vFC,$vPct,$vSeason,
$pgScore,$pgDiff,$pgStars,$pgFC,$pgPct,$pgSeason,
$pbScore,$pbDiff,$pbStars,$pbFC,$pbPct,$pbSeason)
ON CONFLICT(SongId) DO UPDATE SET
GuitarScore=$gScore,GuitarDiff=$gDiff,GuitarStars=$gStars,GuitarFC=$gFC,GuitarPct=$gPct,GuitarSeason=$gSeason,
DrumsScore=$dScore,DrumsDiff=$dDiff,DrumsStars=$dStars,DrumsFC=$dFC,DrumsPct=$dPct,DrumsSeason=$dSeason,
BassScore=$bScore,BassDiff=$bDiff,BassStars=$bStars,BassFC=$bFC,BassPct=$bPct,BassSeason=$bSeason,
VocalsScore=$vScore,VocalsDiff=$vDiff,VocalsStars=$vStars,VocalsFC=$vFC,VocalsPct=$vPct,VocalsSeason=$vSeason,
ProGuitarScore=$pgScore,ProGuitarDiff=$pgDiff,ProGuitarStars=$pgStars,ProGuitarFC=$pgFC,ProGuitarPct=$pgPct,ProGuitarSeason=$pgSeason,
ProBassScore=$pbScore,ProBassDiff=$pbDiff,ProBassStars=$pbStars,ProBassFC=$pbFC,ProBassPct=$pbPct,ProBassSeason=$pbSeason";
                        string[] names = {"$gScore","$gDiff","$gStars","$gFC","$gPct","$gSeason","$dScore","$dDiff","$dStars","$dFC","$dPct","$dSeason","$bScore","$bDiff","$bStars","$bFC","$bPct","$bSeason","$vScore","$vDiff","$vStars","$vFC","$vPct","$vSeason","$pgScore","$pgDiff","$pgStars","$pgFC","$pgPct","$pgSeason","$pbScore","$pbDiff","$pbStars","$pbFC","$pbPct","$pbSeason"};
                        foreach(var p in new[]{"$id"}.Concat(names)) scoreCmd.Parameters.Add(new SqliteParameter(p, 0));

                        int persisted = 0;
                        foreach (var ld in scores)
                        {
                            if(!HasAnyScore(ld)) continue;
                            songCmd.Parameters[0].Value = ld.songId;
                            songCmd.Parameters[1].Value = ld.title ?? string.Empty;
                            songCmd.Parameters[2].Value = ld.artist ?? string.Empty;
                            await songCmd.ExecuteNonQueryAsync();

                            scoreCmd.Parameters[0].Value = ld.songId;
                            Fill(scoreCmd, 1, ld.guitar);
                            Fill(scoreCmd, 7, ld.drums);
                            Fill(scoreCmd, 13, ld.bass);
                            Fill(scoreCmd, 19, ld.vocals);
                            Fill(scoreCmd, 25, ld.pro_guitar);
                            Fill(scoreCmd, 31, ld.pro_bass);
                            await scoreCmd.ExecuteNonQueryAsync();
                            persisted++;
                        }
                        tx.Commit();
                        PersistenceLog.Write($"SaveScoresAsync persisted {persisted} rows");
                    }
                }
            }
            catch(Exception ex)
            {
                PersistenceLog.Write("SaveScoresAsync failed: " + ex);
                throw;
            }
        }
        private static bool HasAnyScore(LeaderboardData ld){ return (ld.guitar?.initialized==true)||(ld.drums?.initialized==true)||(ld.bass?.initialized==true)||(ld.vocals?.initialized==true)||(ld.pro_guitar?.initialized==true)||(ld.pro_bass?.initialized==true); }
        private static void Fill(SqliteCommand cmd, int startIndex, ScoreTracker t)
        {
            if(t==null){ for(int i=0;i<6;i++) cmd.Parameters[startIndex+i].Value = 0; return; }
            cmd.Parameters[startIndex+0].Value = t.maxScore;
            cmd.Parameters[startIndex+1].Value = t.difficulty;
            cmd.Parameters[startIndex+2].Value = t.numStars;
            cmd.Parameters[startIndex+3].Value = t.isFullCombo ? 1:0;
            cmd.Parameters[startIndex+4].Value = t.percentHit;
            cmd.Parameters[startIndex+5].Value = t.seasonAchieved;
        }

        public async Task<IList<Song>> LoadSongsAsync()
        {
            var list = new List<Song>();
            try
            {
                using (var conn = new SqliteConnection(ConnectionString))
                {
                    await conn.OpenAsync();
                    using (var prag = conn.CreateCommand()) { prag.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;"; prag.ExecuteNonQuery(); }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT SongId, Title, Artist, ActiveDate, LastModified, LeadDiff, BassDiff, VocalsDiff, DrumsDiff, ProLeadDiff, ProBassDiff FROM Songs";
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                var song = new Song
                                {
                                    track = new Track
                                    {
                                        su = r.IsDBNull(0)?null:r.GetString(0),
                                        tt = r.IsDBNull(1)?null:r.GetString(1),
                                        an = r.IsDBNull(2)?null:r.GetString(2),
                                        @in = new In
                                        {
                                            gr = r.IsDBNull(5)?0:r.GetInt32(5),
                                            ba = r.IsDBNull(6)?0:r.GetInt32(6),
                                            vl = r.IsDBNull(7)?0:r.GetInt32(7),
                                            ds = r.IsDBNull(8)?0:r.GetInt32(8),
                                            pg = r.IsDBNull(9)?0:r.GetInt32(9),
                                            pb = r.IsDBNull(10)?0:r.GetInt32(10)
                                        }
                                    },
                                    _activeDate = ParseDate(r,3),
                                    lastModified = ParseDate(r,4)
                                };
                                list.Add(song);
                            }
                        }
                    }
                }
                PersistenceLog.Write($"LoadSongsAsync loaded {list.Count} songs");
            }
            catch(Exception ex)
            {
                PersistenceLog.Write("LoadSongsAsync failed: " + ex);
                throw;
            }
            return list;
        }
        private static System.DateTime ParseDate(SqliteDataReader r, int ord){ if(r.IsDBNull(ord)) return System.DateTime.MinValue; if(System.DateTime.TryParse(r.GetString(ord), out var dt)) return dt; return System.DateTime.MinValue; }
        public async Task SaveSongsAsync(IEnumerable<Song> songs)
        {
            try
            {
                using(var conn = new SqliteConnection(ConnectionString))
                {
                    await conn.OpenAsync();
                    using (var prag = conn.CreateCommand()) { prag.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;"; prag.ExecuteNonQuery(); }
                    using(var tx = conn.BeginTransaction())
                    {
                        var cmd = conn.CreateCommand();
                        cmd.CommandText = @"INSERT INTO Songs (SongId, Title, Artist, ActiveDate, LastModified, LeadDiff, BassDiff, VocalsDiff, DrumsDiff, ProLeadDiff, ProBassDiff)
VALUES ($id,$title,$artist,$active,$modified,$lead,$bass,$vocals,$drums,$plead,$pbass)
ON CONFLICT(SongId) DO UPDATE SET Title=$title, Artist=$artist, ActiveDate=$active, LastModified=$modified, LeadDiff=$lead, BassDiff=$bass, VocalsDiff=$vocals, DrumsDiff=$drums, ProLeadDiff=$plead, ProBassDiff=$pbass";
                        var parms = new[]{"$id","$title","$artist","$active","$modified","$lead","$bass","$vocals","$drums","$plead","$pbass"};
                        foreach(var p in parms) cmd.Parameters.Add(new SqliteParameter(p,0));
                        int count=0;
                        foreach(var s in songs)
                        {
                            cmd.Parameters[0].Value = s.track?.su??string.Empty;
                            cmd.Parameters[1].Value = s.track?.tt??string.Empty;
                            cmd.Parameters[2].Value = s.track?.an??string.Empty;
                            cmd.Parameters[3].Value = s._activeDate==System.DateTime.MinValue?"":s._activeDate.ToString("o");
                            cmd.Parameters[4].Value = s.lastModified==System.DateTime.MinValue?"":s.lastModified.ToString("o");
                            cmd.Parameters[5].Value = s.track?.@in?.gr??0;
                            cmd.Parameters[6].Value = s.track?.@in?.ba??0;
                            cmd.Parameters[7].Value = s.track?.@in?.vl??0;
                            cmd.Parameters[8].Value = s.track?.@in?.ds??0;
                            cmd.Parameters[9].Value = s.track?.@in?.pg??0;
                            cmd.Parameters[10].Value = s.track?.@in?.pb??0;
                            await cmd.ExecuteNonQueryAsync();
                            count++;
                        }
                        tx.Commit();
                        PersistenceLog.Write($"SaveSongsAsync persisted {count} songs");
                    }
                }
            }
            catch(Exception ex)
            {
                PersistenceLog.Write("SaveSongsAsync failed: " + ex);
                throw;
            }
        }
    }
}
