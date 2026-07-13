using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class ScopeCompletenessManifestTests
{
    [Fact]
    public void ContiguousPagesAreComplete()
    {
        var manifest = ScopeCompletenessManifest.Create(
            0,
            2,
            new Dictionary<int, GlobalLeaderboardScraper.FetchStatus>
            {
                [0] = GlobalLeaderboardScraper.FetchStatus.Success,
                [1] = GlobalLeaderboardScraper.FetchStatus.Success,
                [2] = GlobalLeaderboardScraper.FetchStatus.Success,
            },
            Entries("a", "b", "c"),
            reportedTotalPages: 3);

        Assert.True(manifest.IsComplete);
        Assert.Equal([0, 1, 2], manifest.ReceivedPages);
        Assert.Equal("complete", manifest.ParseStatus);
        Assert.False(manifest.RetryExhausted);
    }

    [Fact]
    public void MissingOrMalformedPageIsIncomplete()
    {
        var manifest = ScopeCompletenessManifest.Create(
            0,
            2,
            new Dictionary<int, GlobalLeaderboardScraper.FetchStatus>
            {
                [0] = GlobalLeaderboardScraper.FetchStatus.Success,
                [2] = GlobalLeaderboardScraper.FetchStatus.ParseFailure,
            },
            Entries("a"),
            reportedTotalPages: 3);

        Assert.False(manifest.IsComplete);
        Assert.Equal("failed", manifest.ParseStatus);
        Assert.Contains("expected page 1", manifest.FailureReason);
        Assert.Contains("ParseFailure", manifest.FailureReason);
    }

    [Fact]
    public void RetryExhaustionIsIncompleteAndVisible()
    {
        var manifest = ScopeCompletenessManifest.Create(
            0,
            1,
            new Dictionary<int, GlobalLeaderboardScraper.FetchStatus>
            {
                [0] = GlobalLeaderboardScraper.FetchStatus.Success,
                [1] = GlobalLeaderboardScraper.FetchStatus.RetryExhausted,
            },
            Entries("a"),
            reportedTotalPages: 2);

        Assert.False(manifest.IsComplete);
        Assert.True(manifest.RetryExhausted);
    }

    [Fact]
    public void EpicEmptyAndForbiddenTerminalBoundariesRemainLegitimate()
    {
        var empty = ScopeCompletenessManifest.Create(
            0,
            0,
            new Dictionary<int, GlobalLeaderboardScraper.FetchStatus>
            {
                [0] = GlobalLeaderboardScraper.FetchStatus.Success,
            },
            [],
            reportedTotalPages: 0,
            terminalBoundary: ScopeTerminalBoundaryKind.EpicEmpty);
        var forbidden = ScopeCompletenessManifest.Create(
            0,
            4,
            new Dictionary<int, GlobalLeaderboardScraper.FetchStatus>
            {
                [0] = GlobalLeaderboardScraper.FetchStatus.Success,
                [1] = GlobalLeaderboardScraper.FetchStatus.Success,
                [2] = GlobalLeaderboardScraper.FetchStatus.Forbidden,
                [3] = GlobalLeaderboardScraper.FetchStatus.Forbidden,
                [4] = GlobalLeaderboardScraper.FetchStatus.Forbidden,
            },
            Entries("a", "b"),
            reportedTotalPages: 5,
            terminalBoundary: ScopeTerminalBoundaryKind.EpicForbidden,
            terminalBoundaryPage: 2);

        Assert.True(empty.IsComplete);
        Assert.True(forbidden.IsComplete);
        Assert.Equal(2, forbidden.TerminalBoundaryPage);
    }

    [Fact]
    public void DeepMergeIncludesWaveOneAndExtensionCoverage()
    {
        var wave1Entries = Entries("wave1");
        var deepEntries = Entries("deep1", "deep2");
        var wave1 = ScopeCompletenessManifest.Create(
            0,
            1,
            new Dictionary<int, GlobalLeaderboardScraper.FetchStatus>
            {
                [0] = GlobalLeaderboardScraper.FetchStatus.Success,
                [1] = GlobalLeaderboardScraper.FetchStatus.Success,
            },
            wave1Entries,
            reportedTotalPages: 4,
            deepStartPage: 2);
        var deep = ScopeCompletenessManifest.Create(
            2,
            3,
            new Dictionary<int, GlobalLeaderboardScraper.FetchStatus>
            {
                [2] = GlobalLeaderboardScraper.FetchStatus.Success,
                [3] = GlobalLeaderboardScraper.FetchStatus.Success,
            },
            deepEntries,
            reportedTotalPages: 4,
            deepStartPage: 2,
            deepEndPage: 3);

        var merged = ScopeCompletenessManifest.Merge(
            wave1,
            deep,
            wave1Entries.Concat(deepEntries).ToArray());

        Assert.True(merged.IsComplete);
        Assert.Equal([0, 1, 2, 3], merged.ReceivedPages);
        Assert.Equal(2, merged.DeepStartPage);
        Assert.Equal(3, merged.DeepEndPage);
    }

    [Fact]
    public void ContentFingerprintIncludesBandMemberAndBonusFields()
    {
        var first = EntryWithBandMember(score: 500, memberScore: 200);
        var second = EntryWithBandMember(score: 500, memberScore: 201);
        var statuses = new Dictionary<int, GlobalLeaderboardScraper.FetchStatus>
        {
            [0] = GlobalLeaderboardScraper.FetchStatus.Success,
        };

        var firstManifest = ScopeCompletenessManifest.Create(
            0, 0, statuses, [first], reportedTotalPages: 1);
        var secondManifest = ScopeCompletenessManifest.Create(
            0, 0, statuses, [second], reportedTotalPages: 1);

        Assert.NotEqual(
            firstManifest.ContentFingerprint,
            secondManifest.ContentFingerprint);
    }

    private static LeaderboardEntry[] Entries(params string[] accountIds) =>
        accountIds.Select((accountId, index) => new LeaderboardEntry
        {
            AccountId = accountId,
            Score = 1000 - index,
            Rank = index + 1,
        }).ToArray();

    private static LeaderboardEntry EntryWithBandMember(int score, int memberScore) =>
        new()
        {
            AccountId = "band-account",
            Score = score,
            Rank = 1,
            BandScore = score,
            BaseScore = 400,
            InstrumentBonus = 50,
            OverdriveBonus = 50,
            InstrumentCombo = "0:1",
            BandMembers =
            [
                new BandMemberStats
                {
                    MemberIndex = 0,
                    AccountId = "member-1",
                    InstrumentId = 0,
                    Score = memberScore,
                    Accuracy = 99,
                    IsFullCombo = true,
                    Stars = 5,
                    Difficulty = 3,
                },
            ],
        };
}
