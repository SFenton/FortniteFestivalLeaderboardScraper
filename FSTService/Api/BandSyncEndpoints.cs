using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapBandSyncEndpoints(this WebApplication app)
    {
        app.MapGet("/api/bands/{bandType}/{teamKey}/sync-status", async (
            HttpContext httpContext,
            string bandType,
            string teamKey,
            IMetaDatabase metaDb,
            RegistrationMutationCoordinator registrationMutations,
            CancellationToken ct) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=5";

            if (string.IsNullOrWhiteSpace(bandType) || !BandComboIds.IsValidBandType(bandType.Trim()))
                return Results.BadRequest(new { error = "A valid band type is required." });
            if (string.IsNullOrWhiteSpace(teamKey))
                return Results.BadRequest(new { error = "Team key is required." });

            var normalizedBandType = bandType.Trim();
            var normalizedTeamKey = teamKey.Trim();
            var canonicalBandId = BandIdentity.CreateBandId(normalizedBandType, normalizedTeamKey);
            try
            {
                await using var registrationLease =
                    await registrationMutations
                        .TryAcquireWriteLeaseAsync(ct);
                await registrationLease.VerifyHeldAsync(ct);
                var registration =
                    metaDb.RegisterSelectedBandActivity(
                        normalizedBandType,
                        normalizedTeamKey);
                var status =
                    metaDb.GetRegisteredBandProcessingStatus(
                        MetaDatabase.WebBandTrackerDeviceId,
                        normalizedBandType,
                        normalizedTeamKey);

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
            }
            catch (RegistrationMutationBlockedException ex)
            {
                httpContext.Response.Headers.CacheControl =
                    "no-store";
                httpContext.Response.Headers["Retry-After"] =
                    "30";
                return Results.Problem(
                    title: "Registration temporarily unavailable",
                    detail: ex.Message,
                    statusCode:
                        StatusCodes.Status503ServiceUnavailable);
            }
            catch (Npgsql.PostgresException ex)
                when (RegistrationMutationGate
                    .IsDatabaseFenceRejection(ex))
            {
                httpContext.Response.Headers.CacheControl =
                    "no-store";
                httpContext.Response.Headers["Retry-After"] =
                    "30";
                return Results.Problem(
                    title: "Registration temporarily unavailable",
                    detail: new RegistrationMutationBlockedException()
                        .Message,
                    statusCode:
                        StatusCodes.Status503ServiceUnavailable);
            }
        })
        .WithTags("Bands")
        .RequireRateLimiting("public");
    }
}