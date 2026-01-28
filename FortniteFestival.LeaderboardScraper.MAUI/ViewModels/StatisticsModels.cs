using System.Collections.ObjectModel;

namespace FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

/// <summary>
/// Detailed statistics for a single instrument.
/// </summary>
public class InstrumentDetailedStats
{
    public string InstrumentKey { get; set; } = string.Empty;
    public string InstrumentLabel { get; set; } = string.Empty;
    public string Icon => $"{InstrumentKey}.png";

    // Song counts
    public int TotalSongsInLibrary { get; set; }
    public int SongsPlayed { get; set; }
    public int SongsUnplayed => TotalSongsInLibrary - SongsPlayed;
    public double CompletionPercent => TotalSongsInLibrary > 0 ? (SongsPlayed * 100.0 / TotalSongsInLibrary) : 0;

    // Full Combos
    public int FcCount { get; set; }
    public double FcPercent => SongsPlayed > 0 ? (FcCount * 100.0 / SongsPlayed) : 0;

    // Stars
    public int GoldStarCount { get; set; } // 6 stars
    public int FiveStarCount { get; set; }
    public int FourStarCount { get; set; }
    public int ThreeOrLessStarCount { get; set; }
    public double AverageStars { get; set; }

    // Accuracy
    public double AverageAccuracy { get; set; } // as percentage (0-100)
    public double BestAccuracy { get; set; }
    public int PerfectScoreCount { get; set; } // 100% accuracy

    // Scores
    public long TotalScore { get; set; }
    public int HighestScore { get; set; }
    public double AverageScore { get; set; }

    // Leaderboard / Percentile
    public int BestRank { get; set; } // closest to #1
    public double AveragePercentile { get; set; } // raw (0-1 scale, smaller is better)
    public double WeightedPercentile { get; set; }
    public string AveragePercentileFormatted => FormatPercentile(AveragePercentile);
    public string WeightedPercentileFormatted => FormatPercentile(WeightedPercentile);
    public string BestRankFormatted => BestRank > 0 ? $"#{BestRank:N0}" : "N/A";

    // Percentile distribution
    public int Top1PercentCount { get; set; }
    public int Top5PercentCount { get; set; }
    public int Top10PercentCount { get; set; }
    public int Top25PercentCount { get; set; }
    public int Top50PercentCount { get; set; }
    public int Below50PercentCount { get; set; }

    private static string FormatPercentile(double raw)
    {
        if (double.IsNaN(raw) || raw <= 0) return "N/A";
        var topPct = Math.Max(1.0, Math.Min(100.0, raw * 100.0));
        int bucket = (int)Math.Round(topPct, MidpointRounding.AwayFromZero);
        return $"Top {bucket}%";
    }
}
