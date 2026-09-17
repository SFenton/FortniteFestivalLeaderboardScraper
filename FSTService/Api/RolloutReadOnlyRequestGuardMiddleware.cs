using System.Text.Json;
using FSTService.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService.Api;

public sealed class RolloutReadOnlyViolationMonitor
{
    private Exception? _lastViolation;
    private long _violationCount;

    public bool HasViolation => Volatile.Read(ref _lastViolation) is not null;

    public Exception? LastViolation => Volatile.Read(ref _lastViolation);
    public long ViolationCount => Interlocked.Read(ref _violationCount);

    public void Report(Exception exception)
    {
        Interlocked.Exchange(ref _lastViolation, exception);
        Interlocked.Increment(ref _violationCount);
    }
}

public sealed class RolloutReadOnlyRequestGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _enabled;
    private readonly RolloutReadOnlyViolationMonitor _violations;
    private readonly StartupPublicationReadOnlyState? _publicationStartup;

    public RolloutReadOnlyRequestGuardMiddleware(
        RequestDelegate next,
        IOptions<ScraperOptions> options,
        RolloutReadOnlyViolationMonitor violations,
        StartupPublicationReadOnlyState? publicationStartup = null)
    {
        _next = next;
        _enabled = options.Value.RolloutReadOnlyStartup;
        _violations = violations;
        _publicationStartup = publicationStartup;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var readOnly = _enabled || _publicationStartup?.IsLatched == true;
        if (!readOnly && _publicationStartup is { MutationsReady: false })
        {
            if (IsMutationCapableRequest(context.Request) || context.WebSockets.IsWebSocketRequest)
                await WriteUnavailableAsync(context, "startup_initializing", "Initialization has not admitted mutations.");
            else
                await _next(context);
            return;
        }
        if (!readOnly)
        {
            await _next(context);
            return;
        }

        if (IsMutationCapableRequest(context.Request) || context.WebSockets.IsWebSocketRequest)
        {
            await WriteUnavailableAsync(context,
                _enabled ? "rollout_read_only" : "startup_read_only",
                _publicationStartup?.Reason ?? "Rollout read-only mode blocks mutation-capable requests.");
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
            if (_enabled)
                _violations.Report(violation);
            await WriteUnavailableAsync(context,
                _enabled ? "rollout_read_only" : "startup_read_only",
                _publicationStartup?.Reason ?? "Rollout read-only mode blocks mutation-capable requests.");
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

    private static async Task WriteUnavailableAsync(HttpContext context, string code, string error)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.RetryAfter = "1";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error,
            code,
        }));
    }
}
