using System.Text.Encodings.Web;
using FSTService.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

public class ApiKeyAuthHandlerTests
{
    private async Task<AuthenticateResult> RunHandler(string? apiKey, string? headerValue)
    {
        var options = new ApiKeyAuthOptions { ApiKey = apiKey ?? "" };
        var optionsMonitor = Substitute.For<IOptionsMonitor<ApiKeyAuthOptions>>();
        optionsMonitor.Get("ApiKey").Returns(options);
        optionsMonitor.CurrentValue.Returns(options);

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var scheme = new AuthenticationScheme("ApiKey", null, typeof(ApiKeyAuthHandler));
        var handler = new ApiKeyAuthHandler(optionsMonitor, loggerFactory, UrlEncoder.Default);

        var context = new DefaultHttpContext();
        if (headerValue is not null)
            context.Request.Headers["X-API-Key"] = headerValue;

        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    [Fact]
    public async Task ValidApiKey_ReturnsSuccess()
    {
        var result = await RunHandler("my-secret-key", "my-secret-key");
        Assert.True(result.Succeeded);
        Assert.NotNull(result.Principal);
    }

    [Fact]
    public async Task WrongApiKey_ReturnsFail()
    {
        var result = await RunHandler("my-secret-key", "wrong-key");
        Assert.False(result.Succeeded);
        Assert.Contains("Invalid API key", result.Failure?.Message);
    }

    [Fact]
    public async Task MissingHeader_ReturnsFail()
    {
        var result = await RunHandler("my-secret-key", null);
        Assert.False(result.Succeeded);
        Assert.Contains("Missing X-API-Key", result.Failure?.Message);
    }

    [Fact]
    public async Task NoApiKeyConfigured_ReturnsFail()
    {
        var result = await RunHandler("", "any-key");
        Assert.False(result.Succeeded);
        Assert.Contains("not configured", result.Failure?.Message);
    }

    [Fact]
    public async Task ApiKey_IsCaseSensitive()
    {
        var result = await RunHandler("MyKey", "mykey");
        Assert.False(result.Succeeded);
    }
}
