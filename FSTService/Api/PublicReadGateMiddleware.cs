namespace FSTService.Api;

public sealed class PublicReadGateMiddleware
{
    private readonly RequestDelegate _next;

    public PublicReadGateMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, PublicReadGateService gate)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            await _next(context);
            return;
        }

        var state = gate.GetState();
        var publicationBound =
            context.GetEndpoint()?.Metadata.GetMetadata<PublicationBound>()
            is not null;
        if (state.IsFrozen && IsApiRequest(context.Request))
        {
            context.Response.Headers["X-FST-Public-Read-Mode"] = "published";
            if (!string.IsNullOrWhiteSpace(state.Reason))
                context.Response.Headers["X-FST-Public-Read-Freeze-Reason"] = state.Reason;
        }

        if (gate.RequiresCachedReads
            && publicationBound
            && RequiresPublishedData(context.Request)
            && !FailedCandidateReadRoutingPolicy.EndpointHandlesRead(
                context,
                gate))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Retry-After"] = "30";
            await Results.Problem(
                title: "Published data unavailable",
                detail: "A failed candidate changed unversioned derived data. This route is held until a stable published response is available.",
                statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
            return;
        }

        await _next(context);
    }

    internal static bool IsApiRequest(HttpRequest request)
    {
        var path = request.Path.Value;
        return !string.IsNullOrEmpty(path) && path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool RequiresPublishedData(HttpRequest request)
    {
        var path = request.Path.Value;
        if (!IsApiRequest(request))
            return false;
        if (string.IsNullOrEmpty(path))
            return false;

        if (path.EndsWith("/track", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/sync-status", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.EndsWith("/notifications", StringComparison.OrdinalIgnoreCase))
            return IsRankDerivedNotificationRoute(path);

        if (IsPublishedSoloLeaderboardPath(path))
            return false;

        return path.StartsWith("/api/leaderboard/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/leaderboard-rank-offsets/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/rankings/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/player/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/bands/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/songs/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/firstseen", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/status", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/leaderboard-population", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPublishedSoloLeaderboardPath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 4
            && string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[1], "leaderboard", StringComparison.OrdinalIgnoreCase)
            && segments[3].StartsWith("Solo_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRankDerivedNotificationRoute(string path)
        => path.EndsWith("/notifications", StringComparison.OrdinalIgnoreCase)
           && (path.StartsWith("/api/rankings/bands/", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/api/bands/", StringComparison.OrdinalIgnoreCase));
}

internal static class FailedCandidateReadRoutingPolicy
{
    internal static bool EndpointHandlesRead(
        HttpContext context,
        PublicReadGateService gate)
        => gate.FailedCandidateIsolationActive
           && context.GetEndpoint()?.Metadata
               .GetMetadata<EndpointHandlesFailedCandidateRead>() is not null;
}