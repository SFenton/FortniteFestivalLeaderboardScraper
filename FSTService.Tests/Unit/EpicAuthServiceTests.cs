using System.Net;
using FSTService.Auth;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public class EpicAuthServiceTests
{
    private readonly ILogger<EpicAuthService> _log = Substitute.For<ILogger<EpicAuthService>>();

    private (EpicAuthService service, MockHttpMessageHandler handler) CreateService()
    {
        var handler = new MockHttpMessageHandler();
        var http = new HttpClient(handler);
        var service = new EpicAuthService(http, _log);
        return (service, handler);
    }

    // ─── RefreshTokenAsync ──────────────────────────────

    [Fact]
    public async Task RefreshTokenAsync_Success_ReturnsToken()
    {
        var (service, handler) = CreateService();

        var json = """
        {
            "access_token": "new_access_token",
            "expires_in": 7200,
            "expires_at": "2099-12-31T23:59:59.000Z",
            "token_type": "bearer",
            "refresh_token": "new_refresh_token",
            "refresh_expires": 28800,
            "refresh_expires_at": "2099-12-31T23:59:59.000Z",
            "account_id": "acct_123",
            "client_id": "client_xyz",
            "displayName": "TestUser"
        }
        """;
        handler.EnqueueJsonOk(json);

        var result = await service.RefreshTokenAsync("old_refresh_token");

        Assert.NotNull(result);
        Assert.Equal("new_access_token", result!.AccessToken);
        Assert.Equal("new_refresh_token", result.RefreshToken);
        Assert.Equal("acct_123", result.AccountId);
        Assert.Equal("TestUser", result.DisplayName);
        Assert.Equal("bearer", result.TokenType);
        Assert.Equal(7200, result.ExpiresIn);
    }

    [Fact]
    public async Task RefreshTokenAsync_AuthorizationPending_ReturnsNull()
    {
        var (service, handler) = CreateService();

        handler.EnqueueError(HttpStatusCode.BadRequest,
            """{"error":"authorization_pending","error_description":"Waiting for client"}""");

        var result = await service.RefreshTokenAsync("some_token");

        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshTokenAsync_ServerError_Throws()
    {
        var (service, handler) = CreateService();

        handler.EnqueueError(HttpStatusCode.InternalServerError, "Server error");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RefreshTokenAsync("some_token"));
    }

    [Fact]
    public async Task RefreshTokenAsync_SendsBasicAuth()
    {
        var (service, handler) = CreateService();

        handler.EnqueueJsonOk("""
        {
            "access_token": "at",
            "expires_in": 3600,
            "token_type": "bearer",
            "account_id": "a"
        }
        """);

        await service.RefreshTokenAsync("rt");

        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Equal("Basic", req.Headers.Authorization?.Scheme);
        Assert.NotNull(req.Headers.Authorization?.Parameter);
    }

    // ─── VerifyTokenAsync ───────────────────────────────

    [Fact]
    public async Task VerifyTokenAsync_Success_ReturnsTrue()
    {
        var (service, handler) = CreateService();

        handler.EnqueueJsonOk("{}");

        var result = await service.VerifyTokenAsync("valid_token");

        Assert.True(result);
        Assert.Single(handler.Requests);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task VerifyTokenAsync_Unauthorized_ReturnsFalse()
    {
        var (service, handler) = CreateService();

        handler.EnqueueError(HttpStatusCode.Unauthorized, "Invalid");

        var result = await service.VerifyTokenAsync("bad_token");

        Assert.False(result);
    }

    [Fact]
    public async Task VerifyTokenAsync_HttpException_ReturnsFalse()
    {
        // Use a handler that throws
        var handler = new ThrowingHttpHandler();
        var http = new HttpClient(handler);
        var service = new EpicAuthService(http, _log);

        var result = await service.VerifyTokenAsync("any_token");

        Assert.False(result);
    }

    // ─── StartDeviceCodeFlowAsync ───────────────────────

    [Fact]
    public async Task StartDeviceCodeFlowAsync_ReturnsDeviceAuth()
    {
        var (service, handler) = CreateService();

        // First call: client_credentials token
        handler.EnqueueJsonOk("""
        {
            "access_token": "cc_token",
            "expires_in": 3600,
            "token_type": "bearer"
        }
        """);

        // Second call: device authorization
        handler.EnqueueJsonOk("""
        {
            "user_code": "ABC123",
            "device_code": "dc_xyz",
            "verification_uri": "https://example.com/activate",
            "verification_uri_complete": "https://example.com/activate?code=ABC123",
            "expires_in": 600,
            "interval": 5
        }
        """);

        var result = await service.StartDeviceCodeFlowAsync();

        Assert.Equal("ABC123", result.UserCode);
        Assert.Equal("dc_xyz", result.DeviceCode);
        Assert.Equal(600, result.ExpiresIn);
        Assert.Equal(5, result.Interval);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task StartDeviceCodeFlowAsync_ClientCredsFail_Throws()
    {
        var (service, handler) = CreateService();

        handler.EnqueueError(HttpStatusCode.Unauthorized, "Invalid client");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartDeviceCodeFlowAsync());
    }

    [Fact]
    public async Task StartDeviceCodeFlowAsync_DeviceAuthFail_Throws()
    {
        var (service, handler) = CreateService();

        // First call: client_credentials succeeds
        handler.EnqueueJsonOk("""
        {
            "access_token": "cc_token",
            "expires_in": 3600,
            "token_type": "bearer"
        }
        """);

        // Second call: device authorization fails
        handler.EnqueueError(HttpStatusCode.Forbidden, "Device authorization denied");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartDeviceCodeFlowAsync());
    }

    [Fact]
    public async Task StartDeviceCodeFlowAsync_ClientCredsAuthPending_Throws()
    {
        var (service, handler) = CreateService();

        // client_credentials returns authorization_pending (edge case → null → throw)
        handler.EnqueueError(HttpStatusCode.BadRequest, """{"errorCode":"authorization_pending"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartDeviceCodeFlowAsync());
    }

    // ─── ParseTokenResponse (tested via RefreshTokenAsync) ──

    [Fact]
    public async Task ParseTokenResponse_MissingOptionalFields_DefaultsUsed()
    {
        var (service, handler) = CreateService();

        // Minimal token response — no refresh_token, no displayName, etc.
        handler.EnqueueJsonOk("""
        {
            "access_token": "at_minimal",
            "expires_in": 1800,
            "token_type": "bearer"
        }
        """);

        var result = await service.RefreshTokenAsync("rt");

        Assert.NotNull(result);
        Assert.Equal("at_minimal", result!.AccessToken);
        Assert.Equal("", result.RefreshToken);
        Assert.Equal("", result.AccountId);
        Assert.Equal("", result.DisplayName);
        Assert.Equal("", result.ClientId);
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Connection refused");
        }
    }

    // ─── ExchangeAuthorizationCodeAsync ─────────────────

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_Success_ReturnsToken()
    {
        var (service, handler) = CreateService();

        handler.EnqueueJsonOk("""
        {
            "access_token": "user_at",
            "expires_in": 7200,
            "expires_at": "2099-12-31T23:59:59.000Z",
            "token_type": "bearer",
            "refresh_token": "user_rt",
            "refresh_expires": 28800,
            "refresh_expires_at": "2099-12-31T23:59:59.000Z",
            "account_id": "user_acct_123",
            "client_id": "eos_client",
            "displayName": "EpicPlayer"
        }
        """);

        var result = await service.ExchangeAuthorizationCodeAsync(
            "auth_code_xyz", "eos_client", "eos_secret", "https://example.com/callback");

        Assert.Equal("user_at", result.AccessToken);
        Assert.Equal("user_rt", result.RefreshToken);
        Assert.Equal("user_acct_123", result.AccountId);
        Assert.Equal("EpicPlayer", result.DisplayName);
        Assert.Equal("eos_client", result.ClientId);

        // Verify the request used the correct auth header
        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Equal("Basic", req.Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_Failure_Throws()
    {
        var (service, handler) = CreateService();

        handler.EnqueueError(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Authorization code expired"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExchangeAuthorizationCodeAsync(
                "expired_code", "client", "secret", "https://example.com/cb"));
    }

    // ─── RefreshUserTokenAsync ──────────────────────────

    [Fact]
    public async Task RefreshUserTokenAsync_Success_ReturnsToken()
    {
        var (service, handler) = CreateService();

        handler.EnqueueJsonOk("""
        {
            "access_token": "refreshed_at",
            "expires_in": 3600,
            "expires_at": "2099-12-31T23:59:59.000Z",
            "token_type": "bearer",
            "refresh_token": "new_rt",
            "refresh_expires": 28800,
            "refresh_expires_at": "2099-12-31T23:59:59.000Z",
            "account_id": "user_acct",
            "displayName": "Player"
        }
        """);

        var result = await service.RefreshUserTokenAsync(
            "old_rt", "client_id", "client_secret");

        Assert.Equal("refreshed_at", result.AccessToken);
        Assert.Equal("new_rt", result.RefreshToken);
        Assert.Equal("user_acct", result.AccountId);

        // Verify correct auth header
        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Equal("Basic", req.Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task RefreshUserTokenAsync_Failure_Throws()
    {
        var (service, handler) = CreateService();

        handler.EnqueueError(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Refresh token expired"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RefreshUserTokenAsync("bad_rt", "client", "secret"));
    }
}
