using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public class RegistrationBackfillWorkerTests
{
    [Fact]
    public async Task DrainQueuedRegistrationBackfillsAsync_CompletesEachClaimedBatchBeforeNextClaim()
    {
        var claims = new Queue<int>([2, 1, 0]);
        var activeBatch = 0;
        var completedBatches = 0;
        var loggedClaims = new List<int>();

        var total = await RegistrationBackfillWorker.DrainQueuedRegistrationBackfillsAsync(
            batchSize: 4,
            runBatchAsync: async (batchSize, ct) =>
            {
                Assert.Equal(4, batchSize);
                Assert.Equal(0, activeBatch);
                activeBatch++;
                await Task.Yield();
                var claimed = claims.Dequeue();
                activeBatch--;
                completedBatches++;
                return claimed;
            },
            onBatchClaimed: loggedClaims.Add,
            ct: CancellationToken.None);

        Assert.Equal(3, total);
        Assert.Equal(3, completedBatches);
        Assert.Equal([2, 1], loggedClaims);
    }

    [Fact]
    public async Task DrainQueuedRegistrationBackfillsAsync_StopsImmediatelyWhenNoBatchClaimsWork()
    {
        var calls = 0;
        var loggedClaims = new List<int>();

        var total = await RegistrationBackfillWorker.DrainQueuedRegistrationBackfillsAsync(
            batchSize: 4,
            runBatchAsync: (_, _) =>
            {
                calls++;
                return Task.FromResult(0);
            },
            onBatchClaimed: loggedClaims.Add,
            ct: CancellationToken.None);

        Assert.Equal(0, total);
        Assert.Equal(1, calls);
        Assert.Empty(loggedClaims);
    }

    [Fact]
    public async Task RunAvailableRegistrationWorkAsync_DrainsHistoryWhenNoBackfillClaimsWork()
    {
        var historyRuns = 0;

        var claimed = await RegistrationBackfillWorker
            .RunAvailableRegistrationWorkAsync(
                batchSize: 4,
                runBatchAsync: static (_, _) => Task.FromResult(0),
                runHistoryReconAsync: _ =>
                {
                    historyRuns++;
                    return Task.CompletedTask;
                },
                hasQueuedBackfills: static () => false,
                onBatchClaimed: static _ => { },
                ct: CancellationToken.None);

        Assert.Equal(0, claimed);
        Assert.Equal(1, historyRuns);
    }

    [Fact]
    public async Task RunAvailableRegistrationWorkAsync_RunsHistoryAfterBackfillsDrain()
    {
        var claims = new Queue<int>([1, 0]);
        var historyRuns = 0;

        var claimed = await RegistrationBackfillWorker
            .RunAvailableRegistrationWorkAsync(
                batchSize: 4,
                runBatchAsync: (_, _) =>
                    Task.FromResult(claims.Dequeue()),
                runHistoryReconAsync: _ =>
                {
                    historyRuns++;
                    return Task.CompletedTask;
                },
                hasQueuedBackfills: static () => false,
                onBatchClaimed: static _ => { },
                ct: CancellationToken.None);

        Assert.Equal(1, claimed);
        Assert.Equal(1, historyRuns);
    }

    [Fact]
    public async Task RunAvailableRegistrationWorkAsync_DoesNotRunHistoryAfterFailedBackfill()
    {
        var historyRuns = 0;

        var claimed = await RegistrationBackfillWorker
            .RunAvailableRegistrationWorkAsync(
                batchSize: 4,
                runBatchAsync: static (_, _) => Task.FromResult(0),
                runHistoryReconAsync: _ =>
                {
                    historyRuns++;
                    return Task.CompletedTask;
                },
                hasQueuedBackfills: static () => true,
                onBatchClaimed: static _ => { },
                ct: CancellationToken.None);

        Assert.Equal(0, claimed);
        Assert.Equal(0, historyRuns);
    }

    [Fact]
    public async Task BackgroundWorkCoordinator_WaitsForActiveWriterBeforeScrapeContinues()
    {
        var coordinator = new BackgroundWorkCoordinator();
        Assert.True(coordinator.TryBeginBackgroundOperation(out var operation));

        coordinator.RequestPauseForScrape();
        var quiescence = coordinator.WaitForBackgroundQuiescenceAsync();

        Assert.False(quiescence.IsCompleted);
        Assert.False(coordinator.TryBeginBackgroundOperation(out _));

        operation!.Dispose();
        await quiescence.WaitAsync(TimeSpan.FromSeconds(1));

        coordinator.ResumeAfterScrape();
        Assert.True(coordinator.TryBeginBackgroundOperation(out var nextOperation));
        nextOperation!.Dispose();
    }
}