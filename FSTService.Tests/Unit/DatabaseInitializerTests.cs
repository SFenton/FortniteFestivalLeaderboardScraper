using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

public class DatabaseInitializerTests : IDisposable
{
    private readonly InMemoryMetaDatabase _metaFixture;
    private readonly GlobalLeaderboardPersistence _persistence;

    public DatabaseInitializerTests()
    {
        _metaFixture = new InMemoryMetaDatabase();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _persistence = new GlobalLeaderboardPersistence(
            _metaFixture.Db, loggerFactory,
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>(),
            _metaFixture.DataSource,
            Options.Create(new FeatureOptions()));
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _metaFixture.Dispose();
    }

    [Fact]
    public async Task CheckHealthAsync_BeforeInit_ReturnsUnhealthy()
    {
        var festivalService = new FestivalService((IFestivalPersistence?)null);
        var handler = new HttpClient(new NoOpHandler());
        var shopService = new ItemShopService(handler, festivalService, _metaFixture.Db,
            Substitute.For<ILogger<ItemShopService>>());
        var lifetime = Substitute.For<IHostApplicationLifetime>();

        var init = new StartupInitializer(
            _persistence, festivalService, shopService, lifetime,
            Substitute.For<ILogger<StartupInitializer>>());

        Assert.False(init.IsReady);
        var result = await init.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task StartAsync_InitializesAndSignalsReady()
    {
        var festivalService = new FestivalService((IFestivalPersistence?)null);
        var handler = new HttpClient(new NoOpHandler());
        var shopService = new ItemShopService(handler, festivalService, _metaFixture.Db,
            Substitute.For<ILogger<ItemShopService>>());
        var lifetime = Substitute.For<IHostApplicationLifetime>();

        var init = new StartupInitializer(
            _persistence, festivalService, shopService, lifetime,
            Substitute.For<ILogger<StartupInitializer>>());

        await init.StartAsync(CancellationToken.None);

        // Wait for background init
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await init.WaitForReadyAsync(cts.Token);

        Assert.True(init.IsReady);
        var result = await init.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task StopAsync_ReturnsCompletedTask()
    {
        var festivalService = new FestivalService((IFestivalPersistence?)null);
        var handler = new HttpClient(new NoOpHandler());
        var shopService = new ItemShopService(handler, festivalService, _metaFixture.Db,
            Substitute.For<ILogger<ItemShopService>>());
        var lifetime = Substitute.For<IHostApplicationLifetime>();

        var init = new StartupInitializer(
            _persistence, festivalService, shopService, lifetime,
            Substitute.For<ILogger<StartupInitializer>>());

        await init.StopAsync(CancellationToken.None);
    }

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
