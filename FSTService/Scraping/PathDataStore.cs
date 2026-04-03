using FSTService.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FSTService.Scraping;

/// <summary>
/// Path generation data store (<see cref="IPathDataStore"/> implementation).
/// Reads/writes max scores and path generation state from the <c>songs</c> table.
/// </summary>
public sealed class PathDataStore : IPathDataStore
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<PathDataStore>? _log;

    // ── In-memory cache for max scores (rarely changes) ──
    private Dictionary<string, SongMaxScores>? _maxScoresCache;
    private DateTime _maxScoresCacheTime;
    private readonly object _maxScoresCacheLock = new();
    private static readonly TimeSpan MaxScoresCacheTtl = TimeSpan.FromMinutes(5);

    public PathDataStore(NpgsqlDataSource dataSource, ILogger<PathDataStore>? log = null)
    {
        _ds = dataSource;
        _log = log;
    }

    public Dictionary<string, (string Hash, string? LastModified)> GetPathGenerationState()
    {
        var result = new Dictionary<string, (string, string?)>(StringComparer.OrdinalIgnoreCase);
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT song_id, dat_file_hash, song_last_modified FROM songs WHERE dat_file_hash IS NOT NULL";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[r.GetString(0)] = (r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
        return result;
    }

    public Dictionary<string, SongMaxScores> GetAllMaxScores()
    {
        lock (_maxScoresCacheLock)
        {
            if (_maxScoresCache is not null && DateTime.UtcNow - _maxScoresCacheTime < MaxScoresCacheTtl)
                return _maxScoresCache;
        }

        var result = new Dictionary<string, SongMaxScores>(StringComparer.OrdinalIgnoreCase);
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT song_id,
                   max_lead_score, max_bass_score, max_drums_score,
                   max_vocals_score, max_pro_lead_score, max_pro_bass_score,
                   paths_generated_at, chopt_version
            FROM songs
            WHERE max_lead_score IS NOT NULL
               OR max_bass_score IS NOT NULL
               OR max_drums_score IS NOT NULL
               OR max_vocals_score IS NOT NULL
               OR max_pro_lead_score IS NOT NULL
               OR max_pro_bass_score IS NOT NULL
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result[r.GetString(0)] = new SongMaxScores
            {
                MaxLeadScore = r.IsDBNull(1) ? null : r.GetInt32(1),
                MaxBassScore = r.IsDBNull(2) ? null : r.GetInt32(2),
                MaxDrumsScore = r.IsDBNull(3) ? null : r.GetInt32(3),
                MaxVocalsScore = r.IsDBNull(4) ? null : r.GetInt32(4),
                MaxProLeadScore = r.IsDBNull(5) ? null : r.GetInt32(5),
                MaxProBassScore = r.IsDBNull(6) ? null : r.GetInt32(6),
                GeneratedAt = r.IsDBNull(7) ? null : r.GetDateTime(7).ToString("o"),
                CHOptVersion = r.IsDBNull(8) ? null : r.GetString(8),
            };
        }

        lock (_maxScoresCacheLock)
        {
            _maxScoresCache = result;
            _maxScoresCacheTime = DateTime.UtcNow;
        }
        return result;
    }

    public void UpdateMaxScores(string songId, SongMaxScores scores, string datFileHash, string? songLastModified = null)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE songs
            SET max_lead_score     = @lead,
                max_bass_score     = @bass,
                max_drums_score    = @drums,
                max_vocals_score   = @vocals,
                max_pro_lead_score = @proLead,
                max_pro_bass_score = @proBass,
                dat_file_hash      = @hash,
                song_last_modified = @songLastMod,
                paths_generated_at = @genAt,
                chopt_version      = @choptVer
            WHERE song_id = @songId
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("lead", (object?)scores.MaxLeadScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("bass", (object?)scores.MaxBassScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("drums", (object?)scores.MaxDrumsScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("vocals", (object?)scores.MaxVocalsScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("proLead", (object?)scores.MaxProLeadScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("proBass", (object?)scores.MaxProBassScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("hash", datFileHash);
        cmd.Parameters.AddWithValue("songLastMod", (object?)songLastModified ?? DBNull.Value);
        cmd.Parameters.AddWithValue("genAt", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("choptVer", (object?)scores.CHOptVersion ?? DBNull.Value);
        var affected = cmd.ExecuteNonQuery();
        if (affected == 0)
            _log?.LogWarning("UpdateMaxScores: 0 rows affected for song {SongId}. Song may not exist in PG songs table.", songId);

        lock (_maxScoresCacheLock)
        {
            _maxScoresCache = null; // invalidate cache
        }
    }
}
