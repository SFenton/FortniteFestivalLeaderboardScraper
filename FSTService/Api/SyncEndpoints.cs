using System.Net.WebSockets;
using FSTService.Auth;
using FSTService.Persistence;

namespace FSTService.Api;

public static partial class ApiEndpoints
{
    public static void MapSyncEndpoints(this WebApplication app)
    {
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
    }
}
