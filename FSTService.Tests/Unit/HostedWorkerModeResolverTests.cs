using FSTService;

namespace FSTService.Tests.Unit;

public class HostedWorkerModeResolverTests
{
    [Fact]
    public void Resolve_NoFlags_ReturnsFullWorker()
    {
        var mode = HostedWorkerModeResolver.Resolve(
            apiOnlyRequested: false,
            scraperWorkerDisabled: false,
            registrationSyncWorkerRequested: false);

        Assert.Equal(HostedWorkerMode.FullWorker, mode);
    }

    [Fact]
    public void Resolve_ApiOnly_WinsOverMutationModes()
    {
        var mode = HostedWorkerModeResolver.Resolve(
            apiOnlyRequested: true,
            scraperWorkerDisabled: true,
            registrationSyncWorkerRequested: true);

        Assert.Equal(HostedWorkerMode.ApiOnly, mode);
    }

    [Fact]
    public void Resolve_RegistrationSyncWorker_WinsOverFrontendOnly()
    {
        var mode = HostedWorkerModeResolver.Resolve(
            apiOnlyRequested: false,
            scraperWorkerDisabled: true,
            registrationSyncWorkerRequested: true);

        Assert.Equal(HostedWorkerMode.RegistrationSyncWorker, mode);
    }

    [Fact]
    public void Resolve_DisableScraperWorker_ReturnsFrontendOnly()
    {
        var mode = HostedWorkerModeResolver.Resolve(
            apiOnlyRequested: false,
            scraperWorkerDisabled: true,
            registrationSyncWorkerRequested: false);

        Assert.Equal(HostedWorkerMode.FrontendOnly, mode);
    }

    [Theory]
    [InlineData((int)HostedWorkerMode.FullWorker, false, false, true)]
    [InlineData((int)HostedWorkerMode.FullWorker, true, false, false)]
    [InlineData((int)HostedWorkerMode.FullWorker, false, true, false)]
    [InlineData((int)HostedWorkerMode.RegistrationSyncWorker, true, true, true)]
    [InlineData((int)HostedWorkerMode.ApiOnly, false, false, false)]
    [InlineData((int)HostedWorkerMode.FrontendOnly, false, false, false)]
    public void ShouldRunRegistrationBackfillWorker_IsolatesFullWorkerRunOnce(
        int modeValue,
        bool runOnceRequested,
        bool backfillOnlyRequested,
        bool expected)
    {
        var mode = (HostedWorkerMode)modeValue;

        Assert.Equal(
            expected,
            HostedWorkerModeResolver.ShouldRunRegistrationBackfillWorker(
                mode,
                runOnceRequested,
                backfillOnlyRequested));
    }

    [Theory]
    [InlineData((int)HostedWorkerMode.FullWorker)]
    [InlineData((int)HostedWorkerMode.ApiOnly)]
    [InlineData((int)HostedWorkerMode.FrontendOnly)]
    [InlineData((int)HostedWorkerMode.RegistrationSyncWorker)]
    public void ResolveHostedServicePlan_ReadOnlyRolloutSuppressesMutationServices(
        int modeValue)
    {
        var plan = HostedWorkerModeResolver.ResolveHostedServicePlan(
            (HostedWorkerMode)modeValue,
            rolloutReadOnlyStartup: true,
            runOnceRequested: false,
            backfillOnlyRequested: false);

        Assert.False(plan.RegisterStalenessMonitor);
        Assert.False(plan.RegisterPublicationChangeMonitor);
        Assert.False(plan.RegisterSongCatalogRefresh);
        Assert.False(plan.RegisterRegistrationBackfill);
        Assert.False(plan.RegisterFullWorkerServices);
    }
}
