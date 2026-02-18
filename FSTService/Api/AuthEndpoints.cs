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
        // to either:
        //   • A localhost loopback URL (for Windows desktop clients), or
        //   • The mobile app's deep-link scheme (for iOS/Android).
        //
        // Windows clients embed a `return_to` URL inside the OAuth `state`
        // parameter (base64-encoded JSON).  If present and it points to
        // localhost, we redirect there instead of the configured deep link.

        app.MapGet("/api/auth/epiccallback", (HttpContext context, IOptions<EpicOAuthSettings> settings) =>
        {
            var code = context.Request.Query["code"].FirstOrDefault();
            if (string.IsNullOrEmpty(code))
                return Results.BadRequest(new { error = "Missing 'code' query parameter." });

            // Check for a loopback return_to URL in the state parameter.
            var returnTo = TryExtractLoopbackReturnTo(context.Request.Query["state"].FirstOrDefault());

            string redirectUrl;
            if (returnTo is not null)
            {
                // Windows loopback: redirect directly to localhost with the code.
                redirectUrl = $"{returnTo}?code={Uri.EscapeDataString(code)}";
            }
            else
            {
                // Mobile deep link (default).
                var deepLink = settings.Value.AppDeepLink.TrimEnd('/');
                redirectUrl = $"{deepLink}?code={Uri.EscapeDataString(code)}";
            }

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

    // ─── Helpers ────────────────────────────────────────────────

    /// <summary>
    /// Extracts a loopback return URL from the OAuth <c>state</c> parameter.
    /// <para>
    /// Windows desktop clients encode a base64 JSON payload in <c>state</c>:
    /// <c>{ "return_to": "http://localhost:8400/auth/callback" }</c>.
    /// </para>
    /// <para>
    /// For security, only <c>http://localhost</c> (with any port) is accepted.
    /// Returns <c>null</c> if the state is absent, malformed, or points to a
    /// non-localhost host.
    /// </para>
    /// </summary>
    internal static string? TryExtractLoopbackReturnTo(string? state)
    {
        if (string.IsNullOrEmpty(state))
            return null;

        try
        {
            // Base64-decode the state parameter.
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(state));
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("return_to", out var returnToProp))
                return null;

            var returnTo = returnToProp.GetString();
            if (string.IsNullOrEmpty(returnTo))
                return null;

            // Only allow localhost (any port). This prevents open redirect attacks.
            if (!Uri.TryCreate(returnTo, UriKind.Absolute, out var uri))
                return null;

            if (uri.Scheme != "http" || uri.Host != "localhost")
                return null;

            // Return the URL without any query string (we append ?code= ourselves).
            return $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath.TrimEnd('/')}";
        }
        catch
        {
            // Malformed state — ignore and fall through to default deep link.
            return null;
        }
    }
}

// ─── Request DTOs ───────────────────────────────────────────

public sealed record LoginRequest(string? Code, string? DeviceId, string? Platform);
public sealed record RefreshRequest(string? RefreshToken);
public sealed record LogoutRequest(string? RefreshToken);
