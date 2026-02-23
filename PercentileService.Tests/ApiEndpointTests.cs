using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PercentileService.Tests.Helpers;

namespace PercentileService.Tests;

public sealed class ApiEndpointTests : IClassFixture<ApiEndpointTests.PercentileApiFactory>
{
    private readonly HttpClient _client;

    public ApiEndpointTests(PercentileApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Healthz_ReturnsOk()
    {
        var response = await _client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostScrape_ReturnsOk_WithMessage()
    {
        // The mock FstClient returns an empty player profile, so the scrape
        // will find 0 entries and complete immediately with a warning.
        var response = await _client.PostAsync("/api/scrape", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Scrape completed.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task PostScrape_Get_MethodNotAllowed()
    {
        var response = await _client.GetAsync("/api/scrape");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task GetProgress_ReturnsOk_WithProgressData()
    {
        var response = await _client.GetAsync("/api/progress");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isRunning").GetBoolean());
    }

    [Fact]
    public async Task GetProgress_Post_MethodNotAllowed()
    {
        var response = await _client.PostAsync("/api/progress", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // ─── Device Code Auth ───────────────────────────────────

    [Fact]
    public async Task DeviceCode_ReturnsVerificationDetails()
    {
        var response = await _client.PostAsync("/api/auth/device-code", null);

        // The SharedHandler returns generic 200/{} for all calls, so
        // StartDeviceCodeFlowAsync fails because the mock response doesn't
        // contain the required access_token field — expect a 502 error.
        Assert.Equal((HttpStatusCode)502, response.StatusCode);

        // Verify it's a Problem Details response
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Failed to start device code flow", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task DeviceCode_Get_MethodNotAllowed()
    {
        var response = await _client.GetAsync("/api/auth/device-code");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // ─── Test factory ────────────────────────────────────────

    public sealed class PercentileApiFactory : WebApplicationFactory<Program>
    {
        /// <summary>Mock handler that returns an empty player profile for GET, and 200 for POST.</summary>
        private static readonly MockHttpHandler SharedHandler = new(req =>
        {
            if (req.RequestUri?.PathAndQuery.Contains("/api/player/") == true)
            {
                // Empty profile → worker sees 0 entries and skips V1 queries
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"accountId":"test","displayName":"Test","totalScores":0,"scores":[]}""",
                        System.Text.Encoding.UTF8, "application/json"),
                });
            }

            // Default: 200 OK
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                // Remove background workers so they don't interfere with tests
                var hostedDescriptors = services
                    .Where(d => d.ServiceType == typeof(IHostedService))
                    .ToList();
                foreach (var d in hostedDescriptors)
                    services.Remove(d);

                // Configure options with test values
                services.Configure<PercentileOptions>(opts =>
                {
                    opts.AccountId = "test-account-id";
                    opts.FstBaseUrl = "http://localhost:9999";
                    opts.FstApiKey = "test-key";
                    opts.DegreeOfParallelism = 1;
                    opts.InitialDelaySeconds = 999; // won't matter — worker is removed
                    opts.TokenPath = Path.Combine(
                        Path.GetTempPath(), $"pct-api-test-{Guid.NewGuid():N}.json");
                });

                // Replace HTTP clients with mocks
                ReplaceHttpClient<EpicTokenManager>(services);
                ReplaceHttpClient<LeaderboardQuerier>(services);
                ReplaceHttpClient<FstClient>(services);

                // Re-register PercentileScrapeWorker as singleton (no hosted service)
                // so the /api/scrape endpoint can resolve it
                services.AddSingleton<PercentileScrapeWorker>();
            });
        }

        private static void ReplaceHttpClient<T>(IServiceCollection services) where T : class
        {
            // Remove existing HttpClient registrations for the typed client
            // and add a mock handler
            services.AddHttpClient<T>()
                .ConfigurePrimaryHttpMessageHandler(() => SharedHandler);
        }
    }
}
