using Microsoft.AspNetCore.Http;

namespace FSTService.Api;

public enum SelectedProfileKind
{
    Player,
    Band,
}

public abstract record SelectedProfileSelection(SelectedProfileKind Kind);

public sealed record SelectedPlayerSelection(string AccountId)
    : SelectedProfileSelection(SelectedProfileKind.Player);

public sealed record SelectedBandSelection(string BandId, string BandType, string TeamKey)
    : SelectedProfileSelection(SelectedProfileKind.Band);

public static class SelectedProfileHeaders
{
    public const string LegacySelectedPlayerHeader = "X-FST-Selected-Player";
    public const string SelectedProfileTypeHeader = "X-FST-Selected-Profile-Type";
    public const string SelectedProfileIdHeader = "X-FST-Selected-Profile-Id";
    public const string SelectedBandIdHeader = "X-FST-Selected-Band-Id";
    public const string SelectedBandTypeHeader = "X-FST-Selected-Band-Type";
    public const string SelectedBandTeamKeyHeader = "X-FST-Selected-Band-Team-Key";

    public static bool TryParse(IHeaderDictionary headers, out SelectedProfileSelection? selection)
    {
        selection = null;
        var profileType = ReadHeader(headers, SelectedProfileTypeHeader, 32);

        if (string.Equals(profileType, "player", StringComparison.OrdinalIgnoreCase))
        {
            var accountId = ReadHeader(headers, SelectedProfileIdHeader, 128)
                ?? ReadHeader(headers, LegacySelectedPlayerHeader, 128);
            if (accountId is null) return false;
            selection = new SelectedPlayerSelection(accountId);
            return true;
        }

        if (string.Equals(profileType, "band", StringComparison.OrdinalIgnoreCase))
        {
            var bandId = ReadHeader(headers, SelectedBandIdHeader, 128)
                ?? ReadHeader(headers, SelectedProfileIdHeader, 128);
            var bandType = ReadHeader(headers, SelectedBandTypeHeader, 64);
            var teamKey = ReadHeader(headers, SelectedBandTeamKeyHeader, 512);
            if (bandId is null || bandType is null || teamKey is null) return false;
            selection = new SelectedBandSelection(bandId, bandType, teamKey);
            return true;
        }

        var legacyAccountId = ReadHeader(headers, LegacySelectedPlayerHeader, 128);
        if (legacyAccountId is null) return false;

        selection = new SelectedPlayerSelection(legacyAccountId);
        return true;
    }

    private static string? ReadHeader(IHeaderDictionary headers, string name, int maxLength)
    {
        if (!headers.TryGetValue(name, out var values)) return null;
        var value = values.ToString().Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength) return null;
        return value;
    }
}