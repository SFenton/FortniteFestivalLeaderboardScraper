using System.Text.Json;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Api;

/// <summary>
/// Caches the serialized /api/songs JSON response and its ETag.
/// Primed eagerly after scrape passes, path generation, and catalog sync.
/// Falls back to on-demand build if not yet primed.
/// </summary>
public sealed class SongsCacheService
{
    private readonly object _lock = new();
    private byte[]? _cachedJson;
    private string? _etag;
    private DateTime _cachedAt;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Returns (json, etag) if cached and not expired; otherwise null.
    /// </summary>
    public (byte[] Json, string ETag)? Get()
    {
        lock (_lock)
        {
            if (_cachedJson is not null && DateTime.UtcNow - _cachedAt < CacheTtl)
                return (_cachedJson, _etag!);
            return null;
        }
    }

    /// <summary>
    /// Stores the serialized JSON response and computes an ETag.
    /// </summary>
    public string Set(byte[] json)
    {
        var etag = ResponseCacheService.ComputeETag(json);
        lock (_lock)
        {
            _cachedJson = json;
            _etag = etag;
            _cachedAt = DateTime.UtcNow;
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
            _cachedJson = null;
            _etag = null;
        }
    }

    /// <summary>
    /// Builds the songs JSON and primes the cache in one call.
    /// Replaces Invalidate() at all call sites.
    /// </summary>
    public void Prime(
        FestivalService service,
        IPathDataStore pathStore,
        IMetaDatabase metaDb,
        ScrapeTimePrecomputer precomputer,
        JsonSerializerOptions jsonOpts)
    {
        var jsonBytes = BuildSongsJson(service, pathStore, metaDb, precomputer, jsonOpts);
        Set(jsonBytes);
    }

    /// <summary>
    /// Builds the /api/songs JSON payload from current data sources.
    /// </summary>
    public static byte[] BuildSongsJson(
        FestivalService service,
        IPathDataStore pathStore,
        IMetaDatabase metaDb,
        ScrapeTimePrecomputer precomputer,
        JsonSerializerOptions jsonOpts)
    {
        var maxScoresMap = pathStore.GetAllMaxScores();
        var currentSeason = metaDb.GetCurrentSeason();
        var popTiers = precomputer.GetPopulationTiers();
        var allSongs = service.Songs;
        var droppedSongs = allSongs.Where(s => s.track?.su is null).ToList();
        if (droppedSongs.Count > 0)
        {
            foreach (var d in droppedSongs)
                Console.Error.WriteLine($"[SongsCache] Dropped song from /api/songs: _title='{d._title}', track={(d.track is null ? "null" : "present")}, su={(d.track?.su is null ? "null" : $"'{d.track.su}'")}");
        }
        Console.Error.WriteLine($"[SongsCache] BuildSongsJson: {allSongs.Count} total songs, {droppedSongs.Count} dropped, {allSongs.Count - droppedSongs.Count} returned");
        var songs = allSongs
            .Where(s => s.track?.su is not null)
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
                    // proVocals is mic-mode difficulty (@in.bd); 0 is treated as missing (null).
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
                        proVocals  = s.track.@in.bd == 0 ? (int?)null : s.track.@in.bd,
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
                };
            })
            .ToList();

        var payload = new { count = songs.Count, currentSeason, songs };
        return JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
    }

    private static string? TrimAlbumArt(string? url)
        => url is not null && url.StartsWith(ApiEndpoints.AlbumArtPrefix, StringComparison.Ordinal)
            ? url[ApiEndpoints.AlbumArtPrefix.Length..]
            : url;
}
