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
        if (context.WebSockets.IsWebSocketRequest || !RequiresPublishedData(context.Request) || !gate.IsFrozen)
        {
            await _next(context);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Retry-After"] = "30";
        await Results.Problem(
            title: "Leaderboard update in progress",
            detail: "This response reads rank-derived data that is being republished. Retry after the current update is published.",
            statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
    }

    internal static bool RequiresPublishedData(HttpRequest request)
    {
        var path = request.Path.Value;
        if (string.IsNullOrEmpty(path) || !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            return false;

        return path.EndsWith("/notifications", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith("/api/player/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/export", StringComparison.OrdinalIgnoreCase))
            || path.StartsWith("/api/leaderboard-population", StringComparison.OrdinalIgnoreCase);
    }
}