using FSTService.Persistence;

namespace FSTService.Scraping;

public static class SoloFamilyRankingScopes
{
    public const string Pad = "pad";
    public const string ProStrings = "pro_strings";
    public const string ProVocals = "pro_vocals";
    public const string ProDrums = "pro_drums";

    public static readonly IReadOnlyList<SoloFamilyScope> All =
    [
        new(Pad, ["Solo_Guitar", "Solo_Bass", "Solo_Drums", "Solo_Vocals"]),
        new(ProStrings, ["Solo_PeripheralGuitar", "Solo_PeripheralBass"]),
        new(ProVocals, ["Solo_PeripheralVocals"]),
        new(ProDrums, ["Solo_PeripheralCymbals", "Solo_PeripheralDrums"]),
    ];

    public static bool IsValid(string scopeId) =>
        All.Any(scope => scope.ScopeId.Equals(scopeId, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string scopeId)
    {
        var scope = All.FirstOrDefault(s => s.ScopeId.Equals(scopeId, StringComparison.OrdinalIgnoreCase));
        return scope.ScopeId ?? scopeId;
    }
}

public readonly record struct SoloFamilyScope(string ScopeId, IReadOnlyList<string> Instruments);

internal static class SoloFamilyRankingBuilder
{
    private const double MissingPercentile = 1.0;
    private const double MissingMaxScorePercent = 0.0;

    internal static List<SoloFamilyRankingDto> BuildRankings(
        IReadOnlyList<SoloFamilyScope> scopes,
        Dictionary<string, Dictionary<string, RankingsCalculator.AccountMetrics>> perInstrument,
        IReadOnlyDictionary<string, int> totalChartedByInstrument,
        int credibilityThreshold,
        double populationMedian)
    {
        var rankings = new List<SoloFamilyRankingDto>();

        foreach (var scope in scopes)
            rankings.AddRange(BuildScope(scope, perInstrument, totalChartedByInstrument, credibilityThreshold, populationMedian));

        return rankings;
    }

    private static List<SoloFamilyRankingDto> BuildScope(
        SoloFamilyScope scope,
        Dictionary<string, Dictionary<string, RankingsCalculator.AccountMetrics>> perInstrument,
        IReadOnlyDictionary<string, int> totalChartedByInstrument,
        int credibilityThreshold,
        double populationMedian)
    {
        var totalChartedSongs = scope.Instruments.Sum(instrument => totalChartedByInstrument.GetValueOrDefault(instrument));
        if (totalChartedSongs <= 0)
            return [];

        var accounts = new Dictionary<string, FamilyAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var instrument in scope.Instruments)
        {
            if (!perInstrument.TryGetValue(instrument, out var instrumentRows))
                continue;

            foreach (var (accountId, metrics) in instrumentRows)
            {
                if (!accounts.TryGetValue(accountId, out var accumulator))
                    accumulator = new FamilyAccumulator();

                accumulator.SongsPlayed += metrics.SongsPlayed;
                accumulator.FullComboCount += metrics.FullComboCount;
                accumulator.TotalScore += metrics.TotalScore;
                accumulator.RawSkillSum += metrics.RawSkillRating * metrics.SongsPlayed;
                accumulator.RawWeightedSum += (metrics.RawWeightedRating ?? metrics.WeightedRating) * metrics.SongsPlayed;
                accumulator.RawMaxScoreSum += (metrics.RawMaxScorePercent ?? metrics.MaxScorePercent) * metrics.SongsPlayed;

                accounts[accountId] = accumulator;
            }
        }

        var rows = new List<SoloFamilyRankingDto>(accounts.Count);
        foreach (var (accountId, accumulator) in accounts)
        {
            if (accumulator.SongsPlayed <= 0)
                continue;

            var missingSongs = Math.Max(0, totalChartedSongs - accumulator.SongsPlayed);
            var rawSkill = (accumulator.RawSkillSum + missingSongs * MissingPercentile) / totalChartedSongs;
            var rawWeighted = (accumulator.RawWeightedSum + missingSongs * MissingPercentile) / totalChartedSongs;
            var rawMaxScore = (accumulator.RawMaxScoreSum + missingSongs * MissingMaxScorePercent) / totalChartedSongs;

            rows.Add(new SoloFamilyRankingDto
            {
                ScopeId = scope.ScopeId,
                AccountId = accountId,
                SongsPlayed = accumulator.SongsPlayed,
                TotalChartedSongs = totalChartedSongs,
                Coverage = (double)accumulator.SongsPlayed / totalChartedSongs,
                RawSkillRating = rawSkill,
                AdjustedSkillRating = (rawSkill * totalChartedSongs + credibilityThreshold * populationMedian) / (totalChartedSongs + credibilityThreshold),
                WeightedRating = (rawWeighted * totalChartedSongs + credibilityThreshold * populationMedian) / (totalChartedSongs + credibilityThreshold),
                FcRate = (double)accumulator.FullComboCount / totalChartedSongs,
                TotalScore = accumulator.TotalScore,
                MaxScorePercent = (rawMaxScore * totalChartedSongs + credibilityThreshold * populationMedian) / (totalChartedSongs + credibilityThreshold),
                FullComboCount = accumulator.FullComboCount,
                RawWeightedRating = rawWeighted,
                RawMaxScorePercent = rawMaxScore,
            });
        }

        ApplyRanks(rows);
        return rows;
    }

    private static void ApplyRanks(List<SoloFamilyRankingDto> rows)
    {
        var adjusted = RankBy(rows, r => r.AdjustedSkillRating, ascending: true);
        var weighted = RankBy(rows, r => r.WeightedRating, ascending: true);
        var fcRate = RankBy(rows, r => r.FcRate, ascending: false);
        var totalScore = RankBy(rows, r => r.TotalScore, ascending: false);
        var maxScore = RankBy(rows, r => r.MaxScorePercent, ascending: false);

        foreach (var row in rows)
        {
            row.AdjustedSkillRank = adjusted[row.AccountId];
            row.WeightedRank = weighted[row.AccountId];
            row.FcRateRank = fcRate[row.AccountId];
            row.TotalScoreRank = totalScore[row.AccountId];
            row.MaxScorePercentRank = maxScore[row.AccountId];
        }
    }

    private static Dictionary<string, int> RankBy<T>(List<SoloFamilyRankingDto> rows, Func<SoloFamilyRankingDto, T> selector, bool ascending)
        where T : IComparable<T>
    {
        var indices = new int[rows.Count];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;

        Array.Sort(indices, (a, b) =>
        {
            var left = rows[a];
            var right = rows[b];
            int cmp = ascending
                ? selector(left).CompareTo(selector(right))
                : selector(right).CompareTo(selector(left));
            if (cmp != 0) return cmp;
            cmp = right.SongsPlayed.CompareTo(left.SongsPlayed);
            if (cmp != 0) return cmp;
            cmp = right.TotalScore.CompareTo(left.TotalScore);
            if (cmp != 0) return cmp;
            cmp = right.FullComboCount.CompareTo(left.FullComboCount);
            if (cmp != 0) return cmp;
            return string.Compare(left.AccountId, right.AccountId, StringComparison.OrdinalIgnoreCase);
        });

        var ranks = new Dictionary<string, int>(indices.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < indices.Length; i++)
            ranks[rows[indices[i]].AccountId] = i + 1;
        return ranks;
    }

    private struct FamilyAccumulator
    {
        public int SongsPlayed;
        public int FullComboCount;
        public long TotalScore;
        public double RawSkillSum;
        public double RawWeightedSum;
        public double RawMaxScoreSum;
    }
}