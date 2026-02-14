namespace FSTService.Api;

/// <summary>
/// Middleware that rejects requests containing path traversal patterns.
/// Protects against directory traversal attacks that could access
/// sensitive files outside intended API paths.
/// </summary>
public sealed class PathTraversalGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PathTraversalGuardMiddleware> _log;

    private static readonly string[] DangerousPatterns =
    [
        "..",
        "%2e%2e",
        "%2E%2E",
        "%2e.",
        "%2E.",
        ".%2e",
        ".%2E",
    ];

    public PathTraversalGuardMiddleware(RequestDelegate next, ILogger<PathTraversalGuardMiddleware> log)
    {
        _next = next;
        _log = log;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var query = context.Request.QueryString.Value ?? "";

        foreach (var pattern in DangerousPatterns)
        {
            if (path.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                query.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning("Blocked path traversal attempt: {Path}{Query}",
                    path, query);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return context.Response.WriteAsync("Bad request.");
            }
        }

        return _next(context);
    }
}
