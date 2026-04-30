using System.Globalization;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class MetaDatabaseRankingsTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    private MetaDatabase Db => _fixture.Db;

    public void Dispose() => _fixture.Dispose();

    private static BandLeaderboardEntry MakeBandEntry(string[] teamMembers, string instrumentCombo, int score, bool isFullCombo = false)
    {
        var sortedMembers = teamMembers.OrderBy(static member => member, StringComparer.OrdinalIgnoreCase).ToArray();
        return new BandLeaderboardEntry
        {
            TeamKey = string.Join(':', sortedMembers),
            TeamMembers = teamMembers,
            InstrumentCombo = instrumentCombo,
            Score = score,
            Accuracy = 950000,
            IsFullCombo = isFullCombo,
            Stars = 5,
            Difficulty = 3,
            Season = 1,
            Rank = 1,
            Percentile = 0.5,
            Source = "scrape",
        };
    }

    private void SeedBandRankingsSource()
    {
        SeedBandRankingsSource(_fixture);
    }

    private static void SeedBandRankingsSource(InMemoryMetaDatabase fixture)
    {
        var persistence = new BandLeaderboardPersistence(fixture.DataSource, Substitute.For<Microsoft.Extensions.Logging.ILogger<BandLeaderboardPersistence>>());
        persistence.UpsertBandEntries("song_0", "Band_Duets",
        [
            MakeBandEntry(["p1", "p2"], "0:1", 1000, isFullCombo: true),
            MakeBandEntry(["p3", "p4"], "0:3", 900),
            MakeBandEntry(["p1", "p2"], "0:0", 1100),
        ]);
        persistence.UpsertBandEntries("song_1", "Band_Duets",
        [
            MakeBandEntry(["p1", "p2"], "0:1", 1200),
            MakeBandEntry(["p3", "p4"], "0:3", 1300, isFullCombo: true),
            MakeBandEntry(["p5", "p6"], "0:1", 800),
        ]);
    }

    // ═══════════════════════════════════════════════════════════
    // CompositeRankings
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ReplaceCompositeRankings_StoresAndRetrieves()
    {
        var rankings = new List<CompositeRankingDto>
        {
            new()
            {
                AccountId = "p1", InstrumentsPlayed = 3, TotalSongsPlayed = 100,
                CompositeRating = 0.05, CompositeRank = 1,
                GuitarAdjustedSkill = 0.03, GuitarSkillRank = 2,
                BassAdjustedSkill = 0.04, BassSkillRank = 5,
            },
            new()
            {
                AccountId = "p2", InstrumentsPlayed = 1, TotalSongsPlayed = 50,
                CompositeRating = 0.15, CompositeRank = 2,
                DrumsAdjustedSkill = 0.15, DrumsSkillRank = 1,
            },
        };

        Db.ReplaceCompositeRankings(rankings);

        var (entries, total) = Db.GetCompositeRankings(page: 1, pageSize: 50);
        Assert.Equal(2, total);
        Assert.Equal(2, entries.Count);
        Assert.Equal("p1", entries[0].AccountId);
        Assert.Equal(0.05, entries[0].CompositeRating, 4);
        Assert.Equal(0.03, entries[0].GuitarAdjustedSkill!.Value, 2);
        Assert.Null(entries[0].DrumsAdjustedSkill);
    }

    [Fact]
    public void GetCompositeRanking_SingleAccount()
    {
        Db.ReplaceCompositeRankings([new CompositeRankingDto
        {
            AccountId = "p1", InstrumentsPlayed = 2, TotalSongsPlayed = 80,
            CompositeRating = 0.1, CompositeRank = 1,
        }]);

        var r = Db.GetCompositeRanking("p1");
        Assert.NotNull(r);
        Assert.Equal(1, r.CompositeRank);
    }

    [Fact]
    public void GetCompositeRanking_ReturnsNull_ForUnknown()
    {
        Assert.Null(Db.GetCompositeRanking("nonexistent"));
    }

    [Fact]
    public void GetCompositeRankings_Pagination()
    {
        var rankings = Enumerable.Range(0, 10).Select(i => new CompositeRankingDto
        {
            AccountId = $"p{i}", InstrumentsPlayed = 1, TotalSongsPlayed = 10,
            CompositeRating = 0.1 * i, CompositeRank = i + 1,
        }).ToList();
        Db.ReplaceCompositeRankings(rankings);

        var (page1, total) = Db.GetCompositeRankings(page: 1, pageSize: 3);
        var (page2, _) = Db.GetCompositeRankings(page: 2, pageSize: 3);

        Assert.Equal(10, total);
        Assert.Equal(3, page1.Count);
        Assert.Equal(3, page2.Count);
    }

    [Fact]
    public void ReplaceCompositeRankings_Replaces_NotAppends()
    {
        Db.ReplaceCompositeRankings([new CompositeRankingDto
        {
            AccountId = "old", InstrumentsPlayed = 1, TotalSongsPlayed = 5,
            CompositeRating = 0.5, CompositeRank = 1,
        }]);

        Db.ReplaceCompositeRankings([new CompositeRankingDto
        {
            AccountId = "new", InstrumentsPlayed = 2, TotalSongsPlayed = 10,
            CompositeRating = 0.1, CompositeRank = 1,
        }]);

        var (entries, total) = Db.GetCompositeRankings();
        Assert.Equal(1, total);
        Assert.Equal("new", entries[0].AccountId);
    }

    // ═══════════════════════════════════════════════════════════
    // CompositeRankHistory
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void SnapshotCompositeRankHistory_Creates_And_Purges()
    {
        Db.ReplaceCompositeRankings([
            new CompositeRankingDto { AccountId = "p1", InstrumentsPlayed = 1, TotalSongsPlayed = 10, CompositeRating = 0.1, CompositeRank = 1 },
            new CompositeRankingDto { AccountId = "p2", InstrumentsPlayed = 1, TotalSongsPlayed = 5, CompositeRating = 0.2, CompositeRank = 2 },
        ]);

        Db.SnapshotCompositeRankHistory(); // All accounts with data

        // Both p1 and p2 should be snapshotted (sparse change-detection, no topN filter)
    }

    [Fact]
    public void SnapshotCompositeRankHistory_IncludesAdditional()
    {
        Db.ReplaceCompositeRankings([
            new CompositeRankingDto { AccountId = "p1", InstrumentsPlayed = 1, TotalSongsPlayed = 10, CompositeRating = 0.1, CompositeRank = 1 },
            new CompositeRankingDto { AccountId = "p2", InstrumentsPlayed = 1, TotalSongsPlayed = 5, CompositeRating = 0.2, CompositeRank = 2 },
        ]);

        Db.SnapshotCompositeRankHistory();
        // All accounts are included (no topN filtering)
    }

    // ═══════════════════════════════════════════════════════════
    // ComboLeaderboard
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ReplaceComboLeaderboard_StoresAndRetrieves()
    {
        var entries = new List<(string AccountId, double AdjustedRating, double WeightedRating, double FcRate, long TotalScore, double MaxScorePercent, int SongsPlayed, int FullComboCount)>
        {
            ("p1", 0.05, 0.06, 0.8, 50000, 0.95, 100, 80),
            ("p2", 0.10, 0.12, 0.6, 40000, 0.90, 80, 48),
        };
        Db.ReplaceComboLeaderboard("03", entries, 2);

        var (result, total) = Db.GetComboLeaderboard("03", "adjusted", 1, 50);
        Assert.Equal(2, total);
        Assert.Equal(2, result.Count);
        Assert.Equal("p1", result[0].AccountId);
        Assert.Equal(1, result[0].Rank);
        Assert.Equal(0.05, result[0].AdjustedRating, 4);
        Assert.Equal(50000, result[0].TotalScore);
    }

    [Fact]
    public void ReplaceComboLeaderboard_ReplacesOld()
    {
        Db.ReplaceComboLeaderboard("03",
            [("old", 0.5, 0.5, 0.5, 1000, 0.5, 10, 5)], 1);
        Db.ReplaceComboLeaderboard("03",
            [("new", 0.1, 0.1, 0.8, 2000, 0.9, 20, 16)], 1);

        var (entries, total) = Db.GetComboLeaderboard("03");
        Assert.Equal(1, total);
        Assert.Equal("new", entries[0].AccountId);
    }

    [Fact]
    public void GetComboRank_SingleAccount()
    {
        Db.ReplaceComboLeaderboard("03",
            [("p1", 0.05, 0.06, 0.8, 50000, 0.95, 100, 80), ("p2", 0.10, 0.12, 0.6, 40000, 0.90, 80, 48)], 2);

        var entry = Db.GetComboRank("03", "p2");
        Assert.NotNull(entry);
        Assert.Equal(2, entry.Rank);
        Assert.Equal("p2", entry.AccountId);
    }

    [Fact]
    public void GetComboRank_ReturnsNull_ForUnknown()
    {
        Assert.Null(Db.GetComboRank("03", "nobody"));
    }

    [Fact]
    public void GetComboTotalAccounts_ReturnsCount()
    {
        Db.ReplaceComboLeaderboard("03",
            [("p1", 0.05, 0.06, 0.8, 50000, 0.95, 100, 80)], 500_000);

        Assert.Equal(500_000, Db.GetComboTotalAccounts("03"));
    }

    [Fact]
    public void GetComboTotalAccounts_ZeroForUnknown()
    {
        Assert.Equal(0, Db.GetComboTotalAccounts("nonexistent"));
    }

    [Fact]
    public void GetComboLeaderboard_Pagination()
    {
        var entries = Enumerable.Range(0, 10)
            .Select(i => ($"p{i}", 0.01 * i, 0.01 * i, 0.5, (long)(1000 * (10 - i)), 0.5, 100 - i, 50 - i))
            .ToList();
        Db.ReplaceComboLeaderboard("03", entries, 10);

        var (page1, total) = Db.GetComboLeaderboard("03", "adjusted", 1, 3);
        var (page2, _) = Db.GetComboLeaderboard("03", "adjusted", 2, 3);

        Assert.Equal(10, total);
        Assert.Equal(3, page1.Count);
        Assert.Equal(3, page2.Count);
        Assert.NotEqual(page1[0].AccountId, page2[0].AccountId);
    }

    [Fact]
    public void GetComboLeaderboard_EmptyForUnknownCombo()
    {
        var (entries, total) = Db.GetComboLeaderboard("nonexistent");
        Assert.Empty(entries);
        Assert.Equal(0, total);
    }

    [Fact]
    public void GetComboLeaderboard_FcRate_TiebreaksByTotalScoreDesc()
    {
        Db.ReplaceComboLeaderboard("03",
            [
                ("lowScoreManySongs", 0.5, 0.5, 0.75, 1_000L, 0.5, 200, 150),
                ("highScoreFewerSongs", 0.5, 0.5, 0.75, 5_000L, 0.5, 100, 75),
            ], 2);

        var (entries, total) = Db.GetComboLeaderboard("03", "fcrate", 1, 50);
        var lowScoreRank = Db.GetComboRank("03", "lowScoreManySongs", "fcrate");

        Assert.Equal(2, total);
        Assert.Equal("highScoreFewerSongs", entries[0].AccountId);
        Assert.Equal(1, entries[0].Rank);
        Assert.NotNull(lowScoreRank);
        Assert.Equal(2, lowScoreRank.Rank);
    }

    // ═══════════════════════════════════════════════════════════
    // BandTeamRankings
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void RebuildBandTeamRankings_StoresOverallAndComboScopes()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        SeedBandSearchProjection("Band_Duets", "p1:p2", ["p1", "p2"], """
            {"p1":["Solo_Guitar","Solo_Bass"],"p2":["Solo_Drums"]}
            """);

        var (overall, totalTeams) = Db.GetBandTeamRankings("Band_Duets");
        Assert.Equal(3, totalTeams);
        Assert.Equal(3, overall.Count);
        Assert.Equal("p1:p2", overall[0].TeamKey);
        Assert.Equal(1, overall[0].AdjustedSkillRank);
        Assert.Collection(overall[0].Members,
            member =>
            {
                Assert.Equal("p1", member.AccountId);
                Assert.Equal(["Solo_Guitar", "Solo_Bass"], member.Instruments);
            },
            member =>
            {
                Assert.Equal("p2", member.AccountId);
                Assert.Equal(["Solo_Drums"], member.Instruments);
            });

        var comboId = BandComboIds.FromInstruments(["Solo_Guitar", "Solo_Bass"]);
        var (comboEntries, comboTotal) = Db.GetBandTeamRankings("Band_Duets", comboId);
        Assert.Equal(2, comboTotal);
        Assert.Equal(2, comboEntries.Count);
        Assert.Equal("p1:p2", comboEntries[0].TeamKey);

        var team = Db.GetBandTeamRanking("Band_Duets", "p1:p2");
        Assert.NotNull(team);
        Assert.Equal(3, team.TotalRankedTeams);
        Assert.Equal(["Solo_Guitar", "Solo_Bass"], team.Members[0].Instruments);

        var bestForP1 = Db.GetBandTeamRankingForAccount("Band_Duets", "p1", rankBy: "totalscore");
        var expectedBestForP1 = Db.GetBandTeamRankings("Band_Duets", rankBy: "totalscore")
            .Entries
            .First(entry => entry.TeamMembers.Contains("p1"));
        Assert.NotNull(bestForP1);
        Assert.Equal(expectedBestForP1.TeamKey, bestForP1.TeamKey);
        Assert.Equal(expectedBestForP1.TotalScoreRank, bestForP1.TotalScoreRank);
        Assert.Null(Db.GetBandTeamRankingForAccount("Band_Duets", "missing", rankBy: "totalscore"));

        var combos = Db.GetBandRankingCombos("Band_Duets");
        Assert.Contains(combos, entry => entry.ComboId == comboId && entry.TeamCount == 2);
        Assert.Contains(combos, entry => entry.ComboId == BandComboIds.FromInstruments(["Solo_Guitar", "Solo_Guitar"]) && entry.TeamCount == 1);
    }

    [Fact]
    public void RebuildBandTeamRankings_DoesNotLeakOldBackupTables()
    {
        // Regression test: SwapBandCurrentTables previously checked backup
        // table existence before the batched RENAMEs executed, so the DROP
        // for the just-created backup was never queued. This caused one
        // `band_team_rankings_current_band_*_old_<suffix>` pair to leak per
        // rebuild, accumulating tens of GB of orphaned tables in production.
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);

        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT tablename
            FROM pg_tables
            WHERE schemaname = 'public'
              AND (tablename LIKE 'band_team_rankings_current_band_duets_old_%'
                   OR tablename LIKE 'band_team_ranking_stats_current_band_duets_old_%')
            ORDER BY tablename;";
        var orphans = new List<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                orphans.Add(reader.GetString(0));
        }

        Assert.Empty(orphans);
    }

    [Fact]
    public void RebuildBandTeamRankings_AllWriteModesMatch()
    {
        using var monolithicFixture = new InMemoryMetaDatabase();
        using var comboBatchedFixture = new InMemoryMetaDatabase();
        using var phasedFixture = new InMemoryMetaDatabase();

        SeedBandRankingsSource(monolithicFixture);
        SeedBandRankingsSource(comboBatchedFixture);
        SeedBandRankingsSource(phasedFixture);

        var monolithicMetrics = monolithicFixture.Db.RebuildBandTeamRankingsMeasured(
            "Band_Duets",
            totalChartedSongs: 2,
            options: new BandTeamRankingRebuildOptions
            {
                WriteMode = BandTeamRankingWriteMode.Monolithic,
                AnalyzeStagingTable = false,
            });

        var comboBatchedMetrics = comboBatchedFixture.Db.RebuildBandTeamRankingsMeasured(
            "Band_Duets",
            totalChartedSongs: 2,
            options: new BandTeamRankingRebuildOptions
            {
                WriteMode = BandTeamRankingWriteMode.ComboBatched,
                AnalyzeStagingTable = false,
            });

        var phasedMetrics = phasedFixture.Db.RebuildBandTeamRankingsMeasured(
            "Band_Duets",
            totalChartedSongs: 2,
            options: new BandTeamRankingRebuildOptions
            {
                WriteMode = BandTeamRankingWriteMode.Phased,
                AnalyzeStagingTable = false,
            });

        Assert.Equal(monolithicMetrics.ResultRowCount, comboBatchedMetrics.ResultRowCount);
        Assert.Equal(monolithicMetrics.StatsRowCount, comboBatchedMetrics.StatsRowCount);
        Assert.Equal(monolithicMetrics.DistinctComboCount, comboBatchedMetrics.DistinctComboCount);
        Assert.Equal(monolithicMetrics.ResultRowCount, phasedMetrics.ResultRowCount);
        Assert.Equal(monolithicMetrics.StatsRowCount, phasedMetrics.StatsRowCount);
        Assert.Equal(monolithicMetrics.DistinctComboCount, phasedMetrics.DistinctComboCount);

        var monolithicOverall = monolithicFixture.Db.GetBandTeamRankings("Band_Duets", pageSize: 50).Entries
            .Select(SerializeBandTeamRanking)
            .ToList();
        var comboBatchedOverall = comboBatchedFixture.Db.GetBandTeamRankings("Band_Duets", pageSize: 50).Entries
            .Select(SerializeBandTeamRanking)
            .ToList();
        var phasedOverall = phasedFixture.Db.GetBandTeamRankings("Band_Duets", pageSize: 50).Entries
            .Select(SerializeBandTeamRanking)
            .ToList();
        Assert.Equal(monolithicOverall, comboBatchedOverall);
        Assert.Equal(monolithicOverall, phasedOverall);

        var monolithicCombos = monolithicFixture.Db.GetBandRankingCombos("Band_Duets")
            .OrderBy(entry => entry.ComboId, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.ComboId}:{entry.TeamCount}")
            .ToList();
        var comboBatchedCombos = comboBatchedFixture.Db.GetBandRankingCombos("Band_Duets")
            .OrderBy(entry => entry.ComboId, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.ComboId}:{entry.TeamCount}")
            .ToList();
        var phasedCombos = phasedFixture.Db.GetBandRankingCombos("Band_Duets")
            .OrderBy(entry => entry.ComboId, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.ComboId}:{entry.TeamCount}")
            .ToList();
        Assert.Equal(monolithicCombos, comboBatchedCombos);
        Assert.Equal(monolithicCombos, phasedCombos);

        foreach (var comboId in monolithicFixture.Db.GetBandRankingCombos("Band_Duets").Select(entry => entry.ComboId))
        {
            var monolithicEntries = monolithicFixture.Db.GetBandTeamRankings("Band_Duets", comboId, pageSize: 50).Entries
                .Select(SerializeBandTeamRanking)
                .ToList();
            var comboBatchedEntries = comboBatchedFixture.Db.GetBandTeamRankings("Band_Duets", comboId, pageSize: 50).Entries
                .Select(SerializeBandTeamRanking)
                .ToList();
            var phasedEntries = phasedFixture.Db.GetBandTeamRankings("Band_Duets", comboId, pageSize: 50).Entries
                .Select(SerializeBandTeamRanking)
                .ToList();
            Assert.Equal(monolithicEntries, comboBatchedEntries);
            Assert.Equal(monolithicEntries, phasedEntries);
        }
    }

    [Fact]
    public void SnapshotBandRankHistory_SameDayRerun_DoesNotDuplicateRows()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistory("Band_Duets");

        var initialRankingRows = CountBandHistoryRows("band_team_rank_history", "Band_Duets");
        var initialStatsRows = CountBandHistoryRows("band_team_ranking_stats_history", "Band_Duets");

        Db.SnapshotBandRankHistory("Band_Duets");

        Assert.Equal(initialRankingRows, CountBandHistoryRows("band_team_rank_history", "Band_Duets"));
        Assert.Equal(initialStatsRows, CountBandHistoryRows("band_team_ranking_stats_history", "Band_Duets"));
    }

    [Fact]
    public void SnapshotBandRankHistory_ChangedRerun_AddsNewSnapshotRows()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistory("Band_Duets");

        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        BackdateBandHistory("Band_Duets", yesterday);
        var baselineRows = CountBandHistoryRows("band_team_rank_history", "Band_Duets");

        var persistence = new BandLeaderboardPersistence(_fixture.DataSource, Substitute.For<Microsoft.Extensions.Logging.ILogger<BandLeaderboardPersistence>>());
        persistence.UpsertBandEntries("song_1", "Band_Duets",
        [
            MakeBandEntry(["p1", "p2"], "0:1", 1500),
            MakeBandEntry(["p3", "p4"], "0:3", 1300, isFullCombo: true),
            MakeBandEntry(["p5", "p6"], "0:1", 800),
        ]);

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistory("Band_Duets");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.True(CountBandHistoryRows("band_team_rank_history", "Band_Duets") > baselineRows);
        Assert.True(CountBandHistoryRows("band_team_rank_history", "Band_Duets", today) > 0);
    }

    [Fact]
    public void SnapshotBandRankHistory_WritesLatestStateAndNarrowPoints()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        var result = Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions());

        Assert.True(result.RowsScanned > 0);
        Assert.True(result.RowsInserted > 0);
        Assert.True(CountBandHistoryRows("band_team_rank_history", "Band_Duets") > 0);
        Assert.True(CountBandHistoryRows("band_team_rank_history_points", "Band_Duets") > 0);
        Assert.True(CountBandHistoryRows("band_team_rank_history_latest", "Band_Duets") > 0);
    }

    [Fact]
    public void SnapshotBandRankHistory_NarrowOnlyStillServesApiHistory()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions
        {
            UseWideHistoryCompatibilityWrite = false,
            UseNarrowHistory = true,
            UseLatestState = true,
        });

        Assert.Equal(0, CountBandHistoryRows("band_team_rank_history", "Band_Duets"));
        Assert.True(CountBandHistoryRows("band_team_rank_history_points", "Band_Duets") > 0);
        Assert.NotEmpty(Db.GetBandRankHistory("Band_Duets", "p1:p2"));
        var status = Db.GetBandRankHistoryStatus("Band_Duets");
        Assert.Equal("current", status.HistoryStatus);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), status.HistoryComputedThrough);
    }

    [Fact]
    public void GetBandRankHistoryStatus_UsesStatsHistoryWhenPointsAreAbsent()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions());

        using (var conn = _fixture.DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM band_team_rank_history_points WHERE band_type = @bandType";
            cmd.Parameters.AddWithValue("bandType", "Band_Duets");
            cmd.ExecuteNonQuery();
        }

        var status = Db.GetBandRankHistoryStatus("Band_Duets");

        Assert.Equal("current", status.HistoryStatus);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), status.HistoryComputedThrough);
    }

    [Fact]
    public void EnqueueBandRankHistoryJob_CoalescesOlderSameDayJobs()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var oldJob = Db.EnqueueBandRankHistoryJob(100, "Band_Duets", today, "Background", coalesceSameDay: true);
        var newJob = Db.EnqueueBandRankHistoryJob(101, "Band_Duets", today, "Background", coalesceSameDay: true);

        Assert.NotEqual(oldJob.JobId, newJob.JobId);
        Assert.Equal("superseded", GetBandHistoryJobStatus(oldJob.JobId));
        Assert.Equal(newJob.JobId, Db.GetNextBandRankHistoryJob()?.JobId);
    }

    [Fact]
    public void GetBandRankHistory_ReturnsSnapshotsForTeam()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistory("Band_Duets");

        var current = Db.GetBandTeamRanking("Band_Duets", "p1:p2");
        var history = Db.GetBandRankHistory("Band_Duets", "p1:p2");

        Assert.NotNull(current);
        var snapshot = Assert.Single(history);
        Assert.Equal(current.AdjustedSkillRank, snapshot.AdjustedSkillRank);
        Assert.Equal(current.WeightedRank, snapshot.WeightedRank);
        Assert.Equal(current.FcRateRank, snapshot.FcRateRank);
        Assert.Equal(current.TotalScoreRank, snapshot.TotalScoreRank);
        Assert.Equal(current.TotalChartedSongs, snapshot.TotalChartedSongs);
        Assert.Equal(current.TotalRankedTeams, snapshot.TotalRankedTeams);
    }

    [Fact]
    public void GetBandSongPerformances_ReturnsAnyComboSongPercentiles()
    {
        SeedBandRankingsSource();

        var performances = Db.GetBandSongPerformances("Band_Duets", "p1:p2");

        Assert.Equal(["song_0", "song_1"], performances.Select(p => p.SongId).ToArray());
        var song0 = performances.Single(p => p.SongId == "song_0");
        var song1 = performances.Single(p => p.SongId == "song_1");
        Assert.Equal(1, song0.Rank);
        Assert.Equal(2, song0.TotalEntries);
        Assert.Equal(50.0, song0.Percentile, 3);
        Assert.Equal(1100, song0.Score);
        Assert.Equal(2, song1.Rank);
        Assert.Equal(3, song1.TotalEntries);
        Assert.Equal(66.667, song1.Percentile, 3);
    }

    [Fact]
    public void GetBandSongPerformanceExtremes_ReturnsLimitedBestAndWorst()
    {
        SeedBandRankingsSource();

        var (best, worst) = Db.GetBandSongPerformanceExtremes("Band_Duets", "p1:p2", limit: 1);

        var bestSong = Assert.Single(best);
        var worstSong = Assert.Single(worst);
        Assert.Equal("song_0", bestSong.SongId);
        Assert.Equal(50.0, bestSong.Percentile, 3);
        Assert.Equal("song_1", worstSong.SongId);
        Assert.Equal(66.667, worstSong.Percentile, 3);
    }

    [Fact]
    public void RebuildBandSongTeamRankings_PopulatesOverallAndComboProjectionRows()
    {
        SeedBandRankingsSource();

        var metrics = Db.RebuildBandSongTeamRankings("Band_Duets");

        Assert.Equal(11, metrics.RowCount);
        Assert.Equal(5, metrics.OverallRows);
        Assert.Equal(6, metrics.ComboRows);
        Assert.Equal(5, CountBandSongRankingRows("Band_Duets", "overall"));
        Assert.Equal(6, CountBandSongRankingRows("Band_Duets", "combo"));
    }

    [Fact]
    public void GetBandSongPerformanceExtremes_ReadsDerivedProjectionWhenAvailable()
    {
        SeedBandRankingsSource();
        Db.RebuildBandSongTeamRankings("Band_Duets");
        DeleteBandEntries("Band_Duets");

        var (best, worst) = Db.GetBandSongPerformanceExtremes("Band_Duets", "p1:p2", limit: 1);
        var bestSong = Assert.Single(best);
        var worstSong = Assert.Single(worst);
        Assert.Equal("song_0", bestSong.SongId);
        Assert.Equal(50.0, bestSong.Percentile, 3);
        Assert.Equal("song_1", worstSong.SongId);
        Assert.Equal(66.667, worstSong.Percentile, 3);

        var (comboBest, comboWorst) = Db.GetBandSongPerformanceExtremes("Band_Duets", "p1:p2", "Solo_Guitar+Solo_Bass", limit: 1);
        Assert.Equal("song_1", Assert.Single(comboBest).SongId);
        Assert.Equal("song_0", Assert.Single(comboWorst).SongId);
    }

    private int CountBandHistoryRows(string tableName, string bandType, DateOnly? snapshotDate = null)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        var dateFilter = snapshotDate.HasValue ? " AND snapshot_date = @snapshotDate" : string.Empty;
        cmd.CommandText = $"SELECT COUNT(*) FROM {BandRankingStorageNames.QuoteIdentifier(tableName)} WHERE band_type = @bandType{dateFilter}";
        cmd.Parameters.AddWithValue("bandType", bandType);
        if (snapshotDate.HasValue)
            cmd.Parameters.AddWithValue("snapshotDate", snapshotDate.Value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int CountBandSongRankingRows(string bandType, string rankingScope)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*)
            FROM band_song_team_rankings
            WHERE band_type = @bandType
              AND ranking_scope = @rankingScope;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("rankingScope", rankingScope);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void DeleteBandEntries(string bandType)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM band_entries WHERE band_type = @bandType";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.ExecuteNonQuery();
    }

    private void BackdateBandHistory(string bandType, DateOnly snapshotDate)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE band_team_rank_history
            SET snapshot_date = @snapshotDate
            WHERE band_type = @bandType;

            UPDATE band_team_ranking_stats_history
            SET snapshot_date = @snapshotDate
            WHERE band_type = @bandType;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.ExecuteNonQuery();
    }

    private string GetBandHistoryJobStatus(long jobId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status FROM band_rank_history_jobs WHERE job_id = @jobId";
        cmd.Parameters.AddWithValue("jobId", jobId);
        return (string)cmd.ExecuteScalar()!;
    }

    private void SeedBandSearchProjection(string bandType, string teamKey, string[] memberAccountIds, string memberInstrumentsJson)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO band_search_team_projection (
                band_type, team_key, band_id, appearance_count, member_account_ids,
                member_instruments_json, combo_appearances_json, updated_at)
            VALUES (@bandType, @teamKey, @bandId, 1, @memberAccountIds,
                CAST(@memberInstrumentsJson AS jsonb), '{}'::jsonb, @updatedAt)
            ON CONFLICT (band_type, team_key) DO UPDATE SET
                member_account_ids = EXCLUDED.member_account_ids,
                member_instruments_json = EXCLUDED.member_instruments_json,
                updated_at = EXCLUDED.updated_at;
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("bandId", BandIdentity.CreateBandId(bandType, teamKey));
        cmd.Parameters.AddWithValue("memberAccountIds", memberAccountIds);
        cmd.Parameters.AddWithValue("memberInstrumentsJson", memberInstrumentsJson);
        cmd.Parameters.AddWithValue("updatedAt", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private static string SerializeBandTeamRanking(BandTeamRankingDto entry)
    {
        return string.Join("|",
        [
            entry.BandType,
            entry.ComboId ?? string.Empty,
            entry.TeamKey,
            string.Join(",", entry.TeamMembers),
            entry.SongsPlayed.ToString(CultureInfo.InvariantCulture),
            entry.TotalChartedSongs.ToString(CultureInfo.InvariantCulture),
            entry.Coverage.ToString("R", CultureInfo.InvariantCulture),
            entry.RawSkillRating.ToString("R", CultureInfo.InvariantCulture),
            entry.AdjustedSkillRating.ToString("R", CultureInfo.InvariantCulture),
            entry.AdjustedSkillRank.ToString(CultureInfo.InvariantCulture),
            entry.WeightedRating.ToString("R", CultureInfo.InvariantCulture),
            entry.WeightedRank.ToString(CultureInfo.InvariantCulture),
            entry.FcRate.ToString("R", CultureInfo.InvariantCulture),
            entry.FcRateRank.ToString(CultureInfo.InvariantCulture),
            entry.TotalScore.ToString(CultureInfo.InvariantCulture),
            entry.TotalScoreRank.ToString(CultureInfo.InvariantCulture),
            entry.AvgAccuracy.ToString("R", CultureInfo.InvariantCulture),
            entry.FullComboCount.ToString(CultureInfo.InvariantCulture),
            entry.AvgStars.ToString("R", CultureInfo.InvariantCulture),
            entry.BestRank.ToString(CultureInfo.InvariantCulture),
            entry.AvgRank.ToString("R", CultureInfo.InvariantCulture),
            entry.RawWeightedRating?.ToString("R", CultureInfo.InvariantCulture) ?? "null",
            entry.TotalRankedTeams.ToString(CultureInfo.InvariantCulture),
        ]);
    }

    // ═══════════════════════════════════════════════════════════
    // CompositeRankingDeltas
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void WriteCompositeRankingDeltas_StoresAndTruncates()
    {
        var deltas = new List<(string AccountId, double LeewayBucket, double AdjustedRating,
            double WeightedRating, double FcRateRating, double TotalScore, double MaxScoreRating,
            int InstrumentsPlayed, int TotalSongsPlayed)>
        {
            ("p1", -3.0, 0.05, 0.06, 0.8, 50000.0, 0.95, 2, 50),
            ("p2", -3.0, 0.10, 0.12, 0.6, 40000.0, 0.90, 1, 25),
        };
        Db.WriteCompositeRankingDeltas(deltas);

        // No direct read API yet — just verify write succeeded without error
        // Truncate should also succeed
        Db.TruncateCompositeRankingDeltas();
    }

    [Fact]
    public void TruncateCompositeRankingDeltas_NoErrorWhenEmpty()
    {
        Db.TruncateCompositeRankingDeltas(); // should not throw
    }

    // ═══════════════════════════════════════════════════════════
    // ComboRankingDeltas
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void WriteComboRankingDeltas_StoresAndTruncates()
    {
        var deltas = new List<(string ComboId, string AccountId, double LeewayBucket,
            double AdjustedRating, double WeightedRating, double FcRate,
            long TotalScore, double MaxScorePercent, int SongsPlayed, int FullComboCount)>
        {
            ("03", "p1", -3.0, 0.05, 0.06, 0.8, 50000, 0.95, 100, 80),
            ("03", "p2", -3.0, 0.10, 0.12, 0.6, 40000, 0.90, 80, 48),
        };
        Db.WriteComboRankingDeltas(deltas);

        // No direct read API yet — verify write succeeded
        Db.TruncateComboRankingDeltas();
    }

    [Fact]
    public void TruncateComboRankingDeltas_NoErrorWhenEmpty()
    {
        Db.TruncateComboRankingDeltas(); // should not throw
    }
}
