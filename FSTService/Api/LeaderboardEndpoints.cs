using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapLeaderboardEndpoints(this WebApplication app)
    {
        app.MapGet("/api/leaderboard/{songId}/{instrument}", (
            HttpContext httpContext,
            string songId,
            string instrument,
            int? top,
            int? offset,
            double? leeway,
            GlobalLeaderboardPersistence persistence,
            MetaDatabase metaDb,
            PathDataStore pathStore) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300";
            int? maxScore = null;
            if (leeway.HasValue)
            {
                var allMax = pathStore.GetAllMaxScores();
                if (allMax.TryGetValue(songId, out var ms))
                {
                    var raw = ms.GetByInstrument(instrument);
                    if (raw.HasValue)
                        maxScore = (int)(raw.Value * (1.0 + leeway.Value / 100.0));
                }
            }
            var result = persistence.GetLeaderboardWithCount(songId, instrument, top, offset ?? 0, maxScore);
            if (result is null)
                return Results.NotFound(new { error = $"Unknown instrument: {instrument}" });

            var (entries, dbCount) = result.Value;
            var pop = metaDb.GetLeaderboardPopulation(songId, instrument);
            var totalEntries = Math.Max(pop > 0 ? (int)pop : 0, dbCount);
            var names = metaDb.GetDisplayNames(entries.Select(e => e.AccountId));
            var enriched = entries.Select(e => new
            {
                e.AccountId,
                DisplayName = names.GetValueOrDefault(e.AccountId),
                e.Score,
                Rank = e.ApiRank > 0 ? e.ApiRank : e.Rank,
                e.Accuracy,
                e.IsFullCombo,
                e.Stars,
                e.Difficulty,
                e.Season,
                e.Percentile,
                e.EndTime,
                e.Source,
            }).ToList();

            return Results.Ok(new
            {
                songId,
                instrument,
                count = enriched.Count,
                totalEntries,
                localEntries = dbCount,
                entries = enriched
            });
        })
        .WithTags("Leaderboards")
        .RequireRateLimiting("public");

        app.MapGet("/api/leaderboard/{songId}/all", (
            HttpContext httpContext,
            string songId,
            int? top,
            double? leeway,
            GlobalLeaderboardPersistence persistence,
            MetaDatabase metaDb,
            PathDataStore pathStore,
            [FromKeyedServices("LeaderboardAllCache")] ResponseCacheService lbCache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300, stale-while-revalidate=600";

            // ── Check cache ──────────────────────────────────────
            var cacheKey = $"lb:{songId}:{top}:{leeway}";
            var cached = lbCache.Get(cacheKey);
            if (cached is not null)
            {
                var requestETag = httpContext.Request.Headers.IfNoneMatch.ToString();
                if (!string.IsNullOrEmpty(requestETag) && requestETag == cached.Value.ETag)
                {
                    httpContext.Response.Headers.ETag = cached.Value.ETag;
                    return Results.StatusCode(304);
                }
                httpContext.Response.Headers.ETag = cached.Value.ETag;
                return Results.Bytes(cached.Value.Json, "application/json");
            }

            // ── Build response ───────────────────────────────────
            var instrumentKeys = persistence.GetInstrumentKeys();
            var population = metaDb.GetAllLeaderboardPopulation();
            var allAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, SongMaxScores>? maxScoresMap = leeway.HasValue
                ? pathStore.GetAllMaxScores()
                : null;

            // Collect raw data per instrument
            var rawInstruments = new List<(string Instrument, List<LeaderboardEntryDto> Entries, int DbCount, int TotalEntries)>();
            foreach (var instrument in instrumentKeys)
            {
                int? maxScore = null;
                if (maxScoresMap is not null && maxScoresMap.TryGetValue(songId, out var ms))
                {
                    var raw = ms.GetByInstrument(instrument);
                    if (raw.HasValue)
                        maxScore = (int)(raw.Value * (1.0 + leeway!.Value / 100.0));
                }
                var result = persistence.GetLeaderboardWithCount(songId, instrument, top ?? 10, maxScore: maxScore);
                if (result is null) continue;

                var (entries, dbCount) = result.Value;
                var popKey = (songId, instrument);
                var totalEntries = Math.Max(
                    population.TryGetValue(popKey, out var pop) && pop > 0 ? (int)pop : 0,
                    dbCount);

                foreach (var e in entries)
                    allAccountIds.Add(e.AccountId);

                rawInstruments.Add((instrument, entries, dbCount, totalEntries));
            }

            // Single bulk name lookup
            var names = metaDb.GetDisplayNames(allAccountIds);

            var instruments = rawInstruments.Select(ri => new
            {
                instrument = ri.Instrument,
                count = ri.Entries.Count,
                totalEntries = ri.TotalEntries,
                localEntries = ri.DbCount,
                entries = ri.Entries.Select(e => new
                {
                    e.AccountId,
                    DisplayName = names.GetValueOrDefault(e.AccountId),
                    e.Score,
                    e.Rank,
                    e.Accuracy,
                    e.IsFullCombo,
                    e.Stars,
                    e.Difficulty,
                    e.Season,
                    e.Percentile,
                    e.EndTime,
                }).ToList(),
            }).ToList();

            var payload = new
            {
                songId,
                instruments,
            };
            var jsonOpts = httpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
            var etag = lbCache.Set(cacheKey, jsonBytes);

            httpContext.Response.Headers.ETag = etag;
            return Results.Bytes(jsonBytes, "application/json");
        })
        .WithTags("Leaderboards")
        .RequireRateLimiting("public");
    }
}
