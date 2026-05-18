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
        if (state.IsFrozen && IsApiRequest(context.Request))
        {
            context.Response.Headers["X-FST-Public-Read-Mode"] = "published";
            if (!string.IsNullOrWhiteSpace(state.Reason))
                context.Response.Headers["X-FST-Public-Read-Freeze-Reason"] = state.Reason;
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

        return path.EndsWith("/notifications", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/leaderboard-population", StringComparison.OrdinalIgnoreCase);
    }
}