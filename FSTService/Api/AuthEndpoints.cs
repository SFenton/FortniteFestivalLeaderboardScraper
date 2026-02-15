using FSTService.Auth;
using FSTService.Persistence;

namespace FSTService.Api;

/// <summary>
/// Maps authentication endpoints: login, refresh, logout, and current-user info.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // ─── POST /api/auth/login ───────────────────────────

        app.MapPost("/api/auth/login", (LoginRequest request, UserAuthService auth) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.DeviceId))
                return Results.BadRequest(new { error = "username and deviceId are required." });

            var result = auth.Login(request.Username.Trim(), request.DeviceId.Trim(), request.Platform?.Trim());

            return Results.Ok(new
            {
                accessToken     = result.AccessToken,
                refreshToken    = result.RefreshToken,
                expiresIn       = result.ExpiresIn,
                accountId       = result.AccountId,
                displayName     = result.DisplayName,
                personalDbReady = result.PersonalDbReady,
            });
        })
        .WithTags("Auth")
        .RequireRateLimiting("auth");

        // ─── POST /api/auth/refresh ─────────────────────────

        app.MapPost("/api/auth/refresh", (RefreshRequest request, UserAuthService auth) =>
        {
            if (string.IsNullOrWhiteSpace(request.RefreshToken))
                return Results.BadRequest(new { error = "refreshToken is required." });

            var result = auth.Refresh(request.RefreshToken);
            if (result is null)
                return Results.Unauthorized();

            return Results.Ok(new
            {
                accessToken  = result.AccessToken,
                refreshToken = result.RefreshToken,
                expiresIn    = result.ExpiresIn,
            });
        })
        .WithTags("Auth")
        .RequireRateLimiting("auth");

        // ─── POST /api/auth/logout ──────────────────────────

        app.MapPost("/api/auth/logout", (LogoutRequest request, UserAuthService auth) =>
        {
            if (!string.IsNullOrWhiteSpace(request.RefreshToken))
                auth.Logout(request.RefreshToken);

            return Results.NoContent();
        })
        .WithTags("Auth")
        .RequireRateLimiting("auth");

        // ─── GET /api/auth/me ───────────────────────────────

        app.MapGet("/api/auth/me", (HttpContext context, MetaDatabase metaDb) =>
        {
            var username = context.User.FindFirst("sub")?.Value;
            var deviceId = context.User.FindFirst("deviceId")?.Value;

            if (username is null || deviceId is null)
                return Results.Unauthorized();

            var info = metaDb.GetRegistrationInfo(username, deviceId);
            if (info is null)
                return Results.NotFound(new { error = "User not found." });

            return Results.Ok(new
            {
                username     = info.AccountId,
                accountId    = metaDb.GetAccountIdForUsername(username),
                displayName  = info.DisplayName,
                registeredAt = info.RegisteredAt,
                lastLoginAt  = info.LastLoginAt,
            });
        })
        .WithTags("Auth")
        .RequireAuthorization(policy =>
        {
            policy.AuthenticationSchemes.Add("Bearer");
            policy.RequireAuthenticatedUser();
        });
    }
}

// ─── Request DTOs ───────────────────────────────────────────

public sealed record LoginRequest(string? Username, string? DeviceId, string? Platform);
public sealed record RefreshRequest(string? RefreshToken);
public sealed record LogoutRequest(string? RefreshToken);
