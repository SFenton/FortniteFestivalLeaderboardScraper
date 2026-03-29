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
                shop = f.Shop,
                rivals = f.Rivals,
                compete = f.Compete,
                leaderboards = f.Leaderboards,
                firstRun = f.FirstRun,
                difficulty = f.Difficulty,
            });
        })
        .WithTags("Features")
        .RequireRateLimiting("public");
    }
}
