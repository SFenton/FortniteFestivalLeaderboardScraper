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
                var result = CacheHelper.ServeIfCached(httpContext, precomputer.TryGet(precomputedKey));
                if (result is not null) return result;
            }

            // ── Check cache ──────────────────────────────────────
            {
                var result = CacheHelper.ServeIfCached(httpContext, playerCache.Get(cacheKey));
                if (result is not null) return result;
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

            // ── Last-played dates from score_history ──
            var lastPlayedDates = metaDb.GetLastPlayedDates(accountId);
            var validLastPlayedDates = flatThresholds is not null
                ? metaDb.GetLastPlayedDates(accountId, flatThresholds)
                : null;

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
                    si = s.SongId,
                    ins = ComboIds.FromInstruments(new[] { s.Instrument }),
                    sc = s.Score,
                    acc = s.Accuracy / 1000,
                    fc = s.IsFullCombo,
                    st = s.Stars,
                    dif = s.Difficulty,
                    sn = s.Season,
                    pct = s.Percentile,
                    rk = rank,
                    lrk = computedRank > 0 ? computedRank : 0,
                    et = s.EndTime,
                    te = totalEntries,
                    lp = lastPlayedDates.GetValueOrDefault(key),
                    vlp = validLastPlayedDates?.GetValueOrDefault(key),
                    isValid = isValid,
                    validScore = validScore,
                    validAccuracy = validAccuracy / 1000,
                    validIsFullCombo = validIsFullCombo,
                    validStars = validStars,
                    validRank = validRank,
                    validTotalEntries = validTotalEntries,
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
            CyclicalSongMachine cyclicalMachine,
            UserSyncProgressTracker syncTracker,
            NotificationService notifications,
            TokenManager tokenManager,
            GlobalLeaderboardPersistence persistence,
            RivalsOrchestrator rivalsOrchestrator,
            ILoggerFactory loggerFactory) =>
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
                metaDb.StartBackfill(accountId);
                backfillKicked = true;

                // Fire-and-forget: attach to cyclical machine for backfill + history recon
                _ = Task.Run(async () =>
                {
                    var log = loggerFactory.CreateLogger("FSTService.Api.TrackBackfill");
                    try
                    {
                        if (festivalService.Songs.Count == 0)
                            await festivalService.InitializeAsync();

                        var chartedSongIds = festivalService.Songs
                            .Where(s => s.track?.su is not null)
                            .Select(s => s.track.su!)
                            .ToList();

                        var alreadyChecked = metaDb.GetCheckedBackfillPairs(accountId);

                        var user = new UserWorkItem
                        {
                            AccountId = accountId,
                            Purposes = WorkPurpose.Backfill | WorkPurpose.HistoryRecon,
                            AllTimeNeeded = true,
                            SeasonsNeeded = [], // Season discovery handled by cyclical machine
                            AlreadyChecked = alreadyChecked,
                        };

                        syncTracker.BeginBackfill(accountId, chartedSongIds.Count * 6);

                        var result = await cyclicalMachine.AttachAsync(
                            [user], chartedSongIds, seasonWindows: [],
                            isHighPriority: false, ct: CancellationToken.None);

                        // Per-user completion actions
                        metaDb.CompleteBackfill(accountId);
                        rivalsOrchestrator.ComputeForUser(accountId);
                        _ = notifications.NotifyBackfillCompleteAsync(accountId);

                        var reconStatus = metaDb.GetHistoryReconStatus(accountId);
                        if (reconStatus is null)
                            metaDb.EnqueueHistoryRecon(accountId, 0);
                        metaDb.CompleteHistoryRecon(accountId);
                        _ = notifications.NotifyHistoryReconCompleteAsync(accountId);

                        syncTracker.Complete(accountId);
                        log.LogInformation("Track-triggered backfill for {AccountId} completed via cyclical machine.", accountId);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "Track-triggered backfill for {AccountId} failed.", accountId);
                        syncTracker.Error(accountId, ex.Message);
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
            UserSyncProgressTracker syncTracker,
            ScrapeTimePrecomputer precomputer) =>
        {
            // Reduce cache during active sync for fresher fallback reads
            var liveProgress = syncTracker.GetProgress(accountId);
            httpContext.Response.Headers.CacheControl = liveProgress is not null ? "public, max-age=1" : "public, max-age=5";

            // ── Check precomputed store (skip if live progress available) ──
            if (liveProgress is null)
            {
                var result = CacheHelper.ServeIfCached(httpContext, precomputer.TryGet($"syncstatus:{accountId}"));
                if (result is not null) return result;
            }

            var backfill = metaDb.GetBackfillStatus(accountId);
            var historyRecon = metaDb.GetHistoryReconStatus(accountId);
            var rivals = metaDb.GetRivalsStatus(accountId);
            var isRegistered = metaDb.GetRegisteredAccountIds().Contains(accountId);

            // Overlay in-memory progress over DB values when available (always fresher)
            int? liveBfChecked = null;
            int? liveBfEntries = null;
            string? liveBfSongName = null;
            int? liveHrProcessed = null;
            int? liveHrSeasons = null;
            int? liveHrEntries = null;
            int? liveRivalsCombos = null;
            int? liveRivalsFound = null;
            string? liveCurrentSongName = null;

            if (liveProgress is not null)
            {
                var phase = liveProgress.Phase;
                if (phase == SyncProgressPhase.Backfill)
                {
                    liveBfChecked = Volatile.Read(ref liveProgress.ItemsCompleted);
                    liveBfEntries = Volatile.Read(ref liveProgress.EntriesFound);
                    liveBfSongName = liveProgress.CurrentSongName;
                }
                else if (phase == SyncProgressPhase.History)
                {
                    liveHrProcessed = Volatile.Read(ref liveProgress.ItemsCompleted);
                    liveHrSeasons = Volatile.Read(ref liveProgress.SeasonsQueried);
                    liveHrEntries = Volatile.Read(ref liveProgress.EntriesFound);
                    liveCurrentSongName = liveProgress.CurrentSongName;
                }
                else if (phase == SyncProgressPhase.Rivals)
                {
                    liveRivalsCombos = Volatile.Read(ref liveProgress.ItemsCompleted);
                    liveRivalsFound = Volatile.Read(ref liveProgress.RivalsFound);
                }
            }

            return Results.Ok(new
            {
                accountId,
                isTracked = isRegistered,
                backfill = backfill is null ? null : new
                {
                    status = backfill.Status,
                    songsChecked = liveBfChecked ?? backfill.SongsChecked,
                    totalSongsToCheck = backfill.TotalSongsToCheck,
                    entriesFound = liveBfEntries ?? backfill.EntriesFound,
                    startedAt = backfill.StartedAt,
                    completedAt = backfill.CompletedAt,
                    currentSongName = liveBfSongName,
                },
                historyRecon = historyRecon is null ? null : new
                {
                    status = historyRecon.Status,
                    songsProcessed = liveHrProcessed ?? historyRecon.SongsProcessed,
                    totalSongsToProcess = historyRecon.TotalSongsToProcess,
                    seasonsQueried = liveHrSeasons ?? historyRecon.SeasonsQueried,
                    historyEntriesFound = liveHrEntries ?? historyRecon.HistoryEntriesFound,
                    startedAt = historyRecon.StartedAt,
                    completedAt = historyRecon.CompletedAt,
                    currentSongName = liveCurrentSongName,
                },
                rivals = rivals is null ? null : new
                {
                    status = rivals.Status,
                    combosComputed = liveRivalsCombos ?? rivals.CombosComputed,
                    totalCombosToCompute = rivals.TotalCombosToCompute,
                    rivalsFound = liveRivalsFound ?? rivals.RivalsFound,
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
            {
                var result = CacheHelper.ServeIfCached(httpContext, precomputer.TryGet(cacheKey));
                if (result is not null) return result;
            }

            {
                var result = CacheHelper.ServeIfCached(httpContext, playerCache.Get(cacheKey));
                if (result is not null) return result;
            }

            // Return tiered stats if available, else fall back to legacy flat stats
            var tierRows = metaDb.GetPlayerStatsTiers(accountId);
            if (tierRows.Count > 0)
            {
                int totalSongs = persistence.GetTotalSongCount();

                // Include composite ranks when available
                var composite = metaDb.GetCompositeRanking(accountId);
                object? compositeRanks = composite is null ? null : new
                {
                    adjusted = composite.CompositeRank,
                    weighted = composite.CompositeRankWeighted,
                    fcRate = composite.CompositeRankFcRate,
                    totalScore = composite.CompositeRankTotalScore,
                    maxScore = composite.CompositeRankMaxScore,
                };

                // Build per-instrument rank tiers from rank_history_deltas
                var instrumentKeys = persistence.GetInstrumentKeys();
                var instrumentRanks = new List<object>();
                foreach (var instrument in instrumentKeys)
                {
                    var db = persistence.GetOrCreateInstrumentDb(instrument);
                    var baseRanking = db.GetAccountRanking(accountId);
                    if (baseRanking is null) continue;

                    var deltas = db.GetTodayRankDeltas(accountId);
                    var tiers = new List<object>();
                    int prevAdj = baseRanking.AdjustedSkillRank, prevWgt = baseRanking.WeightedRank,
                        prevFc = baseRanking.FcRateRank, prevTs = baseRanking.TotalScoreRank, prevMs = baseRanking.MaxScorePercentRank;

                    foreach (var (bucket, dAdj, dWgt, dFc, dTs, dMs) in deltas)
                    {
                        int effAdj = baseRanking.AdjustedSkillRank + dAdj;
                        int effWgt = baseRanking.WeightedRank + dWgt;
                        int effFc = baseRanking.FcRateRank + dFc;
                        int effTs = baseRanking.TotalScoreRank + dTs;
                        int effMs = baseRanking.MaxScorePercentRank + dMs;

                        if (effAdj == prevAdj && effWgt == prevWgt && effFc == prevFc && effTs == prevTs && effMs == prevMs)
                            continue;

                        var tier = new Dictionary<string, object?> { ["l"] = bucket >= 90.0 ? null : (object)Math.Round(bucket, 1) };
                        if (effAdj != prevAdj) tier["adjusted"] = effAdj;
                        if (effWgt != prevWgt) tier["weighted"] = effWgt;
                        if (effFc != prevFc) tier["fcRate"] = effFc;
                        if (effTs != prevTs) tier["totalScore"] = effTs;
                        if (effMs != prevMs) tier["maxScore"] = effMs;
                        tiers.Add(tier);
                        prevAdj = effAdj; prevWgt = effWgt; prevFc = effFc; prevTs = effTs; prevMs = effMs;
                    }

                    instrumentRanks.Add(new
                    {
                        ins = ComboIds.FromInstruments(new[] { instrument }),
                        totalRanked = db.GetRankedAccountCount(),
                        @base = new { adjusted = baseRanking.AdjustedSkillRank, weighted = baseRanking.WeightedRank, fcRate = baseRanking.FcRateRank, totalScore = baseRanking.TotalScoreRank, maxScore = baseRanking.MaxScorePercentRank },
                        tiers,
                    });
                }

                var payload = new
                {
                    accountId,
                    totalSongs,
                    compositeRanks,
                    instrumentRanks = instrumentRanks.Count > 0 ? instrumentRanks : null,
                    instruments = tierRows.Select(r => new
                    {
                        ins = r.Instrument == "Overall" ? "00" : ComboIds.FromInstruments(new[] { r.Instrument }),
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
                {
                    var result = CacheHelper.ServeIfCached(httpContext, precomputer.TryGet($"history:{accountId}"));
                    if (result is not null) return result;
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
