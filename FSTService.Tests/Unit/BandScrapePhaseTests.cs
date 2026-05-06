using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class BandScrapePhaseTests
{
    [Fact]
    public void ApplyChOptValidation_DoesNotFlagScoreBetweenValidCutoffAndOverThreshold()
    {
        var entry = MakeEntry(instrumentId: 2, score: 100_000);
        var maxScores = new SongMaxScores { MaxVocalsScore = 100_000 };

        BandScrapePhase.ApplyChOptValidation(entry, maxScores, overThresholdMultiplier: 1.05);

        Assert.False(entry.IsOverThreshold);
    }

    [Fact]
    public void ApplyChOptValidation_FlagsScoreAboveOverThresholdMultiplier()
    {
        var entry = MakeEntry(instrumentId: 2, score: 105_001);
        var maxScores = new SongMaxScores { MaxVocalsScore = 100_000 };

        BandScrapePhase.ApplyChOptValidation(entry, maxScores, overThresholdMultiplier: 1.05);

        Assert.True(entry.IsOverThreshold);
    }

    [Fact]
    public void IsWithinChOptValidCutoff_UsesStricterCutoffForScrapeTargetCounting()
    {
        var entry = MakeEntry(instrumentId: 2, score: 95_001);
        var maxScores = new SongMaxScores { MaxVocalsScore = 100_000 };

        var isValidForTarget = BandScrapePhase.IsWithinChOptValidCutoff(entry, maxScores, validCutoffMultiplier: 0.95);

        Assert.False(isValidForTarget);
        Assert.False(entry.IsOverThreshold);
    }

    [Fact]
    public void Validation_IgnoresInstrumentsWithoutChoptMax()
    {
        var entry = MakeEntry(instrumentId: 7, score: 500_000);
        var maxScores = new SongMaxScores { MaxVocalsScore = 100_000 };

        BandScrapePhase.ApplyChOptValidation(entry, maxScores, overThresholdMultiplier: 1.05);
        var isValidForTarget = BandScrapePhase.IsWithinChOptValidCutoff(entry, maxScores, validCutoffMultiplier: 0.95);

        Assert.False(entry.IsOverThreshold);
        Assert.True(isValidForTarget);
    }

    [Fact]
    public void Validation_IsNoOpWhenMaxScoresOrMemberStatsAreMissing()
    {
        var entryWithoutMaxScores = MakeEntry(instrumentId: 2, score: 500_000);
        var entryWithoutMembers = new BandLeaderboardEntry();
        var maxScores = new SongMaxScores { MaxVocalsScore = 100_000 };

        BandScrapePhase.ApplyChOptValidation(entryWithoutMaxScores, null, overThresholdMultiplier: 1.05);
        BandScrapePhase.ApplyChOptValidation(entryWithoutMembers, maxScores, overThresholdMultiplier: 1.05);

        Assert.False(entryWithoutMaxScores.IsOverThreshold);
        Assert.True(BandScrapePhase.IsWithinChOptValidCutoff(entryWithoutMaxScores, null, validCutoffMultiplier: 0.95));
        Assert.False(entryWithoutMembers.IsOverThreshold);
        Assert.True(BandScrapePhase.IsWithinChOptValidCutoff(entryWithoutMembers, maxScores, validCutoffMultiplier: 0.95));
    }

    private static BandLeaderboardEntry MakeEntry(int instrumentId, int score) => new()
    {
        TeamKey = "acct-a:acct-b",
        TeamMembers = ["acct-a", "acct-b"],
        Score = score,
        InstrumentCombo = instrumentId.ToString(),
        MemberStats =
        [
            new BandMemberStats
            {
                MemberIndex = 0,
                AccountId = "acct-a",
                InstrumentId = instrumentId,
                Score = score,
            },
        ],
    };
}
