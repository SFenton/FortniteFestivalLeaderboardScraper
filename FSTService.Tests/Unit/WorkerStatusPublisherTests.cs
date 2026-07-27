using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Logging.Abstractions;
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
            subOperation: "DeferredRegistrationSync",
            detail: "Computing deferred rivals 1/2",
            progressPercent: 50);

        metaDb.Received(1).UpdateWorkerActivity(
            WorkerStatusPublisher.ScraperWorkerKey,
            Arg.Is<WorkerOperationInfo>(operation =>
                operation.OperationKey == "scrape.post_process"
                && operation.SubOperation == "DeferredRegistrationSync"
                && operation.Detail == "Computing deferred rivals 1/2"
                && operation.ProgressPercent == 50
                && operation.UpdatedAtUtc >= operation.StartedAtUtc),
            Arg.Any<WorkerOperationInfo?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<DateTime?>());
    }
}
