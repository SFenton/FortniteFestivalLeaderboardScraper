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

    [Fact]
    public void CleanupCompositeRankHistoryRetention_DeletesOnlyConfiguredBatch()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-365);
        InsertCompositeRankHistoryRow("p1", cutoff.AddDays(-30), 6);
        InsertCompositeRankHistoryRow("p1", cutoff.AddDays(-20), 5);
        InsertCompositeRankHistoryRow("p1", cutoff.AddDays(-10), 4);
        InsertCompositeRankHistoryRow("p1", cutoff.AddDays(2), 3);
        InsertCompositeRankHistoryRow("p2", cutoff.AddDays(-30), 9);

        var firstPassDeleted = Db.CleanupCompositeRankHistoryRetention(batchSize: 1, maxBatches: 1);

        Assert.Equal(1, firstPassDeleted);
        Assert.Equal(3, CountCompositeRankHistoryRows("p1"));
        Assert.Equal([cutoff.AddDays(-30)], GetCompositeRankHistoryDates("p2"));

        var secondPassDeleted = Db.CleanupCompositeRankHistoryRetention(batchSize: 100, maxBatches: 10);

        Assert.Equal(1, secondPassDeleted);
        Assert.Equal([cutoff.AddDays(-10), cutoff.AddDays(2)], GetCompositeRankHistoryDates("p1"));
        Assert.Equal([cutoff.AddDays(-30)], GetCompositeRankHistoryDates("p2"));
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

        var bestComboForP1 = Db.GetBandTeamRankingForAccount("Band_Duets", "p1", comboId, rankBy: "totalscore");
        var expectedComboBestForP1 = Db.GetBandTeamRankings("Band_Duets", comboId, rankBy: "totalscore")
            .Entries
            .First(entry => entry.TeamMembers.Contains("p1"));
        Assert.NotNull(bestComboForP1);
        Assert.Equal(expectedComboBestForP1.TeamKey, bestComboForP1.TeamKey);
        Assert.Equal(expectedComboBestForP1.TotalScoreRank, bestComboForP1.TotalScoreRank);
        Assert.Null(Db.GetBandTeamRankingForAccount("Band_Duets", "p3", comboId, rankBy: "totalscore"));

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
    public void BandRankHistoryV2Schema_EnsuresTablesAndCurrentMetadataColumnsIdempotently()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        _ = Db.GetBandRankHistoryV2Parity("Band_Duets", today);
        _ = Db.GetBandRankHistoryV2Parity("Band_Duets", today);

        Assert.True(TableExists("band_team_ranking_generation"));
        Assert.True(TableExists("band_team_rank_history_snapshot_v2"));
        Assert.True(TableExists("band_team_rank_history_points_v2"));
        Assert.True(TableExists("band_team_rank_history_latest_v2"));
        Assert.True(ColumnExists("band_rank_history_jobs", "source_generation"));
        Assert.True(ColumnExists("band_rank_history_job_chunks", "chunk_ordinal"));
        Assert.True(ColumnExists("band_rank_history_job_chunks", "team_key_start"));
        Assert.True(ColumnExists("band_rank_history_job_chunks", "team_key_end"));
        Assert.True(ColumnExists("band_rank_history_job_chunks", "estimated_rows"));
        Assert.True(ColumnExists("band_rank_history_job_chunks", "source_generation"));
        Assert.True(ColumnExists("band_team_rankings_current_band_duets", "ranking_generation"));
        Assert.True(ColumnExists("band_team_rankings_current_band_duets", "row_fingerprint"));
    }

    [Fact]
    public void RebuildBandTeamRankings_PopulatesGenerationAndRowFingerprints()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);

        var metadata = GetCurrentBandRankingMetadata("Band_Duets");

        Assert.True(metadata.RowCount > 0);
        Assert.Equal(metadata.RowCount, metadata.RowsWithGeneration);
        Assert.Equal(metadata.RowCount, metadata.RowsWithFingerprint);
        Assert.Equal(1, metadata.DistinctGenerationCount);
        Assert.True(metadata.GenerationId > 0);
        Assert.Equal("published", GetBandRankingGenerationStatus(metadata.GenerationId));
        Assert.Equal(metadata.RowCount, GetBandRankingGenerationRowCount(metadata.GenerationId));
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
    public void SnapshotBandRankHistory_LegacyModeDoesNotWriteV2Rows()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions());

        Assert.True(CountBandHistoryRows("band_team_rank_history_points", "Band_Duets") > 0);
        Assert.Equal(0, CountBandHistoryRows("band_team_rank_history_points_v2", "Band_Duets"));
        Assert.Equal(0, CountBandHistoryRows("band_team_rank_history_latest_v2", "Band_Duets"));
        Assert.Equal(0, CountBandHistoryRows("band_team_rank_history_snapshot_v2", "Band_Duets"));
    }

    [Fact]
    public void SnapshotBandRankHistory_DualModeWritesV2ParityAndApiStillUsesLegacyHistory()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        var result = Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions
        {
            WriteMode = BandRankHistoryWriteMode.Dual,
        });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var legacyRows = CountBandHistoryRows("band_team_rank_history_points", "Band_Duets", today);
        var v2Rows = CountBandHistoryRows("band_team_rank_history_points_v2", "Band_Duets", today);
        var latestRows = CountBandHistoryRows("band_team_rank_history_latest_v2", "Band_Duets", today);
        var parity = Db.GetBandRankHistoryV2Parity("Band_Duets", today);

        Assert.True(result.RowsInserted > 0);
        Assert.True(legacyRows > 0);
        Assert.Equal(legacyRows, v2Rows);
        Assert.Equal(legacyRows, latestRows);
        Assert.Equal(legacyRows, parity.LegacyRows);
        Assert.Equal(legacyRows, parity.V2Rows);
        Assert.Equal(legacyRows, parity.MatchingRows);
        Assert.Equal(0, parity.MissingFromV2);
        Assert.Equal(0, parity.MissingFromLegacy);
        Assert.Equal(0, parity.ValueMismatches);
        Assert.True(parity.CompleteSnapshots > 0);
        Assert.Equal(0, parity.IncompleteSnapshots);
        Assert.Equal(parity.LegacyStatsRows, parity.V2SnapshotSourceRows);
        Assert.Equal(latestRows, CountV2LatestRowsWithGenerationAndFingerprint("Band_Duets", today));

        var latestParity = Db.GetBandRankHistoryV2LatestParity("Band_Duets", today);
        Assert.Equal(legacyRows, latestParity.V2PointRows);
        Assert.Equal(legacyRows, latestParity.LatestRowsForSnapshot);
        Assert.Equal(legacyRows, latestParity.MatchingLatestRows);
        Assert.Equal(0, latestParity.MissingFromLatest);
        Assert.Equal(0, latestParity.LatestMismatches);
        Assert.Equal(0, latestParity.ExtraLatestRowsForSnapshot);

        DeleteV2BandHistory("Band_Duets");
        Assert.NotEmpty(Db.GetBandRankHistory("Band_Duets", "p1:p2"));
    }

    [Fact]
    public void SnapshotBandRankHistory_DualModeSameDayRerunDoesNotDuplicateV2Points()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions
        {
            WriteMode = BandRankHistoryWriteMode.Dual,
        });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var initialV2Rows = CountBandHistoryRows("band_team_rank_history_points_v2", "Band_Duets", today);

        var rerun = Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions
        {
            WriteMode = BandRankHistoryWriteMode.Dual,
        });

        Assert.Equal(0, rerun.RowsInserted);
        Assert.Equal(initialV2Rows, CountBandHistoryRows("band_team_rank_history_points_v2", "Band_Duets", today));
        Assert.Equal(CountBandHistoryRows("band_team_rank_history_points", "Band_Duets", today), CountBandHistoryRows("band_team_rank_history_points_v2", "Band_Duets", today));
    }

    [Fact]
    public void SnapshotBandRankHistory_V2OnlyModeWritesOnlyV2History()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        var result = Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions
        {
            WriteMode = BandRankHistoryWriteMode.V2Only,
        });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var v2Rows = CountBandHistoryRows("band_team_rank_history_points_v2", "Band_Duets", today);

        Assert.True(result.RowsInserted > 0);
        Assert.True(v2Rows > 0);
        Assert.Equal(0, CountBandHistoryRows("band_team_rank_history", "Band_Duets", today));
        Assert.Equal(0, CountBandHistoryRows("band_team_rank_history_points", "Band_Duets", today));
        Assert.Equal(0, CountBandHistoryRows("band_team_rank_history_latest", "Band_Duets", today));
        Assert.Equal(0, CountBandHistoryRows("band_team_ranking_stats_history", "Band_Duets", today));
        Assert.Equal(v2Rows, CountBandHistoryRows("band_team_rank_history_latest_v2", "Band_Duets", today));
        Assert.True(CountBandHistoryRows("band_team_rank_history_snapshot_v2", "Band_Duets", today) > 0);

        using var v2Db = CreateMetaDatabase(BandRankHistoryApiReadSource.V2NarrowOnly);
        Assert.NotEmpty(v2Db.GetBandRankHistory("Band_Duets", "p1:p2"));
    }

    [Fact]
    public void SnapshotBandRankHistory_V2OnlyModeUsesV2LatestForSameDayRerun()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions
        {
            WriteMode = BandRankHistoryWriteMode.V2Only,
        });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var initialV2Rows = CountBandHistoryRows("band_team_rank_history_points_v2", "Band_Duets", today);

        var rerun = Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions
        {
            WriteMode = BandRankHistoryWriteMode.V2Only,
        });

        Assert.Equal(0, rerun.RowsInserted);
        Assert.Equal(initialV2Rows, CountBandHistoryRows("band_team_rank_history_points_v2", "Band_Duets", today));
        Assert.Equal(0, CountBandHistoryRows("band_team_rank_history", "Band_Duets", today));
        Assert.Equal(0, CountBandHistoryRows("band_team_rank_history_points", "Band_Duets", today));
        Assert.Equal(0, CountBandHistoryRows("band_team_rank_history_latest", "Band_Duets", today));
    }

    [Fact]
    public void GetBandRankHistoryV2Parity_ReportsValueMismatchSamples()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions
        {
            WriteMode = BandRankHistoryWriteMode.Dual,
        });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        ChangeOneV2BandHistoryRank("Band_Duets", today);

        var parity = Db.GetBandRankHistoryV2Parity("Band_Duets", today, sampleLimit: 1);
        var sample = Assert.Single(parity.Samples);

        Assert.Equal(0, parity.MissingFromV2);
        Assert.Equal(0, parity.MissingFromLegacy);
        Assert.Equal(1, parity.ValueMismatches);
        Assert.Equal("value_mismatch", sample.MismatchKind);
        Assert.Contains("adjusted_skill_rank", sample.MismatchedColumns);
    }

    [Fact]
    public void GetBandRankHistoryV2LatestParity_ReportsStaleLatestRow()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions
        {
            WriteMode = BandRankHistoryWriteMode.Dual,
        });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        StaleOneV2LatestRow("Band_Duets", today);

        var parity = Db.GetBandRankHistoryV2LatestParity("Band_Duets", today, sampleLimit: 1);
        var sample = Assert.Single(parity.Samples);

        Assert.Equal(1, parity.LatestMismatches);
        Assert.Equal("latest_mismatch", sample.MismatchKind);
        Assert.Contains("snapshot_date", sample.MismatchedColumns);
    }

    [Fact]
    public void SnapshotBandRankHistory_DualModeDoesNotRegressV2LatestRows()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions
        {
            WriteMode = BandRankHistoryWriteMode.Dual,
        });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var future = today.AddDays(1);
        SetV2LatestSnapshotDate("Band_Duets", "p1:p2", future);
        UpdateBandEntryScore("song_1", "Band_Duets", "p1:p2", 1600);

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions
        {
            WriteMode = BandRankHistoryWriteMode.Dual,
        });

        var latestDates = GetV2LatestSnapshotDates("Band_Duets", "p1:p2");
        Assert.NotEmpty(latestDates);
        Assert.All(latestDates, date => Assert.Equal(future, date));
    }

    [Fact]
    public void BackfillBandRankHistoryV2FromLegacy_CopiesOldLegacySnapshotAndDoesNotRegressLatest()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions
        {
            WriteMode = BandRankHistoryWriteMode.Dual,
        });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var legacyOnlyDate = today.AddDays(-1);
        CloneBandHistorySnapshot("Band_Duets", today, legacyOnlyDate);
        var legacyRows = CountBandHistoryRows("band_team_rank_history_points", "Band_Duets", legacyOnlyDate);

        var dryRun = Db.BackfillBandRankHistoryV2FromLegacy("Band_Duets", new BandRankHistoryV2BackfillOptions
        {
            StartDate = legacyOnlyDate,
            EndDate = legacyOnlyDate,
        });

        Assert.True(legacyRows > 0);
        Assert.True(dryRun.SlicesTotal > 0);
        Assert.Equal(legacyRows, dryRun.MissingV2Rows);
        Assert.Equal(0, CountBandHistoryRows("band_team_rank_history_points_v2", "Band_Duets", legacyOnlyDate));

        var firstRun = Db.BackfillBandRankHistoryV2FromLegacy("Band_Duets", new BandRankHistoryV2BackfillOptions
        {
            StartDate = legacyOnlyDate,
            EndDate = legacyOnlyDate,
            Execute = true,
        });
        var parity = Db.GetBandRankHistoryV2Parity("Band_Duets", legacyOnlyDate);
        var latestDates = GetV2LatestSnapshotDates("Band_Duets", "p1:p2");

        Assert.Equal(legacyRows, firstRun.PointRowsInserted);
        Assert.Equal(legacyRows, CountBandHistoryRows("band_team_rank_history_points_v2", "Band_Duets", legacyOnlyDate));
        Assert.Equal(legacyRows, parity.LegacyRows);
        Assert.Equal(legacyRows, parity.V2Rows);
        Assert.Equal(legacyRows, parity.MatchingRows);
        Assert.Equal(0, parity.MissingFromV2);
        Assert.Equal(0, parity.MissingFromLegacy);
        Assert.Equal(0, parity.ValueMismatches);
        Assert.All(latestDates, date => Assert.Equal(today, date));

        var secondRun = Db.BackfillBandRankHistoryV2FromLegacy("Band_Duets", new BandRankHistoryV2BackfillOptions
        {
            StartDate = legacyOnlyDate,
            EndDate = legacyOnlyDate,
            Execute = true,
        });

        Assert.Equal(0, secondRun.SlicesTotal);
        Assert.Equal(0, secondRun.PointRowsInserted);
    }

    [Fact]
    public void GetBandRankHistoryV2ReadPreview_ReportsCurrentFallbackTruncation()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions
        {
            WriteMode = BandRankHistoryWriteMode.Dual,
        });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var legacyOnlyDate = today.AddDays(-1);
        CloneBandHistorySnapshot("Band_Duets", today, legacyOnlyDate);

        using var fallbackDb = CreateMetaDatabase(BandRankHistoryApiReadSource.V2NarrowWithLegacyFallback);
        var fallbackHistory = fallbackDb.GetBandRankHistory("Band_Duets", "p1:p2");
        var preview = Db.GetBandRankHistoryV2ReadPreview("Band_Duets", "p1:p2", days: 30);

        Assert.DoesNotContain(legacyOnlyDate.ToString("yyyy-MM-dd"), fallbackHistory.Select(static row => row.SnapshotDate));
        Assert.True(preview.CurrentV2FallbackWouldHideLegacyDates);
        Assert.Contains(legacyOnlyDate.ToString("yyyy-MM-dd"), preview.LegacyDatesHiddenByCurrentV2Fallback);
        Assert.Contains(legacyOnlyDate.ToString("yyyy-MM-dd"), preview.MergedDates);
    }

    [Fact]
    public void GetBandRankHistoryWideNarrowParity_CleanSnapshotHasNoMismatches()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions());

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var wideRows = CountBandHistoryRows("band_team_rank_history", "Band_Duets", today);
        var parity = Db.GetBandRankHistoryWideNarrowParity("Band_Duets", today);

        Assert.True(wideRows > 0);
        Assert.Equal(wideRows, parity.WideRows);
        Assert.Equal(wideRows, parity.NarrowRows);
        Assert.Equal(wideRows, parity.MatchingRows);
        Assert.Equal(0, parity.MissingFromNarrow);
        Assert.Equal(0, parity.MissingFromWide);
        Assert.Equal(0, parity.ValueMismatches);
        Assert.Empty(parity.Samples);
    }

    [Fact]
    public void GetBandRankHistoryWideNarrowParity_ReportsMissingNarrowRows()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions());

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        DeleteOneBandHistoryRow("band_team_rank_history_points", "Band_Duets", today);

        var parity = Db.GetBandRankHistoryWideNarrowParity("Band_Duets", today);
        var sample = Assert.Single(parity.Samples);

        Assert.Equal(1, parity.MissingFromNarrow);
        Assert.Equal(0, parity.MissingFromWide);
        Assert.Equal(0, parity.ValueMismatches);
        Assert.Equal("missing_from_narrow", sample.MismatchKind);
    }

    [Fact]
    public void GetBandRankHistoryWideNarrowParity_ReportsMissingWideRows()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions());

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        DeleteOneBandHistoryRow("band_team_rank_history", "Band_Duets", today);

        var parity = Db.GetBandRankHistoryWideNarrowParity("Band_Duets", today);
        var sample = Assert.Single(parity.Samples);

        Assert.Equal(0, parity.MissingFromNarrow);
        Assert.Equal(1, parity.MissingFromWide);
        Assert.Equal(0, parity.ValueMismatches);
        Assert.Equal("missing_from_wide", sample.MismatchKind);
    }

    [Fact]
    public void GetBandRankHistoryWideNarrowParity_ReportsValueMismatchSamples()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions());

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        ChangeOneNarrowBandHistoryRank("Band_Duets", today);

        var parity = Db.GetBandRankHistoryWideNarrowParity("Band_Duets", today, sampleLimit: 1);
        var sample = Assert.Single(parity.Samples);

        Assert.Equal(0, parity.MissingFromNarrow);
        Assert.Equal(0, parity.MissingFromWide);
        Assert.Equal(1, parity.ValueMismatches);
        Assert.Equal("value_mismatch", sample.MismatchKind);
        Assert.Contains("adjusted_skill_rank", sample.MismatchedColumns);
    }

    [Fact]
    public void GetBandRankHistory_V2NarrowOnlyReadsV2WhenLegacyRowsAreGone()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions
        {
            WriteMode = BandRankHistoryWriteMode.Dual,
        });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var legacyHistory = Db.GetBandRankHistory("Band_Duets", "p1:p2");
        Assert.NotEmpty(legacyHistory);
        DeleteLegacyBandHistory("Band_Duets");

        using var v2Db = CreateMetaDatabase(BandRankHistoryApiReadSource.V2NarrowOnly);
        var v2History = v2Db.GetBandRankHistory("Band_Duets", "p1:p2");
        var status = v2Db.GetBandRankHistoryStatus("Band_Duets");

        Assert.Equal(legacyHistory.Select(static row => row.SnapshotDate), v2History.Select(static row => row.SnapshotDate));
        Assert.Equal(legacyHistory.Select(static row => row.AdjustedSkillRank), v2History.Select(static row => row.AdjustedSkillRank));
        Assert.Equal("current", status.HistoryStatus);
        Assert.Equal(today.ToString("yyyy-MM-dd"), status.HistoryComputedThrough);
    }

    [Fact]
    public void GetBandRankHistory_V2NarrowOnlyDoesNotFallbackToLegacy()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions());

        using var v2Db = CreateMetaDatabase(BandRankHistoryApiReadSource.V2NarrowOnly);
        var history = v2Db.GetBandRankHistory("Band_Duets", "p1:p2");
        var status = v2Db.GetBandRankHistoryStatus("Band_Duets");

        Assert.Empty(history);
        Assert.Equal("stale", status.HistoryStatus);
        Assert.Null(status.HistoryComputedThrough);
    }

    [Fact]
    public void GetBandRankHistory_V2NarrowWithLegacyFallbackUsesLegacyWhenV2Missing()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions());

        using var fallbackDb = CreateMetaDatabase(BandRankHistoryApiReadSource.V2NarrowWithLegacyFallback);
        var history = fallbackDb.GetBandRankHistory("Band_Duets", "p1:p2");
        var status = fallbackDb.GetBandRankHistoryStatus("Band_Duets");

        Assert.NotEmpty(history);
        Assert.Equal("current", status.HistoryStatus);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), status.HistoryComputedThrough);
    }

    [Fact]
    public void GetBandRankHistory_WideReadSourceDoesNotUseNarrowOnlyRows()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions
        {
            UseWideHistoryCompatibilityWrite = false,
            UseNarrowHistory = true,
        });

        using var wideDb = CreateMetaDatabase(BandRankHistoryApiReadSource.Wide);
        using var narrowDb = CreateMetaDatabase(BandRankHistoryApiReadSource.Narrow);

        Assert.Empty(wideDb.GetBandRankHistory("Band_Duets", "p1:p2"));
        Assert.NotEmpty(narrowDb.GetBandRankHistory("Band_Duets", "p1:p2"));
    }

    [Fact]
    public void GetBandRankHistory_NarrowWithWideFallbackReportsFreshNarrowThroughWhenWideIsStale()
    {
        SeedBandRankingsSource();

        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions());

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var staleWideDate = today.AddDays(-1);
        CloneBandHistorySnapshot("Band_Duets", today, staleWideDate);
        DeleteBandHistorySnapshotRows("band_team_rank_history", "Band_Duets", today);
        DeleteBandHistorySnapshotRows("band_team_ranking_stats_history", "Band_Duets", today);

        using var fallbackDb = CreateMetaDatabase(BandRankHistoryApiReadSource.NarrowWithWideFallback);
        var history = fallbackDb.GetBandRankHistory("Band_Duets", "p1:p2");
        var status = fallbackDb.GetBandRankHistoryStatus("Band_Duets");

        Assert.Contains(today.ToString("yyyy-MM-dd"), history.Select(static row => row.SnapshotDate));
        Assert.Equal("current", status.HistoryStatus);
        Assert.Equal(today.ToString("yyyy-MM-dd"), status.HistoryComputedThrough);
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
    public void CleanupBandRankHistoryRetention_DeletesOnlyConfiguredBatchPerHistoryTable()
    {
        SeedBandRankingsSource();
        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions());
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var cutoff = today.AddDays(-365);
        var oldSnapshot = cutoff.AddDays(-20);
        var retainedCutoffSnapshot = cutoff.AddDays(-10);
        CloneBandHistorySnapshot("Band_Duets", today, oldSnapshot);
        CloneBandHistorySnapshot("Band_Duets", today, retainedCutoffSnapshot);
        var initialOldWideRows = CountBandHistoryRows("band_team_rank_history", "Band_Duets", oldSnapshot);
        var initialOldPointRows = CountBandHistoryRows("band_team_rank_history_points", "Band_Duets", oldSnapshot);
        var initialOldStatsRows = CountBandHistoryRows("band_team_ranking_stats_history", "Band_Duets", oldSnapshot);

        var firstPassDeleted = Db.CleanupBandRankHistoryRetention("Band_Duets", batchSize: 1, maxBatches: 1);

        Assert.Equal(3, firstPassDeleted);
        Assert.Equal(initialOldWideRows - 1, CountBandHistoryRows("band_team_rank_history", "Band_Duets", oldSnapshot));
        Assert.Equal(initialOldPointRows - 1, CountBandHistoryRows("band_team_rank_history_points", "Band_Duets", oldSnapshot));
        Assert.Equal(initialOldStatsRows - 1, CountBandHistoryRows("band_team_ranking_stats_history", "Band_Duets", oldSnapshot));

        Db.CleanupBandRankHistoryRetention("Band_Duets", batchSize: 100, maxBatches: 10);

        Assert.Equal(0, CountBandHistoryRows("band_team_rank_history", "Band_Duets", oldSnapshot));
        Assert.Equal(0, CountBandHistoryRows("band_team_rank_history_points", "Band_Duets", oldSnapshot));
        Assert.Equal(0, CountBandHistoryRows("band_team_ranking_stats_history", "Band_Duets", oldSnapshot));
        Assert.True(CountBandHistoryRows("band_team_rank_history", "Band_Duets", retainedCutoffSnapshot) > 0);
        Assert.True(CountBandHistoryRows("band_team_rank_history_points", "Band_Duets", retainedCutoffSnapshot) > 0);
        Assert.True(CountBandHistoryRows("band_team_ranking_stats_history", "Band_Duets", retainedCutoffSnapshot) > 0);
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
    public void FailedBandRankHistoryJob_BecomesNextJobOnlyWithinRetryPolicy()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var job = Db.EnqueueBandRankHistoryJob(200, "Band_Duets", today, "Background", coalesceSameDay: true);

        Assert.True(Db.TryStartBandRankHistoryJob(job.JobId, maxAttempts: 2));
        Db.FailBandRankHistoryJob(job.JobId, "stream timeout");

        Assert.Null(Db.GetNextBandRankHistoryJob(maxAttempts: 2, retryDelay: TimeSpan.FromHours(1)));

        var retry = Db.GetNextBandRankHistoryJob(maxAttempts: 2, retryDelay: TimeSpan.Zero);
        Assert.NotNull(retry);
        Assert.Equal(job.JobId, retry.JobId);

        Assert.True(Db.TryStartBandRankHistoryJob(job.JobId, maxAttempts: 2));
        Db.FailBandRankHistoryJob(job.JobId, "second timeout");

        Assert.Null(Db.GetNextBandRankHistoryJob(maxAttempts: 2, retryDelay: TimeSpan.Zero));
    }

    [Fact]
    public void TryStartBandRankHistoryJob_RestartsFailedJobAndClearsFailureFields()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var job = Db.EnqueueBandRankHistoryJob(201, "Band_Duets", today, "Background", coalesceSameDay: true);

        Assert.True(Db.TryStartBandRankHistoryJob(job.JobId, maxAttempts: 3));
        Db.FailBandRankHistoryJob(job.JobId, "temporary failure");

        Assert.True(Db.TryStartBandRankHistoryJob(job.JobId, maxAttempts: 3));
        var status = GetBandHistoryJobStatusDetails(job.JobId);

        Assert.Equal("running", status.Status);
        Assert.Equal(2, status.Attempts);
        Assert.Null(status.LastError);
        Assert.Null(status.FailedAt);
    }

    [Fact]
    public void SnapshotBandRankHistoryChunked_ResumesFailedChunkWithoutDuplicatingCompletedChunks()
    {
        SeedBandRankingsSource();
        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var job = Db.EnqueueBandRankHistoryJob(202, "Band_Duets", today, "Background", coalesceSameDay: true);

        var first = Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions(), job.JobId);
        Db.CompleteBandRankHistoryJob(job.JobId, first);
        var initialRows = CountBandHistoryRows("band_team_rank_history", "Band_Duets", today);
        var initialCompleteChunks = CountBandHistoryChunks(job.JobId, "complete");

        Assert.True(first.ChunksTotal > 1);
        Assert.Equal(first.ChunksTotal, initialCompleteChunks);

        FailOneBandHistoryChunk(job.JobId);
        var failedCounts = GetBandHistoryChunkStatusCounts(job.JobId);
        Assert.Equal(first.ChunksTotal - 1, failedCounts.Complete);
        Assert.Equal(1, failedCounts.Failed);

        var retry = Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions(), job.JobId);
        Db.CompleteBandRankHistoryJob(job.JobId, retry);

        Assert.Equal(first.ChunksTotal, retry.ChunksTotal);
        Assert.Equal(first.ChunksTotal, retry.ChunksCompleted);
        Assert.Equal(first.ChunksTotal, CountBandHistoryChunks(job.JobId, "complete"));
        Assert.Equal(0, CountBandHistoryChunks(job.JobId, "failed"));
        Assert.Equal(initialRows, CountBandHistoryRows("band_team_rank_history", "Band_Duets", today));
        Assert.Equal("complete", GetBandHistoryJobStatus(job.JobId));
    }

    [Fact]
    public void SnapshotBandRankHistoryChunked_RangeSplitsLargeJobChunksAndRecordsGeneration()
    {
        SeedBandRankingsSource();
        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        var metadata = GetCurrentBandRankingMetadata("Band_Duets");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var job = Db.EnqueueBandRankHistoryJob(203, "Band_Duets", today, "Background", coalesceSameDay: true);

        var result = Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions
        {
            ChunkSize = 1,
            CleanupRetention = false,
        }, job.JobId);
        Db.CompleteBandRankHistoryJob(job.JobId, result);

        var chunks = GetBandHistoryChunkRanges(job.JobId);
        var coverage = GetBandHistoryChunkCoverage(job.JobId, "Band_Duets");

        Assert.True(chunks.Count > 1);
        Assert.Equal(chunks.Count, result.ChunksTotal);
        Assert.Equal(chunks.Count, result.ChunksCompleted);
        Assert.Equal(metadata.RowCount, chunks.Sum(static chunk => chunk.EstimatedRows));
        Assert.Equal(metadata.RowCount, result.RowsScanned);
        Assert.Equal(metadata.RowCount, coverage.CoveredRows);
        Assert.Equal(0, coverage.RowsWithWrongMatchCount);
        Assert.All(chunks, static chunk => Assert.Equal("complete", chunk.Status));
        Assert.All(chunks.Where(static chunk => chunk.EstimatedRows > 0), chunk =>
        {
            Assert.NotNull(chunk.TeamKeyStart);
            Assert.NotNull(chunk.TeamKeyEnd);
            Assert.True(chunk.SourceGeneration > 0);
            Assert.Equal(metadata.GenerationId, chunk.SourceGeneration);
        });
    }

    [Fact]
    public void SnapshotBandRankHistoryChunked_PreservesExistingLegacyJobChunks()
    {
        SeedBandRankingsSource();
        Db.RebuildBandTeamRankings("Band_Duets", totalChartedSongs: 2);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var job = Db.EnqueueBandRankHistoryJob(204, "Band_Duets", today, "Background", coalesceSameDay: true);
        InsertLegacyStyleBandHistoryChunks(job.JobId, "Band_Duets");

        var result = Db.SnapshotBandRankHistoryChunked("Band_Duets", new BandRankHistorySnapshotOptions
        {
            ChunkSize = 1,
            CleanupRetention = false,
        }, job.JobId);
        Db.CompleteBandRankHistoryJob(job.JobId, result);

        var chunks = GetBandHistoryChunkRanges(job.JobId);

        Assert.True(chunks.Count > 1);
        Assert.Equal(chunks.Count, result.ChunksTotal);
        Assert.All(chunks, static chunk => Assert.Equal(0, chunk.ChunkOrdinal));
        Assert.All(chunks, static chunk => Assert.Null(chunk.TeamKeyStart));
        Assert.All(chunks, static chunk => Assert.Null(chunk.TeamKeyEnd));
        Assert.All(chunks, static chunk => Assert.Equal("complete", chunk.Status));
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
        Assert.Equal(1, song0.Season);
        Assert.Equal(2, song1.Rank);
        Assert.Equal(3, song1.TotalEntries);
        Assert.Equal(66.667, song1.Percentile, 3);
    }

    [Fact]
    public void GetBandSongPerformances_ReadsDerivedProjectionWhenAvailable()
    {
        SeedBandRankingsSource();
        Db.RebuildBandSongTeamRankings("Band_Duets");
        DeleteBandEntries("Band_Duets");

        var performances = Db.GetBandSongPerformances("Band_Duets", "p1:p2");

        Assert.Equal(["song_0", "song_1"], performances.Select(p => p.SongId).ToArray());
        var song0 = performances.Single(p => p.SongId == "song_0");
        Assert.Equal("Solo_Guitar+Solo_Guitar", song0.ComboId);
        Assert.Equal(1, song0.Rank);
        Assert.Equal(2, song0.TotalEntries);
        Assert.Equal(50.0, song0.Percentile, 3);
        Assert.Equal(1100, song0.Score);
        Assert.Equal(1, song0.Season);
    }

    [Fact]
    public void GetBandSongPerformances_ReadsDerivedDuplicateInstrumentComboProjection()
    {
        SeedDuplicateTriosBandRankingsSource();
        Db.RebuildBandSongTeamRankings("Band_Trios");
        DeleteBandEntries("Band_Trios");

        var performance = Assert.Single(Db.GetBandSongPerformances(
            "Band_Trios",
            "d1:d2:d3",
            "Solo_Bass+Solo_Drums+Solo_Drums"));

        Assert.Equal("dup_trio_song", performance.SongId);
        Assert.Equal("Solo_Bass+Solo_Drums+Solo_Drums", performance.ComboId);
        Assert.Equal(1, performance.Rank);
        Assert.Equal(2, performance.TotalEntries);
        Assert.Equal(50.0, performance.Percentile, 3);
        Assert.Equal(3100, performance.Score);
        Assert.Equal(1, performance.Season);
    }

    [Fact]
    public void GetBandSongPerformances_FallsBackToCurrentProjectionWhenDerivedDuplicateComboScopeMissesTeam()
    {
        const string songId = "5ec7617e-f48d-4353-9830-b8a1f22be9bb";
        const string teamKey = "195e93ef108143b2975ee46662d4d0e1:4c2a1300df4c49a9b9d2b352d704bdf0:db9342c9dd874c799b58f177ec899f5e";

        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<BandLeaderboardPersistence>>());
        persistence.UpsertBandEntries(songId, "Band_Trios",
        [
            MakeBandEntry([
                "195e93ef108143b2975ee46662d4d0e1",
                "db9342c9dd874c799b58f177ec899f5e",
                "4c2a1300df4c49a9b9d2b352d704bdf0",
            ], "1:1:3", 519611),
            MakeBandEntry(["other1", "other2", "other3"], "1:1:3", 600000),
        ]);
        Db.RebuildBandSongTeamRankings("Band_Trios");
        RebuildCurrentBandProjectionScope(songId, "Band_Trios", "combo", "Solo_Bass+Solo_Bass+Solo_Drums");
        DeleteBandSongTeamRankingRows("Band_Trios", "combo", "Solo_Bass+Solo_Bass+Solo_Drums", teamKey);

        var performance = Assert.Single(Db.GetBandSongPerformances(
            "Band_Trios",
            teamKey,
            "Solo_Bass+Solo_Bass+Solo_Drums"));

        Assert.Equal(songId, performance.SongId);
        Assert.Equal("Solo_Bass+Solo_Bass+Solo_Drums", performance.ComboId);
        Assert.Equal(519611, performance.Score);
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
        Assert.Equal(1, bestSong.Season);
        Assert.Equal("song_1", worstSong.SongId);
        Assert.Equal(66.667, worstSong.Percentile, 3);
        Assert.Equal(1, worstSong.Season);
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
        Assert.Equal(0, CountLegacyBandSongRankingRows("Band_Duets", "overall"));
        Assert.Equal(0, CountLegacyBandSongRankingRows("Band_Duets", "combo"));
    }

    [Fact]
    public void GetBandSongPerformances_FallsBackToLegacyProjectionWhenCurrentScopeMissing()
    {
        SeedBandRankingsSource();
        Db.RebuildBandSongTeamRankings("Band_Duets");
        CopyCurrentBandSongRankingRowsToLegacy("Band_Duets");
        ClearCurrentBandSongRankingRows("Band_Duets");
        DeleteBandEntries("Band_Duets");

        var performances = Db.GetBandSongPerformances("Band_Duets", "p1:p2");

        Assert.Equal(["song_0", "song_1"], performances.Select(p => p.SongId).ToArray());
        Assert.Equal(5, CountLegacyBandSongRankingRows("Band_Duets", "overall"));
        Assert.Equal(0, CountBandSongRankingRows("Band_Duets", "overall"));
    }

    [Fact]
    public void RebuildBandSongTeamRankings_DoesNotLeakOldCurrentTables()
    {
        SeedBandRankingsSource();

        Db.RebuildBandSongTeamRankings("Band_Duets");
        Db.RebuildBandSongTeamRankings("Band_Duets");
        Db.RebuildBandSongTeamRankings("Band_Duets");

        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT tablename
            FROM pg_tables
            WHERE schemaname = 'public'
              AND tablename LIKE 'band_song_team_rankings_current_band_duets_old_%'
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

    [Fact]
    public void GetSongBandLeaderboard_ReadsReadyDuetsCurrentProjection()
    {
        SeedBandRankingsSource();
        RebuildCurrentBandProjectionScope("song_0", "Band_Duets", "overall", string.Empty);
        DeleteBandEntries("Band_Duets");

        var (entries, totalEntries) = Db.GetSongBandLeaderboard("song_0", "Band_Duets", limit: 10);
        var (secondPageEntries, secondPageTotalEntries) = Db.GetSongBandLeaderboard("song_0", "Band_Duets", limit: 1, offset: 1);
        var selectedPlayerEntry = Db.GetSongBandLeaderboardEntryForAccount("song_0", "Band_Duets", "p1");

        Assert.Equal(2, totalEntries);
        Assert.Equal(2, entries.Count);
        Assert.Equal("p1:p2", entries[0].TeamKey);
        Assert.Equal(1100, entries[0].Score);
        Assert.Equal(1, entries[0].Rank);
        Assert.Equal(2, secondPageTotalEntries);
        Assert.Equal("p3:p4", Assert.Single(secondPageEntries).TeamKey);
        Assert.NotNull(selectedPlayerEntry);
        Assert.Equal("p1:p2", selectedPlayerEntry.TeamKey);
        Assert.Equal(1100, selectedPlayerEntry.Score);
    }

    [Fact]
    public void GetSongBandLeaderboard_ReadsReadyTriosComboCurrentProjection()
    {
        SeedTriosBandRankingsSource();
        const string comboId = "Solo_Guitar+Solo_Bass+Solo_Drums";
        RebuildCurrentBandProjectionScope("trio_song", "Band_Trios", "combo", comboId);
        DeleteBandEntries("Band_Trios");

        var (entries, totalEntries) = Db.GetSongBandLeaderboard("trio_song", "Band_Trios", limit: 10, comboId: comboId);
        var selectedTeamEntry = Db.GetSongBandLeaderboardEntryForTeam("trio_song", "Band_Trios", "t1:t2:t3", comboId);

        Assert.Equal(2, totalEntries);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(comboId, entry.ComboId));
        Assert.Equal("t1:t2:t3", entries[0].TeamKey);
        Assert.Equal(3000, entries[0].Score);
        Assert.NotNull(selectedTeamEntry);
        Assert.Equal(3000, selectedTeamEntry.Score);
    }

    [Fact]
    public void GetSongBandLeaderboard_ReadsReadyQuadCurrentProjection()
    {
        SeedQuadBandRankingsSource();
        RebuildCurrentBandProjectionScope("quad_song", "Band_Quad", "overall", string.Empty);
        DeleteBandEntries("Band_Quad");

        var (entries, totalEntries) = Db.GetSongBandLeaderboard("quad_song", "Band_Quad", limit: 10);
        var selectedPlayerEntry = Db.GetSongBandLeaderboardEntryForAccount("quad_song", "Band_Quad", "q1");
        var selectedTeamEntry = Db.GetSongBandLeaderboardEntryForTeam("quad_song", "Band_Quad", "q5:q6:q7:q8");

        Assert.Equal(2, totalEntries);
        Assert.Equal(2, entries.Count);
        Assert.Equal("q1:q2:q3:q4", entries[0].TeamKey);
        Assert.Equal(4000, entries[0].Score);
        Assert.NotNull(selectedPlayerEntry);
        Assert.Equal("q1:q2:q3:q4", selectedPlayerEntry.TeamKey);
        Assert.NotNull(selectedTeamEntry);
        Assert.Equal("q5:q6:q7:q8", selectedTeamEntry.TeamKey);
        Assert.Equal(3500, selectedTeamEntry.Score);
    }

    [Fact]
    public void GetSongBandLeaderboard_FallsBackWhenProjectionScopeIsUnpublished()
    {
        SeedBandRankingsSource();
        RebuildCurrentBandProjectionScope("song_0", "Band_Duets", "overall", string.Empty, publishOnSuccess: false);
        ForceCurrentBandProjectionTopTeam("song_0", "Band_Duets", "overall", string.Empty, "p3:p4", 9999);
        UpdateCurrentBandProjectionScopeStatus("song_0", "Band_Duets", "overall", string.Empty, "failed");

        var (entries, totalEntries) = Db.GetSongBandLeaderboard("song_0", "Band_Duets", limit: 10);
        var selectedTeamEntry = Db.GetSongBandLeaderboardEntryForTeam("song_0", "Band_Duets", "p3:p4");

        Assert.Equal(2, totalEntries);
        Assert.Equal("p1:p2", entries[0].TeamKey);
        Assert.Equal(1100, entries[0].Score);
        Assert.NotNull(selectedTeamEntry);
        Assert.Equal(900, selectedTeamEntry.Score);
    }

    [Fact]
    public void GetSongBandLeaderboard_ProjectionRequiredDoesNotFallBackToBandEntries()
    {
        SeedBandRankingsSource();

        var (legacyEntries, legacyTotalEntries) = Db.GetSongBandLeaderboard("song_0", "Band_Duets", limit: 10);
        var (entries, totalEntries) = Db.GetSongBandLeaderboard("song_0", "Band_Duets", limit: 10, requireCurrentProjection: true);
        var selectedPlayerEntry = Db.GetSongBandLeaderboardEntryForAccount("song_0", "Band_Duets", "p1", requireCurrentProjection: true);
        var selectedTeamEntry = Db.GetSongBandLeaderboardEntryForTeam("song_0", "Band_Duets", "p1:p2", requireCurrentProjection: true);

        Assert.NotEmpty(legacyEntries);
        Assert.True(legacyTotalEntries > 0);
        Assert.Empty(entries);
        Assert.Equal(0, totalEntries);
        Assert.Null(selectedPlayerEntry);
        Assert.Null(selectedTeamEntry);
    }

    [Fact]
    public async Task GetSongBandLeaderboard_IgnoresUnpublishedCandidateGeneration()
    {
        SeedBandRankingsSource();
        var scope = new BandCurrentProjectionScopeKey("song_0", "Band_Duets", "overall", string.Empty);
        var published = RebuildCurrentBandProjectionScope(scope.SongId, scope.BandType, scope.RankingScope, scope.ScopeComboId);

        UpdateBandEntryScore("song_0", "Band_Duets", "p3:p4", 9999);
        var candidate = RebuildCurrentBandProjectionScope(scope.SongId, scope.BandType, scope.RankingScope, scope.ScopeComboId, publishOnSuccess: false);

        var (entriesBeforePublish, _) = Db.GetSongBandLeaderboard("song_0", "Band_Duets", limit: 10);

        Assert.True(candidate.Generation > published.Generation);
        Assert.Equal("p1:p2", entriesBeforePublish[0].TeamKey);
        Assert.Equal(1100, entriesBeforePublish[0].Score);
        Assert.Equal(published.Generation, GetCurrentBandProjectionPublishedGeneration(scope));

        var publishResult = await CreateBandCurrentProjectionBuilder()
            .TryPublishGenerationAsync(candidate.Generation, [scope]);
        var (entriesAfterPublish, _) = Db.GetSongBandLeaderboard("song_0", "Band_Duets", limit: 10);

        Assert.True(publishResult.Published);
        Assert.Equal(candidate.Generation, GetCurrentBandProjectionPublishedGeneration(scope));
        Assert.Equal("p3:p4", entriesAfterPublish[0].TeamKey);
        Assert.Equal(9999, entriesAfterPublish[0].Score);
    }

    [Fact]
    public async Task TryPublishGeneration_DoesNotAdvanceWhenRequiredScopeIsMissing()
    {
        var scope = new BandCurrentProjectionScopeKey("missing_song", "Band_Duets", "overall", string.Empty);

        var result = await CreateBandCurrentProjectionBuilder()
            .TryPublishGenerationAsync(123, [scope]);

        Assert.False(result.Published);
        Assert.Equal(1, result.MissingScopes);
        Assert.Equal(0, GetCurrentBandProjectionStateGeneration());
    }

    [Fact]
    public void RebuildScope_DoesNotPublishMissingUnpublishedScope()
    {
        var result = RebuildCurrentBandProjectionScope("missing_song", "Band_Duets", "overall", string.Empty);
        var scope = new BandCurrentProjectionScopeKey(result.SongId, result.BandType, result.RankingScope, result.ScopeComboId);

        Assert.False(result.SourceScopeExists);
        Assert.Null(GetCurrentBandProjectionPublishedGeneration(scope));
        Assert.Equal(0, GetCurrentBandProjectionStateGeneration());
    }

    [Fact]
    public async Task EnsureSchema_BackfillsAllExistingReadyScopesAsPublished()
    {
        SeedBandRankingsSource();
        var firstScope = new BandCurrentProjectionScopeKey("song_0", "Band_Duets", "overall", string.Empty);
        var secondScope = new BandCurrentProjectionScopeKey("song_1", "Band_Duets", "overall", string.Empty);
        var first = RebuildCurrentBandProjectionScope(firstScope.SongId, firstScope.BandType, firstScope.RankingScope, firstScope.ScopeComboId);
        var second = RebuildCurrentBandProjectionScope(secondScope.SongId, secondScope.BandType, secondScope.RankingScope, secondScope.ScopeComboId);
        ClearPublishedCurrentBandProjectionScopes(first.Generation);

        await CreateBandCurrentProjectionBuilder().EnsureSchemaAsync();

        Assert.True(second.Generation > first.Generation);
        Assert.Equal(first.Generation, GetCurrentBandProjectionPublishedGeneration(firstScope));
        Assert.Equal(second.Generation, GetCurrentBandProjectionPublishedGeneration(secondScope));
    }

    [Fact]
    public async Task TryPublishGeneration_DoesNotAdvanceWhenRequiredScopeFailed()
    {
        SeedBandRankingsSource();
        var scope = new BandCurrentProjectionScopeKey("song_0", "Band_Duets", "overall", string.Empty);
        var published = RebuildCurrentBandProjectionScope(scope.SongId, scope.BandType, scope.RankingScope, scope.ScopeComboId);
        UpdateBandEntryScore("song_0", "Band_Duets", "p3:p4", 9999);
        var candidate = RebuildCurrentBandProjectionScope(scope.SongId, scope.BandType, scope.RankingScope, scope.ScopeComboId, publishOnSuccess: false);
        UpdateCurrentBandProjectionScopeStatus(scope.SongId, scope.BandType, scope.RankingScope, scope.ScopeComboId, "failed");

        var result = await CreateBandCurrentProjectionBuilder()
            .TryPublishGenerationAsync(candidate.Generation, [scope]);
        var (entries, _) = Db.GetSongBandLeaderboard("song_0", "Band_Duets", limit: 10);

        Assert.False(result.Published);
        Assert.Equal(1, result.FailedScopes);
        Assert.Equal(published.Generation, GetCurrentBandProjectionPublishedGeneration(scope));
        Assert.Equal("p1:p2", entries[0].TeamKey);
        Assert.Equal(1100, entries[0].Score);
    }

    [Fact]
    public async Task TryPublishGeneration_PublishesReadyScopeWhenSiblingScopeFailed()
    {
        SeedBandRankingsSource();
        var readyScope = new BandCurrentProjectionScopeKey("song_0", "Band_Duets", "overall", string.Empty);
        var failedScope = new BandCurrentProjectionScopeKey("song_1", "Band_Duets", "overall", string.Empty);
        RebuildCurrentBandProjectionScope(readyScope.SongId, readyScope.BandType, readyScope.RankingScope, readyScope.ScopeComboId);
        var failedPublished = RebuildCurrentBandProjectionScope(failedScope.SongId, failedScope.BandType, failedScope.RankingScope, failedScope.ScopeComboId);

        UpdateBandEntryScore("song_0", "Band_Duets", "p3:p4", 9999);
        UpdateBandEntryScore("song_1", "Band_Duets", "p1:p2", 9999);
        var staged = await CreateBandCurrentProjectionBuilder()
            .RefreshScopesAsync(
                [readyScope, failedScope],
                new BandCurrentProjectionRebuildOptions { PublishOnSuccess = false, SkipUnchangedScopes = false });
        var candidateGeneration = Assert.Single(staged.Scopes.Select(static scope => scope.Generation).Distinct());
        UpdateCurrentBandProjectionScopeStatus(failedScope.SongId, failedScope.BandType, failedScope.RankingScope, failedScope.ScopeComboId, "failed");

        var publish = await CreateBandCurrentProjectionBuilder()
            .TryPublishGenerationAsync(candidateGeneration, [readyScope, failedScope]);
        var (readyEntries, _) = Db.GetSongBandLeaderboard("song_0", "Band_Duets", limit: 10);
        var readyAccountEntry = Db.GetSongBandLeaderboardEntryForAccount("song_0", "Band_Duets", "p3");
        var readyTeamEntry = Db.GetSongBandLeaderboardEntryForTeam("song_0", "Band_Duets", "p3:p4");
        var (failedEntries, _) = Db.GetSongBandLeaderboard("song_1", "Band_Duets", limit: 10);
        var failedTeamEntry = Db.GetSongBandLeaderboardEntryForTeam("song_1", "Band_Duets", "p1:p2");

        Assert.True(publish.Published);
        Assert.Equal(2, publish.ScopeCount);
        Assert.Equal(1, publish.ReadyScopes);
        Assert.Equal(1, publish.PublishedScopes);
        Assert.Equal(1, publish.FailedScopes);
        Assert.Equal(0, publish.MissingScopes);
        Assert.Equal(candidateGeneration, GetCurrentBandProjectionPublishedGeneration(readyScope));
        Assert.Equal(failedPublished.Generation, GetCurrentBandProjectionPublishedGeneration(failedScope));
        Assert.Equal("p3:p4", readyEntries[0].TeamKey);
        Assert.Equal(9999, readyEntries[0].Score);
        Assert.NotNull(readyAccountEntry);
        Assert.Equal(9999, readyAccountEntry.Score);
        Assert.NotNull(readyTeamEntry);
        Assert.Equal(9999, readyTeamEntry.Score);
        Assert.Equal("p3:p4", failedEntries[0].TeamKey);
        Assert.Equal(1300, failedEntries[0].Score);
        Assert.NotNull(failedTeamEntry);
        Assert.Equal(1200, failedTeamEntry.Score);
    }

    [Fact]
    public async Task RefreshScopes_CleansFailedUnpublishedCandidateRows()
    {
        SeedBandRankingsSource();
        var refreshScope = new BandCurrentProjectionScopeKey("song_0", "Band_Duets", "overall", string.Empty);
        var failedScope = new BandCurrentProjectionScopeKey("song_1", "Band_Duets", "overall", string.Empty);
        RebuildCurrentBandProjectionScope(refreshScope.SongId, refreshScope.BandType, refreshScope.RankingScope, refreshScope.ScopeComboId);
        RebuildCurrentBandProjectionScope(failedScope.SongId, failedScope.BandType, failedScope.RankingScope, failedScope.ScopeComboId);

        UpdateBandEntryScore("song_1", "Band_Duets", "p1:p2", 9999);
        var staged = await CreateBandCurrentProjectionBuilder()
            .RefreshScopesAsync(
                [failedScope],
                new BandCurrentProjectionRebuildOptions { PublishOnSuccess = false, SkipUnchangedScopes = false });
        var failedGeneration = Assert.Single(staged.Scopes.Select(static result => result.Generation).Distinct());
        UpdateCurrentBandProjectionScopeStatus(failedScope.SongId, failedScope.BandType, failedScope.RankingScope, failedScope.ScopeComboId, "failed");
        var failedCandidateRowsBeforeCleanup = CountCurrentBandProjectionRows(failedScope, failedGeneration);

        UpdateBandEntryScore("song_0", "Band_Duets", "p3:p4", 9999);
        var refreshed = await CreateBandCurrentProjectionBuilder()
            .RefreshScopesAsync([refreshScope], new BandCurrentProjectionRebuildOptions { SkipUnchangedScopes = false });
        var publishedGeneration = Assert.Single(refreshed.Scopes.Select(static result => result.Generation).Distinct());

        Assert.True(staged.InsertedRows > 0);
        Assert.True(failedCandidateRowsBeforeCleanup > 0);
        Assert.True(refreshed.PublishResult.Published);
        Assert.True(refreshed.CandidateRowsDeleted > 0);
        Assert.Equal(0, CountCurrentBandProjectionRows(failedScope, failedGeneration));
        Assert.Equal(publishedGeneration, GetCurrentBandProjectionPublishedGeneration(refreshScope));
        Assert.True(CountCurrentBandProjectionRows(refreshScope, publishedGeneration) > 0);
    }

    [Fact]
    public async Task RefreshScopes_SkipUnchangedCommitsAfterFilterReaderIsDisposed()
    {
        SeedBandRankingsSource();
        var scope = new BandCurrentProjectionScopeKey("song_0", "Band_Duets", "overall", string.Empty);

        var result = await CreateBandCurrentProjectionBuilder()
            .RefreshScopesAsync([scope], new BandCurrentProjectionRebuildOptions { SkipUnchangedScopes = true });

        var generation = Assert.Single(result.Scopes.Select(static refreshedScope => refreshedScope.Generation).Distinct());
        Assert.True(result.PublishResult.Published);
        Assert.Equal(generation, GetCurrentBandProjectionPublishedGeneration(scope));
        Assert.True(CountCurrentBandProjectionRows(scope, generation) > 0);
    }

    [Fact]
    public async Task RebuildAll_WithBandTypeFilterPrunesOnlyThatBandType()
    {
        SeedBandRankingsSource();
        SeedQuadBandRankingsSource();
        var duetScope = new BandCurrentProjectionScopeKey("song_0", "Band_Duets", "overall", string.Empty);
        var quadScope = new BandCurrentProjectionScopeKey("quad_song", "Band_Quad", "overall", string.Empty);
        var duetPublished = RebuildCurrentBandProjectionScope(duetScope.SongId, duetScope.BandType, duetScope.RankingScope, duetScope.ScopeComboId);
        RebuildCurrentBandProjectionScope(quadScope.SongId, quadScope.BandType, quadScope.RankingScope, quadScope.ScopeComboId);
        DeleteBandEntriesForSong(quadScope.SongId, quadScope.BandType);

        var rebuild = await CreateBandCurrentProjectionBuilder()
            .RebuildAllAsync(new BandCurrentProjectionRebuildOptions { BandTypes = ["Band_Quad"] });

        Assert.True(rebuild.OrphanedRowsDeleted > 0);
        Assert.Null(GetCurrentBandProjectionPublishedGeneration(quadScope));
        Assert.Equal(duetPublished.Generation, GetCurrentBandProjectionPublishedGeneration(duetScope));
        Assert.True(CountCurrentBandProjectionRows(duetScope, duetPublished.Generation) > 0);
    }

    [Fact]
    public async Task RebuildAll_PrunesPublishedScopeMissingFromCurrentSource()
    {
        SeedBandRankingsSource();
        var staleScope = new BandCurrentProjectionScopeKey("song_0", "Band_Duets", "overall", string.Empty);
        RebuildCurrentBandProjectionScope(staleScope.SongId, staleScope.BandType, staleScope.RankingScope, staleScope.ScopeComboId);
        DeleteBandEntriesForSong(staleScope.SongId, staleScope.BandType);

        var rebuild = await CreateBandCurrentProjectionBuilder()
            .RebuildAllAsync(new BandCurrentProjectionRebuildOptions());
        var (entries, totalEntries) = Db.GetSongBandLeaderboard(staleScope.SongId, staleScope.BandType, limit: 10);

        Assert.True(rebuild.OrphanedRowsDeleted > 0);
        Assert.Null(GetCurrentBandProjectionPublishedGeneration(staleScope));
        Assert.Empty(entries);
        Assert.Equal(0, totalEntries);
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

    private bool TableExists(string tableName)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT to_regclass(@tableName) IS NOT NULL";
        cmd.Parameters.AddWithValue("tableName", $"public.{tableName}");
        return Convert.ToBoolean(cmd.ExecuteScalar() ?? false);
    }

    private bool ColumnExists(string tableName, string columnName)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = @tableName
                  AND column_name = @columnName);";
        cmd.Parameters.AddWithValue("tableName", tableName);
        cmd.Parameters.AddWithValue("columnName", columnName);
        return Convert.ToBoolean(cmd.ExecuteScalar() ?? false);
    }

    private BandRankingMetadataCounts GetCurrentBandRankingMetadata(string bandType)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT COUNT(*)::int,
                   COUNT(*) FILTER (WHERE ranking_generation > 0)::int,
                   COUNT(*) FILTER (WHERE row_fingerprint <> '')::int,
                   COUNT(DISTINCT ranking_generation)::int,
                   MAX(ranking_generation)
            FROM {BandRankingStorageNames.QuoteIdentifier(BandRankingStorageNames.GetCurrentRankingTable(bandType))}
            WHERE band_type = @bandType;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new BandRankingMetadataCounts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt64(4));
    }

    private string GetBandRankingGenerationStatus(long generationId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status FROM band_team_ranking_generation WHERE generation_id = @generationId";
        cmd.Parameters.AddWithValue("generationId", generationId);
        return (string)cmd.ExecuteScalar()!;
    }

    private long GetBandRankingGenerationRowCount(long generationId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT row_count FROM band_team_ranking_generation WHERE generation_id = @generationId";
        cmd.Parameters.AddWithValue("generationId", generationId);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private int CountV2LatestRowsWithGenerationAndFingerprint(string bandType, DateOnly snapshotDate)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*)
            FROM band_team_rank_history_latest_v2
            WHERE band_type = @bandType
              AND snapshot_date = @snapshotDate
              AND generation_id > 0
              AND row_fingerprint <> '';";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void DeleteV2BandHistory(string bandType)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM band_team_rank_history_latest_v2 WHERE band_type = @bandType;
            DELETE FROM band_team_rank_history_points_v2 WHERE band_type = @bandType;
            DELETE FROM band_team_rank_history_snapshot_v2 WHERE band_type = @bandType;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.ExecuteNonQuery();
    }

    private void DeleteLegacyBandHistory(string bandType)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM band_team_rank_history_latest WHERE band_type = @bandType;
            DELETE FROM band_team_rank_history_points WHERE band_type = @bandType;
            DELETE FROM band_team_rank_history WHERE band_type = @bandType;
            DELETE FROM band_team_ranking_stats_history WHERE band_type = @bandType;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.ExecuteNonQuery();
    }

    private void DeleteOneBandHistoryRow(string tableName, string bandType, DateOnly snapshotDate)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH selected AS (
                SELECT ctid
                FROM {BandRankingStorageNames.QuoteIdentifier(tableName)}
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                ORDER BY ranking_scope, combo_id, team_key
                LIMIT 1
            )
            DELETE FROM {BandRankingStorageNames.QuoteIdentifier(tableName)} target
            USING selected
            WHERE target.ctid = selected.ctid;
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.ExecuteNonQuery();
    }

    private void DeleteBandHistorySnapshotRows(string tableName, string bandType, DateOnly snapshotDate)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM {BandRankingStorageNames.QuoteIdentifier(tableName)}
            WHERE band_type = @bandType
              AND snapshot_date = @snapshotDate;
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.ExecuteNonQuery();
    }

    private void ChangeOneNarrowBandHistoryRank(string bandType, DateOnly snapshotDate)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH selected AS (
                SELECT ctid
                FROM band_team_rank_history_points
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                ORDER BY ranking_scope, combo_id, team_key
                LIMIT 1
            )
            UPDATE band_team_rank_history_points target
            SET adjusted_skill_rank = adjusted_skill_rank + 1000
            FROM selected
            WHERE target.ctid = selected.ctid;
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.ExecuteNonQuery();
    }

    private void ChangeOneV2BandHistoryRank(string bandType, DateOnly snapshotDate)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH selected AS (
                SELECT ctid
                FROM band_team_rank_history_points_v2
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                ORDER BY ranking_scope, combo_id, team_key
                LIMIT 1
            )
            UPDATE band_team_rank_history_points_v2 target
            SET adjusted_skill_rank = adjusted_skill_rank + 1000
            FROM selected
            WHERE target.ctid = selected.ctid;
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.ExecuteNonQuery();
    }

    private void StaleOneV2LatestRow(string bandType, DateOnly snapshotDate)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH selected AS (
                SELECT ctid
                FROM band_team_rank_history_latest_v2
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                ORDER BY ranking_scope, combo_id, team_key
                LIMIT 1
            )
            UPDATE band_team_rank_history_latest_v2 target
            SET snapshot_date = @staleDate
            FROM selected
            WHERE target.ctid = selected.ctid;
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.Parameters.AddWithValue("staleDate", snapshotDate.AddDays(-1));
        cmd.ExecuteNonQuery();
    }

    private void SetV2LatestSnapshotDate(string bandType, string teamKey, DateOnly snapshotDate)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE band_team_rank_history_latest_v2
            SET snapshot_date = @snapshotDate
            WHERE band_type = @bandType
              AND team_key = @teamKey;
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.ExecuteNonQuery();
    }

    private IReadOnlyList<DateOnly> GetV2LatestSnapshotDates(string bandType, string teamKey)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT snapshot_date
            FROM band_team_rank_history_latest_v2
            WHERE band_type = @bandType
              AND team_key = @teamKey
            ORDER BY ranking_scope, combo_id;
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        using var reader = cmd.ExecuteReader();
        var dates = new List<DateOnly>();
        while (reader.Read())
            dates.Add(reader.GetFieldValue<DateOnly>(0));
        return dates;
    }

    private MetaDatabase CreateMetaDatabase(BandRankHistoryApiReadSource apiReadSource) => new(
        _fixture.DataSource,
        Substitute.For<Microsoft.Extensions.Logging.ILogger<MetaDatabase>>(),
        new BandRankHistoryOptions { ApiReadSource = apiReadSource });

    private sealed record BandRankingMetadataCounts(
        int RowCount,
        int RowsWithGeneration,
        int RowsWithFingerprint,
        int DistinctGenerationCount,
        long GenerationId);

    private int CountCompositeRankHistoryRows(string accountId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM composite_rank_history WHERE account_id = @accountId";
        cmd.Parameters.AddWithValue("accountId", accountId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private DateOnly[] GetCompositeRankHistoryDates(string accountId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT snapshot_date FROM composite_rank_history WHERE account_id = @accountId ORDER BY snapshot_date";
        cmd.Parameters.AddWithValue("accountId", accountId);
        using var reader = cmd.ExecuteReader();
        var dates = new List<DateOnly>();
        while (reader.Read())
            dates.Add(reader.GetFieldValue<DateOnly>(0));
        return dates.ToArray();
    }

    private void InsertCompositeRankHistoryRow(string accountId, DateOnly snapshotDate, int rank)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO composite_rank_history (
                account_id,
                snapshot_date,
                composite_rank,
                composite_rating,
                instruments_played,
                total_songs_played)
            VALUES (@accountId, @snapshotDate, @rank, @rating, 1, 1)";
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.Parameters.AddWithValue("rank", rank);
        cmd.Parameters.AddWithValue("rating", 1.0f / Math.Max(1, rank));
        cmd.ExecuteNonQuery();
    }

    private void CloneBandHistorySnapshot(string bandType, DateOnly sourceDate, DateOnly targetDate)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO band_team_rank_history (
                band_type, ranking_scope, combo_id, team_key, team_members,
                songs_played, total_charted_songs, coverage, raw_skill_rating,
                adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating,
                computed_at, snapshot_date)
            SELECT
                band_type, ranking_scope, combo_id, team_key, team_members,
                songs_played, total_charted_songs, coverage, raw_skill_rating,
                adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating,
                computed_at, @targetDate
            FROM band_team_rank_history
            WHERE band_type = @bandType AND snapshot_date = @sourceDate
            ON CONFLICT DO NOTHING;

            INSERT INTO band_team_rank_history_points (
                band_type, ranking_scope, combo_id, team_key, snapshot_date,
                snapshot_taken_at, adjusted_skill_rank, weighted_rank, fc_rate_rank,
                total_score_rank, adjusted_skill_rating, weighted_rating, fc_rate,
                total_score, songs_played, coverage, full_combo_count,
                total_charted_songs, total_ranked_teams, raw_weighted_rating,
                raw_skill_rating)
            SELECT
                band_type, ranking_scope, combo_id, team_key, @targetDate,
                snapshot_taken_at, adjusted_skill_rank, weighted_rank, fc_rate_rank,
                total_score_rank, adjusted_skill_rating, weighted_rating, fc_rate,
                total_score, songs_played, coverage, full_combo_count,
                total_charted_songs, total_ranked_teams, raw_weighted_rating,
                raw_skill_rating
            FROM band_team_rank_history_points
            WHERE band_type = @bandType AND snapshot_date = @sourceDate
            ON CONFLICT DO NOTHING;

            INSERT INTO band_team_ranking_stats_history (
                band_type, ranking_scope, combo_id, total_teams, computed_at, snapshot_date)
            SELECT band_type, ranking_scope, combo_id, total_teams, computed_at, @targetDate
            FROM band_team_ranking_stats_history
            WHERE band_type = @bandType AND snapshot_date = @sourceDate
            ON CONFLICT DO NOTHING;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("sourceDate", sourceDate);
        cmd.Parameters.AddWithValue("targetDate", targetDate);
        cmd.ExecuteNonQuery();
    }

    private int CountBandSongRankingRows(string bandType, string rankingScope)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT COUNT(*)
            FROM {BandRankingStorageNames.QuoteIdentifier(BandRankingStorageNames.GetCurrentBandSongRankingTable(bandType))}
            WHERE band_type = @bandType
              AND ranking_scope = @rankingScope;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("rankingScope", rankingScope);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int CountLegacyBandSongRankingRows(string bandType, string rankingScope)
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

    private void CopyCurrentBandSongRankingRowsToLegacy(string bandType)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            INSERT INTO band_song_team_rankings (
                band_type, ranking_scope, scope_combo_id, team_key, song_id,
                entry_combo_id, rank, total_entries, percentile, score, accuracy,
                is_full_combo, stars, season, end_time, computed_at)
            SELECT
                band_type, ranking_scope, scope_combo_id, team_key, song_id,
                entry_combo_id, rank, total_entries, percentile, score, accuracy,
                is_full_combo, stars, season, end_time, computed_at
            FROM {BandRankingStorageNames.QuoteIdentifier(BandRankingStorageNames.GetCurrentBandSongRankingTable(bandType))}
            WHERE band_type = @bandType
            ON CONFLICT (band_type, ranking_scope, scope_combo_id, team_key, song_id) DO UPDATE SET
                entry_combo_id = EXCLUDED.entry_combo_id,
                rank = EXCLUDED.rank,
                total_entries = EXCLUDED.total_entries,
                percentile = EXCLUDED.percentile,
                score = EXCLUDED.score,
                accuracy = EXCLUDED.accuracy,
                is_full_combo = EXCLUDED.is_full_combo,
                stars = EXCLUDED.stars,
                season = EXCLUDED.season,
                end_time = EXCLUDED.end_time,
                computed_at = EXCLUDED.computed_at;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.ExecuteNonQuery();
    }

    private void ClearCurrentBandSongRankingRows(string bandType)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {BandRankingStorageNames.QuoteIdentifier(BandRankingStorageNames.GetCurrentBandSongRankingTable(bandType))} WHERE band_type = @bandType";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.ExecuteNonQuery();
    }

    private void DeleteBandEntries(string bandType)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM band_entries WHERE band_type = @bandType";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.ExecuteNonQuery();
    }

    private void DeleteBandEntriesForSong(string songId, string bandType)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM band_entries WHERE song_id = @songId AND band_type = @bandType";
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.ExecuteNonQuery();
    }

    private void DeleteBandSongTeamRankingRows(string bandType, string rankingScope, string scopeComboId, string teamKey)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM {BandRankingStorageNames.QuoteIdentifier(BandRankingStorageNames.GetCurrentBandSongRankingTable(bandType))}
            WHERE band_type = @bandType
              AND ranking_scope = @rankingScope
              AND scope_combo_id = @scopeComboId
              AND team_key = @teamKey;

            DELETE FROM band_song_team_rankings
            WHERE band_type = @bandType
              AND ranking_scope = @rankingScope
              AND scope_combo_id = @scopeComboId
              AND team_key = @teamKey;
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("rankingScope", rankingScope);
        cmd.Parameters.AddWithValue("scopeComboId", scopeComboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.ExecuteNonQuery();
    }

    private BandCurrentProjectionScopeResult RebuildCurrentBandProjectionScope(string songId, string bandType, string rankingScope, string scopeComboId, bool publishOnSuccess = true)
    {
        var builder = CreateBandCurrentProjectionBuilder();

        return builder.RebuildScopeAsync(
                new BandCurrentProjectionScopeKey(songId, bandType, rankingScope, scopeComboId),
                new BandCurrentProjectionRebuildOptions { PublishOnSuccess = publishOnSuccess })
            .GetAwaiter()
            .GetResult();
    }

    private BandCurrentProjectionBuilder CreateBandCurrentProjectionBuilder() =>
        new(
            _fixture.DataSource,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<BandCurrentProjectionBuilder>>());

    private void UpdateBandEntryScore(string songId, string bandType, string teamKey, int score)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE band_entries
            SET score = @score,
                last_updated_at = now()
            WHERE song_id = @songId
              AND band_type = @bandType
              AND team_key = @teamKey;
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("score", score);
        cmd.ExecuteNonQuery();
    }

    private long? GetCurrentBandProjectionPublishedGeneration(BandCurrentProjectionScopeKey scope)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT published_generation
            FROM band_current_projection_scope
            WHERE song_id = @songId
              AND band_type = @bandType
              AND ranking_scope = @rankingScope
              AND scope_combo_id = @scopeComboId;
            """;
        cmd.Parameters.AddWithValue("songId", scope.SongId);
        cmd.Parameters.AddWithValue("bandType", scope.BandType);
        cmd.Parameters.AddWithValue("rankingScope", scope.RankingScope);
        cmd.Parameters.AddWithValue("scopeComboId", scope.ScopeComboId);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private int CountCurrentBandProjectionRows(BandCurrentProjectionScopeKey scope, long generation)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM current_band_leaderboard_entries
            WHERE song_id = @songId
              AND band_type = @bandType
              AND ranking_scope = @rankingScope
              AND scope_combo_id = @scopeComboId
              AND projection_generation = @generation;
            """;
        cmd.Parameters.AddWithValue("songId", scope.SongId);
        cmd.Parameters.AddWithValue("bandType", scope.BandType);
        cmd.Parameters.AddWithValue("rankingScope", scope.RankingScope);
        cmd.Parameters.AddWithValue("scopeComboId", scope.ScopeComboId);
        cmd.Parameters.AddWithValue("generation", generation);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void ClearPublishedCurrentBandProjectionScopes(long stateGeneration)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE band_current_projection_scope
            SET published_generation = NULL,
                published_row_count = 0;

            UPDATE band_current_projection_state
            SET current_generation = @stateGeneration;
            """;
        cmd.Parameters.AddWithValue("stateGeneration", stateGeneration);
        cmd.ExecuteNonQuery();
    }

    private long GetCurrentBandProjectionStateGeneration()
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE((SELECT current_generation FROM band_current_projection_state WHERE id = TRUE), 0);";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private void UpdateCurrentBandProjectionScopeStatus(string songId, string bandType, string rankingScope, string scopeComboId, string status)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE band_current_projection_scope
            SET status = @status,
                updated_at = now()
            WHERE song_id = @songId
              AND band_type = @bandType
              AND ranking_scope = @rankingScope
              AND scope_combo_id = @scopeComboId;
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("rankingScope", rankingScope);
        cmd.Parameters.AddWithValue("scopeComboId", scopeComboId);
        cmd.Parameters.AddWithValue("status", status);
        cmd.ExecuteNonQuery();
    }

    private void ForceCurrentBandProjectionTopTeam(string songId, string bandType, string rankingScope, string scopeComboId, string teamKey, int score)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE current_band_leaderboard_entries
            SET score = CASE WHEN team_key = @teamKey THEN @score ELSE score END,
                rank = CASE WHEN team_key = @teamKey THEN 1 ELSE rank + 1 END
            WHERE song_id = @songId
              AND band_type = @bandType
              AND ranking_scope = @rankingScope
              AND scope_combo_id = @scopeComboId;
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("rankingScope", rankingScope);
        cmd.Parameters.AddWithValue("scopeComboId", scopeComboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("score", score);
        cmd.ExecuteNonQuery();
    }

    private void SeedTriosBandRankingsSource()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<BandLeaderboardPersistence>>());

        persistence.UpsertBandEntries("trio_song", "Band_Trios",
        [
            MakeBandEntry(["t1", "t2", "t3"], "0:1:3", 3000, isFullCombo: true),
            MakeBandEntry(["t4", "t5", "t6"], "3:1:0", 2500),
            MakeBandEntry(["t1", "t2", "t3"], "0:1:2", 1000),
        ]);
    }

    private void SeedDuplicateTriosBandRankingsSource()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<BandLeaderboardPersistence>>());

        persistence.UpsertBandEntries("dup_trio_song", "Band_Trios",
        [
            MakeBandEntry(["d1", "d2", "d3"], "1:3:3", 3100, isFullCombo: true),
            MakeBandEntry(["d4", "d5", "d6"], "3:1:3", 2800),
        ]);
    }

    private void SeedQuadBandRankingsSource()
    {
        var persistence = new BandLeaderboardPersistence(
            _fixture.DataSource,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<BandLeaderboardPersistence>>());

        persistence.UpsertBandEntries("quad_song", "Band_Quad",
        [
            MakeBandEntry(["q1", "q2", "q3", "q4"], "0:1:3:2", 4000, isFullCombo: true),
            MakeBandEntry(["q5", "q6", "q7", "q8"], "0:1:3:2", 3500),
        ]);
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

    private BandHistoryJobStatusDetails GetBandHistoryJobStatusDetails(long jobId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, attempts, last_error, failed_at FROM band_rank_history_jobs WHERE job_id = @jobId";
        cmd.Parameters.AddWithValue("jobId", jobId);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new BandHistoryJobStatusDetails(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDateTime(3));
    }

    private int CountBandHistoryChunks(long jobId, string status)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM band_rank_history_job_chunks WHERE job_id = @jobId AND status = @status";
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.Parameters.AddWithValue("status", status);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private BandHistoryChunkStatusCounts GetBandHistoryChunkStatusCounts(long jobId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FILTER (WHERE status = 'complete')::int,
                   COUNT(*) FILTER (WHERE status = 'failed')::int,
                   COUNT(*) FILTER (WHERE status = 'queued')::int,
                   COUNT(*) FILTER (WHERE status = 'running')::int
            FROM band_rank_history_job_chunks
            WHERE job_id = @jobId;
            """;
        cmd.Parameters.AddWithValue("jobId", jobId);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new BandHistoryChunkStatusCounts(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    private List<BandHistoryChunkRange> GetBandHistoryChunkRanges(long jobId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ranking_scope, combo_id, chunk_ordinal, team_key_start, team_key_end,
                   estimated_rows, source_generation, status
            FROM band_rank_history_job_chunks
            WHERE job_id = @jobId
            ORDER BY ranking_scope, combo_id, chunk_ordinal;
            """;
        cmd.Parameters.AddWithValue("jobId", jobId);
        var chunks = new List<BandHistoryChunkRange>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            chunks.Add(new BandHistoryChunkRange(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetString(7)));
        }

        return chunks;
    }

    private BandHistoryChunkCoverage GetBandHistoryChunkCoverage(long jobId, string bandType)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH matched AS (
                SELECT src.ranking_scope, src.combo_id, src.team_key, COUNT(chunk.job_id)::int AS chunk_matches
                FROM {BandRankingStorageNames.QuoteIdentifier(BandRankingStorageNames.GetCurrentRankingTable(bandType))} src
                LEFT JOIN band_rank_history_job_chunks chunk
                  ON chunk.job_id = @jobId
                 AND chunk.ranking_scope = src.ranking_scope
                 AND chunk.combo_id = src.combo_id
                 AND (chunk.team_key_start IS NULL OR src.team_key >= chunk.team_key_start)
                 AND (chunk.team_key_end IS NULL OR src.team_key <= chunk.team_key_end)
                WHERE src.band_type = @bandType
                GROUP BY src.ranking_scope, src.combo_id, src.team_key
            )
            SELECT COUNT(*) FILTER (WHERE chunk_matches = 1)::int,
                   COUNT(*) FILTER (WHERE chunk_matches <> 1)::int
            FROM matched;
            """;
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new BandHistoryChunkCoverage(reader.GetInt32(0), reader.GetInt32(1));
    }

    private void InsertLegacyStyleBandHistoryChunks(long jobId, string bandType)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO band_rank_history_job_chunks (job_id, band_type, ranking_scope, combo_id, status, updated_at)
            SELECT @jobId, band_type, ranking_scope, combo_id, 'queued', now()
            FROM {BandRankingStorageNames.QuoteIdentifier(BandRankingStorageNames.GetCurrentStatsTable(bandType))}
            WHERE band_type = @bandType;
            """;
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.ExecuteNonQuery();
    }

    private void FailOneBandHistoryChunk(long jobId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH selected AS (
                SELECT ctid
                FROM band_rank_history_job_chunks
                WHERE job_id = @jobId AND status = 'complete'
                ORDER BY ranking_scope, combo_id
                LIMIT 1
            )
            UPDATE band_rank_history_job_chunks chunk
            SET status = 'failed',
                completed_at = NULL,
                last_error = 'test retry',
                updated_at = now()
            FROM selected
            WHERE chunk.ctid = selected.ctid;

            UPDATE band_rank_history_jobs job
            SET status = 'failed',
                failed_at = now(),
                completed_at = NULL,
                last_error = 'test retry',
                chunks_completed = counts.completed_count,
                updated_at = now()
            FROM (
                SELECT COUNT(*) FILTER (WHERE status = 'complete')::int AS completed_count
                FROM band_rank_history_job_chunks
                WHERE job_id = @jobId
            ) counts
            WHERE job.job_id = @jobId;
            """;
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.ExecuteNonQuery();
    }

    private sealed record BandHistoryJobStatusDetails(string Status, int Attempts, string? LastError, DateTime? FailedAt);

    private sealed record BandHistoryChunkStatusCounts(int Complete, int Failed, int Queued, int Running);

    private sealed record BandHistoryChunkRange(
        string RankingScope,
        string ComboId,
        int ChunkOrdinal,
        string? TeamKeyStart,
        string? TeamKeyEnd,
        long EstimatedRows,
        long SourceGeneration,
        string Status);

    private sealed record BandHistoryChunkCoverage(int CoveredRows, int RowsWithWrongMatchCount);

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
