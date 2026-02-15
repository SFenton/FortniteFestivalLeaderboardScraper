using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class BackfillQueueTests
{
    [Fact]
    public void Enqueue_and_Drain_returns_items_in_order()
    {
        var queue = new BackfillQueue();
        queue.Enqueue(new BackfillRequest("acct_1"));
        queue.Enqueue(new BackfillRequest("acct_2"));
        queue.Enqueue(new BackfillRequest("acct_3"));

        var drained = queue.DrainAll();

        Assert.Equal(3, drained.Count);
        Assert.Equal("acct_1", drained[0].AccountId);
        Assert.Equal("acct_2", drained[1].AccountId);
        Assert.Equal("acct_3", drained[2].AccountId);
    }

    [Fact]
    public void DrainAll_empties_queue()
    {
        var queue = new BackfillQueue();
        queue.Enqueue(new BackfillRequest("acct_1"));

        var first = queue.DrainAll();
        var second = queue.DrainAll();

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public void DrainAll_returns_empty_when_nothing_queued()
    {
        var queue = new BackfillQueue();
        var result = queue.DrainAll();
        Assert.Empty(result);
    }

    [Fact]
    public void HasPending_reflects_queue_state()
    {
        var queue = new BackfillQueue();
        Assert.False(queue.HasPending);

        queue.Enqueue(new BackfillRequest("acct_1"));
        Assert.True(queue.HasPending);

        queue.DrainAll();
        Assert.False(queue.HasPending);
    }

    [Fact]
    public void Concurrent_enqueue_does_not_lose_items()
    {
        var queue = new BackfillQueue();
        const int count = 100;

        Parallel.For(0, count, i =>
        {
            queue.Enqueue(new BackfillRequest($"acct_{i}"));
        });

        var drained = queue.DrainAll();
        Assert.Equal(count, drained.Count);
        Assert.Equal(count, drained.Select(r => r.AccountId).Distinct().Count());
    }
}
