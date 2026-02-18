using FSTService.Auth;
using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

/// <summary>
/// Maps authentication endpoints: login, refresh, logout, current-user info,
/// and the Epic OAuth callback redirect.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // ─── GET /api/auth/epiccallback ─────────────────────
        // Epic redirects here after the user authorizes. We 302-redirect
        // to the mobile app's deep link, passing the authorization code.

        app.MapGet("/api/auth/epiccallback", (HttpContext context, IOptions<EpicOAuthSettings> settings) =>
        {
            var code = context.Request.Query["code"].FirstOrDefault();
            if (string.IsNullOrEmpty(code))
                return Results.BadRequest(new { error = "Missing 'code' query parameter." });

            var deepLink = settings.Value.AppDeepLink.TrimEnd('/');
            var redirectUrl = $"{deepLink}?code={Uri.EscapeDataString(code)}";
            return Results.Redirect(redirectUrl);
        })
        .WithTags("Auth")
        .RequireRateLimiting("auth");

        // ─── POST /api/auth/login ───────────────────────────

        app.MapPost("/api/auth/login", async (LoginRequest request, UserAuthService auth) =>
        {
            if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.DeviceId))
                return Results.BadRequest(new { error = "code and deviceId are required." });

            try
            {
                var result = await auth.LoginAsync(
                    request.Code.Trim(), request.DeviceId.Trim(), request.Platform?.Trim());

                return Results.Ok(new
                {
                    accessToken     = result.AccessToken,
                    refreshToken    = result.RefreshToken,
                    expiresIn       = result.ExpiresIn,
                    accountId       = result.AccountId,
                    displayName     = result.DisplayName,
                    personalDbReady = result.PersonalDbReady,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
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

public sealed record LoginRequest(string? Code, string? DeviceId, string? Platform);
public sealed record RefreshRequest(string? RefreshToken);
public sealed record LogoutRequest(string? RefreshToken);
