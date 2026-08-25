using FSTService.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Scraping;

/// <summary>
/// Path generation data store (<see cref="IPathDataStore"/> implementation).
/// Reads/writes max scores and path generation state from the <c>songs</c> table.
/// Effective reads are served from the publication-bound
/// <c>publication_path_artifacts</c> snapshot when
/// <c>Scraper:UsePublicationPathArtifacts</c> is enabled.
/// </summary>
public sealed class PathDataStore : IPathDataStore
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<PathDataStore>? _log;
    private readonly IOptions<ScraperOptions>? _options;

    // ── In-memory cache for max scores (rarely changes) ──
    private Dictionary<string, SongMaxScores>? _maxScoresCache;
    private DateTime _maxScoresCacheTime;
    private long _maxScoresCacheRevision;
    private readonly object _maxScoresCacheLock = new();
    private static readonly TimeSpan MaxScoresCacheTtl = TimeSpan.FromMinutes(5);

    // ── Publication-scoped caches (never mixed with the live cache) ──
    private readonly object _publicationCacheLock = new();
    private readonly Dictionary<
        long,
        (DateTime CachedAtUtc, Dictionary<string, SongMaxScores> Scores)>
        _publicationMaxScoresCache = [];
    private readonly Dictionary<
        long,
        (
            DateTime CachedAtUtc,
            Dictionary<string, PathGenerationState> States)>
        _publicationStatesCache = [];
    private const int MaxCachedPublications = 3;
    private long? _currentPublicationId;
    private DateTime _currentPublicationCachedAtUtc;
    private static readonly TimeSpan CurrentPublicationTtl =
        TimeSpan.FromSeconds(5);

    public PathDataStore(
        NpgsqlDataSource dataSource,
        ILogger<PathDataStore>? log = null,
        IOptions<ScraperOptions>? options = null)
    {
        _ds = dataSource;
        _log = log;
        _options = options;
    }

    private bool UsePublicationArtifacts =>
        _options?.Value.UsePublicationPathArtifacts == true;

    public Dictionary<string, PathGenerationState> GetPathGenerationStates()
    {
        if (TryResolvePublicationScope(out var publicationId, out var explicitScope))
        {
            var scoped = GetPublicationPathGenerationStates(publicationId);
            if (scoped is not null)
                return scoped;
            if (explicitScope)
            {
                throw new PublicationPathArtifactsUnavailableException(
                    publicationId);
            }
        }

        return GetLivePathGenerationStates();
    }

    public Dictionary<string, PathGenerationState> GetLivePathGenerationStates()
    {
        var result = new Dictionary<string, PathGenerationState>(StringComparer.OrdinalIgnoreCase);
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {PathGenerationStateColumns}
            FROM songs
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
        if (TryResolvePublicationScope(out var publicationId, out var explicitScope))
        {
            var scoped = GetPublicationPathGenerationStates(publicationId);
            if (scoped is not null)
            {
                return scoped.TryGetValue(songId, out var state)
                    ? state
                    : null;
            }

            if (explicitScope)
            {
                throw new PublicationPathArtifactsUnavailableException(
                    publicationId);
            }
        }

        return GetLivePathGenerationState(songId);
    }

    public PathGenerationState? GetLivePathGenerationState(string songId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {PathGenerationStateColumns}
            FROM songs
            WHERE song_id = @songId
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadPathGenerationState(r) : null;
    }

    public IDisposable BeginPublicationRead(long publicationId)
        => PathDataStorePublicationScope.Begin(publicationId);

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

    // ── Automatic staging deferral state ──
    // A deferral never clears path_generation_pending. Pending remains the
    // durable record that work is owed; these columns only decide whether an
    // automatic attempt may run now.

    private static readonly TimeSpan RetryBackoffBase = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryBackoffCap = TimeSpan.FromHours(24);

    internal static DateTime ComputeNextAttemptAtUtc(
        DateTime nowUtc,
        int attemptCount)
    {
        var exponent = Math.Clamp(attemptCount - 1, 0, 8);
        var delayTicks = RetryBackoffBase.Ticks * (1L << exponent);
        var delay = delayTicks >= RetryBackoffCap.Ticks || delayTicks <= 0
            ? RetryBackoffCap
            : TimeSpan.FromTicks(delayTicks);
        return nowUtc + delay;
    }

    public IReadOnlyList<PathGenerationCandidate>
        GetAutomaticPathGenerationCandidates(DateTime nowUtc)
    {
        var result = new List<PathGenerationCandidate>();
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT song_id, path_generation_attempt_count
            FROM songs
            WHERE path_generation_pending
              AND (
                  path_generation_deferral_identity
                      IS DISTINCT FROM NULLIF(last_modified, '')
                  OR (
                      NOT path_generation_review_required
                      AND (
                          path_generation_next_attempt_at IS NULL
                          OR path_generation_next_attempt_at <= @now
                      )
                  )
              )
            ORDER BY song_id
            """;
        cmd.Parameters.AddWithValue("now", nowUtc);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PathGenerationCandidate(
                reader.GetString(0),
                reader.GetInt32(1)));
        }

        return result;
    }

    public async Task MarkPathGenerationReviewRequiredAsync(
        string songId,
        string reason,
        string? catalogIdentity,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE songs
            SET path_generation_review_required = TRUE,
                path_generation_review_reason = @reason,
                path_generation_review_at = @now,
                path_generation_next_attempt_at = NULL,
                path_generation_attempt_count =
                    path_generation_attempt_count + 1,
                path_generation_deferral_identity =
                    NULLIF(@catalogIdentity, '')
            WHERE song_id = @songId
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("reason", BoundDetail(reason));
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.Add(
            "catalogIdentity",
            NpgsqlDbType.Text).Value =
            (object?)catalogIdentity ?? DBNull.Value;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SchedulePathGenerationRetryAsync(
        string songId,
        string reason,
        string? catalogIdentity,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE songs
            SET path_generation_attempt_count =
                    path_generation_attempt_count + 1,
                path_generation_review_required = FALSE,
                path_generation_review_at = NULL,
                path_generation_review_reason = @reason,
                path_generation_next_attempt_at = @now + LEAST(
                    make_interval(
                        hours =>
                            (1 << LEAST(
                                GREATEST(
                                    path_generation_attempt_count, 0),
                                8))),
                    INTERVAL '24 hours'),
                path_generation_deferral_identity =
                    NULLIF(@catalogIdentity, '')
            WHERE song_id = @songId
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("reason", BoundDetail(reason));
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.Add(
            "catalogIdentity",
            NpgsqlDbType.Text).Value =
            (object?)catalogIdentity ?? DBNull.Value;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public bool RearmPathGeneration(string songId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE songs
            SET path_generation_review_required = FALSE,
                path_generation_review_reason = NULL,
                path_generation_review_at = NULL,
                path_generation_next_attempt_at = NULL,
                path_generation_attempt_count = 0,
                path_generation_deferral_identity = NULL
            WHERE song_id = @songId
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        return cmd.ExecuteNonQuery() == 1;
    }

    public PathGenerationDeferralState? GetPathGenerationDeferralState(
        string songId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT path_generation_pending,
                   path_generation_review_required,
                   path_generation_review_reason,
                   path_generation_review_at,
                   path_generation_next_attempt_at,
                   path_generation_attempt_count,
                   path_generation_deferral_identity
            FROM songs
            WHERE song_id = @songId
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new PathGenerationDeferralState(
            songId,
            reader.GetBoolean(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    public Dictionary<string, SongMaxScores> GetAllMaxScores()
    {
        if (TryResolvePublicationScope(out var publicationId, out var explicitScope))
        {
            var scoped = GetPublicationMaxScores(publicationId);
            if (scoped is not null)
                return scoped;
            if (explicitScope)
            {
                throw new PublicationPathArtifactsUnavailableException(
                    publicationId);
            }
        }

        return GetLiveAllMaxScores();
    }

    public Dictionary<string, SongMaxScores> GetLiveAllMaxScores()
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
                       max_pro_cymbals_score, max_pro_drums_score,
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
                   OR max_pro_cymbals_score IS NOT NULL
                   OR max_pro_drums_score IS NOT NULL
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var generationProfile =
                    r.IsDBNull(12) ? null : r.GetString(12);
                var hasInvalidPlasticDrumsScores =
                    PathGenerationProfiles.HasInvalidPlasticDrumsScores(
                        generationProfile);
                result[r.GetString(0)] = new SongMaxScores
                {
                    MaxLeadScore = r.IsDBNull(1) ? null : r.GetInt32(1),
                    MaxBassScore = r.IsDBNull(2) ? null : r.GetInt32(2),
                    MaxDrumsScore = r.IsDBNull(3) ? null : r.GetInt32(3),
                    MaxVocalsScore = r.IsDBNull(4) ? null : r.GetInt32(4),
                    MaxProLeadScore = r.IsDBNull(5) ? null : r.GetInt32(5),
                    MaxProBassScore = r.IsDBNull(6) ? null : r.GetInt32(6),
                    MaxProCymbalsScore =
                        hasInvalidPlasticDrumsScores || r.IsDBNull(7)
                            ? null
                            : r.GetInt32(7),
                    MaxProDrumsScore =
                        hasInvalidPlasticDrumsScores || r.IsDBNull(8)
                            ? null
                            : r.GetInt32(8),
                    GeneratedAt =
                        r.IsDBNull(9)
                            ? null
                            : r.GetDateTime(9).ToString("o"),
                    CHOptVersion =
                        r.IsDBNull(10) ? null : r.GetString(10),
                    CHOptBinarySha256 =
                        r.IsDBNull(11) ? null : r.GetString(11),
                    GenerationProfile = generationProfile,
                    ArtifactGenerationId =
                        r.IsDBNull(13) ? null : r.GetString(13),
                    ExpectedInstruments =
                        r.GetFieldValue<string[]>(14),
                };
            }

            if (TryInstallMaxScoresCache(result, revision))
                return result;
        }
    }

    public void InvalidateCachedState()
    {
        InvalidateMaxScoresCache();
        lock (_publicationCacheLock)
        {
            _publicationMaxScoresCache.Clear();
            _publicationStatesCache.Clear();
            _currentPublicationCachedAtUtc = default;
        }
    }

    /// <summary>
    /// Resolves the publication whose snapshot should serve effective reads.
    /// Returns false when live rows must be used.
    /// </summary>
    private bool TryResolvePublicationScope(
        out long publicationId,
        out bool explicitScope)
    {
        publicationId = 0;
        explicitScope = false;
        if (!UsePublicationArtifacts)
            return false;

        if (PathDataStorePublicationScope.CurrentPublicationId
            is long scoped)
        {
            publicationId = scoped;
            explicitScope = true;
            return true;
        }

        if (TryGetCurrentPublicationId(out var current))
        {
            publicationId = current;
            explicitScope = true;
            return true;
        }

        return false;
    }

    private bool TryGetCurrentPublicationId(out long publicationId)
    {
        lock (_publicationCacheLock)
        {
            if (_currentPublicationCachedAtUtc != default
                && DateTime.UtcNow - _currentPublicationCachedAtUtc
                    < CurrentPublicationTtl)
            {
                publicationId = _currentPublicationId ?? 0;
                return _currentPublicationId.HasValue;
            }
        }

        long? resolved = null;
        try
        {
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT current_publication_id
                FROM scrape_publication_state
                WHERE id = TRUE
                """;
            var value = cmd.ExecuteScalar();
            if (value is not null && value is not DBNull)
                resolved = Convert.ToInt64(value);
        }
        catch (NpgsqlException ex)
        {
            _log?.LogWarning(
                ex,
                "Failed to resolve the current publication for path artifact reads.");
            throw;
        }

        lock (_publicationCacheLock)
        {
            _currentPublicationId = resolved;
            _currentPublicationCachedAtUtc = DateTime.UtcNow;
        }

        publicationId = resolved ?? 0;
        return resolved.HasValue;
    }

    private Dictionary<string, SongMaxScores>? GetPublicationMaxScores(
        long publicationId)
    {
        lock (_publicationCacheLock)
        {
            if (_publicationMaxScoresCache.TryGetValue(
                    publicationId,
                    out var cached)
                && DateTime.UtcNow - cached.CachedAtUtc < MaxScoresCacheTtl)
            {
                return cached.Scores;
            }
        }

        var states = GetPublicationPathGenerationStates(publicationId);
        if (states is null)
            return null;

        var scores = new Dictionary<string, SongMaxScores>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var state in states.Values)
        {
            if (!HasAnyMaxScore(state.MaxScores))
                continue;
            scores[state.SongId] = state.MaxScores;
        }

        lock (_publicationCacheLock)
        {
            _publicationMaxScoresCache[publicationId] =
                (DateTime.UtcNow, scores);
            PruneStalePublicationCaches();
        }

        return scores;
    }

    private void PruneStalePublicationCaches()
    {
        while (_publicationStatesCache.Count > MaxCachedPublications)
        {
            var oldest = _publicationStatesCache
                .OrderBy(static entry => entry.Value.CachedAtUtc)
                .First()
                .Key;
            _publicationStatesCache.Remove(oldest);
            _publicationMaxScoresCache.Remove(oldest);
        }

        while (_publicationMaxScoresCache.Count > MaxCachedPublications)
        {
            var oldest = _publicationMaxScoresCache
                .OrderBy(static entry => entry.Value.CachedAtUtc)
                .First()
                .Key;
            _publicationMaxScoresCache.Remove(oldest);
        }
    }

    private static bool HasAnyMaxScore(SongMaxScores scores)
        => scores.MaxLeadScore.HasValue
           || scores.MaxBassScore.HasValue
           || scores.MaxDrumsScore.HasValue
           || scores.MaxVocalsScore.HasValue
           || scores.MaxProLeadScore.HasValue
           || scores.MaxProBassScore.HasValue
           || scores.MaxProCymbalsScore.HasValue
           || scores.MaxProDrumsScore.HasValue;

    /// <summary>
    /// Reads and caches the complete ready publication snapshot, or returns
    /// null when the binding is absent, incomplete, or no longer matches its
    /// canonical row count/hash.
    /// </summary>
    private Dictionary<string, PathGenerationState>?
        GetPublicationPathGenerationStates(long publicationId)
    {
        lock (_publicationCacheLock)
        {
            if (_publicationStatesCache.TryGetValue(
                    publicationId,
                    out var cached)
                && DateTime.UtcNow - cached.CachedAtUtc < MaxScoresCacheTtl)
            {
                return cached.States;
            }
        }

        var result = new Dictionary<string, PathGenerationState>(
            StringComparer.OrdinalIgnoreCase);
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {PublicationPathArtifactSchema.ReadColumns}
            FROM publication_path_artifacts artifact
            WHERE artifact.publication_id = @publicationId
              AND EXISTS (
                  SELECT 1
                  FROM publication_surface_bindings binding
                  JOIN publication_song_catalog catalog
                    ON catalog.publication_id = binding.publication_id
                  JOIN publication_generations generation
                    ON generation.publication_id =
                        binding.publication_id
                  WHERE binding.publication_id = @publicationId
                    AND binding.surface_name = 'path_artifacts'
                    AND binding.binding_kind =
                        'generation_path_artifact_manifest'
                    AND binding.status = 'ready'
                    AND catalog.is_exact
                    AND binding.row_count = catalog.song_count
                    AND binding.row_count = (
                        SELECT COUNT(*)
                        FROM publication_path_artifacts counted
                        WHERE counted.publication_id = @publicationId
                    )
                    AND binding.content_hash =
                        publication_path_artifact_manifest_sha256(
                            @publicationId)
                    AND binding.binding_json ->> 'publicationId' =
                        CAST(@publicationId AS text)
                    AND binding.binding_json ->> 'scrapeId' =
                        generation.scrape_id::text
                    AND binding.binding_json ->> 'contractVersion' =
                        CAST(@contractVersion AS text)
                    AND binding.binding_json ->> 'manifestVersion' =
                        CAST(@manifestVersion AS text)
              )
            ORDER BY artifact.song_id
            """;
        cmd.Parameters.AddWithValue("publicationId", publicationId);
        cmd.Parameters.AddWithValue(
            "contractVersion",
            PublicationPathArtifactSchema.ContractVersion);
        cmd.Parameters.AddWithValue(
            "manifestVersion",
            PublicationPathArtifactSchema.ManifestVersion);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var state = ReadPathGenerationState(reader);
            result[state.SongId] = state;
        }

        if (result.Count == 0)
            return null;

        lock (_publicationCacheLock)
        {
            _publicationStatesCache[publicationId] =
                (DateTime.UtcNow, result);
            PruneStalePublicationCaches();
        }

        return result;
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
                max_pro_cymbals_score = @proCymbals,
                max_pro_drums_score = @proDrums,
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
        cmd.Parameters.AddWithValue(
            "proCymbals",
            (object?)scores.MaxProCymbalsScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "proDrums",
            (object?)scores.MaxProDrumsScore ?? DBNull.Value);
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
            !ProviderTimestampIdentity.Equivalent(
                currentCatalogLastModified,
                promotion.SongLastModified))
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
                    max_pro_cymbals_score = @proCymbals,
                    max_pro_drums_score = @proDrums,
                    dat_file_hash = @datHash,
                    song_last_modified = @songLastModified,
                    paths_generated_at = @generatedAt,
                    chopt_version = @choptVersion,
                    chopt_binary_sha256 = @binaryHash,
                    path_generation_profile = @profile,
                    path_artifact_generation_id = @generationId,
                    path_expected_instruments = @expectedInstruments,
                    path_generation_revision = path_generation_revision + 1,
                    path_generation_pending = FALSE,
                    path_generation_review_required = FALSE,
                    path_generation_review_reason = NULL,
                    path_generation_review_at = NULL,
                    path_generation_next_attempt_at = NULL,
                    path_generation_attempt_count = 0,
                    path_generation_deferral_identity = NULL
                WHERE song_id = @songId
                """;
            update.Parameters.AddWithValue("songId", promotion.SongId);
            update.Parameters.AddWithValue("lead", (object?)promotion.MaxScores.MaxLeadScore ?? DBNull.Value);
            update.Parameters.AddWithValue("bass", (object?)promotion.MaxScores.MaxBassScore ?? DBNull.Value);
            update.Parameters.AddWithValue("drums", (object?)promotion.MaxScores.MaxDrumsScore ?? DBNull.Value);
            update.Parameters.AddWithValue("vocals", (object?)promotion.MaxScores.MaxVocalsScore ?? DBNull.Value);
            update.Parameters.AddWithValue("proLead", (object?)promotion.MaxScores.MaxProLeadScore ?? DBNull.Value);
            update.Parameters.AddWithValue("proBass", (object?)promotion.MaxScores.MaxProBassScore ?? DBNull.Value);
            update.Parameters.AddWithValue(
                "proCymbals",
                (object?)promotion.MaxScores.MaxProCymbalsScore
                    ?? DBNull.Value);
            update.Parameters.AddWithValue(
                "proDrums",
                (object?)promotion.MaxScores.MaxProDrumsScore
                    ?? DBNull.Value);
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

    public async Task<PathGenerationBatchPromotionResult>
        TryPromoteGenerationsAtomicallyAsync(
            IReadOnlyList<PathGenerationPromotion> promotions,
            CancellationToken ct)
        => await TryPromoteGenerationsAtomicallyCoreAsync(
            promotions,
            gate: null,
            connection: null,
            transaction: null,
            ct);

    public async Task<PathGenerationBatchPromotionResult>
        TryPromoteGenerationsAtomicallyAsync(
            IReadOnlyList<PathGenerationPromotion> promotions,
            PathGenerationBatchPromotionGate gate,
            IMaxScoreMaintenanceLease maintenanceLease,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(maintenanceLease);
        return await maintenanceLease.ExecuteTransactionAsync(
            "path-batch-promotion",
            requireSourceLocks: true,
            (connection, transaction, token) =>
                TryPromoteGenerationsAtomicallyCoreAsync(
                    promotions,
                    gate ?? throw new ArgumentNullException(
                        nameof(gate)),
                    connection,
                    transaction,
                    token),
            ct: ct);
    }

    private async Task<PathGenerationBatchPromotionResult>
            TryPromoteGenerationsAtomicallyCoreAsync(
            IReadOnlyList<PathGenerationPromotion> promotions,
            PathGenerationBatchPromotionGate? gate,
            NpgsqlConnection? connection,
            NpgsqlTransaction? transaction,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(promotions);
        if (promotions.Count == 0)
        {
            throw new ArgumentException(
                "Atomic path promotion requires at least one song.",
                nameof(promotions));
        }

        var ordered = promotions
            .OrderBy(promotion => promotion.SongId, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length > MaxScoreMaintenanceManifest.MaximumSongs
            || ordered
                .Select(promotion => promotion.SongId)
                .Distinct(StringComparer.Ordinal)
                .Count() != ordered.Length)
        {
            throw new ArgumentException(
                "Atomic path promotion requires a bounded unique song set.",
                nameof(promotions));
        }

        if ((connection is null) != (transaction is null))
        {
            throw new ArgumentException(
                "A supplied path-promotion connection requires its transaction.");
        }
        if (connection is not null
            && !ReferenceEquals(transaction!.Connection, connection))
        {
            throw new ArgumentException(
                "The path-promotion transaction must belong to the supplied connection.",
                nameof(transaction));
        }

        NpgsqlConnection? ownedConnection = null;
        NpgsqlTransaction? ownedTransaction = null;
        var conn = connection
            ?? (ownedConnection =
                await _ds.OpenConnectionAsync(ct));
        var tx = transaction
            ?? (ownedTransaction =
                await conn.BeginTransactionAsync(ct));
        try
        {
        await using (var timeout = conn.CreateCommand())
        {
            timeout.Transaction = tx;
            timeout.CommandText = """
                SET LOCAL lock_timeout = '5s';
                SET LOCAL statement_timeout = '30s';
                """;
            await timeout.ExecuteNonQueryAsync(ct);
        }

        if (gate is not null)
        {
            bool publicationGateValid;
            await using var publication = conn.CreateCommand();
            publication.Transaction = tx;
            publication.CommandText = """
                SELECT current_publication_id,
                       working_publication_id,
                       published_scrape_id,
                       public_reads_frozen,
                       public_reads_frozen_scrape_id,
                       public_reads_frozen_reason
                FROM scrape_publication_state
                WHERE id = TRUE
                FOR UPDATE
                """;
            await using var reader =
                await publication.ExecuteReaderAsync(ct);
            publicationGateValid =
                await reader.ReadAsync(ct)
                && !reader.IsDBNull(0)
                && reader.GetInt64(0) == gate.PublicationId
                && reader.IsDBNull(1)
                && !reader.IsDBNull(2)
                && Convert.ToInt64(reader.GetValue(2))
                    == gate.PublishedScrapeId
                && reader.GetBoolean(3)
                && !reader.IsDBNull(4)
                && Convert.ToInt64(reader.GetValue(4))
                    == gate.PublishedScrapeId
                && !reader.IsDBNull(5)
                && string.Equals(
                    reader.GetString(5),
                    gate.FreezeReason,
                    StringComparison.Ordinal);
            await reader.DisposeAsync();
            if (!publicationGateValid)
            {
                if (ownedTransaction is not null)
                    await tx.RollbackAsync(ct);
                return new PathGenerationBatchPromotionResult(
                    PathGenerationPromotionOutcome.Conflict,
                    0);
            }
        }

        var current = new Dictionary<
            string,
            (long Revision, string? CatalogLastModified)>(
            StringComparer.Ordinal);
        await using (var lockRows = conn.CreateCommand())
        {
            lockRows.Transaction = tx;
            lockRows.CommandText = """
                SELECT song_id,
                       path_generation_revision,
                       NULLIF(last_modified, '')
                FROM songs
                WHERE song_id = ANY(@songIds)
                ORDER BY song_id
                FOR UPDATE
                """;
            lockRows.Parameters.Add(
                "songIds",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                ordered.Select(promotion => promotion.SongId).ToArray();
            await using var reader = await lockRows.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                current[reader.GetString(0)] = (
                    reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }

        foreach (var promotion in ordered)
        {
            if (!current.TryGetValue(promotion.SongId, out var state))
            {
                if (ownedTransaction is not null)
                    await tx.RollbackAsync(ct);
                return new PathGenerationBatchPromotionResult(
                    PathGenerationPromotionOutcome.SongMissing,
                    0,
                    promotion.SongId);
            }
            if (state.Revision != promotion.ExpectedRevision
                || !ProviderTimestampIdentity.Equivalent(
                    state.CatalogLastModified,
                    promotion.SongLastModified))
            {
                if (ownedTransaction is not null)
                    await tx.RollbackAsync(ct);
                return new PathGenerationBatchPromotionResult(
                    PathGenerationPromotionOutcome.Conflict,
                    0,
                    promotion.SongId);
            }
        }

        foreach (var promotion in ordered)
        {
            await using var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE songs
                SET max_lead_score = @lead,
                    max_bass_score = @bass,
                    max_drums_score = @drums,
                    max_vocals_score = @vocals,
                    max_pro_lead_score = @proLead,
                    max_pro_bass_score = @proBass,
                    max_pro_cymbals_score = @proCymbals,
                    max_pro_drums_score = @proDrums,
                    dat_file_hash = @datHash,
                    song_last_modified = @songLastModified,
                    paths_generated_at = @generatedAt,
                    chopt_version = @choptVersion,
                    chopt_binary_sha256 = @binaryHash,
                    path_generation_profile = @profile,
                    path_artifact_generation_id = @generationId,
                    path_expected_instruments = @expectedInstruments,
                    path_generation_revision =
                        path_generation_revision + 1,
                    path_generation_pending = FALSE,
                    path_generation_review_required = FALSE,
                    path_generation_review_reason = NULL,
                    path_generation_review_at = NULL,
                    path_generation_next_attempt_at = NULL,
                    path_generation_attempt_count = 0,
                    path_generation_deferral_identity = NULL
                WHERE song_id = @songId
                  AND path_generation_revision = @expectedRevision
                """;
            update.Parameters.AddWithValue(
                "songId",
                promotion.SongId);
            update.Parameters.AddWithValue(
                "expectedRevision",
                promotion.ExpectedRevision);
            update.Parameters.AddWithValue(
                "lead",
                (object?)promotion.MaxScores.MaxLeadScore
                ?? DBNull.Value);
            update.Parameters.AddWithValue(
                "bass",
                (object?)promotion.MaxScores.MaxBassScore
                ?? DBNull.Value);
            update.Parameters.AddWithValue(
                "drums",
                (object?)promotion.MaxScores.MaxDrumsScore
                ?? DBNull.Value);
            update.Parameters.AddWithValue(
                "vocals",
                (object?)promotion.MaxScores.MaxVocalsScore
                ?? DBNull.Value);
            update.Parameters.AddWithValue(
                "proLead",
                (object?)promotion.MaxScores.MaxProLeadScore
                ?? DBNull.Value);
            update.Parameters.AddWithValue(
                "proBass",
                (object?)promotion.MaxScores.MaxProBassScore
                ?? DBNull.Value);
            update.Parameters.AddWithValue(
                "proCymbals",
                (object?)promotion.MaxScores.MaxProCymbalsScore
                ?? DBNull.Value);
            update.Parameters.AddWithValue(
                "proDrums",
                (object?)promotion.MaxScores.MaxProDrumsScore
                ?? DBNull.Value);
            update.Parameters.AddWithValue(
                "datHash",
                promotion.DatFileHash);
            update.Parameters.AddWithValue(
                "songLastModified",
                (object?)promotion.SongLastModified
                ?? DBNull.Value);
            update.Parameters.AddWithValue(
                "generatedAt",
                promotion.GeneratedAtUtc);
            update.Parameters.AddWithValue(
                "choptVersion",
                promotion.Runtime.Version);
            update.Parameters.AddWithValue(
                "binaryHash",
                promotion.Runtime.BinarySha256);
            update.Parameters.AddWithValue(
                "profile",
                promotion.Runtime.Profile);
            update.Parameters.AddWithValue(
                "generationId",
                promotion.ArtifactGenerationId);
            update.Parameters.Add(
                "expectedInstruments",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                promotion.ExpectedInstruments.ToArray();
            if (await update.ExecuteNonQueryAsync(ct) != 1)
            {
                if (ownedTransaction is not null)
                    await tx.RollbackAsync(ct);
                return new PathGenerationBatchPromotionResult(
                    PathGenerationPromotionOutcome.Conflict,
                    0,
                    promotion.SongId);
            }
        }

        if (ownedTransaction is not null)
        {
            await tx.CommitAsync(ct);
            InvalidateMaxScoresCache();
        }
        return new PathGenerationBatchPromotionResult(
            PathGenerationPromotionOutcome.Promoted,
            ordered.Length);
        }
        finally
        {
            if (ownedTransaction is not null)
                await ownedTransaction.DisposeAsync();
            if (ownedConnection is not null)
                await ownedConnection.DisposeAsync();
        }
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
        var generationProfile = r.IsDBNull(7) ? null : r.GetString(7);
        var hasInvalidPlasticDrumsScores =
            PathGenerationProfiles.HasInvalidPlasticDrumsScores(
                generationProfile);
        var scores = new SongMaxScores
        {
            MaxLeadScore = r.IsDBNull(10) ? null : r.GetInt32(10),
            MaxBassScore = r.IsDBNull(11) ? null : r.GetInt32(11),
            MaxDrumsScore = r.IsDBNull(12) ? null : r.GetInt32(12),
            MaxVocalsScore = r.IsDBNull(13) ? null : r.GetInt32(13),
            MaxProLeadScore = r.IsDBNull(14) ? null : r.GetInt32(14),
            MaxProBassScore = r.IsDBNull(15) ? null : r.GetInt32(15),
            MaxProCymbalsScore =
                hasInvalidPlasticDrumsScores || r.IsDBNull(16)
                    ? null
                    : r.GetInt32(16),
            MaxProDrumsScore =
                hasInvalidPlasticDrumsScores || r.IsDBNull(17)
                    ? null
                    : r.GetInt32(17),
            GeneratedAt = r.IsDBNull(4) ? null : r.GetDateTime(4).ToString("o"),
            CHOptVersion = r.IsDBNull(5) ? null : r.GetString(5),
            CHOptBinarySha256 = r.IsDBNull(6) ? null : r.GetString(6),
            GenerationProfile = generationProfile,
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
            generationProfile,
            r.IsDBNull(8) ? null : r.GetString(8),
            r.GetFieldValue<string[]>(9),
            scores,
            r.IsDBNull(18) ? null : r.GetString(18),
            r.GetBoolean(19));
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

    private const string PathGenerationStateColumns = """
        song_id,
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
               max_pro_bass_score,
               max_pro_cymbals_score,
               max_pro_drums_score,
               NULLIF(last_modified, ''),
               path_generation_pending
        """;
}
