using System.Reflection;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok("ok"))
           .WithTags("Health")
           .RequireRateLimiting("public");

        app.MapHealthChecks("/readyz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            ResultStatusCodes =
            {
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy] = 200,
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = 503,
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = 503,
            },
        });

        app.MapGet("/api/version", (HttpContext httpContext) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=86400";
            var assembly = typeof(ApiEndpoints).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
            return Results.Ok(new { version });
        })
        .WithTags("Health")
        .RequireRateLimiting("public");

        app.MapGet("/api/progress", (ScrapeProgressTracker tracker) =>
        {
            return Results.Ok(tracker.GetProgressResponse());
        })
        .WithTags("Progress")
        .RequireRateLimiting("public");

        app.MapGet("/api/service-info", (
            HttpContext httpContext,
            ScrapeProgressTracker tracker,
            IMetaDatabase metaDb,
            IOptions<ScraperOptions> scraperOptions) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=1";

            var progress = tracker.GetProgressResponse();
            var current = progress.Current;
            var isUpdating = current is not null;
            var lastCompletedUpdate = metaDb.GetPublishedScrapeRun() ?? metaDb.GetLastCompletedScrapeRun();
            string? nextScheduledUpdateAt = null;
            var workerStatus = BuildWorkerStatus(
                metaDb.GetWorkerStatus(WorkerStatusPublisher.ScraperWorkerKey),
                DateTime.UtcNow);

            if (!isUpdating
                && lastCompletedUpdate?.CompletedAt is not null
                && DateTimeOffset.TryParse(lastCompletedUpdate.CompletedAt, out var completedAtUtc))
            {
                nextScheduledUpdateAt = completedAtUtc
                    .ToUniversalTime()
                    .Add(scraperOptions.Value.ScrapeInterval)
                    .ToString("o");
            }

            return Results.Ok(new
            {
                lastCompletedUpdate = lastCompletedUpdate is null ? null : new
                {
                    startedAt = lastCompletedUpdate.StartedAt,
                    completedAt = lastCompletedUpdate.CompletedAt,
                },
                currentUpdate = new
                {
                    status = isUpdating ? "updating" : "idle",
                    startedAt = current?.StartedAtUtc,
                    phase = current?.Operation,
                    subOperation = current?.SubOperation,
                    progressPercent = current?.ProgressPercent,
                    elapsedSeconds = current?.ElapsedSeconds,
                    estimatedRemainingSeconds = current?.EstimatedRemainingSeconds,
                    branches = current?.Branches,
                },
                workerStatus,
                nextScheduledUpdateAt,
            });
        })
        .WithTags("Health")
        .RequireRateLimiting("public");
    }

    private static object BuildWorkerStatus(WorkerStatusInfo? stored, DateTime nowUtc)
    {
        if (stored is null)
        {
            return new
            {
                workerKey = WorkerStatusPublisher.ScraperWorkerKey,
                status = "unknown",
                rawStatus = (string?)null,
                mode = (string?)null,
                instanceId = (string?)null,
                startedAt = (string?)null,
                lastHeartbeatAt = (string?)null,
                lastStatusChangeAt = (string?)null,
                heartbeatAgeSeconds = (double?)null,
                staleAfterSeconds = 90,
                message = "No worker heartbeat has been recorded yet.",
                currentOperation = (object?)null,
                lastOperation = (object?)null,
            };
        }

        var heartbeatAgeSeconds = stored.LastHeartbeatAtUtc is null
            ? (double?)null
            : Math.Max(0, (nowUtc - stored.LastHeartbeatAtUtc.Value).TotalSeconds);

        return new
        {
            workerKey = stored.WorkerKey,
            status = GetEffectiveWorkerStatus(stored, nowUtc),
            rawStatus = stored.Status,
            mode = stored.Mode,
            instanceId = stored.InstanceId,
            startedAt = FormatUtc(stored.StartedAtUtc),
            lastHeartbeatAt = FormatUtc(stored.LastHeartbeatAtUtc),
            lastStatusChangeAt = FormatUtc(stored.LastStatusChangeAtUtc),
            heartbeatAgeSeconds,
            staleAfterSeconds = 90,
            message = stored.Message,
            currentOperation = FormatWorkerOperation(stored.CurrentOperation),
            lastOperation = FormatWorkerOperation(stored.LastOperation),
        };
    }

    private static string GetEffectiveWorkerStatus(WorkerStatusInfo stored, DateTime nowUtc)
    {
        var raw = stored.Status.ToLowerInvariant();
        if (raw is "offline" or "stopping" or "starting")
            return raw;

        if (stored.LastHeartbeatAtUtc is null)
            return raw == "running" ? "unknown" : raw;

        return nowUtc - stored.LastHeartbeatAtUtc.Value > TimeSpan.FromSeconds(90)
            ? "stale"
            : "online";
    }

    private static object? FormatWorkerOperation(WorkerOperationInfo? operation)
    {
        if (operation is null)
            return null;

        return new
        {
            operationKey = operation.OperationKey,
            operationLabel = operation.OperationLabel,
            status = operation.Status,
            phase = operation.Phase,
            subOperation = operation.SubOperation,
            detail = operation.Detail,
            startedAt = FormatUtc(operation.StartedAtUtc),
            updatedAt = FormatUtc(operation.UpdatedAtUtc),
            endedAt = FormatUtc(operation.EndedAtUtc),
            progressPercent = operation.ProgressPercent,
            elapsedSeconds = operation.ElapsedSeconds,
            estimatedRemainingSeconds = operation.EstimatedRemainingSeconds,
        };
    }

    private static string? FormatUtc(DateTime? value)
        => value?.ToUniversalTime().ToString("o");
}
