using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace FSTService.Api;

/// <summary>
/// General-purpose keyed response cache with ETag support.
/// Used for per-account player profiles and per-song leaderboards.
/// </summary>
public sealed class ResponseCacheService
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;

    public ResponseCacheService(TimeSpan ttl)
    {
        _ttl = ttl;
    }

    /// <summary>
    /// Returns (json, etag) if cached and not expired; otherwise null.
    /// </summary>
    public (byte[] Json, string ETag)? Get(string key)
    {
        if (_cache.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.CachedAt < _ttl)
            return (entry.Json, entry.ETag);
        return null;
    }

    /// <summary>
    /// Stores the serialized JSON response and computes an ETag.
    /// </summary>
    public string Set(string key, byte[] json)
    {
        var hash = SHA256.HashData(json);
        var etag = $"\"{Convert.ToBase64String(hash, 0, 16)}\"";
        _cache[key] = new CacheEntry(json, etag, DateTime.UtcNow);
        return etag;
    }

    /// <summary>
    /// Invalidates a specific cache entry.
    /// </summary>
    public void Invalidate(string key)
    {
        _cache.TryRemove(key, out _);
    }

    /// <summary>
    /// Invalidates all cached entries.
    /// </summary>
    public void InvalidateAll()
    {
        _cache.Clear();
    }

    private sealed record CacheEntry(byte[] Json, string ETag, DateTime CachedAt);
}
