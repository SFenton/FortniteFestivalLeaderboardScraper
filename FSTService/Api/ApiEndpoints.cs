using System.Net.Http.Headers;
using System.Net.WebSockets;
using FortniteFestival.Core.Services;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

/// <summary>
/// Maps all HTTP API endpoints onto the <see cref="WebApplication"/>.
/// Endpoints are split into public (no auth) and protected (API key required).
/// </summary>
public static class ApiEndpoints
{
    public static void MapApiEndpoints(this WebApplication app)
    {
        // ─── Public endpoints ───────────────────────────────

        app.MapGet("/healthz", () => Results.Ok("ok"))
           .WithTags("Health")
           .RequireRateLimiting("public");

        // Check if an account exists by username (used by mobile app before login)
        app.MapGet("/api/account/check", (string username, MetaDatabase metaDb) =>
        {
            if (string.IsNullOrWhiteSpace(username))
                return Results.BadRequest(new { error = "username query parameter is required." });

            var accountId = metaDb.GetAccountIdForUsername(username.Trim());
            return Results.Ok(new
            {
                exists = accountId is not null,
                accountId,
                displayName = accountId is not null ? metaDb.GetDisplayName(accountId) : null,
            });
        })
        .WithTags("Account")
        .RequireRateLimiting("public");

        app.MapGet("/api/progress", (ScrapeProgressTracker tracker) =>
        {
            return Results.Ok(tracker.GetProgressResponse());
        })
        .WithTags("Progress")
        .RequireRateLimiting("public");

        app.MapGet("/api/songs", (FestivalService service) =>
        {
            var songs = service.Songs
                .Where(s => s.track?.su is not null)
                .Select(s => new
                {
                    songId     = s.track.su,
                    title      = s.track.tt,
                    artist     = s.track.an,
                    album      = s.track.ab,
                    year       = s.track.ry,
                    tempo      = s.track.mt,
                    genres     = s.track.ge,
                    difficulty = s.track.@in is null ? null : new
                    {
                        guitar     = s.track.@in.gr,
                        bass       = s.track.@in.ba,
                        vocals     = s.track.@in.vl,
                        drums      = s.track.@in.ds,
                        proGuitar  = s.track.@in.pg,
                        proBass    = s.track.@in.pb,
                    }
                })
                .ToList();

            return Results.Ok(new { count = songs.Count, songs });
        })
        .WithTags("Songs")
        .RequireRateLimiting("public");

        app.MapGet("/api/leaderboard/{songId}/{instrument}", (
            string songId,
            string instrument,
            int? top,
            GlobalLeaderboardPersistence persistence) =>
        {
            var entries = persistence.GetLeaderboard(songId, instrument, top);
            if (entries is null)
                return Results.NotFound(new { error = $"Unknown instrument: {instrument}" });

            return Results.Ok(new
            {
                songId,
                instrument,
                count = entries.Count,
                entries
            });
        })
        .WithTags("Leaderboards")
        .RequireRateLimiting("public");

        app.MapGet("/api/player/{accountId}", (
            string accountId,
            GlobalLeaderboardPersistence persistence,
            MetaDatabase metaDb) =>
        {
            var scores = persistence.GetPlayerProfile(accountId);
            var displayName = metaDb.GetDisplayName(accountId);

            return Results.Ok(new
            {
                accountId,
                displayName,
                totalScores = scores.Count,
                scores
            });
        })
        .WithTags("Players")
        .RequireRateLimiting("public");

        // ─── Protected endpoints (require API key) ──────────

        app.MapPost("/api/auth/device-code", async (TokenManager tokenManager, CancellationToken ct) =>
        {
            try
            {
                var deviceAuth = await tokenManager.StartDeviceCodeFlowAsync(ct);

                // Fire-and-forget: poll in background until the user completes login.
                // CompletePollAsync handles errors internally (timeout → returns false).
                _ = tokenManager.CompletePollAsync(deviceAuth, CancellationToken.None);

                return Results.Ok(new
                {
                    userCode = deviceAuth.UserCode,
                    verificationUri = deviceAuth.VerificationUri,
                    verificationUriComplete = deviceAuth.VerificationUriComplete,
                    expiresIn = deviceAuth.ExpiresIn,
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 502,
                    title: "Failed to start device code flow");
            }
        })
        .WithTags("Auth")
        .RequireAuthorization()
        .RequireRateLimiting("protected");

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

        app.MapGet("/api/player/{accountId}/history", (
            string accountId,
            int? limit,
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

            var history = metaDb.GetScoreHistory(accountId, limit ?? 100);
            return Results.Ok(new
            {
                accountId,
                count = history.Count,
                history
            });
        })
        .WithTags("Players")
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
            var dop = scraperOptions.Value.DegreeOfParallelism;

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
            CancellationToken ct) =>
        {
            var dop = scraperOptions.Value.DegreeOfParallelism;
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
            var found = await backfiller.BackfillAccountAsync(
                accountId, festivalService, accessToken, callerAccountId, dop, ct);

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
                        accountId, seasonWindows, accessToken, callerAccountId, dop, ct: ct);
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

        // ─── Leaderboard population (posted by PercentileService) ────

        app.MapPost("/api/leaderboard-population", async (
            LeaderboardPopulationRequest[] items,
            MetaDatabase metaDb,
            ScrapeProgressTracker progress,
            PersonalDbBuilder personalDbBuilder,
            NotificationService notifications,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("FSTService.Api.ApiEndpoints");
            if (items.Length == 0)
                return Results.BadRequest(new { error = "Empty array." });

            var tuples = items
                .Where(i => !string.IsNullOrWhiteSpace(i.SongId) &&
                            !string.IsNullOrWhiteSpace(i.Instrument) &&
                            i.TotalEntries > 0)
                .Select(i => (i.SongId, i.Instrument, i.TotalEntries))
                .ToList();

            metaDb.UpsertLeaderboardPopulation(tuples);

            // If a scrape is in progress the pipeline will rebuild personal DBs
            // during its post-processing phase. When idle, trigger it now so
            // registered users get fresh population data without waiting.
            int personalDbsRebuilt = 0;
            bool refreshTriggered = false;

            if (progress.Phase == ScrapeProgressTracker.ScrapePhase.Idle)
            {
                var registeredIds = metaDb.GetRegisteredAccountIds();
                if (registeredIds.Count > 0)
                {
                    refreshTriggered = true;
                    try
                    {
                        personalDbsRebuilt = personalDbBuilder.RebuildForAccounts(registeredIds, metaDb);
                        logger.LogInformation(
                            "Leaderboard population POST triggered rebuild of {Count} personal DB(s).",
                            personalDbsRebuilt);

                        foreach (var accountId in registeredIds)
                        {
                            try { await notifications.NotifyPersonalDbReadyAsync(accountId); }
                            catch { /* best effort */ }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Personal DB rebuild after population POST failed.");
                    }
                }
            }

            return Results.Ok(new { upserted = tuples.Count, refreshTriggered, personalDbsRebuilt });
        })
        .WithTags("Leaderboard")
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

        // ─── Sync endpoints (protected, require API key) ────

        app.MapGet("/api/sync/{deviceId}/version", (
            string deviceId,
            MetaDatabase metaDb,
            PersonalDbBuilder personalDbBuilder) =>
        {
            if (!metaDb.IsDeviceRegistered(deviceId))
                return Results.NotFound(new { error = "Device not registered." });

            var accountId = metaDb.GetAccountForDevice(deviceId);
            if (accountId is null)
                return Results.NotFound(new { error = "No account registered for this device." });

            var (version, sizeBytes) = personalDbBuilder.GetVersion(accountId, deviceId);
            return Results.Ok(new
            {
                deviceId,
                available = version is not null,
                version,
                sizeBytes,
            });
        })
        .WithTags("Sync")
        .RequireAuthorization()
        .RequireRateLimiting("protected");

        app.MapGet("/api/sync/{deviceId}", (
            string deviceId,
            MetaDatabase metaDb,
            PersonalDbBuilder personalDbBuilder) =>
        {
            if (!metaDb.IsDeviceRegistered(deviceId))
                return Results.NotFound(new { error = "Device not registered." });

            var accountId = metaDb.GetAccountForDevice(deviceId);
            if (accountId is null)
                return Results.NotFound(new { error = "No account registered for this device." });

            var dbPath = personalDbBuilder.GetPersonalDbPath(accountId, deviceId);
            if (!File.Exists(dbPath))
            {
                // Build on demand if not yet generated
                var built = personalDbBuilder.Build(deviceId, accountId);
                if (built is null)
                    return Results.StatusCode(503); // Service Unavailable
            }

            // Update last sync timestamp
            metaDb.UpdateLastSync(deviceId, accountId);

            // Serve the file
            var stream = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Results.File(stream, "application/x-sqlite3", $"{deviceId}.db");
        })
        .WithTags("Sync")
        .RequireAuthorization()
        .RequireRateLimiting("protected");

        // ─── Bearer-auth endpoints (for mobile app) ─────────

        app.MapGet("/api/me/sync", (
            HttpContext context,
            MetaDatabase metaDb,
            PersonalDbBuilder personalDbBuilder) =>
        {
            var deviceId = context.User.FindFirst("deviceId")?.Value;
            var username = context.User.FindFirst("sub")?.Value;
            if (deviceId is null || username is null)
                return Results.Unauthorized();

            var accountId = metaDb.GetAccountIdForUsername(username);
            if (accountId is null)
                return Results.NotFound(new { error = "Account not found." });

            var dbPath = personalDbBuilder.GetPersonalDbPath(accountId, deviceId);
            if (!File.Exists(dbPath))
            {
                // Build on demand if not yet generated
                var built = personalDbBuilder.Build(deviceId, accountId);
                if (built is null)
                    return Results.StatusCode(503);
            }

            metaDb.UpdateLastSync(deviceId, accountId);

            var stream = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Results.File(stream, "application/x-sqlite3", $"{deviceId}.db");
        })
        .WithTags("Sync")
        .RequireAuthorization(policy =>
        {
            policy.AuthenticationSchemes.Add("Bearer");
            policy.RequireAuthenticatedUser();
        })
        .RequireRateLimiting("protected");

        // ── Paged JSON sync endpoints ──────────────────────────────────
        // For platforms without native SQLite (e.g. Windows).
        // Each endpoint returns one page of a specific data type.
        // Response shape: { page, pageSize, totalItems, totalPages, items: [...] }

        app.MapGet("/api/me/sync/json/songs", (
            HttpContext context,
            MetaDatabase metaDb,
            PersonalDbBuilder personalDbBuilder,
            int page = 0,
            int pageSize = 1000) =>
        {
            var deviceId = context.User.FindFirst("deviceId")?.Value;
            var username = context.User.FindFirst("sub")?.Value;
            if (deviceId is null || username is null)
                return Results.Unauthorized();

            pageSize = Math.Clamp(pageSize, 1, 5000);

            var data = personalDbBuilder.GetSongsAsJson(page, pageSize);
            if (data is null)
                return Results.StatusCode(503);

            return Results.Ok(data);
        })
        .WithTags("Sync")
        .RequireAuthorization(policy =>
        {
            policy.AuthenticationSchemes.Add("Bearer");
            policy.RequireAuthenticatedUser();
        })
        .RequireRateLimiting("protected");

        app.MapGet("/api/me/sync/json/scores", (
            HttpContext context,
            MetaDatabase metaDb,
            PersonalDbBuilder personalDbBuilder,
            int page = 0,
            int pageSize = 1000) =>
        {
            var deviceId = context.User.FindFirst("deviceId")?.Value;
            var username = context.User.FindFirst("sub")?.Value;
            if (deviceId is null || username is null)
                return Results.Unauthorized();

            var accountId = metaDb.GetAccountIdForUsername(username);
            if (accountId is null)
                return Results.NotFound(new { error = "Account not found." });

            pageSize = Math.Clamp(pageSize, 1, 5000);

            var data = personalDbBuilder.GetScoresAsJson(accountId, page, pageSize);
            if (data is null)
                return Results.StatusCode(503);

            return Results.Ok(data);
        })
        .WithTags("Sync")
        .RequireAuthorization(policy =>
        {
            policy.AuthenticationSchemes.Add("Bearer");
            policy.RequireAuthenticatedUser();
        })
        .RequireRateLimiting("protected");

        app.MapGet("/api/me/sync/json/history", (
            HttpContext context,
            MetaDatabase metaDb,
            PersonalDbBuilder personalDbBuilder,
            int page = 0,
            int pageSize = 1000) =>
        {
            var deviceId = context.User.FindFirst("deviceId")?.Value;
            var username = context.User.FindFirst("sub")?.Value;
            if (deviceId is null || username is null)
                return Results.Unauthorized();

            var accountId = metaDb.GetAccountIdForUsername(username);
            if (accountId is null)
                return Results.NotFound(new { error = "Account not found." });

            pageSize = Math.Clamp(pageSize, 1, 5000);

            var data = personalDbBuilder.GetHistoryAsJson(accountId, page, pageSize);
            if (data is null)
                return Results.StatusCode(503);

            // Only update last-sync on the final history page (marks sync complete)
            if (data.Page >= data.TotalPages - 1)
            {
                var deviceIdVal = context.User.FindFirst("deviceId")?.Value;
                if (deviceIdVal is not null)
                    metaDb.UpdateLastSync(deviceIdVal, accountId);
            }

            return Results.Ok(data);
        })
        .WithTags("Sync")
        .RequireAuthorization(policy =>
        {
            policy.AuthenticationSchemes.Add("Bearer");
            policy.RequireAuthenticatedUser();
        })
        .RequireRateLimiting("protected");

        app.MapGet("/api/me/sync/version", (
            HttpContext context,
            MetaDatabase metaDb,
            PersonalDbBuilder personalDbBuilder) =>
        {
            var deviceId = context.User.FindFirst("deviceId")?.Value;
            var username = context.User.FindFirst("sub")?.Value;
            if (deviceId is null || username is null)
                return Results.Unauthorized();

            var accountId = metaDb.GetAccountIdForUsername(username);
            if (accountId is null)
                return Results.NotFound(new { error = "Account not found." });

            var (version, sizeBytes) = personalDbBuilder.GetVersion(accountId, deviceId);
            return Results.Ok(new
            {
                deviceId,
                available = version is not null,
                version,
                sizeBytes,
            });
        })
        .WithTags("Sync")
        .RequireAuthorization(policy =>
        {
            policy.AuthenticationSchemes.Add("Bearer");
            policy.RequireAuthenticatedUser();
        })
        .RequireRateLimiting("protected");

        app.MapGet("/api/me/backfill/status", (
            HttpContext context,
            MetaDatabase metaDb) =>
        {
            var username = context.User.FindFirst("sub")?.Value;
            if (username is null)
                return Results.Unauthorized();

            var accountId = metaDb.GetAccountIdForUsername(username);
            if (accountId is null)
                return Results.NotFound(new { error = "Account not found." });

            var backfillStatus = metaDb.GetBackfillStatus(accountId);
            var reconStatus = metaDb.GetHistoryReconStatus(accountId);

            return Results.Ok(new
            {
                accountId,
                backfill = backfillStatus is null ? null : new
                {
                    status = backfillStatus.Status,
                    songsChecked = backfillStatus.SongsChecked,
                    totalSongsToCheck = backfillStatus.TotalSongsToCheck,
                    entriesFound = backfillStatus.EntriesFound,
                    startedAt = backfillStatus.StartedAt,
                    completedAt = backfillStatus.CompletedAt,
                },
                historyRecon = reconStatus is null ? null : new
                {
                    status = reconStatus.Status,
                },
            });
        })
        .WithTags("Backfill")
        .RequireAuthorization(policy =>
        {
            policy.AuthenticationSchemes.Add("Bearer");
            policy.RequireAuthenticatedUser();
        })
        .RequireRateLimiting("protected");

        // ─── WebSocket endpoint for real-time notifications ─

        app.Map("/api/ws", async (
            HttpContext context,
            JwtTokenService jwt,
            MetaDatabase metaDb,
            NotificationService notifications) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
                return Results.BadRequest(new { error = "WebSocket connection expected." });

            // WebSocket clients can't send Authorization headers, so token
            // is passed as a query parameter: /api/ws?token=xxx
            var token = context.Request.Query["token"].FirstOrDefault();
            if (string.IsNullOrEmpty(token))
                return Results.Unauthorized();

            var principal = await jwt.ValidateAccessTokenAsync(token);
            if (principal is null)
                return Results.Unauthorized();

            var username = principal.FindFirst("sub")?.Value;
            var deviceId = principal.FindFirst("deviceId")?.Value;
            if (username is null || deviceId is null)
                return Results.Unauthorized();

            var accountId = metaDb.GetAccountIdForUsername(username);
            if (accountId is null)
                return Results.NotFound(new { error = "Account not found." });

            var ws = await context.WebSockets.AcceptWebSocketAsync();
            await notifications.HandleConnectionAsync(accountId, deviceId, ws, context.RequestAborted);

            return Results.Empty;
        })
        .WithTags("WebSocket");

        // ─── Diagnostic: query FNFestival events ────────────

        app.MapGet("/api/diag/events", async (
            TokenManager tokenManager,
            IHttpClientFactory httpFactory,
            string? gameId) =>
        {
            gameId ??= "FNFestival";
            var accessToken = await tokenManager.GetAccessTokenAsync();
            if (accessToken is null)
                return Results.Problem("No access token available");

            var accountId = tokenManager.AccountId!;
            var url = $"https://events-public-service-live.ol.epicgames.com/api/v1/events/{gameId}/data/{accountId}?showPastEvents=true";

            using var http = httpFactory.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var res = await http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();

            return Results.Content(body, "application/json", statusCode: (int)res.StatusCode);
        })
        .WithTags("Diagnostic")
        .RequireRateLimiting("public");

        // ─── Diagnostic: test arbitrary leaderboard URL pattern ────────────

        app.MapGet("/api/diag/leaderboard", async (
            HttpContext context,
            TokenManager tokenManager,
            IHttpClientFactory httpFactory,
            string eventId,
            string windowId,
            int? version,
            string? gameId,
            string? acct,
            int? fromIndex,
            string? findTeams,
            int? page,
            int? rank,
            string? teamAccountIds) =>
        {
            gameId ??= "FNFestival";
            var accessToken = await tokenManager.GetAccessTokenAsync();
            if (accessToken is null)
                return Results.Problem("No access token available");

            var accountId = tokenManager.AccountId!;
            using var http = httpFactory.CreateClient();

            if (version == 2)
            {
                // V2: POST — build query string from optional params
                var qs = new List<string>();
                if (acct != "false") // acct=false to omit accountId
                    qs.Add($"accountId={accountId}");
                qs.Add($"fromIndex={fromIndex ?? 0}");
                if (findTeams != null)
                    qs.Add($"findTeams={findTeams}");

                var qsStr = string.Join("&", qs);
                var url = $"https://events-public-service-live.ol.epicgames.com/api/v2/games/{gameId}/leaderboards/{eventId}/{windowId}/scores?{qsStr}";
                var teamsJson = string.IsNullOrEmpty(teamAccountIds)
                    ? "{\"teams\":[]}"
                    : $"{{\"teams\":[[\"{teamAccountIds}\"]]}}";
                var body = new System.Net.Http.StringContent(teamsJson, System.Text.Encoding.UTF8, "application/json");
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                req.Content = body;
                var res = await http.SendAsync(req);
                var respBody = await res.Content.ReadAsStringAsync();
                return Results.Content($"{{\"_url\":\"{url}\",\"_status\":{(int)res.StatusCode},\"body\":{respBody}}}", "application/json", statusCode: 200);
            }
            else
            {
                var p = page ?? 0;
                var r = rank ?? 0;
                var teamPart = string.IsNullOrEmpty(teamAccountIds) ? "" : $"&teamAccountIds={teamAccountIds}";
                var url = $"https://events-public-service-live.ol.epicgames.com/api/v1/leaderboards/{gameId}/{eventId}/{windowId}/{accountId}?page={p}&rank={r}{teamPart}&appId=Fortnite&showLiveSessions=false";
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var res = await http.SendAsync(req);
                var respBody = await res.Content.ReadAsStringAsync();
                return Results.Content($"{{\"_url\":\"{url}\",\"_status\":{(int)res.StatusCode},\"body\":{respBody}}}", "application/json", statusCode: 200);
            }
        })
        .WithTags("Diagnostic")
        .RequireRateLimiting("public");
    }
}

/// <summary>
/// Request body for POST /api/register.
/// </summary>
public sealed class RegisterRequest
{
    public string DeviceId { get; set; } = "";
    public string Username { get; set; } = "";
}

/// <summary>
/// Request body item for POST /api/leaderboard-population.
/// </summary>
public sealed class LeaderboardPopulationRequest
{
    public string SongId { get; set; } = "";
    public string Instrument { get; set; } = "";
    public long TotalEntries { get; set; }
}
