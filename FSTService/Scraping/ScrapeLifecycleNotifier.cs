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
    private readonly PublicationReadContextService _publicationReadContext;
    private readonly ILogger<ScrapeLifecycleNotifier> _log;

    public ScrapeLifecycleNotifier(
        [FromKeyedServices("PlayerCache")] ResponseCacheService playerCache,
        [FromKeyedServices("LeaderboardAllCache")] ResponseCacheService leaderboardAllCache,
        [FromKeyedServices("NeighborhoodCache")] ResponseCacheService neighborhoodCache,
        [FromKeyedServices("RivalsCache")] ResponseCacheService rivalsCache,
        [FromKeyedServices("LeaderboardRivalsCache")] ResponseCacheService leaderboardRivalsCache,
        IMetaDatabase metaDb,
        PublicReadGateService publicReadGate,
        PublicationReadContextService publicationReadContext,
        ILogger<ScrapeLifecycleNotifier> log)
    {
        _caches = [playerCache, leaderboardAllCache, neighborhoodCache, rivalsCache, leaderboardRivalsCache];
        _metaDb = metaDb;
        _publicReadGate = publicReadGate;
        _publicationReadContext = publicationReadContext;
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
            _publicationReadContext.Invalidate();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to persist public-read freeze state.");
        }

        foreach (var cache in _caches)
            cache.Freeze();
    }

    public void ScrapePostProcessing()
    {
        _log.LogInformation("Leaderboard scrape completed — public reads remain frozen on the published scrape during post-processing.");
        try
        {
            _metaDb.SetPublicReadFreeze(true, reason: "post-process");
            _publicReadGate.Invalidate();
            _publicationReadContext.Invalidate();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to persist public-read post-process freeze state.");
            throw;
        }
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
            _metaDb.SetPublicReadFreeze(true, reason: "publish");
            _publicReadGate.Invalidate();
            _publicationReadContext.Invalidate();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to prepare public-read publish freeze state.");
            throw;
        }
    }

    public PublicationCommitIntentLease PublicationCommitStarting(
        long scrapeId)
    {
        var previousState = _metaDb.GetPublicReadFreezeState();
        _log.LogInformation(
            "Publication commit intent recorded for scrape {ScrapeId}; exact published cache hits remain available while uncached reads drain.",
            scrapeId);
        var commitIntent =
            _metaDb.BeginPublicationCommitIntent(scrapeId);
        _publicReadGate.ClearLocalFailClosed();
        _publicReadGate.Invalidate();
        _publicationReadContext.Invalidate();
        return new PublicationCommitIntentLease(
            scrapeId,
            commitIntent,
            previousState,
            _metaDb,
            _publicReadGate,
            _publicationReadContext,
            _log);
    }

    /// <summary>
    /// Unfreeze all response caches and invalidate their contents so the next
    /// request picks up freshly precomputed data. Called after the scrape pass
    /// and all post-scrape enrichment/precomputation are fully complete.
    /// </summary>
    public void ScrapeCompleted()
    {
        _log.LogInformation("Scrape completed — unfreezing public reads and invalidating {Count} response caches.", _caches.Length);
        ReleasePublicReads();
    }

    public void ScrapeFailureIsolationPending(long scrapeId)
    {
        _log.LogError(
            "Durable failed-candidate isolation is pending for scrape {ScrapeId}; public reads and response caches remain fail-closed.",
            scrapeId);
        try
        {
            _metaDb.SetPublicReadFreeze(
                true,
                scrapeId,
                PublicReadFreezeState
                    .PublicationFailureIsolationPendingReason);
            _publicReadGate.Invalidate();
            _publicationReadContext.Invalidate();
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Failed to persist pending publication isolation for scrape {ScrapeId}; in-process response caches remain frozen.",
                scrapeId);
            _publicReadGate.EnterLocalFailClosed(
                scrapeId,
                PublicReadFreezeState
                    .PublicationFailureIsolationPendingReason);
        }
    }

    public void PublicationCommitDeferred(long scrapeId)
    {
        _log.LogWarning(
            "Publication commit for scrape {ScrapeId} remains ready but deferred by contention; public reads stay cached and fail-closed until retry.",
            scrapeId);
        try
        {
            _metaDb.SetPublicReadFreeze(
                true,
                scrapeId,
                PublicReadFreezeState.PublicationCommitDeferredReason);
            _publicReadGate.ClearLocalFailClosed();
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Failed to persist deferred publication state for scrape {ScrapeId}; retaining an in-process fail-closed override for recovery.",
                scrapeId);
            _publicReadGate.EnterLocalFailClosed(
                scrapeId,
                PublicReadFreezeState
                    .PublicationFailureIsolationPendingReason);
        }
        finally
        {
            _publicReadGate.Invalidate();
            _publicationReadContext.Invalidate();
        }
    }

    public void ScrapeFailed(
        bool durableIsolationConfirmed = true)
    {
        if (!durableIsolationConfirmed)
        {
            _log.LogError(
                "Scrape failed without durable candidate isolation; retaining the public-read freeze and {Count} frozen response caches.",
                _caches.Length);
            _publicReadGate.Invalidate();
            _publicationReadContext.Invalidate();
            return;
        }

        _log.LogWarning("Scrape failed — retaining the prior published generation, unfreezing public reads, and invalidating {Count} response caches.", _caches.Length);
        ReleasePublicReads();
    }

    public void InvalidateInProcessCaches()
    {
        foreach (var cache in _caches)
            cache.InvalidateAll();
    }

    private void ReleasePublicReads()
    {
        try
        {
            _metaDb.SetPublicReadFreeze(false);
            _publicReadGate.ClearLocalFailClosed();
            _publicReadGate.Invalidate();
            _publicationReadContext.Invalidate();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to persist public-read unfreeze state.");
        }

        foreach (var cache in _caches)
        {
            cache.Unfreeze();
        }
        InvalidateInProcessCaches();
    }

    public sealed class PublicationCommitIntentLease : IDisposable
    {
        private readonly long _scrapeId;
        private readonly PublicationCommitIntentHandle
            _commitIntent;
        private readonly PublicReadFreezeState _previousState;
        private readonly IMetaDatabase _metaDb;
        private readonly PublicReadGateService _publicReadGate;
        private readonly PublicationReadContextService
            _publicationReadContext;
        private readonly ILogger<ScrapeLifecycleNotifier> _log;
        private bool _disposed;
        private bool _preserveDurableIntent;

        public PublicationCommitIntentLease(
            long scrapeId,
            PublicationCommitIntentHandle commitIntent,
            PublicReadFreezeState previousState,
            IMetaDatabase metaDb,
            PublicReadGateService publicReadGate,
            PublicationReadContextService publicationReadContext,
            ILogger<ScrapeLifecycleNotifier> log)
        {
            _scrapeId = scrapeId;
            _commitIntent = commitIntent;
            _previousState = previousState;
            _metaDb = metaDb;
            _publicReadGate = publicReadGate;
            _publicationReadContext = publicationReadContext;
            _log = log;
        }

        public PublicationCommitIntentHandle CommitIntent =>
            _commitIntent;

        public void Defer()
        {
            _preserveDurableIntent = true;
            try
            {
                _metaDb.TransitionPublicationCommitIntentToDeferred(
                    _commitIntent);
                _publicReadGate.ClearLocalFailClosed();
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "Failed to transition publication commit intent for scrape {ScrapeId} to deferred; retaining the durable commit-pending latch and retrying in background.",
                    _scrapeId);
                _publicReadGate.EnterLocalFailClosed(
                    _scrapeId,
                    PublicReadFreezeState
                        .PublicationFailureIsolationPendingReason);
                _ = RetryDeferredTransitionAsync();
            }
            finally
            {
                _publicReadGate.Invalidate();
                _publicationReadContext.Invalidate();
            }
        }

        public void PreserveForIsolationPending()
        {
            _preserveDurableIntent = true;
            try
            {
                _metaDb
                    .TransitionPublicationCommitIntentToIsolationPending(
                        _commitIntent);
                _publicReadGate.ClearLocalFailClosed();
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "Failed to transition publication commit intent for scrape {ScrapeId} to pending isolation; retaining the durable commit latch and retrying in background.",
                    _scrapeId);
                _publicReadGate.EnterLocalFailClosed(
                    _scrapeId,
                    PublicReadFreezeState
                        .PublicationFailureIsolationPendingReason);
                _ = RetryIsolationPendingTransitionAsync();
            }
            finally
            {
                _publicReadGate.Invalidate();
                _publicationReadContext.Invalidate();
            }
        }

        public void CompleteIsolation()
        {
            _preserveDurableIntent = true;
            try
            {
                _metaDb.ClearPublicationCommitIntentAfterIsolation(
                    _commitIntent);
                _publicReadGate.ClearLocalFailClosed();
            }
            catch
            {
                PreserveForIsolationPending();
            }
            finally
            {
                _publicReadGate.Invalidate();
                _publicationReadContext.Invalidate();
            }
        }

        private async Task RetryDeferredTransitionAsync()
        {
            for (var attempt = 0; attempt < 300; attempt++)
            {
                try
                {
                    _metaDb.HeartbeatPublicationCommitIntent(
                        _commitIntent);
                    _metaDb
                        .TransitionPublicationCommitIntentToDeferred(
                            _commitIntent);
                    _publicReadGate.ClearLocalFailClosed();
                    _publicReadGate.Invalidate();
                    _publicationReadContext.Invalidate();
                    return;
                }
                catch
                {
                    try
                    {
                        var state =
                            _metaDb.GetPublicReadFreezeState();
                        if (state.PublicationCommitDeferred
                            || !state.PublicationCommitPending)
                        {
                            _publicReadGate.ClearLocalFailClosed();
                            _publicReadGate.Invalidate();
                            return;
                        }
                    }
                    catch
                    {
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            _log.LogCritical(
                "Publication commit intent for scrape {ScrapeId} could not transition to deferred after repeated background retries; durable commit-pending remains active.",
                _scrapeId);
        }

        private async Task RetryIsolationPendingTransitionAsync()
        {
            for (var attempt = 0; attempt < 300; attempt++)
            {
                try
                {
                    _metaDb.HeartbeatPublicationCommitIntent(
                        _commitIntent);
                    _metaDb
                        .TransitionPublicationCommitIntentToIsolationPending(
                            _commitIntent);
                    _publicReadGate.ClearLocalFailClosed();
                    _publicReadGate.Invalidate();
                    _publicationReadContext.Invalidate();
                    return;
                }
                catch
                {
                    try
                    {
                        var state =
                            _metaDb.GetPublicReadFreezeState();
                        if (state.PublicationFailureIsolationPending
                            || !state.PublicationCommitPending)
                        {
                            _publicReadGate.ClearLocalFailClosed();
                            _publicReadGate.Invalidate();
                            return;
                        }
                    }
                    catch
                    {
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            _log.LogCritical(
                "Publication commit intent for scrape {ScrapeId} could not transition to pending isolation after repeated background retries; durable commit-pending remains active.",
                _scrapeId);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_preserveDurableIntent)
            {
                _publicReadGate.Invalidate();
                _publicationReadContext.Invalidate();
                return;
            }
            try
            {
                _metaDb.RestorePublicationCommitIntent(
                    _commitIntent,
                    _previousState);
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "Publication commit intent for scrape {ScrapeId} could not be restored immediately; stale-intent reconciliation remains armed.",
                    _scrapeId);
            }
            finally
            {
                _publicReadGate.Invalidate();
                _publicationReadContext.Invalidate();
            }
        }
    }
}
