namespace FSTService.Scraping;

internal readonly record struct BandLeaderboardIdentity(
    string TeamKey,
    string InstrumentCombo);

internal static class LeaderboardEntryIdentity
{
    internal static IEqualityComparer<string> SoloComparer =>
        StringComparer.OrdinalIgnoreCase;

    internal static IEqualityComparer<BandLeaderboardIdentity>
        BandComparer
    { get; } =
        new BandIdentityComparer();

    internal static string Solo(LeaderboardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.AccountId;
    }

    internal static BandLeaderboardIdentity Band(
        BandLeaderboardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Band(
            entry.TeamKey,
            entry.InstrumentCombo);
    }

    internal static BandLeaderboardIdentity Band(
        string teamKey,
        string instrumentCombo) =>
        new(teamKey, instrumentCombo);

    private sealed class BandIdentityComparer
        : IEqualityComparer<BandLeaderboardIdentity>
    {
        public bool Equals(
            BandLeaderboardIdentity left,
            BandLeaderboardIdentity right) =>
            StringComparer.OrdinalIgnoreCase.Equals(
                left.TeamKey,
                right.TeamKey) &&
            StringComparer.OrdinalIgnoreCase.Equals(
                left.InstrumentCombo,
                right.InstrumentCombo);

        public int GetHashCode(
            BandLeaderboardIdentity value)
        {
            var hash = new HashCode();
            hash.Add(
                value.TeamKey,
                StringComparer.OrdinalIgnoreCase);
            hash.Add(
                value.InstrumentCombo,
                StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }
}
