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

            var nowUtc = DateTime.UtcNow;
            var progress = tracker.GetProgressResponse();
            var localCurrent = progress.Current;
            var runtime = metaDb.GetServiceRuntimeState(WorkerStatusPublisher.ScraperWorkerKey);
            var storedWorker = runtime.WorkerStatus;
            var effectiveWorkerStatus = storedWorker is null
                ? "unknown"
                : GetEffectiveWorkerStatus(storedWorker, nowUtc);
            var durableCurrent = storedWorker?.CurrentOperation is { Status: var operationStatus } operation
                && string.Equals(operationStatus, "running", StringComparison.OrdinalIgnoreCase)
                    ? operation
                    : null;
            var lastFailedOperation = IsFailedScrapeOperation(storedWorker?.LastOperation)
                ? storedWorker!.LastOperation
                : null;
            var publishedScrape = runtime.PublishedScrape;
            var latestScrape = runtime.LatestScrape;
            var candidateScrape = latestScrape is not null && latestScrape.Id != publishedScrape?.Id
                ? latestScrape
                : null;
            var activeScrape = candidateScrape
                ?? (IsScrapeOperation(durableCurrent) ? latestScrape : null);
            var workerUnavailable = effectiveWorkerStatus is "offline" or "stale" or "stopping" or "unknown";
            var durableCandidateFailed = string.Equals(
                candidateScrape?.Status,
                "failed",
                StringComparison.OrdinalIgnoreCase);

            var currentStatus = durableCurrent is not null || localCurrent is not null
                ? "updating"
                : candidateScrape is not null && (durableCandidateFailed || lastFailedOperation is not null)
                    ? "failed"
                    : runtime.PublicReadFreeze.IsFrozen || candidateScrape is not null
                        ? workerUnavailable ? "stalled" : "updating"
                        : "idle";

            var currentPhase = durableCurrent?.Phase
                ?? localCurrent?.Operation
                ?? (currentStatus == "failed" ? lastFailedOperation?.Phase : null)
                ?? (currentStatus == "failed" ? candidateScrape?.FailurePhase : null)
                ?? GetFreezePhase(runtime.PublicReadFreeze.Reason)
                ?? (candidateScrape?.CompletedAt is not null ? "Publishing" : candidateScrape is not null ? "Scraping" : null);
            var currentSubOperation = durableCurrent?.SubOperation
                ?? localCurrent?.SubOperation
                ?? (currentStatus == "failed" ? "failed" : runtime.PublicReadFreeze.Reason);
            var currentStartedAt = activeScrape?.StartedAt
                ?? FormatUtc(durableCurrent?.StartedAtUtc)
                ?? localCurrent?.StartedAtUtc?.ToUniversalTime().ToString("o");
            var currentElapsedSeconds = durableCurrent is null
                ? localCurrent?.ElapsedSeconds
                : Math.Max(
                    durableCurrent.ElapsedSeconds ?? 0,
                    Math.Max(0, (nowUtc - durableCurrent.StartedAtUtc).TotalSeconds));
            var nextScheduledUpdateAt = GetNextScheduledUpdateAt(
                runtime,
                currentStatus,
                effectiveWorkerStatus,
                scraperOptions.Value.ScrapeInterval,
                nowUtc);
            var workerStatus = BuildWorkerStatus(storedWorker, nowUtc);

            return Results.Ok(new
            {
                lastCompletedUpdate = publishedScrape is null ? null : new
                {
                    scrapeId = publishedScrape.Id,
                    startedAt = publishedScrape.StartedAt,
                    completedAt = publishedScrape.CompletedAt,
                    publishedAt = FormatUtc(runtime.PublishedAtUtc),
                    bestEffortFailureCount = publishedScrape.BestEffortFailureCount,
                    bestEffortFailedPhases = publishedScrape.BestEffortFailedPhases,
                },
                currentUpdate = new
                {
                    status = currentStatus,
                    scrapeId = activeScrape?.Id,
                    startedAt = currentStartedAt,
                    phase = currentPhase,
                    subOperation = currentSubOperation,
                    detail = durableCurrent?.Detail
                        ?? lastFailedOperation?.Detail
                        ?? candidateScrape?.FailureMessage,
                    updatedAt = FormatUtc(durableCurrent?.UpdatedAtUtc),
                    endedAt = currentStatus == "failed"
                        ? FormatUtc(lastFailedOperation?.EndedAtUtc)
                            ?? candidateScrape?.FailedAt
                        : null,
                    progressPercent = durableCurrent?.ProgressPercent ?? localCurrent?.ProgressPercent,
                    elapsedSeconds = currentElapsedSeconds,
                    estimatedRemainingSeconds = durableCurrent?.EstimatedRemainingSeconds ?? localCurrent?.EstimatedRemainingSeconds,
                    branches = localCurrent?.Branches,
                },
                activeScrapeId = activeScrape?.Id,
                publishedScrapeId = publishedScrape?.Id,
                publication = new
                {
                    publishedScrapeId = publishedScrape?.Id,
                    publishedAt = FormatUtc(runtime.PublishedAtUtc),
                    publicReadsFrozen = runtime.PublicReadFreeze.IsFrozen,
                    frozenAt = FormatUtc(runtime.PublicReadFreeze.FrozenAt),
                    frozenScrapeId = runtime.PublicReadFreeze.ScrapeId,
                    freezeReason = runtime.PublicReadFreeze.Reason,
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

    private static bool IsFailedScrapeOperation(WorkerOperationInfo? operation)
        => operation is not null
           && string.Equals(operation.Status, "failed", StringComparison.OrdinalIgnoreCase)
           && IsScrapeOperation(operation);

    private static bool IsScrapeOperation(WorkerOperationInfo? operation)
        => operation?.OperationKey.StartsWith("scrape.", StringComparison.OrdinalIgnoreCase) == true;

    private static string? GetFreezePhase(string? reason)
        => reason?.Trim().ToLowerInvariant() switch
        {
            "scrape" => "Scraping",
            "post-process" => "PostScrapeEnrichment",
            "publish" => "Publishing",
            _ => null,
        };

    private static string? GetNextScheduledUpdateAt(
        ServiceRuntimeState runtime,
        string currentStatus,
        string effectiveWorkerStatus,
        TimeSpan scrapeInterval,
        DateTime nowUtc)
    {
        if (currentStatus is "updating" or "stalled" || runtime.PublicReadFreeze.IsFrozen)
            return null;
        if (effectiveWorkerStatus is "offline" or "stale" or "stopping")
            return null;

        DateTime? scheduleBaseUtc = null;
        if (currentStatus == "failed"
            && IsFailedScrapeOperation(runtime.WorkerStatus?.LastOperation))
        {
            scheduleBaseUtc = runtime.WorkerStatus!.LastOperation!.EndedAtUtc
                ?? runtime.WorkerStatus.LastOperation.UpdatedAtUtc;
        }
        else if (currentStatus == "failed"
                 && runtime.LatestScrape?.FailedAt is not null
                 && DateTimeOffset.TryParse(runtime.LatestScrape.FailedAt, out var failedAt))
        {
            scheduleBaseUtc = failedAt.UtcDateTime;
        }
        else if (runtime.PublishedAtUtc is not null)
        {
            scheduleBaseUtc = runtime.PublishedAtUtc;
        }
        else if (runtime.PublishedScrape?.CompletedAt is not null
                 && DateTimeOffset.TryParse(runtime.PublishedScrape.CompletedAt, out var completedAt))
        {
            scheduleBaseUtc = completedAt.UtcDateTime;
        }

        if (scheduleBaseUtc is null)
            return null;

        var next = scheduleBaseUtc.Value.ToUniversalTime().Add(scrapeInterval);
        return next > nowUtc ? next.ToString("o") : null;
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
