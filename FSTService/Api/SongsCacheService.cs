using System.Security.Cryptography;
using System.Text.Json;

namespace FSTService.Api;

/// <summary>
/// Caches the serialized /api/songs JSON response and its ETag.
/// Invalidated after scrape passes, path generation, or shop changes.
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
}
