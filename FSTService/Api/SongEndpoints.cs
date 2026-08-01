using System.Text.Json;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    internal const string AlbumArtPrefix = "https://cdn2.unrealengine.com/";

    private static string? TrimAlbumArt(string? url)
        => url is not null && url.StartsWith(AlbumArtPrefix, StringComparison.Ordinal)
            ? url[AlbumArtPrefix.Length..]
            : url;

    public static void MapSongEndpoints(this WebApplication app)
    {
        app.MapGet("/api/songs", (HttpContext httpContext, FestivalService service, IPathDataStore pathStore, IMetaDatabase metaDb, GlobalLeaderboardPersistence persistence, SongsCacheService songsCache, ScrapeTimePrecomputer precomputer, ILoggerFactory loggerFactory, [FromKeyedServices("LeaderboardAllCache")] ResponseCacheService publicationCache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=1800, stale-while-revalidate=3600";

            // ── Check cache ──────────────────────────────────────
            {
                var result = CacheHelper.ServeIfCached(httpContext, songsCache.Get());
                if (result is not null)
                {
                    httpContext.Response.ContentType = "application/json; charset=utf-8";
                    return result;
                }
            }

            var frozenMiss = CacheHelper.ServeUnavailableIfFrozen(
                httpContext,
                publicationCache);
            if (frozenMiss is not null) return frozenMiss;

            // ── Build response ───────────────────────────────────
            var jsonOpts = httpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;

            byte[] jsonBytes;
            string etag;
            try
            {
                jsonBytes = SongsCacheService.BuildSongsJson(service, pathStore, metaDb, persistence, precomputer, jsonOpts);
                etag = songsCache.Set(jsonBytes);
            }
            catch (Exception ex)
            {
                var stale = songsCache.GetStale();
                if (stale is not null)
                {
                    loggerFactory.CreateLogger("FSTService.Api.SongEndpoints")
                        .LogWarning(ex, "Failed to rebuild /api/songs; serving last cached songs response.");
                    httpContext.Response.ContentType = "application/json; charset=utf-8";
                    return CacheHelper.ServeIfCached(httpContext, stale)!;
                }

                throw;
            }

            httpContext.Response.Headers.ETag = etag;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            return Results.Bytes(jsonBytes, "application/json");
        })
        .WithTags("Songs")
        .RequireRateLimiting("public");

        app.MapGet("/api/songs/member-score-filter", (
            HttpContext httpContext,
            string? has,
            string? missing,
            string? instruments,
            double? leeway,
            GlobalLeaderboardPersistence persistence,
            [FromKeyedServices("LeaderboardAllCache")] ResponseCacheService publicationCache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300";

            var hasAccountIds = ParseCsvParameter(has, maxItems: 8);
            var missingAccountIds = ParseCsvParameter(missing, maxItems: 8);
            if (hasAccountIds.Count == 0 && missingAccountIds.Count == 0)
                return Results.BadRequest(new { error = "At least one has or missing account ID is required." });

            var requestedInstruments = ParseCsvParameter(instruments, maxItems: 16);
            if (requestedInstruments.Count == 0)
                return Results.BadRequest(new { error = "instruments is required." });

            foreach (var instrument in requestedInstruments)
            {
                if (!GlobalLeaderboardPersistence.IsValidInstrument(instrument))
                    return Results.NotFound(new { error = $"Unknown instrument: {instrument}" });
            }

            var frozenMiss = CacheHelper.ServeUnavailableIfFrozen(httpContext, publicationCache);
            if (frozenMiss is not null) return frozenMiss;

            var songIds = persistence.GetCurrentStateSongIdsForMemberScoreFilter(
                hasAccountIds,
                missingAccountIds,
                requestedInstruments,
                leeway);

            return Results.Ok(new
            {
                count = songIds.Count,
                songIds,
                hasAccountIds,
                missingAccountIds,
                instruments = requestedInstruments,
            });
        })
        .WithTags("Songs")
        .RequireRateLimiting("public");

        // ── Item Shop (enriched song objects) ───────────────────
        app.MapGet("/api/shop", (HttpContext httpContext, ShopCacheService shopCache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300, stale-while-revalidate=600";

            var cached = shopCache.Get();
            if (cached is null)
                return Results.Ok(new { count = 0, songs = Array.Empty<object>(), lastUpdated = (string?)null });

            httpContext.Response.ContentType = "application/json; charset=utf-8";
            return CacheHelper.ServeIfCached(httpContext, cached)!;
        })
        .WithTags("Shop")
        .RequireRateLimiting("public");

        // ── Path images ─────────────────────────────────────────
        app.MapGet("/api/paths/{songId}/{instrument}/{difficulty}", (
            string songId,
            string instrument,
            string difficulty,
            string? generationId,
            PathArtifactResolver resolver) =>
            GetPathArtifactResult(
                songId,
                instrument,
                difficulty,
                "png",
                generationId,
                resolver))
        .WithTags("Paths")
        .RequireRateLimiting("public");

        // ── Path JSON data (structured activation/score/OD data per difficulty) ─
        app.MapGet("/api/paths/{songId}/{instrument}/{difficulty}/data", (
            string songId,
            string instrument,
            string difficulty,
            string? generationId,
            PathArtifactResolver resolver) =>
            GetPathArtifactResult(
                songId,
                instrument,
                difficulty,
                "json",
                generationId,
                resolver))
        .WithTags("Paths")
        .RequireRateLimiting("public");
    }

    internal static IResult GetPathArtifactResult(
        string songId,
        string instrument,
        string difficulty,
        string extension,
        string? generationId,
        PathArtifactResolver resolver)
    {
        if (!PathGenerationInstruments.Definitions.Any(
                definition => definition.Instrument == instrument))
        {
            return Results.BadRequest(new { error = "Invalid instrument name." });
        }

        if (!PathGenerationInstruments.Difficulties.Contains(
                difficulty,
                StringComparer.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new
            {
                error = "Invalid difficulty. Use easy, medium, hard, or expert.",
            });
        }

        var artifact = resolver.Resolve(
            songId,
            instrument,
            difficulty,
            extension,
            generationId);
        if (artifact is null)
            return Results.BadRequest(new { error = "Invalid path." });
        if (!File.Exists(artifact.FilePath))
        {
            return Results.NotFound(new
            {
                error = extension == "png"
                    ? "Path image not yet generated for this song/instrument/difficulty."
                    : "Path data not yet generated for this song/instrument/difficulty.",
            });
        }

        return Results.File(
            artifact.FilePath,
            extension == "png" ? "image/png" : "application/json");
    }
}
