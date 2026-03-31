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
        app.MapGet("/api/songs", (HttpContext httpContext, FestivalService service, IPathDataStore pathStore, IMetaDatabase metaDb, ItemShopService shopService, SongsCacheService songsCache, ScrapeTimePrecomputer precomputer) =>
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
            var maxScoresMap = pathStore.GetAllMaxScores();
            var currentSeason = metaDb.GetCurrentSeason();
            var inShop = shopService.InShopSongIds;
            var leavingTomorrow = shopService.LeavingTomorrowSongIds;
            var popTiers = precomputer.GetPopulationTiers();
            var songs = service.Songs
                .Where(s => s.track?.su is not null)
                .Select(s =>
                {
                    maxScoresMap.TryGetValue(s.track.su, out var ms);
                    var isInShop = inShop.Contains(s.track.su);

                    // Build population tiers per instrument (if precomputed)
                    Dictionary<string, PopulationTierData>? songPopTiers = null;
                    if (popTiers is not null)
                    {
                        songPopTiers = new Dictionary<string, PopulationTierData>(StringComparer.OrdinalIgnoreCase);
                        foreach (var inst in new[] { "Solo_Guitar", "Solo_Bass", "Solo_Drums", "Solo_Vocals", "Solo_PeripheralGuitar", "Solo_PeripheralBass" })
                        {
                            if (popTiers.TryGetValue((s.track.su, inst), out var pt))
                                songPopTiers[inst] = pt;
                        }
                        if (songPopTiers.Count == 0) songPopTiers = null;
                    }

                    return new
                    {
                        songId     = s.track.su,
                        title      = s.track.tt,
                        artist     = s.track.an,
                        album      = s.track.ab,
                        year       = s.track.ry,
                        tempo      = s.track.mt,
                        albumArt   = TrimAlbumArt(s.track.au),
                        genres     = s.track.ge,
                        difficulty = s.track.@in is null ? null : new
                        {
                            guitar     = s.track.@in.gr,
                            bass       = s.track.@in.ba,
                            vocals     = s.track.@in.vl,
                            drums      = s.track.@in.ds,
                            proGuitar  = s.track.@in.pg,
                            proBass    = s.track.@in.pb,
                        },
                        maxScores = ms is null ? null : new
                        {
                            Solo_Guitar           = ms.MaxLeadScore,
                            Solo_Bass             = ms.MaxBassScore,
                            Solo_Drums            = ms.MaxDrumsScore,
                            Solo_Vocals           = ms.MaxVocalsScore,
                            Solo_PeripheralGuitar = ms.MaxProLeadScore,
                            Solo_PeripheralBass   = ms.MaxProBassScore,
                        },
                        populationTiers = songPopTiers,
                        pathsGeneratedAt = ms?.GeneratedAt,
                        shopUrl = isInShop
                            ? ShopUrlHelper.ComputeShopUrl(s.track.su, s.track.tt)
                            : null,
                        leavingTomorrow = isInShop && leavingTomorrow.Contains(s.track.su),
                    };
                })
                .ToList();

            var payload = new { count = songs.Count, currentSeason, songs };
            var jsonOpts = httpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
            var etag = songsCache.Set(jsonBytes);

            httpContext.Response.Headers.ETag = etag;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            return Results.Bytes(jsonBytes, "application/json");
        })
        .WithTags("Songs")
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
