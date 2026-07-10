using FSTService.Api;
using FSTService.Scraping;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FSTService.Tests.Unit;

public sealed class DiagEndpointSecurityTests
{
    [Fact]
    public void MapDiagEndpoints_RemovesTokenBackedRoutesAndProtectsInflightDiagnostics()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<GlobalLeaderboardScraper>(
            _ => throw new InvalidOperationException("Route metadata test does not resolve the scraper."));
        builder.Services.AddSingleton<ProxyHandlerAccessor>(
            _ => throw new InvalidOperationException("Route metadata test does not resolve the proxy accessor."));
        var app = builder.Build();

        app.MapDiagEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/diag/events");
        Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/diag/leaderboard");

        var inflight = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/api/diag/inflight");
        Assert.Contains(inflight.Metadata, metadata => metadata is IAuthorizeData);
    }
}
