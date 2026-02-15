using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FSTService.Auth;

/// <summary>
/// Mints and validates JWT access tokens (HS256) and generates opaque refresh tokens.
/// </summary>
public sealed class JwtTokenService
{
    private readonly JwtSettings _settings;
    private readonly SymmetricSecurityKey _signingKey;
    private readonly JsonWebTokenHandler _handler = new();

    public JwtTokenService(IOptions<JwtSettings> settings)
    {
        _settings = settings.Value;

        if (string.IsNullOrEmpty(_settings.Secret) || _settings.Secret.Length < 32)
            throw new InvalidOperationException(
                "Jwt:Secret must be configured with at least 32 characters. " +
                "Set it in appsettings.json or the Jwt__Secret environment variable.");

        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.Secret));
    }

    /// <summary>
    /// Generate a JWT access token with username and deviceId claims.
    /// </summary>
    public string GenerateAccessToken(string username, string deviceId)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", username),
                new Claim("deviceId", deviceId),
                new Claim("jti", Guid.NewGuid().ToString("N")),
            ]),
            Issuer = _settings.Issuer,
            IssuedAt = now,
            Expires = now.AddMinutes(_settings.AccessTokenLifetimeMinutes),
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256Signature),
        };

        return _handler.CreateToken(descriptor);
    }

    /// <summary>
    /// Generate an opaque refresh token prefixed with "fst_rt_".
    /// </summary>
    public string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return "fst_rt_" + Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    /// <summary>
    /// Validate a JWT access token. Returns the ClaimsPrincipal if valid, null otherwise.
    /// </summary>
    public async Task<ClaimsPrincipal?> ValidateAccessTokenAsync(string token)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _settings.Issuer,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _signingKey,
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        try
        {
            var result = await _handler.ValidateTokenAsync(token, parameters);
            if (!result.IsValid)
                return null;

            return new ClaimsPrincipal(result.ClaimsIdentity);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// SHA-256 hash a refresh token for storage (never store raw tokens).
    /// </summary>
    public static string HashRefreshToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Access token lifetime in seconds (for the expiresIn response field).</summary>
    public int AccessTokenLifetimeSeconds => _settings.AccessTokenLifetimeMinutes * 60;

    /// <summary>Refresh token expiry date from now.</summary>
    public DateTime RefreshTokenExpiry => DateTime.UtcNow.AddDays(_settings.RefreshTokenLifetimeDays);
}
