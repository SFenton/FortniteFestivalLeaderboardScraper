using FSTService.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

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
    private long _maxScoresCacheRevision;
    private readonly object _maxScoresCacheLock = new();
    private static readonly TimeSpan MaxScoresCacheTtl = TimeSpan.FromMinutes(5);

    public PathDataStore(NpgsqlDataSource dataSource, ILogger<PathDataStore>? log = null)
    {
        _ds = dataSource;
        _log = log;
    }

    public Dictionary<string, PathGenerationState> GetPathGenerationStates()
    {
        var result = new Dictionary<string, PathGenerationState>(StringComparer.OrdinalIgnoreCase);
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            {PathGenerationStateSelect}
            WHERE dat_file_hash IS NOT NULL
               OR path_artifact_generation_id IS NOT NULL
               OR path_generation_revision <> 0
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var state = ReadPathGenerationState(r);
            result[state.SongId] = state;
        }
        return result;
    }

    public PathGenerationState? GetPathGenerationState(string songId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            {PathGenerationStateSelect}
            WHERE song_id = @songId
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadPathGenerationState(r) : null;
    }

    public HashSet<string> GetPendingPathGenerationSongIds()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT song_id
            FROM songs
            WHERE path_generation_pending
            ORDER BY song_id
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    public Dictionary<string, SongMaxScores> GetAllMaxScores()
    {
        while (true)
        {
            long revision;
            lock (_maxScoresCacheLock)
            {
                if (_maxScoresCache is not null &&
                    DateTime.UtcNow - _maxScoresCacheTime < MaxScoresCacheTtl)
                {
                    return _maxScoresCache;
                }

                revision = _maxScoresCacheRevision;
            }

            var result = new Dictionary<string, SongMaxScores>(
                StringComparer.OrdinalIgnoreCase);
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT song_id,
                       max_lead_score, max_bass_score, max_drums_score,
                       max_vocals_score, max_pro_lead_score, max_pro_bass_score,
                       paths_generated_at, chopt_version, chopt_binary_sha256,
                       path_generation_profile, path_artifact_generation_id,
                       COALESCE(path_expected_instruments, ARRAY[]::TEXT[])
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
                    CHOptBinarySha256 = r.IsDBNull(9) ? null : r.GetString(9),
                    GenerationProfile = r.IsDBNull(10) ? null : r.GetString(10),
                    ArtifactGenerationId = r.IsDBNull(11) ? null : r.GetString(11),
                    ExpectedInstruments = r.GetFieldValue<string[]>(12),
                };
            }

            if (TryInstallMaxScoresCache(result, revision))
                return result;
        }
    }

    // Test seeding only. Runtime promotions go through the CAS transaction below.
    internal void UpdateMaxScores(
        string songId,
        SongMaxScores scores,
        string datFileHash,
        string? songLastModified = null)
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
                chopt_version      = @choptVer,
                chopt_binary_sha256 = @binaryHash,
                path_generation_profile = @profile,
                path_artifact_generation_id = @generationId,
                path_expected_instruments = @expected,
                path_generation_revision = path_generation_revision + 1
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
        cmd.Parameters.AddWithValue("binaryHash", (object?)scores.CHOptBinarySha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("profile", (object?)scores.GenerationProfile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("generationId", (object?)scores.ArtifactGenerationId ?? DBNull.Value);
        cmd.Parameters.Add("expected", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            scores.ExpectedInstruments.ToArray();
        var affected = cmd.ExecuteNonQuery();
        if (affected == 0)
            _log?.LogWarning("UpdateMaxScores: 0 rows affected for song {SongId}. Song may not exist in PG songs table.", songId);

        InvalidateMaxScoresCache();
    }

    public async Task<PathGenerationPromotionOutcome> TryPromoteGenerationAsync(
        PathGenerationPromotion promotion,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        long currentRevision;
        string? currentCatalogLastModified;
        await using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.Transaction = tx;
            lockCmd.CommandText = """
                SELECT path_generation_revision,
                       NULLIF(last_modified, '')
                FROM songs
                WHERE song_id = @songId
                FOR UPDATE
                """;
            lockCmd.Parameters.AddWithValue("songId", promotion.SongId);
            await using var reader = await lockCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                await tx.RollbackAsync(ct);
                return PathGenerationPromotionOutcome.SongMissing;
            }

            currentRevision = reader.GetInt64(0);
            currentCatalogLastModified =
                reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        if (currentRevision != promotion.ExpectedRevision ||
            !string.Equals(
                NormalizeLastModified(currentCatalogLastModified),
                NormalizeLastModified(promotion.SongLastModified),
                StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return PathGenerationPromotionOutcome.Conflict;
        }

        await using (var update = conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE songs
                SET max_lead_score = @lead,
                    max_bass_score = @bass,
                    max_drums_score = @drums,
                    max_vocals_score = @vocals,
                    max_pro_lead_score = @proLead,
                    max_pro_bass_score = @proBass,
                    dat_file_hash = @datHash,
                    song_last_modified = @songLastModified,
                    paths_generated_at = @generatedAt,
                    chopt_version = @choptVersion,
                    chopt_binary_sha256 = @binaryHash,
                    path_generation_profile = @profile,
                    path_artifact_generation_id = @generationId,
                    path_expected_instruments = @expectedInstruments,
                    path_generation_revision = path_generation_revision + 1,
                    path_generation_pending = FALSE
                WHERE song_id = @songId
                """;
            update.Parameters.AddWithValue("songId", promotion.SongId);
            update.Parameters.AddWithValue("lead", (object?)promotion.MaxScores.MaxLeadScore ?? DBNull.Value);
            update.Parameters.AddWithValue("bass", (object?)promotion.MaxScores.MaxBassScore ?? DBNull.Value);
            update.Parameters.AddWithValue("drums", (object?)promotion.MaxScores.MaxDrumsScore ?? DBNull.Value);
            update.Parameters.AddWithValue("vocals", (object?)promotion.MaxScores.MaxVocalsScore ?? DBNull.Value);
            update.Parameters.AddWithValue("proLead", (object?)promotion.MaxScores.MaxProLeadScore ?? DBNull.Value);
            update.Parameters.AddWithValue("proBass", (object?)promotion.MaxScores.MaxProBassScore ?? DBNull.Value);
            update.Parameters.AddWithValue("datHash", promotion.DatFileHash);
            update.Parameters.AddWithValue(
                "songLastModified",
                (object?)promotion.SongLastModified ?? DBNull.Value);
            update.Parameters.AddWithValue("generatedAt", promotion.GeneratedAtUtc);
            update.Parameters.AddWithValue("choptVersion", promotion.Runtime.Version);
            update.Parameters.AddWithValue("binaryHash", promotion.Runtime.BinarySha256);
            update.Parameters.AddWithValue("profile", promotion.Runtime.Profile);
            update.Parameters.AddWithValue("generationId", promotion.ArtifactGenerationId);
            update.Parameters.Add(
                "expectedInstruments",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                promotion.ExpectedInstruments.ToArray();
            await update.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        InvalidateMaxScoresCache();
        return PathGenerationPromotionOutcome.Promoted;
    }

    public async Task AppendPathGenerationErrorAsync(
        PathGenerationError error,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO path_generation_errors (
                attempt_id, song_id, dat_file_hash, chopt_version,
                chopt_binary_sha256, path_generation_profile,
                expected_instruments, failure_stage, instrument, difficulty,
                detail, created_at)
            VALUES (
                @attemptId, @songId, @datHash, @choptVersion,
                @binaryHash, @profile, @expected, @stage, @instrument,
                @difficulty, @detail, @createdAt)
            """;
        cmd.Parameters.AddWithValue("attemptId", error.AttemptId);
        cmd.Parameters.AddWithValue("songId", error.SongId);
        cmd.Parameters.AddWithValue("datHash", (object?)error.DatFileHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("choptVersion", (object?)error.ChoptVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("binaryHash", (object?)error.ChoptBinarySha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("profile", (object?)error.GenerationProfile ?? DBNull.Value);
        cmd.Parameters.Add("expected", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            error.ExpectedInstruments.ToArray();
        cmd.Parameters.AddWithValue("stage", error.FailureStage);
        cmd.Parameters.AddWithValue("instrument", (object?)error.Instrument ?? DBNull.Value);
        cmd.Parameters.AddWithValue("difficulty", (object?)error.Difficulty ?? DBNull.Value);
        cmd.Parameters.AddWithValue("detail", BoundDetail(error.Detail));
        cmd.Parameters.AddWithValue("createdAt", error.CreatedAtUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static PathGenerationState ReadPathGenerationState(NpgsqlDataReader r)
    {
        var scores = new SongMaxScores
        {
            MaxLeadScore = r.IsDBNull(10) ? null : r.GetInt32(10),
            MaxBassScore = r.IsDBNull(11) ? null : r.GetInt32(11),
            MaxDrumsScore = r.IsDBNull(12) ? null : r.GetInt32(12),
            MaxVocalsScore = r.IsDBNull(13) ? null : r.GetInt32(13),
            MaxProLeadScore = r.IsDBNull(14) ? null : r.GetInt32(14),
            MaxProBassScore = r.IsDBNull(15) ? null : r.GetInt32(15),
            GeneratedAt = r.IsDBNull(4) ? null : r.GetDateTime(4).ToString("o"),
            CHOptVersion = r.IsDBNull(5) ? null : r.GetString(5),
            CHOptBinarySha256 = r.IsDBNull(6) ? null : r.GetString(6),
            GenerationProfile = r.IsDBNull(7) ? null : r.GetString(7),
            ArtifactGenerationId = r.IsDBNull(8) ? null : r.GetString(8),
            ExpectedInstruments = r.GetFieldValue<string[]>(9),
        };

        return new PathGenerationState(
            r.GetString(0),
            r.GetInt64(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetDateTime(4),
            r.IsDBNull(5) ? null : r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7),
            r.IsDBNull(8) ? null : r.GetString(8),
            r.GetFieldValue<string[]>(9),
            scores);
    }

    private void InvalidateMaxScoresCache()
    {
        lock (_maxScoresCacheLock)
        {
            _maxScoresCacheRevision++;
            _maxScoresCache = null;
        }
    }

    private bool TryInstallMaxScoresCache(
        Dictionary<string, SongMaxScores> result,
        long expectedRevision)
    {
        lock (_maxScoresCacheLock)
        {
            if (_maxScoresCacheRevision != expectedRevision)
                return false;

            _maxScoresCache = result;
            _maxScoresCacheTime = DateTime.UtcNow;
            return true;
        }
    }

    private static string BoundDetail(string detail)
    {
        const int maxLength = 2048;
        var sanitized = detail.Replace('\0', ' ');
        return sanitized.Length <= maxLength
            ? sanitized
            : sanitized[..maxLength];
    }

    private static string? NormalizeLastModified(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private const string PathGenerationStateSelect = """
        SELECT song_id,
               path_generation_revision,
               dat_file_hash,
               song_last_modified,
               paths_generated_at,
               chopt_version,
               chopt_binary_sha256,
               path_generation_profile,
               path_artifact_generation_id,
               COALESCE(path_expected_instruments, ARRAY[]::TEXT[]),
               max_lead_score,
               max_bass_score,
               max_drums_score,
               max_vocals_score,
               max_pro_lead_score,
               max_pro_bass_score
        FROM songs
        """;
}
