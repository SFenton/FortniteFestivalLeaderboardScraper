using FSTService.Persistence;

namespace FSTService.Scraping;

public sealed class RegistrationMutationCoordinator
{
    private readonly IMetaDatabase _metaDatabase;
    private readonly IPathDataStore _pathStore;
    private readonly ISongInstrumentSupportCache _instrumentSupportCache;
    private readonly StartupPublicationReadOnlyState? _publicationStartup;

    public RegistrationMutationCoordinator(
        IMetaDatabase metaDatabase,
        IPathDataStore pathStore,
        ISongInstrumentSupportCache instrumentSupportCache,
        StartupPublicationReadOnlyState? publicationStartup = null)
    {
        _metaDatabase = metaDatabase;
        _pathStore = pathStore;
        _instrumentSupportCache = instrumentSupportCache;
        _publicationStartup = publicationStartup;
    }

    public IRegistrationMutationLease AcquireLease(
        CancellationToken ct = default)
        => AcquireLeaseAsync(ct)
            .GetAwaiter()
            .GetResult();

    public Task<IRegistrationMutationLease>
        AcquireLeaseAsync(CancellationToken ct = default)
        => AcquireLeaseCoreAsync(
            refreshPathAdmission: true,
            tryOnly: false,
            ct);

    public Task<IRegistrationMutationLease>
        AcquireWriteLeaseAsync(
            CancellationToken ct = default)
        => AcquireLeaseCoreAsync(
            refreshPathAdmission: false,
            tryOnly: false,
            ct);

    public Task<IRegistrationMutationLease>
        TryAcquireLeaseAsync(
            CancellationToken ct = default)
        => AcquireLeaseCoreAsync(
            refreshPathAdmission: true,
            tryOnly: true,
            ct);

    public Task<IRegistrationMutationLease>
        TryAcquireWriteLeaseAsync(
            CancellationToken ct = default)
        => AcquireLeaseCoreAsync(
            refreshPathAdmission: false,
            tryOnly: true,
            ct);

    private async Task<IRegistrationMutationLease>
        AcquireLeaseCoreAsync(
            bool refreshPathAdmission,
            bool tryOnly,
            CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_publicationStartup?.IsLatched == true)
            throw new RegistrationMutationBlockedException();
        var lease = tryOnly
            ? await _metaDatabase
                .TryAcquireRegistrationMutationLeaseAsync(ct)
            : await _metaDatabase
                .AcquireRegistrationMutationLeaseAsync(ct);
        try
        {
            await lease.VerifyHeldAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (refreshPathAdmission)
            {
                _pathStore.InvalidateCachedState();
                _instrumentSupportCache
                    .InvalidateSongInstrumentSupport();
                _instrumentSupportCache
                    .RefreshSongInstrumentSupport();
            }
            ct.ThrowIfCancellationRequested();
            return lease;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }
}
