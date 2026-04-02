using System.Security.Cryptography;
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
        var hash = SHA256.HashData(json);
        var etag = $"\"{Convert.ToBase64String(hash, 0, 16)}\"";
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
        var songs = service.Songs
            .Where(s => s.track?.su is not null)
            .Select(s =>
            {
                maxScoresMap.TryGetValue(s.track.su, out var ms);

                // Build population tiers per instrument (if precomputed)
                Dictionary<string, PopulationTierData>? songPopTiers = null;
                if (popTiers is not null)
                {
                    songPopTiers = new Dictionary<string, PopulationTierData>(StringComparer.OrdinalIgnoreCase);
                    foreach (var inst in new[] { "Solo_Guitar", "Solo_Bass", "Solo_Drums", "Solo_Vocals", "Solo_PeripheralGuitar", "Solo_PeripheralBass" })
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
                    albumArt   = TrimAlbumArt(s.track.au),
                    genres     = s.track.ge,
                    difficulty = s.track.@in is null ? null : new
                    {
                        guitar     = s.track.@in.gr,
                        bass       = s.track.@in.ba,
                        vocals     = s.track.@in.vl,
                        drums      = s.track.@in.ds,
                        proGuitar  = s.track.@in.pg,
                        proBass    = s.track.@in.pb,
                    },
                    maxScores = ms is null ? null : new
                    {
                        Solo_Guitar           = ms.MaxLeadScore,
                        Solo_Bass             = ms.MaxBassScore,
                        Solo_Drums            = ms.MaxDrumsScore,
                        Solo_Vocals           = ms.MaxVocalsScore,
                        Solo_PeripheralGuitar = ms.MaxProLeadScore,
                        Solo_PeripheralBass   = ms.MaxProBassScore,
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
