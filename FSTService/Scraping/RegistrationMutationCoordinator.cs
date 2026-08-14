using FSTService.Persistence;

namespace FSTService.Scraping;

public sealed class RegistrationMutationCoordinator
{
    private readonly IMetaDatabase _metaDatabase;
    private readonly IPathDataStore _pathStore;
    private readonly ISongInstrumentSupportCache _instrumentSupportCache;

    public RegistrationMutationCoordinator(
        IMetaDatabase metaDatabase,
        IPathDataStore pathStore,
        ISongInstrumentSupportCache instrumentSupportCache)
    {
        _metaDatabase = metaDatabase;
        _pathStore = pathStore;
        _instrumentSupportCache = instrumentSupportCache;
    }

    public IRegistrationMutationLease AcquireLease(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var lease = _metaDatabase.AcquireRegistrationMutationLease();
        try
        {
            ct.ThrowIfCancellationRequested();
            _pathStore.InvalidateCachedState();
            _instrumentSupportCache.InvalidateSongInstrumentSupport();
            _instrumentSupportCache.RefreshSongInstrumentSupport();
            ct.ThrowIfCancellationRequested();
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }
}
