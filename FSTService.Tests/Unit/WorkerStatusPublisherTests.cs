using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class WorkerStatusPublisherTests
{
    [Fact]
    public void Heartbeat_preserves_operation_progress_timestamp()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        var publisher = new WorkerStatusPublisher(
            metaDb,
            NullLogger<WorkerStatusPublisher>.Instance);
        WorkerOperationInfo? startedOperation = null;
        metaDb.When(x => x.UpdateWorkerActivity(
                WorkerStatusPublisher.ScraperWorkerKey,
                Arg.Any<WorkerOperationInfo>(),
                Arg.Any<WorkerOperationInfo?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<DateTime?>()))
            .Do(call => startedOperation = call.ArgAt<WorkerOperationInfo>(1));
        publisher.BeginOperation(
            "scrape.post_process",
            "Post-processing leaderboard update",
            phase: "PostScrapeEnrichment");
        Assert.NotNull(startedOperation);
        metaDb.ClearReceivedCalls();

        publisher.PublishHeartbeat();

        metaDb.Received(1).UpsertWorkerHeartbeat(
            WorkerStatusPublisher.ScraperWorkerKey,
            "running",
            "scraper",
            Arg.Any<string>(),
            Arg.Any<DateTime>(),
            Arg.Any<DateTime>(),
            null,
            Arg.Is<WorkerOperationInfo>(operation =>
                operation.OperationKey == "scrape.post_process"
                && operation.UpdatedAtUtc == startedOperation.UpdatedAtUtc
                && operation.ElapsedSeconds == startedOperation.ElapsedSeconds));
    }

    [Fact]
    public void Explicit_operation_update_advances_progress_timestamp()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        var publisher = new WorkerStatusPublisher(
            metaDb,
            NullLogger<WorkerStatusPublisher>.Instance);
        publisher.BeginOperation(
            "scrape.post_process",
            "Post-processing leaderboard update",
            phase: "PostScrapeEnrichment");
        metaDb.ClearReceivedCalls();

        publisher.UpdateOperation(
            "scrape.post_process",
            subOperation: "RefreshRegisteredUsers",
            detail: "Refreshing registered users 1/2",
            progressPercent: 50);

        metaDb.Received(1).UpdateWorkerActivity(
            WorkerStatusPublisher.ScraperWorkerKey,
            Arg.Is<WorkerOperationInfo>(operation =>
                operation.OperationKey == "scrape.post_process"
                && operation.SubOperation == "RefreshRegisteredUsers"
                && operation.Detail == "Refreshing registered users 1/2"
                && operation.ProgressPercent == 50
                && operation.UpdatedAtUtc >= operation.StartedAtUtc),
            Arg.Any<WorkerOperationInfo?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTime?>());
    }

    [Fact]
    public void Durable_progress_is_added_without_removing_v1_operation_fields()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        var publisher = new WorkerStatusPublisher(
            metaDb,
            NullLogger<WorkerStatusPublisher>.Instance);
        publisher.BeginOperation(
            "scrape.post_process",
            "Post-processing leaderboard update",
            phase: "PostScrapeEnrichment",
            subOperation: "BandMaintenance");
        metaDb.ClearReceivedCalls();
        var progressAt = DateTime.UtcNow;

        publisher.ApplyDurableProgress(new DurablePhaseProgressView(
            42,
            "scrape.update",
            "post.band_maintenance",
            "running",
            "current_projection_refresh",
            PhaseProgressCatalog.PlanVersion,
            300,
            1,
            "scopes",
            5,
            10,
            true,
            50,
            "indeterminate",
            null,
            null,
            null,
            null,
            null,
            null,
            progressAt,
            progressAt));

        metaDb.Received(1).UpdateWorkerActivity(
            WorkerStatusPublisher.ScraperWorkerKey,
            Arg.Is<WorkerOperationInfo>(operation =>
                operation.OperationKey == "scrape.post_process"
                && operation.OperationLabel == "Post-processing leaderboard update"
                && operation.Phase == "PostScrapeEnrichment"
                && operation.SubOperation == "BandMaintenance"
                && operation.ContractVersion == 2
                && operation.PhaseId == "post.band_maintenance"
                && operation.SubphaseId == "current_projection_refresh"
                && operation.PhasePercent == 50
                && operation.LastProgressAtUtc == progressAt),
            Arg.Any<WorkerOperationInfo?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTime?>());
    }

    [Fact]
    public void Attaching_after_operation_start_creates_durable_attempt()
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
        var sink = new DurablePhaseProgressSink(
            metaDb,
            new ConfigurationBuilder().Build(),
            NullLogger<DurablePhaseProgressSink>.Instance);
        var publisher = new WorkerStatusPublisher(
            metaDb,
            NullLogger<WorkerStatusPublisher>.Instance,
            sink);
        publisher.BeginOperation(
            "scrape.leaderboards",
            "Scraping leaderboard scores",
            phase: "Scraping",
            subOperation: "fetching_leaderboards");
        metaDb.ClearReceivedCalls();

        publisher.AttachScrape(42);

        metaDb.Received(1).StartScrapePhaseAttempt(
            Arg.Is<ScrapePhaseAttemptStart>(start =>
                start.ScrapeId == 42
                && start.PhaseId == "scrape.leaderboards"
                && start.WorkerInstanceId == publisher.InstanceId));
        metaDb.Received().UpdateWorkerActivity(
            WorkerStatusPublisher.ScraperWorkerKey,
            Arg.Is<WorkerOperationInfo>(operation =>
                operation.PhaseId == "scrape.leaderboards"
                && operation.PhaseAttempt == 1),
            Arg.Any<WorkerOperationInfo?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTime?>());
    }

    [Fact]
    public void New_phase_clears_weak_estimates_from_previous_phase()
    {
        var metaDb = Substitute.For<IMetaDatabase>();
        var publisher = new WorkerStatusPublisher(
            metaDb,
            NullLogger<WorkerStatusPublisher>.Instance);
        publisher.BeginOperation(
            "scrape.post_process",
            "Post-processing leaderboard update",
            phase: "PostScrapeEnrichment");
        var now = DateTime.UtcNow;
        publisher.ApplyDurableProgress(new DurablePhaseProgressView(
            42, "scrape.update", "post.band_maintenance", "running", null,
            PhaseProgressCatalog.PlanVersion, 300, 1, "scopes", 5, 10, true,
            50, "indeterminate", null, null, 40, 60, "medium", 5, now, now));
        metaDb.ClearReceivedCalls();

        publisher.ApplyDurableProgress(new DurablePhaseProgressView(
            42, "scrape.update", "post.compute_rankings", "running", null,
            PhaseProgressCatalog.PlanVersion, 310, 1, "instruments", null, null,
            false, null, "indeterminate", null, null, null, null, null, null,
            now.AddSeconds(1), now.AddSeconds(1)));

        metaDb.Received(1).UpdateWorkerActivity(
            WorkerStatusPublisher.ScraperWorkerKey,
            Arg.Is<WorkerOperationInfo>(operation =>
                operation.PhaseId == "post.compute_rankings"
                && operation.PhasePercent == null
                && operation.EtaLowerSeconds == null
                && operation.EtaUpperSeconds == null
                && operation.EtaConfidence == null
                && operation.EtaSampleCount == null),
            Arg.Any<WorkerOperationInfo?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTime?>());
    }
}
