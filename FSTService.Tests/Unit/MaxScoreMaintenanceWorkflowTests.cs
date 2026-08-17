using System.Collections.Concurrent;
using System.Text.Json;
using FortniteFestival.Core;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class MaxScoreMaintenanceWorkflowTests
{
    [Fact]
    public void Expected_account_cache_rows_sort_after_combo_id_projection()
    {
        const string songId = "same-song";
        var row =
            MaxScoreMaintenanceService
                .BuildExpectedAccountCacheRow(
                    "account",
                    [
                        new PlayerScoreDto
                        {
                            SongId = songId,
                            Instrument = "Solo_Bass",
                            Score = 20,
                            Rank = 2,
                        },
                        new PlayerScoreDto
                        {
                            SongId = songId,
                            Instrument = "Solo_Guitar",
                            Score = 10,
                            Rank = 1,
                        },
                    ],
                    new Dictionary<
                        (string SongId, string Instrument),
                        long>
                    {
                        [(songId, "Solo_Bass")] = 200,
                        [(songId, "Solo_Guitar")] = 100,
                    });

        Assert.Equal(
            ["01", "02"],
            row.Scores.Select(score =>
                score.Instrument));
    }

    [Fact]
    public void Maintenance_account_evidence_uses_bounded_hashes()
    {
        const string accountId =
            "sensitive-account-identity";
        var evidenceId =
            MaxScoreMaintenanceAccountIdPolicy
                .FormatEvidenceId(accountId);

        Assert.StartsWith(
            "sha256:",
            evidenceId,
            StringComparison.Ordinal);
        Assert.Equal(23, evidenceId.Length);
        Assert.DoesNotContain(
            accountId,
            evidenceId,
            StringComparison.Ordinal);
        Assert.Equal(
            evidenceId,
            MaxScoreMaintenanceAccountIdPolicy
                .FormatEvidenceId(accountId));
    }

    [Fact]
    public async Task Plan_failure_reports_evidence_stage_and_base_exception_message()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        fixture.Service.PlanEvidenceStageFailureTestHook =
            stage => stage == "complete-score-history-evidence"
                ? new InvalidOperationException(
                    "outer failure included SELECT and credentials",
                    new TimeoutException(
                        "Timeout during reading attempt"))
                : null;

        var plan = await fixture.PlanAsync();

        Assert.False(plan.CanApply);
        var failure = Assert.Single(plan.Checks);
        Assert.Equal("plan", failure.Name);
        Assert.False(failure.Passed);
        Assert.Contains(
            "stage=complete-score-history-evidence",
            failure.Detail,
            StringComparison.Ordinal);
        Assert.Contains(
            "Timeout during reading attempt",
            failure.Detail,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SELECT",
            failure.Detail,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "credentials",
            failure.Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_accepts_valid_target_with_unrelated_over_limit_catalog_maximum()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync(
                includeScopeDivergence: true,
                publishedOnlyMaximum: checked(
                    RankingsCalculator
                        .MaximumScoreWithRepresentableRankingCutoff
                    + 1));

        var plan = await fixture.PlanAsync();

        Assert.True(
            plan.CanApply,
            string.Join(
                Environment.NewLine,
                plan.Checks
                    .Where(check => !check.Passed)
                    .Select(check => check.Detail)));
        Assert.Equal(
            int.MaxValue,
            RankingsCalculator.ComputeMaxScoreThreshold(
                checked(
                    RankingsCalculator
                        .MaximumScoreWithRepresentableRankingCutoff
                    + 1)));
        Assert.All(
            plan.ObservedScoreChecks,
            check => Assert.True(check.Passed));
    }

    [Fact]
    public async Task Plan_apply_and_resume_preserve_evidence_and_use_one_configured_deadline()
    {
        const int commandTimeoutSeconds = 1800;
        await using var fixture =
            await WorkflowFixture.CreateAsync(
                maxScoreMaintenanceCommandTimeoutSeconds:
                    commandTimeoutSeconds);
        var configured =
            new ConcurrentQueue<(string Stage, int Seconds)>();
        fixture.Service.EvidenceCommandTimeoutTestHook =
            (stage, seconds) =>
                configured.Enqueue((stage, seconds));

        var reference =
            await fixture
                .ComputeReferenceScoreHistoryEvidenceAsync();
        var plan = await fixture.PlanAsync();

        Assert.True(plan.CanApply);
        Assert.Equal(reference, plan.ScoreHistoryEvidence);
        AssertConfiguredEvidenceTimeouts(
            configured,
            commandTimeoutSeconds);
        Clear(configured);

        fixture.Service.AfterPhaseCheckpointTestHook =
            phase =>
            {
                if (phase
                    == MaxScoreMaintenancePhase.RollbackCaptured)
                {
                    throw new InvalidOperationException(
                        "Stop after rollback capture.");
                }
            };
        var interrupted = await fixture.ApplyAsync(
            resume: false,
            plan.PlanDigest,
            rollbackPath: "rollback.json");

        Assert.False(interrupted.Succeeded);
        Assert.True(interrupted.Resumable);
        Assert.True(interrupted.PublicReadsFrozen);
        Assert.Equal(
            reference,
            fixture.ReadDurableScoreHistoryEvidence());
        AssertConfiguredEvidenceTimeouts(
            configured,
            commandTimeoutSeconds);
        Clear(configured);

        fixture.Service.AfterPhaseCheckpointTestHook = null;
        var resumed = await fixture.ApplyAsync(
            resume: true,
            plan.PlanDigest,
            rollbackPath: "rollback.json");

        Assert.True(
            resumed.Succeeded,
            fixture.LastFailure?.ToString());
        Assert.Equal(
            reference,
            fixture.ReadDurableScoreHistoryEvidence());
        AssertConfiguredEvidenceTimeouts(
            configured,
            commandTimeoutSeconds);
    }

    [Fact]
    public async Task Plan_apply_and_resume_revalidate_exact_observed_outlier_evidence()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync(
                publishedOverlayScore: 70_000);
        var approved = await fixture.PlanAsync();

        Assert.True(approved.CanApply);
        Assert.Equal(
            MaxScoreMaintenancePlanReport.CurrentReportVersion,
            approved.ReportVersion);
        var approvedCheck =
            Assert.Single(approved.ObservedScoreChecks);
        Assert.Equal(60_000, approvedCheck.NewMaximum);
        Assert.Equal(63_000, approvedCheck.ValidCutoff);
        Assert.Equal(70_000, approvedCheck.HighestObservedScore);
        Assert.Equal(
            40_000,
            approvedCheck.HighestEligibleObservedScore);
        Assert.Equal(1, approvedCheck.AboveValidCutoffCount);
        Assert.True(approvedCheck.SourceMapped);
        Assert.True(approvedCheck.Passed);
        var approvedDetail = Assert.Single(
            approved.Checks,
            check => check.Name == "observed-scores").Detail;
        Assert.Contains(
            "rawHighest=70000",
            approvedDetail,
            StringComparison.Ordinal);
        Assert.Contains(
            "eligibleHighest=40000",
            approvedDetail,
            StringComparison.Ordinal);
        Assert.Contains(
            "aboveValidCutoffCount=1",
            approvedDetail,
            StringComparison.Ordinal);

        fixture.UpdatePublishedSnapshotScore(65_000);
        var changed = await fixture.PlanAsync();
        Assert.True(changed.CanApply);
        var changedCheck =
            Assert.Single(changed.ObservedScoreChecks);
        Assert.True(changedCheck.Passed);
        Assert.Equal(70_000, changedCheck.HighestObservedScore);
        Assert.Null(changedCheck.HighestEligibleObservedScore);
        Assert.Equal(2, changedCheck.AboveValidCutoffCount);
        Assert.NotEqual(
            approved.PlanDigest,
            changed.PlanDigest);

        var rejectedApply = await fixture.ApplyAsync(
            resume: false,
            approved.PlanDigest,
            rollbackPath: "rollback.json");
        Assert.False(rejectedApply.Succeeded);
        Assert.False(rejectedApply.Resumable);
        Assert.False(rejectedApply.PublicReadsFrozen);
        Assert.Contains(
            "Plan digest changed",
            fixture.LastFailure?.Message,
            StringComparison.Ordinal);

        fixture.UpdatePublishedSnapshotScore(40_000);
        var restored = await fixture.PlanAsync();
        Assert.True(restored.CanApply);
        Assert.Equal(
            approved.PlanDigest,
            restored.PlanDigest);
        fixture.Service.AfterPhaseCheckpointTestHook =
            phase =>
            {
                if (phase
                    == MaxScoreMaintenancePhase
                        .RollbackCaptured)
                {
                    throw new InvalidOperationException(
                        "Stop after rollback capture.");
                }
            };
        var interrupted = await fixture.ApplyAsync(
            resume: false,
            restored.PlanDigest,
            rollbackPath: "rollback.json");
        Assert.False(interrupted.Succeeded);
        Assert.True(interrupted.Resumable);
        Assert.True(interrupted.PublicReadsFrozen);

        fixture.Service.AfterPhaseCheckpointTestHook = null;
        fixture.Service.ObservedScoreChecksTestHook =
            (stage, checks) =>
                stage == "apply-resume"
                    ? checks.Select(check => check with
                    {
                        AboveValidCutoffCount =
                                check.AboveValidCutoffCount + 1,
                        Passed = true,
                    })
                        .ToArray()
                    : checks;
        var rejectedResume = await fixture.ApplyAsync(
            resume: true,
            restored.PlanDigest,
            rollbackPath: "rollback.json");
        Assert.False(rejectedResume.Succeeded);
        Assert.True(rejectedResume.Resumable);
        Assert.True(rejectedResume.PublicReadsFrozen);
        Assert.Contains(
            "Recomputed max-score plan evidence digest",
            fixture.LastFailure?.Message,
            StringComparison.Ordinal);

        fixture.Service.ObservedScoreChecksTestHook = null;
        var resumed = await fixture.ApplyAsync(
            resume: true,
            restored.PlanDigest,
            rollbackPath: "rollback.json");
        Assert.True(
            resumed.Succeeded,
            fixture.LastFailure?.ToString());
    }

    [Fact]
    public async Task ApplyOrResume_uses_published_overlay_and_population_for_derived_state_and_caches()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();

        Assert.True(
            plan.CanApply,
            string.Join(
                "; ",
                plan.Checks.Select(check =>
                    $"{check.Name}={check.Passed}:{check.Detail}"))
            + "; candidates="
            + string.Join(
                ",",
                plan.RoutineCandidates.Select(candidate =>
                    $"{candidate.Lane}/{candidate.CandidateKind}/{candidate.SubjectKey}/{candidate.SongId}")));
        Assert.Equal(100, plan.PopulationEvidence.MaximumTotalEntries);
        Assert.True(plan.ScoreHistoryEvidence.RowCount > 0);

        var result = await fixture.ApplyAsync(
            resume: false,
            plan.PlanDigest,
            rollbackPath: "rollback.json");

        Assert.True(
            result.Succeeded,
            $"phase={result.Phase}, stage={result.FailureStage}, detail={result.Detail}, exception={fixture.LastFailure}, rankStats={fixture.ReadRankStatsEvidence()}");
        Assert.Equal(
            MaxScoreMaintenancePhase.Completed,
            result.Phase);
        Assert.False(result.PublicReadsFrozen);
        Assert.False(result.Resumable);
        Assert.Equal(
            plan.ScoreHistoryEvidence,
            fixture.ReadDurableScoreHistoryEvidence());
        Assert.NotNull(result.CacheEvidence);
        Assert.Equal(1, result.CacheEvidence!.OverlayOnlyAccountCount);

        var state = fixture.ReadWorkflowState();
        Assert.False(state.Frozen);
        Assert.Equal("completed", state.RunStatus);
        Assert.Equal("completed", state.RunPhase);
        Assert.Equal(100, state.SongStatsPopulation);
        Assert.Equal(200, state.MutablePopulation);
        Assert.Equal(0, state.VisibleNotifications);
        Assert.DoesNotContain(90_000, state.TargetCacheScores);
        Assert.Contains(40_000, state.TargetCacheScores);
        Assert.Contains(50_000, state.TargetCacheScores);
        Assert.Equal(
            [50_000],
            state.OverlayAccountCacheScores);
    }

    [Fact]
    public async Task Resume_from_paths_promoted_with_complete_tiers_ignores_blank_accounts_and_rebuilds_only_changed_instrument_rivals()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync(
                includeScopeDivergence: true,
                includeBlankAffectedAccount: true);
        var planWithBlank = await fixture.PlanAsync();
        Assert.True(planWithBlank.CanApply);
        Assert.Equal(
            6,
            MaxScoreMaintenancePlanReport
                .CurrentPlanDigestContractVersion);
        Assert.Equal(
            0,
            fixture.ReadBlankAccountIdentityCount());
        var repeatedPlan = await fixture.PlanAsync();
        Assert.True(repeatedPlan.CanApply);
        Assert.Equal(
            repeatedPlan.PlanDigest,
            planWithBlank.PlanDigest);

        fixture.SeedUnrelatedLeaderboardRivalState();
        var unrelatedRivalsBefore =
            fixture.ReadUnrelatedLeaderboardRivalState();
        fixture.Service.AfterPhaseCheckpointTestHook =
            phase =>
            {
                if (phase
                    == MaxScoreMaintenancePhase
                        .PathsPromoted)
                {
                    throw new InvalidOperationException(
                        "Stop after path promotion.");
                }
            };
        var interrupted = await fixture.ApplyAsync(
            resume: false,
            planWithBlank.PlanDigest,
            rollbackPath: "rollback.json");
        Assert.False(interrupted.Succeeded);
        Assert.True(interrupted.Resumable);
        Assert.Equal(
            MaxScoreMaintenancePhase.PathsPromoted,
            interrupted.Phase);

        fixture.SeedCompleteAffectedPlayerTiers();
        fixture.Service.AfterPhaseCheckpointTestHook =
            null;
        var resumed = await fixture.ApplyAsync(
            resume: true,
            planWithBlank.PlanDigest,
            rollbackPath: "rollback.json");

        Assert.True(
            resumed.Succeeded,
            fixture.LastFailure?.ToString());
        Assert.Equal(
            MaxScoreMaintenancePhase.Completed,
            resumed.Phase);
        Assert.Equal(
            unrelatedRivalsBefore,
            fixture.ReadUnrelatedLeaderboardRivalState());
        Assert.Equal(
            0,
            fixture.ReadBlankAccountIdentityCount());
        Assert.Equal(
            2 * LeaderboardRivalsCalculator
                .RankMethods.Length,
            fixture.ReadChangedInstrumentRivalStateCount());
    }

    [Fact]
    public async Task Resume_from_notifications_quarantined_skips_derived_rebuild_and_completes_cache_publication()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync(
                includeScopeDivergence: true);
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.NotificationsQuarantined);
        var notificationAuditBefore =
            fixture.ReadNotificationAuditIdentity();
        fixture.Service.BeforeDerivedRebuildTestHook =
            () => throw new InvalidOperationException(
                "Derived rebuild must remain skipped from notifications_quarantined.");

        var resumed = await fixture.ApplyAsync(
            resume: true,
            plan.PlanDigest,
            rollbackPath: "rollback.json");

        Assert.True(
            resumed.Succeeded,
            fixture.LastFailure?.ToString());
        Assert.Equal(
            MaxScoreMaintenancePhase.Completed,
            resumed.Phase);
        Assert.False(resumed.PublicReadsFrozen);
        Assert.Equal(
            notificationAuditBefore,
            fixture.ReadNotificationAuditIdentity());
        fixture.AssertPublishedCachesMatchLive();
    }

    [Fact]
    public async Task ApplyOrResume_builds_cache_inventory_and_completion_from_publication_owned_scopes()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync(
                includeScopeDivergence: true);
        var plan = await fixture.PlanAsync();
        Assert.True(plan.CanApply);

        var result = await fixture.ApplyAsync(
            resume: false,
            plan.PlanDigest,
            rollbackPath: "rollback.json");

        Assert.True(
            result.Succeeded,
            fixture.LastFailure?.ToString());
        Assert.NotNull(result.CacheEvidence);
        Assert.Equal(
            9,
            result.CacheEvidence!
                .PublishedScopeCacheKeyCount);
        var state = fixture.ReadScopeDivergenceState();
        Assert.True(state.PublishedOnlyBaseKeyPresent);
        Assert.True(state.PublishedOnlyLeewayKeyPresent);
        Assert.True(state.PublishedOnlyOffsetKeyPresent);
        Assert.False(state.ActiveOnlyBaseKeyPresent);
        Assert.False(state.ActiveOnlyLeewayKeyPresent);
        Assert.False(state.ActiveOnlyOffsetKeyPresent);
        Assert.Equal(["Solo_Bass"], state.PublishedOnlyInstruments);
        Assert.True(state.ZeroEntryPublishedStatsPresent);
        Assert.False(state.ActiveOnlyStatsPresent);
        Assert.False(state.ActiveOnlyRankingPresent);
        Assert.Equal(50, state.UnaffectedBassEntryCount);
        Assert.Equal(2, state.GuitarRankingSongCount);
        Assert.Equal(
            [
                "00",
                ComboIds.FromInstruments(["Solo_Guitar"]),
            ],
            state.AffectedAccountCacheInstruments);
        Assert.Equal(
            ["Overall", "Solo_Guitar"],
            state.AffectedAccountDatabaseInstruments);
        Assert.Equal(
            ["Overall", "Solo_Bass", "Solo_Drums"],
            state.UnrelatedAccountDatabaseInstruments);
        Assert.Equal(
            [
                "00",
                ComboIds.FromInstruments(
                    ["Solo_Bass"]),
            ],
            state.UnrelatedAccountCacheInstruments);
        Assert.Equal(3, state.TotalSongs);
        Assert.Equal(50, state.GuitarCompletionPercent);
        Assert.Equal(33.3, state.OverallCompletionPercent);
    }

    [Fact]
    public async Task ApplyOrResume_rejects_post_plan_score_history_mutation_and_accepts_corrected_apply()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var approved = await fixture.PlanAsync();
        Assert.True(approved.CanApply);

        var insertedHistoryId =
            fixture.InsertRelevantHistoryMutation();
        var changed = await fixture.PlanAsync();
        Assert.True(changed.CanApply);
        Assert.NotEqual(
            approved.PlanDigest,
            changed.PlanDigest);
        Assert.NotEqual(
            approved.ScoreHistoryEvidence.Fingerprint,
            changed.ScoreHistoryEvidence.Fingerprint);

        var rejected = await fixture.ApplyAsync(
            resume: false,
            approved.PlanDigest,
            rollbackPath: "rollback.json");

        Assert.False(rejected.Succeeded);
        Assert.False(rejected.Resumable);
        Assert.False(rejected.PublicReadsFrozen);
        Assert.Equal(
            MaxScoreMaintenancePhase.None,
            rejected.Phase);
        Assert.Equal("pre_freeze", rejected.FailureStage);
        var rejectedState = fixture.ReadSafetyState();
        Assert.False(rejectedState.Frozen);
        Assert.Null(rejectedState.RunPhase);
        Assert.True(rejectedState.LiveSentinelPresent);
        Assert.Equal(0, rejectedState.StagedCacheRows);
        Assert.Equal(0, rejectedState.VisibleNotifications);

        fixture.DeleteHistory(insertedHistoryId);
        var corrected = await fixture.PlanAsync();
        Assert.True(corrected.CanApply);
        var applied = await fixture.ApplyAsync(
            resume: false,
            corrected.PlanDigest,
            rollbackPath: "rollback.json");

        Assert.True(
            applied.Succeeded,
            fixture.LastFailure?.ToString());
        Assert.Equal(
            MaxScoreMaintenancePhase.Completed,
            applied.Phase);
        Assert.False(
            fixture.ReadSafetyState().Frozen);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("corrupted")]
    [InlineData("swapped")]
    public async Task ApplyOrResume_keeps_freeze_and_checkpoint_when_rollback_file_changes(
        string mutation)
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        Assert.True(plan.CanApply);
        fixture.Service.AfterPhaseCheckpointTestHook =
            phase =>
            {
                if (phase
                    == MaxScoreMaintenancePhase
                        .RollbackCaptured)
                {
                    throw new InvalidOperationException(
                        "checkpoint-stop");
                }
            };

        var checkpointed = await fixture.ApplyAsync(
            resume: false,
            plan.PlanDigest,
            rollbackPath: "rollback.json");
        Assert.False(checkpointed.Succeeded);
        Assert.True(checkpointed.Resumable);
        Assert.True(checkpointed.PublicReadsFrozen);
        Assert.Equal(
            MaxScoreMaintenancePhase.RollbackCaptured,
            checkpointed.Phase);
        var canonicalRollback =
            File.ReadAllBytes(
                fixture.RollbackPath);
        fixture.Service.AfterPhaseCheckpointTestHook = null;
        await fixture.MutateRollbackAsync(mutation);

        var rejectedResume = await fixture.ApplyAsync(
            resume: true,
            plan.PlanDigest,
            rollbackPath: "rollback.json");

        Assert.False(rejectedResume.Succeeded);
        Assert.True(rejectedResume.Resumable);
        Assert.True(rejectedResume.PublicReadsFrozen);
        Assert.Equal(
            MaxScoreMaintenancePhase.RollbackCaptured,
            rejectedResume.Phase);
        var failedState = fixture.ReadSafetyState();
        Assert.True(failedState.Frozen);
        Assert.Equal(
            "rollback_captured",
            failedState.RunPhase);
        Assert.Equal("failed", failedState.RunStatus);
        Assert.True(failedState.LiveSentinelPresent);
        Assert.Equal(0, failedState.StagedCacheRows);
        Assert.Equal(0, failedState.VisibleNotifications);

        File.WriteAllBytes(
            fixture.RollbackPath,
            canonicalRollback);
        var resumed = await fixture.ApplyAsync(
            resume: true,
            plan.PlanDigest,
            rollbackPath: "rollback.json");

        Assert.True(
            resumed.Succeeded,
            fixture.LastFailure?.ToString());
        Assert.Equal(
            MaxScoreMaintenancePhase.Completed,
            resumed.Phase);
        Assert.False(resumed.PublicReadsFrozen);
        var completedState = fixture.ReadSafetyState();
        Assert.False(completedState.Frozen);
        Assert.Equal(
            "completed",
            completedState.RunPhase);
        Assert.Equal("completed", completedState.RunStatus);
        Assert.False(completedState.LiveSentinelPresent);
        Assert.Equal(0, completedState.VisibleNotifications);
    }

    [Fact]
    public async Task ApplyOrResume_fences_caches_staged_evidence_from_ordinary_precompute_and_resumes()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        Assert.True(plan.CanApply);
        fixture.Service.AfterPhaseCheckpointTestHook =
            phase =>
            {
                if (phase == MaxScoreMaintenancePhase.CachesStaged)
                {
                    throw new InvalidOperationException(
                        "caches-staged-checkpoint-stop");
                }
            };

        var checkpointed = await fixture.ApplyAsync(
            resume: false,
            plan.PlanDigest,
            rollbackPath: "rollback.json");

        Assert.False(checkpointed.Succeeded);
        Assert.True(checkpointed.Resumable);
        Assert.True(checkpointed.PublicReadsFrozen);
        Assert.Equal(
            MaxScoreMaintenancePhase.CachesStaged,
            checkpointed.Phase);
        Assert.NotNull(checkpointed.CacheEvidence);
        var canonicalStaging =
            fixture.ReadStagingGeneration();
        var canonicalEvidence =
            fixture.ReadDurableCacheEvidence();
        fixture.Service.AfterPhaseCheckpointTestHook = null;
        await fixture
            .AssertCacheEvidenceWritersBlockedAsync();
        fixture.AssertStagingGenerationEquals(
            canonicalStaging);
        Assert.Equal(
            canonicalEvidence,
            fixture.ReadDurableCacheEvidence());
        var resumed = await fixture.ApplyAsync(
            resume: true,
            plan.PlanDigest,
            rollbackPath: "rollback.json");

        Assert.True(
            resumed.Succeeded,
            fixture.LastFailure?.ToString());
        Assert.Equal(
            MaxScoreMaintenancePhase.Completed,
            resumed.Phase);
        Assert.False(resumed.PublicReadsFrozen);
        var completedState = fixture.ReadSafetyState();
        Assert.False(completedState.Frozen);
        Assert.Equal("completed", completedState.RunPhase);
        Assert.Equal("completed", completedState.RunStatus);
        Assert.False(completedState.LiveSentinelPresent);
    }

    [Theory]
    [InlineData(MaxScoreMaintenancePhase.PathsPromoted)]
    [InlineData(MaxScoreMaintenancePhase.DerivedStateRebuilt)]
    [InlineData(MaxScoreMaintenancePhase.NotificationsQuarantined)]
    public async Task Rollback_restores_paths_and_full_derived_state_from_incomplete_run(
        MaxScoreMaintenancePhase interruptedPhase)
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        Assert.True(plan.CanApply);
        fixture.Service.AfterPhaseCheckpointTestHook =
            phase =>
            {
                if (phase == interruptedPhase)
                {
                    throw new InvalidOperationException(
                        $"Stop after {phase}.");
                }
            };
        var interrupted = await fixture.ApplyAsync(
            resume: false,
            plan.PlanDigest,
            rollbackPath: "rollback.json");
        Assert.False(interrupted.Succeeded);
        Assert.True(interrupted.PublicReadsFrozen);

        fixture.Service.AfterPhaseCheckpointTestHook = null;
        var rolledBack = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.True(
            rolledBack.Succeeded,
            fixture.LastFailure?.ToString());
        Assert.True(rolledBack.Validated);
        Assert.False(rolledBack.PublicReadsFrozen);
        Assert.Equal(
            MaxScoreMaintenancePhase.RolledBack,
            rolledBack.Phase);
        Assert.NotNull(rolledBack.BeforePathFingerprint);
        Assert.NotNull(rolledBack.AfterPathFingerprint);
        Assert.NotEqual(
            rolledBack.BeforePathFingerprint,
            rolledBack.AfterPathFingerprint);
        Assert.All(
            rolledBack.Stages,
            stage => Assert.Equal(
                MaxScoreMaintenanceRollbackStageStatus.Completed,
                stage.Status));
        var state = fixture.ReadWorkflowState();
        Assert.False(state.Frozen);
        Assert.Equal("rolled_back", state.RunStatus);
        Assert.Equal("rolled_back", state.RunPhase);
        Assert.Equal(
            fixture.Manifest.Songs[0]
                .CurrentPath.Maxima.Lead,
            state.SongStatsMaximum);
        fixture.AssertCurrentPathMatches(
            fixture.Manifest.Songs[0].CurrentPath);
        fixture.AssertPublishedCachesMatchLive();
        Assert.Equal(
            interruptedPhase switch
            {
                MaxScoreMaintenancePhase.PathsPromoted =>
                    "paths_promoted",
                MaxScoreMaintenancePhase.DerivedStateRebuilt =>
                    "derived_state_rebuilt",
                MaxScoreMaintenancePhase.NotificationsQuarantined =>
                    "notifications_quarantined",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(interruptedPhase)),
            },
            fixture.ReadOriginalFailureStage());
    }

    [Fact]
    public async Task Rollback_dry_run_validates_without_mutation()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        fixture.Service.AfterPhaseCheckpointTestHook =
            phase =>
            {
                if (phase == MaxScoreMaintenancePhase.PathsPromoted)
                    throw new InvalidOperationException("stop");
            };
        _ = await fixture.ApplyAsync(
            resume: false,
            plan.PlanDigest,
            "rollback.json");
        fixture.Service.AfterPhaseCheckpointTestHook = null;

        var report = await fixture.RollbackAsync(
            plan.PlanDigest,
            dryRun: true);

        Assert.True(report.Validated);
        Assert.True(report.DryRun);
        Assert.False(report.Succeeded);
        Assert.True(report.PublicReadsFrozen);
        Assert.Contains(
            "paths_promoted/failed",
            report.Detail,
            StringComparison.Ordinal);
        var state = fixture.ReadSafetyState();
        Assert.Equal("paths_promoted", state.RunPhase);
        Assert.True(state.Frozen);
    }

    [Fact]
    public async Task Rollback_rejects_successfully_completed_apply()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        var applied = await fixture.ApplyAsync(
            resume: false,
            plan.PlanDigest,
            "rollback.json");
        Assert.True(applied.Succeeded);

        var rejected = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.False(rejected.Succeeded);
        Assert.False(rejected.PublicReadsFrozen);
        Assert.Contains(
            "successfully completed",
            fixture.LastFailure?.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("plan")]
    [InlineData("rollback")]
    public async Task Rollback_rejects_wrong_cli_digest(
        string identity)
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.PathsPromoted);

        var rejected = await fixture.RollbackAsync(
            plan.PlanDigest,
            expectedManifestDigest:
                identity == "manifest"
                    ? new string('f', 64)
                    : null,
            expectedPlanDigest:
                identity == "plan"
                    ? new string('f', 64)
                    : null,
            expectedRollbackDigest:
                identity == "rollback"
                    ? new string('f', 64)
                    : null);
        Assert.False(rejected.Validated);
        Assert.False(rejected.Succeeded);
        Assert.Equal("preflight", rejected.FailureStage);
        var state = fixture.ReadSafetyState();
        Assert.True(state.Frozen);
        Assert.Equal("paths_promoted", state.RunPhase);
    }

    [Theory]
    [InlineData("freeze")]
    [InlineData("publication")]
    [InlineData("current-path")]
    [InlineData("rollback-row")]
    [InlineData("extra-song")]
    public async Task Rollback_rejects_changed_live_identity(
        string mutation)
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.PathsPromoted);
        fixture.MutateRollbackPrecondition(mutation);

        var rejected = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.False(rejected.Succeeded);
        Assert.True(rejected.PublicReadsFrozen);
        Assert.NotNull(fixture.LastFailure);
        var state = fixture.ReadSafetyState();
        Assert.True(state.Frozen);
        Assert.NotEqual("rolled_back", state.RunPhase);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    public async Task Rollback_rejects_active_backend_or_waiting_lock(
        int maintenanceBackends,
        int workerBackends,
        int waitingLocks)
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.PathsPromoted);
        fixture.Service.RollbackRuntimeStateTestHook =
            () => new MaxScoreMaintenanceRollbackRuntimeState(
                maintenanceBackends,
                workerBackends,
                waitingLocks);

        var rejected = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.False(rejected.Succeeded);
        Assert.True(rejected.PublicReadsFrozen);
        Assert.Contains(
            "zero maintenance/worker backends",
            fixture.LastFailure?.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_interruption_resumes_without_double_restore()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.PathsPromoted);
        fixture.Service.AfterRollbackPhaseCheckpointTestHook =
            phase =>
            {
                if (phase
                    == MaxScoreMaintenancePhase
                        .RollbackPathsRestored)
                {
                    throw new InvalidOperationException(
                        "Stop after rollback path commit.");
                }
            };

        var interrupted = await fixture.RollbackAsync(
            plan.PlanDigest);
        Assert.False(interrupted.Succeeded);
        Assert.True(interrupted.Resumable);
        fixture.AssertCurrentPathMatches(
            fixture.Manifest.Songs[0].CurrentPath);

        fixture.Service.AfterRollbackPhaseCheckpointTestHook =
            null;
        var resumed = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.True(
            resumed.Succeeded,
            fixture.LastFailure?.ToString());
        Assert.Equal(
            fixture.Manifest.Songs.Count,
            resumed.RestoredSongCount);
    }

    [Fact]
    public async Task Rollback_accepts_promoted_paths_after_ambiguous_apply_checkpoint()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.RollbackCaptured);
        await fixture.PromotePathsWithoutCheckpointAsync();

        var completed = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.True(
            completed.Succeeded,
            fixture.LastFailure?.ToString());
        fixture.AssertCurrentPathMatches(
            fixture.Manifest.Songs[0].CurrentPath);
    }

    [Fact]
    public async Task Rollback_rejects_rollback_captured_before_path_promotion()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.RollbackCaptured);

        var rejected = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.False(rejected.Succeeded);
        Assert.True(rejected.PublicReadsFrozen);
        Assert.Contains(
            "path",
            fixture.LastFailure?.Message,
            StringComparison.OrdinalIgnoreCase);
        fixture.AssertCurrentPathMatches(
            fixture.Manifest.Songs[0].CurrentPath);
    }

    [Fact]
    public async Task Rollback_transaction_failure_keeps_promoted_path_and_freeze()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.PathsPromoted);
        fixture.Service.BeforeRollbackPathRestoreTestHook =
            () => throw new InvalidOperationException(
                "injected path transaction failure");

        var rejected = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.False(rejected.Succeeded);
        Assert.True(rejected.Resumable);
        Assert.True(rejected.PublicReadsFrozen);
        fixture.AssertCurrentPathMatches(
            fixture.Manifest.Songs[0].StagedPath with
            {
                Revision =
                    fixture.Manifest.Songs[0]
                        .CurrentPath.Revision + 1,
            });
    }

    [Fact]
    public async Task Rollback_post_validation_failure_resumes_and_unfreezes_once()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.DerivedStateRebuilt);
        fixture.Service.BeforeRollbackCompletionTestHook =
            () => throw new InvalidOperationException(
                "injected final rollback failure");

        var rejected = await fixture.RollbackAsync(
            plan.PlanDigest);
        Assert.False(rejected.Succeeded);
        Assert.True(rejected.Validated);
        Assert.True(rejected.PublicReadsFrozen);
        Assert.Equal(
            MaxScoreMaintenancePhase.RollbackValidated,
            rejected.Phase);

        fixture.Service.BeforeRollbackCompletionTestHook =
            null;
        var completed = await fixture.RollbackAsync(
            plan.PlanDigest);
        var repeated = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.True(completed.Succeeded);
        Assert.True(repeated.Succeeded);
        Assert.False(completed.PublicReadsFrozen);
        Assert.False(repeated.PublicReadsFrozen);
    }

    [Fact]
    public async Task Rollback_revalidates_canonical_file_before_terminal_unfreeze()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.DerivedStateRebuilt);
        var canonicalRollback =
            await File.ReadAllBytesAsync(
                fixture.RollbackPath);
        fixture.Service.AfterRollbackPhaseCheckpointTestHook =
            phase =>
            {
                if (phase
                    == MaxScoreMaintenancePhase
                        .RollbackValidated)
                {
                    File.WriteAllText(
                        fixture.RollbackPath,
                        "{}");
                }
            };

        var rejected = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.False(rejected.Succeeded);
        Assert.True(rejected.Validated);
        Assert.True(rejected.PublicReadsFrozen);
        Assert.Equal(
            MaxScoreMaintenancePhase.RollbackValidated,
            rejected.Phase);

        await File.WriteAllBytesAsync(
            fixture.RollbackPath,
            canonicalRollback);
        fixture.Service.AfterRollbackPhaseCheckpointTestHook =
            null;
        var completed = await fixture.RollbackAsync(
            plan.PlanDigest);
        Assert.True(
            completed.Succeeded,
            fixture.LastFailure?.ToString());
    }

    [Fact]
    public async Task Rollback_reserves_report_path_before_mutation()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.PathsPromoted);

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.RollbackAsync(
                plan.PlanDigest,
                reportOutputPath: "rollback.json"));

        var state = fixture.ReadSafetyState();
        Assert.True(state.Frozen);
        Assert.Equal("paths_promoted", state.RunPhase);
        fixture.AssertCurrentPathMatches(
            fixture.Manifest.Songs[0].StagedPath with
            {
                Revision =
                    fixture.Manifest.Songs[0]
                        .CurrentPath.Revision + 1,
            });
    }

    [Fact]
    public async Task Rollback_active_freeze_blocks_normal_cache_publishers()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.PathsPromoted);
        fixture.Service.AfterRollbackPhaseCheckpointTestHook =
            phase =>
            {
                if (phase
                    == MaxScoreMaintenancePhase
                        .RollbackPathsRestored)
                {
                    throw new InvalidOperationException(
                        "stop before rollback cache evidence");
                }
            };

        var interrupted = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.False(interrupted.Succeeded);
        Assert.Equal(
            MaxScoreMaintenancePhase.RollbackPathsRestored,
            interrupted.Phase);
        await fixture
            .AssertCacheEvidenceWritersBlockedAsync();
    }

    [Fact]
    public async Task Rollback_active_freeze_allows_schema_reinitialization()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.PathsPromoted);

        await fixture.ReinitializeSchemaAsync();

        var state = fixture.ReadSafetyState();
        Assert.True(state.Frozen);
        Assert.Equal("paths_promoted", state.RunPhase);
    }

    [Fact]
    public async Task Rollback_active_freeze_rejects_scrape_allocation()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.PathsPromoted);

        fixture.AssertScrapeAllocationBlocked();

        var state = fixture.ReadSafetyState();
        Assert.True(state.Frozen);
        Assert.Equal("paths_promoted", state.RunPhase);
    }

    [Fact]
    public async Task Rollback_cache_fence_survives_unexpected_working_pointer()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.PathsPromoted);
        fixture.SeedUnexpectedWorkingPublication();

        await fixture
            .AssertCacheEvidenceWritersBlockedAsync(
                allowWorkingPublicationRejection: true);
    }

    [Fact]
    public async Task Rollback_reconciles_post_commit_acknowledgement_failure_as_success()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.DerivedStateRebuilt);
        fixture.Service.AfterRollbackCompletionTestHook =
            backendProcessId =>
            {
                fixture.TerminateBackend(
                    backendProcessId);
                throw new InvalidOperationException(
                    "injected post-commit acknowledgement failure");
            };

        var completed = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.True(completed.Succeeded);
        Assert.True(completed.Validated);
        Assert.False(completed.PublicReadsFrozen);
        Assert.Equal(
            MaxScoreMaintenancePhase.RolledBack,
            completed.Phase);
        Assert.True(fixture.IsMutationGateClear());
    }

    [Fact]
    public async Task Rollback_reports_retryable_terminal_cleanup_pending()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.DerivedStateRebuilt);
        fixture.Service.AfterRollbackCompletionTestHook =
            backendProcessId =>
            {
                fixture.TerminateBackend(
                    backendProcessId);
                throw new InvalidOperationException(
                    "injected post-commit acknowledgement failure");
            };
        fixture.Service
                .TerminalMutationGateCleanupFailureTestHook =
            () => new InvalidOperationException(
                "injected cleanup failure");

        var pending = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.False(pending.Succeeded);
        Assert.True(pending.Validated);
        Assert.True(pending.Resumable);
        Assert.False(pending.PublicReadsFrozen);
        Assert.True(pending.CleanupPending);
        Assert.Equal(
            MaxScoreMaintenancePhase.RolledBack,
            pending.Phase);
        Assert.Equal(
            "mutation_gate_cleanup",
            pending.FailureStage);
        Assert.False(fixture.IsMutationGateClear());

        fixture.Service.AfterRollbackCompletionTestHook =
            null;
        fixture.Service
                .TerminalMutationGateCleanupFailureTestHook =
            null;
        var reconciled = await fixture.RollbackAsync(
            plan.PlanDigest);
        Assert.True(reconciled.Succeeded);
        Assert.False(reconciled.CleanupPending);
        Assert.True(fixture.IsMutationGateClear());
    }

    [Fact]
    public async Task Rollback_terminal_retry_clears_stale_mutation_gate()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.PathsPromoted);
        var completed = await fixture.RollbackAsync(
            plan.PlanDigest);
        Assert.True(completed.Succeeded);
        fixture.SeedStaleMutationGate();

        var dryRun = await fixture.RollbackAsync(
            plan.PlanDigest,
            dryRun: true);
        Assert.False(dryRun.Succeeded);
        Assert.True(dryRun.DryRun);
        Assert.False(fixture.IsMutationGateClear());

        var repeated = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.True(repeated.Succeeded);
        Assert.True(fixture.IsMutationGateClear());
    }

    [Fact]
    public async Task Rollback_terminal_validation_failure_is_not_reconciled_as_success()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.PathsPromoted);
        var completed = await fixture.RollbackAsync(
            plan.PlanDigest);
        Assert.True(completed.Succeeded);
        fixture.MutateRollbackPrecondition(
            "current-path");

        var rejected = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.False(rejected.Succeeded);
        Assert.False(rejected.Validated);
        Assert.False(rejected.CleanupPending);
        Assert.Equal(
            MaxScoreMaintenancePhase.None,
            rejected.Phase);
        Assert.Equal("preflight", rejected.FailureStage);
    }

    [Fact]
    public async Task Rollback_terminal_retry_does_not_clear_unrelated_newer_freeze_or_gate()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.PathsPromoted);
        var completed = await fixture.RollbackAsync(
            plan.PlanDigest);
        Assert.True(completed.Succeeded);
        fixture.SetUnrelatedFreeze();
        fixture.SeedStaleMutationGate();

        var rejected = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.False(rejected.Succeeded);
        Assert.False(rejected.Validated);
        Assert.True(rejected.PublicReadsFrozen);
        Assert.Equal("other-owner", fixture.ReadFreezeReason());
        Assert.False(fixture.IsMutationGateClear());
    }

    [Fact]
    public async Task Rollback_completion_refuses_unrelated_replacement_freeze()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.DerivedStateRebuilt);
        fixture.Service.BeforeRollbackCompletionTestHook =
            fixture.SetUnrelatedFreeze;

        var rejected = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.False(rejected.Succeeded);
        Assert.True(rejected.PublicReadsFrozen);
        Assert.Equal("other-owner", fixture.ReadFreezeReason());
        Assert.NotEqual(
            MaxScoreMaintenancePhase.RolledBack,
            rejected.Phase);
    }

    [Fact]
    public async Task Rollback_uses_direction_specific_notification_audit()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.NotificationsQuarantined);
        var applyAudit =
            fixture.ReadNotificationAuditDirections();
        Assert.Equal(1, applyAudit.RunCount);
        Assert.True(applyAudit.ApplyPresent);
        Assert.False(applyAudit.RollbackPresent);
        fixture.Service.AfterRollbackPhaseCheckpointTestHook =
            phase =>
            {
                if (phase
                    == MaxScoreMaintenancePhase
                        .RollbackNotificationsQuarantined)
                {
                    throw new InvalidOperationException(
                        "stop after atomic rollback notification checkpoint");
                }
            };

        var interrupted = await fixture.RollbackAsync(
            plan.PlanDigest);
        Assert.False(interrupted.Succeeded);
        Assert.Equal(
            MaxScoreMaintenancePhase
                .RollbackNotificationsQuarantined,
            interrupted.Phase);
        var interruptedAudit =
            fixture.ReadNotificationAuditDirections();
        Assert.Equal(2, interruptedAudit.RunCount);
        fixture.Service.AfterRollbackPhaseCheckpointTestHook =
            null;

        var completed = await fixture.RollbackAsync(
            plan.PlanDigest);
        Assert.True(
            completed.Succeeded,
            fixture.LastFailure?.ToString());
        var rollbackAudit =
            fixture.ReadNotificationAuditDirections();
        Assert.Equal(2, rollbackAudit.RunCount);
        Assert.Equal(2, rollbackAudit.DistinctDigestCount);
        Assert.True(rollbackAudit.ApplyPresent);
        Assert.True(rollbackAudit.RollbackPresent);
    }

    [Fact]
    public async Task Apply_rejects_resumable_rollback_phase_with_truthful_report()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.CachesStaged);
        fixture.Service.AfterRollbackPhaseCheckpointTestHook =
            phase =>
            {
                if (phase
                    == MaxScoreMaintenancePhase
                        .RollbackPathsRestored)
                {
                    throw new InvalidOperationException(
                        "stop in rollback");
                }
            };
        var interrupted = await fixture.RollbackAsync(
            plan.PlanDigest);
        Assert.False(interrupted.Succeeded);
        fixture.Service.AfterRollbackPhaseCheckpointTestHook =
            null;

        var rejectedApply = await fixture.ApplyAsync(
            resume: true,
            plan.PlanDigest,
            rollbackPath: "rollback.json");

        Assert.False(rejectedApply.Succeeded);
        Assert.False(rejectedApply.Resumable);
        Assert.True(rejectedApply.PublicReadsFrozen);
        Assert.Equal(
            MaxScoreMaintenancePhase.RollbackPathsRestored,
            rejectedApply.Phase);
        Assert.Equal(
            "rollback_owned",
            rejectedApply.FailureStage);
        Assert.NotNull(rejectedApply.CacheEvidence);
        Assert.True(rejectedApply.StagedCacheEntryCount > 0);
    }

    [Fact]
    public async Task Apply_rejects_terminal_rollback_with_truthful_report()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync();
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.PathsPromoted);
        var completed = await fixture.RollbackAsync(
            plan.PlanDigest);
        Assert.True(completed.Succeeded);

        var rejectedApply = await fixture.ApplyAsync(
            resume: true,
            plan.PlanDigest,
            rollbackPath: "rollback.json");

        Assert.False(rejectedApply.Succeeded);
        Assert.False(rejectedApply.Resumable);
        Assert.False(rejectedApply.PublicReadsFrozen);
        Assert.Equal(
            MaxScoreMaintenancePhase.RolledBack,
            rejectedApply.Phase);
        Assert.Equal(
            "rollback_owned",
            rejectedApply.FailureStage);
    }

    [Fact]
    public async Task Rollback_detects_history_used_only_by_restored_maximum()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync(
                includeRollbackOnlyHistoryEvidence: true);
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.DerivedStateRebuilt);
        fixture.Service.BeforeRollbackCompletionTestHook =
            fixture.MutateRollbackOnlyHistoryEvidence;

        var rejected = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.False(rejected.Succeeded);
        Assert.True(rejected.PublicReadsFrozen);
        Assert.Contains(
            "Rollback score history changed",
            fixture.LastFailure?.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_preserves_unrelated_publication_scopes_and_tiers()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync(
                includeScopeDivergence: true);
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.DerivedStateRebuilt);

        var completed = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.True(
            completed.Succeeded,
            fixture.LastFailure?.ToString());
        var state = fixture.ReadScopeDivergenceState();
        Assert.True(state.PublishedOnlyBaseKeyPresent);
        Assert.True(state.PublishedOnlyLeewayKeyPresent);
        Assert.True(state.PublishedOnlyOffsetKeyPresent);
        Assert.False(state.ActiveOnlyBaseKeyPresent);
        Assert.False(state.ActiveOnlyStatsPresent);
        Assert.False(state.ActiveOnlyRankingPresent);
        Assert.Equal(
            ["Overall", "Solo_Bass", "Solo_Drums"],
            state.UnrelatedAccountDatabaseInstruments);
        Assert.Equal(50, state.UnaffectedBassEntryCount);
        Assert.Equal(3, state.TotalSongs);
    }

    [Fact]
    public async Task Rollback_restores_preapply_missing_maximum()
    {
        await using var fixture =
            await WorkflowFixture.CreateAsync(
                currentMaximumMissing: true);
        var plan = await fixture.PlanAsync();
        await fixture.StopApplyAtAsync(
            plan.PlanDigest,
            MaxScoreMaintenancePhase.DerivedStateRebuilt);

        var completed = await fixture.RollbackAsync(
            plan.PlanDigest);

        Assert.True(
            completed.Succeeded,
            fixture.LastFailure?.ToString());
        fixture.AssertCurrentPathMatches(
            fixture.Manifest.Songs[0].CurrentPath);
        Assert.Null(fixture.ReadTargetSongStatsMaximum());
    }

    private static void AssertConfiguredEvidenceTimeouts(
        ConcurrentQueue<(string Stage, int Seconds)> configured,
        int expectedSeconds)
    {
        var observed = configured.ToArray();
        Assert.NotEmpty(observed);
        Assert.Contains(
            observed,
            item => item.Stage
                == "publication-population-evidence");
        Assert.Contains(
            observed,
            item => item.Stage
                == "complete-score-history-evidence");
        Assert.All(
            observed,
            item => Assert.Equal(
                expectedSeconds,
                item.Seconds));
    }

    private static void Clear<T>(ConcurrentQueue<T> queue)
    {
        while (queue.TryDequeue(out _))
        {
        }
    }

    private sealed class WorkflowFixture : IAsyncDisposable
    {
        private const long PublishedScrapeId = 1296;
        private const long ActiveScrapeId = 1297;
        private const long PublicationId = 500;
        private const string SongId = "workflow-song";
        private const string Instrument = "Solo_Guitar";
        private const string PublishedOnlySongId =
            "workflow-published-only";
        private const string PublishedOnlyInstrument =
            "Solo_Bass";
        private const string PublishedZeroSongId =
            "workflow-published-zero";
        private const string ActiveOnlySongId =
            "workflow-active-only";
        private const string BaseAccount = "published-base";
        private const string OverlayAccount = "overlay-only";
        private const string UnrelatedTierAccount =
            "unrelated-tier-account";
        private const string ValidPngBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

        private readonly NpgsqlDataSource _dataSource;
        private readonly MetaDatabase _meta;
        private readonly GlobalLeaderboardPersistence _persistence;
        private readonly ILoggerFactory _loggerFactory;
        private readonly string _dataDirectory;
        private int _reportNumber;

        private WorkflowFixture(
            NpgsqlDataSource dataSource,
            MetaDatabase meta,
            GlobalLeaderboardPersistence persistence,
            ILoggerFactory loggerFactory,
            string dataDirectory,
            MaxScoreMaintenanceManifest manifest,
            MaxScoreMaintenanceService service)
        {
            _dataSource = dataSource;
            _meta = meta;
            _persistence = persistence;
            _loggerFactory = loggerFactory;
            _dataDirectory = dataDirectory;
            Manifest = manifest;
            Service = service;
        }

        internal MaxScoreMaintenanceManifest Manifest { get; }
        internal MaxScoreMaintenanceService Service { get; }
        internal Exception? LastFailure { get; private set; }
        internal string RollbackPath =>
            Path.Combine(
                _dataDirectory,
                "rollback.json");

        internal static async Task<WorkflowFixture> CreateAsync(
            bool includeScopeDivergence = false,
            bool currentMaximumMissing = false,
            bool includeRollbackOnlyHistoryEvidence = false,
            int maxScoreMaintenanceCommandTimeoutSeconds =
                ScraperOptions
                    .DefaultMaxScoreMaintenanceCommandTimeoutSeconds,
            int publishedOverlayScore = 50_000,
            int publishedOnlyMaximum = 70_000,
            bool includeBlankAffectedAccount = false)
        {
            var dataDirectory = Path.Combine(
                Directory.GetCurrentDirectory(),
                ".test-temp",
                $"max-score-workflow-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDirectory);
            var options = new ScraperOptions
            {
                DataDirectory = dataDirectory,
                EnablePathGeneration = true,
                EnableAutomaticPathGeneration = false,
                PathGenerationParallelism = 1,
                PrecomputeLeaderboardSongParallelism = 1,
                PrecomputeLeaderboardInstrumentParallelism = 1,
                RunPrecomputePhasesInParallel = false,
                PrecomputeLiveLeaderboardRivals = false,
                MaxScoreMaintenanceCommandTimeoutSeconds =
                    maxScoreMaintenanceCommandTimeoutSeconds,
            };
            var optionsWrapper = Options.Create(options);
            var dataSource =
                SharedPostgresContainer.CreateDatabase();
            var meta = new MetaDatabase(
                dataSource,
                NullLogger<MetaDatabase>.Instance,
                scraperOptions: options);
            var loggerFactory =
                LoggerFactory.Create(_ => { });
            var features = new FeatureOptions();
            var persistence =
                new GlobalLeaderboardPersistence(
                    meta,
                    loggerFactory,
                    NullLogger<
                        GlobalLeaderboardPersistence>.Instance,
                    dataSource,
                    Options.Create(features));
            persistence.InitializeReadOnly();

            var catalogCapturedAt = new DateTime(
                2026,
                8,
                14,
                12,
                0,
                0,
                DateTimeKind.Utc);
            using var providerDocument = JsonDocument.Parse(
                """
                {
                  "lastModified": "2026-08-01T00:00:00Z",
                  "track": {
                    "su": "workflow-song",
                    "tt": "Workflow Song",
                    "an": "Workflow Artist",
                    "in": {
                      "gr": 3
                    }
                  }
                }
                """);
            var song = new Song
            {
                track = new Track
                {
                    su = SongId,
                    tt = "Workflow Song",
                    an = "Workflow Artist",
                    @in = new In { gr = 3 },
                },
                providerJson =
                    providerDocument.RootElement.Clone(),
            };
            var catalogSongs = new List<Song> { song };
            if (includeScopeDivergence)
            {
                using var publishedOnlyProvider =
                    JsonDocument.Parse(
                        """
                        {
                          "lastModified": "2026-07-15T00:00:00Z",
                          "track": {
                            "su": "workflow-published-only",
                            "tt": "Published Only Song",
                            "an": "Published Artist",
                            "in": {
                              "ba": 3
                            }
                          }
                        }
                        """);
                catalogSongs.Add(new Song
                {
                    track = new Track
                    {
                        su = PublishedOnlySongId,
                        tt = "Published Only Song",
                        an = "Published Artist",
                        @in = new In { ba = 3 },
                    },
                    providerJson =
                        publishedOnlyProvider.RootElement.Clone(),
                });
                using var publishedZeroProvider =
                    JsonDocument.Parse(
                        """
                        {
                          "lastModified": "2026-07-20T00:00:00Z",
                          "track": {
                            "su": "workflow-published-zero",
                            "tt": "Published Zero Song",
                            "an": "Published Zero Artist",
                            "in": {
                              "gr": 3
                            }
                          }
                        }
                        """);
                catalogSongs.Add(new Song
                {
                    track = new Track
                    {
                        su = PublishedZeroSongId,
                        tt = "Published Zero Song",
                        an = "Published Zero Artist",
                        @in = new In { gr = 3 },
                    },
                    providerJson =
                        publishedZeroProvider.RootElement.Clone(),
                });
            }
            var catalog =
                SongCatalogSnapshotBuilder.Create(
                    catalogSongs);

            var currentIdentity =
                new MaxScoreMaintenancePathIdentity(
                    Revision: 0,
                    DatFileHash: new string('c', 64),
                    SongLastModified:
                        "2026-08-01T00:00:00Z",
                    GeneratedAtUtc: new DateTime(
                        2026,
                        8,
                        1,
                        0,
                        0,
                        0,
                        DateTimeKind.Utc),
                    ChoptVersion: "1.16.2",
                    ChoptBinarySha256:
                        new string('d', 64),
                    GenerationProfile: "profile-v2",
                    ArtifactGenerationId:
                        "workflow-current",
                    ExpectedInstruments:
                        currentMaximumMissing
                            ? ["Solo_Bass"]
                            : [Instrument],
                    Maxima: new MaxScoreMaintenanceMaxima(
                        currentMaximumMissing
                            ? null
                            : 55_000,
                        currentMaximumMissing
                            ? 55_000
                            : null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null),
                    PathGenerationPending: false);
            var runtime = new PathGenerationRuntimeIdentity(
                "1.16.3",
                new string('a', 64),
                "profile-v3");
            var stagedIdentity =
                currentIdentity with
                {
                    DatFileHash = new string('b', 64),
                    GeneratedAtUtc = new DateTime(
                        2026,
                        8,
                        13,
                        0,
                        0,
                        0,
                        DateTimeKind.Utc),
                    ChoptVersion = runtime.Version,
                    ChoptBinarySha256 =
                        runtime.BinarySha256,
                    GenerationProfile = runtime.Profile,
                    ArtifactGenerationId =
                        "workflow-staged",
                    ExpectedInstruments =
                        currentMaximumMissing
                            ? [Instrument, "Solo_Bass"]
                            : currentIdentity
                                .ExpectedInstruments,
                    Maxima = currentIdentity.Maxima with
                    {
                        Lead = 60_000,
                    },
                };
            WriteGeneration(
                dataDirectory,
                SongId,
                currentIdentity);
            WriteGeneration(
                dataDirectory,
                SongId,
                stagedIdentity);
            var currentValidated =
                PathArtifactResolver
                    .ValidateImmutableGeneration(
                        dataDirectory,
                        SongId,
                        currentIdentity
                            .ArtifactGenerationId!);
            var stagedValidated =
                PathArtifactResolver
                    .ValidateImmutableGeneration(
                        dataDirectory,
                        SongId,
                        stagedIdentity
                            .ArtifactGenerationId!);
            currentIdentity = currentIdentity with
            {
                ArtifactTreeSha256 =
                    currentValidated.ArtifactTreeSha256,
                ArtifactFileCount =
                    currentValidated.ArtifactFileCount,
            };
            stagedIdentity = stagedIdentity with
            {
                ArtifactTreeSha256 =
                    stagedValidated.ArtifactTreeSha256,
                ArtifactFileCount =
                    stagedValidated.ArtifactFileCount,
            };

            var manifest =
                new MaxScoreMaintenanceManifest(
                    MaxScoreMaintenanceManifest
                        .CurrentManifestVersion,
                    PublishedScrapeId,
                    PublicationId,
                    CatalogVersion: 1,
                    CatalogSchemaVersion:
                        SongCatalogSnapshotBuilder
                            .SchemaVersion,
                    CatalogContentHash:
                        catalog.ContentHash,
                    CatalogSongCount:
                        catalog.SongCount,
                    CatalogSourceCapturedAtUtc:
                        catalogCapturedAt,
                    CreatedAtUtc: new DateTime(
                        2026,
                        8,
                        14,
                        12,
                        5,
                        0,
                        DateTimeKind.Utc),
                    Scope:
                        new MaxScoreMaintenanceScope(
                            MaxScoreMaintenanceStagePurposes
                                .Promotion,
                            new string('8', 64),
                            currentMaximumMissing
                                ? [Instrument, "Solo_Bass"]
                                : [Instrument],
                            [Instrument]),
                    Runtime: runtime,
                    Songs:
                    [
                        new MaxScoreMaintenanceManifestSong(
                            SongId,
                            "2026-08-01T00:00:00Z",
                            currentIdentity,
                            stagedIdentity,
                            [Instrument]),
                    ])
                .ValidateAndNormalize();

            SeedDatabase(
                dataSource,
                catalog,
                catalogCapturedAt,
                currentIdentity,
                publishedOverlayScore);
            if (includeRollbackOnlyHistoryEvidence)
            {
                SeedRollbackOnlyHistoryEvidence(
                    dataSource);
            }
            if (includeBlankAffectedAccount)
            {
                SetBlankAffectedSourceRow(
                    dataSource,
                    present: true);
            }
            if (includeScopeDivergence)
            {
                SeedScopeDivergence(
                    dataSource,
                    publishedOnlyMaximum);
            }
            meta.InsertAccountIds(
                includeScopeDivergence
                    ? [OverlayAccount, UnrelatedTierAccount]
                    : [OverlayAccount]);
            meta.InsertAccountNames(
                [(OverlayAccount, (string?)"Overlay User")]);
            meta.RegisterUser(
                "web-tracker",
                OverlayAccount);
            if (includeScopeDivergence)
            {
                meta.InsertAccountNames(
                    [
                        (
                            UnrelatedTierAccount,
                            (string?)"Unrelated Tier User"),
                    ]);
                meta.RegisterUser(
                    "web-tracker",
                    UnrelatedTierAccount);
            }

            var pathStore =
                new PathDataStore(dataSource);
            var progress = new ScrapeProgressTracker();
            var rivals =
                new LeaderboardRivalsCalculator(
                    persistence,
                    meta,
                    optionsWrapper,
                    NullLogger<
                        LeaderboardRivalsCalculator>.Instance);
            var rankings = new RankingsCalculator(
                persistence,
                meta,
                pathStore,
                progress,
                NullLogger<RankingsCalculator>.Instance,
                scraperOptions: optionsWrapper);
            var derived =
                new MaxScoreMaintenanceDerivedStateService(
                    persistence,
                    pathStore,
                    rankings,
                    new BandRankingRepairService(
                        meta,
                        dataSource,
                        NullLogger<
                            BandRankingRepairService>.Instance),
                    new BandCurrentProjectionBuilder(
                        dataSource,
                        NullLogger<
                            BandCurrentProjectionBuilder>.Instance),
                    rivals,
                    dataSource,
                    optionsWrapper,
                    NullLogger<
                        MaxScoreMaintenanceDerivedStateService>.Instance);
            var precomputer =
                new ScrapeTimePrecomputer(
                    persistence,
                    meta,
                    pathStore,
                    progress,
                    NullLogger<
                        ScrapeTimePrecomputer>.Instance,
                    loggerFactory,
                    new JsonSerializerOptions(
                        JsonSerializerDefaults.Web),
                    features,
                    options,
                    rivals);
            var routineNotifications =
                new ImprovementNotificationService(
                    dataSource,
                    NullLogger<
                        ImprovementNotificationService>.Instance);
            var notifications =
                new MaxScoreMaintenanceNotificationService(
                    dataSource,
                    routineNotifications,
                    Options.Create(
                        new ImprovementNotificationOptions
                        {
                            Scope = "registered",
                        }),
                    NullLogger<
                        MaxScoreMaintenanceNotificationService>.Instance,
                    optionsWrapper);
            var pathCoordinator =
                new PathGenerationCoordinator(
                    new HttpClient(),
                    pathStore,
                    new SongsCacheService(),
                    optionsWrapper,
                    progress,
                    NullLogger<
                        PathGenerationCoordinator>.Instance,
                    UncontendedPathGenerationAdmissionLeaseProvider
                        .Instance);
            var service = new MaxScoreMaintenanceService(
                pathCoordinator,
                pathStore,
                persistence,
                meta,
                dataSource,
                optionsWrapper,
                notifications,
                derived,
                precomputer,
                Substitute.For<
                    ISongInstrumentSupportCache>(),
                NullLogger<
                    MaxScoreMaintenanceService>.Instance);
            var fixture = new WorkflowFixture(
                dataSource,
                meta,
                persistence,
                loggerFactory,
                dataDirectory,
                manifest,
                service);
            service.FailureTestHook =
                exception => fixture.LastFailure = exception;
            await MaxScoreMaintenanceFileStore
                .WriteCanonicalManifestAsync(
                    dataDirectory,
                    "manifest.json",
                    manifest,
                    CancellationToken.None);
            await using (var statistics =
                         await dataSource.OpenConnectionAsync())
            await using (var flush =
                         statistics.CreateCommand())
            {
                flush.CommandText =
                    "SELECT pg_stat_force_next_flush()";
                await flush.ExecuteNonQueryAsync();
            }
            await Task.Delay(1000);
            return fixture;
        }

        internal Task<MaxScoreMaintenancePlanReport> PlanAsync()
            => Service.PlanAsync(
                PublishedScrapeId,
                "manifest.json",
                Manifest.ComputeDigest(),
                NextReportPath("plan"),
                CancellationToken.None);

        internal async Task<MaxScoreMaintenanceScoreHistoryEvidence>
            ComputeReferenceScoreHistoryEvidenceAsync()
        {
            var maxima = Manifest.Songs.ToDictionary(
                song => song.SongId,
                song => song.StagedPath.Maxima
                    .ToSongMaxScores(),
                StringComparer.OrdinalIgnoreCase);
            await using var connection =
                await _dataSource.OpenConnectionAsync();
            await using var transaction =
                await connection.BeginTransactionAsync(
                    System.Data.IsolationLevel.RepeatableRead);
            return await MaxScoreMaintenanceService
                .ComputeScoreHistoryEvidenceReferenceAsync(
                    Manifest,
                    maxima,
                    connection,
                    transaction,
                    CancellationToken.None);
        }

        internal Task<MaxScoreMaintenanceApplyReport> ApplyAsync(
            bool resume,
            string planDigest,
            string rollbackPath)
        {
            LastFailure = null;
            return Service.ApplyOrResumeAsync(
                resume,
                PublishedScrapeId,
                "manifest.json",
                Manifest.ComputeDigest(),
                planDigest,
                rollbackPath,
                NextReportPath(
                    resume ? "resume" : "apply"),
                CancellationToken.None);
        }

        internal async Task<MaxScoreMaintenanceRollbackReport>
            RollbackAsync(
                string planDigest,
                bool dryRun = false,
                string rollbackPath = "rollback.json",
                string? expectedManifestDigest = null,
                string? expectedPlanDigest = null,
                string? expectedRollbackDigest = null,
                string? reportOutputPath = null)
        {
            LastFailure = null;
            expectedRollbackDigest ??=
                await MaxScoreMaintenanceFileStore
                    .ComputeSha256Async(
                        Path.Combine(
                            _dataDirectory,
                            rollbackPath),
                        CancellationToken.None);
            return await Service.RollbackAsync(
                PublishedScrapeId,
                "manifest.json",
                expectedManifestDigest
                    ?? Manifest.ComputeDigest(),
                expectedPlanDigest
                    ?? planDigest,
                rollbackPath,
                expectedRollbackDigest,
                reportOutputPath
                    ?? NextReportPath(
                        dryRun
                            ? "rollback-dry-run"
                            : "rollback"),
                dryRun,
                CancellationToken.None);
        }

        internal async Task StopApplyAtAsync(
            string planDigest,
            MaxScoreMaintenancePhase phase)
        {
            Service.AfterPhaseCheckpointTestHook =
                checkpoint =>
                {
                    if (checkpoint == phase)
                    {
                        throw new InvalidOperationException(
                            $"Stop after {phase}.");
                    }
                };
            var interrupted = await ApplyAsync(
                resume: false,
                planDigest,
                "rollback.json");
            Service.AfterPhaseCheckpointTestHook = null;
            Assert.False(interrupted.Succeeded);
            Assert.True(interrupted.PublicReadsFrozen);
            Assert.Equal(phase, interrupted.Phase);
        }

        internal async Task PromotePathsWithoutCheckpointAsync()
        {
            await using var lease =
                await _meta
                    .AcquireMaxScoreMaintenanceLeaseAsync(
                        PublicationId);
            var promotions = Manifest.Songs
                .Select(song =>
                    new PathGenerationPromotion(
                        song.StagedPath
                            .ArtifactGenerationId!,
                        song.SongId,
                        song.CurrentPath.Revision,
                        song.StagedPath
                            .ArtifactGenerationId!,
                        song.StagedPath.DatFileHash!,
                        song.ExpectedCatalogLastModified,
                        song.StagedPath.GeneratedAtUtc!.Value,
                        new PathGenerationRuntimeIdentity(
                            song.StagedPath.ChoptVersion!,
                            song.StagedPath
                                .ChoptBinarySha256!,
                            song.StagedPath
                                .GenerationProfile!),
                        song.StagedPath
                            .ExpectedInstruments,
                        song.StagedPath.Maxima
                            .ToSongMaxScores()))
                .ToArray();
            var result =
                await new PathDataStore(_dataSource)
                    .TryPromoteGenerationsAtomicallyAsync(
                        promotions,
                        new PathGenerationBatchPromotionGate(
                            PublicationId,
                            PublishedScrapeId,
                            PublicReadFreezeState
                                .MaxScoreMaintenanceReasonPrefix
                            + Manifest.ComputeDigest()),
                        lease,
                        CancellationToken.None);
            Assert.Equal(
                PathGenerationPromotionOutcome.Promoted,
                result.Outcome);
            Assert.Equal(promotions.Length, result.PromotedCount);
        }

        internal void MutateRollbackOnlyHistoryEvidence()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE score_history DISABLE TRIGGER
                    trg_score_history_registration_mutation_guard;
                UPDATE score_history
                SET new_score = new_score - 1
                WHERE account_id =
                    'rollback-only-history';
                ALTER TABLE score_history ENABLE TRIGGER
                    trg_score_history_registration_mutation_guard;
                """;
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        internal void TerminateBackend(
            int backendProcessId)
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT pg_terminate_backend(@backendProcessId)";
            command.Parameters.AddWithValue(
                "backendProcessId",
                backendProcessId);
            Assert.True(command.ExecuteScalar() is true);
        }

        internal Task ReinitializeSchemaAsync()
            => DatabaseInitializer.EnsureSchemaAsync(
                _dataSource);

        internal void AssertScrapeAllocationBlocked()
        {
            Assert.Throws<PublicationCommitBusyException>(
                () => _meta.StartScrapeRun());
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT working_publication_id IS NULL
                FROM scrape_publication_state
                WHERE id = TRUE
                """;
            Assert.True(command.ExecuteScalar() is true);
        }

        internal void SeedUnexpectedWorkingPublication()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO scrape_log (
                    id,
                    started_at,
                    status)
                VALUES (
                    999999,
                    now(),
                    'running');
                INSERT INTO publication_generations (
                    publication_id,
                    scrape_id,
                    status,
                    created_at)
                VALUES (
                    999999,
                    999999,
                    'building',
                    now());
                UPDATE scrape_publication_state
                SET working_publication_id = 999999
                WHERE id = TRUE;
                """;
            Assert.Equal(3, command.ExecuteNonQuery());
        }

        internal bool IsMutationGateClear()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT max_score_mutation_gate_token IS NULL
                   AND max_score_mutation_gate_publication_id
                       IS NULL
                   AND max_score_mutation_gate_backend_pid
                       IS NULL
                   AND max_score_mutation_gate_backend_start
                       IS NULL
                   AND max_score_mutation_gate_acquired_at
                       IS NULL
                FROM scrape_publication_state
                WHERE id = TRUE
                """;
            return command.ExecuteScalar() is true;
        }

        internal void SeedStaleMutationGate()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE scrape_publication_state
                SET max_score_mutation_gate_token =
                        'stale-terminal-token',
                    max_score_mutation_gate_publication_id =
                        @publicationId,
                    max_score_mutation_gate_backend_pid =
                        2147483647,
                    max_score_mutation_gate_backend_start =
                        now() - interval '1 day',
                    max_score_mutation_gate_acquired_at =
                        now() - interval '1 day'
                WHERE id = TRUE
                """;
            command.Parameters.AddWithValue(
                "publicationId",
                PublicationId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        internal void SetUnrelatedFreeze()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE scrape_publication_state
                SET public_reads_frozen = TRUE,
                    public_reads_frozen_at = now(),
                    public_reads_frozen_scrape_id =
                        @publishedScrapeId,
                    public_reads_frozen_reason =
                        'other-owner',
                    updated_at = now()
                WHERE id = TRUE
                """;
            command.Parameters.AddWithValue(
                "publishedScrapeId",
                PublishedScrapeId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        internal string? ReadFreezeReason()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT public_reads_frozen_reason
                FROM scrape_publication_state
                WHERE id = TRUE
                """;
            return command.ExecuteScalar() as string;
        }

        internal (
            long RunCount,
            long DistinctDigestCount,
            bool ApplyPresent,
            bool RollbackPresent)
            ReadNotificationAuditDirections()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)::BIGINT,
                       COUNT(
                           DISTINCT dry_run_digest)::BIGINT,
                       COALESCE(
                           BOOL_OR(
                               canonical_candidate_data::JSONB
                                   ->> 'alignmentDirection'
                               = 'apply'),
                           FALSE),
                       COALESCE(
                           BOOL_OR(
                               canonical_candidate_data::JSONB
                                   ->> 'alignmentDirection'
                               = 'rollback'),
                           FALSE)
                FROM improvement_notification_maintenance_runs
                WHERE notification_purpose = @purpose
                  AND published_scrape_id =
                      @publishedScrapeId
                """;
            command.Parameters.AddWithValue(
                "purpose",
                MaxScoreMaintenanceSchema.Purpose);
            command.Parameters.AddWithValue(
                "publishedScrapeId",
                checked((int)PublishedScrapeId));
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            return (
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3));
        }

        internal void MutateRollbackPrecondition(
            string mutation)
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.Parameters.AddWithValue(
                "manifestDigest",
                Manifest.ComputeDigest());
            command.Parameters.AddWithValue(
                "songId",
                SongId);
            command.CommandText = mutation switch
            {
                "freeze" => """
                    UPDATE scrape_publication_state
                    SET public_reads_frozen_reason =
                        'other-owner'
                    WHERE id = TRUE
                    """,
                "publication" => """
                    UPDATE scrape_publication_state
                    SET public_reads_frozen_scrape_id =
                        @activeScrapeId
                    WHERE id = TRUE
                    """,
                "current-path" => """
                    UPDATE songs
                    SET path_generation_revision =
                        path_generation_revision + 1
                    WHERE song_id = @songId
                    """,
                "rollback-row" => """
                    DROP TRIGGER
                        trg_reject_max_score_rollback_mutation
                        ON max_score_maintenance_rollback_songs;
                    UPDATE max_score_maintenance_rollback_songs
                    SET max_lead_score =
                        COALESCE(max_lead_score, 0) + 1
                    WHERE manifest_sha256 =
                        @manifestDigest
                      AND song_id = @songId
                    """,
                "extra-song" => """
                    INSERT INTO max_score_maintenance_rollback_songs (
                        manifest_sha256,
                        song_id,
                        expected_catalog_last_modified,
                        path_generation_revision,
                        dat_file_hash,
                        song_last_modified,
                        paths_generated_at,
                        chopt_version,
                        chopt_binary_sha256,
                        path_generation_profile,
                        path_artifact_generation_id,
                        path_artifact_tree_sha256,
                        path_artifact_file_count,
                        path_expected_instruments,
                        max_lead_score,
                        max_bass_score,
                        max_drums_score,
                        max_vocals_score,
                        max_pro_lead_score,
                        max_pro_bass_score,
                        max_pro_cymbals_score,
                        max_pro_drums_score,
                        path_generation_pending)
                    SELECT manifest_sha256,
                           'extra-song',
                           expected_catalog_last_modified,
                           path_generation_revision,
                           dat_file_hash,
                           song_last_modified,
                           paths_generated_at,
                           chopt_version,
                           chopt_binary_sha256,
                           path_generation_profile,
                           path_artifact_generation_id,
                           path_artifact_tree_sha256,
                           path_artifact_file_count,
                           path_expected_instruments,
                           max_lead_score,
                           max_bass_score,
                           max_drums_score,
                           max_vocals_score,
                           max_pro_lead_score,
                           max_pro_bass_score,
                           max_pro_cymbals_score,
                           max_pro_drums_score,
                           path_generation_pending
                    FROM max_score_maintenance_rollback_songs
                    WHERE manifest_sha256 =
                        @manifestDigest
                    ORDER BY song_id
                    LIMIT 1
                    """,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(mutation)),
            };
            if (mutation == "publication")
            {
                command.Parameters.AddWithValue(
                    "activeScrapeId",
                    ActiveScrapeId);
            }
            Assert.True(command.ExecuteNonQuery() > 0);
        }

        internal void SetBlankAffectedSourceRow(
            bool present)
            => SetBlankAffectedSourceRow(
                _dataSource,
                present);

        internal void SeedCompleteAffectedPlayerTiers()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM player_stats_tiers
                WHERE account_id = ANY(@accountIds);

                INSERT INTO player_stats_tiers (
                    account_id,
                    instrument,
                    tiers_json,
                    updated_at)
                SELECT account_id,
                       instrument,
                       '[]'::JSONB,
                       now()
                FROM unnest(
                    @tierAccountIds::TEXT[],
                    @tierInstruments::TEXT[])
                    tier(account_id, instrument);
                """;
            command.Parameters.Add(
                "accountIds",
                NpgsqlTypes.NpgsqlDbType.Array
                | NpgsqlTypes.NpgsqlDbType.Text).Value =
                new[]
                {
                    BaseAccount,
                    OverlayAccount,
                };
            command.Parameters.Add(
                "tierAccountIds",
                NpgsqlTypes.NpgsqlDbType.Array
                | NpgsqlTypes.NpgsqlDbType.Text).Value =
                new[]
                {
                    BaseAccount,
                    BaseAccount,
                    OverlayAccount,
                    OverlayAccount,
                };
            command.Parameters.Add(
                "tierInstruments",
                NpgsqlTypes.NpgsqlDbType.Array
                | NpgsqlTypes.NpgsqlDbType.Text).Value =
                new[]
                {
                    "Overall",
                    Instrument,
                    "Overall",
                    Instrument,
                };
            command.ExecuteNonQuery();
        }

        internal void SeedUnrelatedLeaderboardRivalState()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO leaderboard_rivals (
                    user_id,
                    rival_account_id,
                    instrument,
                    rank_method,
                    direction,
                    user_rank,
                    rival_rank,
                    shared_song_count,
                    ahead_count,
                    behind_count,
                    avg_signed_delta,
                    computed_at)
                VALUES (
                    @userId,
                    'sentinel-rival',
                    @instrument,
                    'totalscore',
                    'above',
                    2,
                    1,
                    1,
                    1,
                    0,
                    -1,
                    '2026-08-01T00:00:00Z');

                INSERT INTO leaderboard_rivals_state (
                    user_id,
                    instrument,
                    rank_method,
                    user_rank,
                    computed_at)
                VALUES (
                    @userId,
                    @instrument,
                    'totalscore',
                    2,
                    '2026-08-01T00:00:00Z');

                INSERT INTO leaderboard_rival_song_samples (
                    user_id,
                    rival_account_id,
                    instrument,
                    rank_method,
                    song_id,
                    user_rank,
                    rival_rank,
                    rank_delta,
                    user_score,
                    rival_score)
                VALUES (
                    @userId,
                    'sentinel-rival',
                    @instrument,
                    'totalscore',
                    @songId,
                    2,
                    1,
                    -1,
                    50,
                    60);
                """;
            command.Parameters.AddWithValue(
                "userId",
                UnrelatedTierAccount);
            command.Parameters.AddWithValue(
                "instrument",
                PublishedOnlyInstrument);
            command.Parameters.AddWithValue(
                "songId",
                PublishedOnlySongId);
            command.ExecuteNonQuery();
        }

        internal string
            ReadUnrelatedLeaderboardRivalState()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT jsonb_build_object(
                    'rivals',
                    COALESCE(
                        (
                            SELECT jsonb_agg(
                                to_jsonb(rival)
                                ORDER BY
                                    rival.user_id,
                                    rival.rival_account_id,
                                    rival.rank_method)
                            FROM leaderboard_rivals rival
                            WHERE rival.instrument =
                                @instrument
                        ),
                        '[]'::JSONB),
                    'state',
                    COALESCE(
                        (
                            SELECT jsonb_agg(
                                to_jsonb(state)
                                ORDER BY
                                    state.user_id,
                                    state.rank_method)
                            FROM leaderboard_rivals_state state
                            WHERE state.instrument =
                                @instrument
                        ),
                        '[]'::JSONB),
                    'samples',
                    COALESCE(
                        (
                            SELECT jsonb_agg(
                                to_jsonb(sample)
                                ORDER BY
                                    sample.user_id,
                                    sample.rival_account_id,
                                    sample.rank_method,
                                    sample.song_id)
                            FROM leaderboard_rival_song_samples
                                sample
                            WHERE sample.instrument =
                                @instrument
                        ),
                        '[]'::JSONB))::TEXT
                """;
            command.Parameters.AddWithValue(
                "instrument",
                PublishedOnlyInstrument);
            return Convert.ToString(
                       command.ExecuteScalar())
                   ?? throw new InvalidOperationException(
                       "Unrelated leaderboard-rivals state was unavailable.");
        }

        internal long ReadBlankAccountIdentityCount()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*)
                     FROM score_history
                     WHERE btrim(account_id) = '')
                  + (SELECT COUNT(*)
                     FROM registered_users
                     WHERE btrim(account_id) = '')
                  + (SELECT COUNT(*)
                     FROM player_stats_tiers
                     WHERE btrim(account_id) = '')
                  + (SELECT COUNT(*)
                     FROM publication_api_response_cache
                     WHERE cache_key IN (
                         'player::::',
                         'playerstats:',
                         'history:v2:',
                         'syncstatus:',
                         'rivals-overview:',
                         'rivals-all:')
                        OR cache_key LIKE
                            'rivals-list::%'
                        OR cache_key LIKE
                            'lb-rivals::%'
                        OR cache_key LIKE
                            'neighborhood:%::5')
                """;
            return Convert.ToInt64(
                command.ExecuteScalar());
        }

        internal long
            ReadChangedInstrumentRivalStateCount()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM leaderboard_rivals_state
                WHERE instrument = @instrument
                  AND user_id = ANY(@accountIds)
                  AND rank_method = ANY(@rankMethods)
                """;
            command.Parameters.AddWithValue(
                "instrument",
                Instrument);
            command.Parameters.Add(
                "accountIds",
                NpgsqlTypes.NpgsqlDbType.Array
                | NpgsqlTypes.NpgsqlDbType.Text).Value =
                new[]
                {
                    OverlayAccount,
                    UnrelatedTierAccount,
                };
            command.Parameters.Add(
                "rankMethods",
                NpgsqlTypes.NpgsqlDbType.Array
                | NpgsqlTypes.NpgsqlDbType.Text).Value =
                LeaderboardRivalsCalculator.RankMethods;
            return Convert.ToInt64(
                command.ExecuteScalar());
        }

        internal void UpdatePublishedSnapshotScore(
            int score)
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE leaderboard_entries_snapshot
                SET score = @score
                WHERE snapshot_id = @snapshotId
                  AND song_id = @songId
                  AND instrument = @instrument
                  AND account_id = @accountId;
                """;
            command.Parameters.AddWithValue(
                "score",
                score);
            command.Parameters.AddWithValue(
                "snapshotId",
                PublishedScrapeId);
            command.Parameters.AddWithValue(
                "songId",
                SongId);
            command.Parameters.AddWithValue(
                "instrument",
                Instrument);
            command.Parameters.AddWithValue(
                "accountId",
                BaseAccount);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        internal long InsertRelevantHistoryMutation()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO score_history (
                    song_id,
                    instrument,
                    account_id,
                    old_score,
                    new_score,
                    old_rank,
                    new_rank,
                    accuracy,
                    is_full_combo,
                    stars,
                    percentile,
                    season,
                    score_achieved_at,
                    season_rank,
                    all_time_rank,
                    difficulty,
                    changed_at)
                VALUES (
                    @songId,
                    @instrument,
                    @accountId,
                    45000,
                    46000,
                    2,
                    2,
                    960000,
                    TRUE,
                    5,
                    0.45,
                    3,
                    now() - interval '1 day',
                    2,
                    2,
                    3,
                    now())
                RETURNING id
                """;
            command.Parameters.AddWithValue(
                "songId",
                SongId);
            command.Parameters.AddWithValue(
                "instrument",
                Instrument);
            command.Parameters.AddWithValue(
                "accountId",
                OverlayAccount);
            return Convert.ToInt64(
                command.ExecuteScalar());
        }

        internal void DeleteHistory(long id)
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM score_history WHERE id = @id";
            command.Parameters.AddWithValue("id", id);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        internal async Task MutateRollbackAsync(
            string mutation)
        {
            switch (mutation)
            {
                case "deleted":
                    File.Delete(RollbackPath);
                    break;
                case "corrupted":
                    await File.WriteAllTextAsync(
                        RollbackPath,
                        "{");
                    break;
                case "swapped":
                    var snapshot =
                        await MaxScoreMaintenanceFileStore
                            .LoadRollbackSnapshotAsync(
                                _dataDirectory,
                                "rollback.json",
                                CancellationToken.None);
                    await File.WriteAllBytesAsync(
                        RollbackPath,
                        (snapshot with
                        {
                            PlanDigest =
                                new string('f', 64),
                        }).SerializeCanonical());
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(mutation));
            }
        }

        internal IReadOnlyList<StagingCacheEntry>
            ReadStagingGeneration()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT cache_key, json_data, etag
                FROM publication_api_response_cache_staging
                WHERE publication_id = @publicationId
                ORDER BY cache_key
                """;
            command.Parameters.AddWithValue(
                "publicationId",
                PublicationId);
            var result = new List<StagingCacheEntry>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new StagingCacheEntry(
                    reader.GetString(0),
                    reader.GetFieldValue<byte[]>(1),
                    reader.GetString(2)));
            }
            Assert.NotEmpty(result);
            Assert.Contains(
                result,
                entry => entry.CacheKey == "firstseen");
            return result;
        }

        internal MaxScoreMaintenanceCacheEvidence
            ReadDurableCacheEvidence()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT staged_cache_evidence::TEXT
                FROM max_score_maintenance_runs
                WHERE manifest_sha256 = @manifestDigest
                """;
            command.Parameters.AddWithValue(
                "manifestDigest",
                Manifest.ComputeDigest());
            return JsonSerializer.Deserialize<
                       MaxScoreMaintenanceCacheEvidence>(
                       Convert.ToString(
                           command.ExecuteScalar())!,
                       MaxScoreMaintenanceJson.Strict)
                   ?? throw new InvalidOperationException(
                       "Durable cache evidence was not found.");
        }

        internal MaxScoreMaintenanceScoreHistoryEvidence
            ReadDurableScoreHistoryEvidence()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT score_history_evidence::TEXT
                FROM max_score_maintenance_runs
                WHERE manifest_sha256 = @manifestDigest
                """;
            command.Parameters.AddWithValue(
                "manifestDigest",
                Manifest.ComputeDigest());
            return JsonSerializer.Deserialize<
                       MaxScoreMaintenanceScoreHistoryEvidence>(
                       Convert.ToString(
                           command.ExecuteScalar())!,
                       MaxScoreMaintenanceJson.Strict)
                   ?? throw new InvalidOperationException(
                       "Durable score-history evidence was not found.");
        }

        internal async Task
            AssertCacheEvidenceWritersBlockedAsync(
                bool allowWorkingPublicationRejection = false)
        {
            var leaseError =
                await Assert.ThrowsAsync<
                    InvalidOperationException>(
                    () => Task.Run(() =>
                        {
                            using var lease = _meta
                                .AcquirePublicationCacheBuildLease(
                                    PublicationId,
                                    requireCurrentPublication: true);
                        })
                        .WaitAsync(
                            TimeSpan.FromSeconds(5)));
            Assert.Contains(
                "active max-score maintenance",
                leaseError.Message,
                StringComparison.OrdinalIgnoreCase);

            var writerError =
                await Assert.ThrowsAsync<
                    InvalidOperationException>(
                    () => Task.Run(() =>
                            _meta
                                .BulkSetCachedResponsesStaging(
                                    [],
                                    PublicationId))
                        .WaitAsync(
                            TimeSpan.FromSeconds(5)));
            Assert.True(
                writerError.Message.Contains(
                    "active max-score maintenance",
                    StringComparison.OrdinalIgnoreCase)
                || allowWorkingPublicationRejection
                && writerError.Message.Contains(
                    "working publication",
                    StringComparison.OrdinalIgnoreCase));

            var swapError =
                await Assert.ThrowsAsync<
                    InvalidOperationException>(
                    () => Task.Run(() =>
                            _meta
                                .SwapCachedResponsesFromStaging(
                                    PublicationId))
                        .WaitAsync(
                            TimeSpan.FromSeconds(5)));
            Assert.True(
                swapError.Message.Contains(
                    "active max-score maintenance",
                    StringComparison.OrdinalIgnoreCase)
                || allowWorkingPublicationRejection
                && swapError.Message.Contains(
                    "working publication",
                    StringComparison.OrdinalIgnoreCase));

            using var connection =
                _dataSource.OpenConnection();
            using var directTruncate =
                connection.CreateCommand();
            directTruncate.CommandText =
                "TRUNCATE api_response_cache_staging";
            var databaseError =
                Assert.Throws<PostgresException>(
                    () => directTruncate.ExecuteNonQuery());
            Assert.Equal(
                PostgresErrorCodes.ObjectNotInPrerequisiteState,
                databaseError.SqlState);
        }

        internal void AssertStagingGenerationEquals(
            IReadOnlyList<StagingCacheEntry> expected)
        {
            var actual = ReadStagingGeneration();
            Assert.Equal(expected.Count, actual.Count);
            for (var index = 0;
                 index < expected.Count;
                 index++)
            {
                Assert.Equal(
                    expected[index].CacheKey,
                    actual[index].CacheKey);
                Assert.Equal(
                    expected[index].ETag,
                    actual[index].ETag);
                Assert.True(
                    expected[index].JsonData
                        .AsSpan()
                        .SequenceEqual(
                            actual[index].JsonData));
            }
        }

        internal SafetyState ReadSafetyState()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT publication.public_reads_frozen,
                       run.phase,
                       run.status,
                       (
                           SELECT COUNT(*)
                           FROM publication_api_response_cache_staging
                           WHERE publication_id = @publicationId
                       ),
                       EXISTS (
                           SELECT 1
                           FROM api_response_cache
                           WHERE cache_key = 'sentinel'
                       ),
                       (
                           SELECT COUNT(*)
                           FROM player_improvement_events
                           WHERE delivery_state = 'visible'
                       ) + (
                           SELECT COUNT(*)
                           FROM band_improvement_events
                           WHERE delivery_state = 'visible'
                       )
                FROM scrape_publication_state publication
                LEFT JOIN max_score_maintenance_runs run
                  ON run.manifest_sha256 =
                     @manifestDigest
                WHERE publication.id = TRUE
                """;
            command.Parameters.AddWithValue(
                "publicationId",
                PublicationId);
            command.Parameters.AddWithValue(
                "manifestDigest",
                Manifest.ComputeDigest());
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            return new SafetyState(
                reader.GetBoolean(0),
                reader.IsDBNull(1)
                    ? null
                    : reader.GetString(1),
                reader.IsDBNull(2)
                    ? null
                    : reader.GetString(2),
                reader.GetInt64(3),
                reader.GetBoolean(4),
                reader.GetInt64(5));
        }

        internal WorkflowState ReadWorkflowState()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    publication.public_reads_frozen,
                    run.status,
                    run.phase,
                    stats.entry_count,
                    stats.max_score,
                    population.total_entries,
                    (
                        SELECT COUNT(*)
                        FROM player_improvement_events
                        WHERE delivery_state = 'visible'
                    ) + (
                        SELECT COUNT(*)
                        FROM band_improvement_events
                        WHERE delivery_state = 'visible'
                    ),
                    (
                        SELECT json_data
                        FROM api_response_cache
                        WHERE cache_key = @targetCacheKey
                    ),
                    (
                        SELECT json_data
                        FROM api_response_cache
                        WHERE cache_key = @accountCacheKey
                    )
                FROM scrape_publication_state publication
                JOIN max_score_maintenance_runs run
                  ON run.manifest_sha256 =
                     @manifestDigest
                JOIN song_stats stats
                  ON stats.song_id = @songId
                 AND stats.instrument = @instrument
                JOIN leaderboard_population population
                  ON population.song_id = @songId
                 AND population.instrument = @instrument
                WHERE publication.id = TRUE
                """;
            command.Parameters.AddWithValue(
                "manifestDigest",
                Manifest.ComputeDigest());
            command.Parameters.AddWithValue(
                "songId",
                SongId);
            command.Parameters.AddWithValue(
                "instrument",
                Instrument);
            command.Parameters.AddWithValue(
                "targetCacheKey",
                LeaderboardCacheKeys.LeaderboardAll(
                    SongId,
                    LeaderboardCacheKeys.SongDetailPreviewTop,
                    null));
            command.Parameters.AddWithValue(
                "accountCacheKey",
                $"player:{OverlayAccount}:::");
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            var targetScores =
                ReadTargetScores(
                    reader.GetFieldValue<byte[]>(7));
            var accountScores =
                ReadAccountScores(
                    reader.GetFieldValue<byte[]>(8));
            return new WorkflowState(
                reader.GetBoolean(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt64(6),
                targetScores,
                accountScores);
        }

        internal void AssertCurrentPathMatches(
            MaxScoreMaintenancePathIdentity expected)
        {
            var actual = new PathDataStore(_dataSource)
                .GetPathGenerationState(SongId);
            Assert.NotNull(actual);
            Assert.True(
                MaxScoreMaintenanceService.PathIdentityMatches(
                    actual!,
                    expected));
        }

        internal void AssertPublishedCachesMatchLive()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                WITH live AS (
                    SELECT cache_key,
                           etag,
                           encode(
                               digest(json_data, 'sha256'),
                               'hex') AS json_sha256
                    FROM api_response_cache
                ), published AS (
                    SELECT cache_key,
                           etag,
                           encode(
                               digest(json_data, 'sha256'),
                               'hex') AS json_sha256
                    FROM publication_api_response_cache
                    WHERE publication_id = @publicationId
                )
                SELECT COUNT(*)::BIGINT
                FROM live
                FULL JOIN published USING (cache_key)
                WHERE live.cache_key IS NULL
                   OR published.cache_key IS NULL
                   OR live.etag IS DISTINCT FROM published.etag
                   OR live.json_sha256 IS DISTINCT FROM
                        published.json_sha256
                """;
            command.Parameters.AddWithValue(
                "publicationId",
                PublicationId);
            Assert.Equal(
                0L,
                Convert.ToInt64(command.ExecuteScalar()));
        }

        internal string? ReadOriginalFailureStage()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT failure_stage
                FROM max_score_maintenance_runs
                WHERE manifest_sha256 = @manifestDigest
                """;
            command.Parameters.AddWithValue(
                "manifestDigest",
                Manifest.ComputeDigest());
            return command.ExecuteScalar() as string;
        }

        internal string ReadNotificationAuditIdentity()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT concat_ws(
                    ':',
                    run.notification_maintenance_run_id,
                    audit.dry_run_digest,
                    audit.candidate_count,
                    audit.player_rank_state_rows_updated)
                FROM max_score_maintenance_runs run
                JOIN improvement_notification_maintenance_runs
                    audit
                  ON audit.maintenance_run_id =
                     run.notification_maintenance_run_id
                WHERE run.manifest_sha256 =
                    @manifestDigest
                """;
            command.Parameters.AddWithValue(
                "manifestDigest",
                Manifest.ComputeDigest());
            return Convert.ToString(
                       command.ExecuteScalar())
                   ?? throw new InvalidOperationException(
                       "Notification maintenance audit identity was unavailable.");
        }

        internal int? ReadTargetSongStatsMaximum()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT max_score
                FROM song_stats
                WHERE song_id = @songId
                  AND instrument = @instrument
                """;
            command.Parameters.AddWithValue(
                "songId",
                SongId);
            command.Parameters.AddWithValue(
                "instrument",
                Instrument);
            var value = command.ExecuteScalar();
            return value is null or DBNull
                ? null
                : Convert.ToInt32(value);
        }

        internal ScopeDivergenceState
            ReadScopeDivergenceState()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    EXISTS (
                        SELECT 1
                        FROM api_response_cache
                        WHERE cache_key =
                            @publishedOnlyBaseKey
                    ),
                    EXISTS (
                        SELECT 1
                        FROM api_response_cache
                        WHERE cache_key =
                            @publishedOnlyLeewayKey
                    ),
                    EXISTS (
                        SELECT 1
                        FROM api_response_cache
                        WHERE cache_key =
                            @publishedOnlyOffsetKey
                    ),
                    EXISTS (
                        SELECT 1
                        FROM api_response_cache
                        WHERE cache_key =
                            @activeOnlyBaseKey
                    ),
                    EXISTS (
                        SELECT 1
                        FROM api_response_cache
                        WHERE cache_key =
                            @activeOnlyLeewayKey
                    ),
                    EXISTS (
                        SELECT 1
                        FROM api_response_cache
                        WHERE cache_key =
                            @activeOnlyOffsetKey
                    ),
                    (
                        SELECT json_data
                        FROM api_response_cache
                        WHERE cache_key =
                            @publishedOnlyBaseKey
                    ),
                    (
                        SELECT json_data
                        FROM api_response_cache
                        WHERE cache_key =
                            @playerStatsKey
                    ),
                    (
                        SELECT json_data
                        FROM api_response_cache
                        WHERE cache_key =
                            @unrelatedPlayerStatsKey
                    ),
                    EXISTS (
                        SELECT 1
                        FROM song_stats
                        WHERE song_id =
                                @publishedZeroSongId
                          AND instrument = @instrument
                          AND entry_count = 0
                          AND max_score = 60000
                    ),
                    EXISTS (
                        SELECT 1
                        FROM song_stats
                        WHERE song_id =
                                @activeOnlySongId
                          AND instrument = @instrument
                    ),
                    EXISTS (
                        SELECT 1
                        FROM account_rankings
                        WHERE account_id =
                                'active-only-account'
                          AND instrument = @instrument
                    ),
                    (
                        SELECT entry_count
                        FROM song_stats
                        WHERE song_id =
                                @publishedOnlySongId
                          AND instrument =
                                @publishedOnlyInstrument
                    ),
                    (
                        SELECT COALESCE(
                            MAX(total_charted_songs),
                            0)
                        FROM account_rankings
                        WHERE instrument = @instrument
                    ),
                    ARRAY(
                        SELECT instrument
                        FROM player_stats_tiers
                        WHERE account_id =
                                @affectedAccount
                        ORDER BY instrument
                    ),
                    ARRAY(
                        SELECT instrument
                        FROM player_stats_tiers
                        WHERE account_id =
                                @unrelatedAccount
                        ORDER BY instrument
                    )
                """;
            command.Parameters.AddWithValue(
                "publishedOnlyBaseKey",
                LeaderboardCacheKeys.LeaderboardAll(
                    PublishedOnlySongId,
                    LeaderboardCacheKeys
                        .SongDetailPreviewTop,
                    null));
            command.Parameters.AddWithValue(
                "publishedOnlyLeewayKey",
                LeaderboardCacheKeys.LeaderboardAll(
                    PublishedOnlySongId,
                    LeaderboardCacheKeys
                        .SongDetailPreviewTop,
                    1.0));
            command.Parameters.AddWithValue(
                "publishedOnlyOffsetKey",
                LeaderboardCacheKeys
                    .LeaderboardRankOffsets(
                        PublishedOnlySongId,
                        PublishedOnlyInstrument));
            command.Parameters.AddWithValue(
                "activeOnlyBaseKey",
                LeaderboardCacheKeys.LeaderboardAll(
                    ActiveOnlySongId,
                    LeaderboardCacheKeys
                        .SongDetailPreviewTop,
                    null));
            command.Parameters.AddWithValue(
                "activeOnlyLeewayKey",
                LeaderboardCacheKeys.LeaderboardAll(
                    ActiveOnlySongId,
                    LeaderboardCacheKeys
                        .SongDetailPreviewTop,
                    1.0));
            command.Parameters.AddWithValue(
                "activeOnlyOffsetKey",
                LeaderboardCacheKeys
                    .LeaderboardRankOffsets(
                        ActiveOnlySongId,
                        Instrument));
            command.Parameters.AddWithValue(
                "playerStatsKey",
                $"playerstats:{OverlayAccount}");
            command.Parameters.AddWithValue(
                "unrelatedPlayerStatsKey",
                $"playerstats:{UnrelatedTierAccount}");
            command.Parameters.AddWithValue(
                "publishedZeroSongId",
                PublishedZeroSongId);
            command.Parameters.AddWithValue(
                "activeOnlySongId",
                ActiveOnlySongId);
            command.Parameters.AddWithValue(
                "publishedOnlySongId",
                PublishedOnlySongId);
            command.Parameters.AddWithValue(
                "publishedOnlyInstrument",
                PublishedOnlyInstrument);
            command.Parameters.AddWithValue(
                "instrument",
                Instrument);
            command.Parameters.AddWithValue(
                "affectedAccount",
                OverlayAccount);
            command.Parameters.AddWithValue(
                "unrelatedAccount",
                UnrelatedTierAccount);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());

            using var publishedOnly =
                JsonDocument.Parse(
                    reader.GetFieldValue<byte[]>(6));
            var publishedOnlyInstruments =
                publishedOnly.RootElement
                    .GetProperty("instruments")
                    .EnumerateArray()
                    .Select(item =>
                        item.GetProperty("instrument")
                            .GetString()!)
                    .ToArray();
            using var playerStats =
                JsonDocument.Parse(
                    reader.GetFieldValue<byte[]>(7));
            using var unrelatedPlayerStats =
                JsonDocument.Parse(
                    reader.GetFieldValue<byte[]>(8));
            var instrumentRows = playerStats.RootElement
                .GetProperty("instruments")
                .EnumerateArray()
                .ToArray();
            var guitarCombo =
                ComboIds.FromInstruments([Instrument]);
            var guitarCompletion = instrumentRows
                .Single(row =>
                    row.GetProperty("ins")
                        .GetString() == guitarCombo)
                .GetProperty("tiers")[0]
                .GetProperty("cp")
                .GetDouble();
            var overallCompletion = instrumentRows
                .Single(row =>
                    row.GetProperty("ins")
                        .GetString() == "00")
                .GetProperty("tiers")[0]
                .GetProperty("cp")
                .GetDouble();
            static string[] ReadCacheInstruments(
                JsonDocument document)
                => document.RootElement
                    .GetProperty("instruments")
                    .EnumerateArray()
                    .Select(row =>
                        row.GetProperty("ins")
                            .GetString()!)
                    .OrderBy(
                        instrument => instrument,
                        StringComparer.Ordinal)
                    .ToArray();
            return new ScopeDivergenceState(
                reader.GetBoolean(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                publishedOnlyInstruments,
                reader.GetBoolean(9),
                reader.GetBoolean(10),
                reader.GetBoolean(11),
                reader.GetInt32(12),
                reader.GetInt32(13),
                ReadCacheInstruments(playerStats),
                reader.GetFieldValue<string[]>(14),
                reader.GetFieldValue<string[]>(15),
                ReadCacheInstruments(
                    unrelatedPlayerStats),
                playerStats.RootElement
                    .GetProperty("totalSongs")
                    .GetInt32(),
                guitarCompletion,
                overallCompletion);
        }

        internal string ReadRankStatsEvidence()
        {
            using var connection =
                _dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(
                    string_agg(
                        concat_ws(
                            ':',
                            stats.relname,
                            stats.n_tup_ins,
                            stats.n_tup_upd,
                            stats.n_tup_del),
                        '|' ORDER BY stats.relname),
                    '')
                FROM pg_stat_all_tables stats
                WHERE stats.schemaname = 'public'
                  AND (
                      stats.relname = 'rank_history'
                      OR stats.relname LIKE 'rank_history_%'
                      OR stats.relname = 'composite_rank_history'
                      OR stats.relname LIKE 'band_team_rank_history%'
                      OR stats.relname LIKE 'band_rank_history_%'
                  )
                """;
            return Convert.ToString(
                       command.ExecuteScalar())
                   ?? string.Empty;
        }

        public ValueTask DisposeAsync()
        {
            _persistence.Dispose();
            _meta.Dispose();
            _dataSource.Dispose();
            _loggerFactory.Dispose();
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(
                    _dataDirectory,
                    recursive: true);
            }
            return ValueTask.CompletedTask;
        }

        private string NextReportPath(string prefix)
            => $"{prefix}-{Interlocked.Increment(ref _reportNumber)}.json";

        private static void SetBlankAffectedSourceRow(
            NpgsqlDataSource dataSource,
            bool present)
        {
            using var connection =
                dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = present
                ? """
                  INSERT INTO leaderboard_entries_snapshot (
                      snapshot_id,
                      song_id,
                      instrument,
                      account_id,
                      score,
                      first_seen_at,
                      last_updated_at)
                  VALUES (
                      @snapshotId,
                      @songId,
                      @instrument,
                      '',
                      1,
                      now(),
                      now())
                  ON CONFLICT DO NOTHING
                  """
                : """
                  DELETE FROM leaderboard_entries_snapshot
                  WHERE snapshot_id = @snapshotId
                    AND song_id = @songId
                    AND instrument = @instrument
                    AND account_id = ''
                  """;
            command.Parameters.AddWithValue(
                "snapshotId",
                PublishedScrapeId);
            command.Parameters.AddWithValue(
                "songId",
                SongId);
            command.Parameters.AddWithValue(
                "instrument",
                Instrument);
            command.ExecuteNonQuery();
        }

        private static void SeedDatabase(
            NpgsqlDataSource dataSource,
            SongCatalogSnapshot catalog,
            DateTime catalogCapturedAt,
            MaxScoreMaintenancePathIdentity currentPath,
            int overlayScore)
        {
            using var connection = dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO scrape_log (
                    id,
                    started_at,
                    completed_at,
                    status,
                    songs_scraped,
                    total_entries,
                    total_requests,
                    total_bytes)
                VALUES
                    (
                        @publishedScrapeId,
                        now() - interval '2 hours',
                        now() - interval '1 hour',
                        'completed',
                        1,
                        2,
                        1,
                        1
                    ),
                    (
                        @activeScrapeId,
                        now() - interval '30 minutes',
                        now() - interval '20 minutes',
                        'completed',
                        1,
                        2,
                        1,
                        1
                    );

                INSERT INTO publication_generations (
                    publication_id,
                    scrape_id,
                    status,
                    created_at,
                    published_at)
                VALUES (
                    @publicationId,
                    @publishedScrapeId,
                    'current',
                    now() - interval '1 hour',
                    now() - interval '1 hour');

                INSERT INTO publication_song_catalog (
                    publication_id,
                    catalog_version,
                    schema_version,
                    catalog_json,
                    content_hash,
                    song_count,
                    source_kind,
                    is_exact,
                    source_captured_at,
                    captured_at)
                VALUES (
                    @publicationId,
                    1,
                    @catalogSchemaVersion,
                    @catalogJson::JSONB,
                    @catalogHash,
                    @catalogSongCount,
                    'provider_exact',
                    TRUE,
                    @catalogCapturedAt,
                    now() - interval '1 hour');

                INSERT INTO publication_surface_bindings (
                    publication_id,
                    surface_name,
                    binding_kind,
                    binding_json,
                    row_count,
                    content_hash,
                    status,
                    built_at)
                VALUES
                    (
                        @publicationId,
                        'song_catalog',
                        'generation_catalog_snapshot',
                        jsonb_build_object(
                            'table',
                            'publication_song_catalog',
                            'publicationId',
                            @publicationId),
                        @catalogSongCount,
                        @catalogHash,
                        'ready',
                        now() - interval '1 hour'
                    ),
                    (
                        @publicationId,
                        'solo_scope_sources',
                        'scrape_id',
                        jsonb_build_object(
                            'publicationId',
                            @publicationId,
                            'table',
                            'leaderboard_published_scope_source',
                            'publishedScrapeId',
                            @publishedScrapeId),
                        1,
                        NULL,
                        'ready',
                        now() - interval '1 hour'
                    );

                INSERT INTO scrape_publication_state (
                    id,
                    current_publication_id,
                    working_publication_id,
                    published_scrape_id,
                    published_at,
                    public_reads_frozen,
                    improvement_notifications_scrape_id,
                    improvement_notifications_status,
                    improvement_notifications_projection_ready,
                    improvement_notifications_projection_scrape_id,
                    updated_at)
                VALUES (
                    TRUE,
                    @publicationId,
                    NULL,
                    @publishedScrapeId,
                    now() - interval '1 hour',
                    FALSE,
                    @publishedScrapeId,
                    'completed',
                    TRUE,
                    @publishedScrapeId,
                    now())
                ON CONFLICT (id) DO UPDATE SET
                    current_publication_id =
                        EXCLUDED.current_publication_id,
                    working_publication_id = NULL,
                    published_scrape_id =
                        EXCLUDED.published_scrape_id,
                    published_at =
                        EXCLUDED.published_at,
                    public_reads_frozen = FALSE,
                    public_reads_frozen_at = NULL,
                    public_reads_frozen_scrape_id = NULL,
                    public_reads_frozen_reason = NULL,
                    improvement_notifications_scrape_id =
                        EXCLUDED.improvement_notifications_scrape_id,
                    improvement_notifications_status =
                        EXCLUDED.improvement_notifications_status,
                    improvement_notifications_projection_ready =
                        TRUE,
                    improvement_notifications_projection_scrape_id =
                        EXCLUDED.improvement_notifications_projection_scrape_id,
                    updated_at = now();

                INSERT INTO improvement_detection_runs (
                    published_scrape_id,
                    completed_at,
                    status,
                    mode,
                    baseline_only,
                    include_players,
                    include_bands,
                    include_song_events,
                    include_rankings,
                    notification_purpose,
                    delivery_state)
                VALUES (
                    @publishedScrapeId,
                    now() - interval '50 minutes',
                    'completed',
                    'execute',
                    FALSE,
                    TRUE,
                    TRUE,
                    TRUE,
                    TRUE,
                    'routine_score_observation_v1',
                    'visible');

                INSERT INTO songs (
                    song_id,
                    title,
                    last_modified,
                    max_lead_score,
                    max_bass_score,
                    dat_file_hash,
                    song_last_modified,
                    paths_generated_at,
                    chopt_version,
                    chopt_binary_sha256,
                    path_generation_profile,
                    path_artifact_generation_id,
                    path_expected_instruments,
                    path_generation_revision,
                    path_generation_pending)
                VALUES (
                    @songId,
                    'Workflow Song',
                    @lastModified,
                    @currentMaxScore,
                    @currentBassMaxScore,
                    @currentDatHash,
                    @lastModified,
                    @currentGeneratedAt,
                    @currentChoptVersion,
                    @currentBinaryHash,
                    @currentProfile,
                    @currentGenerationId,
                    @currentExpectedInstruments,
                    @currentRevision,
                    FALSE);

                INSERT INTO leaderboard_entries_snapshot (
                    snapshot_id,
                    song_id,
                    instrument,
                    account_id,
                    score,
                    accuracy,
                    is_full_combo,
                    stars,
                    season,
                    difficulty,
                    percentile,
                    rank,
                    api_rank,
                    source,
                    first_seen_at,
                    last_updated_at)
                VALUES
                    (
                        @publishedScrapeId,
                        @songId,
                        @instrument,
                        @baseAccount,
                        40000,
                        950000,
                        TRUE,
                        5,
                        3,
                        3,
                        0.5,
                        1,
                        1,
                        'scrape',
                        now() - interval '1 day',
                        now() - interval '1 hour'
                    ),
                    (
                        @activeScrapeId,
                        @songId,
                        @instrument,
                        @baseAccount,
                        90000,
                        990000,
                        TRUE,
                        6,
                        3,
                        3,
                        0.1,
                        1,
                        1,
                        'scrape',
                        now() - interval '1 day',
                        now() - interval '20 minutes'
                    );

                INSERT INTO leaderboard_snapshot_state (
                    song_id,
                    instrument,
                    active_snapshot_id,
                    scrape_id,
                    is_finalized,
                    updated_at)
                VALUES (
                    @songId,
                    @instrument,
                    @activeScrapeId,
                    @activeScrapeId,
                    TRUE,
                    now());

                INSERT INTO leaderboard_published_scope_source (
                    published_scrape_id,
                    song_id,
                    instrument,
                    scope_kind,
                    source_kind,
                    source_snapshot_id,
                    source_scrape_id,
                    row_count,
                    content_fingerprint,
                    coverage_fingerprint,
                    reported_total_entries,
                    reported_total_pages,
                    is_complete,
                    created_at,
                    validated_at)
                VALUES (
                    @publishedScrapeId,
                    @songId,
                    @instrument,
                    'alltime',
                    'snapshot',
                    @publishedScrapeId,
                    @publishedScrapeId,
                    1,
                    md5('workflow-published-source'),
                    md5('workflow-published-coverage'),
                    100,
                    1,
                    TRUE,
                    now() - interval '1 hour',
                    now() - interval '1 hour');

                INSERT INTO leaderboard_entries_overlay (
                    song_id,
                    instrument,
                    account_id,
                    score,
                    accuracy,
                    is_full_combo,
                    stars,
                    season,
                    difficulty,
                    percentile,
                    rank,
                    api_rank,
                    source,
                    first_seen_at,
                    last_updated_at,
                    source_priority,
                    overlay_reason)
                VALUES (
                    @songId,
                    @instrument,
                    @overlayAccount,
                    @overlayScore,
                    970000,
                    TRUE,
                    5,
                    3,
                    3,
                    0.4,
                    2,
                    2,
                    'backfill',
                    now() - interval '1 day',
                    now() - interval '40 minutes',
                    200,
                    'workflow-test');

                INSERT INTO leaderboard_entries (
                    song_id,
                    instrument,
                    account_id,
                    score,
                    accuracy,
                    is_full_combo,
                    stars,
                    season,
                    difficulty,
                    percentile,
                    rank,
                    api_rank,
                    source,
                    first_seen_at,
                    last_updated_at)
                VALUES
                    (
                        @songId,
                        @instrument,
                        @baseAccount,
                        90000,
                        990000,
                        TRUE,
                        6,
                        3,
                        3,
                        0.1,
                        1,
                        1,
                        'scrape',
                        now() - interval '1 day',
                        now() - interval '20 minutes'
                    ),
                    (
                        @songId,
                        @instrument,
                        @overlayAccount,
                        @overlayScore,
                        970000,
                        TRUE,
                        5,
                        3,
                        3,
                        0.4,
                        2,
                        2,
                        'backfill',
                        now() - interval '1 day',
                        now() - interval '40 minutes'
                    );

                INSERT INTO solo_current_projection_scope (
                    song_id,
                    instrument,
                    projection_generation,
                    row_count,
                    source_snapshot_id,
                    status,
                    updated_at)
                VALUES (
                    @songId,
                    @instrument,
                    1,
                    2,
                    @activeScrapeId,
                    'ready',
                    now());

                INSERT INTO current_leaderboard_entries (
                    song_id,
                    instrument,
                    account_id,
                    score,
                    accuracy,
                    is_full_combo,
                    stars,
                    season,
                    difficulty,
                    percentile,
                    rank,
                    api_rank,
                    source,
                    first_seen_at,
                    last_updated_at,
                    projection_generation,
                    computed_at)
                VALUES
                    (
                        @songId,
                        @instrument,
                        @baseAccount,
                        90000,
                        990000,
                        TRUE,
                        6,
                        3,
                        3,
                        0.1,
                        1,
                        1,
                        'projection',
                        now() - interval '1 day',
                        now() - interval '20 minutes',
                        1,
                        now()
                    ),
                    (
                        @songId,
                        @instrument,
                        @overlayAccount,
                        @overlayScore,
                        970000,
                        TRUE,
                        5,
                        3,
                        3,
                        0.4,
                        2,
                        2,
                        'projection',
                        now() - interval '1 day',
                        now() - interval '40 minutes',
                        1,
                        now()
                    );

                INSERT INTO leaderboard_population (
                    song_id,
                    instrument,
                    total_entries,
                    updated_at)
                VALUES (
                    @songId,
                    @instrument,
                    200,
                    now());

                INSERT INTO score_history (
                    song_id,
                    instrument,
                    account_id,
                    old_score,
                    new_score,
                    old_rank,
                    new_rank,
                    accuracy,
                    is_full_combo,
                    stars,
                    percentile,
                    season,
                    score_achieved_at,
                    season_rank,
                    all_time_rank,
                    difficulty,
                    changed_at)
                VALUES (
                    @songId,
                    @instrument,
                    @overlayAccount,
                    40000,
                    45000,
                    3,
                    2,
                    950000,
                    TRUE,
                    5,
                    0.5,
                    3,
                    now() - interval '2 days',
                    2,
                    2,
                    3,
                    now() - interval '2 days');

                INSERT INTO player_improvement_state (
                    account_id,
                    song_id,
                    instrument,
                    score,
                    rank,
                    stars,
                    is_full_combo,
                    difficulty,
                    percentile,
                    season,
                    first_seen_at,
                    last_updated_at,
                    observed_at,
                    updated_at)
                VALUES (
                    @overlayAccount,
                    @songId,
                    @instrument,
                    @overlayScore,
                    2,
                    5,
                    TRUE,
                    3,
                    0.4,
                    3,
                    now() - interval '1 day',
                    now() - interval '40 minutes',
                    now() - interval '50 minutes',
                    now() - interval '50 minutes');

                INSERT INTO api_response_cache (
                    cache_key,
                    json_data,
                    etag,
                    cached_at)
                VALUES (
                    'sentinel',
                    convert_to('{"old":true}', 'UTF8'),
                    'sentinel-etag',
                    now());
                """;
            command.Parameters.AddWithValue(
                "publishedScrapeId",
                PublishedScrapeId);
            command.Parameters.AddWithValue(
                "activeScrapeId",
                ActiveScrapeId);
            command.Parameters.AddWithValue(
                "publicationId",
                PublicationId);
            command.Parameters.AddWithValue(
                "catalogSchemaVersion",
                SongCatalogSnapshotBuilder.SchemaVersion);
            command.Parameters.AddWithValue(
                "catalogJson",
                catalog.CatalogJson);
            command.Parameters.AddWithValue(
                "catalogHash",
                catalog.ContentHash);
            command.Parameters.AddWithValue(
                "catalogSongCount",
                catalog.SongCount);
            command.Parameters.AddWithValue(
                "catalogCapturedAt",
                catalogCapturedAt);
            command.Parameters.AddWithValue(
                "songId",
                SongId);
            command.Parameters.AddWithValue(
                "instrument",
                Instrument);
            command.Parameters.AddWithValue(
                "baseAccount",
                BaseAccount);
            command.Parameters.AddWithValue(
                "overlayAccount",
                OverlayAccount);
            command.Parameters.AddWithValue(
                "overlayScore",
                overlayScore);
            command.Parameters.AddWithValue(
                "lastModified",
                currentPath.SongLastModified!);
            command.Parameters.AddWithValue(
                "currentMaxScore",
                (object?)currentPath.Maxima.Lead
                ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "currentBassMaxScore",
                (object?)currentPath.Maxima.Bass
                ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "currentDatHash",
                currentPath.DatFileHash!);
            command.Parameters.AddWithValue(
                "currentGeneratedAt",
                currentPath.GeneratedAtUtc!.Value);
            command.Parameters.AddWithValue(
                "currentChoptVersion",
                currentPath.ChoptVersion!);
            command.Parameters.AddWithValue(
                "currentBinaryHash",
                currentPath.ChoptBinarySha256!);
            command.Parameters.AddWithValue(
                "currentProfile",
                currentPath.GenerationProfile!);
            command.Parameters.AddWithValue(
                "currentGenerationId",
                currentPath.ArtifactGenerationId!);
            command.Parameters.AddWithValue(
                "currentRevision",
                currentPath.Revision);
            command.Parameters.Add(
                "currentExpectedInstruments",
                NpgsqlTypes.NpgsqlDbType.Array
                | NpgsqlTypes.NpgsqlDbType.Text).Value =
                currentPath.ExpectedInstruments.ToArray();
            command.ExecuteNonQuery();
        }

        private static void SeedRollbackOnlyHistoryEvidence(
            NpgsqlDataSource dataSource)
        {
            using var connection = dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO leaderboard_entries_overlay (
                    song_id,
                    instrument,
                    account_id,
                    score,
                    accuracy,
                    is_full_combo,
                    stars,
                    season,
                    difficulty,
                    percentile,
                    rank,
                    api_rank,
                    source,
                    first_seen_at,
                    last_updated_at,
                    source_priority,
                    overlay_reason)
                VALUES (
                    @songId,
                    @instrument,
                    'rollback-only-history',
                    60000,
                    960000,
                    TRUE,
                    5,
                    3,
                    3,
                    0.3,
                    3,
                    3,
                    'backfill',
                    now() - interval '2 days',
                    now() - interval '1 hour',
                    200,
                    'rollback-evidence-test');

                INSERT INTO score_history (
                    song_id,
                    instrument,
                    account_id,
                    old_score,
                    new_score,
                    old_rank,
                    new_rank,
                    accuracy,
                    is_full_combo,
                    stars,
                    percentile,
                    season,
                    score_achieved_at,
                    season_rank,
                    all_time_rank,
                    difficulty,
                    changed_at)
                VALUES (
                    @songId,
                    @instrument,
                    'rollback-only-history',
                    56000,
                    57000,
                    4,
                    3,
                    950000,
                    TRUE,
                    5,
                    0.4,
                    3,
                    now() - interval '3 days',
                    3,
                    3,
                    3,
                    now() - interval '3 days');
                """;
            command.Parameters.AddWithValue(
                "songId",
                SongId);
            command.Parameters.AddWithValue(
                "instrument",
                Instrument);
            Assert.Equal(2, command.ExecuteNonQuery());
        }

        private static void SeedScopeDivergence(
            NpgsqlDataSource dataSource,
            int publishedOnlyMaximum)
        {
            using var connection =
                dataSource.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO songs (
                    song_id,
                    title,
                    last_modified,
                    max_bass_score,
                    path_generation_revision,
                    path_generation_pending)
                VALUES (
                    @publishedOnlySongId,
                    'Published Only Song',
                    '2026-07-15T00:00:00Z',
                    @publishedOnlyMaximum,
                    0,
                    FALSE);

                INSERT INTO songs (
                    song_id,
                    title,
                    last_modified,
                    max_lead_score,
                    path_generation_revision,
                    path_generation_pending)
                VALUES (
                    @publishedZeroSongId,
                    'Published Zero Song',
                    '2026-07-20T00:00:00Z',
                    60000,
                    0,
                    FALSE);

                INSERT INTO songs (
                    song_id,
                    title,
                    last_modified,
                    max_lead_score,
                    path_generation_revision,
                    path_generation_pending)
                VALUES (
                    @activeOnlySongId,
                    'Active Only Song',
                    '2026-08-10T00:00:00Z',
                    80000,
                    0,
                    FALSE);

                INSERT INTO leaderboard_entries_snapshot (
                    snapshot_id,
                    song_id,
                    instrument,
                    account_id,
                    score,
                    accuracy,
                    is_full_combo,
                    stars,
                    season,
                    difficulty,
                    percentile,
                    rank,
                    api_rank,
                    source,
                    first_seen_at,
                    last_updated_at)
                VALUES (
                    @publishedScrapeId,
                    @publishedOnlySongId,
                    @publishedOnlyInstrument,
                    'published-only-account',
                    65000,
                    950000,
                    TRUE,
                    5,
                    3,
                    3,
                    0.5,
                    1,
                    1,
                    'scrape',
                    now() - interval '1 day',
                    now() - interval '1 hour');

                INSERT INTO leaderboard_published_scope_source (
                    published_scrape_id,
                    song_id,
                    instrument,
                    scope_kind,
                    source_kind,
                    source_snapshot_id,
                    source_scrape_id,
                    row_count,
                    content_fingerprint,
                    coverage_fingerprint,
                    reported_total_entries,
                    reported_total_pages,
                    is_complete,
                    created_at,
                    validated_at)
                VALUES
                    (
                        @publishedScrapeId,
                        @publishedOnlySongId,
                        @publishedOnlyInstrument,
                        'alltime',
                        'snapshot',
                        @publishedScrapeId,
                        @publishedScrapeId,
                        1,
                        md5('published-only-source'),
                        md5('published-only-coverage'),
                        50,
                        1,
                        TRUE,
                        now() - interval '1 hour',
                        now() - interval '1 hour'
                    ),
                    (
                        @publishedScrapeId,
                        @publishedZeroSongId,
                        @instrument,
                        'alltime',
                        'empty',
                        NULL,
                        @publishedScrapeId,
                        0,
                        md5('published-zero-source'),
                        md5('published-zero-coverage'),
                        0,
                        0,
                        TRUE,
                        now() - interval '1 hour',
                        now() - interval '1 hour'
                    );

                INSERT INTO leaderboard_entries (
                    song_id,
                    instrument,
                    account_id,
                    score,
                    accuracy,
                    is_full_combo,
                    stars,
                    season,
                    difficulty,
                    percentile,
                    rank,
                    api_rank,
                    source,
                    first_seen_at,
                    last_updated_at)
                VALUES (
                    @activeOnlySongId,
                    @instrument,
                    'active-only-account',
                    75000,
                    950000,
                    TRUE,
                    5,
                    3,
                    3,
                    0.5,
                    1,
                    1,
                    'scrape',
                    now() - interval '1 day',
                    now() - interval '20 minutes');

                INSERT INTO song_stats (
                    song_id,
                    instrument,
                    entry_count,
                    previous_entry_count,
                    log_weight,
                    max_score,
                    computed_at)
                VALUES
                    (
                        @activeOnlySongId,
                        @instrument,
                        1,
                        1,
                        0,
                        80000,
                        now()
                    ),
                    (
                        @publishedOnlySongId,
                        @publishedOnlyInstrument,
                        50,
                        49,
                        log(2, 50),
                        70000,
                        now() - interval '1 day'
                    );

                INSERT INTO account_rankings (
                    account_id,
                    instrument,
                    songs_played,
                    total_charted_songs,
                    coverage,
                    raw_skill_rating,
                    adjusted_skill_rating,
                    adjusted_skill_rank,
                    weighted_rating,
                    weighted_rank,
                    fc_rate,
                    fc_rate_rank,
                    total_score,
                    total_score_rank,
                    max_score_percent,
                    max_score_percent_rank,
                    avg_accuracy,
                    full_combo_count,
                    avg_stars,
                    best_rank,
                    avg_rank,
                    computed_at)
                VALUES (
                    'active-only-account',
                    @instrument,
                    1,
                    1,
                    1,
                    0.01,
                    0.01,
                    1,
                    0.01,
                    1,
                    1,
                    1,
                    75000,
                    1,
                    0.9375,
                    1,
                    950000,
                    1,
                    5,
                    1,
                    1,
                    now());

                INSERT INTO player_stats_tiers (
                    account_id,
                    instrument,
                    tiers_json,
                    updated_at)
                VALUES
                    (
                        @overlayAccount,
                        'Solo_Drums',
                        '[]'::JSONB,
                        now() - interval '1 day'
                    ),
                    (
                        @unrelatedTierAccount,
                        'Overall',
                        '[]'::JSONB,
                        now() - interval '1 day'
                    ),
                    (
                        @unrelatedTierAccount,
                        @publishedOnlyInstrument,
                        '[]'::JSONB,
                        now() - interval '1 day'
                    ),
                    (
                        @unrelatedTierAccount,
                        'Solo_Drums',
                        '[]'::JSONB,
                        now() - interval '1 day'
                    );

                INSERT INTO leaderboard_population (
                    song_id,
                    instrument,
                    total_entries,
                    updated_at)
                VALUES (
                    @activeOnlySongId,
                    @instrument,
                    300,
                    now());

                UPDATE publication_surface_bindings binding
                SET row_count = (
                    SELECT COUNT(*)
                    FROM leaderboard_published_scope_source source
                    WHERE source.published_scrape_id =
                              @publishedScrapeId
                      AND source.scope_kind = 'alltime'
                )
                FROM scrape_publication_state state
                WHERE state.id = TRUE
                  AND state.published_scrape_id =
                          @publishedScrapeId
                  AND binding.publication_id =
                          state.current_publication_id
                  AND binding.surface_name =
                          'solo_scope_sources';
                """;
            command.Parameters.AddWithValue(
                "publishedScrapeId",
                PublishedScrapeId);
            command.Parameters.AddWithValue(
                "publishedOnlySongId",
                PublishedOnlySongId);
            command.Parameters.AddWithValue(
                "publishedOnlyInstrument",
                PublishedOnlyInstrument);
            command.Parameters.AddWithValue(
                "publishedOnlyMaximum",
                publishedOnlyMaximum);
            command.Parameters.AddWithValue(
                "publishedZeroSongId",
                PublishedZeroSongId);
            command.Parameters.AddWithValue(
                "activeOnlySongId",
                ActiveOnlySongId);
            command.Parameters.AddWithValue(
                "instrument",
                Instrument);
            command.Parameters.AddWithValue(
                "overlayAccount",
                OverlayAccount);
            command.Parameters.AddWithValue(
                "unrelatedTierAccount",
                UnrelatedTierAccount);
            command.ExecuteNonQuery();
        }

        private static void WriteGeneration(
            string dataDirectory,
            string songId,
            MaxScoreMaintenancePathIdentity identity)
        {
            var generationDirectory =
                PathArtifactResolver.GetGenerationDirectory(
                    dataDirectory,
                    songId,
                    identity.ArtifactGenerationId!);
            Directory.CreateDirectory(generationDirectory);
            var expertScores =
                identity.ExpectedInstruments.ToDictionary(
                    instrument => instrument,
                    instrument => identity.Maxima
                        .GetByInstrument(instrument)!.Value,
                    StringComparer.Ordinal);
            var manifest = new PathArtifactManifest(
                identity.ArtifactGenerationId!,
                songId,
                identity.DatFileHash!,
                identity.SongLastModified,
                identity.ChoptVersion!,
                identity.ChoptBinarySha256!,
                identity.GenerationProfile!,
                identity.ExpectedInstruments.ToArray(),
                expertScores,
                identity.GeneratedAtUtc!.Value);
            File.WriteAllText(
                Path.Combine(
                    generationDirectory,
                    PathArtifactResolver.ManifestFileName),
                JsonSerializer.Serialize(
                    manifest,
                    PathArtifactManifest.JsonOptions));
            var png =
                Convert.FromBase64String(
                    ValidPngBase64);
            foreach (var instrument in
                     identity.ExpectedInstruments)
            {
                var instrumentDirectory = Path.Combine(
                    generationDirectory,
                    instrument);
                Directory.CreateDirectory(
                    instrumentDirectory);
                foreach (var difficulty in
                         PathGenerationInstruments
                             .Difficulties)
                {
                    File.WriteAllBytes(
                        Path.Combine(
                            instrumentDirectory,
                            $"{difficulty}.png"),
                        png);
                    File.WriteAllText(
                        Path.Combine(
                            instrumentDirectory,
                            $"{difficulty}.json"),
                        BuildPathJson(
                            difficulty,
                            difficulty == "expert"
                                ? expertScores[instrument]
                                : 0,
                            instrument));
                }
            }
        }

        private static string BuildPathJson(
            string difficulty,
            int totalScore,
            string instrument)
            => JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                songName = "Workflow Song",
                artist = "Workflow Artist",
                charter = "Workflow Charter",
                difficulty,
                totalScore,
                pathSummary = string.Empty,
                activations = Array.Empty<object>(),
                notes = new[]
                {
                    new
                    {
                        beat = 1,
                        seconds = 0.5,
                        isSpNote = false,
                        frets =
                            new Dictionary<string, int>
                            {
                                [instrument] = 1,
                            },
                    },
                },
                spPhrases = Array.Empty<object>(),
                measures = Array.Empty<object>(),
                bpms = Array.Empty<object>(),
                timeSignatures = Array.Empty<object>(),
                drumFills = Array.Empty<object>(),
            });

        private static int[] ReadTargetScores(byte[] payload)
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement
                .GetProperty("instruments")
                .EnumerateArray()
                .Single(item =>
                    item.GetProperty("instrument")
                        .GetString() == Instrument)
                .GetProperty("entries")
                .EnumerateArray()
                .Select(entry =>
                    entry.GetProperty("score").GetInt32())
                .Order()
                .ToArray();
        }

        private static int[] ReadAccountScores(byte[] payload)
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement
                .GetProperty("scores")
                .EnumerateArray()
                .Select(score =>
                    score.GetProperty("sc").GetInt32())
                .Order()
                .ToArray();
        }
    }

    private sealed record WorkflowState(
        bool Frozen,
        string RunStatus,
        string RunPhase,
        int SongStatsPopulation,
        int SongStatsMaximum,
        int MutablePopulation,
        long VisibleNotifications,
        IReadOnlyList<int> TargetCacheScores,
        IReadOnlyList<int> OverlayAccountCacheScores);

    private sealed record SafetyState(
        bool Frozen,
        string? RunPhase,
        string? RunStatus,
        long StagedCacheRows,
        bool LiveSentinelPresent,
        long VisibleNotifications);

    private sealed record StagingCacheEntry(
        string CacheKey,
        byte[] JsonData,
        string ETag);

    private sealed record ScopeDivergenceState(
        bool PublishedOnlyBaseKeyPresent,
        bool PublishedOnlyLeewayKeyPresent,
        bool PublishedOnlyOffsetKeyPresent,
        bool ActiveOnlyBaseKeyPresent,
        bool ActiveOnlyLeewayKeyPresent,
        bool ActiveOnlyOffsetKeyPresent,
        IReadOnlyList<string> PublishedOnlyInstruments,
        bool ZeroEntryPublishedStatsPresent,
        bool ActiveOnlyStatsPresent,
        bool ActiveOnlyRankingPresent,
        int UnaffectedBassEntryCount,
        int GuitarRankingSongCount,
        IReadOnlyList<string> AffectedAccountCacheInstruments,
        IReadOnlyList<string> AffectedAccountDatabaseInstruments,
        IReadOnlyList<string> UnrelatedAccountDatabaseInstruments,
        IReadOnlyList<string> UnrelatedAccountCacheInstruments,
        int TotalSongs,
        double GuitarCompletionPercent,
        double OverallCompletionPercent);
}
