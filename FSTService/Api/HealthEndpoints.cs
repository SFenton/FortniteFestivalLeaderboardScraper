using System.Reflection;
using FSTService.Scraping;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok("ok"))
           .WithTags("Health")
           .RequireRateLimiting("public");

        app.MapGet("/api/version", () =>
        {
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
    }
}
