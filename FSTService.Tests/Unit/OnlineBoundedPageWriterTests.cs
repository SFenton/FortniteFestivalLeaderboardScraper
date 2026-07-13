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

    [Fact]
    public async Task FailureResultRetainsExactRowsAndCanReplay()
    {
        var baseDirectory = Path.Combine(
            Path.GetTempPath(),
            $"online_writer_failure_{Guid.NewGuid():N}");
        var attempts = 0;
        var replayed = new List<(string SongId, int Rows)>();
        var writer = new OnlineBoundedPageWriter<TestEntry>(
            _log,
            "test-online-failure",
            (_, batch) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new InvalidOperationException("injected online failure");

                replayed.AddRange(batch.Select(item => (item.SongId, item.Entries.Count)));
            },
            channelCapacity: 4,
            maxBatchPages: 4,
            writerCount: 1,
            replayBaseDirectory: baseDirectory);

        try
        {
            await writer.EnqueueAsync(
                "song_1",
                "Solo_Guitar",
                [new TestEntry("a", 1), new TestEntry("b", 2)]);
            await writer.EnqueueAsync(
                "song_2",
                "Solo_Guitar",
                [new TestEntry("c", 3)]);

            var failed = await writer.CompleteAndDrainAsync();

            Assert.False(failed.IsSuccess);
            var failure = Assert.Single(failed.Failures);
            Assert.True(failure.PageCount >= 1);
            Assert.True(failure.RowCount >= 1);
            Assert.NotNull(failure.ArtifactPath);
            Assert.True(File.Exists(failure.ArtifactPath));

            await writer.DisposeAsync();
            var replay = OnlineBoundedPageWriter<TestEntry>.ReplayArtifactDirectory(
                _log,
                "test-online-failure",
                failed.ReplayArtifactDirectory!,
                (_, batch) =>
                    replayed.AddRange(batch.Select(item => (item.SongId, item.Entries.Count))));

            Assert.True(replay.IsSuccess);
            Assert.Equal(3, replayed.Sum(static item => item.Rows));
            Assert.Contains(replayed, static item => item.SongId == "song_1" && item.Rows == 2);
            Assert.Contains(replayed, static item => item.SongId == "song_2" && item.Rows == 1);
        }
        finally
        {
            await writer.DisposeAsync();
            if (Directory.Exists(baseDirectory))
                Directory.Delete(baseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ReplayFailures_AndPersistedReplay_ReportRepeatedFailure()
    {
        var baseDirectory = Path.Combine(
            Path.GetTempPath(),
            $"online_writer_repeat_failure_{Guid.NewGuid():N}");
        var writer = new OnlineBoundedPageWriter<TestEntry>(
            _log,
            "repeat-failure",
            (_, _) => throw new InvalidOperationException("still failing"),
            channelCapacity: 2,
            maxBatchPages: 2,
            writerCount: 1,
            replayBaseDirectory: baseDirectory);

        try
        {
            await writer.EnqueueAsync(
                "song_1",
                "Solo_Guitar",
                [new TestEntry("a", 1)]);
            var initial = await writer.CompleteAndDrainAsync();

            var inMemoryReplay = writer.ReplayFailures();

            Assert.False(inMemoryReplay.IsSuccess);
            Assert.Equal(1, Assert.Single(inMemoryReplay.Failures).RowCount);

            await writer.DisposeAsync();
            var persistedReplay =
                OnlineBoundedPageWriter<TestEntry>.ReplayArtifactDirectory(
                    _log,
                    "repeat-failure",
                    initial.ReplayArtifactDirectory!,
                    (_, _) => throw new InvalidOperationException("persisted failure"));

            Assert.False(persistedReplay.IsSuccess);
            var persistedFailure = Assert.Single(persistedReplay.Failures);
            Assert.Equal("Solo_Guitar", persistedFailure.Instrument);
            Assert.Equal(1, persistedFailure.RowCount);
            Assert.Contains("persisted failure", persistedFailure.ErrorMessage);
        }
        finally
        {
            await writer.DisposeAsync();
            if (Directory.Exists(baseDirectory))
                Directory.Delete(baseDirectory, recursive: true);
        }
    }

    [Fact]
    public void PersistedReplay_MalformedArtifactIsVisible()
    {
        var artifactDirectory = Path.Combine(
            Path.GetTempPath(),
            $"online_writer_bad_artifact_{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactDirectory);
        File.WriteAllText(
            Path.Combine(artifactDirectory, "worker-bad.json"),
            "{not-json");

        try
        {
            var result = OnlineBoundedPageWriter<TestEntry>.ReplayArtifactDirectory(
                _log,
                "bad-artifact",
                artifactDirectory,
                (_, _) => { });

            Assert.False(result.IsSuccess);
            var failure = Assert.Single(result.Failures);
            Assert.Equal("artifact", failure.Instrument);
            Assert.Equal(0, failure.RowCount);
        }
        finally
        {
            Directory.Delete(artifactDirectory, recursive: true);
        }
    }
}