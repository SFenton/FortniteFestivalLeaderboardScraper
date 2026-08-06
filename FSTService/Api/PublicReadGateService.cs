using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

public readonly record struct PublicReadCacheSafetySnapshot(
    bool IsFrozen,
    bool FailedCandidateIsolationActive,
    long Revision);

public sealed class PublicReadGateService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);
    private readonly IMetaDatabase _metaDb;
    private readonly ILogger<PublicReadGateService> _log;
    private readonly TimeSpan _staleCommitIntentAfter;
    private readonly IPublicationRecoveryCoordinator?
        _recoveryCoordinator;
    private readonly object _lock = new();
    private PublicReadFreezeState _cachedState = PublicReadFreezeState.NotFrozen;
    private bool _cachedRequiresCachedReads;
    private bool _cachedFailedCandidateIsolation;
    private PublicReadFreezeState? _localFailClosedState;
    private DateTime _cachedAtUtc = DateTime.MinValue;
    private long _stateRevision;

    public PublicReadGateService(
        IMetaDatabase metaDb,
        ILogger<PublicReadGateService> log,
        IOptions<PublicationCommitOptions>? publicationCommitOptions = null,
        IPublicationRecoveryCoordinator? recoveryCoordinator = null)
    {
        _metaDb = metaDb;
        _log = log;
        _recoveryCoordinator = recoveryCoordinator;
        _staleCommitIntentAfter = TimeSpan.FromSeconds(
            Math.Max(
                1,
                publicationCommitOptions?.Value
                    .StaleCommitIntentSeconds
                ?? new PublicationCommitOptions()
                    .StaleCommitIntentSeconds));
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

    public PublicReadCacheSafetySnapshot GetCacheSafetySnapshot()
    {
        var state = GetState();
        lock (_lock)
        {
            return new PublicReadCacheSafetySnapshot(
                state.IsFrozen,
                _cachedFailedCandidateIsolation,
                _stateRevision);
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
                var previousState = _cachedState;
                var previousRequiresCachedReads = _cachedRequiresCachedReads;
                var previousFailedCandidateIsolation =
                    _cachedFailedCandidateIsolation;
                var freezeState = _metaDb.GetPublicReadFreezeState();
                if (freezeState.PublicationFailureIsolationPending
                    || freezeState.PublicationCommitPending
                    && (!freezeState.FrozenAt.HasValue
                        || now - freezeState.FrozenAt.Value
                            >= _staleCommitIntentAfter))
                {
                    _recoveryCoordinator?.Trigger();
                }
                var failedCandidateIsolation =
                    _metaDb.GetFailedCandidateReadIsolationState()
                    ?? PublicReadFreezeState.NotFrozen;
                _cachedFailedCandidateIsolation =
                    failedCandidateIsolation.IsFrozen;
                _cachedRequiresCachedReads =
                    failedCandidateIsolation.IsFrozen
                    || freezeState
                        .PublicationFailureIsolationPending
                    || freezeState.PublicationCommitDeferred
                    || freezeState.PublicationCommitPending;
                _cachedState = freezeState.IsFrozen ? freezeState : failedCandidateIsolation;
                if (_localFailClosedState is not null)
                {
                    var durableFailClosed =
                        failedCandidateIsolation.IsFrozen
                        || freezeState.PublicationFailureIsolationPending
                        || freezeState.PublicationCommitDeferred
                        || freezeState.PublicationCommitPending;
                    if (durableFailClosed)
                    {
                        _localFailClosedState = null;
                    }
                    else
                    {
                        _recoveryCoordinator?.Trigger();
                        _cachedState = _localFailClosedState;
                        _cachedRequiresCachedReads = true;
                        _cachedFailedCandidateIsolation = false;
                    }
                }
                if (previousState != _cachedState ||
                    previousRequiresCachedReads != _cachedRequiresCachedReads ||
                    previousFailedCandidateIsolation
                        != _cachedFailedCandidateIsolation)
                {
                    _stateRevision++;
                }
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
                _stateRevision++;
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
            _stateRevision++;
        }
    }

    public void EnterLocalFailClosed(
        long? scrapeId,
        string reason)
    {
        lock (_lock)
        {
            _localFailClosedState =
                new PublicReadFreezeState(
                    true,
                    DateTime.UtcNow,
                    scrapeId,
                    reason);
            _cachedState = _localFailClosedState;
            _cachedRequiresCachedReads = true;
            _cachedFailedCandidateIsolation = false;
            _cachedAtUtc = DateTime.UtcNow;
            _stateRevision++;
        }
        _recoveryCoordinator?.Trigger();
    }

    public void ClearLocalFailClosed()
    {
        lock (_lock)
        {
            _localFailClosedState = null;
            _cachedAtUtc = DateTime.MinValue;
            _stateRevision++;
        }
    }
}