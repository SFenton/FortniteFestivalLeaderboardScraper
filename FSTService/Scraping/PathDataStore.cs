using Microsoft.Data.Sqlite;

namespace FSTService.Scraping;

/// <summary>
/// Reads and writes path generation data (max scores, .dat hashes) in the
/// Songs table of fst-service.db.
/// </summary>
public sealed class PathDataStore : IPathDataStore
{
    private readonly string _connectionString;

    // ── In-memory cache for max scores (rarely changes) ──
    private Dictionary<string, SongMaxScores>? _maxScoresCache;
    private DateTime _maxScoresCacheTime;
    private readonly object _maxScoresCacheLock = new();
    private static readonly TimeSpan MaxScoresCacheTtl = TimeSpan.FromMinutes(5);

    public PathDataStore(string songDbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = songDbPath
        }.ToString();
    }

    /// <summary>
    /// Returns a dictionary of SongId → (DatFileHash, SongLastModified) for path generation state.
    /// Returns empty if the table or columns don't exist yet.
    /// </summary>
    public Dictionary<string, (string Hash, string? LastModified)> GetPathGenerationState()
    {
        var result = new Dictionary<string, (string, string?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT SongId, DatFileHash, SongLastModified FROM Songs WHERE DatFileHash IS NOT NULL";
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var songId = reader.GetString(0);
                var hash = reader.GetString(1);
                var lastMod = reader.IsDBNull(2) ? null : reader.GetString(2);
                result[songId] = (hash, lastMod);
            }
        }
        catch (SqliteException)
        {
            // Table or column doesn't exist yet — return empty
        }
        return result;
    }

    /// <summary>
    /// Returns a dictionary of SongId → MaxScores for all songs.
    /// Results are cached in-memory for 5 minutes.
    /// Returns empty if the table or columns don't exist yet.
    /// </summary>
    public Dictionary<string, SongMaxScores> GetAllMaxScores()
    {
        lock (_maxScoresCacheLock)
        {
            if (_maxScoresCache is not null && DateTime.UtcNow - _maxScoresCacheTime < MaxScoresCacheTtl)
                return _maxScoresCache;
        }

        var result = new Dictionary<string, SongMaxScores>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT SongId,
                       MaxLeadScore, MaxBassScore, MaxDrumsScore,
                       MaxVocalsScore, MaxProLeadScore, MaxProBassScore,
                       PathsGeneratedAt, CHOptVersion
                FROM Songs
                WHERE MaxLeadScore IS NOT NULL
                   OR MaxBassScore IS NOT NULL
                   OR MaxDrumsScore IS NOT NULL
                   OR MaxVocalsScore IS NOT NULL
                   OR MaxProLeadScore IS NOT NULL
                   OR MaxProBassScore IS NOT NULL
                """;
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var songId = reader.GetString(0);
                result[songId] = new SongMaxScores
                {
                    MaxLeadScore = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    MaxBassScore = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    MaxDrumsScore = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    MaxVocalsScore = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    MaxProLeadScore = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    MaxProBassScore = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    GeneratedAt = reader.IsDBNull(7) ? null : reader.GetString(7),
                    CHOptVersion = reader.IsDBNull(8) ? null : reader.GetString(8),
                };
            }
        }
        catch (SqliteException)
        {
            // Table or column doesn't exist yet — return empty
        }

        lock (_maxScoresCacheLock)
        {
            _maxScoresCache = result;
            _maxScoresCacheTime = DateTime.UtcNow;
        }
        return result;
    }

    /// <summary>
    /// Update max scores and .dat hash for a song after path generation.
    /// </summary>
    public void UpdateMaxScores(string songId, SongMaxScores scores, string datFileHash, string? songLastModified = null)
    {
        lock (_maxScoresCacheLock) { _maxScoresCache = null; }
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Songs
            SET MaxLeadScore     = @lead,
                MaxBassScore     = @bass,
                MaxDrumsScore    = @drums,
                MaxVocalsScore   = @vocals,
                MaxProLeadScore  = @proLead,
                MaxProBassScore  = @proBass,
                DatFileHash      = @hash,
                SongLastModified = @songLastMod,
                PathsGeneratedAt = @genAt,
                CHOptVersion     = @version
            WHERE SongId = @songId
            """;
        cmd.Parameters.AddWithValue("@lead", (object?)scores.MaxLeadScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bass", (object?)scores.MaxBassScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@drums", (object?)scores.MaxDrumsScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@vocals", (object?)scores.MaxVocalsScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@proLead", (object?)scores.MaxProLeadScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@proBass", (object?)scores.MaxProBassScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash", datFileHash);
        cmd.Parameters.AddWithValue("@songLastMod", (object?)songLastModified ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@genAt", scores.GeneratedAt ?? DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@version", (object?)scores.CHOptVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@songId", songId);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Max attainable scores for a song, one per instrument.
/// </summary>
public sealed class SongMaxScores
{
    public int? MaxLeadScore { get; set; }
    public int? MaxBassScore { get; set; }
    public int? MaxDrumsScore { get; set; }
    public int? MaxVocalsScore { get; set; }
    public int? MaxProLeadScore { get; set; }
    public int? MaxProBassScore { get; set; }
    public string? GeneratedAt { get; set; }
    public string? CHOptVersion { get; set; }

    /// <summary>
    /// Get max score by instrument DB name (e.g., "Solo_Guitar").
    /// </summary>
    public int? GetByInstrument(string instrument) => instrument switch
    {
        "Solo_Guitar" => MaxLeadScore,
        "Solo_Bass" => MaxBassScore,
        "Solo_Drums" => MaxDrumsScore,
        "Solo_Vocals" => MaxVocalsScore,
        "Solo_PeripheralGuitar" => MaxProLeadScore,
        "Solo_PeripheralBass" => MaxProBassScore,
        _ => null,
    };

    /// <summary>
    /// Set max score by instrument DB name.
    /// </summary>
    public void SetByInstrument(string instrument, int? score)
    {
        switch (instrument)
        {
            case "Solo_Guitar": MaxLeadScore = score; break;
            case "Solo_Bass": MaxBassScore = score; break;
            case "Solo_Drums": MaxDrumsScore = score; break;
            case "Solo_Vocals": MaxVocalsScore = score; break;
            case "Solo_PeripheralGuitar": MaxProLeadScore = score; break;
            case "Solo_PeripheralBass": MaxProBassScore = score; break;
        }
    }
}
