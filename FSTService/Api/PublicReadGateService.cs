using FSTService.Persistence;

namespace FSTService.Api;

public sealed class PublicReadGateService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);
    private readonly IMetaDatabase _metaDb;
    private readonly ILogger<PublicReadGateService> _log;
    private readonly object _lock = new();
    private PublicReadFreezeState _cachedState = PublicReadFreezeState.NotFrozen;
    private bool _cachedRequiresCachedReads;
    private bool _cachedFailedCandidateIsolation;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public PublicReadGateService(IMetaDatabase metaDb, ILogger<PublicReadGateService> log)
    {
        _metaDb = metaDb;
        _log = log;
    }

    public bool IsFrozen => GetState().IsFrozen;

    // Normal scrape freezes allow published-resolver cold misses. A failed candidate
    // that changed unversioned derived data requires cached or explicitly mapped reads.
    public bool RequiresCachedReads
    {
        get
        {
            _ = GetState();
            lock (_lock)
                return _cachedRequiresCachedReads;
        }
    }

    public bool FailedCandidateIsolationActive
    {
        get
        {
            _ = GetState();
            lock (_lock)
                return _cachedFailedCandidateIsolation;
        }
    }

    public PublicReadFreezeState GetState()
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            // A permissive state is never reused across requests: the next request
            // after a failed scrape must observe strict isolation immediately.
            // Strict/fail-closed states remain briefly cached to bound DB load.
            if (_cachedRequiresCachedReads
                && now - _cachedAtUtc < CacheTtl)
                return _cachedState;

            try
            {
                var freezeState = _metaDb.GetPublicReadFreezeState();
                var failedCandidateIsolation =
                    _metaDb.GetFailedCandidateReadIsolationState()
                    ?? PublicReadFreezeState.NotFrozen;
                _cachedFailedCandidateIsolation =
                    failedCandidateIsolation.IsFrozen;
                _cachedRequiresCachedReads = failedCandidateIsolation.IsFrozen;
                _cachedState = freezeState.IsFrozen ? freezeState : failedCandidateIsolation;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Unable to read public-read safety state; failing derived reads closed.");
                _cachedState = new PublicReadFreezeState(
                    true,
                    now,
                    null,
                    "read-safety-state-unavailable");
                _cachedRequiresCachedReads = true;
                _cachedFailedCandidateIsolation = false;
            }

            _cachedAtUtc = now;
            return _cachedState;
        }
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _cachedAtUtc = DateTime.MinValue;
        }
    }
}