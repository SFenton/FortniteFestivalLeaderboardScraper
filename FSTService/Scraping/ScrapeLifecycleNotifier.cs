using FSTService.Api;

namespace FSTService.Scraping;

/// <summary>
/// Coordinates cache freezing across all keyed <see cref="ResponseCacheService"/>
/// instances during scrape passes. When a scrape starts, all caches are frozen so
/// API consumers see consistent (stale) data. When the scrape completes and
/// precomputed responses are ready, caches are unfrozen and invalidated so the
/// next request picks up fresh data atomically.
/// </summary>
public sealed class ScrapeLifecycleNotifier
{
    private readonly ResponseCacheService[] _caches;
    private readonly ILogger<ScrapeLifecycleNotifier> _log;

    public ScrapeLifecycleNotifier(
        [FromKeyedServices("PlayerCache")] ResponseCacheService playerCache,
        [FromKeyedServices("LeaderboardAllCache")] ResponseCacheService leaderboardAllCache,
        [FromKeyedServices("NeighborhoodCache")] ResponseCacheService neighborhoodCache,
        [FromKeyedServices("RivalsCache")] ResponseCacheService rivalsCache,
        [FromKeyedServices("LeaderboardRivalsCache")] ResponseCacheService leaderboardRivalsCache,
        ILogger<ScrapeLifecycleNotifier> log)
    {
        _caches = [playerCache, leaderboardAllCache, neighborhoodCache, rivalsCache, leaderboardRivalsCache];
        _log = log;
    }

    /// <summary>
    /// Freeze all response caches so TTL-based expiration is suppressed.
    /// Called at the start of a scrape pass, before any data is finalized.
    /// </summary>
    public void ScrapeStarting()
    {
        _log.LogInformation("Scrape starting — freezing {Count} response caches.", _caches.Length);
        foreach (var cache in _caches)
            cache.Freeze();
    }

    /// <summary>
    /// Unfreeze all response caches and invalidate their contents so the next
    /// request picks up freshly precomputed data. Called after the scrape pass
    /// and all post-scrape enrichment/precomputation are fully complete.
    /// </summary>
    public void ScrapeCompleted()
    {
        _log.LogInformation("Scrape completed — unfreezing and invalidating {Count} response caches.", _caches.Length);
        foreach (var cache in _caches)
        {
            cache.Unfreeze();
            cache.InvalidateAll();
        }
    }
}
