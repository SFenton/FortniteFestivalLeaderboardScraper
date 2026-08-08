using FSTService.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace FSTService.Tests.Integration;

public sealed class ApiPublicationClassificationTests
    : IClassFixture<ApiEndpointIntegrationTests.FstWebApplicationFactory>
{
    private readonly ApiEndpointIntegrationTests.FstWebApplicationFactory _factory;

    public ApiPublicationClassificationTests(ApiEndpointIntegrationTests.FstWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void EveryApiRoute_HasExactlyOnePublicationClassification()
    {
        var endpoints = GetApiEndpoints();
        Assert.NotEmpty(endpoints);

        var failures = endpoints
            .Select(endpoint => new
            {
                Endpoint = endpoint,
                Classifications = endpoint.Metadata.GetOrderedMetadata<ApiPublicationClassification>(),
            })
            .Where(candidate => candidate.Classifications.Count != 1)
            .Select(candidate =>
                $"{ApiPublicationEndpointDescriptions.GetMethodDisplay(candidate.Endpoint)} " +
                $"{candidate.Endpoint.RoutePattern.RawText}: found {candidate.Classifications.Count}")
            .ToArray();

        Assert.True(
            failures.Length == 0,
            "Every /api RouteEndpoint must have exactly one publication classification." +
            Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void ClassifiedRouteDescriptions_MatchTheIntentionalRouteCatalog()
    {
        var actual = ApiPublicationEndpointDescriptions.Describe(GetEndpointDataSource())
            .SelectMany(ToClassificationKeys)
            .ToArray();
        var expected = ExpectedRoutes
            .Select(ToClassificationKey)
            .ToArray();

        var missing = expected.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var unexpected = actual.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var duplicates = actual
            .GroupBy(key => key, StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => $"{group.Key} appears {group.Count()} times")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0 && unexpected.Length == 0 && duplicates.Length == 0,
            $"Classified route descriptions differ from the route catalog.{Environment.NewLine}" +
            $"Missing:{Environment.NewLine}{string.Join(Environment.NewLine, missing)}{Environment.NewLine}" +
            $"Unexpected:{Environment.NewLine}{string.Join(Environment.NewLine, unexpected)}{Environment.NewLine}" +
            $"Duplicates:{Environment.NewLine}{string.Join(Environment.NewLine, duplicates)}");

        var catalog = ApiPublicationRouteCatalog.Routes
            .SelectMany(ToClassificationKeys)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), catalog);
    }

    [Fact]
    public void KnownPublicDataRoutes_ArePublicationBound()
        => AssertRoutesHaveClassification<PublicationBound>(PublicationRoutes);

    [Fact]
    public void KnownOperationalRoutes_AreOperationalLive()
        => AssertRoutesHaveClassification<OperationalLive>(OperationalRoutes);

    [Fact]
    public void AdminDiagnosticAndAuthRoutes_AreAdminPrivate()
        => AssertRoutesHaveClassification<AdminPrivate>(AdminPrivateRoutes);

    [Fact]
    public void FailedCandidateEndpointOwnership_MatchesTheIntentionalRouteCatalog()
    {
        var actual = ApiPublicationEndpointDescriptions
            .Describe(GetEndpointDataSource())
            .SelectMany(route => route.HttpMethods.Select(method =>
                $"{method} {route.RoutePattern} " +
                $"{route.HandlesFailedCandidateRead}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = ApiPublicationRouteCatalog.Routes
            .SelectMany(route => route.HttpMethods.Select(method =>
                $"{method} {route.RoutePattern} " +
                $"{route.HandlesFailedCandidateRead}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("/api/player/{accountId}/export")]
    [InlineData("/api/bands/{bandType}/{teamKey}/export")]
    [InlineData("/api/rankings/bands/{bandType}/{teamKey}/notifications")]
    [InlineData("/api/bands/{bandId}/notifications")]
    [InlineData("/api/leaderboard-population")]
    public void FailedCandidateUnguardedRoutes_RemainOuterBlocked(
        string routePattern)
    {
        var route = Assert.Single(
            ApiPublicationEndpointDescriptions
                .Describe(GetEndpointDataSource()),
            candidate => string.Equals(
                candidate.RoutePattern,
                routePattern,
                StringComparison.Ordinal));

        Assert.False(route.HandlesFailedCandidateRead);
    }

    [Theory]
    [InlineData("/api/example")]
    [InlineData("api/example")]
    [InlineData("~/api/example")]
    public void ApiRouteSpellings_AreCanonicalized(string routePattern)
    {
        Assert.True(ApiPublicationEndpointDescriptions.IsApiRoutePattern(routePattern));
        Assert.Equal(
            "/api/example",
            ApiPublicationEndpointDescriptions.CanonicalizeRoutePattern(routePattern));
    }

    [Fact]
    public void StartupValidation_RejectsAnUnclassifiedApiEndpoint()
    {
        var endpoint = new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("api/unclassified"),
            order: 0,
            new EndpointMetadataCollection(new HttpMethodMetadata([HttpMethods.Get])),
            displayName: "unclassified");
        var dataSource = new DefaultEndpointDataSource(endpoint);

        var error = Assert.Throws<InvalidOperationException>(
            () => ApiPublicationEndpointDescriptions.Validate(dataSource));

        Assert.Contains("GET /api/unclassified has 0 publication classifications", error.Message);
    }

    [Fact]
    public void StartupValidation_RejectsADynamicPrefixThatCanCaptureApiTraffic()
    {
        var endpoint = new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/{prefix}/unclassified"),
            order: 0,
            new EndpointMetadataCollection(new HttpMethodMetadata([HttpMethods.Get])),
            displayName: "dynamic-prefix");
        var dataSource = new DefaultEndpointDataSource(endpoint);

        var error = Assert.Throws<InvalidOperationException>(
            () => ApiPublicationEndpointDescriptions.Validate(dataSource));

        Assert.Contains("GET /{prefix}/unclassified has 0 publication classifications", error.Message);
    }

    [Fact]
    public void StartupValidation_RejectsMetadataThatDoesNotMatchTheCatalog()
    {
        var endpoint = new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/shop"),
            order: 0,
            new EndpointMetadataCollection(
                new HttpMethodMetadata([HttpMethods.Get]),
                new OperationalLive("incorrect test classification")),
            displayName: "incorrectly-classified");
        var dataSource = new DefaultEndpointDataSource(endpoint);

        var error = Assert.Throws<InvalidOperationException>(
            () => ApiPublicationEndpointDescriptions.Validate(dataSource));

        Assert.Contains("catalog requires PublicationBound", error.Message);
    }

    private void AssertRoutesHaveClassification<TClassification>(IEnumerable<RouteExpectation> expectations)
        where TClassification : ApiPublicationClassification
    {
        var endpoints = GetApiEndpoints();
        foreach (var expectation in expectations)
        {
            var endpoint = Assert.Single(
                endpoints,
                endpoint => string.Equals(
                                endpoint.RoutePattern.RawText,
                                expectation.RoutePattern,
                                StringComparison.Ordinal)
                            && ApiPublicationEndpointDescriptions.GetHttpMethods(endpoint)
                                .Contains(expectation.HttpMethod, StringComparer.Ordinal));
            var classification = Assert.Single(
                endpoint.Metadata.GetOrderedMetadata<ApiPublicationClassification>());

            Assert.True(
                classification is TClassification,
                $"{expectation.HttpMethod} {expectation.RoutePattern} was " +
                $"{classification.GetType().Name}; expected {typeof(TClassification).Name}.");

            if (classification is OperationalLive operational)
                Assert.False(string.IsNullOrWhiteSpace(operational.Reason));
            if (classification is AdminPrivate adminPrivate)
                Assert.False(string.IsNullOrWhiteSpace(adminPrivate.Reason));
        }
    }

    private IReadOnlyList<RouteEndpoint> GetApiEndpoints()
        => ApiPublicationEndpointDescriptions.GetApiRouteEndpoints(GetEndpointDataSource());

    private EndpointDataSource GetEndpointDataSource()
        => _factory.Services.GetRequiredService<EndpointDataSource>();

    private static readonly RouteExpectation[] PublicationRoutes =
    [
        new(HttpMethods.Post, "/api/account/name-refresh"),
        new(HttpMethods.Get, "/api/account/search"),
        new(HttpMethods.Get, "/api/shop"),
        new(HttpMethods.Get, "/api/songs"),
        new(HttpMethods.Get, "/api/songs/member-score-filter"),
        new(HttpMethods.Get, "/api/paths/{songId}/{instrument}/{difficulty}"),
        new(HttpMethods.Get, "/api/paths/{songId}/{instrument}/{difficulty}/data"),
        new(HttpMethods.Get, "/api/leaderboard/{songId}/bands/all"),
        new(HttpMethods.Get, "/api/leaderboard/{songId}/bands/{bandType}"),
        new(HttpMethods.Get, "/api/leaderboard/{songId}/members/scores"),
        new(HttpMethods.Get, "/api/leaderboard/{songId}/{instrument}"),
        new(HttpMethods.Get, "/api/leaderboard-rank-offsets/{songId}/{instrument}"),
        new(HttpMethods.Get, "/api/leaderboard/{songId}/all"),
        new(HttpMethods.Get, "/api/player/{accountId}"),
        new(HttpMethods.Get, "/api/player/{accountId}/stats"),
        new(HttpMethods.Get, "/api/player/{accountId}/bands"),
        new(HttpMethods.Get, "/api/player/{accountId}/bands/{bandType}"),
        new(HttpMethods.Get, "/api/player/{accountId}/history"),
        new(HttpMethods.Get, "/api/player/{accountId}/export"),
        new(HttpMethods.Get, "/api/bands/{bandType}/{teamKey}/export"),
        new(HttpMethods.Get, "/api/player/{accountId}/leaderboard-rivals/{instrument}"),
        new(HttpMethods.Get, "/api/player/{accountId}/leaderboard-rivals/{instrument}/{rivalId}"),
        new(HttpMethods.Get, "/api/player/{accountId}/rivals"),
        new(HttpMethods.Get, "/api/player/{accountId}/rivals/suggestions"),
        new(HttpMethods.Get, "/api/player/{accountId}/rivals/all"),
        new(HttpMethods.Get, "/api/player/{accountId}/rivals/{combo}"),
        new(HttpMethods.Get, "/api/player/{accountId}/rivals/{combo}/{rivalId}"),
        new(HttpMethods.Get, "/api/player/{accountId}/rivals/{rivalId}/songs/{instrument}"),
        new(HttpMethods.Get, "/api/player/{accountId}/notifications"),
        new(HttpMethods.Get, "/api/rankings/bands/{bandType}/{teamKey}/notifications"),
        new(HttpMethods.Get, "/api/bands/{bandId}/notifications"),
        new(HttpMethods.Get, "/api/rankings/selected-members"),
        new(HttpMethods.Get, "/api/rankings/family/{scopeId}"),
        new(HttpMethods.Get, "/api/rankings/family/{scopeId}/{accountId}"),
        new(HttpMethods.Get, "/api/rankings/{instrument}"),
        new(HttpMethods.Get, "/api/rankings/{instrument}/{accountId}"),
        new(HttpMethods.Get, "/api/rankings/{instrument}/{accountId}/history"),
        new(HttpMethods.Get, "/api/rankings/composite"),
        new(HttpMethods.Get, "/api/rankings/composite/{accountId}"),
        new(HttpMethods.Get, "/api/rankings/combo"),
        new(HttpMethods.Get, "/api/rankings/combo/{accountId}"),
        new(HttpMethods.Get, "/api/rankings/bands/{bandType}/combos"),
        new(HttpMethods.Get, "/api/rankings/bands/{bandType}"),
        new(HttpMethods.Get, "/api/bands/search"),
        new(HttpMethods.Get, "/api/bands/{bandId}"),
        new(HttpMethods.Get, "/api/rankings/bands/{bandType}/{teamKey}/history"),
        new(HttpMethods.Get, "/api/rankings/bands/{bandType}/{teamKey}/songs"),
        new(HttpMethods.Get, "/api/rankings/bands/{bandType}/{teamKey}/song-rows"),
        new(HttpMethods.Get, "/api/rankings/bands/{bandType}/{teamKey}"),
        new(HttpMethods.Get, "/api/rankings/{instrument}/{accountId}/neighborhood"),
        new(HttpMethods.Get, "/api/rankings/composite/{accountId}/neighborhood"),
        new(HttpMethods.Get, "/api/rankings/overview"),
        new(HttpMethods.Get, "/api/firstseen"),
        new(HttpMethods.Get, "/api/leaderboard-population"),
    ];

    private static readonly RouteExpectation[] OperationalRoutes =
    [
        new(HttpMethods.Get, "/api/progress"),
        new(HttpMethods.Get, "/api/features"),
        new(HttpMethods.Get, "/api/publication"),
        new(HttpMethods.Get, "/api/service-info"),
        new(HttpMethods.Get, "/api/status"),
        new(HttpMethods.Get, "/api/version"),
        new(HttpMethods.Post, "/api/player/{accountId}/track"),
        new(HttpMethods.Get, "/api/player/{accountId}/sync-status"),
        new(HttpMethods.Get, "/api/bands/{bandType}/{teamKey}/sync-status"),
    ];

    private static readonly RouteExpectation[] AdminPrivateRoutes =
    [
        new(HttpMethods.Get, "/api/account/check"),
        new(HttpMethods.Get, "/api/admin/epic-token"),
        new(HttpMethods.Post, "/api/admin/shop/refresh"),
        new(HttpMethods.Post, "/api/register"),
        new(HttpMethods.Delete, "/api/register"),
        new(HttpMethods.Post, "/api/firstseen/calculate"),
        new(HttpMethods.Post, "/api/admin/regenerate-paths"),
        new(HttpMethods.Get, "/api/backfill/{accountId}/status"),
        new(HttpMethods.Post, "/api/backfill/{accountId}"),
        new(HttpMethods.Get, "/api/admin/dbstats/queries"),
        new(HttpMethods.Get, "/api/admin/dbstats/bloat"),
        new(HttpMethods.Get, "/api/admin/dbstats/pressure"),
        new(HttpMethods.Get, "/api/admin/public-cache-telemetry"),
        new(HttpMethods.Get, "/api/diag/inflight"),
        new(HttpMethods.Get, "/api/diag/improvement-notifications"),
        new(HttpMethods.Post, "/api/debug/client-interactions"),
        new(HttpMethods.Post, "/api/player/{accountId}/leaderboard-rivals/recompute"),
        new(HttpMethods.Post, "/api/player/{accountId}/rivals/recompute"),
        new(HttpMethods.Get, "/api/player/{accountId}/rivals/diagnostics"),
        new(ApiPublicationRouteCatalog.AnyMethod, "/api/{**path}"),
    ];

    private static readonly ExpectedRoute[] ExpectedRoutes =
        PublicationRoutes
            .Select(route => new ExpectedRoute(route.HttpMethod, route.RoutePattern, typeof(PublicationBound)))
            .Append(new ExpectedRoute(ApiPublicationRouteCatalog.AnyMethod, "/api/ws", typeof(PublicationBound)))
            .Concat(OperationalRoutes.Select(route =>
                new ExpectedRoute(route.HttpMethod, route.RoutePattern, typeof(OperationalLive))))
            .Concat(AdminPrivateRoutes.Select(route =>
                new ExpectedRoute(route.HttpMethod, route.RoutePattern, typeof(AdminPrivate))))
            .ToArray();

    private static IEnumerable<string> ToClassificationKeys(ClassifiedApiRouteDescription route)
        => route.HttpMethods.Select(method =>
            $"{method} {route.RoutePattern} {route.Classification.GetType().Name}");

    private static string ToClassificationKey(ExpectedRoute route)
        => $"{route.HttpMethod} {route.RoutePattern} {route.ClassificationType.Name}";

    private sealed record RouteExpectation(string HttpMethod, string RoutePattern);

    private sealed record ExpectedRoute(
        string HttpMethod,
        string RoutePattern,
        Type ClassificationType);
}
