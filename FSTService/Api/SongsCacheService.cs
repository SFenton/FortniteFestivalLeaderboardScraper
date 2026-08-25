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
    DurableStoreFailed,
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
    private readonly PublicationApiResponseCacheService?
        _publicationApiCache;
    private readonly TimeSpan _cacheTtl;
    private byte[]? _cachedJson;
    private string? _etag;
    private DateTime _cachedAt;
    private long? _publicationId;
    private long _safetyRevision;
    private long _contentRevision;
    private int _contentMutationDepth;
    private Action? _durableRefresh;
    private volatile bool _durableRefreshPending;
    private readonly bool _publicationBoundReads;
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Bounded attempts for a publication pointer move or a content-revision
    /// race during hydration. Exhausting them falls back to a local build that
    /// still never persists.
    /// </summary>
    internal const int MaxHydrationAttempts = 8;

    public SongsCacheService(
        PublicReadGateService? publicReadGate = null,
        Func<long?>? publicationIdProvider = null,
        PublicationApiResponseCacheService?
            publicationApiCache = null,
        bool publicationBoundReads = false)
        : this(
            publicReadGate is null
                ? static () => default
                : publicReadGate.GetCacheSafetySnapshot,
            DefaultCacheTtl,
            publicationIdProvider,
            publicationApiCache,
            publicationBoundReads)
    {
    }

    public bool DurableRefreshPending =>
        _durableRefreshPending;

    public SongsCacheService(Func<bool> isFrozen, TimeSpan cacheTtl)
        : this(
            () => new PublicReadCacheSafetySnapshot(
                isFrozen(),
                false,
                0),
            cacheTtl,
            null,
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
            publicationIdProvider,
            null)
    {
    }

    internal SongsCacheService(
        Func<PublicReadCacheSafetySnapshot> safetyProvider,
        TimeSpan cacheTtl,
        Func<long?>? publicationIdProvider = null,
        PublicationApiResponseCacheService?
            publicationApiCache = null,
        bool publicationBoundReads = false)
    {
        _safetyProvider =
            safetyProvider
            ?? throw new ArgumentNullException(nameof(safetyProvider));
        _publicationIdProvider = publicationIdProvider;
        _publicationApiCache = publicationApiCache;
        _cacheTtl = cacheTtl;
        _publicationBoundReads = publicationBoundReads;
    }

    /// <summary>
    /// True when the publication pipeline owns the durable
    /// <c>public-api:songs:v1</c> row. In that mode this process hydrates the
    /// in-process cache from that row and never rewrites it from
    /// process-local state.
    /// </summary>
    public bool PublicationBoundReads => _publicationBoundReads;

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
        long? publicationId;
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
            publicationId =
                _publicationIdProvider?.Invoke();
            _publicationId = publicationId;
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
        _publicationApiCache?.InvalidateAll();
    }

    public void InvalidateForContentChange()
    {
        Invalidate();
        if (_publicationBoundReads)
        {
            // The durable row is bound to the published catalog snapshot, not
            // to this process's live catalog. A local content change must not
            // mark it stale, or reads would fall back to degraded local
            // builds. Clearing the in-process cache is enough: the next read
            // re-hydrates from the durable row.
            return;
        }

        _durableRefreshPending = true;
        _publicationApiCache?.MarkCurrentKeyStale(
            PublicationApiCacheKeys.Songs);
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
        out string etag,
        bool persistPublicationCache = false)
    {
        // Fail closed: in publication-bound mode no caller may persist a
        // process-local build into the publication-owned durable row.
        if (_publicationBoundReads)
            persistPublicationCache = false;
        etag = ResponseCacheService.ComputeETag(json);
        var safety = _safetyProvider();
        var publicationId = _publicationIdProvider?.Invoke();
        long installedContentRevision = 0;
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
            installedContentRevision = _contentRevision;
            _cachedJson = json;
            _etag = etag;
            _cachedAt = DateTime.UtcNow;
            _publicationId = publicationId;
            _safetyRevision = safety.Revision;
        }
        if (persistPublicationCache
            && publicationId.HasValue
            && _publicationApiCache?.TryStoreCurrent(
                publicationId.Value,
                PublicationApiCacheKeys.Songs,
                json,
                etag) is null)
        {
            Invalidate();
            _durableRefreshPending = true;
            _publicationApiCache?.MarkCurrentKeyStale(
                PublicationApiCacheKeys.Songs);
            return SongsCacheWriteResult.DurableStoreFailed;
        }
        if (persistPublicationCache)
        {
            var racedContentMutation = false;
            lock (_lock)
            {
                if (_contentRevision
                        != installedContentRevision
                    || _contentMutationDepth > 0)
                {
                    _durableRefreshPending = true;
                    racedContentMutation = true;
                }
                else
                {
                    _durableRefreshPending = false;
                    _publicationApiCache
                        ?.ClearCurrentKeyStale(
                            PublicationApiCacheKeys.Songs);
                }
            }
            if (racedContentMutation)
            {
                _publicationApiCache?.MarkCurrentKeyStale(
                    PublicationApiCacheKeys.Songs);
                return SongsCacheWriteResult.Stale;
            }
        }
        return SongsCacheWriteResult.Stored;
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
            _durableRefreshPending = !_publicationBoundReads;
        }

        _publicationApiCache?.InvalidateAll();
        if (!_publicationBoundReads)
        {
            _publicationApiCache?.MarkCurrentKeyStale(
                PublicationApiCacheKeys.Songs);
        }

        return new ContentMutationLease(this);
    }

    public void SetDurableRefresh(Action durableRefresh)
    {
        _durableRefresh = durableRefresh
            ?? throw new ArgumentNullException(
                nameof(durableRefresh));
    }

    private void EndContentMutation()
    {
        var refresh = false;
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
            refresh = _contentMutationDepth == 0;
        }
        if (refresh)
            _durableRefresh?.Invoke();
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
    /// Publication-bound hydration. Installs the durable current-publication
    /// <c>public-api:songs:v1</c> payload into the in-process cache exactly as
    /// stored, without rebuilding it and without writing the durable row.
    /// </summary>
    /// <returns>
    /// True when the in-process cache now reflects the durable row, or when a
    /// safety state legitimately blocked the install. False when no usable
    /// durable row exists and the caller must fall back to a local build.
    /// </returns>
    public bool TryHydrateFromDurablePublicationCache()
    {
        if (_publicationApiCache is null)
            return false;

        for (var attempt = 1;
             attempt <= MaxHydrationAttempts;
             attempt++)
        {
            var token = CaptureBuildToken();
            if (!token.PublicationId.HasValue)
                return false;

            var durable = _publicationApiCache
                .TryGetCurrentDurableRow(PublicationApiCacheKeys.Songs);
            if (durable is null || durable.Json.Length == 0)
                return false;

            if (durable.PublicationId != token.PublicationId.Value)
            {
                // The publication pointer moved between capturing the token
                // and reading the row. Retry against the new pointer instead
                // of falling back to a process-local build.
                continue;
            }

            // Byte/ETag identity: never install a payload whose ETag does not
            // match the durable contract clients may already hold.
            if (!string.Equals(
                    ResponseCacheService.ComputeETag(durable.Json),
                    durable.ETag,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var result = TrySetIfBuildTokenUnchanged(
                durable.Json,
                token,
                out var etag,
                persistPublicationCache: false);
            switch (result)
            {
                case SongsCacheWriteResult.Stored:
                    return string.Equals(
                        etag,
                        durable.ETag,
                        StringComparison.Ordinal);
                case SongsCacheWriteResult.Blocked:
                    return true;
                default:
                    continue;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the songs JSON and primes the cache in one call.
    /// Replaces Invalidate() at all call sites.
    /// </summary>
    /// <remarks>
    /// In publication-bound mode the durable publication cache row is owned by
    /// the publication pipeline. This process hydrates from it and never
    /// persists a process-local build, because process-local state (for
    /// example an API role with no precomputed population tiers) would
    /// otherwise overwrite the canonical payload with a degraded one.
    /// </remarks>
    public void Prime(
        FestivalService service,
        IPathDataStore pathStore,
        IMetaDatabase metaDb,
        GlobalLeaderboardPersistence persistence,
        ScrapeTimePrecomputer precomputer,
        JsonSerializerOptions jsonOpts,
        bool persistPublicationCache = false)
    {
        if (_publicationBoundReads)
        {
            if (TryHydrateFromDurablePublicationCache())
                return;

            // No usable durable row: populate L1 from the bound publication
            // only. Never poison L2 or expose mutable live catalog state.
            persistPublicationCache = false;
        }

        while (true)
        {
            var token = CaptureBuildToken();
            var jsonBytes =
                _publicationBoundReads
                && token.PublicationId is long publicationId
                    ? BuildBoundPublicationSongsJson(
                        publicationId,
                        pathStore,
                        metaDb,
                        persistence,
                        precomputer,
                        jsonOpts)
                    : BuildSongsJson(
                        service,
                        pathStore,
                        metaDb,
                        persistence,
                        precomputer,
                        jsonOpts);
            var result = TrySetIfBuildTokenUnchanged(
                jsonBytes,
                token,
                out _,
                persistPublicationCache);
            if (result is SongsCacheWriteResult.Stored
                or SongsCacheWriteResult.Blocked)
            {
                return;
            }
            if (result
                == SongsCacheWriteResult.DurableStoreFailed)
            {
                throw new InvalidOperationException(
                    "The durable publication songs cache could not be updated.");
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
        return BuildSongsJson(
            service.Songs,
            maxScoresMap,
            currentSeason,
            popTiers,
            jsonOpts);
    }

    internal static byte[] BuildSongsJson(
        IReadOnlyCollection<Song> sourceSongs,
        IReadOnlyDictionary<string, SongMaxScores>
            maxScoresMap,
        int currentSeason,
        IReadOnlyDictionary<
            (string SongId, string Instrument),
            PopulationTierData>? popTiers,
        JsonSerializerOptions jsonOpts)
    {
        var allSongs = sourceSongs;
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
                    maxScores = BuildPublicMaxScores(ms),
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

    internal static IReadOnlyDictionary<string, int>? BuildPublicMaxScores(
        SongMaxScores? maxScores)
    {
        if (maxScores is null)
            return null;

        var result = new Dictionary<string, int>(
            StringComparer.Ordinal);
        Add("Solo_Guitar", maxScores.MaxLeadScore);
        Add("Solo_Bass", maxScores.MaxBassScore);
        Add("Solo_Drums", maxScores.MaxDrumsScore);
        Add("Solo_Vocals", maxScores.MaxVocalsScore);
        Add("Solo_PeripheralGuitar", maxScores.MaxProLeadScore);
        Add("Solo_PeripheralBass", maxScores.MaxProBassScore);
        Add(
            "Solo_PeripheralCymbals",
            maxScores.MaxProCymbalsScore);
        Add("Solo_PeripheralDrums", maxScores.MaxProDrumsScore);
        return result.Count == 0 ? null : result;

        void Add(string instrument, int? value)
        {
            if (value.HasValue)
                result[instrument] = value.Value;
        }
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

        var publishedSongs =
            SongCatalogSnapshotBuilder.DeserializeCatalogForFallback(
                catalog.CatalogJson,
                catalog.SchemaVersion)
            .ToArray();
        if (publishedSongs.Length != catalog.SongCount)
        {
            throw new InvalidOperationException(
                "Published song catalog count does not match its binding.");
        }

        IReadOnlyList<Song> songs = publishedSongs;
        var liveCatalog = (metaDb as MetaDatabase)?
            .GetLiveExactSongCatalogFallback();
        if (liveCatalog is { } live &&
            live.SongCount == catalog.SongCount)
        {
            var liveSongs =
                SongCatalogSnapshotBuilder.DeserializeCatalogForFallback(
                    live.CatalogJson,
                    live.SchemaVersion)
                .ToArray();
            songs = SelectPublishedFallbackSongs(
                publishedSongs,
                liveSongs);
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

    /// <summary>
    /// Builds the /api/songs payload strictly from the bound publication
    /// catalog and the publication-scoped path snapshot. Unlike
    /// <see cref="BuildPublishedSongsJson"/> it never overlays mutable live
    /// catalog fields.
    /// </summary>
    public static byte[] BuildBoundPublicationSongsJson(
        long publicationId,
        IPathDataStore pathStore,
        IMetaDatabase metaDb,
        GlobalLeaderboardPersistence persistence,
        ScrapeTimePrecomputer precomputer,
        JsonSerializerOptions jsonOpts)
    {
        var catalog = (metaDb as MetaDatabase)?
            .GetCurrentPublicationSongCatalogFallback(publicationId)
            ?? throw new InvalidOperationException(
                $"Publication {publicationId} has no bound song catalog.");
        if (catalog.PublicationId != publicationId)
        {
            throw new InvalidOperationException(
                "Published song catalog is not bound to the requested publication.");
        }

        var publishedSongs =
            SongCatalogSnapshotBuilder.DeserializeCatalogForFallback(
                catalog.CatalogJson,
                catalog.SchemaVersion)
            .ToArray();
        if (publishedSongs.Length != catalog.SongCount)
        {
            throw new InvalidOperationException(
                "Published song catalog count does not match its binding.");
        }

        using var scope = pathStore.BeginPublicationRead(publicationId);
        var service =
            FestivalService.CreateFromSongCatalogSnapshot(publishedSongs);
        return BuildSongsJson(
            service,
            pathStore,
            metaDb,
            persistence,
            precomputer,
            jsonOpts);
    }

    internal static IReadOnlyList<Song> SelectPublishedFallbackSongs(
        IReadOnlyList<Song> publishedSongs,
        IReadOnlyList<Song> liveSongs)
    {
        if (publishedSongs.Count != liveSongs.Count)
            return publishedSongs;

        var liveById = new Dictionary<string, Song>(
            StringComparer.Ordinal);
        foreach (var liveSong in liveSongs)
        {
            var songId = liveSong.track?.su;
            if (string.IsNullOrWhiteSpace(songId) ||
                !liveById.TryAdd(songId, liveSong))
            {
                return publishedSongs;
            }
        }

        foreach (var publishedSong in publishedSongs)
        {
            var songId = publishedSong.track?.su;
            if (string.IsNullOrWhiteSpace(songId) ||
                !liveById.TryGetValue(songId, out var liveSong) ||
                publishedSong.lastModified.ToUniversalTime() !=
                    liveSong.lastModified.ToUniversalTime())
            {
                return publishedSongs;
            }
        }

        return liveSongs;
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
