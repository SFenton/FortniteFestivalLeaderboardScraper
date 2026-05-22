using FSTService.Persistence;

namespace FSTService.Api;

public sealed class PublicApiResponseCacheMiddleware
{
    private const long MaxCacheableBytes = 10 * 1024 * 1024;

    private readonly RequestDelegate _next;
    private readonly ILogger<PublicApiResponseCacheMiddleware> _log;

    public PublicApiResponseCacheMiddleware(RequestDelegate next, ILogger<PublicApiResponseCacheMiddleware> log)
    {
        _next = next;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext context, IMetaDatabase metaDb, PublicReadGateService gate)
    {
        if (!PublicApiResponseCachePolicy.IsCacheableRequest(context.Request, out var cacheKey))
        {
            await _next(context);
            return;
        }

        if (gate.IsFrozen)
        {
            var cached = metaDb.GetCachedResponse(cacheKey);
            var cachedResult = CacheHelper.ServeIfCached(context, cached);
            if (cachedResult is not null)
            {
                context.Response.Headers["X-FST-Public-Cache"] = "hit";
                await cachedResult.ExecuteAsync(context);
                return;
            }

            context.Response.Headers["X-FST-Public-Cache"] = "miss";
            await _next(context);
            return;
        }

        var originalBody = context.Response.Body;
        await using var capture = new MemoryStream();
        context.Response.Body = capture;

        try
        {
            await _next(context);

            if (ShouldStoreResponse(context.Response, capture.Length))
            {
                var json = capture.ToArray();
                var etag = context.Response.Headers.ETag.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(etag))
                    etag = ResponseCacheService.ComputeETag(json);

                try
                {
                    metaDb.BulkSetCachedResponses([(cacheKey, json, etag)]);
                    context.Response.Headers["X-FST-Public-Cache"] = "store";
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to store public API response cache entry for {CacheKey}.", cacheKey);
                }
            }

            capture.Position = 0;
            await capture.CopyToAsync(originalBody, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static bool ShouldStoreResponse(HttpResponse response, long bodyLength)
    {
        if (response.StatusCode != StatusCodes.Status200OK)
            return false;
        if (bodyLength <= 0 || bodyLength > MaxCacheableBytes)
            return false;

        return response.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true;
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

        return string.Concat(
            "public-route:",
            request.Path.Value,
            request.QueryString.Value,
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