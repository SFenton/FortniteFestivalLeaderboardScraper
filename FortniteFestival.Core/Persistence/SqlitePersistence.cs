using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace FortniteFestival.Core.Persistence
{
    public class SqlitePersistence : IFestivalPersistence
    {
        private readonly string _dbPath;
        public SqlitePersistence(string dbPath)
        {
            _dbPath = dbPath;
            EnsureDatabase();
        }

        private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();

        private void EnsureDatabase()
        {
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Songs (
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
        }

        public async Task<IList<LeaderboardData>> LoadScoresAsync()
        {
            var list = new List<LeaderboardData>();
            using (var conn = new SqliteConnection(ConnectionString))
            {
                await conn.OpenAsync();
                var sql = @"SELECT s.SongId, s.Title, s.Artist,
    sc.GuitarScore, sc.GuitarDiff, sc.GuitarStars, sc.GuitarFC, sc.GuitarPct, sc.GuitarSeason,
    sc.DrumsScore, sc.DrumsDiff, sc.DrumsStars, sc.DrumsFC, sc.DrumsPct, sc.DrumsSeason,
    sc.BassScore, sc.BassDiff, sc.BassStars, sc.BassFC, sc.BassPct, sc.BassSeason,
    sc.VocalsScore, sc.VocalsDiff, sc.VocalsStars, sc.VocalsFC, sc.VocalsPct, sc.VocalsSeason,
    sc.ProGuitarScore, sc.ProGuitarDiff, sc.ProGuitarStars, sc.ProGuitarFC, sc.ProGuitarPct, sc.ProGuitarSeason,
    sc.ProBassScore, sc.ProBassDiff, sc.ProBassStars, sc.ProBassFC, sc.ProBassPct, sc.ProBassSeason
FROM Songs s LEFT JOIN Scores sc ON s.SongId = sc.SongId";
                using (var cmd = conn.CreateCommand()) { cmd.CommandText = sql; using (var r = await cmd.ExecuteReaderAsync()) { while (await r.ReadAsync()) { var ld = new LeaderboardData { songId = r.GetString(0), title = r.IsDBNull(1)?null:r.GetString(1), artist = r.IsDBNull(2)?null:r.GetString(2) }; int ord = 3; Func<ScoreTracker> readTracker = () => { if (r.IsDBNull(ord)) { ord += 6; return null; } var t = new ScoreTracker { maxScore = r.IsDBNull(ord)?0:r.GetInt32(ord), difficulty = r.IsDBNull(ord+1)?0:r.GetInt32(ord+1), numStars = r.IsDBNull(ord+2)?0:r.GetInt32(ord+2), isFullCombo = !r.IsDBNull(ord+3) && r.GetInt32(ord+3)==1, percentHit = r.IsDBNull(ord+4)?0:r.GetInt32(ord+4), seasonAchieved = r.IsDBNull(ord+5)?0:r.GetInt32(ord+5), initialized = !r.IsDBNull(ord) && r.GetInt32(ord)>0 }; ord+=6; return t; };
                    ld.guitar = readTracker(); ld.drums = readTracker(); ld.bass = readTracker(); ld.vocals = readTracker(); ld.pro_guitar = readTracker(); ld.pro_bass = readTracker(); list.Add(ld); } } }
            }
            return list;
        }

        public async Task SaveScoresAsync(IEnumerable<LeaderboardData> scores)
        {
            using (var conn = new SqliteConnection(ConnectionString))
            {
                await conn.OpenAsync();
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var ld in scores)
                    {
                        // Upsert Song
                        using (var cmd = conn.CreateCommand())
                        {
                            var title = ld.title ?? string.Empty; var artist = ld.artist ?? string.Empty;
                            cmd.CommandText = @"INSERT INTO Songs (SongId, Title, Artist, ActiveDate, LastModified, LeadDiff, BassDiff, VocalsDiff, DrumsDiff, ProLeadDiff, ProBassDiff)
VALUES ($id,$title,$artist,'','','0','0','0','0','0','0')
ON CONFLICT(SongId) DO UPDATE SET Title=$title, Artist=$artist";
                            cmd.Parameters.AddWithValue("$id", ld.songId);
                            cmd.Parameters.AddWithValue("$title", title);
                            cmd.Parameters.AddWithValue("$artist", artist);
                            await cmd.ExecuteNonQueryAsync();
                        }
                        // Upsert Score
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"INSERT INTO Scores (SongId,
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
                            void Add(string name, ScoreTracker t, Func<ScoreTracker,int> sel) => cmd.Parameters.AddWithValue(name, t==null? (object)0 : sel(t));
                            ScoreTracker g = ld.guitar, d = ld.drums, b = ld.bass, v = ld.vocals, pg = ld.pro_guitar, pb = ld.pro_bass;
                            cmd.Parameters.AddWithValue("$id", ld.songId);
                            Add("$gScore", g, x=>x.maxScore); Add("$gDiff", g, x=>x.difficulty); Add("$gStars", g, x=>x.numStars); Add("$gFC", g, x=>x.isFullCombo?1:0); Add("$gPct", g, x=>x.percentHit); Add("$gSeason", g, x=>x.seasonAchieved);
                            Add("$dScore", d, x=>x.maxScore); Add("$dDiff", d, x=>x.difficulty); Add("$dStars", d, x=>x.numStars); Add("$dFC", d, x=>x.isFullCombo?1:0); Add("$dPct", d, x=>x.percentHit); Add("$dSeason", d, x=>x.seasonAchieved);
                            Add("$bScore", b, x=>x.maxScore); Add("$bDiff", b, x=>x.difficulty); Add("$bStars", b, x=>x.numStars); Add("$bFC", b, x=>x.isFullCombo?1:0); Add("$bPct", b, x=>x.percentHit); Add("$bSeason", b, x=>x.seasonAchieved);
                            Add("$vScore", v, x=>x.maxScore); Add("$vDiff", v, x=>x.difficulty); Add("$vStars", v, x=>x.numStars); Add("$vFC", v, x=>x.isFullCombo?1:0); Add("$vPct", v, x=>x.percentHit); Add("$vSeason", v, x=>x.seasonAchieved);
                            Add("$pgScore", pg, x=>x.maxScore); Add("$pgDiff", pg, x=>x.difficulty); Add("$pgStars", pg, x=>x.numStars); Add("$pgFC", pg, x=>x.isFullCombo?1:0); Add("$pgPct", pg, x=>x.percentHit); Add("$pgSeason", pg, x=>x.seasonAchieved);
                            Add("$pbScore", pb, x=>x.maxScore); Add("$pbDiff", pb, x=>x.difficulty); Add("$pbStars", pb, x=>x.numStars); Add("$pbFC", pb, x=>x.isFullCombo?1:0); Add("$pbPct", pb, x=>x.percentHit); Add("$pbSeason", pb, x=>x.seasonAchieved);
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }
                    tx.Commit();
                }
            }
        }

        public async Task<IList<Song>> LoadSongsAsync()
        {
            var list = new List<Song>();
            using (var conn = new SqliteConnection(ConnectionString))
            {
                await conn.OpenAsync();
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
            return list;
        }
        private static System.DateTime ParseDate(SqliteDataReader r, int ord){ if(r.IsDBNull(ord)) return System.DateTime.MinValue; if(System.DateTime.TryParse(r.GetString(ord), out var dt)) return dt; return System.DateTime.MinValue; }
        public async Task SaveSongsAsync(IEnumerable<Song> songs)
        {
            using(var conn = new SqliteConnection(ConnectionString))
            {
                await conn.OpenAsync();
                using(var tx = conn.BeginTransaction())
                {
                    foreach(var s in songs)
                    {
                        using(var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"INSERT INTO Songs (SongId, Title, Artist, ActiveDate, LastModified, LeadDiff, BassDiff, VocalsDiff, DrumsDiff, ProLeadDiff, ProBassDiff)
VALUES ($id,$title,$artist,$active,$modified,$lead,$bass,$vocals,$drums,$plead,$pbass)
ON CONFLICT(SongId) DO UPDATE SET Title=$title, Artist=$artist, ActiveDate=$active, LastModified=$modified, LeadDiff=$lead, BassDiff=$bass, VocalsDiff=$vocals, DrumsDiff=$drums, ProLeadDiff=$plead, ProBassDiff=$pbass";
                            cmd.Parameters.AddWithValue("$id", s.track?.su??string.Empty);
                            cmd.Parameters.AddWithValue("$title", s.track?.tt??string.Empty);
                            cmd.Parameters.AddWithValue("$artist", s.track?.an??string.Empty);
                            cmd.Parameters.AddWithValue("$active", s._activeDate==System.DateTime.MinValue?"":s._activeDate.ToString("o"));
                            cmd.Parameters.AddWithValue("$modified", s.lastModified==System.DateTime.MinValue?"":s.lastModified.ToString("o"));
                            cmd.Parameters.AddWithValue("$lead", s.track?.@in?.gr??0);
                            cmd.Parameters.AddWithValue("$bass", s.track?.@in?.ba??0);
                            cmd.Parameters.AddWithValue("$vocals", s.track?.@in?.vl??0);
                            cmd.Parameters.AddWithValue("$drums", s.track?.@in?.ds??0);
                            cmd.Parameters.AddWithValue("$plead", s.track?.@in?.pg??0);
                            cmd.Parameters.AddWithValue("$pbass", s.track?.@in?.pb??0);
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }
                    tx.Commit();
                }
            }
        }
    }
}
