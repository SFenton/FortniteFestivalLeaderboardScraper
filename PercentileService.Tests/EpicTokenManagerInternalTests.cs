using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PercentileService.Tests.Helpers;

namespace PercentileService.Tests;

public sealed class EpicTokenManagerInternalTests : IDisposable
{
    private readonly string _tokenPath;

    public EpicTokenManagerInternalTests()
    {
        _tokenPath = Path.Combine(Path.GetTempPath(), $"etm-internal-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try { File.Delete(_tokenPath); } catch { }
    }

    [Fact]
    public void ApplyTokenResponse_parses_all_fields()
    {
        var handler = MockHttpHandler.WithJsonResponse("{}");
        var mgr = CreateManager(handler);

        mgr.ApplyTokenResponse("""
        {
            "access_token": "my-access",
            "refresh_token": "my-refresh",
            "account_id": "acct123",
            "displayName": "TestPlayer",
            "expires_in": 7200
        }
        """);

        Assert.Equal("my-access", mgr.AccessToken);
        Assert.Equal("acct123", mgr.AccountId);
        Assert.Equal("TestPlayer", mgr.DisplayName);
        Assert.True(mgr.IsAuthenticated);
    }

    [Fact]
    public void ApplyTokenResponse_handles_missing_displayName()
    {
        var handler = MockHttpHandler.WithJsonResponse("{}");
        var mgr = CreateManager(handler);

        mgr.ApplyTokenResponse("""
        {
            "access_token": "tok",
            "refresh_token": "ref",
            "account_id": "a1",
            "expires_in": 3600
        }
        """);

        Assert.Equal("tok", mgr.AccessToken);
        Assert.Null(mgr.DisplayName); // No displayName in response
    }

    [Fact]
    public void ApplyTokenResponse_preserves_previous_displayName()
    {
        var handler = MockHttpHandler.WithJsonResponse("{}");
        var mgr = CreateManager(handler);

        // First: set displayName
        mgr.ApplyTokenResponse("""
        {
            "access_token": "t1", "refresh_token": "r1",
            "account_id": "a1", "displayName": "Original", "expires_in": 3600
        }
        """);
        Assert.Equal("Original", mgr.DisplayName);

        // Second: no displayName → keeps "Original"
        mgr.ApplyTokenResponse("""
        {
            "access_token": "t2", "refresh_token": "r2",
            "account_id": "a1", "expires_in": 3600
        }
        """);
        Assert.Equal("Original", mgr.DisplayName);
    }

    [Fact]
    public void IsAuthenticated_false_when_expired()
    {
        var handler = MockHttpHandler.WithJsonResponse("{}");
        var mgr = CreateManager(handler);

        mgr.ApplyTokenResponse("""
        {
            "access_token": "expired-tok",
            "refresh_token": "ref",
            "account_id": "a1",
            "expires_in": 0
        }
        """);

        // expires_in=0 + time to parse → expired
        Assert.False(mgr.IsAuthenticated);
    }

    [Fact]
    public async Task DeviceCodeFlow_success_with_polling()
    {
        int callCount = 0;
        var handler = new MockHttpHandler(req =>
        {
            callCount++;
            var url = req.RequestUri!.ToString();

            if (url.Contains("/oauth/token") && callCount == 1)
            {
                // client_credentials response
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    { "access_token": "cc-token", "expires_in": 3600 }
                    """, System.Text.Encoding.UTF8, "application/json"),
                });
            }

            if (url.Contains("/deviceAuthorization"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                        "device_code": "dc123",
                        "user_code": "ABC123",
                        "verification_uri_complete": "https://epic.com/activate?code=ABC123",
                        "expires_in": 600,
                        "interval": 1
                    }
                    """, System.Text.Encoding.UTF8, "application/json"),
                });
            }

            if (url.Contains("/oauth/token"))
            {
                // First poll: pending. Second poll: success.
                if (callCount <= 4) // cc, deviceAuth, poll1, poll2
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("""
                        { "errorCode": "authorization_pending" }
                        """, System.Text.Encoding.UTF8, "application/json"),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                        "access_token": "final-token",
                        "refresh_token": "final-refresh",
                        "account_id": "acct-dc",
                        "displayName": "DeviceUser",
                        "expires_in": 7200
                    }
                    """, System.Text.Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var mgr = CreateManager(handler);

        await mgr.EnsureAuthenticatedAsync(CancellationToken.None);

        Assert.True(mgr.IsAuthenticated);
        Assert.Equal("final-token", mgr.AccessToken);
        Assert.Equal("DeviceUser", mgr.DisplayName);
        Assert.Equal("acct-dc", mgr.AccountId);

        // Verify credentials were saved
        Assert.True(File.Exists(_tokenPath));
    }

    [Fact]
    public async Task DeviceCodeFlow_fails_on_deviceAuthorization_error()
    {
        int callCount = 0;
        var handler = new MockHttpHandler(req =>
        {
            callCount++;
            if (callCount == 1) // client_credentials
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    { "access_token": "cc-token", "expires_in": 3600 }
                    """, System.Text.Encoding.UTF8, "application/json"),
                });
            }
            // deviceAuthorization fails
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"error":"forbidden"}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        });

        var mgr = CreateManager(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mgr.EnsureAuthenticatedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeviceCodeFlow_fails_on_non_pending_poll_error()
    {
        int callCount = 0;
        var handler = new MockHttpHandler(req =>
        {
            callCount++;
            var url = req.RequestUri!.ToString();

            if (callCount == 1) // client_credentials
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "access_token": "cc", "expires_in": 3600 }""",
                        System.Text.Encoding.UTF8, "application/json"),
                });
            }

            if (url.Contains("/deviceAuthorization"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                        "device_code": "dc",
                        "user_code": "UC",
                        "verification_uri_complete": "https://epic.com/activate",
                        "expires_in": 600,
                        "interval": 1
                    }
                    """, System.Text.Encoding.UTF8, "application/json"),
                });
            }

            // Poll returns a non-pending error (e.g., "expired")
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{ "errorCode": "expired" }""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        });

        var mgr = CreateManager(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mgr.EnsureAuthenticatedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EnsureAuthenticated_creates_directory_for_token_on_save()
    {
        var nestedPath = Path.Combine(Path.GetTempPath(), $"nested-{Guid.NewGuid():N}", "sub", "token.json");

        int callCount = 0;
        var handler = new MockHttpHandler(req =>
        {
            callCount++;
            if (callCount == 1) // cc
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "access_token": "cc", "expires_in": 3600 }""",
                        System.Text.Encoding.UTF8, "application/json"),
                });
            if (req.RequestUri!.ToString().Contains("/deviceAuthorization"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    { "device_code":"d","user_code":"U","verification_uri_complete":"https://x","expires_in":600,"interval":1 }
                    """, System.Text.Encoding.UTF8, "application/json"),
                });
            // Immediate success
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                { "access_token":"a","refresh_token":"r","account_id":"id","displayName":"N","expires_in":3600 }
                """, System.Text.Encoding.UTF8, "application/json"),
            });
        });

        var opts = Options.Create(new PercentileOptions { TokenPath = nestedPath });
        var mgr = new EpicTokenManager(new HttpClient(handler),
            Substitute.For<ILogger<EpicTokenManager>>(), opts);

        await mgr.EnsureAuthenticatedAsync(CancellationToken.None);

        Assert.True(File.Exists(nestedPath));

        // Cleanup
        try { Directory.Delete(Path.GetDirectoryName(nestedPath)!, true); } catch { }
    }

    private EpicTokenManager CreateManager(MockHttpHandler handler)
    {
        var opts = Options.Create(new PercentileOptions { TokenPath = _tokenPath });
        return new EpicTokenManager(
            new HttpClient(handler),
            Substitute.For<ILogger<EpicTokenManager>>(), opts);
    }
}
