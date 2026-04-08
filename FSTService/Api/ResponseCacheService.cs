using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace FSTService.Api;

/// <summary>
/// General-purpose keyed response cache with ETag support.
/// Used for per-account player profiles and per-song leaderboards.
/// </summary>
public sealed class ResponseCacheService : IDisposable
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;
    private readonly Timer _evictionTimer;
    private volatile bool _frozen;

    public ResponseCacheService(TimeSpan ttl)
    {
        _ttl = ttl;
        _evictionTimer = new Timer(_ => Cleanup(), null, ttl, ttl);
    }

    /// <summary>
    /// When true, <see cref="Get"/> skips TTL checks and <see cref="Cleanup"/>
    /// skips eviction — all cached entries are treated as fresh.
    /// Used during scrape passes to prevent partial data from leaking through.
    /// </summary>
    public bool IsFrozen => _frozen;

    /// <summary>Freeze the cache — entries never expire until <see cref="Unfreeze"/> is called.</summary>
    public void Freeze() => _frozen = true;

    /// <summary>Unfreeze the cache — normal TTL-based expiration resumes.</summary>
    public void Unfreeze() => _frozen = false;

    /// <summary>
    /// Returns (json, etag) if cached and not expired; otherwise null.
    /// When frozen, TTL is ignored and all entries are treated as fresh.
    /// </summary>
    public (byte[] Json, string ETag)? Get(string key)
    {
        if (_cache.TryGetValue(key, out var entry) && (_frozen || DateTime.UtcNow - entry.CachedAt < _ttl))
            return (entry.Json, entry.ETag);
        return null;
    }

    /// <summary>
    /// Stores the serialized JSON response and computes an ETag.
    /// </summary>
    public string Set(string key, byte[] json)
    {
        var etag = ComputeETag(json);
        _cache[key] = new CacheEntry(json, etag, DateTime.UtcNow);
        return etag;
    }

    /// <summary>
    /// Compute a deterministic ETag from raw JSON bytes.
    /// Shared by all cache services to avoid duplicating SHA256 logic.
    /// </summary>
    public static string ComputeETag(byte[] json)
    {
        var hash = SHA256.HashData(json);
        return $"\"{Convert.ToBase64String(hash, 0, 16)}\"";
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

    private void Cleanup()
    {
        if (_frozen) return;

        var now = DateTime.UtcNow;
        foreach (var key in _cache.Keys)
        {
            if (_cache.TryGetValue(key, out var entry) && now - entry.CachedAt >= _ttl)
                _cache.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        _evictionTimer.Dispose();
    }

    private sealed record CacheEntry(byte[] Json, string ETag, DateTime CachedAt);
}
