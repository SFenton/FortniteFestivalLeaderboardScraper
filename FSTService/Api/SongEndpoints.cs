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
        app.MapGet("/api/songs", (HttpContext httpContext, FestivalService service, IPathDataStore pathStore, IMetaDatabase metaDb, SongsCacheService songsCache, ScrapeTimePrecomputer precomputer) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=1800, stale-while-revalidate=3600";

            // ── Check cache ──────────────────────────────────────
            var cached = songsCache.Get();
            if (cached is not null)
            {
                var requestETag = httpContext.Request.Headers.IfNoneMatch.ToString();
                if (!string.IsNullOrEmpty(requestETag) && requestETag == cached.Value.ETag)
                {
                    httpContext.Response.Headers.ETag = cached.Value.ETag;
                    return Results.StatusCode(304);
                }

                httpContext.Response.Headers.ETag = cached.Value.ETag;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                return Results.Bytes(cached.Value.Json, "application/json");
            }

            // ── Build response ───────────────────────────────────
            var jsonOpts = httpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;
            var jsonBytes = SongsCacheService.BuildSongsJson(service, pathStore, metaDb, precomputer, jsonOpts);
            var etag = songsCache.Set(jsonBytes);

            httpContext.Response.Headers.ETag = etag;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            return Results.Bytes(jsonBytes, "application/json");
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

            var requestETag = httpContext.Request.Headers.IfNoneMatch.ToString();
            if (!string.IsNullOrEmpty(requestETag) && requestETag == cached.Value.ETag)
            {
                httpContext.Response.Headers.ETag = cached.Value.ETag;
                return Results.StatusCode(304);
            }

            httpContext.Response.Headers.ETag = cached.Value.ETag;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            return Results.Bytes(cached.Value.Json, "application/json");
        })
        .WithTags("Shop")
        .RequireRateLimiting("public");

        // ── Path images ─────────────────────────────────────────
        app.MapGet("/api/paths/{songId}/{instrument}/{difficulty}", (
            string songId,
            string instrument,
            string difficulty,
            IOptions<ScraperOptions> options) =>
        {
            // Validate instrument name to prevent path traversal
            var allowedInstruments = new HashSet<string>(StringComparer.Ordinal)
            {
                "Solo_Guitar", "Solo_Bass", "Solo_Drums",
                "Solo_Vocals", "Solo_PeripheralGuitar", "Solo_PeripheralBass"
            };
            if (!allowedInstruments.Contains(instrument))
                return Results.BadRequest(new { error = "Invalid instrument name." });

            var allowedDifficulties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "easy", "medium", "hard", "expert"
            };
            if (!allowedDifficulties.Contains(difficulty))
                return Results.BadRequest(new { error = "Invalid difficulty. Use easy, medium, hard, or expert." });

            var dataDir = Path.GetFullPath(options.Value.DataDirectory);
            var imagePath = Path.Combine(dataDir, "paths", songId, instrument, $"{difficulty.ToLowerInvariant()}.png");

            // Ensure the resolved path is still within the data directory
            if (!Path.GetFullPath(imagePath).StartsWith(dataDir, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Invalid path." });

            if (!File.Exists(imagePath))
                return Results.NotFound(new { error = "Path image not yet generated for this song/instrument/difficulty." });

            return Results.File(imagePath, "image/png");
        })
        .WithTags("Paths")
        .RequireRateLimiting("public");
    }
}
