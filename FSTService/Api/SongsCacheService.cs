using System.Text.Json;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Api;

internal readonly record struct SongsCacheBuildToken(
    long ContentRevision,
    PublicReadCacheSafetySnapshot Safety,
    long? PublicationId,
    bool ContentMutationInProgress);

internal enum SongsCacheWriteResult
{
    Stored,
    Stale,
    Blocked,
}

/// <summary>
/// Caches the serialized /api/songs JSON response and its ETag.
/// Primed eagerly after scrape passes, path generation, and catalog sync.
/// Falls back to on-demand build if not yet primed.
/// </summary>
public sealed class SongsCacheService
{
    private readonly object _lock = new();
    private readonly Func<PublicReadCacheSafetySnapshot> _safetyProvider;
    private readonly Func<long?>? _publicationIdProvider;
    private readonly TimeSpan _cacheTtl;
    private byte[]? _cachedJson;
    private string? _etag;
    private DateTime _cachedAt;
    private long? _publicationId;
    private long _safetyRevision;
    private long _contentRevision;
    private int _contentMutationDepth;
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);

    public SongsCacheService(
        PublicReadGateService? publicReadGate = null,
        Func<long?>? publicationIdProvider = null)
        : this(
            publicReadGate is null
                ? static () => default
                : publicReadGate.GetCacheSafetySnapshot,
            DefaultCacheTtl,
            publicationIdProvider)
    {
    }

    public SongsCacheService(Func<bool> isFrozen, TimeSpan cacheTtl)
        : this(
            () => new PublicReadCacheSafetySnapshot(
                isFrozen(),
                false,
                0),
            cacheTtl,
            null)
    {
    }

    internal SongsCacheService(
        Func<bool> isFrozen,
        TimeSpan cacheTtl,
        Func<bool> isFailedCandidateIsolation,
        Func<long?>? publicationIdProvider = null)
        : this(
            () => new PublicReadCacheSafetySnapshot(
                isFrozen(),
                isFailedCandidateIsolation(),
                0),
            cacheTtl,
            publicationIdProvider)
    {
    }

    internal SongsCacheService(
        Func<PublicReadCacheSafetySnapshot> safetyProvider,
        TimeSpan cacheTtl,
        Func<long?>? publicationIdProvider = null)
    {
        _safetyProvider =
            safetyProvider
            ?? throw new ArgumentNullException(nameof(safetyProvider));
        _publicationIdProvider = publicationIdProvider;
        _cacheTtl = cacheTtl;
    }

    /// <summary>
    /// Returns (json, etag) if cached and not expired; otherwise null.
    /// </summary>
    public (byte[] Json, string ETag)? Get()
    {
        var safety = _safetyProvider();
        lock (_lock)
        {
            if (ClearForFailedCandidateIsolation(safety))
                return null;
            if (ClearForPublicationMismatch())
                return null;
            if (ClearAfterCompletedSafetyTransition(safety))
                return null;

            if (_cachedJson is not null &&
                (safety.IsFrozen ||
                 DateTime.UtcNow - _cachedAt < _cacheTtl))
                return (_cachedJson, _etag!);
            return null;
        }
    }

    /// <summary>
    /// Returns the last cached response regardless of freshness.
    /// Used as a last-good fallback when rebuilding the live songs payload fails.
    /// </summary>
    public (byte[] Json, string ETag)? GetStale()
    {
        var safety = _safetyProvider();
        lock (_lock)
        {
            if (ClearForFailedCandidateIsolation(safety))
                return null;
            if (ClearForPublicationMismatch())
                return null;
            if (ClearAfterCompletedSafetyTransition(safety))
                return null;

            return _cachedJson is null ? null : (_cachedJson, _etag!);
        }
    }

    /// <summary>
    /// Stores the serialized JSON response and computes an ETag.
    /// </summary>
    public string Set(byte[] json)
    {
        var etag = ResponseCacheService.ComputeETag(json);
        var safety = _safetyProvider();
        lock (_lock)
        {
            if (ClearForFailedCandidateIsolation(safety))
                return etag;
            if (safety.IsFrozen)
                return etag;

            _contentRevision++;
            _cachedJson = json;
            _etag = etag;
            _cachedAt = DateTime.UtcNow;
            _publicationId = _publicationIdProvider?.Invoke();
            _safetyRevision = safety.Revision;
        }
        return etag;
    }

    /// <summary>
    /// Invalidates the cache so the next request rebuilds it.
    /// </summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _contentRevision++;
            _cachedJson = null;
            _etag = null;
            _publicationId = null;
            _safetyRevision = 0;
        }
    }

    internal SongsCacheBuildToken CaptureBuildToken()
    {
        var safety = _safetyProvider();
        var publicationId = _publicationIdProvider?.Invoke();
        lock (_lock)
        {
            return new SongsCacheBuildToken(
                _contentRevision,
                safety,
                publicationId,
                _contentMutationDepth > 0);
        }
    }

    internal SongsCacheWriteResult TrySetIfBuildTokenUnchanged(
        byte[] json,
        SongsCacheBuildToken token,
        out string etag)
    {
        etag = ResponseCacheService.ComputeETag(json);
        var safety = _safetyProvider();
        var publicationId = _publicationIdProvider?.Invoke();
        lock (_lock)
        {
            if (_contentRevision != token.ContentRevision ||
                safety.Revision != token.Safety.Revision ||
                publicationId != token.PublicationId)
            {
                return SongsCacheWriteResult.Stale;
            }

            if (safety.IsFrozen ||
                safety.FailedCandidateIsolationActive ||
                token.Safety.IsFrozen ||
                token.Safety.FailedCandidateIsolationActive ||
                token.ContentMutationInProgress ||
                _contentMutationDepth > 0)
            {
                return SongsCacheWriteResult.Blocked;
            }

            _contentRevision++;
            _cachedJson = json;
            _etag = etag;
            _cachedAt = DateTime.UtcNow;
            _publicationId = publicationId;
            _safetyRevision = safety.Revision;
            return SongsCacheWriteResult.Stored;
        }
    }

    internal IDisposable BeginContentMutation()
    {
        lock (_lock)
        {
            _contentMutationDepth++;
            _contentRevision++;
            _cachedJson = null;
            _etag = null;
            _publicationId = null;
            _safetyRevision = 0;
        }

        return new ContentMutationLease(this);
    }

    private void EndContentMutation()
    {
        lock (_lock)
        {
            if (_contentMutationDepth <= 0)
                return;

            _contentMutationDepth--;
            _contentRevision++;
            _cachedJson = null;
            _etag = null;
            _publicationId = null;
            _safetyRevision = 0;
        }
    }

    private bool ClearForFailedCandidateIsolation(
        PublicReadCacheSafetySnapshot safety)
    {
        if (!safety.FailedCandidateIsolationActive)
            return false;

        _contentRevision++;
        _cachedJson = null;
        _etag = null;
        _publicationId = null;
        _safetyRevision = 0;
        return true;
    }

    private bool ClearForPublicationMismatch()
    {
        if (_publicationIdProvider is null
            || _cachedJson is null
            || _publicationId == _publicationIdProvider())
        {
            return false;
        }

        _cachedJson = null;
        _etag = null;
        _publicationId = null;
        _safetyRevision = 0;
        _contentRevision++;
        return true;
    }

    private bool ClearAfterCompletedSafetyTransition(
        PublicReadCacheSafetySnapshot safety)
    {
        if (_cachedJson is null ||
            safety.IsFrozen ||
            _safetyRevision == safety.Revision)
        {
            return false;
        }

        _contentRevision++;
        _cachedJson = null;
        _etag = null;
        _publicationId = null;
        _safetyRevision = 0;
        return true;
    }

    /// <summary>
    /// Builds the songs JSON and primes the cache in one call.
    /// Replaces Invalidate() at all call sites.
    /// </summary>
    public void Prime(
        FestivalService service,
        IPathDataStore pathStore,
        IMetaDatabase metaDb,
        GlobalLeaderboardPersistence persistence,
        ScrapeTimePrecomputer precomputer,
        JsonSerializerOptions jsonOpts)
    {
        while (true)
        {
            var token = CaptureBuildToken();
            var jsonBytes = BuildSongsJson(
                service,
                pathStore,
                metaDb,
                persistence,
                precomputer,
                jsonOpts);
            var result = TrySetIfBuildTokenUnchanged(
                jsonBytes,
                token,
                out _);
            if (result is SongsCacheWriteResult.Stored
                or SongsCacheWriteResult.Blocked)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Builds the /api/songs JSON payload from current data sources.
    /// </summary>
    public static byte[] BuildSongsJson(
        FestivalService service,
        IPathDataStore pathStore,
        IMetaDatabase metaDb,
        GlobalLeaderboardPersistence persistence,
        ScrapeTimePrecomputer precomputer,
        JsonSerializerOptions jsonOpts)
    {
        var maxScoresMap = pathStore.GetAllMaxScores();
        // Prefer the season_windows table (authoritative, set by events-API discovery)
        // but floor by the max season observed across instrument DBs. This defends
        // against Epic renaming a window in a way our regex doesn't match: if S14
        // data has already been persisted, the UI reflects it even before the next
        // events-API refresh upserts a matching season_windows row.
        var metaSeason = metaDb.GetCurrentSeason();
        var instrumentMax = persistence.GetMaxSeasonAcrossInstruments() ?? 0;
        var currentSeason = Math.Max(metaSeason, instrumentMax);
        var popTiers = precomputer.GetPopulationTiers();
        var allSongs = service.Songs;
        var droppedSongs = allSongs.Where(s => s.track?.su is null).ToList();
        if (droppedSongs.Count > 0)
        {
            foreach (var d in droppedSongs)
                Console.Error.WriteLine($"[SongsCache] Dropped song from /api/songs: _title='{d._title}', track={(d.track is null ? "null" : "present")}, su={(d.track?.su is null ? "null" : $"'{d.track.su}'")}");
        }
        Console.Error.WriteLine($"[SongsCache] BuildSongsJson: {allSongs.Count} total songs, {droppedSongs.Count} dropped, {allSongs.Count - droppedSongs.Count} returned");
        var songs = OrderSongsForPublicResponse(allSongs)
            .Select(s =>
            {
                maxScoresMap.TryGetValue(s.track.su, out var ms);

                // Build population tiers per instrument (if precomputed)
                Dictionary<string, PopulationTierData>? songPopTiers = null;
                if (popTiers is not null)
                {
                    songPopTiers = new Dictionary<string, PopulationTierData>(StringComparer.OrdinalIgnoreCase);
                    foreach (var inst in FSTService.Scraping.GlobalLeaderboardScraper.AllInstruments)
                    {
                        if (popTiers.TryGetValue((s.track.su, inst), out var pt))
                            songPopTiers[inst] = pt;
                    }
                    if (songPopTiers.Count == 0) songPopTiers = null;
                }

                return new
                {
                    songId     = s.track.su,
                    title      = s.track.tt,
                    artist     = s.track.an,
                    album      = s.track.ab,
                    year       = s.track.ry,
                    tempo      = s.track.mt,
                    sig        = s.track.sig,
                    durationSeconds = s.track.dn == 0 ? (int?)null : s.track.dn,
                    albumArt   = TrimAlbumArt(s.track.au),
                    genres     = s.track.ge,
                    // Difficulty per instrument. proDrums and proCymbals share the same
                    // spark-track value (@in.pd) — Epic stores a single plastic-drums difficulty.
                    // proVocals is mic-mode difficulty (@in.bd); 99 means the song has no Karaoke chart.
                    difficulty = s.track.@in is null ? null : new
                    {
                        guitar     = (int?)s.track.@in.gr,
                        bass       = (int?)s.track.@in.ba,
                        vocals     = (int?)s.track.@in.vl,
                        drums      = (int?)s.track.@in.ds,
                        proGuitar  = (int?)s.track.@in.pg,
                        proBass    = (int?)s.track.@in.pb,
                        proDrums   = (int?)s.track.@in.pd,
                        proCymbals = (int?)s.track.@in.pd,
                        proVocals  = Track.HasChartedDifficulty(s.track.@in.bd) ? s.track.@in.bd : (int?)null,
                    },
                    maxScores = ms is null ? null : new Dictionary<string, int?>
                    {
                        ["Solo_Guitar"]           = ms.MaxLeadScore,
                        ["Solo_Bass"]             = ms.MaxBassScore,
                        ["Solo_Drums"]            = ms.MaxDrumsScore,
                        ["Solo_Vocals"]           = ms.MaxVocalsScore,
                        ["Solo_PeripheralGuitar"] = ms.MaxProLeadScore,
                        ["Solo_PeripheralBass"]   = ms.MaxProBassScore,
                    },
                    populationTiers = songPopTiers,
                    pathsGeneratedAt = ms?.GeneratedAt,
                    pathArtifactGenerationId = ms?.ArtifactGenerationId,
                    pathExpectedInstruments = ms?.ExpectedInstruments,
                    pathChoptVersion = ms?.CHOptVersion,
                    pathChoptBinarySha256 = ms?.CHOptBinarySha256,
                    pathGenerationProfile = ms?.GenerationProfile,
                };
            })
            .ToList();

        var payload = new { count = songs.Count, currentSeason, songs };
        return JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
    }

    public static byte[] BuildPublishedSongsJson(
        IPathDataStore pathStore,
        IMetaDatabase metaDb,
        GlobalLeaderboardPersistence persistence,
        ScrapeTimePrecomputer precomputer,
        JsonSerializerOptions jsonOpts)
    {
        var pointers = metaDb.GetPublicationPointerState();
        if (pointers.PublishedScrapeId is not long publishedScrapeId ||
            pointers.CurrentPublicationId is not long currentPublicationId)
        {
            throw new InvalidOperationException(
                "No current published song catalog is available.");
        }

        var catalog = (metaDb as MetaDatabase)?
            .GetCurrentPublicationSongCatalogFallback(
                currentPublicationId)
            ?? throw new InvalidOperationException(
                $"Published scrape {publishedScrapeId} has no bound song catalog.");
        if (catalog.PublicationId != currentPublicationId)
        {
            throw new InvalidOperationException(
                "Published song catalog is not bound to the current publication.");
        }

        var songs = SongCatalogSnapshotBuilder.DeserializeCatalog(
            catalog.CatalogJson);
        if (songs.Count != catalog.SongCount)
        {
            throw new InvalidOperationException(
                "Published song catalog count does not match its binding.");
        }

        var service = FestivalService.CreateFromSongCatalogSnapshot(songs);
        return BuildSongsJson(
            service,
            pathStore,
            metaDb,
            persistence,
            precomputer,
            jsonOpts);
    }

    internal static IEnumerable<Song> OrderSongsForPublicResponse(IEnumerable<Song> songs) =>
        songs
            .Where(static song => song.track?.su is not null)
            .OrderBy(static song => song.track.su, StringComparer.Ordinal);

    private static string? TrimAlbumArt(string? url)
        => url is not null && url.StartsWith(ApiEndpoints.AlbumArtPrefix, StringComparison.Ordinal)
            ? url[ApiEndpoints.AlbumArtPrefix.Length..]
            : url;

    private sealed class ContentMutationLease : IDisposable
    {
        private SongsCacheService? _owner;

        public ContentMutationLease(SongsCacheService owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?
                .EndContentMutation();
        }
    }
}
