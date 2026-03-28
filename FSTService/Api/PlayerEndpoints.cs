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
            GlobalLeaderboardPersistence persistence,
            MetaDatabase metaDb) =>
        {
            httpContext.Response.Headers.CacheControl = "public, max-age=30";
            // Optional instrument filter: ?instruments=Solo_Guitar,Solo_Bass
            HashSet<string>? instrumentFilter = null;
            if (!string.IsNullOrWhiteSpace(instruments))
                instrumentFilter = new HashSet<string>(instruments.Split(','), StringComparer.OrdinalIgnoreCase);

            var scores = persistence.GetPlayerProfile(accountId, songId, instrumentFilter);
            var displayName = metaDb.GetDisplayName(accountId);
            var rankings = persistence.GetPlayerRankings(accountId, songId, instrumentFilter);
            var population = metaDb.GetAllLeaderboardPopulation();

            var enriched = scores.Select(s =>
            {
                var key = (s.SongId, s.Instrument);
                var computedRank = rankings.GetValueOrDefault(key, 0);
                // Always use DB-computed rank for consistency with leaderboard ordering
                var rank = computedRank > 0 ? computedRank : s.Rank;
                var totalEntries = population.TryGetValue(key, out var pop) && pop > 0 ? (int)pop : 0;
                return new
                {
                    s.SongId,
                    s.Instrument,
                    s.Score,
                    s.Accuracy,
                    s.IsFullCombo,
                    s.Stars,
                    s.Season,
                    s.Percentile,
                    Rank = rank,
                    s.EndTime,
                    TotalEntries = totalEntries,
                };
            }).ToList();

            return Results.Ok(new
            {
                accountId,
                displayName,
                totalScores = enriched.Count,
                scores = enriched
            });
        })
        .WithTags("Players")
        .RequireRateLimiting("public");

        // ─── Web tracking: register + backfill for web-viewed players ────

        app.MapPost("/api/player/{accountId}/track", (
            string accountId,
            MetaDatabase metaDb,
            FestivalService festivalService,
            ScoreBackfiller backfiller,
            HistoryReconstructor historyReconstructor,
            PersonalDbBuilder personalDbBuilder,
            NotificationService notifications,
            TokenManager tokenManager,
            IOptions<ScraperOptions> scraperOptions,
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
                        var dop = scraperOptions.Value.PageConcurrency;
                        var accessToken = await tokenManager.GetAccessTokenAsync(CancellationToken.None);
                        if (accessToken is null)
                        {
                            log.LogWarning("Track-triggered backfill for {AccountId}: no access token available.", accountId);
                            return;
                        }
                        var callerAccountId = tokenManager.AccountId!;

                        if (festivalService.Songs.Count == 0)
                            await festivalService.InitializeAsync();

                        int initialDop = Math.Max(1, dop / 2);
                        using var limiter = new FortniteFestival.Core.Scraping.AdaptiveConcurrencyLimiter(
                            initialDop, minDop: 2, maxDop: dop,
                            loggerFactory.CreateLogger("PlayerBackfillLimiter"));

                        await backfiller.BackfillAccountAsync(
                            accountId, festivalService, accessToken, callerAccountId, limiter, dop, CancellationToken.None);

                        // Reconstruct score history
                        var reconStatus = metaDb.GetHistoryReconStatus(accountId);
                        if (reconStatus?.Status != "complete")
                        {
                            var seasonWindows = await historyReconstructor.DiscoverSeasonWindowsAsync(
                                accessToken, callerAccountId, CancellationToken.None);
                            if (seasonWindows.Count > 0)
                            {
                                await historyReconstructor.ReconstructAccountAsync(
                                    accountId, seasonWindows, accessToken, callerAccountId, limiter, dop,
                                    CancellationToken.None);
                            }
                        }

                        // Rebuild personal DB and notify
                        var accountSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { accountId };
                        personalDbBuilder.RebuildForAccounts(accountSet, metaDb);
                        try { await notifications.NotifyPersonalDbReadyAsync(accountId); }
                        catch { /* best effort */ }

                        log.LogInformation("Track-triggered backfill for {AccountId} completed.", accountId);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "Track-triggered backfill for {AccountId} failed.", accountId);
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
            string accountId,
            MetaDatabase metaDb) =>
        {
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
            string accountId,
            MetaDatabase metaDb) =>
        {
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
            string accountId,
            int? limit,
            string? songId,
            string? instrument,
            MetaDatabase metaDb) =>
        {
            // Check if the account is a registered user
            var registeredIds = metaDb.GetRegisteredAccountIds();
            if (!registeredIds.Contains(accountId))
            {
                return Results.NotFound(new
                {
                    error = "Score history is only available for registered users."
                });
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
