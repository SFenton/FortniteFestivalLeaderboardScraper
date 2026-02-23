namespace PercentileService.Tests;

public sealed class PercentileOptionsTests
{
    [Fact]
    public void Defaults_are_correct()
    {
        var opts = new PercentileOptions();

        Assert.Equal("195e93ef108143b2975ee46662d4d0e1", opts.AccountId);
        Assert.Equal("data/percentile-auth.json", opts.TokenPath);
        Assert.Equal(TimeSpan.FromHours(4), opts.TokenRefreshInterval);
        Assert.Equal("03:30", opts.ScrapeTimeOfDay);
        Assert.Equal("America/Los_Angeles", opts.ScrapeTimeZone);
        Assert.Equal("http://localhost:8080", opts.FstBaseUrl);
        Assert.Equal("", opts.FstApiKey);
        Assert.Equal(8, opts.DegreeOfParallelism);
        Assert.Equal(2048, opts.MaxDegreeOfParallelism);
        Assert.Equal(1024, opts.StartingDegreeOfParallelism);
        Assert.Equal(2, opts.MinDegreeOfParallelism);
    }

    [Fact]
    public void Section_name_is_Percentile()
    {
        Assert.Equal("Percentile", PercentileOptions.Section);
    }

    [Fact]
    public void Properties_are_settable()
    {
        var opts = new PercentileOptions
        {
            AccountId = "abc",
            TokenPath = "/tmp/test.json",
            TokenRefreshInterval = TimeSpan.FromMinutes(30),
            ScrapeTimeOfDay = "12:00",
            ScrapeTimeZone = "UTC",
            FstBaseUrl = "http://example.com",
            FstApiKey = "key123",
            DegreeOfParallelism = 16,
            MaxDegreeOfParallelism = 4096,
            StartingDegreeOfParallelism = 2048,
            MinDegreeOfParallelism = 4,
        };

        Assert.Equal("abc", opts.AccountId);
        Assert.Equal("/tmp/test.json", opts.TokenPath);
        Assert.Equal(TimeSpan.FromMinutes(30), opts.TokenRefreshInterval);
        Assert.Equal("12:00", opts.ScrapeTimeOfDay);
        Assert.Equal("UTC", opts.ScrapeTimeZone);
        Assert.Equal("http://example.com", opts.FstBaseUrl);
        Assert.Equal("key123", opts.FstApiKey);
        Assert.Equal(16, opts.DegreeOfParallelism);
        Assert.Equal(4096, opts.MaxDegreeOfParallelism);
        Assert.Equal(2048, opts.StartingDegreeOfParallelism);
        Assert.Equal(4, opts.MinDegreeOfParallelism);
    }
}
