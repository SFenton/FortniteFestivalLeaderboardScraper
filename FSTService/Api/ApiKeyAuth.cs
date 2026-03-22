using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FSTService.Api;

/// <summary>
/// Configuration for the API key authentication scheme.
/// </summary>
public sealed class ApiSettings
{
    public const string Section = "Api";

    /// <summary>
    /// The API key required for protected endpoints.
    /// Set via environment variable <c>Api__ApiKey</c> or appsettings.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Allowed CORS origins. Use specific origins in production, not "*".
    /// </summary>
    public string[] AllowedOrigins { get; set; } = ["http://localhost:3000"];
}

/// <summary>
/// Custom authentication handler that validates an API key from the
/// <c>X-API-Key</c> header. Used for protected endpoints only.
/// </summary>
public sealed class ApiKeyAuthHandler : AuthenticationHandler<ApiKeyAuthOptions>
{
    private const string ApiKeyHeaderName = "X-API-Key";
    private readonly ILogger<ApiKeyAuthHandler> _log;

    public ApiKeyAuthHandler(
        IOptionsMonitor<ApiKeyAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
        _log = logger.CreateLogger<ApiKeyAuthHandler>();
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (string.IsNullOrEmpty(Options.ApiKey))
        {
            _log.LogWarning("API key not configured on server — rejecting protected request to {Path}.",
                Request.Path);
            return Task.FromResult(AuthenticateResult.Fail("API key not configured on server."));
        }

        if (!Request.Headers.TryGetValue(ApiKeyHeaderName, out var providedKey))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!string.Equals(providedKey, Options.ApiKey, StringComparison.Ordinal))
        {
            _log.LogWarning("Invalid API key for {Method} {Path} from {RemoteIp}.",
                Request.Method, Request.Path, Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var claims = new[] { new Claim(ClaimTypes.Name, "api-client") };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>
/// Options for the API key authentication scheme.
/// </summary>
public sealed class ApiKeyAuthOptions : AuthenticationSchemeOptions
{
    public string ApiKey { get; set; } = "";
}
