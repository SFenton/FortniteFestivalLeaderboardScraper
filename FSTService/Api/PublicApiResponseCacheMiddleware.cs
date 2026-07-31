using FSTService.Persistence;

namespace FSTService.Api;

public sealed class PublicApiResponseCacheMiddleware
{
    private readonly RequestDelegate _next;

    public PublicApiResponseCacheMiddleware(RequestDelegate next, ILogger<PublicApiResponseCacheMiddleware> log)
    {
        _next = next;
        _ = log;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IMetaDatabase metaDb,
        PublicReadGateService gate,
        PublicApiCacheTelemetry telemetry)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            await _next(context);
            return;
        }

        var publicationBound =
            context.GetEndpoint()?.Metadata.GetMetadata<PublicationBound>() is not null;
        if (!PublicApiResponseCachePolicy.IsCacheableRequest(context.Request, out var cacheKey))
        {
            if (publicationBound && gate.IsFrozen)
            {
                telemetry.Record(context, PublicApiCacheOutcome.Bypassed);
            }
            await _next(context);
            return;
        }

        if (gate.IsFrozen)
        {
            var cached = metaDb.GetCachedResponse(cacheKey);
            var cachedResult = CacheHelper.ServeIfCached(context, cached);
            if (cachedResult is not null)
            {
                if (publicationBound)
                    telemetry.Record(context, PublicApiCacheOutcome.Hit);
                context.Response.Headers["X-FST-Public-Cache"] = "hit";
                await cachedResult.ExecuteAsync(context);
                return;
            }

            context.Response.Headers["X-FST-Public-Cache"] = "miss";
            if (gate.RequiresCachedReads &&
                PublicReadGateMiddleware.RequiresPublishedData(context.Request))
            {
                if (publicationBound)
                    telemetry.Record(context, PublicApiCacheOutcome.MissBlocked);
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Retry-After"] = "30";
                await Results.Problem(
                    title: "Published data unavailable",
                    detail: "A failed candidate changed unversioned derived data. This route is held until a stable published response is available.",
                    statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
                return;
            }

            if (publicationBound)
                telemetry.Record(context, PublicApiCacheOutcome.MissContinued);
            await _next(context);
            return;
        }

        await _next(context);
    }
}

internal static class PublicApiResponseCachePolicy
{
    private static readonly string[] LivePrefixes =
    [
        "/api/account/",
        "/api/admin/",
        "/api/backfill/",
        "/api/diag/",
        "/api/paths/",
    ];

    private static readonly string[] LiveExactPaths =
    [
        "/api/features",
        "/api/progress",
        "/api/service-info",
        "/api/shop",
        "/api/songs",
        "/api/status",
        "/api/version",
    ];

    public static bool IsCacheableRequest(HttpRequest request, out string cacheKey)
    {
        cacheKey = string.Empty;

        if (!HttpMethods.IsGet(request.Method) || request.HttpContext.WebSockets.IsWebSocketRequest)
            return false;

        var path = request.Path.Value;
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            return false;

        if (LiveExactPaths.Any(livePath => string.Equals(path, livePath, StringComparison.OrdinalIgnoreCase)) ||
            LivePrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
            HasSelectedOverlayQuery(request) ||
            path.EndsWith("/notifications", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/diagnostics", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/sync-status", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/export", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        cacheKey = BuildCacheKey(request);
        return true;
    }

    private static bool HasSelectedOverlayQuery(HttpRequest request) =>
        request.Query.ContainsKey("accountId") ||
        request.Query.ContainsKey("teamKey") ||
        request.Query.ContainsKey("selectedTeamKey") ||
        request.Query.ContainsKey("selectedBandType");

    internal static string BuildCacheKey(HttpRequest request)
    {
        var selectedProfileType = HeaderValue(request, SelectedProfileHeaders.SelectedProfileTypeHeader);
        var selectedProfileId = HeaderValue(request, SelectedProfileHeaders.SelectedProfileIdHeader);
        var legacySelectedPlayer = HeaderValue(request, SelectedProfileHeaders.LegacySelectedPlayerHeader);
        var selectedBandId = HeaderValue(request, SelectedProfileHeaders.SelectedBandIdHeader);
        var selectedBandType = HeaderValue(request, SelectedProfileHeaders.SelectedBandTypeHeader);
        var selectedBandTeamKey = HeaderValue(request, SelectedProfileHeaders.SelectedBandTeamKeyHeader);
        var routeCacheVersion = request.Path.StartsWithSegments(new PathString("/api/leaderboard"), StringComparison.OrdinalIgnoreCase)
            ? "|routeVersion=rank-offsets-v1"
            : string.Empty;

        return string.Concat(
            "public-route:",
            request.Path.Value,
            request.QueryString.Value,
            routeCacheVersion,
            "|profileType=", selectedProfileType,
            "|profileId=", selectedProfileId,
            "|legacyPlayer=", legacySelectedPlayer,
            "|bandId=", selectedBandId,
            "|bandType=", selectedBandType,
            "|teamKey=", selectedBandTeamKey);
    }

    private static string HeaderValue(HttpRequest request, string headerName) =>
        request.Headers.TryGetValue(headerName, out var value) ? value.ToString() : string.Empty;
}