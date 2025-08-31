using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace FortniteFestivalLeaderboardScraper.Helpers.Data
{
    public static class SqlRepository
    {
        private static bool _initialized;
        private static string _dbPath;
        private static string _connString;
        private static readonly object _lock = new object();

        public static void Initialize()
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;
                var exe = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
                _dbPath = Path.Combine(exe, "FNFLS_data.sqlite");
                _connString = "Data Source=" + _dbPath + ";Cache=Shared";
                using (var c = new SqliteConnection(_connString))
                {
                    c.Open();
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS leaderboard_data (
songId TEXT PRIMARY KEY,
 title TEXT, artist TEXT,
 drums_initialized INTEGER, drums_maxScore INTEGER, drums_difficulty INTEGER, drums_numStars INTEGER, drums_isFullCombo INTEGER, drums_percentHit INTEGER, drums_seasonAchieved INTEGER,
 guitar_initialized INTEGER, guitar_maxScore INTEGER, guitar_difficulty INTEGER, guitar_numStars INTEGER, guitar_isFullCombo INTEGER, guitar_percentHit INTEGER, guitar_seasonAchieved INTEGER,
 bass_initialized INTEGER, bass_maxScore INTEGER, bass_difficulty INTEGER, bass_numStars INTEGER, bass_isFullCombo INTEGER, bass_percentHit INTEGER, bass_seasonAchieved INTEGER,
 vocals_initialized INTEGER, vocals_maxScore INTEGER, vocals_difficulty INTEGER, vocals_numStars INTEGER, vocals_isFullCombo INTEGER, vocals_percentHit INTEGER, vocals_seasonAchieved INTEGER,
 pro_guitar_initialized INTEGER, pro_guitar_maxScore INTEGER, pro_guitar_difficulty INTEGER, pro_guitar_numStars INTEGER, pro_guitar_isFullCombo INTEGER, pro_guitar_percentHit INTEGER, pro_guitar_seasonAchieved INTEGER,
 pro_bass_initialized INTEGER, pro_bass_maxScore INTEGER, pro_bass_difficulty INTEGER, pro_bass_numStars INTEGER, pro_bass_isFullCombo INTEGER, pro_bass_percentHit INTEGER, pro_bass_seasonAchieved INTEGER
);";
                        cmd.ExecuteNonQuery();
                    }
                }
                _initialized = true;
            }
        }

        private static SqliteConnection Conn()
        {
            Initialize();
            return new SqliteConnection(_connString);
        }

        private static void EnsureTrackers(LeaderboardData d)
        {
            if (d.drums == null) d.drums = new ScoreTracker();
            if (d.guitar == null) d.guitar = new ScoreTracker();
            if (d.bass == null) d.bass = new ScoreTracker();
            if (d.vocals == null) d.vocals = new ScoreTracker();
            if (d.pro_guitar == null) d.pro_guitar = new ScoreTracker();
            if (d.pro_bass == null) d.pro_bass = new ScoreTracker();
        }

        public static void Upsert(LeaderboardData data)
        {
            if (data == null || string.IsNullOrEmpty(data.songId)) return;
            BulkUpsert(new List<LeaderboardData> { data });
        }

        public static void BulkUpsert(List<LeaderboardData> list)
        {
            if (list == null || list.Count == 0) return;
            using (var c = Conn())
            {
                c.Open();
                using (var tx = c.BeginTransaction())
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO leaderboard_data (
 songId,title,artist,
 drums_initialized,drums_maxScore,drums_difficulty,drums_numStars,drums_isFullCombo,drums_percentHit,drums_seasonAchieved,
 guitar_initialized,guitar_maxScore,guitar_difficulty,guitar_numStars,guitar_isFullCombo,guitar_percentHit,guitar_seasonAchieved,
 bass_initialized,bass_maxScore,bass_difficulty,bass_numStars,bass_isFullCombo,bass_percentHit,bass_seasonAchieved,
 vocals_initialized,vocals_maxScore,vocals_difficulty,vocals_numStars,vocals_isFullCombo,vocals_percentHit,vocals_seasonAchieved,
 pro_guitar_initialized,pro_guitar_maxScore,pro_guitar_difficulty,pro_guitar_numStars,pro_guitar_isFullCombo,pro_guitar_percentHit,pro_guitar_seasonAchieved,
 pro_bass_initialized,pro_bass_maxScore,pro_bass_difficulty,pro_bass_numStars,pro_bass_isFullCombo,pro_bass_percentHit,pro_bass_seasonAchieved
) VALUES (
 @songId,@title,@artist,
 @d_init,@d_score,@d_diff,@d_stars,@d_fc,@d_pct,@d_season,
 @g_init,@g_score,@g_diff,@g_stars,@g_fc,@g_pct,@g_season,
 @b_init,@b_score,@b_diff,@b_stars,@b_fc,@b_pct,@b_season,
 @v_init,@v_score,@v_diff,@v_stars,@v_fc,@v_pct,@v_season,
 @pg_init,@pg_score,@pg_diff,@pg_stars,@pg_fc,@pg_pct,@pg_season,
 @pb_init,@pb_score,@pb_diff,@pb_stars,@pb_fc,@pb_pct,@pb_season
) ON CONFLICT(songId) DO UPDATE SET
 title=excluded.title, artist=excluded.artist,
 drums_initialized=excluded.drums_initialized, drums_maxScore=excluded.drums_maxScore, drums_difficulty=excluded.drums_difficulty, drums_numStars=excluded.drums_numStars, drums_isFullCombo=excluded.drums_isFullCombo, drums_percentHit=excluded.drums_percentHit, drums_seasonAchieved=excluded.drums_seasonAchieved,
 guitar_initialized=excluded.guitar_initialized, guitar_maxScore=excluded.guitar_maxScore, guitar_difficulty=excluded.guitar_difficulty, guitar_numStars=excluded.guitar_numStars, guitar_isFullCombo=excluded.guitar_isFullCombo, guitar_percentHit=excluded.guitar_percentHit, guitar_seasonAchieved=excluded.guitar_seasonAchieved,
 bass_initialized=excluded.bass_initialized, bass_maxScore=excluded.bass_maxScore, bass_difficulty=excluded.bass_difficulty, bass_numStars=excluded.bass_numStars, bass_isFullCombo=excluded.bass_isFullCombo, bass_percentHit=excluded.bass_percentHit, bass_seasonAchieved=excluded.bass_seasonAchieved,
 vocals_initialized=excluded.vocals_initialized, vocals_maxScore=excluded.vocals_maxScore, vocals_difficulty=excluded.vocals_difficulty, vocals_numStars=excluded.vocals_numStars, vocals_isFullCombo=excluded.vocals_isFullCombo, vocals_percentHit=excluded.vocals_percentHit, vocals_seasonAchieved=excluded.vocals_seasonAchieved,
 pro_guitar_initialized=excluded.pro_guitar_initialized, pro_guitar_maxScore=excluded.pro_guitar_maxScore, pro_guitar_difficulty=excluded.pro_guitar_difficulty, pro_guitar_numStars=excluded.pro_guitar_numStars, pro_guitar_isFullCombo=excluded.pro_guitar_isFullCombo, pro_guitar_percentHit=excluded.pro_guitar_percentHit, pro_guitar_seasonAchieved=excluded.pro_guitar_seasonAchieved,
 pro_bass_initialized=excluded.pro_bass_initialized, pro_bass_maxScore=excluded.pro_bass_maxScore, pro_bass_difficulty=excluded.pro_bass_difficulty, pro_bass_numStars=excluded.pro_bass_numStars, pro_bass_isFullCombo=excluded.pro_bass_isFullCombo, pro_bass_percentHit=excluded.pro_bass_percentHit, pro_bass_seasonAchieved=excluded.pro_bass_seasonAchieved;";

                    foreach (var d in list)
                    {
                        EnsureTrackers(d);
                        cmd.Parameters.Clear();
                        Add(cmd, "@songId", d.songId);
                        Add(cmd, "@title", d.title);
                        Add(cmd, "@artist", d.artist);
                        AddTracker(cmd, d.drums, "d");
                        AddTracker(cmd, d.guitar, "g");
                        AddTracker(cmd, d.bass, "b");
                        AddTracker(cmd, d.vocals, "v");
                        AddTracker(cmd, d.pro_guitar, "pg");
                        AddTracker(cmd, d.pro_bass, "pb");
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
        }

        private static void AddTracker(SqliteCommand cmd, ScoreTracker t, string prefix)
        {
            Add(cmd, $"@{prefix}_init", t.initialized ? 1 : 0);
            Add(cmd, $"@{prefix}_score", t.maxScore);
            Add(cmd, $"@{prefix}_diff", t.difficulty);
            Add(cmd, $"@{prefix}_stars", t.numStars);
            Add(cmd, $"@{prefix}_fc", t.isFullCombo ? 1 : 0);
            Add(cmd, $"@{prefix}_pct", t.percentHit);
            Add(cmd, $"@{prefix}_season", t.seasonAchieved);
        }

        private static void Add(SqliteCommand cmd, string name, object value) => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

        public static List<LeaderboardData> LoadAll()
        {
            var list = new List<LeaderboardData>();
            using (var c = Conn())
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM leaderboard_data";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read()) list.Add(Read(r));
                    }
                }
            }
            return list;
        }

        private static LeaderboardData Read(IDataRecord r)
        {
            return new LeaderboardData
            {
                songId = r["songId"].ToString(),
                title = r["title"].ToString(),
                artist = r["artist"].ToString(),
                drums = ReadTracker(r, "drums"),
                guitar = ReadTracker(r, "guitar"),
                bass = ReadTracker(r, "bass"),
                vocals = ReadTracker(r, "vocals"),
                pro_guitar = ReadTracker(r, "pro_guitar"),
                pro_bass = ReadTracker(r, "pro_bass")
            };
        }

        private static ScoreTracker ReadTracker(IDataRecord r, string p)
        {
            return new ScoreTracker
            {
                initialized = GetInt(r, p + "_initialized") == 1,
                maxScore = GetInt(r, p + "_maxScore"),
                difficulty = GetInt(r, p + "_difficulty"),
                numStars = GetInt(r, p + "_numStars"),
                isFullCombo = GetInt(r, p + "_isFullCombo") == 1,
                percentHit = GetInt(r, p + "_percentHit"),
                seasonAchieved = GetInt(r, p + "_seasonAchieved")
            };
        }

        private static int GetInt(IDataRecord r, string col)
        {
            int ord = r.GetOrdinal(col);
            if (r.IsDBNull(ord)) return 0;
            return Convert.ToInt32(r.GetValue(ord));
        }
    }
}
