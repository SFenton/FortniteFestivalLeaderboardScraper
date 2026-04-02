using System.Text.Json;
using FortniteFestival.Core.Services;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapPlayerEndpoints(this WebApplication app)
    {
        app.MapGet("/api/player/{accountId}", (
            HttpContext httpContext,
            string accountId,
            string? songId,
            string? instruments,
            double? leeway,
            GlobalLeaderboardPersistence persistence,
            IMetaDatabase metaDb,
            IPathDataStore pathStore,
            ScrapeTimePrecomputer precomputer,
            [FromKeyedServices("PlayerCache")] ResponseCacheService playerCache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=120, stale-while-revalidate=300";

            // Build cache key from all parameters
            var cacheKey = $"player:{accountId}:{songId}:{instruments}:{leeway}";

            // ── Check precomputed store (covers all leeway values in one response) ──
            if (songId is null && instruments is null)
            {
                var precomputedKey = $"player:{accountId}:::";
                var precomputed = precomputer.TryGet(precomputedKey);
                if (precomputed is not null)
                {
                    var requestETag = httpContext.Request.Headers.IfNoneMatch.ToString();
                    if (!string.IsNullOrEmpty(requestETag) && requestETag == precomputed.Value.ETag)
                    {
                        httpContext.Response.Headers.ETag = precomputed.Value.ETag;
                        return Results.StatusCode(304);
                    }
                    httpContext.Response.Headers.ETag = precomputed.Value.ETag;
                    return Results.Bytes(precomputed.Value.Json, "application/json");
                }
            }

            // ── Check cache ──────────────────────────────────────
            var cached = playerCache.Get(cacheKey);
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
            // Optional instrument filter: ?instruments=Solo_Guitar,Solo_Bass
            HashSet<string>? instrumentFilter = null;
            if (!string.IsNullOrWhiteSpace(instruments))
                instrumentFilter = new HashSet<string>(instruments.Split(','), StringComparer.OrdinalIgnoreCase);

            var scores = persistence.GetPlayerProfile(accountId, songId, instrumentFilter);
            var displayName = metaDb.GetDisplayName(accountId);

            // ── Build per-song max-score thresholds when leeway is provided ──
            Dictionary<string, Dictionary<string, int>>? maxScoresByInstrument = null;
            Dictionary<(string SongId, string Instrument), int>? flatThresholds = null;
            if (leeway.HasValue)
            {
                var allMax = pathStore.GetAllMaxScores();
                if (allMax.Count > 0)
                {
                    maxScoresByInstrument = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
                    flatThresholds = new Dictionary<(string, string), int>();
                    foreach (var s in scores)
                    {
                        if (!allMax.TryGetValue(s.SongId, out var songMax)) continue;
                        var raw = songMax.GetByInstrument(s.Instrument);
                        if (!raw.HasValue) continue;
                        var threshold = (int)(raw.Value * (1.0 + leeway.Value / 100.0));

                        if (!maxScoresByInstrument.TryGetValue(s.Instrument, out var instDict))
                        {
                            instDict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                            maxScoresByInstrument[s.Instrument] = instDict;
                        }
                        instDict[s.SongId] = threshold;
                        flatThresholds[(s.SongId, s.Instrument)] = threshold;
                    }
                }
            }

            var rankings = maxScoresByInstrument is not null
                ? persistence.GetPlayerRankingsFiltered(accountId, maxScoresByInstrument, songId, instrumentFilter)
                : persistence.GetPlayerRankings(accountId, songId, instrumentFilter);

            var population = maxScoresByInstrument is not null
                ? persistence.GetFilteredPopulation(maxScoresByInstrument, instrumentFilter)
                : null;
            var unfilteredPopulation = metaDb.GetAllLeaderboardPopulation();

            // ── Find best valid scores for invalid entries (from ScoreHistory) ──
            Dictionary<(string SongId, string Instrument), ValidScoreFallback>? validFallbacks = null;
            if (flatThresholds is not null)
            {
                // Identify which scores are invalid
                var invalidThresholds = new Dictionary<(string, string), int>();
                foreach (var s in scores)
                {
                    var key = (s.SongId, s.Instrument);
                    if (flatThresholds.TryGetValue(key, out var threshold) && s.Score > threshold)
                        invalidThresholds[key] = threshold;
                }
                if (invalidThresholds.Count > 0)
                    validFallbacks = metaDb.GetBestValidScores(accountId, invalidThresholds);
            }

            var enriched = scores.Select(s =>
            {
                var key = (s.SongId, s.Instrument);
                var computedRank = rankings.GetValueOrDefault(key, 0);
                // Priority: Epic ApiRank (authoritative) > computed rank (local DB) > stored rank
                var rank = s.ApiRank > 0 ? s.ApiRank : (computedRank > 0 ? computedRank : s.Rank);
                var totalEntries = unfilteredPopulation.TryGetValue(key, out var pop) && pop > 0 ? (int)pop : 0;

                // ── Score validity (only when leeway is provided) ──
                bool? isValid = null;
                int? validScore = null;
                int? validAccuracy = null;
                bool? validIsFullCombo = null;
                int? validStars = null;
                int? validRank = null;
                int? validTotalEntries = null;

                if (flatThresholds is not null)
                {
                    var hasThreshold = flatThresholds.TryGetValue(key, out var threshold);
                    var scoreIsValid = !hasThreshold || s.Score <= threshold;
                    isValid = scoreIsValid;

                    if (scoreIsValid)
                    {
                        // Score is valid — use filtered rank
                        validScore = s.Score;
                        validAccuracy = s.Accuracy;
                        validIsFullCombo = s.IsFullCombo;
                        validStars = s.Stars;
                        validRank = computedRank > 0 ? computedRank : rank;
                    }
                    else if (validFallbacks is not null && validFallbacks.TryGetValue(key, out var fallback))
                    {
                        // Score is invalid but we have a valid historical score
                        validScore = fallback.Score;
                        validAccuracy = fallback.Accuracy;
                        validIsFullCombo = fallback.IsFullCombo;
                        validStars = fallback.Stars;
                        // Compute what rank this valid score would have on the filtered leaderboard
                        var maxForSong = flatThresholds.GetValueOrDefault(key, 0);
                        validRank = persistence.GetRankForScore(s.Instrument, s.SongId, fallback.Score, maxForSong > 0 ? maxForSong : null);
                    }
                    // else: invalid with no fallback — validScore stays null

                    if (population is not null)
                        validTotalEntries = population.GetValueOrDefault(key, totalEntries);
                }

                return new
                {
                    s.SongId,
                    s.Instrument,
                    s.Score,
                    s.Accuracy,
                    s.IsFullCombo,
                    s.Stars,
                    s.Difficulty,
                    s.Season,
                    s.Percentile,
                    Rank = rank,
                    s.EndTime,
                    TotalEntries = totalEntries,
                    IsValid = isValid,
                    ValidScore = validScore,
                    ValidAccuracy = validAccuracy,
                    ValidIsFullCombo = validIsFullCombo,
                    ValidStars = validStars,
                    ValidRank = validRank,
                    ValidTotalEntries = validTotalEntries,
                };
            }).ToList();

            var payload = new
            {
                accountId,
                displayName,
                totalScores = enriched.Count,
                scores = enriched
            };
            var jsonOpts = httpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
            var etag = playerCache.Set(cacheKey, jsonBytes);

            httpContext.Response.Headers.ETag = etag;
            return Results.Bytes(jsonBytes, "application/json");
        })
        .WithTags("Players")
        .RequireRateLimiting("public");

        // ─── Web tracking: register + backfill for web-viewed players ────

        app.MapPost("/api/player/{accountId}/track", (
            string accountId,
            IMetaDatabase metaDb,
            FestivalService festivalService,
            ScoreBackfiller backfiller,
            HistoryReconstructor historyReconstructor,
            NotificationService notifications,
            TokenManager tokenManager,
            SharedDopPool pool,
            ILoggerFactory loggerFactory,
            BackfillQueue backfillQueue) =>
        {
            if (string.IsNullOrWhiteSpace(accountId))
                return Results.BadRequest(new { error = "accountId is required." });

            // Verify the account exists in our DB
            var displayName = metaDb.GetDisplayName(accountId);
            if (displayName is null)
                return Results.NotFound(new { error = "Unknown account." });

            // Register with a synthetic device ID for web tracking
            const string webDeviceId = "web-tracker";
            metaDb.RegisterUser(webDeviceId, accountId);

            // Enqueue for backfill if not already completed
            var existingStatus = metaDb.GetBackfillStatus(accountId);
            bool backfillKicked = false;
            if (existingStatus is null || existingStatus.Status == "error")
            {
                var songCount = Math.Max(festivalService.Songs.Count, 200);
                metaDb.EnqueueBackfill(accountId, songCount * 6); // songs × instruments
                backfillKicked = true;

                // Fire-and-forget: run backfill + history recon + personal DB rebuild
                _ = Task.Run(async () =>
                {
                    var log = loggerFactory.CreateLogger("FSTService.Api.TrackBackfill");
                    try
                    {
                        var accessToken = await tokenManager.GetAccessTokenAsync(CancellationToken.None);
                        if (accessToken is null)
                        {
                            log.LogWarning("Track-triggered backfill for {AccountId}: no access token available.", accountId);
                            return;
                        }
                        var callerAccountId = tokenManager.AccountId!;

                        if (festivalService.Songs.Count == 0)
                            await festivalService.InitializeAsync();

                        await backfiller.BackfillAccountAsync(
                            accountId, festivalService, accessToken, callerAccountId, pool, ct: CancellationToken.None);

                        // Reconstruct score history
                        var reconStatus = metaDb.GetHistoryReconStatus(accountId);
                        if (reconStatus?.Status != "complete")
                        {
                            var seasonWindows = await historyReconstructor.DiscoverSeasonWindowsAsync(
                                accessToken, callerAccountId, CancellationToken.None);
                            if (seasonWindows.Count > 0)
                            {
                                await historyReconstructor.ReconstructAccountAsync(
                                    accountId, seasonWindows, accessToken, callerAccountId, pool,
                                    ct: CancellationToken.None);
                            }
                        }

                        log.LogInformation("Track-triggered backfill for {AccountId} completed.", accountId);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "Track-triggered backfill for {AccountId} failed.", accountId);
                        try
                        {
                            var hrStatus = metaDb.GetHistoryReconStatus(accountId);
                            if (hrStatus is not null && hrStatus.Status is "pending" or "in_progress")
                                metaDb.FailHistoryRecon(accountId, ex.Message);
                        }
                        catch { /* best-effort */ }
                    }
                });
            }

            var status = metaDb.GetBackfillStatus(accountId);
            return Results.Ok(new
            {
                accountId,
                displayName,
                trackingStarted = true,
                backfillStatus = status?.Status ?? "pending",
                backfillKicked,
            });
        })
        .WithTags("Players")
        .RequireRateLimiting("public");

        app.MapGet("/api/player/{accountId}/sync-status", (
            HttpContext httpContext,
            string accountId,
            IMetaDatabase metaDb,
            ScrapeTimePrecomputer precomputer) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=5";

            // ── Check precomputed store ──
            var precomputed = precomputer.TryGet($"syncstatus:{accountId}");
            if (precomputed is not null)
            {
                var requestETag = httpContext.Request.Headers.IfNoneMatch.ToString();
                if (!string.IsNullOrEmpty(requestETag) && requestETag == precomputed.Value.ETag)
                {
                    httpContext.Response.Headers.ETag = precomputed.Value.ETag;
                    return Results.StatusCode(304);
                }
                httpContext.Response.Headers.ETag = precomputed.Value.ETag;
                return Results.Bytes(precomputed.Value.Json, "application/json");
            }

            var backfill = metaDb.GetBackfillStatus(accountId);
            var historyRecon = metaDb.GetHistoryReconStatus(accountId);
            var rivals = metaDb.GetRivalsStatus(accountId);
            var isRegistered = metaDb.GetRegisteredAccountIds().Contains(accountId);

            return Results.Ok(new
            {
                accountId,
                isTracked = isRegistered,
                backfill = backfill is null ? null : new
                {
                    status = backfill.Status,
                    songsChecked = backfill.SongsChecked,
                    totalSongsToCheck = backfill.TotalSongsToCheck,
                    entriesFound = backfill.EntriesFound,
                    startedAt = backfill.StartedAt,
                    completedAt = backfill.CompletedAt,
                },
                historyRecon = historyRecon is null ? null : new
                {
                    status = historyRecon.Status,
                    songsProcessed = historyRecon.SongsProcessed,
                    totalSongsToProcess = historyRecon.TotalSongsToProcess,
                    seasonsQueried = historyRecon.SeasonsQueried,
                    historyEntriesFound = historyRecon.HistoryEntriesFound,
                    startedAt = historyRecon.StartedAt,
                    completedAt = historyRecon.CompletedAt,
                },
                rivals = rivals is null ? null : new
                {
                    status = rivals.Status,
                    combosComputed = rivals.CombosComputed,
                    totalCombosToCompute = rivals.TotalCombosToCompute,
                    rivalsFound = rivals.RivalsFound,
                    startedAt = rivals.StartedAt,
                    completedAt = rivals.CompletedAt,
                },
            });
        })
        .WithTags("Players")
        .RequireRateLimiting("public");

        app.MapGet("/api/player/{accountId}/stats", (
            HttpContext httpContext,
            string accountId,
            IMetaDatabase metaDb,
            GlobalLeaderboardPersistence persistence,
            ScrapeTimePrecomputer precomputer,
            [FromKeyedServices("PlayerCache")] ResponseCacheService playerCache) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=300";

            var cacheKey = $"playerstats:{accountId}";

            // ── Check precomputed store first ──
            var precomputed = precomputer.TryGet(cacheKey);
            if (precomputed is not null)
            {
                var requestETag = httpContext.Request.Headers.IfNoneMatch.ToString();
                if (!string.IsNullOrEmpty(requestETag) && requestETag == precomputed.Value.ETag)
                {
                    httpContext.Response.Headers.ETag = precomputed.Value.ETag;
                    return Results.StatusCode(304);
                }
                httpContext.Response.Headers.ETag = precomputed.Value.ETag;
                return Results.Bytes(precomputed.Value.Json, "application/json");
            }

            var cached = playerCache.Get(cacheKey);
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

            // Return tiered stats if available, else fall back to legacy flat stats
            var tierRows = metaDb.GetPlayerStatsTiers(accountId);
            if (tierRows.Count > 0)
            {
                int totalSongs = persistence.GetTotalSongCount();
                var payload = new
                {
                    accountId,
                    totalSongs,
                    instruments = tierRows.Select(r => new
                    {
                        instrument = r.Instrument,
                        tiers = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(r.TiersJson),
                    }).ToList(),
                };
                var jsonOpts = httpContext.RequestServices
                    .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                    .Value.SerializerOptions;
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOpts);
                var etag = playerCache.Set(cacheKey, jsonBytes);
                httpContext.Response.Headers.ETag = etag;
                return Results.Bytes(jsonBytes, "application/json");
            }

            // Legacy fallback (PlayerStats table — rarely populated)
            var stats = metaDb.GetPlayerStats(accountId);
            if (stats.Count == 0)
                return Results.Ok(new { accountId, stats = Array.Empty<object>() });

            return Results.Ok(new
            {
                accountId,
                stats = stats.Select(s => new
                {
                    s.Instrument,
                    s.SongsPlayed,
                    s.FullComboCount,
                    s.GoldStarCount,
                    s.AvgAccuracy,
                    s.BestRank,
                    s.BestRankSongId,
                    s.TotalScore,
                    s.PercentileDist,
                    s.AvgPercentile,
                    s.OverallPercentile,
                }).ToList(),
            });
        })
        .WithTags("Players")
        .RequireRateLimiting("public");

        app.MapGet("/api/player/{accountId}/history", (
            HttpContext httpContext,
            string accountId,
            int? limit,
            string? songId,
            string? instrument,
            IMetaDatabase metaDb,
            ScrapeTimePrecomputer precomputer) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=60";
            // Check if the account is a registered user
            var registeredIds = metaDb.GetRegisteredAccountIds();
            if (!registeredIds.Contains(accountId))
            {
                return Results.NotFound(new
                {
                    error = "Score history is only available for registered users."
                });
            }

            // ── Check precomputed store for unfiltered requests ──
            if (songId is null && instrument is null && (limit is null || limit >= 50000))
            {
                var precomputed = precomputer.TryGet($"history:{accountId}");
                if (precomputed is not null)
                {
                    var requestETag = httpContext.Request.Headers.IfNoneMatch.ToString();
                    if (!string.IsNullOrEmpty(requestETag) && requestETag == precomputed.Value.ETag)
                    {
                        httpContext.Response.Headers.ETag = precomputed.Value.ETag;
                        return Results.StatusCode(304);
                    }
                    httpContext.Response.Headers.ETag = precomputed.Value.ETag;
                    return Results.Bytes(precomputed.Value.Json, "application/json");
                }
            }

            var history = metaDb.GetScoreHistory(accountId, limit ?? 50000, songId, instrument);
            return Results.Ok(new
            {
                accountId,
                count = history.Count,
                history
            });
        })
        .WithTags("Players")
        .RequireRateLimiting("public");
    }
}
