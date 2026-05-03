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