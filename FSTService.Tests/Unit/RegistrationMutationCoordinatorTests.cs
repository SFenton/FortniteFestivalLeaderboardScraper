using FSTService.Persistence;
using FSTService.Scraping;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FSTService.Tests.Unit;

public sealed class RegistrationMutationCoordinatorTests
{
    [Fact]
    public void AcquireLease_RefreshFailureDisposesDurableLease()
    {
        var metaDatabase = Substitute.For<IMetaDatabase>();
        var pathStore = Substitute.For<IPathDataStore>();
        var supportCache =
            Substitute.For<ISongInstrumentSupportCache>();
        var durableLease =
            Substitute.For<IRegistrationMutationLease>();
        metaDatabase.AcquireRegistrationMutationLease()
            .Returns(durableLease);
        supportCache
            .When(cache =>
                cache.RefreshSongInstrumentSupport())
            .Throw(new InvalidOperationException(
                "refresh failed"));
        var coordinator = new RegistrationMutationCoordinator(
            metaDatabase,
            pathStore,
            supportCache);

        Assert.Throws<InvalidOperationException>(
            () => coordinator.AcquireLease());

        durableLease.Received(1).Dispose();
    }

    [Fact]
    public void AcquireLease_AlreadyCancelledDoesNotAcquireDurableLease()
    {
        var metaDatabase = Substitute.For<IMetaDatabase>();
        var coordinator = new RegistrationMutationCoordinator(
            metaDatabase,
            Substitute.For<IPathDataStore>(),
            Substitute.For<ISongInstrumentSupportCache>());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => coordinator.AcquireLease(cts.Token));

        metaDatabase.DidNotReceive()
            .AcquireRegistrationMutationLease();
    }
}
