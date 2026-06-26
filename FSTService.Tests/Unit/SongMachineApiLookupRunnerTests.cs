using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class SongMachineApiLookupRunnerTests
{
    [Fact]
    public async Task TryRunAsync_ForegroundRegistration_RethrowsCdnBlockedException()
    {
        var runner = new SongMachineApiLookupRunner(null, new ScrapeProgressTracker());
        using var pool = CreatePool();
        var onFailure = Substitute.For<Action<Exception>>();

        await Assert.ThrowsAsync<CdnBlockedException>(() => runner.TryRunAsync<object>(
            pool,
            isHighPriority: true,
            EpicTrafficKind.ForegroundRegistration,
            CancellationToken.None,
            () => throw new CdnBlockedException("cdn exhausted"),
            onFailure));

        onFailure.DidNotReceive().Invoke(Arg.Any<Exception>());
    }

    [Fact]
    public async Task TryRunAsync_BackgroundCdnBlocked_ReturnsFailedResult()
    {
        var runner = new SongMachineApiLookupRunner(null, new ScrapeProgressTracker());
        using var pool = CreatePool();
        var onFailure = Substitute.For<Action<Exception>>();

        var result = await runner.TryRunAsync<object>(
            pool,
            isHighPriority: false,
            EpicTrafficKind.Background,
            CancellationToken.None,
            () => throw new CdnBlockedException("cdn exhausted"),
            onFailure);

        Assert.False(result.Succeeded);
        Assert.Null(result.Value);
        onFailure.Received(1).Invoke(Arg.Any<CdnBlockedException>());
    }

    private static SharedDopPool CreatePool()
    {
        var limiter = new AdaptiveConcurrencyLimiter(
            initialDop: 1,
            minDop: 1,
            maxDop: 1,
            Substitute.For<ILogger<AdaptiveConcurrencyLimiter>>());
        return new SharedDopPool(limiter, lowPrioritySlots: 1);
    }
}
