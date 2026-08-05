using System.Text.Json;
using FSTService.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService.Api;

public sealed class RolloutReadOnlyViolationMonitor
{
    private Exception? _lastViolation;

    public bool HasViolation => Volatile.Read(ref _lastViolation) is not null;

    public Exception? LastViolation => Volatile.Read(ref _lastViolation);

    public void Report(Exception exception) =>
        Interlocked.Exchange(ref _lastViolation, exception);
}

public sealed class RolloutReadOnlyRequestGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _enabled;
    private readonly RolloutReadOnlyViolationMonitor _violations;

    public RolloutReadOnlyRequestGuardMiddleware(
        RequestDelegate next,
        IOptions<ScraperOptions> options,
        RolloutReadOnlyViolationMonitor violations)
    {
        _next = next;
        _enabled = options.Value.RolloutReadOnlyStartup;
        _violations = violations;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_enabled)
        {
            await _next(context);
            return;
        }

        if (IsMutationCapableRequest(context.Request))
        {
            await WriteUnavailableAsync(context);
            return;
        }

        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            var violation = FindReadOnlyViolation(exception);
            if (violation is null)
                throw;
            _violations.Report(violation);
            await WriteUnavailableAsync(context);
        }
    }

    internal static PostgresException? FindReadOnlyViolation(
        Exception exception)
    {
        if (exception is PostgresException postgres
            && postgres.SqlState == "25006")
        {
            return postgres;
        }
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                var nested = FindReadOnlyViolation(inner);
                if (nested is not null)
                    return nested;
            }
        }
        return exception.InnerException is null
            ? null
            : FindReadOnlyViolation(exception.InnerException);
    }

    internal static bool IsMutationCapableRequest(HttpRequest request)
    {
        if (request.Method is not ("GET" or "HEAD" or "OPTIONS"))
            return true;
        if (HttpMethods.IsOptions(request.Method))
            return false;

        var path = CanonicalizePath(request.Path.Value);
        if (path.Equals(
                "/api/admin/epic-token",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 4
            && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("player", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("stats", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return segments.Length == 5
               && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
               && segments[1].Equals("bands", StringComparison.OrdinalIgnoreCase)
               && segments[4].Equals(
                   "sync-status",
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static string CanonicalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return "/";
        var canonical = path.TrimEnd('/');
        return canonical.Length == 0 ? "/" : canonical;
    }

    private static async Task WriteUnavailableAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = "Rollout read-only mode blocks mutation-capable requests.",
        }));
    }
}
