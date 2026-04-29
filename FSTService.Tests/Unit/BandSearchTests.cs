using System.Text.Json;
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
    public void SearchBands_ProjectionAvailable_ReturnsRichResultsWithoutMembershipRebuild()
    {
        SeedAccountNames(
            ("acct-sf", "SFentonX"),
            ("acct-a", "Alpha"));
        SeedBandSearchProjection(
            "Band_Duets",
            "acct-a:acct-sf",
            appearanceCount: 7,
            new Dictionary<string, string[]>
            {
                ["acct-sf"] = ["Solo_Guitar"],
                ["acct-a"] = ["Solo_Bass"],
            },
            ("acct-sf", ["0:1"], 7),
            ("acct-a", ["0:1"], 7));
        PublishBandSearchProjectionState();

        var response = _persistence.SearchBands("SFentonX", null, pageSize: 10);

        Assert.Equal(1, response.TotalCount);
        var result = Assert.Single(response.Results);
        Assert.Equal("acct-a:acct-sf", result.TeamKey);
        Assert.Equal(7, result.AppearanceCount);
        Assert.Equal(BandIdentity.CreateBandId("Band_Duets", "acct-a:acct-sf"), result.BandId);
        Assert.Contains(result.Members, member =>
            member.AccountId == "acct-sf" &&
            member.DisplayName == "SFentonX" &&
            member.Instruments.SequenceEqual(["Solo_Guitar"]));

        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM band_team_membership_state";
        Assert.Equal(0, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact]
    public void SearchBands_ProjectionAvailable_ComboFilterUsesMemberProjectionCombos()
    {
        SeedAccountNames(
            ("acct-sf", "SFentonX"),
            ("acct-bass", "Bass"),
            ("acct-drums", "Drums"));
        SeedBandSearchProjection(
            "Band_Duets",
            "acct-bass:acct-sf",
            appearanceCount: 5,
            new Dictionary<string, string[]>
            {
                ["acct-sf"] = ["Solo_Guitar"],
                ["acct-bass"] = ["Solo_Bass"],
            },
            ("acct-sf", ["0:1"], 5),
            ("acct-bass", ["0:1"], 5));
        SeedBandSearchProjection(
            "Band_Duets",
            "acct-drums:acct-sf",
            appearanceCount: 8,
            new Dictionary<string, string[]>
            {
                ["acct-sf"] = ["Solo_Guitar"],
                ["acct-drums"] = ["Solo_Drums"],
            },
            ("acct-sf", ["0:3"], 8),
            ("acct-drums", ["0:3"], 8));
        PublishBandSearchProjectionState();

        var response = _persistence.SearchBands(
            "SFentonX",
            null,
            bandTypeFilter: "Band_Duets",
            comboIdFilter: BandComboIds.FromEpicRawCombo("0:1"),
            pageSize: 10);

        var result = Assert.Single(response.Results);
        Assert.Equal("acct-bass:acct-sf", result.TeamKey);
    }

    [Fact]
    public async Task RefreshIncremental_RefreshesChangedTeamsAndRemovesDeadProjectionRows()
    {
        SeedAccountNames(
            ("acct-sf", "SFentonX"),
            ("acct-live", "Live Bandmate"),
            ("acct-dead", "Dead Bandmate"));
        SeedBandSearchProjection(
            "Band_Duets",
            "acct-live:acct-sf",
            appearanceCount: 1,
            new Dictionary<string, string[]>
            {
                ["acct-sf"] = ["Solo_Guitar"],
                ["acct-live"] = ["Solo_Bass"],
            },
            ("acct-sf", ["0:1"], 1),
            ("acct-live", ["0:1"], 1));
        SeedBandSearchProjection(
            "Band_Duets",
            "acct-dead:acct-sf",
            appearanceCount: 9,
            new Dictionary<string, string[]>
            {
                ["acct-sf"] = ["Solo_Guitar"],
                ["acct-dead"] = ["Solo_Bass"],
            },
            ("acct-sf", ["0:1"], 9),
            ("acct-dead", ["0:1"], 9));
        var rebuiltAt = DateTime.UtcNow.AddDays(-1);
        PublishBandSearchProjectionState(rebuiltAt);

        SeedBandSourceRow("song-live-1", "Band_Duets", "acct-live:acct-sf", "0:1", 2_000, DateTime.UtcNow,
            (0, "acct-live", 1),
            (1, "acct-sf", 0));
        SeedBandSourceRow("song-live-2", "Band_Duets", "acct-live:acct-sf", "0:3", 3_000, DateTime.UtcNow,
            (0, "acct-live", 3),
            (1, "acct-sf", 0));

        var builder = new BandSearchProjectionBuilder(
            _fixture.DataSource,
            Substitute.For<ILogger<BandSearchProjectionBuilder>>());

        var result = await builder.RefreshIncrementalAsync(
            new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Band_Duets"] = ["acct-dead:acct-sf"],
            });

        Assert.True(result.ProjectionAvailable);
        Assert.Equal(2, result.ImpactedTeams);
        Assert.Equal(1, result.InsertedTeamRows);
        Assert.Equal(2, result.InsertedMemberRows);

        var liveResponse = _persistence.SearchBands("Live Bandmate", null, pageSize: 10);
        var liveResult = Assert.Single(liveResponse.Results);
        Assert.Equal("acct-live:acct-sf", liveResult.TeamKey);
        Assert.Equal(2, liveResult.AppearanceCount);
        Assert.Equal(BandIdentity.CreateBandId("Band_Duets", "acct-live:acct-sf"), liveResult.BandId);

        var deadResponse = _persistence.SearchBands("Dead Bandmate", null, pageSize: 10);
        Assert.Empty(deadResponse.Results);

        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT team_rows, member_rows FROM {BandSearchProjectionBuilder.StateTable} WHERE id = TRUE";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(2, reader.GetInt64(1));
    }

    [Fact]
    public async Task CatchUp_ReconcilesDeadStaleAndNewProjectionRows()
    {
        SeedAccountNames(
            ("acct-sf", "SFentonX"),
            ("acct-live", "Live Bandmate"),
            ("acct-dead", "Dead Bandmate"),
            ("acct-new", "New Bandmate"));
        SeedBandSearchProjection(
            "Band_Duets",
            "acct-live:acct-sf",
            appearanceCount: 1,
            new Dictionary<string, string[]>
            {
                ["acct-sf"] = ["Solo_Guitar"],
                ["acct-live"] = ["Solo_Bass"],
            },
            ("acct-sf", ["0:1"], 1),
            ("acct-live", ["0:1"], 1));
        SeedBandSearchProjection(
            "Band_Duets",
            "acct-dead:acct-sf",
            appearanceCount: 9,
            new Dictionary<string, string[]>
            {
                ["acct-sf"] = ["Solo_Guitar"],
                ["acct-dead"] = ["Solo_Bass"],
            },
            ("acct-sf", ["0:1"], 9),
            ("acct-dead", ["0:1"], 9));
        PublishBandSearchProjectionState(DateTime.UtcNow.AddDays(-1));

        SeedBandSourceRow("song-live-1", "Band_Duets", "acct-live:acct-sf", "0:1", 2_000, DateTime.UtcNow,
            (0, "acct-live", 1),
            (1, "acct-sf", 0));
        SeedBandSourceRow("song-live-2", "Band_Duets", "acct-live:acct-sf", "0:3", 3_000, DateTime.UtcNow,
            (0, "acct-live", 3),
            (1, "acct-sf", 0));
        SeedBandSourceRow("song-new", "Band_Duets", "acct-new:acct-sf", "0:1", 4_000, DateTime.UtcNow,
            (0, "acct-new", 1),
            (1, "acct-sf", 0));

        var builder = new BandSearchProjectionBuilder(
            _fixture.DataSource,
            Substitute.For<ILogger<BandSearchProjectionBuilder>>());

        var result = await builder.CatchUpAsync();

        Assert.Equal(3, result.ImpactedTeams);
        Assert.Equal(1, result.DeadProjectedTeams);
        Assert.Equal(1, result.StaleLiveProjectedTeams);
        Assert.Equal(1, result.NewSourceTeams);
        Assert.Equal(2, result.DeletedTeamRows);
        Assert.Equal(2, result.InsertedTeamRows);
        Assert.Equal(4, result.DeletedMemberRows);
        Assert.Equal(4, result.InsertedMemberRows);
        Assert.Equal(2, result.FinalTeamRows);
        Assert.Equal(4, result.FinalMemberRows);

        var liveResponse = _persistence.SearchBands("Live Bandmate", null, pageSize: 10);
        Assert.Equal(2, Assert.Single(liveResponse.Results).AppearanceCount);

        var newResponse = _persistence.SearchBands("New Bandmate", null, pageSize: 10);
        Assert.Equal("acct-new:acct-sf", Assert.Single(newResponse.Results).TeamKey);

        var deadResponse = _persistence.SearchBands("Dead Bandmate", null, pageSize: 10);
        Assert.Empty(deadResponse.Results);

        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT refreshed_at, team_rows, member_rows FROM {BandSearchProjectionBuilder.StateTable} WHERE id = TRUE";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.False(reader.IsDBNull(0));
        Assert.Equal(2, reader.GetInt64(1));
        Assert.Equal(4, reader.GetInt64(2));
    }

    [Fact]
    public void GetBandById_ResolvesFromSearchProjectionWithoutBandMemberRows()
    {
        SeedAccountNames(
            ("acct-alpha", "Alpha"),
            ("acct-beta", "Beta"));
        SeedBandSearchProjection(
            "Band_Duets",
            "acct-alpha:acct-beta",
            appearanceCount: 7,
            new Dictionary<string, string[]>
            {
                ["acct-alpha"] = ["Solo_Guitar"],
                ["acct-beta"] = ["Solo_Bass"],
            },
            ("acct-alpha", ["0:1"], 7),
            ("acct-beta", ["0:1"], 7));
        var bandId = BandIdentity.CreateBandId("Band_Duets", "acct-alpha:acct-beta");

        var result = _persistence.GetBandById(bandId);

        Assert.NotNull(result);
        Assert.Equal(bandId, result!.BandId);
        Assert.Equal("Band_Duets", result.BandType);
        Assert.Equal("acct-alpha:acct-beta", result.TeamKey);
        Assert.Equal(7, result.AppearanceCount);
        Assert.Equal(2, result.Members.Count);
        Assert.Equal("Alpha", result.Members[0].DisplayName);
        Assert.Equal(new[] { "Solo_Guitar" }, result.Members[0].Instruments);
        Assert.Equal("Beta", result.Members[1].DisplayName);
        Assert.Equal(new[] { "Solo_Bass" }, result.Members[1].Instruments);

        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM band_members";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void GetBandById_ReturnsNullForUnknownBandId()
    {
        var result = _persistence.GetBandById("missing-band-id");

        Assert.Null(result);
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

    private void SeedBandSourceRow(
        string songId,
        string bandType,
        string teamKey,
        string instrumentCombo,
        int score,
        DateTime updatedAt,
        params (int MemberIndex, string AccountId, int InstrumentId)[] members)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using (var entryCmd = conn.CreateCommand())
        {
            entryCmd.CommandText = """
                INSERT INTO band_entries (
                    song_id, band_type, team_key, instrument_combo, team_members, score,
                    accuracy, is_full_combo, stars, difficulty, season, rank, percentile,
                    source, is_over_threshold, first_seen_at, last_updated_at)
                VALUES (
                    @songId, @bandType, @teamKey, @instrumentCombo, @teamMembers, @score,
                    950000, TRUE, 5, 3, 1, 1, 0.1,
                    'test', FALSE, @updatedAt, @updatedAt)
                ON CONFLICT (song_id, band_type, team_key, instrument_combo) DO UPDATE SET
                    score = EXCLUDED.score,
                    last_updated_at = EXCLUDED.last_updated_at
                """;
            entryCmd.Parameters.AddWithValue("songId", songId);
            entryCmd.Parameters.AddWithValue("bandType", bandType);
            entryCmd.Parameters.AddWithValue("teamKey", teamKey);
            entryCmd.Parameters.AddWithValue("instrumentCombo", instrumentCombo);
            entryCmd.Parameters.AddWithValue("teamMembers", teamKey.Split(':'));
            entryCmd.Parameters.AddWithValue("score", score);
            entryCmd.Parameters.AddWithValue("updatedAt", updatedAt);
            entryCmd.ExecuteNonQuery();
        }

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

    private void SeedBandSearchProjection(
        string bandType,
        string teamKey,
        int appearanceCount,
        Dictionary<string, string[]> memberInstruments,
        params (string AccountId, string[] InstrumentCombos, int AppearanceCount)[] members)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using (var teamCmd = conn.CreateCommand())
        {
            teamCmd.CommandText = $"""
                INSERT INTO {BandSearchProjectionBuilder.TeamProjectionTable}
                    (band_type, team_key, band_id, appearance_count, member_account_ids, member_instruments_json, combo_appearances_json, updated_at)
                VALUES (@bandType, @teamKey, @bandId, @appearanceCount, @memberAccountIds, @memberInstrumentsJson::jsonb, jsonb_build_object(), @updatedAt)
                ON CONFLICT (band_type, team_key) DO UPDATE SET
                    band_id = EXCLUDED.band_id,
                    appearance_count = EXCLUDED.appearance_count,
                    member_account_ids = EXCLUDED.member_account_ids,
                    member_instruments_json = EXCLUDED.member_instruments_json,
                    combo_appearances_json = EXCLUDED.combo_appearances_json,
                    updated_at = EXCLUDED.updated_at
                """;
            teamCmd.Parameters.AddWithValue("bandType", bandType);
            teamCmd.Parameters.AddWithValue("teamKey", teamKey);
            teamCmd.Parameters.AddWithValue("bandId", BandIdentity.CreateBandId(bandType, teamKey));
            teamCmd.Parameters.AddWithValue("appearanceCount", appearanceCount);
            teamCmd.Parameters.AddWithValue("memberAccountIds", teamKey.Split(':'));
            teamCmd.Parameters.AddWithValue("memberInstrumentsJson", JsonSerializer.Serialize(memberInstruments));
            teamCmd.Parameters.AddWithValue("updatedAt", DateTime.UtcNow);
            teamCmd.ExecuteNonQuery();
        }

        foreach (var member in members)
        {
            using var memberCmd = conn.CreateCommand();
            memberCmd.CommandText = $"""
                INSERT INTO {BandSearchProjectionBuilder.MemberProjectionTable}
                    (account_id, band_type, team_key, band_id, appearance_count, team_appearance_count, instrument_combos, updated_at)
                VALUES (@accountId, @bandType, @teamKey, @bandId, @appearanceCount, @teamAppearanceCount, @instrumentCombos, @updatedAt)
                ON CONFLICT (account_id, band_type, team_key) DO UPDATE SET
                    band_id = EXCLUDED.band_id,
                    appearance_count = EXCLUDED.appearance_count,
                    team_appearance_count = EXCLUDED.team_appearance_count,
                    instrument_combos = EXCLUDED.instrument_combos,
                    updated_at = EXCLUDED.updated_at
                """;
            memberCmd.Parameters.AddWithValue("accountId", member.AccountId);
            memberCmd.Parameters.AddWithValue("bandType", bandType);
            memberCmd.Parameters.AddWithValue("teamKey", teamKey);
            memberCmd.Parameters.AddWithValue("bandId", BandIdentity.CreateBandId(bandType, teamKey));
            memberCmd.Parameters.AddWithValue("appearanceCount", member.AppearanceCount);
            memberCmd.Parameters.AddWithValue("teamAppearanceCount", appearanceCount);
            memberCmd.Parameters.AddWithValue("instrumentCombos", member.InstrumentCombos);
            memberCmd.Parameters.AddWithValue("updatedAt", DateTime.UtcNow);
            memberCmd.ExecuteNonQuery();
        }
    }

    private void PublishBandSearchProjectionState(DateTime? rebuiltAt = null)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {BandSearchProjectionBuilder.StateTable} (id, rebuilt_at, refreshed_at, team_rows, member_rows)
            VALUES (
                TRUE,
                @rebuiltAt,
                @rebuiltAt,
                (SELECT COUNT(*) FROM {BandSearchProjectionBuilder.TeamProjectionTable}),
                (SELECT COUNT(*) FROM {BandSearchProjectionBuilder.MemberProjectionTable})
            )
            ON CONFLICT (id) DO UPDATE SET
                rebuilt_at = EXCLUDED.rebuilt_at,
                refreshed_at = EXCLUDED.refreshed_at,
                team_rows = EXCLUDED.team_rows,
                member_rows = EXCLUDED.member_rows
            """;
        cmd.Parameters.AddWithValue("rebuiltAt", rebuiltAt ?? DateTime.UtcNow);
        cmd.ExecuteNonQuery();
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
