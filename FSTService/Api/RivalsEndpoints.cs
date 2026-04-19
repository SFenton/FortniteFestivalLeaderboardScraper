using System.Text.Json;
using FortniteFestival.Core.Models;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapRivalsEndpoints(this WebApplication app)
    {
        // ─── Combo overview ────────────────────────────────────────

        app.MapGet("/api/player/{accountId}/rivals", (
            HttpContext httpContext,
            string accountId,
            IMetaDatabase metaDb,
            ScrapeTimePrecomputer precomputer,
            [FromKeyedServices("RivalsCache")] ResponseCacheService rivalsCache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300, stale-while-revalidate=600";

            // ── Check precomputed store first ──
            var precomputedKey = $"rivals-overview:{accountId}";
            {
                var result = CacheHelper.ServeIfCached(httpContext, precomputer.TryGet(precomputedKey));
                if (result is not null) return result;
            }

            var cacheKey = $"overview:{accountId}";
            {
                var result = CacheHelper.ServeIfCached(httpContext, rivalsCache.Get(cacheKey));
                if (result is not null) return result;
            }

            var status = metaDb.GetRivalsStatus(accountId);
            var combos = metaDb.GetRivalCombos(accountId);

            var payload = new
            {
                accountId,
                computedAt = status?.CompletedAt,
                combos = combos.Select(c => new
                {
                    combo = c.InstrumentCombo,
                    aboveCount = c.AboveCount,
                    belowCount = c.BelowCount,
                }).ToList(),
            };
            var jsonOpts = httpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
            var etag = rivalsCache.Set(cacheKey, jsonBytes);

            httpContext.Response.Headers.ETag = etag;
            return Results.Bytes(jsonBytes, "application/json");
        })
        .WithTags("Rivals")
        .RequireRateLimiting("public");

        // ─── Batch rivals data for suggestion generation ───────────
        // Registered before {combo} route to avoid "suggestions" matching as combo value.

        app.MapGet("/api/player/{accountId}/rivals/suggestions", (
            HttpContext httpContext,
            string accountId,
            string? combo,
            int? limit,
            IMetaDatabase metaDb,
            [FromKeyedServices("RivalsCache")] ResponseCacheService rivalsCache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300, stale-while-revalidate=600";

            var effectiveLimit = limit ?? 5;
            var effectiveCombo = !string.IsNullOrEmpty(combo)
                ? (ComboIds.NormalizeAnyComboParam(combo) ?? combo)
                : "";
            var cacheKey = $"suggestions:{accountId}:{effectiveCombo}:{effectiveLimit}";
            {
                var result = CacheHelper.ServeIfCached(httpContext, rivalsCache.Get(cacheKey));
                if (result is not null) return result;
            }

            var status = metaDb.GetRivalsStatus(accountId);

            // Determine which combos to query
            var combosToQuery = new List<string>();
            if (!string.IsNullOrEmpty(effectiveCombo))
            {
                combosToQuery.Add(effectiveCombo);
            }
            else
            {
                // No combo specified — use all available combos
                var allCombos = metaDb.GetRivalCombos(accountId);
                combosToQuery.AddRange(allCombos.Select(c => c.InstrumentCombo));
            }

            // Gather top N rivals per direction across all requested combos
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var aboveRivals = new List<UserRivalRow>();
            var belowRivals = new List<UserRivalRow>();

            foreach (var c in combosToQuery)
            {
                foreach (var r in metaDb.GetUserRivals(accountId, c, "above"))
                {
                    if (seen.Add(r.RivalAccountId + ":above"))
                        aboveRivals.Add(r);
                }
                foreach (var r in metaDb.GetUserRivals(accountId, c, "below"))
                {
                    if (seen.Add(r.RivalAccountId + ":below"))
                        belowRivals.Add(r);
                }
            }

            // Take top N per direction (sorted by RivalScore descending)
            var topAbove = aboveRivals.OrderByDescending(r => r.RivalScore).Take(effectiveLimit).ToList();
            var topBelow = belowRivals.OrderByDescending(r => r.RivalScore).Take(effectiveLimit).ToList();
            var allRivals = topAbove.Concat(topBelow).ToList();

            if (allRivals.Count == 0)
                return Results.NotFound(new { error = "No rivals found." });

            // Bulk name resolution
            var rivalIds = allRivals.Select(r => r.RivalAccountId).Distinct().ToList();
            var names = metaDb.GetDisplayNames(rivalIds);

            // Parse combo(s) into instrument list for sample fetching
            var instrumentSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in combosToQuery)
            {
                if (c.Contains('+'))
                    foreach (var i in c.Split('+')) instrumentSet.Add(i);
                else if (c.Length <= 2 && int.TryParse(c, System.Globalization.NumberStyles.HexNumber, null, out _))
                    foreach (var i in ComboIds.ToInstruments(c)) instrumentSet.Add(i);
                else
                    instrumentSet.Add(c);
            }

            // Build entries with song samples
            var entries = allRivals.Select(r =>
            {
                var songs = new List<RivalSongSampleRow>();
                foreach (var inst in instrumentSet)
                    songs.AddRange(metaDb.GetRivalSongSamples(accountId, r.RivalAccountId, inst));

                return new
                {
                    accountId = r.RivalAccountId,
                    displayName = names.GetValueOrDefault(r.RivalAccountId),
                    direction = r.Direction,
                    sharedSongCount = r.SharedSongCount,
                    aheadCount = r.AheadCount,
                    behindCount = r.BehindCount,
                    songs = songs.Select(s => new
                    {
                        s.SongId,
                        s.Instrument,
                        s.UserRank,
                        s.RivalRank,
                        s.RankDelta,
                        s.UserScore,
                        s.RivalScore,
                    }).ToList(),
                };
            }).ToList();

            var payload = new
            {
                accountId,
                combo = effectiveCombo,
                computedAt = status?.CompletedAt,
                rivals = entries,
            };
            var jsonOpts = httpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
            var etag = rivalsCache.Set(cacheKey, jsonBytes);

            httpContext.Response.Headers.ETag = etag;
            return Results.Bytes(jsonBytes, "application/json");
        })
        .WithTags("Rivals")
        .RequireRateLimiting("public");

        // ─── Batch: all combos in one call ─────────────────────────
        // Registered before {combo} to avoid "all" matching as a combo value.

        app.MapGet("/api/player/{accountId}/rivals/all", (
            HttpContext httpContext,
            string accountId,
            IMetaDatabase metaDb,
            ScrapeTimePrecomputer precomputer,
            [FromKeyedServices("RivalsCache")] ResponseCacheService rivalsCache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300, stale-while-revalidate=600";

            // ── Check precomputed store first ──
            var precomputedKey = $"rivals-all:{accountId}";
            {
                var result = CacheHelper.ServeIfCached(httpContext, precomputer.TryGet(precomputedKey));
                if (result is not null) return result;
            }

            var cacheKey = $"all:{accountId}";
            {
                var result = CacheHelper.ServeIfCached(httpContext, rivalsCache.Get(cacheKey));
                if (result is not null) return result;
            }

            var combos = metaDb.GetRivalCombos(accountId);
            if (combos.Count == 0)
                return Results.NotFound(new { error = "No rivals found." });

            var allRivalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var comboData = new Dictionary<string, (List<UserRivalRow> Above, List<UserRivalRow> Below)>();

            foreach (var c in combos)
            {
                var above = metaDb.GetUserRivals(accountId, c.InstrumentCombo, "above");
                var below = metaDb.GetUserRivals(accountId, c.InstrumentCombo, "below");
                comboData[c.InstrumentCombo] = (above, below);
                foreach (var r in above) allRivalIds.Add(r.RivalAccountId);
                foreach (var r in below) allRivalIds.Add(r.RivalAccountId);
            }

            var names = metaDb.GetDisplayNames(allRivalIds);

            var payload = new
            {
                accountId,
                combos = comboData.Select(kv => new
                {
                    combo = kv.Key,
                    above = kv.Value.Above.Select(r => MapRivalSummary(r, names)),
                    below = kv.Value.Below.Select(r => MapRivalSummary(r, names)),
                }).ToList(),
            };

            var jsonOpts = httpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
            var etag = rivalsCache.Set(cacheKey, jsonBytes);

            httpContext.Response.Headers.ETag = etag;
            return Results.Bytes(jsonBytes, "application/json");
        })
        .WithTags("Rivals")
        .RequireRateLimiting("public");

        // ─── Diagnostics (admin only) ──────────────────────────────
        // Registered before {combo} route to avoid "diagnostics" matching as combo value.

        app.MapGet("/api/player/{accountId}/rivals/diagnostics", (
            string accountId,
            IMetaDatabase metaDb,
            RivalsCalculator rivalsCalculator) =>
        {
            var status = metaDb.GetRivalsStatus(accountId);
            var combos = metaDb.GetRivalCombos(accountId);
            var diagnostics = rivalsCalculator.GetDiagnostics(accountId);

            return Results.Ok(new
            {
                accountId,
                rivalsStatus = status is null ? null : new
                {
                    status.Status,
                    status.CombosComputed,
                    status.TotalCombosToCompute,
                    status.RivalsFound,
                    status.StartedAt,
                    status.CompletedAt,
                    status.ErrorMessage,
                },
                combosStored = combos.Select(c => new
                {
                    c.InstrumentCombo,
                    c.AboveCount,
                    c.BelowCount,
                }).ToList(),
                instruments = diagnostics.Instruments.Select(i => new
                {
                    i.Instrument,
                    i.TotalSongs,
                    i.MeetsMinimum,
                    i.RankedSongs,
                    i.CandidateCount,
                    rankBreakdown = new
                    {
                        i.BothZero,
                        i.RankOnly,
                        i.ApiRankOnly,
                        i.BothSet,
                        i.Mismatch,
                    },
                    thresholdCounts = new
                    {
                        above = new
                        {
                            i.AboveThresholdCounts.AtLeastFive,
                            i.AboveThresholdCounts.AtLeastFour,
                            i.AboveThresholdCounts.AtLeastThree,
                            i.AboveThresholdCounts.AtLeastTwo,
                            i.AboveThresholdCounts.AtLeastOne,
                        },
                        below = new
                        {
                            i.BelowThresholdCounts.AtLeastFive,
                            i.BelowThresholdCounts.AtLeastFour,
                            i.BelowThresholdCounts.AtLeastThree,
                            i.BelowThresholdCounts.AtLeastTwo,
                            i.BelowThresholdCounts.AtLeastOne,
                        },
                    },
                    selectionPreview = i.SelectionPreview is null ? null : new
                    {
                        i.SelectionPreview.AboveSelected,
                        i.SelectionPreview.BelowSelected,
                        i.SelectionPreview.LoosestThresholdUsedAbove,
                        i.SelectionPreview.LoosestThresholdUsedBelow,
                    },
                    probe = i.Probe is null ? null : new
                    {
                        i.Probe.SongId,
                        i.Probe.EffectiveRank,
                        i.Probe.Rank,
                        i.Probe.ApiRank,
                        i.Probe.RangeLo,
                        i.Probe.RangeHi,
                        i.Probe.NeighborsFound,
                    },
                    sampleEntries = i.SampleEntries,
                }).ToList(),
            });
        })
        .WithTags("Rivals")
        .RequireRateLimiting("protected")
        .RequireAuthorization();

        // ─── Rival list for a specific combo ───────────────────────

        app.MapGet("/api/player/{accountId}/rivals/{combo}", (
            HttpContext httpContext,
            string accountId,
            string combo,
            IMetaDatabase metaDb,
            [FromKeyedServices("RivalsCache")] ResponseCacheService rivalsCache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300, stale-while-revalidate=600";

            var resolvedCombo = TryResolveRivalCombo(combo);
            if (resolvedCombo is null)
                return Results.BadRequest(new { error = $"Invalid combo: {combo}" });
            combo = resolvedCombo.Value.CanonicalCombo;

            var cacheKey = $"list:{accountId}:{combo}";
            {
                var result = CacheHelper.ServeIfCached(httpContext, rivalsCache.Get(cacheKey));
                if (result is not null) return result;
            }

            var above = metaDb.GetUserRivals(accountId, combo, "above");
            var below = metaDb.GetUserRivals(accountId, combo, "below");

            if (above.Count == 0 && below.Count == 0)
                return Results.NotFound(new { error = "No rivals found for this combo." });

            var rivalIds = above.Concat(below).Select(r => r.RivalAccountId).Distinct().ToList();
            var names = metaDb.GetDisplayNames(rivalIds);

            var payload = new
            {
                combo,
                above = above.Select(r => MapRivalSummary(r, names)),
                below = below.Select(r => MapRivalSummary(r, names)),
            };
            var jsonOpts = httpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
            var etag = rivalsCache.Set(cacheKey, jsonBytes);

            httpContext.Response.Headers.ETag = etag;
            return Results.Bytes(jsonBytes, "application/json");
        })
        .WithTags("Rivals")
        .RequireRateLimiting("public");

        // ─── Detailed comparison with a rival for a combo (paginated) ──

        app.MapGet("/api/player/{accountId}/rivals/{combo}/{rivalId}", (
            HttpContext httpContext,
            string accountId,
            string combo,
            string rivalId,
            int? limit,
            int? offset,
            string? sort,
            IMetaDatabase metaDb,
            FestivalService festivalService,
            RivalsCalculator rivalsCalculator,
            [FromKeyedServices("RivalsCache")] ResponseCacheService rivalsCache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=120, stale-while-revalidate=300";

            var resolvedCombo = TryResolveRivalCombo(combo);
            if (resolvedCombo is null)
                return Results.BadRequest(new { error = $"Invalid combo: {combo}" });

            combo = resolvedCombo.Value.CanonicalCombo;
            var instruments = resolvedCombo.Value.Instruments;

            var effectiveLimit = limit ?? 50;
            var effectiveOffset = offset ?? 0;
            var sortMode = sort?.ToLowerInvariant() ?? "closest";
            var cacheKey = $"detail:{accountId}:{combo}:{rivalId}:{effectiveLimit}:{effectiveOffset}:{sortMode}";
            {
                var result = CacheHelper.ServeIfCached(httpContext, rivalsCache.Get(cacheKey));
                if (result is not null) return result;
            }

            var allSamples = new List<RivalSongSampleRow>();
            foreach (var inst in instruments)
            {
                allSamples.AddRange(metaDb.GetRivalSongSamples(accountId, rivalId, inst));
            }

            if (allSamples.Count == 0)
                return Results.NotFound(new { error = "No song data for this rival." });

            // Sort
            IEnumerable<RivalSongSampleRow> sorted = sortMode switch
            {
                "they_lead" => allSamples.OrderBy(s => s.RankDelta),
                "you_lead" => allSamples.OrderByDescending(s => s.RankDelta),
                _ => allSamples.OrderBy(s => Math.Abs(s.RankDelta)),
            };

            var total = allSamples.Count;

            // limit=0 means all
            var page = effectiveLimit == 0
                ? sorted.Skip(effectiveOffset).ToList()
                : sorted.Skip(effectiveOffset).Take(effectiveLimit).ToList();

            var songLookup = festivalService.Songs
                .Where(s => s.track?.su is not null)
                .ToDictionary(s => s.track.su, StringComparer.OrdinalIgnoreCase);
            var rivalName = metaDb.GetDisplayName(rivalId);

            // Compute song gaps on-the-fly
            var gaps = rivalsCalculator.ComputeSongGaps(accountId, rivalId, instruments);

            var payload = new
            {
                rival = new { accountId = rivalId, displayName = rivalName },
                combo,
                totalSongs = total,
                offset = effectiveOffset,
                limit = effectiveLimit,
                sort = sortMode,
                songs = page.Select(s =>
                {
                    songLookup.TryGetValue(s.SongId, out var song);
                    return new
                    {
                        s.SongId,
                        title = song?.track?.tt,
                        artist = song?.track?.an,
                        s.Instrument,
                        s.UserRank,
                        s.RivalRank,
                        s.RankDelta,
                        s.UserScore,
                        s.RivalScore,
                    };
                }).ToList(),
                songsToCompete = gaps.SongsToCompete.Select(g =>
                {
                    songLookup.TryGetValue(g.SongId, out var song);
                    return new
                    {
                        g.SongId,
                        title = song?.track?.tt,
                        artist = song?.track?.an,
                        g.Instrument,
                        g.Score,
                        g.Rank,
                    };
                }).ToList(),
                yourExclusiveSongs = gaps.YourExclusives.Select(g =>
                {
                    songLookup.TryGetValue(g.SongId, out var song);
                    return new
                    {
                        g.SongId,
                        title = song?.track?.tt,
                        artist = song?.track?.an,
                        g.Instrument,
                        g.Score,
                        g.Rank,
                    };
                }).ToList(),
            };
            var jsonOpts = httpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
            var etag = rivalsCache.Set(cacheKey, jsonBytes);

            httpContext.Response.Headers.ETag = etag;
            return Results.Bytes(jsonBytes, "application/json");
        })
        .WithTags("Rivals")
        .RequireRateLimiting("public");

        // ─── Per-instrument songs for a rival (no combo context) ───

        app.MapGet("/api/player/{accountId}/rivals/{rivalId}/songs/{instrument}", (
            HttpContext httpContext,
            string accountId,
            string rivalId,
            string instrument,
            int? limit,
            int? offset,
            string? sort,
            IMetaDatabase metaDb,
            FestivalService festivalService,
            [FromKeyedServices("RivalsCache")] ResponseCacheService rivalsCache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=120, stale-while-revalidate=300";

            var effectiveLimit = limit ?? 50;
            var effectiveOffset = offset ?? 0;
            var sortMode = sort?.ToLowerInvariant() ?? "closest";
            var cacheKey = $"songs:{accountId}:{rivalId}:{instrument}:{effectiveLimit}:{effectiveOffset}:{sortMode}";
            {
                var result = CacheHelper.ServeIfCached(httpContext, rivalsCache.Get(cacheKey));
                if (result is not null) return result;
            }

            var samples = metaDb.GetRivalSongSamples(accountId, rivalId, instrument);
            if (samples.Count == 0)
                return Results.NotFound(new { error = "No song data for this rival on this instrument." });

            IEnumerable<RivalSongSampleRow> sorted = sortMode switch
            {
                "they_lead" => samples.OrderBy(s => s.RankDelta),
                "you_lead" => samples.OrderByDescending(s => s.RankDelta),
                _ => samples.OrderBy(s => Math.Abs(s.RankDelta)),
            };

            var total = samples.Count;
            var page = effectiveLimit == 0
                ? sorted.Skip(effectiveOffset).ToList()
                : sorted.Skip(effectiveOffset).Take(effectiveLimit).ToList();

            var songLookup = festivalService.Songs
                .Where(s => s.track?.su is not null)
                .ToDictionary(s => s.track.su, StringComparer.OrdinalIgnoreCase);
            var rivalName = metaDb.GetDisplayName(rivalId);

            var payload = new
            {
                rival = new { accountId = rivalId, displayName = rivalName },
                instrument,
                totalSongs = total,
                offset = effectiveOffset,
                limit = effectiveLimit,
                sort = sortMode,
                songs = page.Select(s =>
                {
                    songLookup.TryGetValue(s.SongId, out var song);
                    return new
                    {
                        s.SongId,
                        title = song?.track?.tt,
                        artist = song?.track?.an,
                        s.UserRank,
                        s.RivalRank,
                        s.RankDelta,
                        s.UserScore,
                        s.RivalScore,
                    };
                }).ToList(),
            };
            var jsonOpts = httpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
            var etag = rivalsCache.Set(cacheKey, jsonBytes);

            httpContext.Response.Headers.ETag = etag;
            return Results.Bytes(jsonBytes, "application/json");
        })
        .WithTags("Rivals")
        .RequireRateLimiting("public");

        // ─── Force recomputation ───────────────────────────────────

        app.MapPost("/api/player/{accountId}/rivals/recompute", (
            string accountId,
            IMetaDatabase metaDb,
            RivalsOrchestrator rivalsOrchestrator) =>
        {
            metaDb.EnsureRivalsStatus(accountId);
            rivalsOrchestrator.ComputeForUser(accountId);
            return Results.Ok(new { accountId, status = "recomputed" });
        })
        .WithTags("Rivals")
        .RequireRateLimiting("protected")
        .RequireAuthorization();
    }

    private static object MapRivalSummary(UserRivalRow r, Dictionary<string, string> names)
    {
        return new
        {
            accountId = r.RivalAccountId,
            displayName = names.GetValueOrDefault(r.RivalAccountId),
            rivalScore = r.RivalScore,
            sharedSongCount = r.SharedSongCount,
            aheadCount = r.AheadCount,
            behindCount = r.BehindCount,
            avgSignedDelta = r.AvgSignedDelta,
        };
    }

    internal static ResolvedRivalCombo? TryResolveRivalCombo(string? combo)
    {
        var normalizedCombo = ComboIds.NormalizeAnyComboParam(combo);
        if (normalizedCombo is null)
            return null;

        return new ResolvedRivalCombo(normalizedCombo, ComboIds.ToInstruments(normalizedCombo).ToArray());
    }

    internal readonly record struct ResolvedRivalCombo(string CanonicalCombo, string[] Instruments);
}
