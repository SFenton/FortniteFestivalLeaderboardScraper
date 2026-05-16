using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapBandSyncEndpoints(this WebApplication app)
    {
        app.MapGet("/api/bands/{bandType}/{teamKey}/sync-status", (
            HttpContext httpContext,
            string bandType,
            string teamKey,
            IMetaDatabase metaDb) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=5";

            if (string.IsNullOrWhiteSpace(bandType) || !BandComboIds.IsValidBandType(bandType.Trim()))
                return Results.BadRequest(new { error = "A valid band type is required." });
            if (string.IsNullOrWhiteSpace(teamKey))
                return Results.BadRequest(new { error = "Team key is required." });

            var normalizedBandType = bandType.Trim();
            var normalizedTeamKey = teamKey.Trim();
            var canonicalBandId = BandIdentity.CreateBandId(normalizedBandType, normalizedTeamKey);
            var registration = metaDb.RegisterSelectedBandActivity(normalizedBandType, normalizedTeamKey);
            var status = metaDb.GetRegisteredBandProcessingStatus(MetaDatabase.WebBandTrackerDeviceId, normalizedBandType, normalizedTeamKey);

            return Results.Ok(new
            {
                bandId = string.IsNullOrWhiteSpace(registration.BandId) ? canonicalBandId : registration.BandId,
                bandType = normalizedBandType,
                teamKey = normalizedTeamKey,
                isTracked = registration.Registered || status is not null,
                processing = status is null ? null : new
                {
                    status = status.Status,
                    lookupsChecked = status.LookupsChecked,
                    totalLookupsToCheck = status.TotalLookupsToCheck,
                    entriesFound = status.EntriesFound,
                    startedAt = status.StartedAt,
                    completedAt = status.CompletedAt,
                    lastResumedAt = status.LastResumedAt,
                },
            });
        })
        .WithTags("Bands")
        .RequireRateLimiting("public");
    }
}