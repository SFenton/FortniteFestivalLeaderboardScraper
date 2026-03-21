using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapSongEndpoints(this WebApplication app)
    {
        app.MapGet("/api/songs", (FestivalService service, PathDataStore pathStore, MetaDatabase metaDb, ItemShopService shopService) =>
        {
            var maxScoresMap = pathStore.GetAllMaxScores();
            var currentSeason = metaDb.GetCurrentSeason();
            var inShop = shopService.InShopSongIds;
            var songs = service.Songs
                .Where(s => s.track?.su is not null)
                .Select(s =>
                {
                    maxScoresMap.TryGetValue(s.track.su, out var ms);
                    return new
                    {
                        songId     = s.track.su,
                        title      = s.track.tt,
                        artist     = s.track.an,
                        album      = s.track.ab,
                        year       = s.track.ry,
                        tempo      = s.track.mt,
                        albumArt   = s.track.au,
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
                        pathsGeneratedAt = ms?.GeneratedAt,
                        shopUrl = inShop.Contains(s.track.su)
                            ? ShopUrlHelper.ComputeShopUrl(s.track.su, s.track.tt)
                            : null,
                    };
                })
                .ToList();

            return Results.Ok(new { count = songs.Count, currentSeason, songs });
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
