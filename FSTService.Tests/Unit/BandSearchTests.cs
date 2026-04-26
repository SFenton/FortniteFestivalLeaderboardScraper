using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class BandSearchTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    private readonly GlobalLeaderboardPersistence _persistence;

    public BandSearchTests()
    {
        _persistence = new GlobalLeaderboardPersistence(
            _fixture.Db,
            Substitute.For<ILoggerFactory>(),
            Substitute.For<ILogger<GlobalLeaderboardPersistence>>(),
            _fixture.DataSource,
            Options.Create(new FeatureOptions()));
    }

    public void Dispose()
    {
        _persistence.Dispose();
        _fixture.Dispose();
    }

    [Fact]
    public void SearchBands_FreeTextAmbiguousPhrase_UnionsSplitAndFullUsername()
    {
        SeedAccountNames(
            ("acct-sf", "SFentonX"),
            ("acct-jas", "Jasgor9"),
            ("acct-phrase", "SFenton Jasgor9"),
            ("acct-other", "Other Player"));
        SeedBandRows("song-split", "Band_Duets", "acct-jas:acct-sf", "0:1", (0, "acct-sf", 0), (1, "acct-jas", 1));
        SeedBandRows("song-phrase", "Band_Duets", "acct-other:acct-phrase", "0:1", (0, "acct-phrase", 0), (1, "acct-other", 1));

        var response = _persistence.SearchBands("SFenton Jasgor9", null, pageSize: 10);

        Assert.True(response.IsAmbiguous);
        Assert.False(response.NeedsDisambiguation);
        Assert.Equal(2, response.TotalCount);
        Assert.Contains(response.Interpretations, interpretation =>
            interpretation.Terms.Count == 1 && interpretation.Terms[0].Text == "SFenton Jasgor9");
        Assert.Contains(response.Interpretations, interpretation =>
            interpretation.Terms.Select(term => term.Text).SequenceEqual(["SFenton", "Jasgor9"]));
        Assert.Contains(response.Results, result => result.TeamKey == "acct-jas:acct-sf");
        Assert.Contains(response.Results, result => result.TeamKey == "acct-other:acct-phrase");
    }

    [Fact]
    public void SearchBands_MultiTokenInterpretation_RequiresAllTermsInSameBand()
    {
        SeedAccountNames(
            ("acct-sf", "SFentonX"),
            ("acct-jas", "Jasgor9"),
            ("acct-other", "Other Player"));
        SeedBandRows("song-match", "Band_Duets", "acct-jas:acct-sf", "0:1", (0, "acct-sf", 0), (1, "acct-jas", 1));
        SeedBandRows("song-sf-only", "Band_Duets", "acct-other:acct-sf", "0:1", (0, "acct-sf", 0), (1, "acct-other", 1));

        var response = _persistence.SearchBands("SFenton Jasgor9", null, pageSize: 10);

        Assert.Equal(1, response.TotalCount);
        Assert.Equal("acct-jas:acct-sf", Assert.Single(response.Results).TeamKey);
    }

    [Fact]
    public void SearchBands_ExplicitAccountIds_BypassAmbiguousText()
    {
        SeedAccountNames(
            ("acct-sf", "SFentonX"),
            ("acct-jas", "Jasgor9"),
            ("acct-phrase", "SFenton Jasgor9"),
            ("acct-other", "Other Player"));
        SeedBandRows("song-split", "Band_Duets", "acct-jas:acct-sf", "0:1", (0, "acct-sf", 0), (1, "acct-jas", 1));
        SeedBandRows("song-phrase", "Band_Duets", "acct-other:acct-phrase", "0:1", (0, "acct-phrase", 0), (1, "acct-other", 1));

        var response = _persistence.SearchBands(
            "SFenton Jasgor9",
            ["acct-sf", "acct-jas"],
            pageSize: 10);

        Assert.False(response.IsAmbiguous);
        Assert.Single(response.Interpretations);
        Assert.True(response.Interpretations[0].IsExplicit);
        Assert.Equal(2, response.Interpretations[0].Terms.Count);
        Assert.Equal("acct-jas:acct-sf", Assert.Single(response.Results).TeamKey);
    }

    [Fact]
    public void SearchBands_BandTypeAndComboFilters_RestrictResults()
    {
        SeedAccountNames(
            ("acct-sf", "SFentonX"),
            ("acct-a", "Alpha"),
            ("acct-b", "Bravo"),
            ("acct-c", "Charlie"));
        SeedBandRows("song-duo-guitar-bass", "Band_Duets", "acct-a:acct-sf", "0:1", (0, "acct-sf", 0), (1, "acct-a", 1));
        SeedBandRows("song-duo-guitar-drums", "Band_Duets", "acct-b:acct-sf", "0:3", (0, "acct-sf", 0), (1, "acct-b", 3));
        SeedBandRows("song-trio", "Band_Trios", "acct-a:acct-c:acct-sf", "0:1:3", (0, "acct-sf", 0), (1, "acct-a", 1), (2, "acct-c", 3));

        var response = _persistence.SearchBands(
            "SFentonX",
            null,
            bandTypeFilter: "Band_Duets",
            comboIdFilter: BandComboIds.FromEpicRawCombo("0:1"),
            pageSize: 10);

        var result = Assert.Single(response.Results);
        Assert.Equal("Band_Duets", result.BandType);
        Assert.Equal("acct-a:acct-sf", result.TeamKey);
    }

    [Fact]
    public void SearchAccountNames_TreatsLikeWildcardsAsLiteralCharacters()
    {
        SeedAccountNames(
            ("acct-wild", "Wild%Name"),
            ("acct-other", "WildXName"));

        var results = _fixture.Db.SearchAccountNames("Wild%", limit: 10);

        Assert.Single(results);
        Assert.Equal("acct-wild", results[0].AccountId);
    }

    private void SeedAccountNames(params (string AccountId, string DisplayName)[] accounts)
    {
        _fixture.Db.InsertAccountIds(accounts.Select(static account => account.AccountId));
        _fixture.Db.InsertAccountNames(accounts.Select(static account => (account.AccountId, (string?)account.DisplayName)).ToArray());
    }

    private void SeedBandRows(
        string songId,
        string bandType,
        string teamKey,
        string instrumentCombo,
        params (int MemberIndex, string AccountId, int InstrumentId)[] members)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        foreach (var member in members)
        {
            using var statsCmd = conn.CreateCommand();
            statsCmd.CommandText = """
                INSERT INTO band_member_stats (song_id, band_type, team_key, instrument_combo, member_index, account_id, instrument_id)
                VALUES (@songId, @bandType, @teamKey, @instrumentCombo, @memberIndex, @accountId, @instrumentId)
                ON CONFLICT DO NOTHING
                """;
            AddBandMemberParameters(statsCmd, songId, bandType, teamKey, instrumentCombo, member);
            statsCmd.ExecuteNonQuery();

            using var lookupCmd = conn.CreateCommand();
            lookupCmd.CommandText = """
                INSERT INTO band_members (account_id, song_id, band_type, team_key, instrument_combo)
                VALUES (@accountId, @songId, @bandType, @teamKey, @instrumentCombo)
                ON CONFLICT DO NOTHING
                """;
            AddBandMemberParameters(lookupCmd, songId, bandType, teamKey, instrumentCombo, member);
            lookupCmd.ExecuteNonQuery();
        }
    }

    private static void AddBandMemberParameters(
        NpgsqlCommand cmd,
        string songId,
        string bandType,
        string teamKey,
        string instrumentCombo,
        (int MemberIndex, string AccountId, int InstrumentId) member)
    {
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("instrumentCombo", instrumentCombo);
        cmd.Parameters.AddWithValue("memberIndex", member.MemberIndex);
        cmd.Parameters.AddWithValue("accountId", member.AccountId);
        cmd.Parameters.AddWithValue("instrumentId", member.InstrumentId);
    }
}
