using System.Net;
using System.Text.RegularExpressions;

namespace FSTService.Scraping.Replay;

public enum TierZeroConfigurationFailureKind
{
    EmptyAllowlist,
    DuplicateAllowlistKey,
    MissingAllowlistedKey,
    KeyNotAllowlisted,
    SecretLikeKey,
    SecretLikeValue,
    InvalidKey,
    InvalidValue,
}

public sealed class TierZeroConfigurationException : ArgumentException
{
    public TierZeroConfigurationException(
        TierZeroConfigurationFailureKind kind,
        string message)
        : base(message)
    {
        Kind = kind;
    }

    public TierZeroConfigurationFailureKind Kind { get; }
}

public static partial class TierZeroConfigurationFingerprinter
{
    internal const string Algorithm = "sha256-canonical-json-v1";

    private static readonly string[] SecretKeyTokens =
    [
        "password",
        "passwd",
        "secret",
        "token",
        "apikey",
        "cookie",
        "authorization",
        "connectionstring",
        "credential",
        "privatekey",
        "clientsecret",
        "proxy",
        "endpoint",
        "hostname",
        "account",
        "username",
        "wireguard",
    ];

    private static readonly string[] SecretValueMarkers =
    [
        "://",
        "bearer ",
        "basic ",
        "host=",
        "password=",
        "pwd=",
        "username=",
        "user id=",
        "account=",
        "authorization:",
        "cookie:",
        "set-cookie:",
        "-----begin ",
    ];

    public static TierZeroConfigurationFingerprint Create(
        IReadOnlyDictionary<string, string?> values,
        IEnumerable<string> allowlistedKeys)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(allowlistedKeys);

        var allowlist = allowlistedKeys.ToArray();
        if (allowlist.Length == 0)
        {
            throw new TierZeroConfigurationException(
                TierZeroConfigurationFailureKind.EmptyAllowlist,
                "A named configuration allowlist is required.");
        }

        var duplicate = allowlist
            .GroupBy(static key => key, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new TierZeroConfigurationException(
                TierZeroConfigurationFailureKind.DuplicateAllowlistKey,
                $"Configuration allowlist contains duplicate key '{duplicate.Key}'.");
        }

        var allowed = new HashSet<string>(allowlist, StringComparer.Ordinal);
        foreach (var key in allowlist)
            ValidateKey(key);

        foreach (var (key, value) in values)
        {
            ValidateKey(key);
            ValidateValue(value);
            if (!allowed.Contains(key))
            {
                throw new TierZeroConfigurationException(
                    TierZeroConfigurationFailureKind.KeyNotAllowlisted,
                    $"Configuration key '{key}' is not explicitly allowlisted.");
            }
        }

        var missing = allowlist.FirstOrDefault(key => !values.ContainsKey(key));
        if (missing is not null)
        {
            throw new TierZeroConfigurationException(
                TierZeroConfigurationFailureKind.MissingAllowlistedKey,
                $"Allowlisted configuration key '{missing}' has no supplied value.");
        }

        var entries = values
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => new ConfigurationHashEntry(
                pair.Key,
                pair.Value))
            .ToArray();
        var hash = TierZeroCanonicalJson.Sha256Hex(
            TierZeroCanonicalJson.Serialize(entries));

        return new TierZeroConfigurationFingerprint(
            Algorithm,
            entries.Select(static entry => entry.Key).ToArray(),
            hash);
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) ||
            key.Any(char.IsControl))
        {
            throw new TierZeroConfigurationException(
                TierZeroConfigurationFailureKind.InvalidKey,
                "Configuration fingerprint keys must be non-empty printable strings.");
        }

        if (IsSecretLikeKey(key))
        {
            throw new TierZeroConfigurationException(
                TierZeroConfigurationFailureKind.SecretLikeKey,
                $"Configuration key '{key}' is secret-like and cannot be fingerprinted.");
        }
    }

    internal static bool IsSecretLikeKey(string key)
    {
        var normalized = NonAlphaNumericRegex()
            .Replace(key, "")
            .ToLowerInvariant();
        return SecretKeyTokens.Any(normalized.Contains);
    }

    private static void ValidateValue(string? value)
    {
        if (value is null)
            return;
        if (value.Any(char.IsControl))
        {
            throw new TierZeroConfigurationException(
                TierZeroConfigurationFailureKind.InvalidValue,
                "Configuration fingerprint values must be printable strings.");
        }

        if (IsSecretLikeValue(value))
        {
            throw new TierZeroConfigurationException(
                TierZeroConfigurationFailureKind.SecretLikeValue,
                "Configuration fingerprint values cannot contain credentials, endpoints, or authorization material.");
        }
    }

    internal static bool IsSecretLikeValue(string? value)
    {
        if (value is null)
            return false;
        var normalized = value.Trim().ToLowerInvariant();
        return SecretValueMarkers.Any(normalized.Contains) ||
               JwtLikeRegex().IsMatch(value) ||
               CredentialAssignmentRegex().IsMatch(value) ||
               EndpointWithPortRegex().IsMatch(value) ||
               IpAddressRegex().IsMatch(value) ||
               DnsNameTokenRegex().IsMatch(value) ||
               LocalhostTokenRegex().IsMatch(value) ||
               ContainsIpv6Address(value) ||
               IsBareEndpoint(normalized);
    }

    private static bool ContainsIpv6Address(string value)
    {
        foreach (Match match in Ipv6TokenRegex().Matches(value))
        {
            var candidate = match.Value.Trim('[', ']');
            if (IPAddress.TryParse(candidate, out var address) &&
                address.AddressFamily ==
                System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsBareEndpoint(string value)
    {
        var candidate = value.Trim('[', ']');
        return (candidate.Contains(':') &&
                IPAddress.TryParse(candidate, out _)) ||
               BareDnsNameRegex().IsMatch(value);
    }

    private sealed record ConfigurationHashEntry(
        string Key,
        string? Value);

    [GeneratedRegex("[^A-Za-z0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(
        "(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}(?![A-Za-z0-9_-])",
        RegexOptions.CultureInvariant)]
    private static partial Regex JwtLikeRegex();

    [GeneratedRegex(
        "(?<![A-Za-z0-9_])(?:access[_-]?token|refresh[_-]?token|id[_-]?token|token|api[_-]?key|password|passwd|pwd|client[_-]?secret|proxy|endpoint|authorization|cookie|host|server|data[_ -]?source|port|database|initial[_ ]?catalog|user[_ ]?id)\\s*[\\\"']?\\s*[:=]\\s*[\\\"']?\\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9.-])(?:localhost|[A-Za-z][A-Za-z0-9-]*|(?:[A-Za-z0-9-]+\.)+[A-Za-z0-9-]+|(?:[0-9]{1,3}\.){3}[0-9]{1,3}|\[[0-9A-Fa-f:]+\]):[0-9]{1,5}(?![0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EndpointWithPortRegex();

    [GeneratedRegex(
        @"(?<![0-9A-Fa-f:.])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?![0-9.])",
        RegexOptions.CultureInvariant)]
    private static partial Regex IpAddressRegex();

    [GeneratedRegex(
        @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,62}\.)+[A-Za-z]{2,63}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareDnsNameRegex();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9.-])[A-Za-z0-9](?:[A-Za-z0-9-]{0,62}\.)+[A-Za-z]{2,63}(?![A-Za-z0-9.-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DnsNameTokenRegex();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9.-])localhost(?![A-Za-z0-9.-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalhostTokenRegex();

    [GeneratedRegex(
        @"(?<![0-9A-Fa-f:])\[?[0-9A-Fa-f]*:[0-9A-Fa-f:]+\]?(?![0-9A-Fa-f:])",
        RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6TokenRegex();
}
