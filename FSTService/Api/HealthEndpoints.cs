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
            var lastCompletedUpdate = metaDb.GetLastCompletedScrapeRun();
            var isUpdating = current is not null;
            string? nextScheduledUpdateAt = null;

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
                nextScheduledUpdateAt,
            });
        })
        .WithTags("Health")
        .RequireRateLimiting("public");
    }
}
