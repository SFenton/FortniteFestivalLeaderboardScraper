using Microsoft.Extensions.Options;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapFeatureEndpoints(this WebApplication app)
    {
        app.MapGet("/api/features", (IOptions<FeatureOptions> options, IOptions<FeedbackOptions> feedbackOptions) =>
        {
            return Results.Ok(new
            {
                appManual = options.Value.AppManual,
                feedback = IsFeedbackAvailable(options.Value, feedbackOptions.Value),
            });
        })
        .WithTags("Features")
        .RequireRateLimiting("public");
    }
}
