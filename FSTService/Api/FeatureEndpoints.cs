using Microsoft.Extensions.Options;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapFeatureEndpoints(this WebApplication app)
    {
        app.MapGet("/api/features", (IOptions<FeatureOptions> options) =>
        {
            return Results.Ok(new
            {
                appManual = options.Value.AppManual,
            });
        })
        .WithTags("Features")
        .RequireRateLimiting("public");
    }
}
