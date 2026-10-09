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

    [Fact]
    public void RebuildBandTeamMembershipForTeams_RebuildsExactBandConfigurations()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());

        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-a", "acct-b"], "0:1", 1_000, instruments: [0, 1]), rebuildTeamMembership: false);
        UpsertDirect(persistence, "song-b", MakeBandEntry(["acct-a", "acct-b"], "0:1", 1_200, instruments: [1, 0]), rebuildTeamMembership: false);

        persistence.RebuildBandTeamMembershipForTeams("Band_Duets", ["acct-a:acct-b"]);

        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT instrument_combo, assignment_key, appearance_count,
                   member_assignments_json->>'acct-a', member_assignments_json->>'acct-b'
            FROM band_team_configurations
            ORDER BY assignment_key
            """;
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("0:1", reader.GetString(0));
        Assert.Equal("acct-a=Solo_Bass|acct-b=Solo_Guitar", reader.GetString(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.Equal("Solo_Bass", reader.GetString(3));
        Assert.Equal("Solo_Guitar", reader.GetString(4));

        Assert.True(reader.Read());
        Assert.Equal("0:1", reader.GetString(0));
        Assert.Equal("acct-a=Solo_Guitar|acct-b=Solo_Bass", reader.GetString(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.Equal("Solo_Guitar", reader.GetString(3));
        Assert.Equal("Solo_Bass", reader.GetString(4));

        Assert.False(reader.Read());
    }

    [Fact]
    public void RebuildBandTeamMembershipForTeams_PreservesRepeatedBandInstruments()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());

        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-a", "acct-b"], "0:0", 1_000, instruments: [0, 0]), rebuildTeamMembership: false);

        persistence.RebuildBandTeamMembershipForTeams("Band_Duets", ["acct-a:acct-b"]);

        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT instrument_combo, assignment_key, appearance_count,
                   member_assignments_json->>'acct-a', member_assignments_json->>'acct-b'
            FROM band_team_configurations
            """;
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("0:0", reader.GetString(0));
        Assert.Equal("acct-a=Solo_Guitar|acct-b=Solo_Guitar", reader.GetString(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.Equal("Solo_Guitar", reader.GetString(3));
        Assert.Equal("Solo_Guitar", reader.GetString(4));
        Assert.False(reader.Read());
    }

    [Fact]
    public void PruneBandEntries_RemovesOnlyDeletedEntryMembersAndSummaries()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());

        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-a", "acct-b"], "0:1", 3_000), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-c", "acct-d"], "0:1", 2_000), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-e", "acct-f"], "0:1", 1_000), rebuildTeamMembership: true);

        Assert.Equal(3, CountRows("band_entries"));
        Assert.Equal(6, CountRows("band_member_stats"));
        Assert.Equal(6, CountRows("band_members"));
        Assert.Equal(6, CountRows("band_team_membership"));

        var deleted = persistence.PruneBandEntries(new HashSet<string>(), maxValidEntries: 1);

        Assert.Equal(2, deleted);
        Assert.Equal(1, CountRows("band_entries"));
        Assert.Equal(2, CountRows("band_member_stats"));
        Assert.Equal(2, CountRows("band_members"));
        Assert.Equal(2, CountRows("band_team_membership"));
        Assert.True(BandEntryExists("song-a", "acct-a:acct-b"));
        Assert.False(BandEntryExists("song-a", "acct-c:acct-d"));
        Assert.False(BandEntryExists("song-a", "acct-e:acct-f"));
    }

    [Fact]
    public void PruneBandEntriesDetailed_ReturnsAffectedTeamsForProjectionRefresh()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());

        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-a", "acct-b"], "0:1", 3_000), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-c", "acct-d"], "0:1", 2_000), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-e", "acct-f"], "0:1", 1_000), rebuildTeamMembership: true);

        var result = persistence.PruneBandEntriesDetailed(new HashSet<string>(), maxValidEntries: 1);

        Assert.Equal(2, result.DeletedEntries);
        var affectedTeams = Assert.Single(result.AffectedTeamsByBandType);
        Assert.Equal("Band_Duets", affectedTeams.Key);
        Assert.Contains("acct-c:acct-d", affectedTeams.Value);
        Assert.Contains("acct-e:acct-f", affectedTeams.Value);
    }

    [Fact]
    public void PruneBandEntries_PreservesRegisteredUserTeamsPastValidLimit()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());

        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-a", "acct-b"], "0:1", 3_000), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-c", "acct-d"], "0:1", 2_000), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-e", "acct-f"], "0:1", 1_000), rebuildTeamMembership: true);

        var deleted = persistence.PruneBandEntries(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acct-e" },
            maxValidEntries: 1);

        Assert.Equal(1, deleted);
        Assert.Equal(2, CountRows("band_entries"));
        Assert.True(BandEntryExists("song-a", "acct-a:acct-b"));
        Assert.False(BandEntryExists("song-a", "acct-c:acct-d"));
        Assert.True(BandEntryExists("song-a", "acct-e:acct-f"));
    }

    [Fact]
    public void GetSongBandLeaderboard_UsesBestTeamScoreAndPerScoreMemberInstruments()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());

        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-a", "acct-b"], "0:1", 1_000, instruments: [0, 1]), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-b", "acct-a"], "2:3", 1_200, instruments: [2, 3], memberScores: [700, 500], memberDifficulties: [2, 3]), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-c", "acct-d"], "0:3", 1_100, instruments: [0, 3]), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-e", "acct-f"], "0:1", 1_300, instruments: [0, 1], isOverThreshold: true), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-b", MakeBandEntry(["acct-g", "acct-h"], "0:1", 1_400, instruments: [0, 1]), rebuildTeamMembership: true);

        var (firstPage, totalEntries) = _fixture.Db.GetSongBandLeaderboard("song-a", "Band_Duets", limit: 1, offset: 0);

        Assert.Equal(2, totalEntries);
        var top = Assert.Single(firstPage);
        Assert.Equal("acct-a:acct-b", top.TeamKey);
        Assert.Equal(1_200, top.Score);
        Assert.Equal(1, top.Rank);
        Assert.Equal("Solo_Drums+Solo_Vocals", top.ComboId);
        Assert.Equal(new[] { "acct-a", "acct-b" }, top.Members.Select(member => member.AccountId));
        Assert.Equal(new[] { "Solo_Vocals" }, top.Members[0].Instruments);
        Assert.Equal(new[] { "Solo_Drums" }, top.Members[1].Instruments);
        Assert.Equal(700, top.Members[0].Score);
        Assert.Equal(500, top.Members[1].Score);
        Assert.Equal(950_000, top.Members[0].Accuracy);
        Assert.True(top.Members[0].IsFullCombo);
        Assert.Equal(5, top.Members[0].Stars);
        Assert.Equal(2, top.Members[0].Difficulty);
        Assert.Equal(1, top.Members[0].Season);

        var (secondPage, secondTotal) = _fixture.Db.GetSongBandLeaderboard("song-a", "Band_Duets", limit: 1, offset: 1);

        Assert.Equal(2, secondTotal);
        var second = Assert.Single(secondPage);
        Assert.Equal("acct-c:acct-d", second.TeamKey);
        Assert.Equal(2, second.Rank);
    }

    [Fact]
    public void GetSongBandLeaderboard_WithCombo_FiltersBeforeChoosingBestTeamScore()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());

        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-a", "acct-b"], "0:1", 1_000, instruments: [0, 1]), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-b", "acct-a"], "2:3", 1_200, instruments: [2, 3]), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-c", "acct-d"], "0:1", 1_100, instruments: [0, 1]), rebuildTeamMembership: true);

        var (entries, totalEntries) = _fixture.Db.GetSongBandLeaderboard("song-a", "Band_Duets", limit: 25, offset: 0, comboId: "Solo_Guitar+Solo_Bass");

        Assert.Equal(2, totalEntries);
        Assert.Equal(["acct-c:acct-d", "acct-a:acct-b"], entries.Select(entry => entry.TeamKey));
        Assert.All(entries, entry => Assert.Equal("Solo_Guitar+Solo_Bass", entry.ComboId));
        Assert.Equal([1, 2], entries.Select(entry => entry.Rank));
        Assert.Equal([1_100, 1_000], entries.Select(entry => entry.Score));
    }

    [Fact]
    public void GetSongBandLeaderboard_WithCombo_PreservesRepeatedBandInstruments()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());

        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-a", "acct-b"], "0:0", 1_000, instruments: [0, 0]), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-c", "acct-d"], "0:1", 1_100, instruments: [0, 1]), rebuildTeamMembership: true);

        var (entries, totalEntries) = _fixture.Db.GetSongBandLeaderboard("song-a", "Band_Duets", limit: 25, offset: 0, comboId: "Solo_Guitar+Solo_Guitar");

        Assert.Equal(1, totalEntries);
        var entry = Assert.Single(entries);
        Assert.Equal("acct-a:acct-b", entry.TeamKey);
        Assert.Equal("Solo_Guitar+Solo_Guitar", entry.ComboId);
        Assert.Equal(["Solo_Guitar"], entry.Members[0].Instruments);
        Assert.Equal(["Solo_Guitar"], entry.Members[1].Instruments);
    }

    [Fact]
    public void GetSongBandLeaderboardEntryForAccount_ReturnsBestTeamContainingAccount()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());

        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-a", "acct-b"], "0:1", 1_000, instruments: [0, 1]), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-b", "acct-a"], "2:3", 1_200, instruments: [2, 3]), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-c", "acct-d"], "0:3", 1_100, instruments: [0, 3]), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-e", "acct-f"], "0:1", 1_300, instruments: [0, 1], isOverThreshold: true), rebuildTeamMembership: true);

        var selectedTop = _fixture.Db.GetSongBandLeaderboardEntryForAccount("song-a", "Band_Duets", "acct-b");
        var selectedSecond = _fixture.Db.GetSongBandLeaderboardEntryForAccount("song-a", "Band_Duets", "acct-d");
        var missing = _fixture.Db.GetSongBandLeaderboardEntryForAccount("song-a", "Band_Duets", "acct-missing");

        Assert.NotNull(selectedTop);
        Assert.Equal("acct-a:acct-b", selectedTop.TeamKey);
        Assert.Equal(1, selectedTop.Rank);
        Assert.Equal(1_200, selectedTop.Score);
        Assert.NotNull(selectedSecond);
        Assert.Equal("acct-c:acct-d", selectedSecond.TeamKey);
        Assert.Equal(2, selectedSecond.Rank);
        Assert.Null(missing);
    }

    [Fact]
    public void GetSongBandLeaderboardEntryForAccount_WithCombo_RanksWithinComboScope()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());

        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-a", "acct-b"], "0:1", 1_000, instruments: [0, 1]), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-b", "acct-a"], "2:3", 1_200, instruments: [2, 3]), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-c", "acct-d"], "0:1", 1_100, instruments: [0, 1]), rebuildTeamMembership: true);

        var leadBass = _fixture.Db.GetSongBandLeaderboardEntryForAccount("song-a", "Band_Duets", "acct-b", comboId: "Solo_Guitar+Solo_Bass");
        var drumsVocals = _fixture.Db.GetSongBandLeaderboardEntryForAccount("song-a", "Band_Duets", "acct-b", comboId: "Solo_Drums+Solo_Vocals");
        var missing = _fixture.Db.GetSongBandLeaderboardEntryForTeam("song-a", "Band_Duets", "acct-a:acct-b", comboId: "Solo_Guitar+Solo_Guitar");

        Assert.NotNull(leadBass);
        Assert.Equal("Solo_Guitar+Solo_Bass", leadBass.ComboId);
        Assert.Equal(2, leadBass.Rank);
        Assert.Equal(1_000, leadBass.Score);
        Assert.NotNull(drumsVocals);
        Assert.Equal("Solo_Drums+Solo_Vocals", drumsVocals.ComboId);
        Assert.Equal(1, drumsVocals.Rank);
        Assert.Equal(1_200, drumsVocals.Score);
        Assert.Null(missing);
    }

    [Fact]
    public void GetSongBandLeaderboardEntryForTeam_ReturnsExactTeamByRankedEntry()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());

        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-a", "acct-b"], "0:1", 1_000, instruments: [0, 1]), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-c", "acct-d"], "0:3", 1_100, instruments: [0, 3]), rebuildTeamMembership: true);
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-e", "acct-f"], "0:1", 1_300, instruments: [0, 1], isOverThreshold: true), rebuildTeamMembership: true);

        var selectedTeam = _fixture.Db.GetSongBandLeaderboardEntryForTeam("song-a", "Band_Duets", "acct-c:acct-d");
        var missing = _fixture.Db.GetSongBandLeaderboardEntryForTeam("song-a", "Band_Duets", "acct-missing:acct-other");

        Assert.NotNull(selectedTeam);
        Assert.Equal("acct-c:acct-d", selectedTeam.TeamKey);
        Assert.Equal(1, selectedTeam.Rank);
        Assert.Equal(1_100, selectedTeam.Score);
        Assert.Null(missing);
    }

    [Fact]
    public async Task BandSpoolWriterFactory_FlushAll_UpdatesOverThresholdFlagWhenScoreIsUnchanged()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());
        var logger = Substitute.For<ILogger<BandLeaderboardPersistence>>();

        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-a", "acct-b"], "0:1", 1_000, isOverThreshold: true), rebuildTeamMembership: true);

        await using var spool = BandSpoolWriterFactory.Create(logger, persistence);
        spool.Enqueue("song-a", "Band_Duets", [MakeBandEntry(["acct-a", "acct-b"], "0:1", 1_000, isOverThreshold: false)]);

        spool.Complete();
        spool.FlushAll();

        Assert.False(GetBandEntryIsOverThreshold("song-a", "acct-a:acct-b"));
    }

    [Fact]
    public async Task BandSpoolWriterFactory_FlushAll_PrefilterSkipsUnchangedRowsAndAppliesChanges()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());
        var logger = Substitute.For<ILogger<BandLeaderboardPersistence>>();

        var unchanged = MakeBandEntry(["acct-a", "acct-b"], "0:1", 1_000);
        var lower = MakeBandEntry(["acct-c", "acct-d"], "0:1", 2_000);
        var improved = MakeBandEntry(["acct-e", "acct-f"], "0:1", 3_000);
        var bonusChanged = MakeBandEntry(["acct-g", "acct-h"], "0:1", 4_000);
        foreach (var entry in new[] { unchanged, lower, improved, bonusChanged })
            UpsertDirect(persistence, "song-a", entry, rebuildTeamMembership: true);
        SetAllBandEntriesLastUpdated(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var stagedLower = MakeBandEntry(["acct-c", "acct-d"], "0:1", 1_500);
        var stagedImproved = MakeBandEntry(["acct-e", "acct-f"], "0:1", 3_500);
        var stagedBonus = MakeBandEntry(["acct-g", "acct-h"], "0:1", 4_000);
        stagedBonus.BaseScore = (bonusChanged.BaseScore ?? 0) + 7;
        var stagedNew = MakeBandEntry(["acct-i", "acct-j"], "0:1", 500);

        await using var spool = BandSpoolWriterFactory.Create(logger, persistence);
        spool.Enqueue(
            "song-a",
            "Band_Duets",
            [MakeBandEntry(["acct-a", "acct-b"], "0:1", 1_000), stagedLower, stagedImproved, stagedBonus, stagedNew]);
        spool.Complete();
        spool.FlushAll();

        var rows = ReadBandEntries("song-a");
        Assert.Equal(5, rows.Count);
        Assert.Equal((1_000, false), (rows["acct-a:acct-b"].Score, rows["acct-a:acct-b"].Touched));
        Assert.Equal((2_000, false), (rows["acct-c:acct-d"].Score, rows["acct-c:acct-d"].Touched));
        Assert.Equal((3_500, true), (rows["acct-e:acct-f"].Score, rows["acct-e:acct-f"].Touched));
        Assert.Equal(4_000, rows["acct-g:acct-h"].Score);
        Assert.Equal(stagedBonus.BaseScore, rows["acct-g:acct-h"].BaseScore);
        Assert.Equal((500, true), (rows["acct-i:acct-j"].Score, rows["acct-i:acct-j"].Touched));
    }

    [Fact]
    public void BandSpoolPrefilter_ComparesWithCoveringIndexOnlyScan()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());
        for (var i = 0; i < 50; i++)
            UpsertDirect(persistence, "song-a", MakeBandEntry([$"acct-{i:D2}-a", $"acct-{i:D2}-b"], "0:1", 1_000 + i), rebuildTeamMembership: false);

        using var conn = _fixture.DataSource.OpenConnection();
        using (var vacuum = conn.CreateCommand())
        {
            vacuum.CommandText = "VACUUM ANALYZE band_entries_duets";
            vacuum.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        using (var setup = conn.CreateCommand())
        {
            setup.Transaction = tx;
            setup.CommandText = """
                SET LOCAL enable_seqscan = off;
                SET LOCAL enable_bitmapscan = off;
                CREATE TEMP TABLE _be_staging ON COMMIT DROP AS
                SELECT song_id, band_type, team_key, instrument_combo, team_members, score, base_score,
                       instrument_bonus, overdrive_bonus, accuracy, is_full_combo, stars, difficulty,
                       season, rank, percentile, end_time, source, is_over_threshold, last_updated_at AS ts
                FROM band_entries;
                """;
            setup.ExecuteNonQuery();
        }

        using var explain = conn.CreateCommand();
        explain.Transaction = tx;
        explain.CommandText = "EXPLAIN " + BandSpoolWriterFactory.PrefilterUnchangedSql;
        var plan = new List<string>();
        using (var reader = explain.ExecuteReader())
        {
            while (reader.Read())
                plan.Add(reader.GetString(0));
        }

        Assert.Contains(plan, line => line.Contains("Index Only Scan using ix_be_duets_prefilter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Memberless_band_write_after_shared_backend_loss_is_fenced_by_exclusive_maintenance()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());
        var scrapeId = _fixture.Db.StartScrapeRun();
        _fixture.Db.CompleteScrapeRun(
            scrapeId,
            1,
            1,
            1,
            1);
        _fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false);
        var publicationId = _fixture.Db
            .GetPublicationPointerState()
            .CurrentPublicationId!.Value;

        var staleSharedLease =
            await _fixture.Db
                .AcquireRegistrationMutationLeaseAsync();
        await staleSharedLease.VerifyHeldAsync();
        using (var terminator =
               _fixture.DataSource.OpenConnection())
        using (var terminate = terminator.CreateCommand())
        {
            terminate.CommandText =
                "SELECT pg_terminate_backend(@backendProcessId)";
            terminate.Parameters.AddWithValue(
                "backendProcessId",
                staleSharedLease.BackendProcessId);
            Assert.True(terminate.ExecuteScalar() is true);
        }
        await staleSharedLease.DisposeAsync();

        await using var maintenanceLease =
            await _fixture.Db
                .AcquireMaxScoreMaintenanceLeaseAsync(
                    publicationId);
        var memberless = MakeBandEntry(
            ["acct-a", "acct-b"],
            "0:1",
            1_000);
        memberless.MemberStats = [];

        Assert.Throws<RegistrationMutationBlockedException>(
            () => persistence.UpsertBandEntries(
                "song-memberless",
                "Band_Duets",
                [memberless]));

        Assert.Equal(0, CountRows("band_entries"));
        Assert.Equal(0, CountRows("band_member_stats"));
        Assert.Equal(0, CountRows("band_members"));
        Assert.Equal(0, CountRows("band_team_membership"));
        Assert.Equal(0, CountRows("band_team_membership_state"));
        Assert.Equal(0, CountRows("band_team_configurations"));
    }

    [Fact]
    public async Task TeamMembershipRebuildsSerializeWithinBandTypeOnly()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<ILogger<BandLeaderboardPersistence>>());
        UpsertDirect(persistence, "song-a", MakeBandEntry(["acct-a", "acct-b"], "0:1", 1_000), rebuildTeamMembership: false);

        using var holder = _fixture.DataSource.OpenConnection();
        using var holderTx = holder.BeginTransaction();
        BandLeaderboardPersistence.RebuildBandTeamMembershipForTeams(holder, holderTx, "Band_Duets", ["acct-a:acct-b"]);

        var otherBandType = Task.Run(() => persistence.RebuildBandTeamMembershipForTeams("Band_Trios", ["acct-a:acct-b:acct-c"]));
        Assert.Same(otherBandType, await Task.WhenAny(otherBandType, Task.Delay(TimeSpan.FromSeconds(30))));
        await otherBandType;

        var sameBandType = Task.Run(() => persistence.RebuildBandTeamMembershipForTeams("Band_Duets", ["acct-a:acct-b"]));
        var account = Task.Run(() =>
        {
            using var conn = _fixture.DataSource.OpenConnection();
            using var tx = conn.BeginTransaction();
            BandLeaderboardPersistence.RebuildBandTeamMembershipForAccount(conn, tx, "acct-a");
            tx.Commit();
        });
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(sameBandType.IsCompleted);
        Assert.False(account.IsCompleted);

        holderTx.Commit();
        await Task.WhenAll(sameBandType, account).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, CountMembershipRows());
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

    private long CountRows(string tableName)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        return (long)cmd.ExecuteScalar()!;
    }

    private bool BandEntryExists(string songId, string teamKey)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM band_entries
                WHERE song_id = @songId
                  AND band_type = 'Band_Duets'
                  AND team_key = @teamKey
            )
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        return Convert.ToBoolean(cmd.ExecuteScalar());
    }

    private void SetAllBandEntriesLastUpdated(DateTime timestampUtc)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE band_entries SET last_updated_at = @ts";
        cmd.Parameters.AddWithValue("ts", timestampUtc);
        cmd.ExecuteNonQuery();
    }

    private Dictionary<string, (int Score, int? BaseScore, bool Touched)> ReadBandEntries(string songId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT team_key, score, base_score, last_updated_at > TIMESTAMPTZ '2020-01-02'
            FROM band_entries
            WHERE song_id = @songId
              AND band_type = 'Band_Duets'
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        var rows = new Dictionary<string, (int, int?, bool)>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows[reader.GetString(0)] = (
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.GetBoolean(3));
        }

        return rows;
    }

    private bool GetBandEntryIsOverThreshold(string songId, string teamKey)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT is_over_threshold
            FROM band_entries
            WHERE song_id = @songId
              AND band_type = 'Band_Duets'
              AND team_key = @teamKey
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        return Convert.ToBoolean(cmd.ExecuteScalar());
    }

    private static BandLeaderboardEntry MakeBandEntry(
        string[] teamMembers,
        string instrumentCombo,
        int score,
        int[]? instruments = null,
        int[]? memberScores = null,
        int[]? memberDifficulties = null,
        bool isOverThreshold = false)
    {
        var sortedMembers = teamMembers.OrderBy(static member => member, StringComparer.OrdinalIgnoreCase).ToArray();
        var memberInstruments = instruments ?? [0, 1];
        var perMemberScores = memberScores ?? [score / 2, score / 2];
        var perMemberDifficulties = memberDifficulties ?? [3, 3];
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
            IsOverThreshold = isOverThreshold,
            MemberStats =
            [
                new BandMemberStats
                {
                    MemberIndex = 0,
                    AccountId = sortedMembers[0],
                    InstrumentId = memberInstruments[0],
                    Score = perMemberScores[0],
                    Accuracy = 950_000,
                    IsFullCombo = true,
                    Stars = 5,
                    Difficulty = perMemberDifficulties[0],
                },
                new BandMemberStats
                {
                    MemberIndex = 1,
                    AccountId = sortedMembers[1],
                    InstrumentId = memberInstruments[1],
                    Score = perMemberScores[1],
                    Accuracy = 950_000,
                    IsFullCombo = true,
                    Stars = 5,
                    Difficulty = perMemberDifficulties[1],
                },
            ],
        };
    }
}
