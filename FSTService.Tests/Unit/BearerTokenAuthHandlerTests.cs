using System.Text.Encodings.Web;
using FSTService.Api;
using FSTService.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

public class BearerTokenAuthHandlerTests
{
    private static JwtTokenService CreateJwtService()
    {
        var settings = Options.Create(new JwtSettings
        {
            Secret = "ThisIsATestSecretThatIsAtLeast32CharsLong!!",
            Issuer = "FSTService-Test",
            AccessTokenLifetimeMinutes = 60,
            RefreshTokenLifetimeDays = 7,
        });
        return new JwtTokenService(settings);
    }

    private async Task<AuthenticateResult> RunHandler(JwtTokenService jwt, string? headerValue)
    {
        var options = new BearerAuthOptions();
        var optionsMonitor = Substitute.For<IOptionsMonitor<BearerAuthOptions>>();
        optionsMonitor.Get("Bearer").Returns(options);
        optionsMonitor.CurrentValue.Returns(options);

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var scheme = new AuthenticationScheme("Bearer", null, typeof(BearerTokenAuthHandler));
        var handler = new BearerTokenAuthHandler(optionsMonitor, loggerFactory, UrlEncoder.Default, jwt);

        var context = new DefaultHttpContext();
        if (headerValue is not null)
            context.Request.Headers["Authorization"] = headerValue;

        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    [Fact]
    public async Task ValidToken_ReturnsSuccess()
    {
        var jwt = CreateJwtService();
        var token = jwt.GenerateAccessToken("testuser", "device1");

        var result = await RunHandler(jwt, $"Bearer {token}");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Principal);
        var sub = result.Principal!.FindFirst("sub")?.Value;
        Assert.Equal("testuser", sub);
    }

    [Fact]
    public async Task MissingHeader_ReturnsFail()
    {
        var jwt = CreateJwtService();
        var result = await RunHandler(jwt, null);

        Assert.False(result.Succeeded);
        Assert.Contains("Missing Authorization header", result.Failure?.Message);
    }

    [Fact]
    public async Task NonBearerScheme_ReturnsFail()
    {
        var jwt = CreateJwtService();
        var result = await RunHandler(jwt, "Basic abc123");

        Assert.False(result.Succeeded);
        Assert.Contains("not a Bearer token", result.Failure?.Message);
    }

    [Fact]
    public async Task EmptyBearerToken_ReturnsFail()
    {
        var jwt = CreateJwtService();
        var result = await RunHandler(jwt, "Bearer ");

        Assert.False(result.Succeeded);
        Assert.Contains("empty", result.Failure?.Message);
    }

    [Fact]
    public async Task InvalidToken_ReturnsFail()
    {
        var jwt = CreateJwtService();
        var result = await RunHandler(jwt, "Bearer not.a.valid.token");

        Assert.False(result.Succeeded);
        Assert.Contains("Invalid", result.Failure?.Message);
    }

    [Fact]
    public async Task ExpiredToken_ReturnsFail()
    {
        // Create a JWT service with very short lifetime
        var settings = Options.Create(new JwtSettings
        {
            Secret = "ThisIsATestSecretThatIsAtLeast32CharsLong!!",
            Issuer = "FSTService-Test",
            AccessTokenLifetimeMinutes = -1, // Already expired
            RefreshTokenLifetimeDays = 7,
        });
        var jwt = new JwtTokenService(settings);
        var token = jwt.GenerateAccessToken("testuser", "device1");

        // Use the same secret for validation
        var result = await RunHandler(jwt, $"Bearer {token}");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task TokenFromDifferentSecret_ReturnsFail()
    {
        // Generate token with one secret
        var jwt1 = CreateJwtService();
        var token = jwt1.GenerateAccessToken("testuser", "device1");

        // Validate with different secret
        var settings2 = Options.Create(new JwtSettings
        {
            Secret = "ADifferentSecretKeyThatIsAlso32CharsLong!!",
            Issuer = "FSTService-Test",
            AccessTokenLifetimeMinutes = 60,
            RefreshTokenLifetimeDays = 7,
        });
        var jwt2 = new JwtTokenService(settings2);

        var result = await RunHandler(jwt2, $"Bearer {token}");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ValidToken_ContainsDeviceIdClaim()
    {
        var jwt = CreateJwtService();
        var token = jwt.GenerateAccessToken("testuser", "my-device");

        var result = await RunHandler(jwt, $"Bearer {token}");

        Assert.True(result.Succeeded);
        var deviceId = result.Principal!.FindFirst("deviceId")?.Value;
        Assert.Equal("my-device", deviceId);
    }
}
