using System.Threading.Channels;
using System.Text.Json;

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
    private readonly string _artifactDirectory;
    private readonly object _failureLock = new();
    private readonly List<WriterBatchFailure> _failures = [];
    private readonly List<(string Instrument, List<(string SongId, IReadOnlyList<T> Entries)> Batch)> _failedBatches = [];
    private long _enqueuedPages;
    private long _enqueuedEntries;
    private long _flushedPages;
    private long _flushedEntries;
    private int _completed;

    private readonly record struct PageWorkItem(string SongId, string Instrument, IReadOnlyList<T> Entries);

    private sealed class ReplayArtifactPayload
    {
        public int FormatVersion { get; init; } = 1;
        public string WriterKind { get; init; } = "";
        public string Instrument { get; init; } = "";
        public DateTime FailedAtUtc { get; init; }
        public string ExceptionType { get; init; } = "";
        public string ErrorMessage { get; init; } = "";
        public IReadOnlyList<ReplayArtifactPage> Pages { get; init; } = [];
    }

    private sealed class ReplayArtifactPage
    {
        public string SongId { get; init; } = "";
        public long RowCount { get; init; }
        public IReadOnlyList<T> Entries { get; init; } = [];
    }

    public OnlineBoundedPageWriter(
        ILogger log,
        string label,
        FlushBatch flush,
        int channelCapacity,
        int maxBatchPages,
        int writerCount,
        CancellationToken ct = default,
        string? replayBaseDirectory = null)
    {
        if (channelCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(channelCapacity));
        if (maxBatchPages <= 0) throw new ArgumentOutOfRangeException(nameof(maxBatchPages));
        if (writerCount <= 0) throw new ArgumentOutOfRangeException(nameof(writerCount));

        _log = log;
        _label = label;
        _flush = flush;
        _maxBatchPages = maxBatchPages;
        _artifactDirectory = Path.Combine(
            replayBaseDirectory ?? Path.GetTempPath(),
            $"fst_scrape_{label}_{Guid.NewGuid():N}");
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

    public async Task<WriterDrainResult> CompleteAndDrainAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            _channel.Writer.TryComplete();

        await Task.WhenAll(_workerTasks).ConfigureAwait(false);

        _log.LogInformation(
            "Online bounded writer [{Label}] drained: {Pages:N0}/{EnqueuedPages:N0} pages, {Entries:N0}/{EnqueuedEntries:N0} entries flushed.",
            _label, FlushedPages, EnqueuedPages, FlushedEntries, EnqueuedEntries);
        lock (_failureLock)
        {
            return new WriterDrainResult(
                _label,
                EnqueuedPages,
                EnqueuedEntries,
                FlushedPages,
                FlushedEntries,
                _failures.ToArray(),
                _failures.Count == 0 ? null : _artifactDirectory);
        }
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

        lock (_failureLock)
        {
            if (_failures.Count == 0)
            {
                try
                {
                    if (Directory.Exists(_artifactDirectory))
                        Directory.Delete(_artifactDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to delete online writer artifact directory {Path}.", _artifactDirectory);
                }
            }
            else
            {
                _log.LogWarning(
                    "Online bounded writer [{Label}] retained {FailureCount} replay artifact(s) at {Path}.",
                    _label,
                    _failures.Count,
                    _artifactDirectory);
            }
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
                var scopes = currentBatch
                    .GroupBy(static item => item.SongId, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => new WriterFailedScope(
                        group.Key,
                        group.Count(),
                        group.Sum(static item => (long)item.Entries.Count)))
                    .OrderBy(static scope => scope.SongId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var artifactPath = WriteReplayArtifact(
                    workerIndex,
                    group.Key,
                    currentBatch,
                    ex);
                lock (_failureLock)
                {
                    _failures.Add(new WriterBatchFailure(
                        _label,
                        group.Key,
                        scopes,
                        ex.GetType().FullName ?? ex.GetType().Name,
                        ex.Message,
                        artifactPath,
                        DateTime.UtcNow));
                    _failedBatches.Add((group.Key, currentBatch));
                }
                _log.LogError(ex,
                    "Online bounded writer [{Label}] worker {Worker} failed flushing {Instrument} ({Pages:N0} pages, {Entries:N0} entries). Replay artifact={ArtifactPath}.",
                    _label, workerIndex, group.Key, currentBatch.Count, entryCount, artifactPath);
            }
        }
    }

    public WriterDrainResult ReplayFailures()
    {
        List<(string Instrument, List<(string SongId, IReadOnlyList<T> Entries)> Batch)> batches;
        lock (_failureLock)
            batches = _failedBatches.ToList();

        long attemptedPages = 0;
        long attemptedRows = 0;
        long flushedPages = 0;
        long flushedRows = 0;
        var remainingFailures = new List<WriterBatchFailure>();
        var remainingBatches = new List<(string Instrument, List<(string SongId, IReadOnlyList<T> Entries)> Batch)>();

        foreach (var (instrument, batch) in batches)
        {
            var pages = batch.Count;
            var rows = batch.Sum(static item => (long)item.Entries.Count);
            attemptedPages += pages;
            attemptedRows += rows;
            try
            {
                _flush(instrument, batch);
                flushedPages += pages;
                flushedRows += rows;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var scopes = batch
                    .GroupBy(static item => item.SongId, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => new WriterFailedScope(
                        group.Key,
                        group.Count(),
                        group.Sum(static item => (long)item.Entries.Count)))
                    .ToArray();
                remainingFailures.Add(new WriterBatchFailure(
                    _label,
                    instrument,
                    scopes,
                    ex.GetType().FullName ?? ex.GetType().Name,
                    ex.Message,
                    _artifactDirectory,
                    DateTime.UtcNow));
                remainingBatches.Add((instrument, batch));
            }
        }

        lock (_failureLock)
        {
            _failures.Clear();
            _failures.AddRange(remainingFailures);
            _failedBatches.Clear();
            _failedBatches.AddRange(remainingBatches);
        }

        return new WriterDrainResult(
            _label,
            attemptedPages,
            attemptedRows,
            flushedPages,
            flushedRows,
            remainingFailures,
            remainingFailures.Count == 0 ? null : _artifactDirectory);
    }

    public static WriterDrainResult ReplayArtifactDirectory(
        ILogger log,
        string writerKind,
        string artifactDirectory,
        FlushBatch flush)
    {
        if (!Directory.Exists(artifactDirectory))
            throw new DirectoryNotFoundException(artifactDirectory);

        long enqueuedPages = 0;
        long enqueuedRows = 0;
        long flushedPages = 0;
        long flushedRows = 0;
        var failures = new List<WriterBatchFailure>();
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        foreach (var file in Directory.GetFiles(artifactDirectory, "worker-*.json")
                     .OrderBy(static file => file, StringComparer.Ordinal))
        {
            ReplayArtifactPayload? artifact;
            try
            {
                artifact = JsonSerializer.Deserialize<ReplayArtifactPayload>(
                    File.ReadAllText(file),
                    options);
                if (artifact is null || artifact.FormatVersion != 1)
                    throw new InvalidDataException($"Unsupported online replay artifact: {file}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new WriterBatchFailure(
                    writerKind,
                    "artifact",
                    [new WriterFailedScope(Path.GetFileName(file), 0, 0)],
                    ex.GetType().FullName ?? ex.GetType().Name,
                    ex.Message,
                    file,
                    DateTime.UtcNow));
                continue;
            }

            var batch = artifact.Pages
                .Select(static page => (page.SongId, page.Entries))
                .ToList();
            var pages = batch.Count;
            var rows = batch.Sum(static item => (long)item.Entries.Count);
            enqueuedPages += pages;
            enqueuedRows += rows;

            try
            {
                flush(artifact.Instrument, batch);
                flushedPages += pages;
                flushedRows += rows;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new WriterBatchFailure(
                    writerKind,
                    artifact.Instrument,
                    batch
                        .GroupBy(static item => item.SongId, StringComparer.OrdinalIgnoreCase)
                        .Select(static group => new WriterFailedScope(
                            group.Key,
                            group.Count(),
                            group.Sum(static item => (long)item.Entries.Count)))
                        .ToArray(),
                    ex.GetType().FullName ?? ex.GetType().Name,
                    ex.Message,
                    file,
                    DateTime.UtcNow));
                log.LogError(
                    ex,
                    "Persisted online replay failed for {WriterKind}/{Instrument}: {Pages:N0} pages, {Rows:N0} rows.",
                    writerKind,
                    artifact.Instrument,
                    pages,
                    rows);
            }
        }

        return new WriterDrainResult(
            writerKind,
            enqueuedPages,
            enqueuedRows,
            flushedPages,
            flushedRows,
            failures,
            failures.Count == 0 ? null : artifactDirectory);
    }

    private string WriteReplayArtifact(
        int workerIndex,
        string instrument,
        List<(string SongId, IReadOnlyList<T> Entries)> batch,
        Exception exception)
    {
        try
        {
            Directory.CreateDirectory(_artifactDirectory);
            var path = Path.Combine(
                _artifactDirectory,
                $"worker-{workerIndex:D2}-{SanitizeFileName(instrument)}-{Guid.NewGuid():N}.json");
            var json = JsonSerializer.Serialize(
                new ReplayArtifactPayload
                {
                    FormatVersion = 1,
                    WriterKind = _label,
                    Instrument = instrument,
                    FailedAtUtc = DateTime.UtcNow,
                    ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
                    ErrorMessage = exception.Message,
                    Pages = batch.Select(static item => new ReplayArtifactPage
                    {
                        SongId = item.SongId,
                        RowCount = item.Entries.Count,
                        Entries = item.Entries,
                    }).ToArray(),
                },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            return path;
        }
        catch (Exception artifactException)
        {
            _log.LogError(
                artifactException,
                "Failed to persist online writer replay artifact in {Path}.",
                _artifactDirectory);
            return _artifactDirectory;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }
}