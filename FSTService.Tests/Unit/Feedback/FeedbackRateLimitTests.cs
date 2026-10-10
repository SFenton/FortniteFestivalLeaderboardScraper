using System.Text.Json;
using System.Threading.RateLimiting;
using FSTService.Api;

namespace FSTService.Tests.Unit.Feedback;

public sealed class FeedbackRateLimitTests
{
    [Fact]
    public void Defaults_AllowSixtySubmissionsPerHour()
    {
        var options = new FeedbackOptions();

        Assert.Equal(60, options.SubmissionsPerWindow);
        Assert.Equal(60, options.SubmissionWindowMinutes);
    }

    [Fact]
    public void Appsettings_MatchDefaultSubmissionLimit()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
        var feedback = document.RootElement.GetProperty(FeedbackOptions.Section);
        var defaults = new FeedbackOptions();

        Assert.Equal(defaults.SubmissionsPerWindow, feedback.GetProperty("SubmissionsPerWindow").GetInt32());
        Assert.Equal(defaults.SubmissionWindowMinutes, feedback.GetProperty("SubmissionWindowMinutes").GetInt32());
    }

    [Fact]
    public void LimiterOptions_UseConfiguredWindowWithoutQueueing()
    {
        var limiter = ApiEndpoints.CreateFeedbackLimiterOptions(new FeedbackOptions());

        Assert.Equal(60, limiter.PermitLimit);
        Assert.Equal(TimeSpan.FromMinutes(60), limiter.Window);
        Assert.Equal(0, limiter.QueueLimit);
    }

    [Fact]
    public void LimiterOptions_ClampNonPositiveSettings()
    {
        var limiter = ApiEndpoints.CreateFeedbackLimiterOptions(
            new FeedbackOptions { SubmissionsPerWindow = 0, SubmissionWindowMinutes = -5 });

        Assert.Equal(1, limiter.PermitLimit);
        Assert.Equal(TimeSpan.FromMinutes(1), limiter.Window);
    }

    [Fact]
    public void DefaultLimiter_AcceptsTwentyReportsInARow()
    {
        using var limiter = new FixedWindowRateLimiter(
            ApiEndpoints.CreateFeedbackLimiterOptions(new FeedbackOptions()));

        for (var i = 0; i < 20; i++)
        {
            using var lease = limiter.AttemptAcquire();
            Assert.True(lease.IsAcquired, $"submission {i + 1} was rate limited");
        }
    }

    [Fact]
    public void DefaultLimiter_StillRejectsFloodsPastTheHourlyCap()
    {
        using var limiter = new FixedWindowRateLimiter(
            ApiEndpoints.CreateFeedbackLimiterOptions(new FeedbackOptions()));

        for (var i = 0; i < 60; i++)
        {
            using var lease = limiter.AttemptAcquire();
            Assert.True(lease.IsAcquired);
        }

        using var rejected = limiter.AttemptAcquire();
        Assert.False(rejected.IsAcquired);
    }
}
