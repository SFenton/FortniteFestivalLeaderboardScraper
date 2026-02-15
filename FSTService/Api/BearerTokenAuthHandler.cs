using System.Security.Claims;
using System.Text.Encodings.Web;
using FSTService.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

/// <summary>
/// Authentication handler that validates a JWT Bearer token from the
/// <c>Authorization: Bearer {token}</c> header.
/// </summary>
public sealed class BearerTokenAuthHandler : AuthenticationHandler<BearerAuthOptions>
{
    private readonly JwtTokenService _jwt;

    public BearerTokenAuthHandler(
        IOptionsMonitor<BearerAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        JwtTokenService jwt)
        : base(options, logger, encoder)
    {
        _jwt = jwt;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
            return AuthenticateResult.Fail("Missing Authorization header.");

        var headerValue = authHeader.ToString();
        if (!headerValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.Fail("Authorization header is not a Bearer token.");

        var token = headerValue["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token))
            return AuthenticateResult.Fail("Bearer token is empty.");

        var principal = await _jwt.ValidateAccessTokenAsync(token);
        if (principal is null)
            return AuthenticateResult.Fail("Invalid or expired access token.");

        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}

/// <summary>
/// Options for the Bearer token authentication scheme.
/// </summary>
public sealed class BearerAuthOptions : AuthenticationSchemeOptions
{
}
