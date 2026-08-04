using FSTService.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FSTService.Tests.Integration;

public sealed class PublicationRouteSurfaceContractTests
    : IClassFixture<ApiEndpointIntegrationTests.FstWebApplicationFactory>
{
    private readonly ApiEndpointIntegrationTests.FstWebApplicationFactory _factory;

    public PublicationRouteSurfaceContractTests(
        ApiEndpointIntegrationTests.FstWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void EveryPublicationBoundRouteHasExactlyOneSurfaceContract()
    {
        var routes = ApiPublicationEndpointDescriptions
            .Describe(_factory.Services.GetRequiredService<EndpointDataSource>())
            .Where(static route => route.Classification is PublicationBound)
            .SelectMany(route => route.HttpMethods.Select(method =>
                (Method: method, route.RoutePattern)))
            .ToArray();

        Assert.Equal(55, routes.Length);
        Assert.Equal(
            routes.Length,
            routes.Distinct().Count());

        foreach (var route in routes)
        {
            var contract = PublicationRouteSurfaceContractCatalog.Resolve(
                route.Method,
                route.RoutePattern);
            Assert.NotEmpty(contract.RequiredSurfaces);
        }
    }

    [Fact]
    public void ContractValidationRejectsDuplicateDefinitions()
    {
        var contracts = PublicationRouteSurfaceContractCatalog.Routes
            .Append(PublicationRouteSurfaceContractCatalog.Routes[0]);

        var failures =
            PublicationRouteSurfaceContractCatalog.GetValidationFailures(
                ApiPublicationRouteCatalog.Routes,
                contracts);

        Assert.Contains(
            failures,
            static failure => failure.StartsWith(
                "Duplicate surface contract",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ContractValidationRejectsUnmappedPublicationRoute()
    {
        var contracts = PublicationRouteSurfaceContractCatalog.Routes
            .Skip(1);

        var failures =
            PublicationRouteSurfaceContractCatalog.GetValidationFailures(
                ApiPublicationRouteCatalog.Routes,
                contracts);

        Assert.Contains(
            failures,
            static failure => failure.Contains(
                "has no surface contract",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ContractValidationRejectsStaleDefinition()
    {
        var contracts = PublicationRouteSurfaceContractCatalog.Routes
            .Append(new PublicationRouteSurfaceContract(
                HttpMethods.Get,
                "/api/stale-publication-contract",
                "account-identity",
                [FSTService.Persistence.PublicationSurfaceNames.AccountNames]));

        var failures =
            PublicationRouteSurfaceContractCatalog.GetValidationFailures(
                ApiPublicationRouteCatalog.Routes,
                contracts);

        Assert.Contains(
            failures,
            static failure => failure.Contains(
                "has no PublicationBound route",
                StringComparison.Ordinal));
    }
}
