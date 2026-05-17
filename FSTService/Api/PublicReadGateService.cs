using FSTService.Persistence;

namespace FSTService.Api;

public sealed class PublicReadGateService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);
    private readonly IMetaDatabase _metaDb;
    private readonly ILogger<PublicReadGateService> _log;
    private readonly object _lock = new();
    private PublicReadFreezeState _cachedState = PublicReadFreezeState.NotFrozen;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public PublicReadGateService(IMetaDatabase metaDb, ILogger<PublicReadGateService> log)
    {
        _metaDb = metaDb;
        _log = log;
    }

    public bool IsFrozen => GetState().IsFrozen;

    public bool RequiresCachedReads
    {
        get
        {
            var state = GetState();
            return state.IsFrozen && !string.Equals(state.Reason, "scrape", StringComparison.OrdinalIgnoreCase);
        }
    }

    public PublicReadFreezeState GetState()
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (now - _cachedAtUtc < CacheTtl)
                return _cachedState;

            try
            {
                _cachedState = _metaDb.GetPublicReadFreezeState();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Unable to read public-read freeze state; allowing request to continue.");
                _cachedState = PublicReadFreezeState.NotFrozen;
            }

            _cachedAtUtc = now;
            return _cachedState;
        }
    }

    public void Invalidate()
    {
        lock (_lock)
            _cachedAtUtc = DateTime.MinValue;
    }
}