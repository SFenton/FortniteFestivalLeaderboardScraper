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
}