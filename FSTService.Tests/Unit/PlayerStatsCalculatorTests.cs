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
    public void AllScoresBelowMax_NearMaxGetBreakpoints()
    {
        var scores = new List<PlayerScoreDto>
        {
            Score("song1", 90000, fc: true, stars: 6, accuracy: 10000),  // -10% → null (below -5%)
            Score("song2", 80000, stars: 5, accuracy: 8000),             // -20% → null
            Score("song3", 70000, stars: 4, accuracy: 7000),             // -30% → null
        };
        var maxScores = MakeMaxScores(("song1", "Solo_Guitar", 100000), ("song2", "Solo_Guitar", 100000), ("song3", "Solo_Guitar", 100000));

        var tiers = PlayerStatsCalculator.ComputeTiers(scores, maxScores, "Solo_Guitar", 10, EmptyPopulation);

        // All scores are more than 5% below max, so all get null minLeeway → single base tier
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
    public void OneScoreAboveMax_CreatesTiersWithCap()
    {
        var scores = new List<PlayerScoreDto>
        {
            Score("song1", 90000),   // -10% → null (well below max)
            Score("song2", 110000),  // 10% above max of 100000 → minLeeway = 10.0 (above cap)
        };
        var maxScores = MakeMaxScores(("song1", "Solo_Guitar", 100000), ("song2", "Solo_Guitar", 100000));

        var tiers = PlayerStatsCalculator.ComputeTiers(scores, maxScores, "Solo_Guitar", 10, EmptyPopulation);

        // Only base tier — the 10% breakpoint exceeds the 5.0 cap, so no tier is created for it
        Assert.Single(tiers);

        var baseTier = tiers[0];
        Assert.Null(baseTier.MinLeeway);
        Assert.Equal(1, baseTier.SongsPlayed);       // only song1
        Assert.Equal(1, baseTier.OverThresholdCount); // song2 excluded
    }

    [Fact]
    public void MultipleBreakpoints_CappedAndOrdered()
    {
        var scores = new List<PlayerScoreDto>
        {
            Score("song1", 90000),   // -10% → null (well below max)
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
    public void FallbackScore_OnlyUsedForPositiveLeeway()
    {
        var scores = new List<PlayerScoreDto>
        {
            Score("song1", 90000, fc: true, stars: 6),   // -10% → null
            Score("song2", 110000, fc: true, stars: 6),  // +10% → over cap, no tier
        };
        var maxScores = MakeMaxScores(("song1", "Solo_Guitar", 100000), ("song2", "Solo_Guitar", 100000));

        var fallbacks = new Dictionary<(string, string), List<ValidScoreFallback>>
        {
            [("song2", "Solo_Guitar")] = [new ValidScoreFallback { Score = 95000, Accuracy = 8000, IsFullCombo = false, Stars = 5 }]
        };

        var tiers = PlayerStatsCalculator.ComputeTiers(scores, maxScores, "Solo_Guitar", 10, EmptyPopulation, fallbacks);

        // Only base tier (10% breakpoint is above 5.0 cap, but fallback still applies)
        Assert.Single(tiers);

        var baseTier = tiers[0];
        Assert.Equal(2, baseTier.SongsPlayed);      // both present (fallback replaces song2)
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

    [Fact]
    public void NegativeLeeway_ScoresNearMaxGetBreakpoints()
    {
        // Scores within 5% of CHOpt max should get negative minLeeway breakpoints
        var scores = new List<PlayerScoreDto>
        {
            Score("song1", 50000),   // -50% → null (well below max)
            Score("song2", 97000),   // -3% → minLeeway = -3.0
            Score("song3", 99000),   // -1% → minLeeway = -1.0
            Score("song4", 100000),  //  0% → minLeeway = 0.0
        };
        var maxScores = MakeMaxScores(
            ("song1", "Solo_Guitar", 100000),
            ("song2", "Solo_Guitar", 100000),
            ("song3", "Solo_Guitar", 100000),
            ("song4", "Solo_Guitar", 100000));

        var tiers = PlayerStatsCalculator.ComputeTiers(scores, maxScores, "Solo_Guitar", 10, EmptyPopulation);

        Assert.Equal(4, tiers.Count);

        // Base tier: only song1 (null minLeeway)
        Assert.Null(tiers[0].MinLeeway);
        Assert.Equal(1, tiers[0].SongsPlayed);
        Assert.Equal(3, tiers[0].OverThresholdCount);

        // Tier at -3.0: song1 + song2
        Assert.Equal(-3.0, tiers[1].MinLeeway);
        Assert.Equal(2, tiers[1].SongsPlayed);
        Assert.Equal(2, tiers[1].OverThresholdCount);

        // Tier at -1.0: song1 + song2 + song3
        Assert.Equal(-1.0, tiers[2].MinLeeway);
        Assert.Equal(3, tiers[2].SongsPlayed);
        Assert.Equal(1, tiers[2].OverThresholdCount);

        // Tier at 0.0: all songs
        Assert.Equal(0.0, tiers[3].MinLeeway);
        Assert.Equal(4, tiers[3].SongsPlayed);
        Assert.Equal(0, tiers[3].OverThresholdCount);
    }

    [Fact]
    public void LeewayBreakpoints_CappedAtFivePercent()
    {
        // Scores with leeway > 5% should NOT create tiers beyond 5.0
        var scores = new List<PlayerScoreDto>
        {
            Score("song1", 50000),   // -50% → null
            Score("song2", 103000),  // +3% → tier at 3.0
            Score("song3", 108000),  // +8% → no tier (above cap)
            Score("song4", 120000),  // +20% → no tier (above cap)
        };
        var maxScores = MakeMaxScores(
            ("song1", "Solo_Guitar", 100000),
            ("song2", "Solo_Guitar", 100000),
            ("song3", "Solo_Guitar", 100000),
            ("song4", "Solo_Guitar", 100000));

        var tiers = PlayerStatsCalculator.ComputeTiers(scores, maxScores, "Solo_Guitar", 10, EmptyPopulation);

        Assert.Equal(2, tiers.Count);
        Assert.Null(tiers[0].MinLeeway);
        Assert.Equal(3.0, tiers[1].MinLeeway);

        // At the 3.0 tier: song1 (null) + song2 (3.0 <= 3.0) = 2 songs
        // song3 (8.0 > 3.0) and song4 (20.0 > 3.0) are still over threshold
        Assert.Equal(2, tiers[1].SongsPlayed);
        Assert.Equal(2, tiers[1].OverThresholdCount);
    }

    [Fact]
    public void ScoreExactlyAtMax_GetsZeroBreakpoint()
    {
        var scores = new List<PlayerScoreDto>
        {
            Score("song1", 100000),  // exactly at max → minLeeway = 0.0
        };
        var maxScores = MakeMaxScores(("song1", "Solo_Guitar", 100000));

        var tiers = PlayerStatsCalculator.ComputeTiers(scores, maxScores, "Solo_Guitar", 10, EmptyPopulation);

        Assert.Equal(2, tiers.Count);
        Assert.Null(tiers[0].MinLeeway);
        Assert.Equal(0, tiers[0].SongsPlayed);       // excluded from base tier
        Assert.Equal(1, tiers[0].OverThresholdCount);

        Assert.Equal(0.0, tiers[1].MinLeeway);
        Assert.Equal(1, tiers[1].SongsPlayed);       // included at 0.0 tier
        Assert.Equal(0, tiers[1].OverThresholdCount);
    }

    [Fact]
    public void NegativeLeeway_FallbackNotUsed()
    {
        // Fallbacks should only apply for positive leeway (over-max scores)
        var scores = new List<PlayerScoreDto>
        {
            Score("song1", 99000, fc: true, stars: 6),  // -1% → minLeeway = -1.0
        };
        var maxScores = MakeMaxScores(("song1", "Solo_Guitar", 100000));

        var fallbacks = new Dictionary<(string, string), List<ValidScoreFallback>>
        {
            [("song1", "Solo_Guitar")] = [new ValidScoreFallback { Score = 90000, Accuracy = 7000, IsFullCombo = false, Stars = 4 }]
        };

        var tiers = PlayerStatsCalculator.ComputeTiers(scores, maxScores, "Solo_Guitar", 10, EmptyPopulation, fallbacks);

        Assert.Equal(2, tiers.Count); // base (null) + tier at -1.0

        // Base tier: song1 is excluded (minLeeway = -1.0, not null) but NO fallback substituted
        Assert.Equal(0, tiers[0].SongsPlayed);
        Assert.Equal(1, tiers[0].OverThresholdCount);

        // Tier at -1.0: song1 included with its original score
        Assert.Equal(1, tiers[1].SongsPlayed);
        Assert.Equal(99000, tiers[1].TotalScore); // original score, not fallback
    }

    [Fact]
    public void OverallTiers_CappedBreakpoints()
    {
        var guitarTiers = new List<PlayerStatsTier>
        {
            new() { MinLeeway = null, SongsPlayed = 5 },
            new() { MinLeeway = -2.0, SongsPlayed = 7 },
            new() { MinLeeway = 3.0, SongsPlayed = 10 },
            new() { MinLeeway = 8.0, SongsPlayed = 12 },  // should be excluded (> 5.0)
        };
        var bassTiers = new List<PlayerStatsTier>
        {
            new() { MinLeeway = null, SongsPlayed = 4 },
            new() { MinLeeway = -7.0, SongsPlayed = 5 },  // should be excluded (< -5.0)
            new() { MinLeeway = 1.0, SongsPlayed = 6 },
        };

        var perInstrument = new Dictionary<string, List<PlayerStatsTier>>
        {
            ["Solo_Guitar"] = guitarTiers,
            ["Solo_Bass"] = bassTiers,
        };

        var overall = PlayerStatsCalculator.ComputeOverallTiers(perInstrument, 100);

        // Should have: null, -2.0, 1.0, 3.0 (no 8.0 or -7.0)
        Assert.Equal(4, overall.Count);
        Assert.Null(overall[0].MinLeeway);
        Assert.Equal(-2.0, overall[1].MinLeeway);
        Assert.Equal(1.0, overall[2].MinLeeway);
        Assert.Equal(3.0, overall[3].MinLeeway);
    }
}
