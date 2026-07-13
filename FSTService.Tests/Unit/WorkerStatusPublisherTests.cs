using FSTService.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class WorkerStatusPublisherTests
{
    [Fact]
    public void Heartbeat_refreshes_active_operation_timestamp_and_elapsed_time()
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
                && operation.UpdatedAtUtc >= operation.StartedAtUtc
                && operation.ElapsedSeconds >= 0));
    }
}
