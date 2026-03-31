using System.Text.Json;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Computes pre-aggregated player statistics with leeway breakpoint tiers.
/// Each tier is a complete stats snapshot for a specific leeway threshold,
/// enabling the frontend to display exact stats at any slider position.
/// </summary>
public sealed class PlayerStatsCalculator
{
    private static readonly int[] PercentileBuckets = [1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100];
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Compute leeway-tiered stats for a single instrument.
    /// </summary>
    /// <param name="scores">All scores for this account on this instrument.</param>
    /// <param name="maxScores">CHOpt max scores keyed by songId.</param>
    /// <param name="instrument">Instrument DB name (e.g., "Solo_Guitar").</param>
    /// <param name="totalSongs">Total number of charted songs (for completion %).</param>
    /// <param name="fallbacks">Valid fallback scores from score_history (registered users only). Null for non-registered.</param>
    /// <returns>Ordered list of tiers (null/base first, then ascending minLeeway).</returns>
    public static List<PlayerStatsTier> ComputeTiers(
        IReadOnlyList<PlayerScoreDto> scores,
        Dictionary<string, SongMaxScores> maxScores,
        string instrument,
        int totalSongs,
        Dictionary<(string SongId, string Instrument), List<ValidScoreFallback>>? fallbacks = null)
    {
        if (scores.Count == 0)
            return [ComputeTierFromScores([], totalSongs, overThresholdCount: 0)];

        // Classify each score: compute its minLeeway breakpoint
        var classified = new List<ClassifiedScore>(scores.Count);
        foreach (var s in scores)
        {
            double? minLeeway = null;
            if (maxScores.TryGetValue(s.SongId, out var ms))
            {
                var max = ms.GetByInstrument(instrument);
                if (max.HasValue && max.Value > 0 && s.Score > max.Value)
                    minLeeway = Math.Round(((double)s.Score / max.Value - 1.0) * 100.0, 6);
            }

            ValidScoreFallback? fallback = null;
            if (minLeeway.HasValue && fallbacks is not null)
            {
                // Find best fallback at leeway=0 (score <= max)
                var key = (s.SongId, instrument);
                if (fallbacks.TryGetValue(key, out var tierList) && tierList.Count > 0)
                    fallback = tierList[0]; // Already sorted best-first
            }

            classified.Add(new ClassifiedScore
            {
                Score = s,
                MinLeeway = minLeeway,
                Fallback = fallback,
            });
        }

        // Find distinct breakpoints (sorted ascending)
        var breakpoints = classified
            .Where(c => c.MinLeeway.HasValue)
            .Select(c => c.MinLeeway!.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        // Always produce the base tier (minLeeway = null = strictest filtering)
        var tiers = new List<PlayerStatsTier>(breakpoints.Count + 1);

        // Base tier: only scores at or below CHOpt max (+ fallbacks for over-threshold)
        tiers.Add(BuildTier(classified, minLeewayThreshold: null, totalSongs));

        // One tier per breakpoint: each includes scores up to that leeway
        foreach (var bp in breakpoints)
            tiers.Add(BuildTier(classified, minLeewayThreshold: bp, totalSongs));

        return tiers;
    }

    /// <summary>
    /// Compute an "Overall" tier set that aggregates across all instruments.
    /// </summary>
    public static List<PlayerStatsTier> ComputeOverallTiers(
        Dictionary<string, List<PlayerStatsTier>> perInstrumentTiers,
        int totalSongs)
    {
        // Collect all distinct breakpoints across all instruments
        var allBreakpoints = new SortedSet<double>();
        foreach (var (_, tiers) in perInstrumentTiers)
            foreach (var t in tiers)
                if (t.MinLeeway.HasValue)
                    allBreakpoints.Add(t.MinLeeway.Value);

        var result = new List<PlayerStatsTier>(allBreakpoints.Count + 1);

        // Base tier
        result.Add(BuildOverallTier(perInstrumentTiers, minLeeway: null, totalSongs));

        foreach (var bp in allBreakpoints)
            result.Add(BuildOverallTier(perInstrumentTiers, minLeeway: bp, totalSongs));

        return result;
    }

    // ── Private implementation ────────────────────────────────────

    private static PlayerStatsTier BuildTier(
        List<ClassifiedScore> classified,
        double? minLeewayThreshold,
        int totalSongs)
    {
        var effectiveScores = new List<EffectiveScore>(classified.Count);
        int overThresholdCount = 0;

        foreach (var c in classified)
        {
            if (c.MinLeeway is null || (minLeewayThreshold.HasValue && c.MinLeeway.Value <= minLeewayThreshold.Value))
            {
                // Score is valid at this leeway — use original
                effectiveScores.Add(new EffectiveScore
                {
                    SongId = c.Score.SongId,
                    Score = c.Score.Score,
                    Accuracy = c.Score.Accuracy,
                    IsFullCombo = c.Score.IsFullCombo,
                    Stars = c.Score.Stars,
                    Rank = EffectiveRank(c.Score),
                    TotalEntries = 0, // Populated by rank refresh
                    Percentile = c.Score.Percentile,
                });
            }
            else
            {
                // Score exceeds threshold at this leeway
                overThresholdCount++;
                if (c.Fallback is not null)
                {
                    // Registered user: substitute fallback
                    effectiveScores.Add(new EffectiveScore
                    {
                        SongId = c.Score.SongId,
                        Score = c.Fallback.Score,
                        Accuracy = c.Fallback.Accuracy ?? 0,
                        IsFullCombo = c.Fallback.IsFullCombo ?? false,
                        Stars = c.Fallback.Stars ?? 0,
                        Rank = 0, // Fallback rank unknown
                        TotalEntries = 0,
                        Percentile = 0,
                    });
                }
                // else: non-registered — exclude entirely
            }
        }

        var tier = ComputeTierFromScores(effectiveScores, totalSongs, overThresholdCount);
        tier.MinLeeway = minLeewayThreshold;
        return tier;
    }

    private static PlayerStatsTier ComputeTierFromScores(
        List<EffectiveScore> scores,
        int totalSongs,
        int overThresholdCount)
    {
        var tier = new PlayerStatsTier
        {
            SongsPlayed = scores.Count,
            OverThresholdCount = overThresholdCount,
        };

        if (scores.Count == 0)
        {
            tier.CompletionPercent = 0;
            return tier;
        }

        int fcCount = 0, goldStars = 0, fiveStars = 0, fourStars = 0;
        int threeStars = 0, twoStars = 0, oneStars = 0;
        long totalScore = 0;
        int bestRank = 0;
        string? bestRankSongId = null;
        double accSum = 0; int accCount = 0;
        int bestAcc = 0;
        double starsSum = 0; int starsCount = 0;

        // Percentile data
        var percentileValues = new List<(string SongId, double Pct)>();

        foreach (var s in scores)
        {
            totalScore += s.Score;
            if (s.IsFullCombo) fcCount++;

            switch (s.Stars)
            {
                case >= 6: goldStars++; break;
                case 5: fiveStars++; break;
                case 4: fourStars++; break;
                case 3: threeStars++; break;
                case 2: twoStars++; break;
                case 1: oneStars++; break;
            }

            if (s.Stars > 0) { starsSum += s.Stars; starsCount++; }
            if (s.Accuracy > 0) { accSum += s.Accuracy; accCount++; if (s.Accuracy > bestAcc) bestAcc = s.Accuracy; }

            var rank = s.Rank;
            if (rank > 0)
            {
                if (bestRank == 0 || rank < bestRank)
                {
                    bestRank = rank;
                    bestRankSongId = s.SongId;
                }
            }

            if (rank > 0 && s.TotalEntries > 0)
                percentileValues.Add((s.SongId, (double)rank / s.TotalEntries * 100.0));
        }

        tier.FcCount = fcCount;
        tier.FcPercent = scores.Count > 0 ? Math.Round((double)fcCount / scores.Count * 100.0, 1) : 0;
        tier.GoldStarCount = goldStars;
        tier.FiveStarCount = fiveStars;
        tier.FourStarCount = fourStars;
        tier.ThreeStarCount = threeStars;
        tier.TwoStarCount = twoStars;
        tier.OneStarCount = oneStars;
        tier.AvgAccuracy = accCount > 0 ? accSum / accCount : 0;
        tier.BestAccuracy = bestAcc;
        tier.AverageStars = starsCount > 0 ? Math.Round(starsSum / starsCount, 2) : 0;
        tier.AvgScore = scores.Count > 0 ? Math.Round((double)totalScore / scores.Count, 2) : 0;
        tier.TotalScore = totalScore;
        tier.BestRank = bestRank;
        tier.BestRankSongId = bestRankSongId;
        tier.CompletionPercent = totalSongs > 0 ? Math.Round((double)scores.Count / totalSongs * 100.0, 1) : 0;

        // Percentile distribution
        if (percentileValues.Count > 0)
        {
            var dist = new Dictionary<int, int>();
            foreach (var bucket in PercentileBuckets) dist[bucket] = 0;

            foreach (var (_, pct) in percentileValues)
            {
                foreach (var bucket in PercentileBuckets)
                {
                    if (pct <= bucket) { dist[bucket]++; break; }
                }
            }
            tier.PercentileDist = JsonSerializer.Serialize(dist);

            // Average percentile (played songs only)
            double avgPct = percentileValues.Average(p => p.Pct);
            tier.AvgPercentile = FormatPercentileBucket(avgPct);

            // Overall percentile (unplayed = 100%)
            int unplayed = totalSongs - scores.Count;
            double totalPct = percentileValues.Sum(p => p.Pct / 100.0) + unplayed;
            double overallPct = totalSongs > 0 ? totalPct / totalSongs * 100.0 : 100;
            tier.OverallPercentile = FormatPercentileBucket(overallPct);

            // Top/bottom 5 songs
            var sorted = percentileValues.OrderBy(p => p.Pct).ToList();
            tier.TopSongs = JsonSerializer.Serialize(
                sorted.Take(5).Select(p => new StatsSongRef { SongId = p.SongId, Percentile = Math.Round(p.Pct, 1) }),
                JsonOpts);
            tier.BottomSongs = JsonSerializer.Serialize(
                sorted.TakeLast(5).Reverse().Select(p => new StatsSongRef { SongId = p.SongId, Percentile = Math.Round(p.Pct, 1) }),
                JsonOpts);
        }

        return tier;
    }

    private static PlayerStatsTier BuildOverallTier(
        Dictionary<string, List<PlayerStatsTier>> perInstrumentTiers,
        double? minLeeway,
        int totalSongs)
    {
        // For each instrument, find the tier at this leeway
        var matchingTiers = new List<(string Instrument, PlayerStatsTier Tier)>();
        foreach (var (inst, tiers) in perInstrumentTiers)
        {
            PlayerStatsTier? match = null;
            foreach (var t in tiers)
            {
                if (t.MinLeeway is null || (minLeeway.HasValue && t.MinLeeway.Value <= minLeeway.Value))
                    match = t;
                else
                    break;
            }
            if (match is not null)
                matchingTiers.Add((inst, match));
        }

        // Aggregate across instruments
        var overall = new PlayerStatsTier { MinLeeway = minLeeway };
        var allSongIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int bestRank = 0;
        string? bestRankSongId = null;
        string? bestRankInstrument = null;

        foreach (var (inst, t) in matchingTiers)
        {
            overall.FcCount += t.FcCount;
            overall.GoldStarCount += t.GoldStarCount;
            overall.FiveStarCount += t.FiveStarCount;
            overall.FourStarCount += t.FourStarCount;
            overall.ThreeStarCount += t.ThreeStarCount;
            overall.TwoStarCount += t.TwoStarCount;
            overall.OneStarCount += t.OneStarCount;
            overall.OverThresholdCount += t.OverThresholdCount;
            overall.TotalScore += t.TotalScore;

            if (t.BestRank > 0 && (bestRank == 0 || t.BestRank < bestRank))
            {
                bestRank = t.BestRank;
                bestRankSongId = t.BestRankSongId;
                bestRankInstrument = inst;
            }

            // We can't simply sum SongsPlayed — a song played on multiple instruments counts once for overall unique songs.
            // For per-instrument sums that don't need dedup, we accumulate directly.
        }

        // SongsPlayed for overall = unique songs across all instruments
        // We need song-level data for this. For now, sum per-instrument and note it's a total-scores count.
        // The frontend historically used unique songIds from all scores, so we match that.
        int totalScoresCount = matchingTiers.Sum(m => m.Tier.SongsPlayed);
        overall.SongsPlayed = totalScoresCount;

        overall.BestRank = bestRank;
        overall.BestRankSongId = bestRankSongId;
        overall.BestRankInstrument = bestRankInstrument;

        // Weighted averages
        double accWeightedSum = 0; int accTotalCount = 0;
        double starsWeightedSum = 0; int starsTotalCount = 0;
        foreach (var (_, t) in matchingTiers)
        {
            if (t.AvgAccuracy > 0) { accWeightedSum += t.AvgAccuracy * t.SongsPlayed; accTotalCount += t.SongsPlayed; }
            if (t.AverageStars > 0) { starsWeightedSum += t.AverageStars * t.SongsPlayed; starsTotalCount += t.SongsPlayed; }
        }
        overall.AvgAccuracy = accTotalCount > 0 ? accWeightedSum / accTotalCount : 0;
        overall.BestAccuracy = matchingTiers.Max(m => m.Tier.BestAccuracy);
        overall.AverageStars = starsTotalCount > 0 ? Math.Round(starsWeightedSum / starsTotalCount, 2) : 0;
        overall.AvgScore = totalScoresCount > 0 ? Math.Round((double)overall.TotalScore / totalScoresCount, 2) : 0;
        overall.FcPercent = totalScoresCount > 0 ? Math.Round((double)overall.FcCount / totalScoresCount * 100.0, 1) : 0;
        overall.CompletionPercent = totalSongs > 0 ? Math.Round((double)totalScoresCount / totalSongs * 100.0, 1) : 0;

        return overall;
    }

    private static int EffectiveRank(PlayerScoreDto s) =>
        s.ApiRank > 0 ? s.ApiRank : s.Rank;

    internal static string FormatPercentileBucket(double pct)
    {
        foreach (var bucket in PercentileBuckets)
        {
            if (pct <= bucket) return $"Top {bucket}%";
        }
        return "Top 100%";
    }

    private sealed class ClassifiedScore
    {
        public required PlayerScoreDto Score { get; init; }
        public double? MinLeeway { get; init; }
        public ValidScoreFallback? Fallback { get; init; }
    }

    private sealed class EffectiveScore
    {
        public required string SongId { get; init; }
        public int Score { get; init; }
        public int Accuracy { get; init; }
        public bool IsFullCombo { get; init; }
        public int Stars { get; init; }
        public int Rank { get; init; }
        public int TotalEntries { get; init; }
        public double Percentile { get; init; }
    }
}
