using Microsoft.Extensions.Options;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapFeatureEndpoints(this WebApplication app)
    {
        app.MapGet("/api/features", (IOptions<FeatureOptions> opts) =>
        {
            var f = opts.Value;
            return Results.Ok(new
            {
                compete = f.Compete,
                leaderboards = f.Leaderboards,
                difficulty = f.Difficulty,
                playerBands = f.PlayerBands,
            });
        })
        .WithTags("Features")
        .RequireRateLimiting("public");
    }
}
