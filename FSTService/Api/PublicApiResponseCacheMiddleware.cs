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
        PublicApiCacheTelemetry telemetry,
        PublicationReadContextService? publicationService = null)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            await _next(context);
            return;
        }

        var publicationBound =
            context.GetEndpoint()?.Metadata.GetMetadata<PublicationBound>() is not null;
        if (FailedCandidateReadRoutingPolicy.EndpointHandlesRead(
                context,
                gate))
        {
            if (publicationBound)
                telemetry.Record(context, PublicApiCacheOutcome.Bypassed);
            context.Response.Headers["X-FST-Public-Cache"] =
                "endpoint";
            await _next(context);
            return;
        }

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
            var publicationContext = context.GetPublicationReadContext();
            PublicationCachedResponse? currentCached = null;
            (byte[] Json, string ETag)? cached;
            if (publicationContext is not null)
            {
                cached = metaDb.GetCachedResponse(
                    publicationContext.PublicationId,
                    cacheKey);
            }
            else
            {
                var lookup =
                    metaDb.GetCurrentCacheLookup(cacheKey);
                currentCached = lookup?.CachedResponse;
                cached = currentCached is null
                    ? lookup?.HasCurrentPublication == true
                        || publicationService?.PinningConfigured
                            == true
                        ? null
                        : metaDb.GetCachedResponse(cacheKey)
                    : (currentCached.Json, currentCached.ETag);

                if (currentCached is not null
                    && publicationService?.PinningConfigured == true
                    && !CanServePinnedCacheHit(
                        context,
                        publicationService,
                        currentCached))
                {
                    await _next(context);
                    return;
                }
            }
            var cachedResult = CacheHelper.ServeIfCached(context, cached);
            if (cachedResult is not null)
            {
                if (currentCached is not null)
                {
                    context.Response.Headers[
                        PublicationReadContextMiddleware
                            .PublicationHeader] =
                        currentCached.PublicationId.ToString(
                            System.Globalization.CultureInfo
                                .InvariantCulture);
                    context.Response.Headers.Append(
                        "Vary",
                        PublicationReadContextMiddleware
                            .PublicationHeader);
                    context.SetPublicationReadContext(
                        new PublicationReadContext(
                            currentCached.PublicationId,
                            currentCached.PublishedScrapeId,
                            currentCached.PublishedAtUtc));
                }
                if (publicationBound)
                    telemetry.Record(context, PublicApiCacheOutcome.Hit);
                context.Response.Headers["X-FST-Public-Cache"] = "hit";
                await cachedResult.ExecuteAsync(context);
                SelectedProfileActivityMiddleware
                    .RecordActivityIfNeeded(
                        context,
                        metaDb,
                        context.RequestServices
                            .GetService<
                                Microsoft.Extensions.Options
                                    .IOptions<ScraperOptions>>()
                            ?.Value.RolloutReadOnlyStartup
                        == true);
                return;
            }

            context.Response.Headers["X-FST-Public-Cache"] = "miss";
            if (publicationBound &&
                gate.RequiresCachedReads &&
                !gate.GetState().PublicationCommitPending &&
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

    private static bool CanServePinnedCacheHit(
        HttpContext context,
        PublicationReadContextService publicationService,
        PublicationCachedResponse cached)
    {
        if (!PublicationReadContextMiddleware
                .TryReadRequestedPublicationId(
                    context.Request,
                    out var requestedPublicationId,
                    out _)
            || requestedPublicationId.HasValue
            && requestedPublicationId.Value
                != cached.PublicationId)
        {
            return false;
        }

        try
        {
            var readiness =
                publicationService.EvaluateReadiness(
                    new PublicationPointerState(
                        cached.PublicationId,
                        PreviousPublicationId: null,
                        WorkingPublicationId: null,
                        cached.PublishedScrapeId,
                        cached.PublishedAtUtc));
            return readiness.ReadyForPinning;
        }
        catch
        {
            return false;
        }
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
        var profileInvariant =
            IsProfileInvariantPerInstrumentRanking(request.Path);
        var selectedProfileType = profileInvariant
            ? string.Empty
            : HeaderValue(
                request,
                SelectedProfileHeaders.SelectedProfileTypeHeader);
        var selectedProfileId = profileInvariant
            ? string.Empty
            : HeaderValue(
                request,
                SelectedProfileHeaders.SelectedProfileIdHeader);
        var legacySelectedPlayer = profileInvariant
            ? string.Empty
            : HeaderValue(
                request,
                SelectedProfileHeaders.LegacySelectedPlayerHeader);
        var selectedBandId = profileInvariant
            ? string.Empty
            : HeaderValue(
                request,
                SelectedProfileHeaders.SelectedBandIdHeader);
        var selectedBandType = profileInvariant
            ? string.Empty
            : HeaderValue(
                request,
                SelectedProfileHeaders.SelectedBandTypeHeader);
        var selectedBandTeamKey = profileInvariant
            ? string.Empty
            : HeaderValue(
                request,
                SelectedProfileHeaders.SelectedBandTeamKeyHeader);
        var routeCacheVersion = request.Path.StartsWithSegments(new PathString("/api/leaderboard"), StringComparison.OrdinalIgnoreCase)
            ? "|routeVersion=rank-offsets-v1"
            : string.Empty;

        return string.Concat(
            "public-route:",
            request.Path.Value,
            BuildCanonicalQueryString(request),
            routeCacheVersion,
            "|profileType=", selectedProfileType,
            "|profileId=", selectedProfileId,
            "|legacyPlayer=", legacySelectedPlayer,
            "|bandId=", selectedBandId,
            "|bandType=", selectedBandType,
            "|teamKey=", selectedBandTeamKey);
    }

    internal static string BuildCacheKeyForRequestTarget(
        string requestTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestTarget);
        var queryIndex = requestTarget.IndexOf('?', StringComparison.Ordinal);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = queryIndex < 0
            ? requestTarget
            : requestTarget[..queryIndex];
        if (queryIndex >= 0)
        {
            context.Request.QueryString =
                new QueryString(requestTarget[queryIndex..]);
        }

        return BuildCacheKey(context.Request);
    }

    private static bool IsProfileInvariantPerInstrumentRanking(
        PathString path)
    {
        var segments = path.Value?.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        return segments is { Length: 3 }
            && string.Equals(
                segments[0],
                "api",
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                segments[1],
                "rankings",
                StringComparison.OrdinalIgnoreCase)
            && GlobalLeaderboardPersistence.IsValidInstrument(
                segments[2]);
    }

    private static string BuildCanonicalQueryString(HttpRequest request)
    {
        var values = request.Query
            .Where(pair => !string.Equals(
                pair.Key,
                PublicationReadContextMiddleware.PublicationQueryParameter,
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value.Select(value =>
                new KeyValuePair<string, string?>(pair.Key, value)))
            .OrderBy(
                static pair => pair.Key,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                static pair => pair.Value,
                StringComparer.Ordinal);
        return QueryString.Create(values).Value ?? string.Empty;
    }

    private static string HeaderValue(HttpRequest request, string headerName) =>
        request.Headers.TryGetValue(headerName, out var value) ? value.ToString() : string.Empty;
}