using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class DurablePhaseProgressSinkTests
{
    [Fact]
    public void Attach_interrupts_orphans_and_start_persists_immediately()
    {
        var (sink, metaDb, clock) = CreateSink();

        sink.AttachScrape(42, "instance-b");
        var view = sink.StartPhase(PhaseProgressCatalog.All[0], "fetching_leaderboards");

        metaDb.Received(1).InterruptOrphanedScrapePhaseAttempts(
            "instance-b",
            clock.UtcNow,
            Arg.Any<string>());
        metaDb.Received(1).StartScrapePhaseAttempt(
            Arg.Is<ScrapePhaseAttemptStart>(start =>
                start.ScrapeId == 42
                && start.PhaseId == "scrape.leaderboards"
                && start.CurrentSubphaseId == "fetching_leaderboards"
                && start.LastProgressAtUtc == clock.UtcNow));
        Assert.NotNull(view);
        Assert.Equal(1, view!.Attempt);
    }

    [Fact]
    public void CurrentProjectionCandidateChangesDurableConfigurationIdentity()
    {
        var baseline = CaptureConfigId(enabled: false);
        var candidate = CaptureConfigId(enabled: true);

        Assert.StartsWith("sha256:", baseline, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", candidate, StringComparison.Ordinal);
        Assert.NotEqual(baseline, candidate);
    }

    [Fact]
    public void Reattaching_same_scrape_and_instance_is_idempotent()
    {
        var (sink, metaDb, _) = CreateSink();
        sink.AttachScrape(42, "instance-a");
        var first = sink.StartPhase(PhaseProgressCatalog.All[0]);
        metaDb.ClearReceivedCalls();

        sink.AttachScrape(42, "instance-a");
        var second = sink.StartPhase(PhaseProgressCatalog.All[0]);

        Assert.Equal(first, second);
        metaDb.DidNotReceive().InterruptOrphanedScrapePhaseAttempts(
            Arg.Any<string>(),
            Arg.Any<DateTime>(),
            Arg.Any<string>());
        metaDb.DidNotReceive().StartScrapePhaseAttempt(
            Arg.Any<ScrapePhaseAttemptStart>());
    }

    [Fact]
    public void Active_progress_is_meaningful_and_coalesced_to_five_seconds()
    {
        var (sink, metaDb, clock) = CreateSink();
        sink.AttachScrape(42, "instance-a");
        sink.StartPhase(PhaseProgressCatalog.All[0], "fetching_leaderboards");
        metaDb.ClearReceivedCalls();

        var first = sink.ObserveTracker(ScrapingSnapshot(1, 10));
        Assert.Empty(first);
        metaDb.DidNotReceive().UpdateScrapePhaseAttemptProgress(
            Arg.Any<ScrapePhaseAttemptProgress>());

        clock.Advance(TimeSpan.FromSeconds(5));
        var second = sink.ObserveTracker(ScrapingSnapshot(2, 10));
        var view = Assert.Single(second);
        Assert.Equal(20, view.PhasePercent);
        metaDb.Received(1).UpdateScrapePhaseAttemptProgress(
            Arg.Is<ScrapePhaseAttemptProgress>(progress =>
                progress.UnitsCompleted == 2
                && progress.UnitsTotal == 10
                && progress.UnitsTotalFinal
                && progress.PhasePercent == 20));

        metaDb.ClearReceivedCalls();
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(sink.ObserveTracker(ScrapingSnapshot(2, 10)));
        metaDb.DidNotReceive().UpdateScrapePhaseAttemptProgress(
            Arg.Any<ScrapePhaseAttemptProgress>());
    }

    [Fact]
    public void Pending_progress_flushes_after_interval_without_new_advancement()
    {
        var (sink, metaDb, clock) = CreateSink();
        sink.AttachScrape(42, "instance-a");
        sink.StartPhase(PhaseProgressCatalog.All[0], "fetching_leaderboards");
        metaDb.ClearReceivedCalls();

        Assert.Empty(sink.ObserveTracker(ScrapingSnapshot(1, 10)));
        var progressedAt = clock.UtcNow;
        clock.Advance(TimeSpan.FromSeconds(5));

        var view = Assert.Single(sink.ObserveTracker(ScrapingSnapshot(1, 10)));

        Assert.Equal(progressedAt, view.LastProgressAtUtc);
        metaDb.Received(1).UpdateScrapePhaseAttemptProgress(
            Arg.Is<ScrapePhaseAttemptProgress>(progress =>
                progress.UnitsCompleted == 1
                && progress.LastProgressAtUtc == progressedAt
                && progress.HeartbeatAtUtc == clock.UtcNow));

        metaDb.ClearReceivedCalls();
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(sink.ObserveTracker(ScrapingSnapshot(1, 10)));
        metaDb.DidNotReceive().UpdateScrapePhaseAttemptProgress(
            Arg.Any<ScrapePhaseAttemptProgress>());
    }

    [Fact]
    public void Terminal_transition_flushes_pending_progress_before_completion()
    {
        var (sink, metaDb, clock) = CreateSink();
        sink.AttachScrape(42, "instance-a");
        sink.StartPhase(PhaseProgressCatalog.All[0], "fetching_leaderboards");
        metaDb.ClearReceivedCalls();

        Assert.Empty(sink.ObserveTracker(ScrapingSnapshot(10, 10)));
        sink.CompletePhase("scrape.leaderboards", "completed");

        Received.InOrder(() =>
        {
            metaDb.UpdateScrapePhaseAttemptProgress(
                Arg.Is<ScrapePhaseAttemptProgress>(progress =>
                    progress.UnitsCompleted == 10
                    && progress.UnitsTotal == 10
                    && progress.UnitsTotalFinal
                    && progress.PhasePercent == 100));
            metaDb.CompleteScrapePhaseAttempt(
                Arg.Is<ScrapePhaseAttemptCompletion>(completion =>
                    completion.Status == "completed"
                    && completion.CompletedAtUtc == clock.UtcNow));
        });
    }

    [Fact]
    public void Subphase_transition_persists_immediately()
    {
        var (sink, metaDb, _) = CreateSink();
        sink.AttachScrape(42, "instance-a");
        sink.StartPhase(PhaseProgressCatalog.All[0], "fetching_leaderboards");
        metaDb.ClearReceivedCalls();

        var view = sink.TransitionSubphase(
            "scrape.leaderboards",
            "persisting_scores");

        Assert.Equal("persisting_scores", view?.SubphaseId);
        metaDb.Received(1).UpdateScrapePhaseAttemptProgress(
            Arg.Is<ScrapePhaseAttemptProgress>(progress =>
                progress.CurrentSubphaseId == "persisting_scores"));
    }

    [Fact]
    public void Heartbeat_does_not_advance_last_progress()
    {
        var (sink, metaDb, clock) = CreateSink();
        sink.AttachScrape(42, "instance-a");
        sink.StartPhase(PhaseProgressCatalog.All[0]);
        metaDb.ClearReceivedCalls();
        clock.Advance(TimeSpan.FromSeconds(15));

        sink.Heartbeat();

        metaDb.Received(1).HeartbeatScrapePhaseAttempts(
            42,
            "instance-a",
            clock.UtcNow);
        metaDb.DidNotReceive().UpdateScrapePhaseAttemptProgress(
            Arg.Any<ScrapePhaseAttemptProgress>());
    }

    [Fact]
    public void Unknown_denominator_suppresses_exact_phase_percent()
    {
        var (sink, metaDb, clock) = CreateSink();
        sink.AttachScrape(42, "instance-a");
        var descriptor = PhaseProgressCatalog.FindPostScrape("ComputeRankings")!;
        sink.StartPhase(descriptor);
        metaDb.ClearReceivedCalls();
        clock.Advance(TimeSpan.FromSeconds(5));

        var writes = sink.ObserveTracker(new OperationSnapshot
        {
            Operation = "ComputingRankings",
            WorkItems = new ProgressCounter { Completed = 5, Total = 10 },
            WorkItemsTotalFinal = false,
        });

        var view = Assert.Single(writes);
        Assert.Null(view.PhasePercent);
        metaDb.Received(1).UpdateScrapePhaseAttemptProgress(
            Arg.Is<ScrapePhaseAttemptProgress>(progress =>
                !progress.UnitsTotalFinal
                && progress.PhasePercent == null));
    }

    [Fact]
    public void Denominator_becoming_nonfinal_clears_previous_exact_percent()
    {
        var (sink, metaDb, clock) = CreateSink();
        sink.AttachScrape(42, "instance-a");
        var descriptor = PhaseProgressCatalog.FindPostScrape("ComputeRankings")!;
        sink.StartPhase(descriptor);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(
            50,
            Assert.Single(sink.ObserveTracker(new OperationSnapshot
            {
                Operation = "ComputingRankings",
                WorkItems = new ProgressCounter { Completed = 5, Total = 10 },
                WorkItemsTotalFinal = true,
            })).PhasePercent);
        metaDb.ClearReceivedCalls();
        clock.Advance(TimeSpan.FromSeconds(5));

        var view = Assert.Single(sink.ObserveTracker(new OperationSnapshot
        {
            Operation = "ComputingRankings",
            WorkItems = new ProgressCounter { Completed = 5, Total = 15 },
            WorkItemsTotalFinal = false,
        }));

        Assert.False(view.UnitsTotalFinal);
        Assert.Null(view.PhasePercent);
        metaDb.Received(1).UpdateScrapePhaseAttemptProgress(
            Arg.Is<ScrapePhaseAttemptProgress>(progress =>
                !progress.UnitsTotalFinal
                && progress.UnitsTotal == 15
                && progress.PhasePercent == null));
    }

    [Fact]
    public void Concurrent_branch_progress_updates_distinct_attempts()
    {
        var (sink, metaDb, clock) = CreateSink();
        sink.AttachScrape(42, "instance-a");
        sink.StartPhase(PhaseProgressCatalog.FindPostScrape("RankRecompute")!);
        sink.StartPhase(PhaseProgressCatalog.FindPostScrape("FirstSeenSeason")!);
        metaDb.ClearReceivedCalls();
        clock.Advance(TimeSpan.FromSeconds(5));

        var writes = sink.ObserveTracker(new OperationSnapshot
        {
            Operation = "PostScrapeEnrichment",
            SubOperation = "enriching_parallel_tail",
            Branches =
            [
                new BranchProgress
                {
                    Id = "rank_recompute",
                    Status = "running",
                    Completed = 4,
                    Total = 8,
                },
                new BranchProgress
                {
                    Id = "first_seen",
                    Status = "running",
                    Completed = 2,
                    Total = 10,
                },
            ],
        });

        Assert.Equal(2, writes.Count);
        metaDb.Received(1).UpdateScrapePhaseAttemptProgress(
            Arg.Is<ScrapePhaseAttemptProgress>(progress =>
                progress.PhaseId == "post.rank_recompute"
                && progress.PhasePercent == 50));
        metaDb.Received(1).UpdateScrapePhaseAttemptProgress(
            Arg.Is<ScrapePhaseAttemptProgress>(progress =>
                progress.PhaseId == "post.first_seen_season"
                && progress.PhasePercent == 20));
    }

    [Fact]
    public void Persistence_failure_is_nonblocking()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.InterruptOrphanedScrapePhaseAttempts(
                Arg.Any<string>(),
                Arg.Any<DateTime>(),
                Arg.Any<string>())
            .Returns(_ => throw new InvalidOperationException("database unavailable"));
        metaDb.StartScrapePhaseAttempt(Arg.Any<ScrapePhaseAttemptStart>())
            .Returns(_ => throw new InvalidOperationException("database unavailable"));
        var sink = new DurablePhaseProgressSink(
            metaDb,
            BuildConfiguration(),
            NullLogger<DurablePhaseProgressSink>.Instance,
            new FakePhaseProgressClock());

        var exception = Record.Exception(() =>
        {
            sink.AttachScrape(42, "instance-a");
            sink.StartPhase(PhaseProgressCatalog.All[0]);
            sink.Heartbeat();
            sink.CompletePhase("scrape.leaderboards", "failed", errorMessage: "failure");
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Reserved_phase_cannot_start_or_contribute_progress()
    {
        var (sink, metaDb, _) = CreateSink();
        sink.AttachScrape(42, "instance-a");
        metaDb.ClearReceivedCalls();
        var descriptor = Assert.IsType<PhaseProgressDescriptor>(
            PhaseProgressCatalog.FindById("post.checkpoint"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            sink.StartPhase(descriptor));

        Assert.Contains("Reserved phase 'post.checkpoint'", error.Message);
        metaDb.DidNotReceive()
            .StartScrapePhaseAttempt(Arg.Any<ScrapePhaseAttemptStart>());
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("completed")]
    public void Terminal_transition_persists_immediately(string status)
    {
        var (sink, metaDb, clock) = CreateSink();
        sink.AttachScrape(42, "instance-a");
        sink.StartPhase(PhaseProgressCatalog.All[0]);
        metaDb.ClearReceivedCalls();

        var view = sink.CompletePhase(
            "scrape.leaderboards",
            status,
            warningMessage: status == "cancelled" ? "cancelled" : null,
            errorMessage: status == "failed" ? "failed" : null);

        Assert.Equal(status, view?.PhaseStatus);
        metaDb.Received(1).CompleteScrapePhaseAttempt(
            Arg.Is<ScrapePhaseAttemptCompletion>(completion =>
                completion.Status == status
                && completion.CompletedAtUtc == clock.UtcNow));
    }

    [Fact]
    public void Eta_requires_conservative_sample_and_variance_gates()
    {
        Assert.Null(PhaseEtaEstimator.TryEstimate(
            [100_000, 101_000, 99_000, 102_000],
            phasePercent: 50));
        Assert.Null(PhaseEtaEstimator.TryEstimate(
            [10_000, 20_000, 30_000, 40_000, 120_000],
            phasePercent: 50));

        var estimate = PhaseEtaEstimator.TryEstimate(
            [98_000, 99_000, 100_000, 101_000, 102_000],
            phasePercent: 50);
        Assert.NotNull(estimate);
        Assert.Equal(5, estimate!.SampleCount);
        Assert.True(estimate.LowerSeconds <= estimate.UpperSeconds);

        var later = PhaseEtaEstimator.TryEstimate(
            [98_000, 99_000, 100_000, 101_000, 102_000],
            phasePercent: 75,
            previousUpperSeconds: estimate.UpperSeconds);
        Assert.NotNull(later);
        Assert.True(later!.UpperSeconds <= estimate.UpperSeconds);
    }

    [Fact]
    public void Sink_emits_eta_only_for_same_units_and_comparable_final_total()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.StartScrapePhaseAttempt(Arg.Any<ScrapePhaseAttemptStart>())
            .Returns(1);
        metaDb.GetSuccessfulPhaseDurationSamples(
                "post.compute_rankings",
                PhaseProgressCatalog.PlanVersion,
                Arg.Any<string?>(),
                20)
            .Returns(
            [
                new PhaseDurationSample(98_000, "instruments", 10, true),
                new PhaseDurationSample(99_000, "instruments", 10, true),
                new PhaseDurationSample(100_000, "instruments", 10, true),
                new PhaseDurationSample(101_000, "instruments", 10, true),
                new PhaseDurationSample(102_000, "instruments", 10, true),
            ]);
        var clock = new FakePhaseProgressClock();
        var sink = new DurablePhaseProgressSink(
            metaDb,
            BuildConfiguration(),
            NullLogger<DurablePhaseProgressSink>.Instance,
            clock);
        sink.AttachScrape(42, "instance-a");
        sink.StartPhase(PhaseProgressCatalog.FindPostScrape("ComputeRankings")!);
        clock.Advance(TimeSpan.FromSeconds(5));

        var comparable = Assert.Single(sink.ObserveTracker(new OperationSnapshot
        {
            Operation = "ComputingRankings",
            WorkItems = new ProgressCounter { Completed = 5, Total = 10 },
            WorkItemsTotalFinal = true,
        }));
        Assert.NotNull(comparable.EtaUpperSeconds);

        sink.CompletePhase("post.compute_rankings", "completed");
        sink.StartPhase(PhaseProgressCatalog.FindPostScrape("ComputeRankings")!);
        clock.Advance(TimeSpan.FromSeconds(5));
        var mismatched = Assert.Single(sink.ObserveTracker(new OperationSnapshot
        {
            Operation = "ComputingRankings",
            WorkItems = new ProgressCounter { Completed = 50, Total = 100 },
            WorkItemsTotalFinal = true,
        }));
        Assert.Null(mismatched.EtaUpperSeconds);
    }

    private static OperationSnapshot ScrapingSnapshot(int completed, int total) =>
        new()
        {
            Operation = "Scraping",
            SubOperation = "fetching_leaderboards",
            Leaderboards = new ProgressCounter
            {
                Completed = completed,
                Total = total,
            },
        };

    private static (
        DurablePhaseProgressSink Sink,
        IMetaDatabase MetaDb,
        FakePhaseProgressClock Clock) CreateSink()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        metaDb.StartScrapePhaseAttempt(Arg.Any<ScrapePhaseAttemptStart>())
            .Returns(1);
        metaDb.GetSuccessfulPhaseDurationSamples(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<int>())
            .Returns([]);
        var clock = new FakePhaseProgressClock();
        return (
            new DurablePhaseProgressSink(
                metaDb,
                BuildConfiguration(),
                NullLogger<DurablePhaseProgressSink>.Instance,
                clock),
            metaDb,
            clock);
    }

    private static string CaptureConfigId(bool enabled)
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        ScrapePhaseAttemptStart? captured = null;
        metaDb.StartScrapePhaseAttempt(
                Arg.Do<ScrapePhaseAttemptStart>(
                    start => captured = start))
            .Returns(1);
        metaDb.GetSuccessfulPhaseDurationSamples(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<int>())
            .Returns([]);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Scraper:BandCurrentProjectionUseBatchedMemberStatsAggregation"] =
                        enabled.ToString(),
                })
            .Build();
        var sink = new DurablePhaseProgressSink(
            metaDb,
            configuration,
            NullLogger<DurablePhaseProgressSink>.Instance,
            new FakePhaseProgressClock());

        sink.AttachScrape(42, "instance-a");
        sink.StartPhase(PhaseProgressCatalog.All[0]);

        return Assert.IsType<string>(captured?.ConfigId);
    }

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scraper:EnabledPhases"] = "All",
                ["Scraper:RunOnce"] = "true",
                ["Features:EnforcePublicationCriticalPhases"] = "true",
            })
            .Build();

    private sealed class FakePhaseProgressClock : IPhaseProgressClock
    {
        public DateTime UtcNow { get; private set; } =
            new(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc);

        public void Advance(TimeSpan duration) => UtcNow += duration;
    }
}
