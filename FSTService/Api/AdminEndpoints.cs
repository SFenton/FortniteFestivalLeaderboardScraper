using FortniteFestival.Core.Services;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/api/status", (
            GlobalLeaderboardPersistence persistence,
            MetaDatabase metaDb) =>
        {
            var lastRun = metaDb.GetLastCompletedScrapeRun();
            var counts = persistence.GetEntryCounts();
            var totalEntries = counts.Values.Sum();

            return Results.Ok(new
            {
                lastScrape = lastRun is null ? null : new
                {
                    id            = lastRun.Id,
                    startedAt     = lastRun.StartedAt,
                    completedAt   = lastRun.CompletedAt,
                    songsScraped  = lastRun.SongsScraped,
                    totalEntries  = lastRun.TotalEntries,
                    totalRequests = lastRun.TotalRequests,
                    totalBytes    = lastRun.TotalBytes,
                },
                instruments = counts,
                totalEntries
            });
        })
        .WithTags("Status")
        .RequireAuthorization()
        .RequireRateLimiting("protected");

        app.MapGet("/api/admin/epic-token", async (TokenManager tokenManager, CancellationToken ct) =>
        {
            var accessToken = await tokenManager.GetAccessTokenAsync(ct);
            if (accessToken is null)
                return Results.Problem("No access token available. Service may need re-authentication.");

            return Results.Ok(new
            {
                accessToken,
                accountId = tokenManager.AccountId,
                displayName = tokenManager.DisplayName,
                expiresAt = tokenManager.ExpiresAt,
            });
        })
        .WithTags("Admin")
        .RequireAuthorization()
        .RequireRateLimiting("protected");

        app.MapPost("/api/admin/shop/refresh", async (ItemShopService shopService, ILogger<ItemShopService> log, CancellationToken ct) =>
        {
            try
            {
                var result = await shopService.TriggerScrapeAsync(ct);
                return Results.Ok(new
                {
                    success = result >= 0,
                    matchedCount = result >= 0 ? result : shopService.InShopSongIds.Count,
                    contentChanged = result >= 0,
                    scrapedAt = shopService.LastScrapedAt,
                });
            }
            catch (HttpRequestException ex)
            {
                log.LogError(ex, "Shop scrape failed: {Message}", ex.Message);
                return Results.Json(new
                {
                    success = false,
                    error = ex.Message,
                    scrapedAt = shopService.LastScrapedAt,
                }, statusCode: 502);
            }
        })
        .WithTags("Admin")
        .RequireAuthorization()
        .RequireRateLimiting("protected");

        app.MapPost("/api/register", (
            RegisterRequest request,
            MetaDatabase metaDb,
            PersonalDbBuilder personalDbBuilder) =>
        {
            if (string.IsNullOrWhiteSpace(request.DeviceId) ||
                string.IsNullOrWhiteSpace(request.Username))
            {
                return Results.BadRequest(new { error = "deviceId and username are required." });
            }

            // Look up the Epic account ID by display name (case-insensitive)
            var accountId = metaDb.GetAccountIdForUsername(request.Username.Trim());
            if (accountId is null)
            {
                return Results.Ok(new
                {
                    registered = false,
                    error = "no_account_found",
                    description = "No Epic Games account was found for that name. Please check spelling and try again.",
                });
            }

            var displayName = metaDb.GetDisplayName(accountId);
            var isNew = metaDb.RegisterUser(request.DeviceId, accountId);

            // Build the personal DB immediately on registration
            string? dbPath = null;
            if (isNew)
            {
                dbPath = personalDbBuilder.Build(request.DeviceId, accountId);
            }

            return Results.Ok(new
            {
                registered = isNew,
                deviceId = request.DeviceId,
                accountId,
                displayName,
                personalDbReady = dbPath is not null,
            });
        })
        .WithTags("Registration")
        .RequireAuthorization()
        .RequireRateLimiting("protected");

        app.MapDelete("/api/register", (
            string deviceId,
            string accountId,
            MetaDatabase metaDb,
            PersonalDbBuilder personalDbBuilder) =>
        {
            if (string.IsNullOrWhiteSpace(deviceId) ||
                string.IsNullOrWhiteSpace(accountId))
            {
                return Results.BadRequest(new { error = "deviceId and accountId query parameters are required." });
            }

            var removed = metaDb.UnregisterUser(deviceId, accountId);

            // Clean up the personal DB file if it exists
            if (removed)
            {
                var dbPath = personalDbBuilder.GetPersonalDbPath(accountId, deviceId);
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }

            return Results.Ok(new
            {
                unregistered = removed,
                deviceId,
                accountId,
            });
        })
        .WithTags("Registration")
        .RequireAuthorization()
        .RequireRateLimiting("protected");

        // ─── FirstSeenSeason endpoints ────────────────────

        app.MapGet("/api/firstseen", (MetaDatabase metaDb) =>
        {
            var all = metaDb.GetAllFirstSeenSeasons();
            var songs = all.Select(kvp => new
            {
                songId = kvp.Key,
                firstSeenSeason = kvp.Value.FirstSeenSeason,
                estimatedSeason = kvp.Value.EstimatedSeason,
            }).ToList();
            return Results.Ok(new { count = songs.Count, songs });
        })
        .WithTags("FirstSeenSeason")
        .RequireRateLimiting("public");

        app.MapPost("/api/firstseen/calculate", async (
            FirstSeenSeasonCalculator calculator,
            FestivalService festivalService,
            TokenManager tokenManager,
            IOptions<ScraperOptions> scraperOptions,
            CancellationToken ct) =>
        {
            var dop = scraperOptions.Value.PageConcurrency;

            var accessToken = await tokenManager.GetAccessTokenAsync(ct);
            if (accessToken is null)
                return Results.Problem("No access token available. Service may need re-authentication.");

            var callerAccountId = tokenManager.AccountId!;

            if (festivalService.Songs.Count == 0)
            {
                await festivalService.InitializeAsync();
                if (festivalService.Songs.Count == 0)
                    return Results.Problem("Song catalog is empty.");
            }

            var calculated = await calculator.CalculateAsync(
                festivalService, accessToken, callerAccountId, dop, ct);

            return Results.Ok(new
            {
                songsCalculated = calculated,
                message = calculated > 0
                    ? $"Calculated FirstSeenSeason for {calculated} song(s)."
                    : "All songs already have FirstSeenSeason set."
            });
        })
        .WithTags("FirstSeenSeason")
        .RequireAuthorization()
        .RequireRateLimiting("protected");

        // ── Path regeneration (admin) ───────────────────────────
        app.MapPost("/api/admin/regenerate-paths", async (
            string? songId,
            bool? force,
            PathGenerator pathGenerator,
            PathDataStore pathStore,
            FestivalService festivalService,
            ScrapeProgressTracker progress,
            IHostApplicationLifetime lifetime,
            IOptions<ScraperOptions> scraperOptions,
            ILogger<PathGenerator> logger) =>
        {
            if (!scraperOptions.Value.EnablePathGeneration)
                return Results.BadRequest(new { error = "Path generation is disabled." });

            if (festivalService.Songs.Count == 0)
            {
                await festivalService.InitializeAsync();
                if (festivalService.Songs.Count == 0)
                    return Results.Problem("Song catalog is empty.");
            }

            var existingState = pathStore.GetPathGenerationState();
            var allSongs = festivalService.Songs
                .Where(s => s.track?.su is not null && !string.IsNullOrEmpty(s.track.mu))
                .Where(s => songId is null || s.track.su == songId)
                .ToList();

            var songs = allSongs.Select(s =>
            {
                existingState.TryGetValue(s.track.su, out var state);
                return new PathGenerator.SongPathRequest(
                    s.track.su,
                    s.track.tt ?? s.track.su,
                    s.track.an ?? "Unknown",
                    s.track.mu,
                    s.lastModified == DateTime.MinValue ? null : s.lastModified,
                    state.Hash,
                    state.LastModified);
            }).ToList();

            if (songs.Count == 0)
                return Results.NotFound(new { error = "No matching songs found." });

            // Fire-and-forget — use app shutdown token, not the request token
            // which gets cancelled as soon as the 202 response is sent.
            var appStopping = lifetime.ApplicationStopping;
            _ = Task.Run(async () =>
            {
                progress.BeginPathGeneration(songs.Count);
                try
                {
                    var results = await pathGenerator.GeneratePathsAsync(songs, force ?? false, appStopping);
                    foreach (var result in results)
                    {
                        var scores = new SongMaxScores
                        {
                            GeneratedAt = DateTime.UtcNow.ToString("o"),
                            CHOptVersion = "1.10.3",
                        };
                        foreach (var pr in result.Results.Where(r => r.Difficulty == "expert"))
                            scores.SetByInstrument(pr.Instrument, pr.MaxScore);
                        var songEntry = allSongs.FirstOrDefault(s => s.track.su == result.SongId);
                        var lastMod = songEntry?.lastModified is { } lm && lm != DateTime.MinValue ? lm.ToString("o") : null;
                        pathStore.UpdateMaxScores(result.SongId, scores, result.DatFileHash, lastMod);
                    }
                    progress.EndPathGeneration();
                    logger.LogInformation("Admin path regeneration complete: {Count} song(s) updated.", results.Count);
                }
                catch (Exception ex)
                {
                    progress.EndPathGeneration();
                    logger.LogError(ex, "Admin path regeneration failed.");
                }
            }, appStopping);

            return Results.Accepted(value: new
            {
                message = songId is not null
                    ? $"Path regeneration started for song {songId}."
                    : $"Path regeneration started for {songs.Count} song(s).",
                force = force ?? false,
            });
        })
        .WithTags("Paths")
        .RequireAuthorization()
        .RequireRateLimiting("protected");

        app.MapGet("/api/backfill/{accountId}/status", (
            string accountId,
            MetaDatabase metaDb) =>
        {
            var status = metaDb.GetBackfillStatus(accountId);
            if (status is null)
                return Results.NotFound(new { error = "No backfill found for this account." });

            return Results.Ok(new
            {
                accountId  = status.AccountId,
                status     = status.Status,
                songsChecked     = status.SongsChecked,
                totalSongsToCheck = status.TotalSongsToCheck,
                entriesFound     = status.EntriesFound,
                startedAt        = status.StartedAt,
                completedAt      = status.CompletedAt,
                errorMessage     = status.ErrorMessage,
            });
        })
        .WithTags("Backfill")
        .RequireAuthorization()
        .RequireRateLimiting("protected");

        app.MapPost("/api/backfill/{accountId}", async (
            string accountId,
            ScoreBackfiller backfiller,
            HistoryReconstructor historyReconstructor,
            PersonalDbBuilder personalDbBuilder,
            FestivalService festivalService,
            TokenManager tokenManager,
            MetaDatabase metaDb,
            IOptions<ScraperOptions> scraperOptions,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var dop = scraperOptions.Value.PageConcurrency;
            // Verify the account is registered
            var registeredIds = metaDb.GetRegisteredAccountIds();
            if (!registeredIds.Contains(accountId))
            {
                return Results.NotFound(new { error = "Account is not registered." });
            }

            // Need a valid access token to call Epic's API
            var accessToken = await tokenManager.GetAccessTokenAsync(ct);
            if (accessToken is null)
                return Results.Problem("No access token available. Service may need re-authentication.");

            var callerAccountId = tokenManager.AccountId!;

            // Ensure song catalog is loaded
            if (festivalService.Songs.Count == 0)
            {
                await festivalService.InitializeAsync();
                if (festivalService.Songs.Count == 0)
                    return Results.Problem("Song catalog is empty. Cannot run backfill.");
            }

            // ── Step 1: Backfill missing scores ──
            int initialDop = Math.Max(1, dop / 2);
            using var limiter = new FortniteFestival.Core.Scraping.AdaptiveConcurrencyLimiter(
                initialDop, minDop: 2, maxDop: dop,
                loggerFactory.CreateLogger("AdminBackfillLimiter"));

            var found = await backfiller.BackfillAccountAsync(
                accountId, festivalService, accessToken, callerAccountId, limiter, dop, ct);

            var status = metaDb.GetBackfillStatus(accountId);

            // ── Step 2: Reconstruct score history (if not already done) ──
            int historyEntries = 0;
            var reconStatus = metaDb.GetHistoryReconStatus(accountId);
            if (reconStatus?.Status != "complete")
            {
                var seasonWindows = await historyReconstructor.DiscoverSeasonWindowsAsync(
                    accessToken, callerAccountId, ct);

                if (seasonWindows.Count > 0)
                {
                    historyEntries = await historyReconstructor.ReconstructAccountAsync(
                        accountId, seasonWindows, accessToken, callerAccountId, limiter, dop, ct);
                }
            }

            // ── Step 3: Rebuild personal DB ──
            var accountSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { accountId };
            var personalDbsRebuilt = personalDbBuilder.RebuildForAccounts(accountSet, metaDb);

            return Results.Ok(new
            {
                accountId,
                newEntriesFound = found,
                status = status?.Status,
                songsChecked = status?.SongsChecked,
                totalSongsToCheck = status?.TotalSongsToCheck,
                entriesFound = status?.EntriesFound,
                historyEntriesCreated = historyEntries,
                personalDbsRebuilt,
            });
        })
        .WithTags("Backfill")
        .RequireAuthorization()
        .RequireRateLimiting("protected");



        app.MapGet("/api/leaderboard-population", (MetaDatabase metaDb) =>
        {
            var data = metaDb.GetAllLeaderboardPopulation();
            var result = data.Select(kv => new
            {
                songId = kv.Key.SongId,
                instrument = kv.Key.Instrument,
                totalEntries = kv.Value,
            });
            return Results.Ok(result);
        })
        .WithTags("Leaderboard")
        .RequireAuthorization()
        .RequireRateLimiting("protected");
    }
}
