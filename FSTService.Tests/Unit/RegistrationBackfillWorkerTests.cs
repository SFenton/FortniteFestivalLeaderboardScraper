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
}