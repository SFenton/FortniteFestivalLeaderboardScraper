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

        if (state.MaxScoreMaintenance
            && ChangesRegistrationState(context.Request))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Retry-After"] = "30";
            await Results.Problem(
                title: "Registration temporarily unavailable",
                detail: "Player and band registration changes are paused while max-score maintenance owns the current publication.",
                statusCode: StatusCodes.Status503ServiceUnavailable)
                .ExecuteAsync(context);
            return;
        }

        if (gate.RequiresCachedReads
            && publicationBound
            && (RequiresPublishedData(context.Request)
                || state.MaxScoreMaintenance
                && RequiresMaxScoreMaintenanceData(context.Request))
            && !(state.MaxScoreMaintenance
                 && EndpointHandlesMaxScoreMaintenanceRead(
                     context.Request))
            && !FailedCandidateReadRoutingPolicy.EndpointHandlesRead(
                context,
                gate))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Retry-After"] = "30";
            await Results.Problem(
                title: "Published data unavailable",
                detail: "Published data is under a fail-closed maintenance or recovery gate. This route is held until a stable published response is available.",
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

    internal static bool RequiresMaxScoreMaintenanceData(
        HttpRequest request)
    {
        var path = request.Path.Value;
        return !string.IsNullOrEmpty(path)
            && (path.Equals(
                    "/api/songs",
                    StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(
                    "/api/paths/",
                    StringComparison.OrdinalIgnoreCase)
                || IsPublishedSoloLeaderboardPath(path));
    }

    internal static bool ChangesRegistrationState(
        HttpRequest request)
    {
        var path = request.Path.Value;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var segments = path.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        return (HttpMethods.IsPost(request.Method)
                && segments.Length == 4
                && string.Equals(
                    segments[0],
                    "api",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    segments[1],
                    "player",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    segments[3],
                    "track",
                    StringComparison.OrdinalIgnoreCase))
            || (HttpMethods.IsGet(request.Method)
                && segments.Length == 5
                && string.Equals(
                    segments[0],
                    "api",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    segments[1],
                    "bands",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    segments[4],
                    "sync-status",
                    StringComparison.OrdinalIgnoreCase))
            || (HttpMethods.IsPost(request.Method)
                && segments.Length == 3
                && string.Equals(
                    segments[0],
                    "api",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    segments[1],
                    "backfill",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static bool EndpointHandlesMaxScoreMaintenanceRead(
        HttpRequest request)
    {
        var path = request.Path.Value;
        return string.Equals(
                   path,
                   "/api/songs",
                   StringComparison.OrdinalIgnoreCase)
               || !string.IsNullOrEmpty(path)
               && path.StartsWith(
                   "/api/paths/",
                   StringComparison.OrdinalIgnoreCase);
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
