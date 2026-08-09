using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class LeaderboardRivalsRecomputeCommandTests
{
    [Fact]
    public void ParserAcceptsSeparateAndInlineAccountIds()
    {
        Assert.Null(LeaderboardRivalsRecomputeCommand.Parse([]));

        Assert.Equal(
            "acct-separate",
            LeaderboardRivalsRecomputeCommand.Parse(
                [
                    LeaderboardRivalsRecomputeCommand.AccountFlag,
                    "acct-separate",
                ])?.AccountId);
        Assert.Equal(
            "acct-inline",
            LeaderboardRivalsRecomputeCommand.Parse(
                [
                    $"{LeaderboardRivalsRecomputeCommand.AccountFlag}=acct-inline",
                ])?.AccountId);
    }

    [Fact]
    public void ParserRejectsMissingOrDuplicateAccountIds()
    {
        Assert.Throws<ArgumentException>(() =>
            LeaderboardRivalsRecomputeCommand.Parse(
                [LeaderboardRivalsRecomputeCommand.AccountFlag]));
        Assert.Throws<ArgumentException>(() =>
            LeaderboardRivalsRecomputeCommand.Parse(
            [
                LeaderboardRivalsRecomputeCommand.AccountFlag,
                "acct-one",
                $"{LeaderboardRivalsRecomputeCommand.AccountFlag}=acct-two",
            ]));
    }
}
