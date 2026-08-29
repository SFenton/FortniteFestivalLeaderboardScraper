using FSTService.Persistence.Maintenance;

namespace FSTService.Tests.Unit;

public sealed class SnapshotGenerationRetentionSafePointQueueTests
{
    [Fact]
    public void ConstructorRejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SnapshotGenerationRetentionSafePointQueue(
                capacity: 0));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void EnqueueRejectsNonPositiveIdentities(
        long scrapeId,
        long publicationId)
    {
        var queue =
            new SnapshotGenerationRetentionSafePointQueue();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => queue.Enqueue(
                new PendingSnapshotGenerationRetentionSafePoint(
                    scrapeId,
                    publicationId)));
    }

    [Fact]
    public void EmptyQueueHasNoHead()
    {
        var queue =
            new SnapshotGenerationRetentionSafePointQueue();

        Assert.False(queue.TryPeek(out var safePoint));
        Assert.Null(safePoint);
    }

    [Fact]
    public void QueueIsKeyedOrderedAndIdempotent()
    {
        var queue =
            new SnapshotGenerationRetentionSafePointQueue();
        var first =
            new PendingSnapshotGenerationRetentionSafePoint(
                1308,
                9008);
        var second =
            new PendingSnapshotGenerationRetentionSafePoint(
                1309,
                9009);

        Assert.True(queue.Enqueue(first));
        Assert.False(queue.Enqueue(first));
        Assert.True(queue.Enqueue(second));

        Assert.Equal([first, second], queue.Snapshot());
        Assert.True(queue.TryPeek(out var head));
        Assert.Equal(first, head);
        queue.CompleteTerminal(first);
        Assert.Equal([second], queue.Snapshot());
    }

    [Fact]
    public void RetryLeavesHeadAheadOfLaterPublication()
    {
        var queue =
            new SnapshotGenerationRetentionSafePointQueue();
        var retrying =
            new PendingSnapshotGenerationRetentionSafePoint(
                1308,
                9008);
        var later =
            new PendingSnapshotGenerationRetentionSafePoint(
                1309,
                9009);
        queue.Enqueue(retrying);
        queue.Enqueue(later);

        Assert.True(queue.TryPeek(out var firstAttempt));
        Assert.Equal(retrying, firstAttempt);
        Assert.True(queue.TryPeek(out var retryAttempt));
        Assert.Equal(retrying, retryAttempt);
        Assert.Equal([retrying, later], queue.Snapshot());
    }

    [Fact]
    public void CapacityFailsClosedWithoutDiscardingQueuedPublication()
    {
        var queue =
            new SnapshotGenerationRetentionSafePointQueue(
                capacity: 1);
        var retained =
            new PendingSnapshotGenerationRetentionSafePoint(
                1308,
                9008);
        queue.Enqueue(retained);

        var error = Assert.Throws<InvalidOperationException>(
            () => queue.Enqueue(
                new PendingSnapshotGenerationRetentionSafePoint(
                    1309,
                    9009)));

        Assert.Contains(
            "no publication was discarded",
            error.Message,
            StringComparison.Ordinal);
        Assert.Equal([retained], queue.Snapshot());
    }

    [Fact]
    public void OnlyMatchingHeadCanBeCompleted()
    {
        var queue =
            new SnapshotGenerationRetentionSafePointQueue();
        var first =
            new PendingSnapshotGenerationRetentionSafePoint(
                1308,
                9008);
        queue.Enqueue(first);

        Assert.Throws<InvalidOperationException>(
            () => queue.CompleteTerminal(
                new PendingSnapshotGenerationRetentionSafePoint(
                    1309,
                    9009)));
        Assert.Equal([first], queue.Snapshot());
    }
}
