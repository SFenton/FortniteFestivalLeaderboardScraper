using System.Text.Json;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapLeaderboardRivalsEndpoints(this WebApplication app)
    {
        // ─── Leaderboard rivals list per instrument ────────────

        app.MapGet("/api/player/{accountId}/leaderboard-rivals/{instrument}", (
            HttpContext httpContext,
            string accountId,
            string instrument,
            string? rankBy,
            IMetaDatabase metaDb,
            GlobalLeaderboardPersistence persistence,
            ScrapeTimePrecomputer precomputer,
            [FromKeyedServices("LeaderboardRivalsCache")] ResponseCacheService cache) =>
        {
            var effectiveRankBy = rankBy ?? "totalscore";
            // Validate rank method via whitelist
            var rankColumn = InstrumentDatabase.MapRankColumn(effectiveRankBy);
            if (rankColumn == "TotalScoreRank" && effectiveRankBy != "totalscore")
                effectiveRankBy = "totalscore"; // fallback unknown values

            httpContext.Response.Headers.CacheControl = "public, max-age=300, stale-while-revalidate=600";

            // ── Check precomputed store for default sort ──
            if (effectiveRankBy == "totalscore")
            {
                {
                    var result = CacheHelper.ServeIfCached(httpContext, precomputer.TryGet($"lb-rivals:{accountId}:{instrument}:totalscore"));
                    if (result is not null) return result;
                }
            }

            var cacheKey = $"lb-rivals:{accountId}:{instrument}:{effectiveRankBy}";
            {
                var result = CacheHelper.ServeIfCached(httpContext, cache.Get(cacheKey));
                if (result is not null) return result;
            }

            var rivals = metaDb.GetLeaderboardRivals(accountId, instrument, effectiveRankBy);
            var names = metaDb.GetDisplayNames(rivals.Select(r => r.RivalAccountId));

            // Look up user's own rank for context
            int? userRank = null;
            var db = persistence.GetOrCreateInstrumentDb(instrument);
            {
                var (_, self, _) = db.GetAccountRankingNeighborhood(accountId, 0, effectiveRankBy);
                if (self is not null)
                    userRank = InstrumentDatabase.GetRankValue(self, effectiveRankBy);
            }

            var above = rivals.Where(r => r.Direction == "above").Select(r => MapRival(r, names));
            var below = rivals.Where(r => r.Direction == "below").Select(r => MapRival(r, names));

            var payload = new
            {
                instrument,
                rankBy = effectiveRankBy,
                userRank,
                above = above.ToList(),
                below = below.ToList(),
            };

            var jsonOpts = httpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
            var etag = cache.Set(cacheKey, jsonBytes);

            httpContext.Response.Headers.ETag = etag;
            return Results.Bytes(jsonBytes, "application/json");
        })
        .WithTags("LeaderboardRivals")
        .RequireRateLimiting("public");

        // ─── Leaderboard rival detail (head-to-head) ──────────

        app.MapGet("/api/player/{accountId}/leaderboard-rivals/{instrument}/{rivalId}", (
            HttpContext httpContext,
            string accountId,
            string instrument,
            string rivalId,
            string? rankBy,
            string? sort,
            IMetaDatabase metaDb,
            GlobalLeaderboardPersistence persistence,
            FestivalService festivalService,
            RivalsCalculator rivalsCalculator,
            [FromKeyedServices("LeaderboardRivalsCache")] ResponseCacheService cache) =>
        {
            var effectiveRankBy = rankBy ?? "totalscore";
            var effectiveSort = sort ?? "closest";

            httpContext.Response.Headers.CacheControl = "public, max-age=300, stale-while-revalidate=600";

            var cacheKey = $"lb-rival-detail:{accountId}:{instrument}:{rivalId}:{effectiveRankBy}:{effectiveSort}";
            {
                var result = CacheHelper.ServeIfCached(httpContext, cache.Get(cacheKey));
                if (result is not null) return result;
            }

            var samples = metaDb.GetLeaderboardRivalSongSamples(accountId, rivalId, instrument, effectiveRankBy);
            var rivalName = metaDb.GetDisplayName(rivalId);

            // Sort based on preference
            var sortedSamples = effectiveSort switch
            {
                "they_lead" => samples.OrderBy(s => s.RankDelta).ToList(),
                "you_lead" => samples.OrderByDescending(s => s.RankDelta).ToList(),
                _ => samples.OrderBy(s => Math.Abs(s.RankDelta)).ToList(), // closest
            };

            // Get song metadata for enrichment
            var songLookup = festivalService.Songs
                .Where(s => s.track?.su is not null)
                .ToDictionary(s => s.track.su, StringComparer.OrdinalIgnoreCase);

            // Compute song gaps (reuse existing method)
            var songGaps = rivalsCalculator.ComputeSongGaps(accountId, rivalId, new[] { instrument });

            var payload = new
            {
                rival = new { accountId = rivalId, displayName = rivalName },
                instrument,
                rankBy = effectiveRankBy,
                totalSongs = sortedSamples.Count,
                sort = effectiveSort,
                songs = sortedSamples.Select(s =>
                {
                    songLookup.TryGetValue(s.SongId, out var song);
                    return new
                    {
                        songId = s.SongId,
                        title = song?.track?.tt,
                        artist = song?.track?.an,
                        instrument,
                        userRank = s.UserRank,
                        rivalRank = s.RivalRank,
                        rankDelta = s.RankDelta,
                        userScore = s.UserScore,
                        rivalScore = s.RivalScore,
                    };
                }).ToList(),
                songsToCompete = songGaps.SongsToCompete.Where(g => g.Instrument == instrument).Select(g => new
                {
                    songId = g.SongId,
                    instrument = g.Instrument,
                    score = g.Score,
                    rank = g.Rank,
                }).ToList(),
                yourExclusiveSongs = songGaps.YourExclusives.Where(g => g.Instrument == instrument).Select(g => new
                {
                    songId = g.SongId,
                    instrument = g.Instrument,
                    score = g.Score,
                    rank = g.Rank,
                }).ToList(),
            };

            var jsonOpts = httpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
            var etag = cache.Set(cacheKey, jsonBytes);

            httpContext.Response.Headers.ETag = etag;
            return Results.Bytes(jsonBytes, "application/json");
        })
        .WithTags("LeaderboardRivals")
        .RequireRateLimiting("public");
    }

    private static object MapRival(LeaderboardRivalRow r, Dictionary<string, string> names)
    {
        names.TryGetValue(r.RivalAccountId, out var name);
        return new
        {
            accountId = r.RivalAccountId,
            displayName = name,
            sharedSongCount = r.SharedSongCount,
            aheadCount = r.AheadCount,
            behindCount = r.BehindCount,
            avgSignedDelta = r.AvgSignedDelta,
            leaderboardRank = r.RivalRank,
            userLeaderboardRank = r.UserRank,
        };
    }
}
