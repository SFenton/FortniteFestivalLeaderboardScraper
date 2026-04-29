using FSTService.Scraping;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class OnlineBoundedPageWriterTests
{
    private readonly ILogger _log = Substitute.For<ILogger>();

    private sealed record TestEntry(string Id, int Value);

    [Fact]
    public async Task CompleteAndDrainAsync_BatchesAndGroupsByInstrument()
    {
        var flushed = new List<(string Instrument, int Pages, int Entries)>();
        await using var writer = new OnlineBoundedPageWriter<TestEntry>(
            _log,
            "test",
            (instrument, batch) => flushed.Add((instrument, batch.Count, batch.Sum(p => p.Entries.Count))),
            channelCapacity: 10,
            maxBatchPages: 3,
            writerCount: 1);

        await writer.EnqueueAsync("song_1", "Solo_Guitar", [new TestEntry("a", 1)]);
        await writer.EnqueueAsync("song_2", "Solo_Bass", [new TestEntry("b", 2), new TestEntry("c", 3)]);
        await writer.EnqueueAsync("song_3", "Solo_Guitar", [new TestEntry("d", 4)]);
        await writer.EnqueueAsync("song_4", "Solo_Guitar", [new TestEntry("e", 5)]);

        await writer.CompleteAndDrainAsync();

        Assert.Equal(4, writer.EnqueuedPages);
        Assert.Equal(5, writer.EnqueuedEntries);
        Assert.Equal(4, writer.FlushedPages);
        Assert.Equal(5, writer.FlushedEntries);
        Assert.Equal(0, writer.PendingPages);
        Assert.Equal(3, flushed.Where(f => f.Instrument == "Solo_Guitar").Sum(f => f.Pages));
        Assert.Equal(1, flushed.Where(f => f.Instrument == "Solo_Bass").Sum(f => f.Pages));
    }

    [Fact]
    public async Task EnqueueAsync_AppliesBackpressure_WhenChannelIsFull()
    {
        var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblockFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var writer = new OnlineBoundedPageWriter<TestEntry>(
            _log,
            "test",
            (_, _) =>
            {
                flushStarted.TrySetResult();
                unblockFlush.Task.GetAwaiter().GetResult();
            },
            channelCapacity: 1,
            maxBatchPages: 1,
            writerCount: 1);

        await writer.EnqueueAsync("song_1", "Solo_Guitar", [new TestEntry("a", 1)]);
        await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await writer.EnqueueAsync("song_2", "Solo_Guitar", [new TestEntry("b", 2)]);
        var blockedWrite = writer.EnqueueAsync("song_3", "Solo_Guitar", [new TestEntry("c", 3)]).AsTask();

        var completedEarly = await Task.WhenAny(blockedWrite, Task.Delay(100));
        Assert.NotSame(blockedWrite, completedEarly);

        unblockFlush.SetResult();
        await blockedWrite.WaitAsync(TimeSpan.FromSeconds(5));
        await writer.CompleteAndDrainAsync();

        Assert.Equal(3, writer.FlushedPages);
    }
}