using FSTService.Persistence;
using FSTService.Scraping;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FSTService.Tests.Unit;

public sealed class RegistrationMutationCoordinatorTests
{
    [Fact]
    public async Task AcquireLease_RefreshFailureDisposesDurableLease()
    {
        var metaDatabase = Substitute.For<IMetaDatabase>();
        var pathStore = Substitute.For<IPathDataStore>();
        var supportCache =
            Substitute.For<ISongInstrumentSupportCache>();
        var durableLease =
            Substitute.For<IRegistrationMutationLease>();
        metaDatabase
            .AcquireRegistrationMutationLeaseAsync(
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(durableLease));
        supportCache
            .When(cache =>
                cache.RefreshSongInstrumentSupport())
            .Throw(new InvalidOperationException(
                "refresh failed"));
        var coordinator = new RegistrationMutationCoordinator(
            metaDatabase,
            pathStore,
            supportCache);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.AcquireLeaseAsync());

        await durableLease.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task AcquireLease_AlreadyCancelledDoesNotAcquireDurableLease()
    {
        var metaDatabase = Substitute.For<IMetaDatabase>();
        var coordinator = new RegistrationMutationCoordinator(
            metaDatabase,
            Substitute.For<IPathDataStore>(),
            Substitute.For<ISongInstrumentSupportCache>());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => coordinator.AcquireLeaseAsync(cts.Token));

        _ = metaDatabase.DidNotReceive()
            .AcquireRegistrationMutationLeaseAsync(
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcquireWriteLease_HoldsGateWithoutRefreshingLookupCaches()
    {
        var metaDatabase = Substitute.For<IMetaDatabase>();
        var pathStore = Substitute.For<IPathDataStore>();
        var supportCache =
            Substitute.For<ISongInstrumentSupportCache>();
        var durableLease =
            Substitute.For<IRegistrationMutationLease>();
        metaDatabase
            .AcquireRegistrationMutationLeaseAsync(
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(durableLease));
        var coordinator = new RegistrationMutationCoordinator(
            metaDatabase,
            pathStore,
            supportCache);

        await using (await coordinator
                         .AcquireWriteLeaseAsync())
        {
            pathStore.DidNotReceive()
                .InvalidateCachedState();
            supportCache.DidNotReceive()
                .InvalidateSongInstrumentSupport();
            supportCache.DidNotReceive()
                .RefreshSongInstrumentSupport();
        }

        await durableLease.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireWriteLease_UsesBoundedDatabaseAdmissionAndVerifiesSession()
    {
        var metaDatabase = Substitute.For<IMetaDatabase>();
        var durableLease =
            Substitute.For<IRegistrationMutationLease>();
        metaDatabase
            .TryAcquireRegistrationMutationLeaseAsync(
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(durableLease));
        var coordinator = new RegistrationMutationCoordinator(
            metaDatabase,
            Substitute.For<IPathDataStore>(),
            Substitute.For<ISongInstrumentSupportCache>());

        await using (await coordinator
                         .TryAcquireWriteLeaseAsync())
        {
        }

        _ = metaDatabase.Received(1)
            .TryAcquireRegistrationMutationLeaseAsync(
                Arg.Any<CancellationToken>());
        await durableLease.Received(1)
            .VerifyHeldAsync(
                Arg.Any<CancellationToken>());
        await durableLease.Received(1).DisposeAsync();
    }
}
