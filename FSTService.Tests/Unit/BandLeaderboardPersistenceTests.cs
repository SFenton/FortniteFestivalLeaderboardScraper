using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class BandLeaderboardPersistenceTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void UpsertBandEntriesDirect_CanDeferAndConsolidateMembershipRebuild()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());

        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-a", "acct-b"], "0:1", 1_000), rebuildTeamMembership: false);
        UpsertDirect(persistence, "song-b", MakeBandEntry(["acct-b", "acct-a"], "0:1", 1_200), rebuildTeamMembership: false);

        Assert.Equal(0, CountMembershipRows());

        var rebuilt = persistence.RebuildBandTeamMembershipForTeams("Band_Duets", ["acct-a:acct-b"]);

        Assert.Equal(2, rebuilt);
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT account_id, team_key, instrument_combo, appearance_count
            FROM band_team_membership
            ORDER BY account_id
            """;
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("acct-a", reader.GetString(0));
        Assert.Equal("acct-a:acct-b", reader.GetString(1));
        Assert.Equal("0:1", reader.GetString(2));
        Assert.Equal(2, reader.GetInt32(3));

        Assert.True(reader.Read());
        Assert.Equal("acct-b", reader.GetString(0));
        Assert.Equal("acct-a:acct-b", reader.GetString(1));
        Assert.Equal("0:1", reader.GetString(2));
        Assert.Equal(2, reader.GetInt32(3));

        Assert.False(reader.Read());
    }

    private void UpsertDirect(
        BandLeaderboardPersistence persistence,
        string songId,
        BandLeaderboardEntry entry,
        bool rebuildTeamMembership)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var tx = conn.BeginTransaction();
        persistence.UpsertBandEntriesDirect(
            songId,
            "Band_Duets",
            [entry],
            conn,
            tx,
            rebuildTeamMembership: rebuildTeamMembership);
        tx.Commit();
    }

    private long CountMembershipRows()
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM band_team_membership";
        return (long)cmd.ExecuteScalar()!;
    }

    private static BandLeaderboardEntry MakeBandEntry(string[] teamMembers, string instrumentCombo, int score)
    {
        var sortedMembers = teamMembers.OrderBy(static member => member, StringComparer.OrdinalIgnoreCase).ToArray();
        return new BandLeaderboardEntry
        {
            TeamKey = string.Join(':', sortedMembers),
            TeamMembers = sortedMembers,
            InstrumentCombo = instrumentCombo,
            Score = score,
            Accuracy = 950_000,
            IsFullCombo = true,
            Stars = 5,
            Difficulty = 3,
            Season = 1,
            Rank = 1,
            Percentile = 0.1,
            Source = "test",
            MemberStats =
            [
                new BandMemberStats
                {
                    MemberIndex = 0,
                    AccountId = sortedMembers[0],
                    InstrumentId = 0,
                    Score = score / 2,
                    Accuracy = 950_000,
                    IsFullCombo = true,
                    Stars = 5,
                    Difficulty = 3,
                },
                new BandMemberStats
                {
                    MemberIndex = 1,
                    AccountId = sortedMembers[1],
                    InstrumentId = 1,
                    Score = score / 2,
                    Accuracy = 950_000,
                    IsFullCombo = true,
                    Stars = 5,
                    Difficulty = 3,
                },
            ],
        };
    }
}
