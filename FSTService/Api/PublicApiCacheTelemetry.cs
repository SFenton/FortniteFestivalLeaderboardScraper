using System.Collections.Concurrent;
using Microsoft.AspNetCore.Routing;

namespace FSTService.Api;

public enum PublicApiCacheOutcome
{
    Hit,
    MissContinued,
    MissBlocked,
    Bypassed,
}

public sealed class PublicApiCacheTelemetry
{
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private readonly ConcurrentDictionary<RouteKey, RouteCounters> _routes = new();

    public void Record(HttpContext context, PublicApiCacheOutcome outcome)
    {
        var endpoint = context.GetEndpoint() as RouteEndpoint;
        if (endpoint is null || endpoint.Metadata.GetMetadata<PublicationBound>() is null)
            return;

        var routePattern = endpoint.RoutePattern.RawText is { } rawPattern
            ? ApiPublicationEndpointDescriptions.CanonicalizeRoutePattern(rawPattern)
            : string.Empty;
        var declaredMethods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        var method = declaredMethods is { Count: > 0 }
            ? string.Join(",", declaredMethods.Order(StringComparer.Ordinal))
            : ApiPublicationRouteCatalog.AnyMethod;
        var key = new RouteKey(method, routePattern, nameof(PublicationBound));
        _routes.GetOrAdd(key, static _ => new RouteCounters()).Increment(outcome);
    }

    public PublicApiCacheTelemetrySnapshot Snapshot()
    {
        var rows = _routes
            .Select(pair => pair.Value.Snapshot(pair.Key))
            .OrderBy(row => row.RoutePattern, StringComparer.Ordinal)
            .ThenBy(row => row.HttpMethod, StringComparer.Ordinal)
            .ToArray();

        return new PublicApiCacheTelemetrySnapshot(
            _startedAtUtc,
            DateTime.UtcNow,
            rows.Sum(row => row.Hits),
            rows.Sum(row => row.MissesContinued),
            rows.Sum(row => row.MissesBlocked),
            rows.Sum(row => row.Bypassed),
            rows);
    }

    private readonly record struct RouteKey(
        string HttpMethod,
        string RoutePattern,
        string Classification);

    private sealed class RouteCounters
    {
        private long _hits;
        private long _missesContinued;
        private long _missesBlocked;
        private long _bypassed;

        public void Increment(PublicApiCacheOutcome outcome)
        {
            switch (outcome)
            {
                case PublicApiCacheOutcome.Hit:
                    Interlocked.Increment(ref _hits);
                    break;
                case PublicApiCacheOutcome.MissContinued:
                    Interlocked.Increment(ref _missesContinued);
                    break;
                case PublicApiCacheOutcome.MissBlocked:
                    Interlocked.Increment(ref _missesBlocked);
                    break;
                case PublicApiCacheOutcome.Bypassed:
                    Interlocked.Increment(ref _bypassed);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
            }
        }

        public PublicApiCacheTelemetryRoute Snapshot(RouteKey key) => new(
            key.HttpMethod,
            key.RoutePattern,
            key.Classification,
            Interlocked.Read(ref _hits),
            Interlocked.Read(ref _missesContinued),
            Interlocked.Read(ref _missesBlocked),
            Interlocked.Read(ref _bypassed));
    }
}

public sealed record PublicApiCacheTelemetrySnapshot(
    DateTime StartedAtUtc,
    DateTime CapturedAtUtc,
    long Hits,
    long MissesContinued,
    long MissesBlocked,
    long Bypassed,
    IReadOnlyList<PublicApiCacheTelemetryRoute> Routes)
{
    public long Total => Hits + MissesContinued + MissesBlocked + Bypassed;
}

public sealed record PublicApiCacheTelemetryRoute(
    string HttpMethod,
    string RoutePattern,
    string Classification,
    long Hits,
    long MissesContinued,
    long MissesBlocked,
    long Bypassed)
{
    public long Total => Hits + MissesContinued + MissesBlocked + Bypassed;
}
