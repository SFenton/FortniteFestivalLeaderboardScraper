using FSTService.Persistence;

namespace FSTService.Scraping;

internal static class BandCurrentProjectionScopeTracker
{
    public const string OverallScope = "overall";
    public const string ComboScope = "combo";

    public static void AddScopes(
        ISet<BandCurrentProjectionScopeKey> scopes,
        string songId,
        string bandType,
        string? rawInstrumentCombo)
    {
        if (string.IsNullOrWhiteSpace(songId) || !BandComboIds.IsValidBandType(bandType))
            return;

        scopes.Add(new BandCurrentProjectionScopeKey(songId, bandType, OverallScope, string.Empty));

        var comboId = BandComboIds.FromEpicRawCombo(rawInstrumentCombo);
        if (string.IsNullOrWhiteSpace(comboId))
            return;

        if (BandComboIds.ToInstruments(comboId).Count != BandInstrumentMapping.ExpectedMemberCount(bandType))
            return;

        scopes.Add(new BandCurrentProjectionScopeKey(songId, bandType, ComboScope, comboId));
    }

    public static IReadOnlyCollection<BandCurrentProjectionScopeKey> OrderedDistinct(
        IEnumerable<BandCurrentProjectionScopeKey> scopes) =>
        scopes
            .Where(static scope => !string.IsNullOrWhiteSpace(scope.SongId)
                                   && BandComboIds.IsValidBandType(scope.BandType)
                                   && scope.RankingScope is OverallScope or ComboScope)
            .Distinct()
            .OrderBy(static scope => scope.BandType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static scope => scope.RankingScope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static scope => scope.ScopeComboId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static scope => scope.SongId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}