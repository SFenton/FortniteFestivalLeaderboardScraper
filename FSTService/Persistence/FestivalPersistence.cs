using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

/// <summary>
/// <see cref="IFestivalPersistence"/> implementation for the Core library's
/// FestivalService. Reads/writes the <c>songs</c> table.
/// The Scores table is not used — leaderboard data lives in leaderboard_entries.
/// </summary>
public sealed class FestivalPersistence : IFestivalPersistence
{
    private readonly NpgsqlDataSource _ds;

    public FestivalPersistence(NpgsqlDataSource dataSource)
    {
        _ds = dataSource;
    }

    public async Task<IList<Song>> LoadSongsAsync()
    {
        var list = new List<Song>();
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT song_id, title, artist, active_date, last_modified, image_path,
                   lead_diff, bass_diff, vocals_diff, drums_diff,
                   pro_lead_diff, pro_bass_diff, release_year, tempo,
                   plastic_guitar_diff, plastic_bass_diff, plastic_drums_diff, pro_vocals_diff
            FROM songs
            """;
        await using var r = await cmd.ExecuteReaderAsync();
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
                        gr = r.IsDBNull(6) ? 0 : r.GetInt32(6),
                        ba = r.IsDBNull(7) ? 0 : r.GetInt32(7),
                        vl = r.IsDBNull(8) ? 0 : r.GetInt32(8),
                        ds = r.IsDBNull(9) ? 0 : r.GetInt32(9),
                        pg = r.IsDBNull(10) ? 0 : r.GetInt32(10),
                        pb = r.IsDBNull(11) ? 0 : r.GetInt32(11),
                    },
                    ry = r.IsDBNull(12) ? 0 : r.GetInt32(12),
                    mt = r.IsDBNull(13) ? 0 : r.GetInt32(13),
                },
                _activeDate = ParseDate(r, 3),
                lastModified = ParseDate(r, 4),
                imagePath = r.IsDBNull(5) ? null : r.GetString(5),
            };
            if (song.track?.@in != null)
            {
                if (!r.IsDBNull(14)) song.track.@in.pg = r.GetInt32(14);
                if (!r.IsDBNull(15)) song.track.@in.pb = r.GetInt32(15);
                if (!r.IsDBNull(16)) song.track.@in.pd = r.GetInt32(16);
                if (!r.IsDBNull(17))
                {
                    var pv = r.GetInt32(17);
                    song.track.@in.bd = pv <= 0 ? 0 : pv;
                }
            }
            list.Add(song);
        }
        return list;
    }

    public async Task SaveSongsAsync(IEnumerable<Song> songs)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        foreach (var s in songs)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO songs (song_id, title, artist, active_date, last_modified, image_path,
                                   lead_diff, bass_diff, vocals_diff, drums_diff,
                                   pro_lead_diff, pro_bass_diff, release_year, tempo,
                                   plastic_guitar_diff, plastic_bass_diff, plastic_drums_diff, pro_vocals_diff)
                VALUES (@id, @title, @artist, @active, @modified, @image,
                        @lead, @bass, @vocals, @drums, @plead, @pbass, @ry, @tempo,
                        @plGtr, @plBass, @plDrums, @proVocals)
                ON CONFLICT (song_id) DO UPDATE SET
                    title = EXCLUDED.title, artist = EXCLUDED.artist,
                    active_date = EXCLUDED.active_date, last_modified = EXCLUDED.last_modified,
                    image_path = EXCLUDED.image_path,
                    lead_diff = EXCLUDED.lead_diff, bass_diff = EXCLUDED.bass_diff,
                    vocals_diff = EXCLUDED.vocals_diff, drums_diff = EXCLUDED.drums_diff,
                    pro_lead_diff = EXCLUDED.pro_lead_diff, pro_bass_diff = EXCLUDED.pro_bass_diff,
                    release_year = EXCLUDED.release_year, tempo = EXCLUDED.tempo,
                    plastic_guitar_diff = EXCLUDED.plastic_guitar_diff, plastic_bass_diff = EXCLUDED.plastic_bass_diff,
                    plastic_drums_diff = EXCLUDED.plastic_drums_diff, pro_vocals_diff = EXCLUDED.pro_vocals_diff
                """;

            var proVocals = s.track?.@in?.bd ?? 0;
            if (proVocals == 0) proVocals = -1;

            cmd.Parameters.AddWithValue("id", s.track?.su ?? string.Empty);
            cmd.Parameters.AddWithValue("title", s.track?.tt ?? string.Empty);
            cmd.Parameters.AddWithValue("artist", s.track?.an ?? string.Empty);
            cmd.Parameters.AddWithValue("active", s._activeDate == DateTime.MinValue ? "" : s._activeDate.ToString("o"));
            cmd.Parameters.AddWithValue("modified", s.lastModified == DateTime.MinValue ? "" : s.lastModified.ToString("o"));
            cmd.Parameters.AddWithValue("image", s.imagePath ?? string.Empty);
            cmd.Parameters.AddWithValue("lead", s.track?.@in?.gr ?? 0);
            cmd.Parameters.AddWithValue("bass", s.track?.@in?.ba ?? 0);
            cmd.Parameters.AddWithValue("vocals", s.track?.@in?.vl ?? 0);
            cmd.Parameters.AddWithValue("drums", s.track?.@in?.ds ?? 0);
            cmd.Parameters.AddWithValue("plead", s.track?.@in?.pg ?? 0);
            cmd.Parameters.AddWithValue("pbass", s.track?.@in?.pb ?? 0);
            cmd.Parameters.AddWithValue("ry", s.track?.ry ?? 0);
            cmd.Parameters.AddWithValue("tempo", s.track?.mt ?? 0);
            cmd.Parameters.AddWithValue("plGtr", s.track?.@in?.pg ?? 0);
            cmd.Parameters.AddWithValue("plBass", s.track?.@in?.pb ?? 0);
            cmd.Parameters.AddWithValue("plDrums", s.track?.@in?.pd ?? 0);
            cmd.Parameters.AddWithValue("proVocals", proVocals);

            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    public Task<IList<LeaderboardData>> LoadScoresAsync()
    {
        // The per-user Scores table is deprecated in PG — data lives in leaderboard_entries
        return Task.FromResult<IList<LeaderboardData>>(new List<LeaderboardData>());
    }

    public Task SaveScoresAsync(IEnumerable<LeaderboardData> scores)
    {
        // No-op: scores are managed by GlobalLeaderboardPersistence via leaderboard_entries
        return Task.CompletedTask;
    }

    private static DateTime ParseDate(NpgsqlDataReader r, int ord)
    {
        if (r.IsDBNull(ord)) return DateTime.MinValue;
        var s = r.GetString(ord);
        return DateTime.TryParse(s, out var dt) ? dt : DateTime.MinValue;
    }
}
