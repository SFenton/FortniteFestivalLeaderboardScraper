using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace FSTService.Api;

/// <summary>
/// Describes whether an API endpoint must read from one published data version or
/// is intentionally outside the public publication boundary.
/// </summary>
public abstract record ApiPublicationClassification;

/// <summary>Public game/content data that must eventually be a function of one publication ID.</summary>
public sealed record PublicationBound : ApiPublicationClassification
{
    public static PublicationBound Instance { get; } = new();

    private PublicationBound()
    {
    }
}

/// <summary>Live operational status or control data that is not publication-versioned.</summary>
public sealed record OperationalLive : ApiPublicationClassification
{
    public OperationalLive(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Reason = reason;
    }

    public string Reason { get; }
}

/// <summary>Administrative, diagnostic, or private account workflow outside public publication reads.</summary>
public sealed record AdminPrivate : ApiPublicationClassification
{
    public AdminPrivate(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Reason = reason;
    }

    public string Reason { get; }
}

/// <summary>
/// The endpoint owns failed-candidate isolation after the outer route cache.
/// It must serve a stable published response or fail closed before live reads.
/// </summary>
public sealed record EndpointHandlesFailedCandidateRead
{
    public static EndpointHandlesFailedCandidateRead Instance { get; } = new();

    private EndpointHandlesFailedCandidateRead()
    {
    }
}

/// <summary>A route/method/classification tuple suitable for publication matrix test generation.</summary>
public sealed record ClassifiedApiRouteDescription(
    string RoutePattern,
    IReadOnlyList<string> HttpMethods,
    ApiPublicationClassification Classification,
    bool HandlesFailedCandidateRead = false)
{
    public string MethodDisplay => string.Join(",", HttpMethods);

    public override string ToString()
        => $"{MethodDisplay} {RoutePattern} => {Classification.GetType().Name}";
}

/// <summary>
/// Intentional, exact route manifest. New API mappings must be added here before startup succeeds.
/// </summary>
public static class ApiPublicationRouteCatalog
{
    public const string AnyMethod = "*";

    private static readonly ClassifiedApiRouteDescription[] Definitions =
    [
        // Publication-bound account identity reads/enrichment.
        Publication(HttpMethods.Post, "/api/account/name-refresh"),
        Publication(HttpMethods.Get, "/api/account/search"),

        // Publication-bound songs, shop content, and generated path artifacts.
        Publication(HttpMethods.Get, "/api/shop"),
        Publication(HttpMethods.Get, "/api/songs"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/songs/member-score-filter"),
        Publication(HttpMethods.Get, "/api/paths/{songId}/{instrument}/{difficulty}"),
        Publication(HttpMethods.Get, "/api/paths/{songId}/{instrument}/{difficulty}/data"),

        // Publication-bound song leaderboard data.
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/leaderboard/{songId}/bands/all"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/leaderboard/{songId}/bands/{bandType}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/leaderboard/{songId}/members/scores"),
        Publication(HttpMethods.Get, "/api/leaderboard/{songId}/{instrument}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/leaderboard-rank-offsets/{songId}/{instrument}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/leaderboard/{songId}/all"),

        // Publication-bound player profiles, history, bands, and exports.
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/player/{accountId}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/player/{accountId}/stats"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/player/{accountId}/bands"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/player/{accountId}/bands/{bandType}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/player/{accountId}/history"),
        Publication(HttpMethods.Get, "/api/player/{accountId}/export"),
        Publication(HttpMethods.Get, "/api/bands/{bandType}/{teamKey}/export"),

        // Publication-bound rivals and score/rank notification feeds.
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/player/{accountId}/leaderboard-rivals/{instrument}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/player/{accountId}/leaderboard-rivals/{instrument}/{rivalId}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/player/{accountId}/rivals"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/player/{accountId}/rivals/suggestions"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/player/{accountId}/rivals/all"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/player/{accountId}/rivals/{combo}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/player/{accountId}/rivals/{combo}/{rivalId}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/player/{accountId}/rivals/{rivalId}/songs/{instrument}"),
        Publication(HttpMethods.Get, "/api/player/{accountId}/notifications"),
        Publication(HttpMethods.Get, "/api/rankings/bands/{bandType}/{teamKey}/notifications"),
        Publication(HttpMethods.Get, "/api/bands/{bandId}/notifications"),

        // Publication-bound rankings, bands, and ranking history.
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/selected-members"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/family/{scopeId}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/family/{scopeId}/{accountId}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/{instrument}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/{instrument}/{accountId}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/{instrument}/{accountId}/history"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/composite"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/composite/{accountId}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/combo"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/combo/{accountId}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/bands/{bandType}/combos"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/bands/{bandType}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/bands/search"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/bands/{bandId}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/bands/{bandType}/{teamKey}/history"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/bands/{bandType}/{teamKey}/songs"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/bands/{bandType}/{teamKey}/song-rows"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/bands/{bandType}/{teamKey}"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/{instrument}/{accountId}/neighborhood"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/composite/{accountId}/neighborhood"),
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/rankings/overview"),

        // Publication-bound derived catalog/population reads.
        PublicationWithEndpointFallback(HttpMethods.Get, "/api/firstseen"),
        Publication(HttpMethods.Get, "/api/leaderboard-population"),

        // Live operational state and controls.
        Operational(HttpMethods.Get, "/api/progress", "Reports live in-process scrape progress."),
        Operational(HttpMethods.Get, "/api/features", "Reports deployment UI feature switches."),
        Operational(HttpMethods.Get, "/api/publication", "Bootstraps the current durable publication generation before public query fan-out."),
        Operational(HttpMethods.Get, "/api/service-info", "Reports live worker, scrape, and publication health."),
        Operational(HttpMethods.Get, "/api/status", "Reports protected live scrape and storage status."),
        Operational(HttpMethods.Get, "/api/version", "Reports immutable service build metadata."),
        Operational(HttpMethods.Post, "/api/player/{accountId}/track", "Starts or refreshes live account synchronization work."),
        Operational(HttpMethods.Get, "/api/player/{accountId}/sync-status", "Reports live account synchronization progress."),
        Operational(HttpMethods.Get, "/api/bands/{bandType}/{teamKey}/sync-status", "Reports live band synchronization progress."),
        Publication(AnyMethod, "/api/ws"),

        // Account/auth, maintenance, diagnostics, and protected controls.
        Private(HttpMethods.Get, "/api/account/check", "Pre-authentication account lookup is part of the account/auth flow."),
        Private(HttpMethods.Get, "/api/admin/epic-token", "Protected credential diagnostic."),
        Private(HttpMethods.Post, "/api/admin/shop/refresh", "Protected shop refresh command."),
        Private(HttpMethods.Post, "/api/register", "Authenticated account registration flow."),
        Private(HttpMethods.Delete, "/api/register", "Authenticated account registration flow."),
        Private(HttpMethods.Post, "/api/firstseen/calculate", "Protected derived-data maintenance command."),
        Private(HttpMethods.Post, "/api/admin/regenerate-paths", "Protected path maintenance command."),
        Private(HttpMethods.Get, "/api/backfill/{accountId}/status", "Protected account maintenance status."),
        Private(HttpMethods.Post, "/api/backfill/{accountId}", "Protected account maintenance command."),
        Private(HttpMethods.Get, "/api/admin/dbstats/queries", "Protected database diagnostics."),
        Private(HttpMethods.Get, "/api/admin/dbstats/bloat", "Protected database diagnostics."),
        Private(HttpMethods.Get, "/api/admin/dbstats/pressure", "Protected database diagnostics."),
        Private(HttpMethods.Get, "/api/admin/public-cache-telemetry", "Protected publication cache diagnostics."),
        Private(HttpMethods.Get, "/api/diag/inflight", "Protected service diagnostics."),
        Private(HttpMethods.Get, "/api/diag/improvement-notifications", "Protected notification publication diagnostics."),
        Private(HttpMethods.Post, "/api/debug/client-interactions", "Client diagnostic telemetry ingestion."),
        Private(HttpMethods.Post, "/api/player/{accountId}/leaderboard-rivals/recompute", "Protected derived-data recomputation command."),
        Private(HttpMethods.Post, "/api/player/{accountId}/rivals/recompute", "Protected derived-data recomputation command."),
        Private(HttpMethods.Get, "/api/player/{accountId}/rivals/diagnostics", "Protected rivals diagnostics."),
        Private(AnyMethod, "/api/{**path}", "Unmatched API fallback returns only a 404 response."),
    ];

    private static readonly IReadOnlyDictionary<RouteKey, ApiPublicationClassification> Classifications =
        BuildClassifications();
    private static readonly IReadOnlySet<RouteKey> EndpointFallbacks =
        BuildEndpointFallbacks();

    public static IReadOnlyList<ClassifiedApiRouteDescription> Routes { get; } =
        Array.AsReadOnly(Definitions);

    internal static ApiPublicationClassification Resolve(string httpMethod, string routePattern)
    {
        var key = new RouteKey(
            httpMethod.ToUpperInvariant(),
            ApiPublicationEndpointDescriptions.CanonicalizeRoutePattern(routePattern));
        if (Classifications.TryGetValue(key, out var classification))
            return classification;

        throw new InvalidOperationException(
            $"API route {httpMethod} {routePattern} has no explicit publication classification.");
    }

    internal static bool HandlesFailedCandidateRead(
        string httpMethod,
        string routePattern)
    {
        var key = new RouteKey(
            httpMethod.ToUpperInvariant(),
            ApiPublicationEndpointDescriptions.CanonicalizeRoutePattern(
                routePattern));
        return EndpointFallbacks.Contains(key);
    }

    private static IReadOnlyDictionary<RouteKey, ApiPublicationClassification> BuildClassifications()
    {
        var result = new Dictionary<RouteKey, ApiPublicationClassification>();
        foreach (var route in Definitions)
        {
            foreach (var method in route.HttpMethods)
            {
                var key = new RouteKey(method.ToUpperInvariant(), route.RoutePattern);
                if (!result.TryAdd(key, route.Classification))
                    throw new InvalidOperationException($"Duplicate API publication classification for {method} {route.RoutePattern}.");
            }
        }

        return result;
    }

    private static IReadOnlySet<RouteKey> BuildEndpointFallbacks()
    {
        var result = new HashSet<RouteKey>();
        foreach (var route in Definitions.Where(
                     static route => route.HandlesFailedCandidateRead))
        {
            foreach (var method in route.HttpMethods)
            {
                result.Add(new RouteKey(
                    method.ToUpperInvariant(),
                    route.RoutePattern));
            }
        }

        return result;
    }

    private static ClassifiedApiRouteDescription Publication(string method, string pattern)
        => new(pattern, [method], PublicationBound.Instance);

    private static ClassifiedApiRouteDescription PublicationWithEndpointFallback(
        string method,
        string pattern)
        => new(
            pattern,
            [method],
            PublicationBound.Instance,
            HandlesFailedCandidateRead: true);

    private static ClassifiedApiRouteDescription Operational(string method, string pattern, string reason)
        => new(pattern, [method], new OperationalLive(reason));

    private static ClassifiedApiRouteDescription Private(string method, string pattern, string reason)
        => new(pattern, [method], new AdminPrivate(reason));

    private readonly record struct RouteKey(string HttpMethod, string RoutePattern);
}

/// <summary>Reflection surface for enumerating the classified API route matrix.</summary>
public static class ApiPublicationEndpointDescriptions
{
    public static IReadOnlyList<RouteEndpoint> GetApiRouteEndpoints(EndpointDataSource dataSource)
        => dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(IsApiRouteEndpoint)
            .OrderBy(endpoint => endpoint.RoutePattern.RawText, StringComparer.Ordinal)
            .ThenBy(GetMethodDisplay, StringComparer.Ordinal)
            .ToArray();

    public static IReadOnlyList<ClassifiedApiRouteDescription> Describe(EndpointDataSource dataSource)
        => GetApiRouteEndpoints(dataSource)
            .Select(Describe)
            .ToArray();

    public static ClassifiedApiRouteDescription Describe(RouteEndpoint endpoint)
    {
        var routePattern = CanonicalizeRoutePattern(
            endpoint.RoutePattern.RawText
            ?? throw new InvalidOperationException("API RouteEndpoint has no raw route pattern."));
        if (!IsApiRouteEndpoint(endpoint))
            throw new ArgumentException($"Route is not an API route: {routePattern}", nameof(endpoint));

        var classifications = endpoint.Metadata.GetOrderedMetadata<ApiPublicationClassification>();
        if (classifications.Count != 1)
        {
            throw new InvalidOperationException(
                $"{GetMethodDisplay(endpoint)} {routePattern} has {classifications.Count} publication classifications; expected exactly one.");
        }

        return new ClassifiedApiRouteDescription(
            routePattern,
            GetHttpMethods(endpoint),
            classifications[0],
            endpoint.Metadata.GetMetadata<EndpointHandlesFailedCandidateRead>()
                is not null);
    }

    public static IReadOnlyList<string> GetHttpMethods(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        return methods is { Count: > 0 }
            ? methods.Order(StringComparer.Ordinal).ToArray()
            : [ApiPublicationRouteCatalog.AnyMethod];
    }

    public static string GetMethodDisplay(RouteEndpoint endpoint)
        => string.Join(",", GetHttpMethods(endpoint));

    internal static bool IsApiRoutePattern(string? routePattern)
    {
        if (string.IsNullOrWhiteSpace(routePattern))
            return false;

        var canonical = CanonicalizeRoutePattern(routePattern);
        return canonical.Equals("/api", StringComparison.OrdinalIgnoreCase)
               || canonical.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
    }

    internal static string CanonicalizeRoutePattern(string routePattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routePattern);
        var trimmed = routePattern.Trim();
        if (trimmed.StartsWith("~/", StringComparison.Ordinal))
            trimmed = trimmed[2..];
        trimmed = trimmed.TrimStart('/');
        return $"/{trimmed}";
    }

    public static void Validate(EndpointDataSource dataSource)
    {
        PublicationRouteSurfaceContractCatalog.Validate(
            ApiPublicationRouteCatalog.Routes);

        var endpoints = GetApiRouteEndpoints(dataSource);
        var failures = new List<string>();

        foreach (var endpoint in endpoints)
        {
            try
            {
                var description = Describe(endpoint);
                foreach (var method in description.HttpMethods)
                {
                    var expected = ApiPublicationRouteCatalog.Resolve(method, description.RoutePattern);
                    if (description.Classification != expected)
                    {
                        failures.Add(
                            $"{method} {description.RoutePattern} is classified as " +
                            $"{description.Classification.GetType().Name}; catalog requires " +
                            $"{expected.GetType().Name}");
                    }

                    var expectedEndpointFallback =
                        ApiPublicationRouteCatalog.HandlesFailedCandidateRead(
                            method,
                            description.RoutePattern);
                    if (description.HandlesFailedCandidateRead
                        != expectedEndpointFallback)
                    {
                        failures.Add(
                            $"{method} {description.RoutePattern} failed-candidate " +
                            $"endpoint ownership is " +
                            $"{description.HandlesFailedCandidateRead}; catalog " +
                            $"requires {expectedEndpointFallback}");
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex.Message);
            }
        }

        var duplicates = endpoints
            .SelectMany(endpoint => GetHttpMethods(endpoint).Select(method => new
            {
                Method = method,
                Route = CanonicalizeRoutePattern(endpoint.RoutePattern.RawText!),
            }))
            .GroupBy(route => $"{route.Method} {route.Route}", StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => $"{group.Key} is mapped {group.Count()} times")
            .ToArray();
        failures.AddRange(duplicates);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "API publication classification validation failed:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, failures));
        }
    }

    private static bool IsApiRouteEndpoint(RouteEndpoint endpoint)
    {
        if (IsApiRoutePattern(endpoint.RoutePattern.RawText))
            return true;

        // A normal-priority route with a dynamic first segment can capture
        // /api requests and outrank the explicit API fallback. Generic SPA
        // fallbacks use int.MaxValue and remain outside this contract.
        if (endpoint.Order == int.MaxValue || endpoint.RoutePattern.PathSegments.Count == 0)
            return false;

        return endpoint.RoutePattern.PathSegments[0].Parts
            .Any(part => part is RoutePatternParameterPart);
    }
}

/// <summary>Mapping convention that attaches metadata from the exact route manifest.</summary>
internal static class ClassifiedApiEndpointMappingExtensions
{
    // WebApplication is more specific than the framework's IEndpointRouteBuilder
    // extensions, so API endpoint registrations consistently pass through this manifest.
    public static RouteHandlerBuilder MapGet(this WebApplication app, string pattern, Delegate handler)
        => Classify(EndpointRouteBuilderExtensions.MapGet(app, pattern, handler), HttpMethods.Get, pattern);

    public static RouteHandlerBuilder MapPost(this WebApplication app, string pattern, Delegate handler)
        => Classify(EndpointRouteBuilderExtensions.MapPost(app, pattern, handler), HttpMethods.Post, pattern);

    public static RouteHandlerBuilder MapDelete(this WebApplication app, string pattern, Delegate handler)
        => Classify(EndpointRouteBuilderExtensions.MapDelete(app, pattern, handler), HttpMethods.Delete, pattern);

    public static RouteHandlerBuilder Map(this WebApplication app, string pattern, Delegate handler)
        => Classify(EndpointRouteBuilderExtensions.Map(app, pattern, handler), ApiPublicationRouteCatalog.AnyMethod, pattern);

    private static RouteHandlerBuilder Classify(RouteHandlerBuilder builder, string method, string pattern)
    {
        if (ApiPublicationEndpointDescriptions.IsApiRoutePattern(pattern))
        {
            builder.WithApiPublicationClassification(method, pattern);
            if (ApiPublicationRouteCatalog.HandlesFailedCandidateRead(
                    method,
                    pattern))
            {
                builder.WithMetadata(
                    EndpointHandlesFailedCandidateRead.Instance);
            }
        }

        return builder;
    }
}

internal static class ApiPublicationEndpointConventionExtensions
{
    public static TBuilder WithApiPublicationClassification<TBuilder>(
        this TBuilder builder,
        string httpMethod,
        string routePattern)
        where TBuilder : IEndpointConventionBuilder
    {
        var classification = ApiPublicationRouteCatalog.Resolve(httpMethod, routePattern);
        builder.Add(endpointBuilder => endpointBuilder.Metadata.Add(classification));
        return builder;
    }
}
