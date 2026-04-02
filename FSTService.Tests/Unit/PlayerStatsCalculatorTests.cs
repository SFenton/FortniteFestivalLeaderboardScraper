using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public class PlayerStatsCalculatorTests
{
    private static readonly Dictionary<string, SongMaxScores> EmptyMaxScores = new();
    private static readonly Dictionary<(string, string), long> EmptyPopulation = new();

    private static Dictionary<string, SongMaxScores> MakeMaxScores(params (string SongId, string Instrument, int Max)[] entries)
    {
        var result = new Dictionary<string, SongMaxScores>(StringComparer.OrdinalIgnoreCase);
        foreach (var (songId, instrument, max) in entries)
        {
            if (!result.TryGetValue(songId, out var ms))
            {
                ms = new SongMaxScores();
                result[songId] = ms;
            }
            ms.SetByInstrument(instrument, max);
        }
        return result;
    }

    private static PlayerScoreDto Score(string songId, int score, int accuracy = 5000, bool fc = false, int stars = 3, int rank = 10, int apiRank = 0) =>
        new()
        {
            SongId = songId,
            Instrument = "Solo_Guitar",
            Score = score,
            Accuracy = accuracy,
            IsFullCombo = fc,
            Stars = stars,
            Rank = rank,
            ApiRank = apiRank,
        };

    [Fact]
    public void EmptyScores_ReturnsSingleBaseTier()
    {
        var tiers = PlayerStatsCalculator.ComputeTiers([], EmptyMaxScores, "Solo_Guitar", 100, EmptyPopulation);

        Assert.Single(tiers);
        Assert.Null(tiers[0].MinLeeway);
        Assert.Equal(0, tiers[0].SongsPlayed);
        Assert.Equal(0, tiers[0].CompletionPercent);
    }

    [Fact]
    public void AllScoresBelowMax_SingleTier()
    {
        var scores = new List<PlayerScoreDto>
        {
            Score("song1", 90000, fc: true, stars: 6, accuracy: 10000),
            Score("song2", 80000, stars: 5, accuracy: 8000),
            Score("song3", 70000, stars: 4, accuracy: 7000),
        };
        var maxScores = MakeMaxScores(("song1", "Solo_Guitar", 100000), ("song2", "Solo_Guitar", 100000), ("song3", "Solo_Guitar", 100000));

        var tiers = PlayerStatsCalculator.ComputeTiers(scores, maxScores, "Solo_Guitar", 10, EmptyPopulation);

        Assert.Single(tiers);
        var t = tiers[0];
        Assert.Null(t.MinLeeway);
        Assert.Equal(3, t.SongsPlayed);
        Assert.Equal(0, t.OverThresholdCount);
        Assert.Equal(1, t.FcCount);
        Assert.Equal(1, t.GoldStarCount);
        Assert.Equal(1, t.FiveStarCount);
        Assert.Equal(1, t.FourStarCount);
        Assert.Equal(240000, t.TotalScore);
        Assert.Equal(30.0, t.CompletionPercent);
    }

    [Fact]
    public void OneScoreAboveMax_CreatesTwoTiers()
    {
        var scores = new List<PlayerScoreDto>
        {
            Score("song1", 90000),   // below max
            Score("song2", 110000),  // 10% above max of 100000 → minLeeway = 10.0
        };
        var maxScores = MakeMaxScores(("song1", "Solo_Guitar", 100000), ("song2", "Solo_Guitar", 100000));

        var tiers = PlayerStatsCalculator.ComputeTiers(scores, maxScores, "Solo_Guitar", 10, EmptyPopulation);

        Assert.Equal(2, tiers.Count);

        // Base tier: only song1 (song2 excluded — no fallback for non-registered)
        var baseTier = tiers[0];
        Assert.Null(baseTier.MinLeeway);
        Assert.Equal(1, baseTier.SongsPlayed);
        Assert.Equal(1, baseTier.OverThresholdCount);

        // Second tier at leeway=10: both songs
        var tier2 = tiers[1];
        Assert.Equal(10.0, tier2.MinLeeway);
        Assert.Equal(2, tier2.SongsPlayed);
        Assert.Equal(0, tier2.OverThresholdCount);
    }

    [Fact]
    public void MultipleBreakpoints_CorrectOrdering()
    {
        var scores = new List<PlayerScoreDto>
        {
            Score("song1", 90000),   // below max
            Score("song2", 102000),  // 2% above → minLeeway = 2.0
            Score("song3", 105000),  // 5% above → minLeeway = 5.0
        };
        var maxScores = MakeMaxScores(
            ("song1", "Solo_Guitar", 100000),
            ("song2", "Solo_Guitar", 100000),
            ("song3", "Solo_Guitar", 100000));

        var tiers = PlayerStatsCalculator.ComputeTiers(scores, maxScores, "Solo_Guitar", 10, EmptyPopulation);

        Assert.Equal(3, tiers.Count);
        Assert.Null(tiers[0].MinLeeway);
        Assert.Equal(1, tiers[0].SongsPlayed);     // only song1
        Assert.Equal(2, tiers[0].OverThresholdCount);

        Assert.Equal(2.0, tiers[1].MinLeeway);
        Assert.Equal(2, tiers[1].SongsPlayed);     // song1 + song2
        Assert.Equal(1, tiers[1].OverThresholdCount);

        Assert.Equal(5.0, tiers[2].MinLeeway);
        Assert.Equal(3, tiers[2].SongsPlayed);     // all songs
        Assert.Equal(0, tiers[2].OverThresholdCount);
    }

    [Fact]
    public void FallbackScore_UsedForRegisteredUsers()
    {
        var scores = new List<PlayerScoreDto>
        {
            Score("song1", 90000, fc: true, stars: 6),
            Score("song2", 110000, fc: true, stars: 6), // over threshold
        };
        var maxScores = MakeMaxScores(("song1", "Solo_Guitar", 100000), ("song2", "Solo_Guitar", 100000));

        var fallbacks = new Dictionary<(string, string), List<ValidScoreFallback>>
        {
            [("song2", "Solo_Guitar")] = [new ValidScoreFallback { Score = 95000, Accuracy = 8000, IsFullCombo = false, Stars = 5 }]
        };

        var tiers = PlayerStatsCalculator.ComputeTiers(scores, maxScores, "Solo_Guitar", 10, EmptyPopulation, fallbacks);

        Assert.Equal(2, tiers.Count);

        // Base tier: song1 original + song2 fallback
        var baseTier = tiers[0];
        Assert.Equal(2, baseTier.SongsPlayed);      // both present (fallback replaces)
        Assert.Equal(1, baseTier.OverThresholdCount);
        Assert.Equal(1, baseTier.FcCount);           // only song1 is FC (fallback is not)
        Assert.Equal(1, baseTier.GoldStarCount);     // only song1 has gold stars
        Assert.Equal(1, baseTier.FiveStarCount);     // fallback has 5 stars
    }

    [Fact]
    public void NoMaxScore_ScoreAlwaysValid()
    {
        var scores = new List<PlayerScoreDto>
        {
            Score("song1", 999999), // very high but no CHOpt max defined
        };

        var tiers = PlayerStatsCalculator.ComputeTiers(scores, EmptyMaxScores, "Solo_Guitar", 10, EmptyPopulation);

        Assert.Single(tiers);
        Assert.Equal(1, tiers[0].SongsPlayed);
        Assert.Equal(0, tiers[0].OverThresholdCount);
    }

    [Fact]
    public void StarDistribution_AllBuckets()
    {
        var scores = new List<PlayerScoreDto>
        {
            Score("s1", 100, stars: 6),
            Score("s2", 100, stars: 6),
            Score("s3", 100, stars: 5),
            Score("s4", 100, stars: 4),
            Score("s5", 100, stars: 3),
            Score("s6", 100, stars: 2),
            Score("s7", 100, stars: 1),
        };

        var tiers = PlayerStatsCalculator.ComputeTiers(scores, EmptyMaxScores, "Solo_Guitar", 100, EmptyPopulation);

        var t = tiers[0];
        Assert.Equal(2, t.GoldStarCount);
        Assert.Equal(1, t.FiveStarCount);
        Assert.Equal(1, t.FourStarCount);
        Assert.Equal(1, t.ThreeStarCount);
        Assert.Equal(1, t.TwoStarCount);
        Assert.Equal(1, t.OneStarCount);
    }

    [Fact]
    public void FcPercent_Calculated()
    {
        var scores = new List<PlayerScoreDto>
        {
            Score("s1", 100, fc: true),
            Score("s2", 100, fc: true),
            Score("s3", 100, fc: false),
            Score("s4", 100, fc: false),
        };

        var tiers = PlayerStatsCalculator.ComputeTiers(scores, EmptyMaxScores, "Solo_Guitar", 10, EmptyPopulation);

        Assert.Equal(2, tiers[0].FcCount);
        Assert.Equal(50.0, tiers[0].FcPercent);
    }

    [Fact]
    public void CompletionPercent_RelativeToTotalSongs()
    {
        var scores = new List<PlayerScoreDto>
        {
            Score("s1", 100),
            Score("s2", 100),
        };

        var tiers = PlayerStatsCalculator.ComputeTiers(scores, EmptyMaxScores, "Solo_Guitar", 200, EmptyPopulation);

        Assert.Equal(1.0, tiers[0].CompletionPercent);
    }

    [Fact]
    public void OverallTiers_AggregatesAcrossInstruments()
    {
        var guitarTiers = new List<PlayerStatsTier>
        {
            new() { MinLeeway = null, SongsPlayed = 10, FcCount = 3, GoldStarCount = 2, TotalScore = 500000, BestRank = 5, BestRankSongId = "s1", AvgAccuracy = 8500, AverageStars = 4.5 },
        };
        var bassTiers = new List<PlayerStatsTier>
        {
            new() { MinLeeway = null, SongsPlayed = 8, FcCount = 2, GoldStarCount = 1, TotalScore = 400000, BestRank = 3, BestRankSongId = "s2", AvgAccuracy = 9000, AverageStars = 5.0 },
        };

        var perInstrument = new Dictionary<string, List<PlayerStatsTier>>
        {
            ["Solo_Guitar"] = guitarTiers,
            ["Solo_Bass"] = bassTiers,
        };

        var overall = PlayerStatsCalculator.ComputeOverallTiers(perInstrument, 100);

        Assert.Single(overall);
        var t = overall[0];
        Assert.Null(t.MinLeeway);
        Assert.Equal(18, t.SongsPlayed);     // 10 + 8
        Assert.Equal(5, t.FcCount);          // 3 + 2
        Assert.Equal(3, t.GoldStarCount);    // 2 + 1
        Assert.Equal(900000, t.TotalScore);  // 500K + 400K
        Assert.Equal(3, t.BestRank);         // bass has rank 3 < guitar rank 5
        Assert.Equal("s2", t.BestRankSongId);
        Assert.Equal("02", t.BestRankInstrument);
    }

    [Fact]
    public void FormatPercentileBucket_MatchesFrontend()
    {
        Assert.Equal("Top 1%", PlayerStatsCalculator.FormatPercentileBucket(0.5));
        Assert.Equal("Top 1%", PlayerStatsCalculator.FormatPercentileBucket(1.0));
        Assert.Equal("Top 2%", PlayerStatsCalculator.FormatPercentileBucket(1.5));
        Assert.Equal("Top 5%", PlayerStatsCalculator.FormatPercentileBucket(5.0));
        Assert.Equal("Top 10%", PlayerStatsCalculator.FormatPercentileBucket(7.0));
        Assert.Equal("Top 50%", PlayerStatsCalculator.FormatPercentileBucket(45.0));
        Assert.Equal("Top 100%", PlayerStatsCalculator.FormatPercentileBucket(100.0));
        Assert.Equal("Top 100%", PlayerStatsCalculator.FormatPercentileBucket(150.0));
    }
}
