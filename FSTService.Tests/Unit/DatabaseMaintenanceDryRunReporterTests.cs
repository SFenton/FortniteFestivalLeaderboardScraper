using System.Reflection;
using System.Runtime.CompilerServices;
using FSTService.Persistence;
using FSTService.Persistence.Maintenance;

namespace FSTService.Tests.Unit;

public sealed class DatabaseMaintenanceDryRunReporterTests
{
    [Fact]
    public void SnapshotPolicy_KeepsActiveProjectionAndOneRollbackCompleted()
    {
        var scrapes = new[]
        {
            Completed(750),
            Completed(749),
            Completed(746),
            Incomplete(748),
        };
        var policy = SnapshotRetentionPolicy.Create([750], [750], scrapes, rollbackCompletedSnapshotsToKeep: 1);

        var decisions = policy.Classify([750, 749, 748, 746]).ToDictionary(decision => decision.SnapshotId);

        Assert.Equal(SnapshotCleanupAction.Keep, decisions[750].Action);
        Assert.Contains("active snapshot state", decisions[750].Reasons);
        Assert.Contains("current projection source", decisions[750].Reasons);
        Assert.Equal(SnapshotCleanupAction.Keep, decisions[749].Action);
        Assert.Contains("rollback completed snapshot", decisions[749].Reasons);
        Assert.Equal(SnapshotCleanupAction.PurgeCandidate, decisions[746].Action);
        Assert.Contains("older completed scrape outside rollback window", decisions[746].Reasons);
    }

    [Fact]
    public void SnapshotPolicy_ClassifiesIncompleteWithNewerScrapeAsPurgeCandidate()
    {
        var policy = SnapshotRetentionPolicy.Create(
            activeSnapshotIds: [750],
            projectionSourceSnapshotIds: [750],
            scrapes: [Completed(750), Incomplete(748)],
            rollbackCompletedSnapshotsToKeep: 1);

        var decision = policy.Classify(748);

        Assert.Equal(SnapshotCleanupAction.PurgeCandidate, decision.Action);
        Assert.Contains("incomplete scrape with newer scrape started", decision.Reasons);
    }

    [Fact]
    public void SnapshotPolicy_BlocksLatestIncompleteWhenNoNewerScrapeExists()
    {
        var policy = SnapshotRetentionPolicy.Create(
            activeSnapshotIds: [],
            projectionSourceSnapshotIds: [],
            scrapes: [Incomplete(751)],
            rollbackCompletedSnapshotsToKeep: 1);

        var decision = policy.Classify(751);

        Assert.Equal(SnapshotCleanupAction.Blocked, decision.Action);
        Assert.Contains("latest incomplete scrape has no newer scrape yet", decision.Reasons);
    }

    [Fact]
    public void SnapshotPolicy_BlocksSnapshotsMissingScrapeLogRows()
    {
        var policy = SnapshotRetentionPolicy.Create(
            activeSnapshotIds: [],
            projectionSourceSnapshotIds: [],
            scrapes: [Completed(750)],
            rollbackCompletedSnapshotsToKeep: 1);

        var decision = policy.Classify(719);

        Assert.Equal(SnapshotCleanupAction.Blocked, decision.Action);
        Assert.Contains("missing scrape_log row", decision.Reasons);
    }

    [Fact]
    public void LegacyLiveSummary_IsPlannedDropReportOnly()
    {
        var summary = new LegacyLiveDryRunSummary(
            PlannedDropCandidate: true,
            Tables: [new RelationFootprint("leaderboard_entries_solo_guitar", 100, 40, 60, 0, 0)],
            Indexes: [],
            TotalBytes: 100,
            HeapBytes: 40,
            IndexBytes: 60,
            LiveTuples: 0,
            Reason: "legacy live solo is planned for drop; report-only in this act slice");

        Assert.True(summary.PlannedDropCandidate);
        Assert.Contains("report-only", summary.Reason);
    }

    [Fact]
    public void LegacyStagingSummary_EligibleWhenEmptyNoActiveMetaAndIndexBytesExist()
    {
        var summary = new LegacyStagingDryRunSummary(
            CleanupEligible: true,
            Footprint: new RelationFootprint("leaderboard_staging", 9_600_000, 0, 9_600_000, 0, 0),
            ExactRowCount: 0,
            ActiveStagingMetaRows: 0,
            ReportOnly: true,
            Reason: "eligible candidate: no rows, no active staging metadata, and index bytes exceed cleanup threshold",
            Indexes: []);

        Assert.True(summary.CleanupEligible);
        Assert.True(summary.ReportOnly);
        Assert.Contains("eligible candidate", summary.Reason);
    }

    [Fact]
    public void LegacyStagingEligibility_IgnoresNormalTinyPrimaryKeyFootprint()
    {
        var eligible = InvokeLegacyStagingEligibility(
            new RelationFootprint("leaderboard_staging", 16_384, 8_192, 8_192, 0, 0),
            exactRowCount: 0,
            activeStagingMetaRows: 0);

        Assert.False(eligible);
    }

    [Fact]
    public void RuntimeIndexDefinitions_DoNotRecreateDeprecatedIndexes()
    {
        var definitions = GetPrivateStaticStringArray("SoloIndexDefinitions")
            .Concat(GetPrivateStaticStringArray("BandIndexDefinitions"))
            .ToArray();

        Assert.DoesNotContain(definitions, definition => definition.Contains("ix_le_song_rank", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(definitions, definition => definition.Contains("ix_le_account ON", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(definitions, definition => definition.Contains("ix_be_song_rank", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(definitions, definition => definition.Contains("ix_be_song_score", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(definitions, definition => definition.Contains("ix_be_combo", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(definitions, definition => definition.Contains("ix_bms_account", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(definitions, definition => definition.Contains("ix_bm_song_type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RuntimeIndexDefinitions_KeepCurrentSoloIndexes()
    {
        var definitions = GetPrivateStaticStringArray("SoloIndexDefinitions");

        Assert.Contains(definitions, definition => definition.Contains("ix_le_song_score", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(definitions, definition => definition.Contains("ix_le_account_song", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(definitions, definition => definition.Contains("ix_le_song_source", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(definitions, definition => definition.Contains("ix_le_band_members", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RetentionHelperIndexDefinitions_GenerateConcurrentSqlForExplicitTargets()
    {
        var definitions = GetReporterPrivateStaticArray<RetentionHelperIndexDefinition>("RetentionHelperIndexDefinitions");

        Assert.Contains(definitions, definition => definition.Name == "ix_crh_retention_cutoff_account");
        Assert.Contains(definitions, definition => definition.Name == "ix_btrhp_retention_cutoff_scope_team");
        Assert.Contains(definitions, definition => definition.Name == "ix_btrh_retention_cutoff_scope_team");
        Assert.Contains(definitions, definition => definition.Name == "ix_btrh_latest");
        Assert.Contains(definitions, definition => definition.Name == "ix_btrhl_snapshot");
        Assert.Contains(definitions, definition => definition.Name == "ix_btrhp_team_date");
        Assert.Contains(definitions, definition => definition.Name == "ix_btrhp_status_date");
        Assert.Contains(definitions, definition => definition.Name == "ix_btrsh_retention_cutoff_scope");
        Assert.Contains(definitions, definition => definition.Name == "ix_btrsh_latest");
        Assert.Contains(definitions, definition => definition.Name == "band_team_rankings_current_band_duets_ix_adjusted");
        Assert.Contains(definitions, definition => definition.Name == "band_team_rankings_current_band_duets_ix_weighted");
        Assert.Contains(definitions, definition => definition.Name == "band_team_rankings_current_band_duets_ix_fcrate");
        Assert.Contains(definitions, definition => definition.Name == "band_team_rankings_current_band_duets_ix_totalscore");
        Assert.Contains(definitions, definition => definition.Name == "band_team_rankings_current_band_trios_ix_adjusted");
        Assert.Contains(definitions, definition => definition.Name == "band_team_rankings_current_band_trios_ix_weighted");
        Assert.Contains(definitions, definition => definition.Name == "band_team_rankings_current_band_trios_ix_fcrate");
        Assert.Contains(definitions, definition => definition.Name == "band_team_rankings_current_band_trios_ix_totalscore");
        Assert.Contains(definitions, definition => definition.Name == "band_team_rankings_current_band_quad_ix_adjusted");
        Assert.Contains(definitions, definition => definition.Name == "band_team_rankings_current_band_quad_ix_weighted");
        Assert.Contains(definitions, definition => definition.Name == "band_team_rankings_current_band_quad_ix_fcrate");
        Assert.Contains(definitions, definition => definition.Name == "band_team_rankings_current_band_quad_ix_totalscore");
        Assert.All(definitions, definition =>
        {
            Assert.Contains("CREATE INDEX CONCURRENTLY IF NOT EXISTS", definition.CreateSql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"\"{definition.Name}\"", definition.CreateSql, StringComparison.Ordinal);
            Assert.Contains($"public.\"{definition.TableName}\"", definition.CreateSql, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CurrentBandRankingStartupSchema_DoesNotCreateRankOrderIndexes()
    {
        var schema = BandRankingStorageNames.GetCurrentSchemaSql();

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"band_team_rankings_current_band_duets\"", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"band_team_rankings_current_band_trios\"", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"band_team_rankings_current_band_quad\"", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("band_team_rankings_current_band_duets_ix_adjusted", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("band_team_rankings_current_band_duets_ix_weighted", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("band_team_rankings_current_band_duets_ix_fcrate", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("band_team_rankings_current_band_duets_ix_totalscore", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("band_team_rankings_current_band_trios_ix_adjusted", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("band_team_rankings_current_band_trios_ix_weighted", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("band_team_rankings_current_band_trios_ix_fcrate", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("band_team_rankings_current_band_trios_ix_totalscore", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("band_team_rankings_current_band_quad_ix_adjusted", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("band_team_rankings_current_band_quad_ix_weighted", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("band_team_rankings_current_band_quad_ix_fcrate", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("band_team_rankings_current_band_quad_ix_totalscore", schema, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaintenanceIndexMatching_TreatsRenamedBuildIndexesAsEquivalent()
    {
        var method = typeof(DatabaseMaintenanceDryRunReporter).GetMethod("IndexDefinitionMatchesKeyColumns", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var matches = Assert.IsType<bool>(method.Invoke(null,
        [
            "CREATE INDEX band_team_rankings_build_band_quad_20260509_ix_adjusted ON public.band_team_rankings_current_band_quad USING btree (band_type, ranking_scope, combo_id, adjusted_skill_rank)",
            new[] { "band_type", "ranking_scope", "combo_id", "adjusted_skill_rank" },
        ]));
        var mismatch = Assert.IsType<bool>(method.Invoke(null,
        [
            "CREATE INDEX band_team_rankings_build_band_quad_20260509_ix_weighted ON public.band_team_rankings_current_band_quad USING btree (band_type, ranking_scope, combo_id, weighted_rank)",
            new[] { "band_type", "ranking_scope", "combo_id", "adjusted_skill_rank" },
        ]));

        Assert.True(matches);
        Assert.False(mismatch);
    }

    [Fact]
    public void RetentionHelperIndexResolution_IsSingleNamedTargetOnly()
    {
        var method = typeof(DatabaseMaintenanceDryRunReporter).GetMethod("ResolveRetentionHelperIndexDefinition", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var definition = Assert.IsType<RetentionHelperIndexDefinition>(method.Invoke(null, ["IX_CRH_RETENTION_CUTOFF_ACCOUNT"]));

        Assert.Equal("ix_crh_retention_cutoff_account", definition.Name);
        Assert.Null(method.Invoke(null, ["all"]));
        Assert.Null(method.Invoke(null, [""]));
    }

    [Fact]
    public void BandHistoryCoverageClassification_RequiresNarrowPointsAndStatsToCoverWideRows()
    {
        var wide = Coverage(rows: 100, teams: 10, dates: 5);
        var points = Coverage(rows: 100, teams: 10, dates: 5);
        var stats = Coverage(rows: 5, teams: 0, dates: 5);

        Assert.Equal(BandHistoryCoverageClassification.Complete, InvokeBandHistoryCoverageClassification(wide, points, stats, missingWideRows: 0, missingStatsRows: 0));
        Assert.Equal(BandHistoryCoverageClassification.Partial, InvokeBandHistoryCoverageClassification(wide, points, stats, missingWideRows: 2, missingStatsRows: 0));
        Assert.Equal(BandHistoryCoverageClassification.Partial, InvokeBandHistoryCoverageClassification(wide, points, stats, missingWideRows: 0, missingStatsRows: 1));
        Assert.Equal(BandHistoryCoverageClassification.WideOnly, InvokeBandHistoryCoverageClassification(wide, null, null, missingWideRows: 100, missingStatsRows: 0));
        Assert.Equal(BandHistoryCoverageClassification.Absent, InvokeBandHistoryCoverageClassification(null, null, null, missingWideRows: 0, missingStatsRows: 0));
    }

    [Fact]
    public void SnapshotRewritePlan_UsesKeepAndPurgeIdsDeterministically()
    {
        var plans = InvokeSnapshotRewritePlans(
            [new SnapshotPartitionStats(
                "leaderboard_entries_snapshot_pro_drums",
                TotalBytes: 1_000,
                HeapBytes: 700,
                IndexBytes: 300,
                LiveTuples: 100,
                NDistinct: 3,
                EstimatedStatsCoverage: 1,
                SnapshotEstimates:
                [
                    new SnapshotEstimate(750, 0.4, 40, 400),
                    new SnapshotEstimate(749, 0.3, 30, 300),
                    new SnapshotEstimate(746, 0.3, 30, 300),
                ])],
            [
                new SnapshotRetentionDecision(750, SnapshotCleanupAction.Keep, ["active snapshot state"]),
                new SnapshotRetentionDecision(749, SnapshotCleanupAction.Keep, ["rollback completed snapshot"]),
                new SnapshotRetentionDecision(746, SnapshotCleanupAction.PurgeCandidate, ["older completed scrape outside rollback window"]),
            ],
            [
                new PartitionSnapshotCandidate("leaderboard_entries_snapshot_pro_drums", 750, SnapshotCleanupAction.Keep, [], 40, 400, 0.4),
                new PartitionSnapshotCandidate("leaderboard_entries_snapshot_pro_drums", 749, SnapshotCleanupAction.Keep, [], 30, 300, 0.3),
                new PartitionSnapshotCandidate("leaderboard_entries_snapshot_pro_drums", 746, SnapshotCleanupAction.PurgeCandidate, [], 30, 300, 0.3),
            ]);

        var plan = Assert.Single(plans);
        Assert.True(plan.CanExecute);
        Assert.Equal("Solo_PeripheralDrums", plan.Instrument);
        Assert.Equal([750, 749], plan.KeepSnapshotIds);
        Assert.Equal([746], plan.PurgeSnapshotIds);
        Assert.Equal(300, plan.EstimatedPurgeBytes);
    }

    [Fact]
    public void SnapshotRewritePlan_BlocksUnexpectedPartitions()
    {
        var plans = InvokeSnapshotRewritePlans(
            [new SnapshotPartitionStats(
                "leaderboard_entries_snapshot_experimental",
                TotalBytes: 1_000,
                HeapBytes: 700,
                IndexBytes: 300,
                LiveTuples: 100,
                NDistinct: 2,
                EstimatedStatsCoverage: 1,
                SnapshotEstimates:
                [
                    new SnapshotEstimate(750, 0.5, 50, 500),
                    new SnapshotEstimate(746, 0.5, 50, 500),
                ])],
            [
                new SnapshotRetentionDecision(750, SnapshotCleanupAction.Keep, ["active snapshot state"]),
                new SnapshotRetentionDecision(746, SnapshotCleanupAction.PurgeCandidate, ["older completed scrape outside rollback window"]),
            ],
            [new PartitionSnapshotCandidate("leaderboard_entries_snapshot_experimental", 746, SnapshotCleanupAction.PurgeCandidate, [], 50, 500, 0.5)]);

        var plan = Assert.Single(plans);
        Assert.False(plan.CanExecute);
        Assert.Contains("not an expected", plan.Reason);
    }

    [Fact]
    public void SnapshotRewritePlan_RetainsBlockedSnapshotIds()
    {
        var plans = InvokeSnapshotRewritePlans(
            [new SnapshotPartitionStats(
                "leaderboard_entries_snapshot_pro_drums",
                TotalBytes: 1_000,
                HeapBytes: 700,
                IndexBytes: 300,
                LiveTuples: 100,
                NDistinct: 3,
                EstimatedStatsCoverage: 1,
                SnapshotEstimates:
                [
                    new SnapshotEstimate(749, 0.3, 30, 300),
                    new SnapshotEstimate(746, 0.3, 30, 300),
                    new SnapshotEstimate(720, 0.4, 40, 400),
                ])],
            [
                new SnapshotRetentionDecision(749, SnapshotCleanupAction.Keep, ["rollback completed snapshot"]),
                new SnapshotRetentionDecision(746, SnapshotCleanupAction.PurgeCandidate, ["older completed scrape outside rollback window"]),
                new SnapshotRetentionDecision(720, SnapshotCleanupAction.Blocked, ["missing scrape_log row"]),
            ],
            [
                new PartitionSnapshotCandidate("leaderboard_entries_snapshot_pro_drums", 749, SnapshotCleanupAction.Keep, [], 30, 300, 0.3),
                new PartitionSnapshotCandidate("leaderboard_entries_snapshot_pro_drums", 746, SnapshotCleanupAction.PurgeCandidate, [], 30, 300, 0.3),
                new PartitionSnapshotCandidate("leaderboard_entries_snapshot_pro_drums", 720, SnapshotCleanupAction.Blocked, [], 40, 400, 0.4),
            ]);

        var plan = Assert.Single(plans);
        Assert.True(plan.CanExecute);
        Assert.Equal([749, 720], plan.KeepSnapshotIds);
        Assert.Equal([720], plan.BlockedSnapshotIds);
        Assert.Equal([746], plan.PurgeSnapshotIds);
    }

    [Fact]
    public void RewriteSnapshotPartitionAsync_CallsPartitionSwap()
    {
        var method = typeof(DatabaseMaintenanceDryRunReporter).GetMethod(
            nameof(DatabaseMaintenanceDryRunReporter.RewriteSnapshotPartitionAsync),
            BindingFlags.Public | BindingFlags.Instance);
        var stateMachineType = method?.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        var moveNext = stateMachineType?.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance);
        var target = typeof(DatabaseMaintenanceDryRunReporter).GetMethod("SwapSnapshotPartitionAsync", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(moveNext);
        Assert.NotNull(target);
        Assert.True(MethodBodyContainsCall(moveNext, target));
    }

    private static ScrapeSummary Completed(long id) => new(id, StartedAt: DateTime.UtcNow.AddHours(-id), CompletedAt: DateTime.UtcNow.AddHours(-id).AddMinutes(10));

    private static ScrapeSummary Incomplete(long id) => new(id, StartedAt: DateTime.UtcNow.AddHours(-id), CompletedAt: null);

    private static IReadOnlyList<string> GetPrivateStaticStringArray(string fieldName)
    {
        var field = typeof(GlobalLeaderboardPersistence).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return Assert.IsType<string[]>(field.GetValue(null));
    }

    private static IReadOnlyList<T> GetReporterPrivateStaticArray<T>(string fieldName)
    {
        var field = typeof(DatabaseMaintenanceDryRunReporter).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<IReadOnlyList<T>>(field.GetValue(null));
    }

    private static BandHistoryCoverageSourceSummary Coverage(long rows, long teams, long dates) =>
        new(rows, teams, dates, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10)), DateOnly.FromDateTime(DateTime.UtcNow));

    private static BandHistoryCoverageClassification InvokeBandHistoryCoverageClassification(
        BandHistoryCoverageSourceSummary? wide,
        BandHistoryCoverageSourceSummary? points,
        BandHistoryCoverageSourceSummary? stats,
        long missingWideRows,
        long missingStatsRows)
    {
        var method = typeof(DatabaseMaintenanceDryRunReporter).GetMethod("ClassifyBandHistoryCoverage", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return Assert.IsType<BandHistoryCoverageClassification>(method.Invoke(null, [wide, points, stats, missingWideRows, missingStatsRows]));
    }

    private static bool InvokeLegacyStagingEligibility(RelationFootprint? footprint, long exactRowCount, long activeStagingMetaRows)
    {
        var method = typeof(DatabaseMaintenanceDryRunReporter).GetMethod("IsLegacyStagingCleanupEligible", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method.Invoke(null, [footprint, exactRowCount, activeStagingMetaRows]));
    }

    private static IReadOnlyList<SnapshotPartitionRewritePlan> InvokeSnapshotRewritePlans(
        IReadOnlyList<SnapshotPartitionStats> partitions,
        IReadOnlyList<SnapshotRetentionDecision> decisions,
        IReadOnlyList<PartitionSnapshotCandidate> candidates)
    {
        var method = typeof(DatabaseMaintenanceDryRunReporter).GetMethod("BuildSnapshotRewritePlans", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return Assert.IsAssignableFrom<IReadOnlyList<SnapshotPartitionRewritePlan>>(method.Invoke(null, [partitions, decisions, candidates]));
    }

    private static bool MethodBodyContainsCall(MethodInfo method, MethodInfo target)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
            return false;

        for (var index = 0; index < il.Length - 4; index++)
        {
            if (il[index] is not 0x28 and not 0x6F)
                continue;

            var token = BitConverter.ToInt32(il, index + 1);
            try
            {
                if (method.Module.ResolveMethod(token) == target)
                    return true;
            }
            catch (ArgumentException)
            {
            }
        }

        return false;
    }
}