using System.Threading.Channels;

namespace FSTService.Scraping;

/// <summary>
/// Bounded in-memory page writer that applies explicit backpressure to producers
/// while a small, fixed set of database workers flushes pages in bulk batches.
/// </summary>
public sealed class OnlineBoundedPageWriter<T> : IAsyncDisposable
{
    public delegate void FlushBatch(string instrument, List<(string SongId, IReadOnlyList<T> Entries)> batch);

    private readonly Channel<PageWorkItem> _channel;
    private readonly List<Task> _workerTasks;
    private readonly FlushBatch _flush;
    private readonly ILogger _log;
    private readonly string _label;
    private readonly int _maxBatchPages;
    private long _enqueuedPages;
    private long _enqueuedEntries;
    private long _flushedPages;
    private long _flushedEntries;
    private int _completed;

    private readonly record struct PageWorkItem(string SongId, string Instrument, IReadOnlyList<T> Entries);

    public OnlineBoundedPageWriter(
        ILogger log,
        string label,
        FlushBatch flush,
        int channelCapacity,
        int maxBatchPages,
        int writerCount,
        CancellationToken ct = default)
    {
        if (channelCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(channelCapacity));
        if (maxBatchPages <= 0) throw new ArgumentOutOfRangeException(nameof(maxBatchPages));
        if (writerCount <= 0) throw new ArgumentOutOfRangeException(nameof(writerCount));

        _log = log;
        _label = label;
        _flush = flush;
        _maxBatchPages = maxBatchPages;
        _channel = Channel.CreateBounded<PageWorkItem>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = writerCount == 1,
            SingleWriter = false,
        });

        _workerTasks = Enumerable.Range(0, writerCount)
            .Select(index => Task.Run(() => RunWriterAsync(index + 1, ct), ct))
            .ToList();

        _log.LogInformation(
            "Online bounded writer [{Label}] started: capacity={Capacity}, batchPages={BatchPages}, writers={Writers}.",
            _label, channelCapacity, maxBatchPages, writerCount);
    }

    public long EnqueuedPages => Interlocked.Read(ref _enqueuedPages);
    public long EnqueuedEntries => Interlocked.Read(ref _enqueuedEntries);
    public long FlushedPages => Interlocked.Read(ref _flushedPages);
    public long FlushedEntries => Interlocked.Read(ref _flushedEntries);
    public long PendingPages => Math.Max(0, EnqueuedPages - FlushedPages);

    public async ValueTask EnqueueAsync(
        string songId,
        string instrument,
        IReadOnlyList<T> entries,
        CancellationToken ct = default)
    {
        if (entries.Count == 0) return;
        if (Volatile.Read(ref _completed) != 0)
            throw new InvalidOperationException("Writer has already been completed.");

        await _channel.Writer.WriteAsync(new PageWorkItem(songId, instrument, entries), ct)
            .ConfigureAwait(false);

        Interlocked.Increment(ref _enqueuedPages);
        Interlocked.Add(ref _enqueuedEntries, entries.Count);
    }

    public async Task CompleteAndDrainAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            _channel.Writer.TryComplete();

        await Task.WhenAll(_workerTasks).ConfigureAwait(false);

        _log.LogInformation(
            "Online bounded writer [{Label}] drained: {Pages:N0}/{EnqueuedPages:N0} pages, {Entries:N0}/{EnqueuedEntries:N0} entries flushed.",
            _label, FlushedPages, EnqueuedPages, FlushedEntries, EnqueuedEntries);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            _channel.Writer.TryComplete();

        try
        {
            await Task.WhenAll(_workerTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Dispose may be called during scrape cancellation; cancellation is expected.
        }
    }

    private async Task RunWriterAsync(int workerIndex, CancellationToken ct)
    {
        var batch = new List<PageWorkItem>(_maxBatchPages);

        while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < _maxBatchPages && _channel.Reader.TryRead(out var item))
                batch.Add(item);

            if (batch.Count == 0) continue;

            FlushGroupedBatch(workerIndex, batch);
        }
    }

    private void FlushGroupedBatch(int workerIndex, List<PageWorkItem> batch)
    {
        foreach (var group in batch.GroupBy(static item => item.Instrument, StringComparer.OrdinalIgnoreCase))
        {
            var currentBatch = group
                .Select(static item => (item.SongId, item.Entries))
                .ToList();
            var entryCount = currentBatch.Sum(static item => item.Entries.Count);

            try
            {
                _flush(group.Key, currentBatch);
                Interlocked.Add(ref _flushedPages, currentBatch.Count);
                Interlocked.Add(ref _flushedEntries, entryCount);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex,
                    "Online bounded writer [{Label}] worker {Worker} failed flushing {Instrument} ({Pages:N0} pages, {Entries:N0} entries). Data will be re-scraped next pass.",
                    _label, workerIndex, group.Key, currentBatch.Count, entryCount);
            }
        }
    }
}