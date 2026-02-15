using System.Net.Http.Headers;
using FortniteFestival.Core.Services;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping;

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
                string.IsNullOrWhiteSpace(request.AccountId))
            {
                return Results.BadRequest(new { error = "deviceId and accountId are required." });
            }

            var isNew = metaDb.RegisterUser(request.DeviceId, request.AccountId);

            // Build the personal DB immediately on registration
            string? dbPath = null;
            if (isNew)
            {
                dbPath = personalDbBuilder.Build(request.DeviceId, request.AccountId);
            }

            return Results.Ok(new
            {
                registered = isNew,
                deviceId = request.DeviceId,
                accountId = request.AccountId,
                personalDbReady = dbPath is not null,
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

        // ─── Sync endpoints (protected, require API key) ────

        app.MapGet("/api/sync/{deviceId}/version", (
            string deviceId,
            MetaDatabase metaDb,
            PersonalDbBuilder personalDbBuilder) =>
        {
            if (!metaDb.IsDeviceRegistered(deviceId))
                return Results.NotFound(new { error = "Device not registered." });

            var (version, sizeBytes) = personalDbBuilder.GetVersion(deviceId);
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

            var dbPath = personalDbBuilder.GetPersonalDbPath(deviceId);
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
            int? rank) =>
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
                var body = new System.Net.Http.StringContent("{\"teams\":[]}", System.Text.Encoding.UTF8, "application/json");
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
                var url = $"https://events-public-service-live.ol.epicgames.com/api/v1/leaderboards/{gameId}/{eventId}/{windowId}/{accountId}?page={p}&rank={r}&appId=Fortnite&showLiveSessions=false";
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
    public string AccountId { get; set; } = "";
}
