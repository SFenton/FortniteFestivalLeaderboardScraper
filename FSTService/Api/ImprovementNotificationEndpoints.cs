using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapImprovementNotificationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/player/{accountId}/notifications", (
            HttpContext httpContext,
            string accountId,
            int? limit,
            bool? includeExpired,
            string? kind,
            string? instrument,
            string? songId,
            ImprovementNotificationService notifications) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=60";

            return Results.Ok(notifications.GetPlayerNotifications(
                accountId,
                limit ?? 50,
                includeExpired ?? false,
                kind,
                instrument,
                songId));
        })
        .WithTags("Notifications")
        .RequireRateLimiting("public");

        app.MapGet("/api/rankings/bands/{bandType}/{teamKey}/notifications", (
            HttpContext httpContext,
            string bandType,
            string teamKey,
            int? limit,
            bool? includeExpired,
            string? rankingScope,
            string? comboId,
            string? kind,
            ImprovementNotificationService notifications) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=60";

            if (!BandComboIds.IsValidBandType(bandType))
                return Results.NotFound(new { error = $"Unknown band type: {bandType}" });

            var normalizedCombo = NormalizeBandNotificationComboId(bandType, comboId);
            if (normalizedCombo.Error is not null)
                return normalizedCombo.Error;

            return Results.Ok(notifications.GetBandNotificationsByTeamKey(
                bandType,
                teamKey,
                limit ?? 50,
                includeExpired ?? false,
                rankingScope ?? "overall",
                normalizedCombo.ComboId,
                kind));
        })
        .WithTags("Notifications")
        .RequireRateLimiting("public");

        app.MapGet("/api/bands/{bandId}/notifications", (
            HttpContext httpContext,
            string bandId,
            int? limit,
            bool? includeExpired,
            string? rankingScope,
            string? comboId,
            string? kind,
            GlobalLeaderboardPersistence persistence,
            ImprovementNotificationService notifications) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=60";

            var band = persistence.GetBandById(bandId);
            if (band is null)
                return Results.NotFound(new { error = "Band not found." });

            var normalizedCombo = NormalizeBandNotificationComboId(band.BandType, comboId);
            if (normalizedCombo.Error is not null)
                return normalizedCombo.Error;

            return Results.Ok(notifications.GetBandNotificationsByTeamKey(
                band.BandType,
                band.TeamKey,
                limit ?? 50,
                includeExpired ?? false,
                rankingScope ?? "overall",
                normalizedCombo.ComboId,
                kind));
        })
        .WithTags("Notifications")
        .RequireRateLimiting("public");
    }

    private static (string? ComboId, IResult? Error) NormalizeBandNotificationComboId(string bandType, string? comboId)
    {
        if (string.IsNullOrWhiteSpace(comboId))
            return (null, null);

        var normalized = BandComboIds.TryNormalizeForBandType(bandType, comboId);
        if (normalized.Error is not null || string.IsNullOrWhiteSpace(normalized.ComboId))
            return (null, Results.BadRequest(new { error = normalized.Error ?? "Invalid band combo." }));

        return (normalized.ComboId, null);
    }
}