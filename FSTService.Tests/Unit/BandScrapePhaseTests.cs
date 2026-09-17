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

    [Fact]
    public void ProductionBandPaginationKeepsConfiguredAndValidTargetSemantics()
    {
        Assert.False(
            LeaderboardPaginationPlanner
                .ShouldFetchBandPage(
                    nextPage: 400,
                    providerReportedTotalPages: 500,
                    configuredMaximumPages: 400,
                    validEntryCount: 0,
                    validEntryTarget: 0));
        Assert.True(
            LeaderboardPaginationPlanner
                .ShouldFetchBandPage(
                    nextPage: 400,
                    providerReportedTotalPages: 500,
                    configuredMaximumPages: 400,
                    validEntryCount: 9_999,
                    validEntryTarget: 10_000));
        Assert.False(
            LeaderboardPaginationPlanner
                .ShouldFetchBandPage(
                    nextPage: 400,
                    providerReportedTotalPages: 500,
                    configuredMaximumPages: 400,
                    validEntryCount: 10_000,
                    validEntryTarget: 10_000));
    }

    [Fact]
    public void ProductionBandIdentityIncludesTeamAndInstrumentCombo()
    {
        var first = MakeEntry(
            instrumentId: 0,
            score: 100);
        var duplicate = MakeEntry(
            instrumentId: 0,
            score: 200);
        var differentCombo = MakeEntry(
            instrumentId: 1,
            score: 300);

        Assert.Equal(
            2,
            new[]
                {
                    first,
                    duplicate,
                    differentCombo,
                }
                .GroupBy(
                    LeaderboardEntryIdentity.Band,
                    LeaderboardEntryIdentity
                        .BandComparer)
                .Count());
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
