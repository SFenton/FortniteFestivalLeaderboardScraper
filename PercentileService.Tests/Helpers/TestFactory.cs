using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace PercentileService.Tests.Helpers;

/// <summary>
/// Factory for creating test-ready service instances with mock dependencies.
/// </summary>
public static class TestFactory
{
    public static IOptions<PercentileOptions> DefaultOptions(Action<PercentileOptions>? configure = null)
    {
        var opts = new PercentileOptions
        {
            AccountId = "test-account-id",
            TokenPath = Path.Combine(Path.GetTempPath(), $"percentile-test-{Guid.NewGuid():N}.json"),
            FstBaseUrl = "http://localhost:9999",
            FstApiKey = "test-api-key",
            ScrapeTimeOfDay = "03:30",
            ScrapeTimeZone = "America/Los_Angeles",
            DegreeOfParallelism = 2,
        };
        configure?.Invoke(opts);
        return Options.Create(opts);
    }

    public static LeaderboardQuerier CreateQuerier(MockHttpHandler handler)
    {
        var http = new HttpClient(handler);
        var log = Substitute.For<ILogger<LeaderboardQuerier>>();
        return new LeaderboardQuerier(http, log);
    }

    public static FstClient CreateFstClient(MockHttpHandler handler, Action<PercentileOptions>? configure = null)
    {
        var http = new HttpClient(handler);
        var opts = DefaultOptions(configure);
        var log = Substitute.For<ILogger<FstClient>>();
        return new FstClient(http, opts, log);
    }

    public static EpicTokenManager CreateTokenManager(MockHttpHandler handler, Action<PercentileOptions>? configure = null)
    {
        var http = new HttpClient(handler);
        var opts = DefaultOptions(configure);
        var log = Substitute.For<ILogger<EpicTokenManager>>();
        return new EpicTokenManager(http, log, opts);
    }
}
