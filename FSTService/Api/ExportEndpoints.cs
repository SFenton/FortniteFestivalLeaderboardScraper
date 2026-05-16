using FSTService.Exports;
using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapExportEndpoints(this WebApplication app)
    {
        app.MapGet("/api/player/{accountId}/export", (
            HttpContext httpContext,
            string accountId,
            IMetaDatabase metaDb,
            UserSyncProgressTracker syncTracker,
            PlayerDataExportService exportService) =>
        {
            if (string.IsNullOrWhiteSpace(accountId))
                return Results.BadRequest(new { error = "Account ID is required." });

            var normalizedAccountId = accountId.Trim();
            if (!RequestMatchesPlayerExport(httpContext, normalizedAccountId))
                return Results.Conflict(new { error = "Selected profile does not match export target." });
            if (!IsPlayerExportReady(metaDb, syncTracker, normalizedAccountId))
                return Results.Conflict(new { error = "Player sync is not complete." });

            httpContext.Response.Headers.CacheControl = "no-store";
            var timeZoneId = httpContext.Request.Headers["X-FST-Time-Zone"].FirstOrDefault();
            var export = exportService.BuildPlayerArchive(normalizedAccountId, timeZoneId);
            return Results.File(export.Content, export.ContentType, export.FileName);
        })
        .WithTags("Exports")
        .RequireRateLimiting("public");

        app.MapGet("/api/bands/{bandType}/{teamKey}/export", (
            HttpContext httpContext,
            string bandType,
            string teamKey,
            IMetaDatabase metaDb,
            PlayerDataExportService exportService) =>
        {
            if (string.IsNullOrWhiteSpace(bandType) || !BandComboIds.IsValidBandType(bandType.Trim()))
                return Results.BadRequest(new { error = "A valid band type is required." });
            if (string.IsNullOrWhiteSpace(teamKey))
                return Results.BadRequest(new { error = "Team key is required." });

            var normalizedBandType = bandType.Trim();
            var normalizedTeamKey = teamKey.Trim();
            if (!RequestMatchesBandExport(httpContext, normalizedBandType, normalizedTeamKey))
                return Results.Conflict(new { error = "Selected profile does not match export target." });
            if (!IsBandExportReady(metaDb, normalizedBandType, normalizedTeamKey))
                return Results.Conflict(new { error = "Band sync is not complete." });

            try
            {
                httpContext.Response.Headers.CacheControl = "no-store";
                var timeZoneId = httpContext.Request.Headers["X-FST-Time-Zone"].FirstOrDefault();
                var export = exportService.BuildBandArchive(normalizedBandType, normalizedTeamKey, timeZoneId);
                return Results.File(export.Content, export.ContentType, export.FileName);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "Band not found." });
            }
        })
        .WithTags("Exports")
        .RequireRateLimiting("public");
    }

    private static bool IsPlayerExportReady(IMetaDatabase metaDb, UserSyncProgressTracker syncTracker, string accountId)
    {
        if (syncTracker.GetProgress(accountId) is not null)
            return false;

        var isRegistered = metaDb.GetRegisteredAccountIds().Contains(accountId);
        if (!isRegistered)
            return false;

        var backfill = metaDb.GetBackfillStatus(accountId);
        var historyRecon = metaDb.GetHistoryReconStatus(accountId);
        var rivals = metaDb.GetRivalsStatus(accountId);
        if (backfill?.RankingsPending == true)
            return false;

        var statuses = new[]
        {
            backfill?.Status,
            historyRecon?.Status,
            rivals?.Status,
        }.Where(static status => !string.IsNullOrWhiteSpace(status)).ToArray();

        return statuses.Length > 0 && statuses.All(IsCompleteStatus);
    }

    private static bool IsBandExportReady(IMetaDatabase metaDb, string bandType, string teamKey)
    {
        var status = metaDb.GetRegisteredBandProcessingStatus(MetaDatabase.WebBandTrackerDeviceId, bandType, teamKey);
        return IsCompleteStatus(status?.Status);
    }

    private static bool RequestMatchesPlayerExport(HttpContext httpContext, string accountId)
    {
        if (!SelectedProfileHeaders.TryParse(httpContext.Request.Headers, out var selection) || selection is null)
            return true;

        return selection is SelectedPlayerSelection player
            && string.Equals(player.AccountId, accountId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequestMatchesBandExport(HttpContext httpContext, string bandType, string teamKey)
    {
        if (!SelectedProfileHeaders.TryParse(httpContext.Request.Headers, out var selection) || selection is null)
            return true;

        return selection is SelectedBandSelection band
            && string.Equals(band.BandType, bandType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(band.TeamKey, teamKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompleteStatus(string? status) =>
        string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase);
}