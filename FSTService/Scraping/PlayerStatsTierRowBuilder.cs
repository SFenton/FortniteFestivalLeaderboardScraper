using System.Text.Json;
using FSTService.Persistence;

namespace FSTService.Scraping;

public static class PlayerStatsTierRowBuilder
{
    public static List<PlayerStatsTiersRow> BuildRows(
        string accountId,
        IReadOnlyList<PlayerScoreDto> allScores,
        IReadOnlyList<string> instrumentKeys,
        int totalSongs,
        Dictionary<string, SongMaxScores> allMaxScores,
        Dictionary<(string SongId, string Instrument), long> population,
        Dictionary<(string SongId, string Instrument), List<ValidScoreFallback>>? fallbacks = null)
    {
        if (allScores.Count == 0)
            return [];

        var byInstrument = new Dictionary<string, List<PlayerScoreDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var score in allScores)
        {
            if (!byInstrument.TryGetValue(score.Instrument, out var list))
            {
                list = [];
                byInstrument[score.Instrument] = list;
            }

            list.Add(score);
        }

        var rows = new List<PlayerStatsTiersRow>();
        var perInstrumentTiers = new Dictionary<string, List<PlayerStatsTier>>(StringComparer.OrdinalIgnoreCase);

        foreach (var instrument in instrumentKeys)
        {
            if (!byInstrument.TryGetValue(instrument, out var scores) || scores.Count == 0)
                continue;

            var tiers = PlayerStatsCalculator.ComputeTiers(scores, allMaxScores, instrument, totalSongs, population, fallbacks);
            perInstrumentTiers[instrument] = tiers;
            rows.Add(new PlayerStatsTiersRow
            {
                AccountId = accountId,
                Instrument = instrument,
                TiersJson = JsonSerializer.Serialize(tiers),
            });
        }

        if (perInstrumentTiers.Count > 0)
        {
            var overallTiers = PlayerStatsCalculator.ComputeOverallTiers(perInstrumentTiers, totalSongs);
            rows.Add(new PlayerStatsTiersRow
            {
                AccountId = accountId,
                Instrument = "Overall",
                TiersJson = JsonSerializer.Serialize(overallTiers),
            });
        }

        return rows;
    }

    public static Dictionary<(string SongId, string Instrument), int> BuildAboveMaxThresholds(
        IReadOnlyList<PlayerScoreDto> allScores,
        Dictionary<string, SongMaxScores> allMaxScores)
    {
        var thresholds = new Dictionary<(string SongId, string Instrument), int>();
        foreach (var score in allScores)
        {
            if (!allMaxScores.TryGetValue(score.SongId, out var maxScores))
                continue;

            var max = maxScores.GetByInstrument(score.Instrument);
            if (max.HasValue && max.Value > 0 && score.Score > max.Value)
                thresholds[(score.SongId, score.Instrument)] = (int)(max.Value * 1.05);
        }

        return thresholds;
    }
}