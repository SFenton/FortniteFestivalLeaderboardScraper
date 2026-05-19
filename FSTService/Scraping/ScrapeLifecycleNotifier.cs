using FSTService.Api;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Coordinates cache freshness across all keyed <see cref="ResponseCacheService"/>
/// instances during scrape passes. Cached entries stay fresh while cache misses
/// continue through endpoint published-read fallbacks until the new scrape is
/// fully published.
/// </summary>
public sealed class ScrapeLifecycleNotifier
{
    private readonly ResponseCacheService[] _caches;
    private readonly IMetaDatabase _metaDb;
    private readonly PublicReadGateService _publicReadGate;
    private readonly ILogger<ScrapeLifecycleNotifier> _log;

    public ScrapeLifecycleNotifier(
        [FromKeyedServices("PlayerCache")] ResponseCacheService playerCache,
        [FromKeyedServices("LeaderboardAllCache")] ResponseCacheService leaderboardAllCache,
        [FromKeyedServices("NeighborhoodCache")] ResponseCacheService neighborhoodCache,
        [FromKeyedServices("RivalsCache")] ResponseCacheService rivalsCache,
        [FromKeyedServices("LeaderboardRivalsCache")] ResponseCacheService leaderboardRivalsCache,
        IMetaDatabase metaDb,
        PublicReadGateService publicReadGate,
        ILogger<ScrapeLifecycleNotifier> log)
    {
        _caches = [playerCache, leaderboardAllCache, neighborhoodCache, rivalsCache, leaderboardRivalsCache];
        _metaDb = metaDb;
        _publicReadGate = publicReadGate;
        _log = log;
    }

    /// <summary>
    /// Freeze all response caches so TTL-based expiration is suppressed.
    /// Called at the start of a scrape pass, before any data is finalized.
    /// </summary>
    public void ScrapeStarting()
    {
        _log.LogInformation("Scrape starting — freezing public reads and {Count} response caches.", _caches.Length);
        try
        {
            _metaDb.SetPublicReadFreeze(true, reason: "scrape");
            _publicReadGate.Invalidate();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to persist public-read freeze state.");
        }

        foreach (var cache in _caches)
            cache.Freeze();
    }

    /// <summary>
    /// Marks the public-read freeze as publishing before public read models are
    /// updated for the next published scrape.
    /// </summary>
    public void ScrapePublishing()
    {
        _log.LogInformation("Scrape publication starting — public reads will prefer persisted published responses and compute cold misses from stable read models.");
        try
        {
            _metaDb.PublishCurrentBandTeamRankings();
            _metaDb.SetPublicReadFreeze(true, reason: "publish");
            _publicReadGate.Invalidate();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to prepare public-read publish freeze state.");
            throw;
        }
    }

    /// <summary>
    /// Unfreeze all response caches and invalidate their contents so the next
    /// request picks up freshly precomputed data. Called after the scrape pass
    /// and all post-scrape enrichment/precomputation are fully complete.
    /// </summary>
    public void ScrapeCompleted()
    {
        _log.LogInformation("Scrape completed — unfreezing public reads and invalidating {Count} response caches.", _caches.Length);
        try
        {
            _metaDb.SetPublicReadFreeze(false);
            _publicReadGate.Invalidate();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to persist public-read unfreeze state.");
        }

        foreach (var cache in _caches)
        {
            cache.Unfreeze();
            cache.InvalidateAll();
        }
    }
}
