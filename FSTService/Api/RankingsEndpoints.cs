using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapRankingsEndpoints(this WebApplication app)
    {
        // ─── Per-instrument rankings (paginated) ───────────────

        app.MapGet("/api/rankings/{instrument}", (
            HttpContext httpContext,
            string instrument,
            string? rankBy,
            int? page,
            int? pageSize,
            GlobalLeaderboardPersistence persistence,
            IMetaDatabase metaDb,
            ScrapeTimePrecomputer precomputer) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=1800, stale-while-revalidate=3600";

            var effectivePage = page ?? 1;
            var effectivePageSize = Math.Clamp(pageSize ?? 50, 1, 200);
            var metric = rankBy ?? "adjusted";

            // ── Check precomputed store for page 1 with default size ──
            if (effectivePage == 1 && effectivePageSize == 50)
            {
                {
                    var result = CacheHelper.ServeIfCached(httpContext, precomputer.TryGet($"rankings:{instrument}:{metric}:1:50"));
                    if (result is not null) return result;
                }
            }

            var db = persistence.GetOrCreateInstrumentDb(instrument);
            var (entries, total) = db.GetAccountRankings(metric, effectivePage, effectivePageSize);

            // Bulk resolve display names (single DB call)
            var names = metaDb.GetDisplayNames(entries.Select(e => e.AccountId));

            var enriched = entries.Select(e => new
            {
                e.AccountId,
                displayName = names.GetValueOrDefault(e.AccountId),
                e.SongsPlayed,
                e.TotalChartedSongs,
                e.Coverage,
                e.RawSkillRating,
                e.AdjustedSkillRating,
                e.AdjustedSkillRank,
                e.WeightedRating,
                e.WeightedRank,
                e.FcRate,
                e.FcRateRank,
                e.TotalScore,
                e.TotalScoreRank,
                e.MaxScorePercent,
                e.MaxScorePercentRank,
                e.AvgAccuracy,
                e.FullComboCount,
                e.AvgStars,
                e.BestRank,
                e.AvgRank,
                e.RawMaxScorePercent,
                e.ComputedAt,
            }).ToList();

            return Results.Ok(new
            {
                instrument,
                rankBy = metric,
                page = effectivePage,
                pageSize = effectivePageSize,
                totalAccounts = total,
                entries = enriched,
            });
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");

        // ─── Single account per-instrument ranking ─────────────

        app.MapGet("/api/rankings/{instrument}/{accountId}", (
            HttpContext httpContext,
            string instrument,
            string accountId,
            GlobalLeaderboardPersistence persistence,
            IMetaDatabase metaDb) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300";
            var db = persistence.GetOrCreateInstrumentDb(instrument);
            var ranking = db.GetAccountRanking(accountId);
            if (ranking is null)
                return Results.NotFound(new { error = "Account not found in rankings for this instrument." });

            var totalRanked = db.GetRankedAccountCount();

            return Results.Ok(new
            {
                ranking.AccountId,
                displayName = metaDb.GetDisplayName(accountId),
                ranking.Instrument,
                ranking.SongsPlayed,
                ranking.TotalChartedSongs,
                ranking.Coverage,
                ranking.RawSkillRating,
                ranking.AdjustedSkillRating,
                ranking.AdjustedSkillRank,
                ranking.WeightedRating,
                ranking.WeightedRank,
                ranking.FcRate,
                ranking.FcRateRank,
                ranking.TotalScore,
                ranking.TotalScoreRank,
                ranking.MaxScorePercent,
                ranking.MaxScorePercentRank,
                ranking.AvgAccuracy,
                ranking.FullComboCount,
                ranking.AvgStars,
                ranking.BestRank,
                ranking.AvgRank,
                ranking.RawMaxScorePercent,
                ranking.ComputedAt,
                totalRankedAccounts = totalRanked,
            });
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");

        // ─── Rank history for a player on an instrument ────────

        app.MapGet("/api/rankings/{instrument}/{accountId}/history", (
            HttpContext httpContext,
            string instrument,
            string accountId,
            int? days,
            GlobalLeaderboardPersistence persistence) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300";
            var db = persistence.GetOrCreateInstrumentDb(instrument);
            var history = db.GetRankHistory(accountId, days ?? 30);
            return Results.Ok(new { instrument, accountId, history });
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");

        // ─── Composite rankings (paginated) ────────────────────

        app.MapGet("/api/rankings/composite", (
            HttpContext httpContext,
            int? page,
            int? pageSize,
            IMetaDatabase metaDb,
            ScrapeTimePrecomputer precomputer) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=1800, stale-while-revalidate=3600";

            var effectivePage = page ?? 1;
            var effectivePageSize = Math.Clamp(pageSize ?? 50, 1, 200);

            // ── Check precomputed store for page 1 default size ──
            if (effectivePage == 1 && effectivePageSize == 50)
            {
                // Composite rankings are metric-agnostic; use adjusted as canonical key
                {
                    var result = CacheHelper.ServeIfCached(httpContext, precomputer.TryGet("rankings:composite:adjusted:1:50"));
                    if (result is not null) return result;
                }
            }

            var (entries, total) = metaDb.GetCompositeRankings(effectivePage, effectivePageSize);

            var names = metaDb.GetDisplayNames(entries.Select(e => e.AccountId));

            var enriched = entries.Select(e => new
            {
                e.AccountId,
                displayName = names.GetValueOrDefault(e.AccountId),
                e.InstrumentsPlayed,
                e.TotalSongsPlayed,
                e.CompositeRating,
                e.CompositeRank,
                instruments = new
                {
                    guitar = e.GuitarAdjustedSkill.HasValue ? new { skill = e.GuitarAdjustedSkill, rank = e.GuitarSkillRank } : null,
                    bass = e.BassAdjustedSkill.HasValue ? new { skill = e.BassAdjustedSkill, rank = e.BassSkillRank } : null,
                    drums = e.DrumsAdjustedSkill.HasValue ? new { skill = e.DrumsAdjustedSkill, rank = e.DrumsSkillRank } : null,
                    vocals = e.VocalsAdjustedSkill.HasValue ? new { skill = e.VocalsAdjustedSkill, rank = e.VocalsSkillRank } : null,
                    proGuitar = e.ProGuitarAdjustedSkill.HasValue ? new { skill = e.ProGuitarAdjustedSkill, rank = e.ProGuitarSkillRank } : null,
                    proBass = e.ProBassAdjustedSkill.HasValue ? new { skill = e.ProBassAdjustedSkill, rank = e.ProBassSkillRank } : null,
                },
                e.ComputedAt,
            }).ToList();

            return Results.Ok(new
            {
                page = effectivePage,
                pageSize = effectivePageSize,
                totalAccounts = total,
                entries = enriched,
            });
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");

        // ─── Single account composite ranking ──────────────────

        app.MapGet("/api/rankings/composite/{accountId}", (
            HttpContext httpContext,
            string accountId,
            IMetaDatabase metaDb) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300";
            var ranking = metaDb.GetCompositeRanking(accountId);
            if (ranking is null)
                return Results.NotFound(new { error = "Account not found in composite rankings." });

            return Results.Ok(new
            {
                ranking.AccountId,
                displayName = metaDb.GetDisplayName(accountId),
                ranking.InstrumentsPlayed,
                ranking.TotalSongsPlayed,
                ranking.CompositeRating,
                ranking.CompositeRank,
                instruments = new
                {
                    guitar = ranking.GuitarAdjustedSkill.HasValue ? new { skill = ranking.GuitarAdjustedSkill, rank = ranking.GuitarSkillRank } : null,
                    bass = ranking.BassAdjustedSkill.HasValue ? new { skill = ranking.BassAdjustedSkill, rank = ranking.BassSkillRank } : null,
                    drums = ranking.DrumsAdjustedSkill.HasValue ? new { skill = ranking.DrumsAdjustedSkill, rank = ranking.DrumsSkillRank } : null,
                    vocals = ranking.VocalsAdjustedSkill.HasValue ? new { skill = ranking.VocalsAdjustedSkill, rank = ranking.VocalsSkillRank } : null,
                    proGuitar = ranking.ProGuitarAdjustedSkill.HasValue ? new { skill = ranking.ProGuitarAdjustedSkill, rank = ranking.ProGuitarSkillRank } : null,
                    proBass = ranking.ProBassAdjustedSkill.HasValue ? new { skill = ranking.ProBassAdjustedSkill, rank = ranking.ProBassSkillRank } : null,
                },
                ranking.ComputedAt,
            });
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");

        // ─── Combo leaderboard (paginated) ─────────────────────

        app.MapGet("/api/rankings/combo", (
            HttpContext httpContext,
            string? combo,
            string? instruments,
            string? rankBy,
            int? page,
            int? pageSize,
            IMetaDatabase metaDb) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=1800, stale-while-revalidate=3600";
            var comboId = ComboIds.NormalizeComboParam(combo ?? instruments);
            if (string.IsNullOrEmpty(comboId))
                return Results.BadRequest(new { error = "At least two instruments required. Use 'combo' (hex ID) or 'instruments' (e.g. Solo_Guitar+Solo_Bass)." });

            var metric = rankBy ?? "adjusted";
            var (entries, totalAccounts) = metaDb.GetComboLeaderboard(
                comboId, metric, page ?? 1, Math.Clamp(pageSize ?? 50, 1, 200));

            var entryList = entries.ToList();
            var names = metaDb.GetDisplayNames(entryList.Select(e => e.AccountId));
            var enriched = entryList.Select(e => new
            {
                e.Rank,
                e.AccountId,
                displayName = names.GetValueOrDefault(e.AccountId),
                e.AdjustedRating,
                e.WeightedRating,
                e.FcRate,
                e.TotalScore,
                e.MaxScorePercent,
                e.SongsPlayed,
                e.FullComboCount,
                e.ComputedAt,
            }).ToList();

            return Results.Ok(new
            {
                comboId,
                rankBy = metric,
                page = page ?? 1,
                pageSize = Math.Clamp(pageSize ?? 50, 1, 200),
                totalAccounts,
                entries = enriched,
            });
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");

        // ─── Single account combo rank ─────────────────────────

        app.MapGet("/api/rankings/combo/{accountId}", (
            HttpContext httpContext,
            string accountId,
            string? combo,
            string? instruments,
            string? rankBy,
            IMetaDatabase metaDb) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300";
            var comboId = ComboIds.NormalizeComboParam(combo ?? instruments);
            if (string.IsNullOrEmpty(comboId))
                return Results.BadRequest(new { error = "At least two instruments required. Use 'combo' (hex ID) or 'instruments' (e.g. Solo_Guitar+Solo_Bass)." });

            var metric = rankBy ?? "adjusted";
            var entry = metaDb.GetComboRank(comboId, accountId, metric);
            if (entry is null)
                return Results.NotFound(new { error = "Account not found in this combo ranking." });

            var totalAccounts = metaDb.GetComboTotalAccounts(comboId);

            return Results.Ok(new
            {
                comboId,
                rankBy = metric,
                entry.Rank,
                entry.AccountId,
                displayName = metaDb.GetDisplayName(accountId),
                entry.AdjustedRating,
                entry.WeightedRating,
                entry.FcRate,
                entry.TotalScore,
                entry.MaxScorePercent,
                entry.SongsPlayed,
                entry.FullComboCount,
                totalAccounts,
                entry.ComputedAt,
            });
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");

        // ─── Per-instrument ranking neighborhood ───────────────

        app.MapGet("/api/rankings/{instrument}/{accountId}/neighborhood", (
            HttpContext httpContext,
            string instrument,
            string accountId,
            int? radius,
            GlobalLeaderboardPersistence persistence,
            IMetaDatabase metaDb,
            ScrapeTimePrecomputer precomputer,
            [FromKeyedServices("NeighborhoodCache")] ResponseCacheService cache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300, stale-while-revalidate=600";

            var effectiveRadius = Math.Clamp(radius ?? 5, 1, 25);

            // ── Check precomputed store for default radius ──
            if (effectiveRadius == 5)
            {
                {
                    var result = CacheHelper.ServeIfCached(httpContext, precomputer.TryGet($"neighborhood:{instrument}:{accountId}:5"));
                    if (result is not null) return result;
                }
            }

            var cacheKey = $"neighborhood:{instrument}:{accountId}:{effectiveRadius}";

            {
                var result = CacheHelper.ServeIfCached(httpContext, cache.Get(cacheKey));
                if (result is not null) return result;
            }

            var db = persistence.GetOrCreateInstrumentDb(instrument);
            var (above, self, below) = db.GetAccountRankingNeighborhood(accountId, effectiveRadius);

            if (self is null)
                return Results.NotFound(new { error = "Account not found in rankings for this instrument." });

            var allIds = above.Select(e => e.AccountId)
                .Append(self.AccountId)
                .Concat(below.Select(e => e.AccountId));
            var names = metaDb.GetDisplayNames(allIds);

            object Map(AccountRankingDto e) => new
            {
                e.AccountId,
                displayName = names.GetValueOrDefault(e.AccountId),
                e.TotalScore,
                e.TotalScoreRank,
                e.SongsPlayed,
                e.TotalChartedSongs,
                e.Coverage,
                e.AdjustedSkillRating,
                e.AdjustedSkillRank,
            };

            var payload = new
            {
                instrument,
                accountId,
                rank = self.TotalScoreRank,
                above = above.Select(Map).ToList(),
                self = Map(self),
                below = below.Select(Map).ToList(),
            };

            var jsonOpts = httpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
            var etag = cache.Set(cacheKey, jsonBytes);

            httpContext.Response.Headers.ETag = etag;
            return Results.Bytes(jsonBytes, "application/json");
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");

        // ─── Composite ranking neighborhood ────────────────────

        app.MapGet("/api/rankings/composite/{accountId}/neighborhood", (
            HttpContext httpContext,
            string accountId,
            int? radius,
            IMetaDatabase metaDb,
            ScrapeTimePrecomputer precomputer,
            [FromKeyedServices("NeighborhoodCache")] ResponseCacheService cache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300, stale-while-revalidate=600";

            var effectiveRadius = Math.Clamp(radius ?? 5, 1, 25);

            // ── Check precomputed store for default radius ──
            if (effectiveRadius == 5)
            {
                {
                    var result = CacheHelper.ServeIfCached(httpContext, precomputer.TryGet($"neighborhood:composite:{accountId}:5"));
                    if (result is not null) return result;
                }
            }

            var cacheKey = $"neighborhood:composite:{accountId}:{effectiveRadius}";

            {
                var result = CacheHelper.ServeIfCached(httpContext, cache.Get(cacheKey));
                if (result is not null) return result;
            }

            var (above, self, below) = metaDb.GetCompositeRankingNeighborhood(accountId, effectiveRadius);

            if (self is null)
                return Results.NotFound(new { error = "Account not found in composite rankings." });

            var allIds = above.Select(e => e.AccountId)
                .Append(self.AccountId)
                .Concat(below.Select(e => e.AccountId));
            var names = metaDb.GetDisplayNames(allIds);

            object Map(CompositeRankingDto e) => new
            {
                e.AccountId,
                displayName = names.GetValueOrDefault(e.AccountId),
                e.CompositeRating,
                e.CompositeRank,
                e.InstrumentsPlayed,
                e.TotalSongsPlayed,
            };

            var payload = new
            {
                accountId,
                rank = self.CompositeRank,
                above = above.Select(Map).ToList(),
                self = Map(self),
                below = below.Select(Map).ToList(),
            };

            var jsonOpts = httpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
            var etag = cache.Set(cacheKey, jsonBytes);

            httpContext.Response.Headers.ETag = etag;
            return Results.Bytes(jsonBytes, "application/json");
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");

        // ─── Batch rankings overview (all instruments in one call) ──

        app.MapGet("/api/rankings/overview", (
            HttpContext httpContext,
            string? rankBy,
            int? pageSize,
            GlobalLeaderboardPersistence persistence,
            IMetaDatabase metaDb,
            ScrapeTimePrecomputer precomputer) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=1800, stale-while-revalidate=3600";
            var metric = rankBy ?? "adjusted";
            var effectivePageSize = Math.Clamp(pageSize ?? 10, 1, 50);

            // ── Check precomputed store for default size ──
            if (effectivePageSize == 10)
            {
                var cached = CacheHelper.ServeIfCached(httpContext, precomputer.TryGet($"rankings:overview:{metric}:10"));
                if (cached is not null) return cached;
            }

            var instrumentKeys = persistence.GetInstrumentKeys();

            var allAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var perInstrument = new Dictionary<string, (List<AccountRankingDto> Entries, int Total)>();

            foreach (var instrument in instrumentKeys)
            {
                var db = persistence.GetOrCreateInstrumentDb(instrument);
                var (entries, total) = db.GetAccountRankings(metric, 1, effectivePageSize);
                var entryList = entries.ToList();

                foreach (var e in entryList)
                    allAccountIds.Add(e.AccountId);

                perInstrument[instrument] = (entryList, total);
            }

            // Single bulk name resolution across all instruments
            var names = metaDb.GetDisplayNames(allAccountIds);

            var result = new Dictionary<string, object>();
            foreach (var (instrument, (entries, total)) in perInstrument)
            {
                result[instrument] = new
                {
                    totalAccounts = total,
                    entries = entries.Select(e => new
                    {
                        e.AccountId,
                        displayName = names.GetValueOrDefault(e.AccountId),
                        e.AdjustedSkillRating,
                        e.AdjustedSkillRank,
                        e.WeightedRating,
                        e.WeightedRank,
                        e.FcRate,
                        e.FcRateRank,
                        e.TotalScore,
                        e.TotalScoreRank,
                        e.MaxScorePercent,
                        e.MaxScorePercentRank,
                        e.SongsPlayed,
                        e.Coverage,
                    }).ToList(),
                };
            }

            return Results.Ok(new
            {
                rankBy = metric,
                pageSize = effectivePageSize,
                instruments = result,
            });
        })
        .WithTags("Rankings")
        .RequireRateLimiting("public");
    }
}
