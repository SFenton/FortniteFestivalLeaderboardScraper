using System.Security.Cryptography;
using System.Text.Json;
using FortniteFestival.Core.Services;
using FSTService.Scraping;

namespace FSTService.Api;

/// <summary>
/// Caches the serialized /api/shop JSON response and its ETag.
/// Primed eagerly by <see cref="ItemShopService"/> on shop rotation
/// and on startup, so /api/shop never incurs a cold-start penalty.
/// </summary>
public sealed class ShopCacheService
{
    private readonly object _lock = new();
    private byte[]? _cachedJson;
    private string? _etag;

    /// <summary>
    /// Returns (json, etag) if the cache has been primed; otherwise null.
    /// No TTL — the cache is only refreshed when the shop changes.
    /// </summary>
    public (byte[] Json, string ETag)? Get()
    {
        lock (_lock)
        {
            if (_cachedJson is not null)
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
        }
        return etag;
    }

    /// <summary>
    /// Builds the enriched shop payload from current state and primes the cache.
    /// Returns the serialized JSON bytes.
    /// </summary>
    public byte[] Prime(
        IReadOnlySet<string> inShopSongIds,
        IReadOnlySet<string> leavingTomorrowSongIds,
        FestivalService festivalService,
        JsonSerializerOptions jsonOpts)
    {
        var songLookup = new Dictionary<string, FortniteFestival.Core.Song>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var s in festivalService.Songs)
        {
            if (s.track?.su is not null)
                songLookup[s.track.su] = s;
        }

        var songs = new List<object>();
        foreach (var songId in inShopSongIds)
        {
            if (!songLookup.TryGetValue(songId, out var entry) || entry.track is null)
                continue;

            songs.Add(new
            {
                songId,
                title = entry.track.tt,
                artist = entry.track.an,
                year = entry.track.ry,
                albumArt = TrimAlbumArt(entry.track.au),
                shopUrl = ShopUrlHelper.ComputeShopUrl(songId, entry.track.tt ?? songId),
                leavingTomorrow = leavingTomorrowSongIds.Contains(songId),
            });
        }

        var payload = new
        {
            count = songs.Count,
            songs,
            lastUpdated = DateTime.UtcNow.ToString("o"),
        };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
        Set(jsonBytes);
        return jsonBytes;
    }

    /// <summary>
    /// Builds the enriched song objects list for WebSocket messages.
    /// Returns a list suitable for embedding in shop_snapshot/shop_changed payloads.
    /// </summary>
    public static List<object> BuildEnrichedSongList(
        IEnumerable<string> songIds,
        IReadOnlySet<string> leavingTomorrowSongIds,
        FestivalService festivalService)
    {
        var songLookup = new Dictionary<string, FortniteFestival.Core.Song>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var s in festivalService.Songs)
        {
            if (s.track?.su is not null)
                songLookup[s.track.su] = s;
        }

        var result = new List<object>();
        foreach (var songId in songIds)
        {
            if (!songLookup.TryGetValue(songId, out var entry) || entry.track is null)
                continue;

            result.Add(new
            {
                songId,
                title = entry.track.tt,
                artist = entry.track.an,
                year = entry.track.ry,
                albumArt = TrimAlbumArt(entry.track.au),
                shopUrl = ShopUrlHelper.ComputeShopUrl(songId, entry.track.tt ?? songId),
                leavingTomorrow = leavingTomorrowSongIds.Contains(songId),
            });
        }
        return result;
    }

    private static string? TrimAlbumArt(string? url)
        => url is not null && url.StartsWith(ApiEndpoints.AlbumArtPrefix, StringComparison.Ordinal)
            ? url[ApiEndpoints.AlbumArtPrefix.Length..]
            : url;
}
