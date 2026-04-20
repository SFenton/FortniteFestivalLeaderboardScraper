using FSTService.Persistence;

namespace FSTService.Scraping;

internal static class ComboLeaderboardBuilder
{
    internal sealed record ComputedComboLeaderboard(
        string ComboId,
        IReadOnlyList<string> Instruments,
        IReadOnlyList<(string AccountId, double AdjustedRating, double WeightedRating, double FcRate, long TotalScore, double MaxScorePercent, int SongsPlayed, int FullComboCount)> Entries);

    internal static List<string> ResolveComboIds(IReadOnlyList<string>? comboIds)
    {
        if (comboIds is null || comboIds.Count == 0)
            return ComboIds.WithinGroupComboMasks.Select(ComboIds.FromMask).ToList();

        var resolved = new List<string>(comboIds.Count);
        foreach (var rawComboId in comboIds)
        {
            var comboId = ComboIds.NormalizeComboParam(rawComboId);
            if (string.IsNullOrWhiteSpace(comboId) || !ComboIds.IsWithinGroupCombo(comboId))
                throw new ArgumentException($"Invalid within-group combo: {rawComboId}", nameof(comboIds));

            if (!resolved.Contains(comboId, StringComparer.OrdinalIgnoreCase))
                resolved.Add(comboId);
        }

        return resolved;
    }

    internal static IReadOnlyList<ComputedComboLeaderboard> BuildLeaderboards(
        IReadOnlyList<string> comboIds,
        Dictionary<string, Dictionary<string, RankingsCalculator.AccountMetrics>> perInstrument)
    {
        var leaderboards = new List<ComputedComboLeaderboard>(comboIds.Count);

        foreach (var comboId in comboIds)
        {
            var comboInstruments = ComboIds.ToInstruments(comboId);
            if (!comboInstruments.All(perInstrument.ContainsKey))
                continue;

            Dictionary<string, AggregatedMetrics>? accountMetrics = null;

            foreach (var instrument in comboInstruments)
            {
                var instrumentData = perInstrument[instrument];

                if (accountMetrics is null)
                {
                    accountMetrics = new Dictionary<string, AggregatedMetrics>(instrumentData.Count, StringComparer.OrdinalIgnoreCase);
                    foreach (var (accountId, metrics) in instrumentData)
                    {
                        accountMetrics[accountId] = new AggregatedMetrics(
                            metrics.AdjustedRating * metrics.SongsPlayed,
                            metrics.WeightedRating * metrics.SongsPlayed,
                            metrics.FullComboCount,
                            metrics.TotalScore,
                            metrics.MaxScorePercent,
                            metrics.SongsPlayed,
                            1);
                    }

                    continue;
                }

                var missingAccounts = new List<string>();
                foreach (var accountId in accountMetrics.Keys)
                {
                    if (!instrumentData.ContainsKey(accountId))
                        missingAccounts.Add(accountId);
                }

                foreach (var accountId in missingAccounts)
                    accountMetrics.Remove(accountId);

                foreach (var (accountId, metrics) in instrumentData)
                {
                    if (!accountMetrics.TryGetValue(accountId, out var existing))
                        continue;

                    accountMetrics[accountId] = new AggregatedMetrics(
                        existing.AdjWeightedSum + metrics.AdjustedRating * metrics.SongsPlayed,
                        existing.WgtWeightedSum + metrics.WeightedRating * metrics.SongsPlayed,
                        existing.TotalFcCount + metrics.FullComboCount,
                        existing.TotalScore + metrics.TotalScore,
                        existing.MaxScoreSum + metrics.MaxScorePercent,
                        existing.TotalSongs + metrics.SongsPlayed,
                        existing.InstrumentCount + 1);
                }
            }

            var entries = accountMetrics is null
                ? []
                : accountMetrics.Select(kvp =>
                {
                    var metrics = kvp.Value;
                    return (
                        AccountId: kvp.Key,
                        AdjustedRating: metrics.TotalSongs > 0 ? metrics.AdjWeightedSum / metrics.TotalSongs : 0.0,
                        WeightedRating: metrics.TotalSongs > 0 ? metrics.WgtWeightedSum / metrics.TotalSongs : 0.0,
                        FcRate: metrics.TotalSongs > 0 ? (double)metrics.TotalFcCount / metrics.TotalSongs : 0.0,
                        TotalScore: metrics.TotalScore,
                        MaxScorePercent: metrics.InstrumentCount > 0 ? metrics.MaxScoreSum / metrics.InstrumentCount : 0.0,
                        SongsPlayed: metrics.TotalSongs,
                        FullComboCount: metrics.TotalFcCount);
                }).ToList();

            leaderboards.Add(new ComputedComboLeaderboard(comboId, comboInstruments, entries));
        }

        return leaderboards;
    }

    private readonly record struct AggregatedMetrics(
        double AdjWeightedSum,
        double WgtWeightedSum,
        int TotalFcCount,
        long TotalScore,
        double MaxScoreSum,
        int TotalSongs,
        int InstrumentCount);
}