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

internal sealed record SoloFamilyInstrumentDenominator(
    string Instrument,
    int CatalogDenominator,
    int CanonicalDenominator,
    int EffectiveDenominator)
{
    internal bool IsOverride => EffectiveDenominator != CatalogDenominator;
}

internal sealed record SoloFamilyRankingInvariantViolation(
    string ScopeId,
    string AccountId,
    int SongsPlayed,
    int FullComboCount,
    int TotalChartedSongs,
    double Coverage,
    double FcRate,
    string Reason);

internal sealed record SoloFamilyRankingBuildResult(
    IReadOnlyList<SoloFamilyRankingDto> Rankings,
    IReadOnlyList<SoloFamilyInstrumentDenominator> InstrumentDenominators,
    IReadOnlyDictionary<string, int> ScopeDenominators,
    int InvalidRowCount,
    SoloFamilyRankingInvariantViolation? FirstInvalidRow)
{
    internal void ThrowIfInvalid()
    {
        if (InvalidRowCount == 0)
            return;

        var first = FirstInvalidRow;
        throw new InvalidOperationException(
            $"Solo family ranking build produced {InvalidRowCount:N0} " +
            "publication-incompatible row(s). " +
            (first is null
                ? ""
                : $"First invalid row: scope={first.ScopeId}, " +
                  $"account={first.AccountId}, songs={first.SongsPlayed}, " +
                  $"fullCombos={first.FullComboCount}, " +
                  $"denominator={first.TotalChartedSongs}, " +
                  $"coverage={first.Coverage:R}, fcRate={first.FcRate:R}, " +
                  $"reason={first.Reason}."));
    }
}

internal static class SoloFamilyRankingBuilder
{
    private const double MissingPercentile = 1.0;
    private const double MissingMaxScorePercent = 0.0;
    internal const double RateTolerance = 1e-9;

    internal static SoloFamilyRankingBuildResult BuildRankings(
        IReadOnlyList<SoloFamilyScope> scopes,
        Dictionary<string, Dictionary<string, RankingsCalculator.AccountMetrics>> perInstrument,
        IReadOnlyDictionary<string, int> totalChartedByInstrument,
        int credibilityThreshold,
        double populationMedian)
    {
        var denominatorEvidence = BuildDenominatorEvidence(
            scopes,
            perInstrument,
            totalChartedByInstrument);
        var effectiveByInstrument = denominatorEvidence.ToDictionary(
            row => row.Instrument,
            row => row.EffectiveDenominator,
            StringComparer.OrdinalIgnoreCase);
        var rankings = new List<SoloFamilyRankingDto>();
        var scopeDenominators = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        var invalidRowCount = 0;
        SoloFamilyRankingInvariantViolation? firstInvalidRow = null;

        foreach (var scope in scopes)
        {
            var scopeDenominator = scope.Instruments.Sum(
                instrument => effectiveByInstrument.GetValueOrDefault(
                    instrument));
            scopeDenominators[scope.ScopeId] = scopeDenominator;
            rankings.AddRange(BuildScope(
                scope,
                perInstrument,
                scopeDenominator,
                credibilityThreshold,
                populationMedian,
                ref invalidRowCount,
                ref firstInvalidRow));
        }

        return new SoloFamilyRankingBuildResult(
            rankings,
            denominatorEvidence,
            scopeDenominators,
            invalidRowCount,
            firstInvalidRow);
    }

    private static IReadOnlyList<SoloFamilyInstrumentDenominator>
        BuildDenominatorEvidence(
            IReadOnlyList<SoloFamilyScope> scopes,
            Dictionary<string, Dictionary<string, RankingsCalculator.AccountMetrics>> perInstrument,
            IReadOnlyDictionary<string, int> totalChartedByInstrument)
    {
        var evidence = new List<SoloFamilyInstrumentDenominator>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var instrument in scopes.SelectMany(scope => scope.Instruments))
        {
            if (!seen.Add(instrument))
                continue;

            var catalogDenominator = Math.Max(
                0,
                totalChartedByInstrument.GetValueOrDefault(instrument));
            var canonicalDenominator = 0;
            if (perInstrument.TryGetValue(instrument, out var instrumentRows))
            {
                foreach (var metrics in instrumentRows.Values)
                {
                    canonicalDenominator = Math.Max(
                        canonicalDenominator,
                        metrics.TotalChartedSongs);
                }
            }

            canonicalDenominator = Math.Max(0, canonicalDenominator);
            evidence.Add(new SoloFamilyInstrumentDenominator(
                instrument,
                catalogDenominator,
                canonicalDenominator,
                Math.Max(catalogDenominator, canonicalDenominator)));
        }

        return evidence;
    }

    private static List<SoloFamilyRankingDto> BuildScope(
        SoloFamilyScope scope,
        Dictionary<string, Dictionary<string, RankingsCalculator.AccountMetrics>> perInstrument,
        int totalChartedSongs,
        int credibilityThreshold,
        double populationMedian,
        ref int invalidRowCount,
        ref SoloFamilyRankingInvariantViolation? firstInvalidRow)
    {
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
        rows.Sort(static (left, right) =>
        {
            var comparison = string.Compare(
                left.AccountId,
                right.AccountId,
                StringComparison.OrdinalIgnoreCase);
            return comparison != 0
                ? comparison
                : string.Compare(
                    left.AccountId,
                    right.AccountId,
                    StringComparison.Ordinal);
        });

        foreach (var row in rows)
        {
            var violation = GetInvariantViolation(row);
            if (violation is null)
                continue;

            invalidRowCount++;
            firstInvalidRow ??= violation;
        }

        return rows;
    }

    private static SoloFamilyRankingInvariantViolation? GetInvariantViolation(
        SoloFamilyRankingDto row)
    {
        var reasons = new List<string>(4);
        if (row.SongsPlayed > row.TotalChartedSongs)
            reasons.Add("songs_played_exceeds_denominator");
        if (row.FullComboCount > row.TotalChartedSongs)
            reasons.Add("full_combo_count_exceeds_denominator");
        if (!double.IsFinite(row.Coverage)
            || row.Coverage > 1.0 + RateTolerance)
        {
            reasons.Add("coverage_exceeds_one");
        }
        if (!double.IsFinite(row.FcRate)
            || row.FcRate > 1.0 + RateTolerance)
        {
            reasons.Add("fc_rate_exceeds_one");
        }

        return reasons.Count == 0
            ? null
            : new SoloFamilyRankingInvariantViolation(
                row.ScopeId,
                row.AccountId,
                row.SongsPlayed,
                row.FullComboCount,
                row.TotalChartedSongs,
                row.Coverage,
                row.FcRate,
                string.Join(",", reasons));
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