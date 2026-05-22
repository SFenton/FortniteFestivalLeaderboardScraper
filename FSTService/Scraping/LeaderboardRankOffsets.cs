using FSTService.Persistence;

namespace FSTService.Scraping;

public sealed class LeaderboardRankOffsetData
{
    public string SongId { get; init; } = "";
    public string Instrument { get; init; } = "";
    public int MaxScore { get; init; }
    public int MinLeewayTenths { get; init; } = LeaderboardRankOffsetCalculator.MinLeewayTenths;
    public int MaxLeewayTenths { get; init; } = LeaderboardRankOffsetCalculator.MaxLeewayTenths;
    public int StepTenths { get; init; } = LeaderboardRankOffsetCalculator.StepTenths;
    public int[] Removed { get; init; } = [];
    public bool[] Exact { get; init; } = [];
    public string GeneratedAt { get; init; } = "";
}

public static class LeaderboardRankOffsetCalculator
{
    public const int MinLeewayTenths = -50;
    public const int MaxLeewayTenths = 50;
    public const int StepTenths = 1;

    public static LeaderboardRankOffsetData Compute(
        string songId,
        string instrument,
        int maxScore,
        IInstrumentDatabase db)
    {
        var lowerBound = CalculateThreshold(maxScore, MinLeewayTenths);
        var upperBound = CalculateThreshold(maxScore, MaxLeewayTenths);
        return Compute(
            songId,
            instrument,
            maxScore,
            db.GetCurrentStateRankOffsetCoverage(songId),
            db.GetCurrentStatePopulationAtOrBelow(songId, lowerBound),
            db.GetCurrentStateScoresInBand(songId, lowerBound, upperBound));
    }

    public static LeaderboardRankOffsetData Compute(
        string songId,
        string instrument,
        int maxScore,
        (int TotalCount, int? MaxScore, int? MinScrapeScore) coverage,
        int baseCount,
        IReadOnlyList<int> bandScores)
    {
        var bucketCount = ((MaxLeewayTenths - MinLeewayTenths) / StepTenths) + 1;
        var removed = new int[bucketCount];
        var exact = new bool[bucketCount];

        if (coverage.TotalCount <= 0)
        {
            Array.Fill(exact, true);
            return new LeaderboardRankOffsetData
            {
                SongId = songId,
                Instrument = instrument,
                MaxScore = maxScore,
                Removed = removed,
                Exact = exact,
                GeneratedAt = DateTime.UtcNow.ToString("o"),
            };
        }

        var bandIndex = 0;
        for (var leewayTenths = MinLeewayTenths; leewayTenths <= MaxLeewayTenths; leewayTenths += StepTenths)
        {
            var bucketIndex = ToBucketIndex(leewayTenths);
            var threshold = CalculateThreshold(maxScore, leewayTenths);
            while (bandIndex < bandScores.Count && bandScores[bandIndex] <= threshold)
                bandIndex++;

            var atOrBelow = baseCount + bandIndex;
            removed[bucketIndex] = Math.Max(0, coverage.TotalCount - atOrBelow);
            exact[bucketIndex] = IsExactAtThreshold(coverage, threshold);
        }

        return new LeaderboardRankOffsetData
        {
            SongId = songId,
            Instrument = instrument,
            MaxScore = maxScore,
            Removed = removed,
            Exact = exact,
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
    }

    public static int CalculateThreshold(int maxScore, int leewayTenths)
        => (int)(maxScore * (1.0 + leewayTenths / 1000.0));

    public static int ToLeewayTenths(double leeway)
        => Math.Clamp((int)Math.Round(leeway * 10.0, MidpointRounding.AwayFromZero), MinLeewayTenths, MaxLeewayTenths);

    public static bool TryGetExactRemovedAbove(LeaderboardRankOffsetData? data, double leeway, out int removed)
    {
        removed = 0;
        if (data is null) return false;

        var bucketIndex = ToBucketIndex(ToLeewayTenths(leeway));
        if (bucketIndex < 0 || bucketIndex >= data.Removed.Length || bucketIndex >= data.Exact.Length)
            return false;

        if (!data.Exact[bucketIndex])
            return false;

        removed = data.Removed[bucketIndex];
        return true;
    }

    private static int ToBucketIndex(int leewayTenths)
        => (leewayTenths - MinLeewayTenths) / StepTenths;

    private static bool IsExactAtThreshold((int TotalCount, int? MaxScore, int? MinScrapeScore) coverage, int threshold)
    {
        if (coverage.TotalCount <= 0) return true;
        if (coverage.MaxScore is null || coverage.MaxScore.Value <= threshold) return true;
        return coverage.MinScrapeScore.HasValue && coverage.MinScrapeScore.Value <= threshold;
    }
}