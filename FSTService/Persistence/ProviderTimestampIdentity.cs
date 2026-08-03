using System.Globalization;

namespace FSTService.Persistence;

internal static class ProviderTimestampIdentity
{
    private const DateTimeStyles ParseStyles =
        DateTimeStyles.AllowWhiteSpaces;

    internal static bool Equivalent(string? left, string? right)
    {
        var leftMissing = string.IsNullOrWhiteSpace(left);
        var rightMissing = string.IsNullOrWhiteSpace(right);
        if (leftMissing || rightMissing)
            return leftMissing && rightMissing;

        return TryNormalize(left, out var normalizedLeft)
            && TryNormalize(right, out var normalizedRight)
            && string.Equals(
                normalizedLeft,
                normalizedRight,
                StringComparison.Ordinal);
    }

    internal static bool TryNormalize(
        string? value,
        out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value)
            || !HasExplicitOffset(value)
            || !DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                ParseStyles,
                out var parsed))
        {
            return false;
        }

        normalized = parsed.UtcDateTime.ToString(
            "O",
            CultureInfo.InvariantCulture);
        return true;
    }

    internal static string NormalizeRequired(
        string value,
        string parameterName)
    {
        if (!TryNormalize(value, out var normalized))
        {
            throw new ArgumentException(
                "Provider timestamps must be valid ISO-8601 values.",
                parameterName);
        }

        return normalized!;
    }

    private static bool HasExplicitOffset(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.EndsWith('Z') || trimmed.EndsWith('z'))
            return true;
        if (trimmed.Length < 6)
            return false;

        var offset = trimmed.AsSpan(trimmed.Length - 6);
        return (offset[0] is '+' or '-')
            && char.IsAsciiDigit(offset[1])
            && char.IsAsciiDigit(offset[2])
            && offset[3] == ':'
            && char.IsAsciiDigit(offset[4])
            && char.IsAsciiDigit(offset[5]);
    }
}
