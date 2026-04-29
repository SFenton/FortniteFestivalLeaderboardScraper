using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace FSTService.Scraping;

/// <summary>
/// Generic disk spool that decouples a high-throughput producer from a slower
/// consumer.  Each "instrument" (or band type, or any string key) gets its own
/// binary file and dedicated consumer task, enabling N-way parallel consumption.
///
/// <para>The binary format per record is:</para>
/// <code>[4B songIdLen][songId UTF8][4B entryCount][…serialized entries…]</code>
///
/// Serialization and flush are pluggable via delegates, making this reusable
/// for <see cref="LeaderboardEntry"/>, <see cref="BandLeaderboardEntry"/>,
/// or any future entry type.
/// </summary>
public sealed class SpoolWriter<T> : IAsyncDisposable
{
    /// <summary>Serialize a page of entries into the buffer. Called per-Enqueue.</summary>
    public delegate void SerializePage(MemoryStream buf, byte[] header, string songId, IReadOnlyList<T> entries);

    /// <summary>Deserialize one record from the stream. Returns (songId, entries).</summary>
    public delegate (string SongId, IReadOnlyList<T> Entries) DeserializePage(Stream stream, byte[] header);

    /// <summary>Flush a batch of pages for one instrument to the database.</summary>
    public delegate void FlushBatch(string instrument, List<(string SongId, IReadOnlyList<T> Entries)> batch);

    /// <summary>Progress snapshot emitted before, during, and after chunk flushes.</summary>
    public sealed class FlushProgress
    {
        public string Label { get; init; } = "";
        public string Instrument { get; init; } = "";
        public int InstrumentIndex { get; init; }
        public int InstrumentsCompleted { get; init; }
        public int InstrumentsTotal { get; init; }
        public long PagesFlushed { get; init; }
        public long PagesTotal { get; init; }
        public long EntriesFlushed { get; init; }
        public long EntriesTotal { get; init; }
        public long InstrumentPagesFlushed { get; init; }
        public long InstrumentPagesTotal { get; init; }
        public long InstrumentEntriesFlushed { get; init; }
        public long InstrumentEntriesTotal { get; init; }
        public int ChunkIndex { get; init; }
        public int ChunkTotal { get; init; }
        public int ChunkPages { get; init; }
        public long ChunkEntries { get; init; }
        public string State { get; init; } = "";
        public double ActiveChunkElapsedSeconds { get; init; }
        public DateTime UpdatedAtUtc { get; init; }
    }

    private readonly string _spoolDir;
    private readonly string _label;
    private readonly ILogger _log;
    private readonly SerializePage _serialize;
    private readonly DeserializePage _deserialize;
    private readonly FlushBatch _flush;
    private readonly Dictionary<string, InstrumentSpool> _spools = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _spoolsLock = new();
    private long _recordCount;
    private long _entryCount;
    private int _disposed;
    private volatile bool _completed;

    /// <summary>Directory containing this writer's transient spool files.</summary>
    public string SpoolDirectory => _spoolDir;

    /// <summary>Number of page records written across all instruments.</summary>
    public long RecordCount => Interlocked.Read(ref _recordCount);

    /// <summary>Total individual entries written across all pages.</summary>
    public long EntryCount => Interlocked.Read(ref _entryCount);

    /// <summary>Whether the writer has been signalled complete (no more data).</summary>
    public bool IsCompleted => _completed;

    /// <summary>Sum of all per-instrument spool file sizes.</summary>
    public long TotalBytesWritten
    {
        get
        {
            long total = 0;
            lock (_spoolsLock)
            {
                foreach (var spool in _spools.Values)
                    total += spool.FlushedPosition;
            }
            return total;
        }
    }

    private sealed class InstrumentSpool
    {
        public readonly FileStream WriteStream;
        public readonly string Path;
        public readonly object WriteLock = new();
        public long FlushedPosition;
        public long RecordCount;
        public long EntryCount;

        public InstrumentSpool(string path)
        {
            Path = path;
            WriteStream = new FileStream(path, FileMode.Create, FileAccess.Write,
                FileShare.Read, bufferSize: 1024 * 1024, useAsync: false);
        }
    }

    /// <param name="log">Logger instance.</param>
    /// <param name="label">Human-readable label for log messages (e.g. "solo", "band").</param>
    /// <param name="serialize">Writes one page record into the buffer.</param>
    /// <param name="deserialize">Reads one page record from the stream.</param>
    /// <param name="flush">Persists a batch of pages for one instrument.</param>
    /// <param name="baseDirectory">Base directory for spool files. Defaults to system temp.</param>
    public SpoolWriter(ILogger log, string label,
                       SerializePage serialize,
                       DeserializePage deserialize,
                       FlushBatch flush,
                       string? baseDirectory = null)
    {
        _log = log;
        _label = label;
        _serialize = serialize;
        _deserialize = deserialize;
        _flush = flush;
        _spoolDir = Path.Combine(baseDirectory ?? System.IO.Path.GetTempPath(), $"fst_scrape_{label}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_spoolDir);
        _log.LogInformation("Spool [{Label}] directory created: {Path}", _label, _spoolDir);
    }

    private InstrumentSpool GetOrCreateSpool(string instrument)
    {
        lock (_spoolsLock)
        {
            if (_spools.TryGetValue(instrument, out var existing))
                return existing;

            var path = System.IO.Path.Combine(_spoolDir, $"{instrument}.bin");
            var spool = new InstrumentSpool(path);
            _spools[instrument] = spool;
            _log.LogDebug("Spool [{Label}] created file for {Instrument}: {Path}", _label, instrument, path);
            return spool;
        }
    }

    /// <summary>
    /// Append a page of entries to the per-instrument spool file.  Thread-safe
    /// per instrument — each instrument has its own lock and file.
    /// </summary>
    public void Enqueue(string songId, string instrument, IReadOnlyList<T> entries)
    {
        if (entries.Count == 0) return;

        var spool = GetOrCreateSpool(instrument);

        var buf = new MemoryStream(entries.Count * 200);
        var header = new byte[8];
        _serialize(buf, header, songId, entries);

        var bytes = buf.GetBuffer();
        var length = (int)buf.Length;
        lock (spool.WriteLock)
        {
            spool.WriteStream.Write(bytes, 0, length);
            spool.WriteStream.Flush();
            Interlocked.Exchange(ref spool.FlushedPosition, spool.WriteStream.Position);
        }

        Interlocked.Increment(ref spool.RecordCount);
        Interlocked.Add(ref spool.EntryCount, entries.Count);
        Interlocked.Increment(ref _recordCount);
        Interlocked.Add(ref _entryCount, entries.Count);
    }

    /// <summary>
    /// Signal that no more pages will be written.
    /// </summary>
    public void Complete()
    {
        _completed = true;
        // Close all write streams so FlushAll can read them
        lock (_spoolsLock)
        {
            foreach (var spool in _spools.Values)
                spool.WriteStream.Dispose();
        }
        _log.LogInformation("Spool [{Label}] writer completed: {Records:N0} records, {Entries:N0} entries, {Bytes:N0} bytes across {Instruments} instruments.",
            _label, RecordCount, EntryCount, TotalBytesWritten, _spools.Count);
    }

    /// <summary>
    /// Read all spool files and flush each instrument's data in one batch.
    /// Call after <see cref="Complete"/>. This is the post-fetch flush path —
    /// no live consumers, no memory accumulation during fetch.
    /// </summary>
    /// <param name="maxBatchPages">Maximum pages per flush call. 0 = unlimited (flush all pages in one call).
    /// When &gt; 0, pages are accumulated up to the limit, flushed, then the next chunk begins.
    /// Each chunk results in an independent flush delegate call (and therefore its own DB transaction).</param>
    /// <param name="onInstrumentFlush">Optional callback invoked before each instrument flush with (instrument, completedSoFar, totalInstruments).</param>
    /// <param name="onProgress">Optional callback invoked at instrument/chunk boundaries and every heartbeat interval while a chunk is being flushed.</param>
    /// <param name="heartbeatInterval">How often to invoke <paramref name="onProgress"/> while a chunk flush is in-flight. Defaults to one second.</param>
    public void FlushAll(
        int maxBatchPages = 0,
        Action<string, int, int>? onInstrumentFlush = null,
        Action<FlushProgress>? onProgress = null,
        TimeSpan? heartbeatInterval = null)
    {
        if (!_completed)
            throw new InvalidOperationException("Call Complete() before FlushAll().");

        var heartbeatEvery = heartbeatInterval ?? TimeSpan.FromSeconds(1);
        long flushedPages = 0;
        long flushedEntries = 0;
        var totalPages = RecordCount;
        var totalEntries = EntryCount;

        lock (_spoolsLock)
        {
            int instrumentIndex = 0;
            int instrumentCount = _spools.Count;

            foreach (var (instrument, spool) in _spools)
            {
                if (spool.FlushedPosition == 0)
                {
                    instrumentIndex++;
                    continue;
                }

                onInstrumentFlush?.Invoke(instrument, instrumentIndex, instrumentCount);

                _log.LogInformation("Spool [{Label}] flushing {Instrument}: {Size:N0} bytes...",
                    _label, instrument, spool.FlushedPosition);

                var instrumentTotalPages = Interlocked.Read(ref spool.RecordCount);
                var instrumentTotalEntries = Interlocked.Read(ref spool.EntryCount);
                var chunkTotal = maxBatchPages > 0
                    ? (int)Math.Ceiling((double)instrumentTotalPages / maxBatchPages)
                    : instrumentTotalPages > 0 ? 1 : 0;
                long instrumentPagesFlushed = 0;
                long instrumentEntriesFlushed = 0;

                EmitFlushProgress(
                    onProgress,
                    instrument,
                    instrumentIndex,
                    instrumentCount,
                    flushedPages,
                    totalPages,
                    flushedEntries,
                    totalEntries,
                    instrumentPagesFlushed,
                    instrumentTotalPages,
                    instrumentEntriesFlushed,
                    instrumentTotalEntries,
                    chunkIndex: 0,
                    chunkTotal,
                    chunkPages: 0,
                    chunkEntries: 0,
                    state: "instrument_started",
                    activeChunkElapsed: TimeSpan.Zero);

                using var readStream = new FileStream(spool.Path, FileMode.Open, FileAccess.Read,
                    FileShare.None, bufferSize: 4 * 1024 * 1024, useAsync: false);

                var header = new byte[8];
                var batch = new List<(string SongId, IReadOnlyList<T> Entries)>();
                long instrumentPages = 0;
                long instrumentEntries = 0;
                int chunkIndex = 0;

                while (readStream.Position < readStream.Length)
                {
                    var (songId, entries) = _deserialize(readStream, header);
                    batch.Add((songId, entries));
                    instrumentEntries += entries.Count;
                    instrumentPages++;

                    // Flush chunk when we hit the page limit
                    if (maxBatchPages > 0 && batch.Count >= maxBatchPages)
                    {
                        chunkIndex++;
                        FlushChunk(instrument, batch, chunkIndex, chunkTotal, isFinalChunk: false);
                        batch = new List<(string SongId, IReadOnlyList<T> Entries)>();
                    }
                }

                // Flush remaining pages
                if (batch.Count > 0)
                {
                    if (chunkIndex > 0)
                    {
                        chunkIndex++;
                    }
                    else
                    {
                        chunkIndex = 1;
                    }

                    FlushChunk(instrument, batch, chunkIndex, chunkTotal, isFinalChunk: maxBatchPages > 0 && chunkIndex == chunkTotal);
                }

                _log.LogInformation("Spool [{Label}/{Instrument}] flushed: {Pages:N0} pages, {Entries:N0} entries{Chunks}.",
                    _label, instrument, instrumentPages, instrumentEntries,
                    chunkIndex > 0 ? $" in {chunkIndex} chunks" : "");

                EmitFlushProgress(
                    onProgress,
                    instrument,
                    instrumentIndex + 1,
                    instrumentCount,
                    flushedPages,
                    totalPages,
                    flushedEntries,
                    totalEntries,
                    instrumentPagesFlushed,
                    instrumentTotalPages,
                    instrumentEntriesFlushed,
                    instrumentTotalEntries,
                    chunkIndex,
                    chunkTotal,
                    chunkPages: 0,
                    chunkEntries: 0,
                    state: "instrument_completed",
                    activeChunkElapsed: TimeSpan.Zero);

                instrumentIndex++;

                void FlushChunk(
                    string currentInstrument,
                    List<(string SongId, IReadOnlyList<T> Entries)> currentBatch,
                    int currentChunkIndex,
                    int currentChunkTotal,
                    bool isFinalChunk)
                {
                    var chunkPages = currentBatch.Count;
                    var chunkEntries = currentBatch.Sum(static b => b.Entries.Count);
                    var chunkLabel = isFinalChunk ? " (final)" : string.Empty;

                    if (maxBatchPages > 0)
                    {
                        _log.LogInformation(
                            "Spool [{Label}/{Instrument}] chunk {Chunk}/{ChunkTotal}{Final}: {Pages:N0} pages, {Entries:N0} entries...",
                            _label, currentInstrument, currentChunkIndex, currentChunkTotal, chunkLabel, chunkPages, chunkEntries);
                    }

                    var chunkStopwatch = Stopwatch.StartNew();
                    ReportChunkProgress("running", chunkStopwatch.Elapsed);
                    FlushWithHeartbeat(
                        () => _flush(currentInstrument, currentBatch),
                        () => ReportChunkProgress("running", chunkStopwatch.Elapsed),
                        heartbeatEvery,
                        onProgress is not null);
                    chunkStopwatch.Stop();

                    flushedPages += chunkPages;
                    flushedEntries += chunkEntries;
                    instrumentPagesFlushed += chunkPages;
                    instrumentEntriesFlushed += chunkEntries;
                    ReportChunkProgress("completed", chunkStopwatch.Elapsed);

                    void ReportChunkProgress(string state, TimeSpan elapsed)
                    {
                        EmitFlushProgress(
                            onProgress,
                            currentInstrument,
                            instrumentIndex,
                            instrumentCount,
                            flushedPages,
                            totalPages,
                            flushedEntries,
                            totalEntries,
                            instrumentPagesFlushed,
                            instrumentTotalPages,
                            instrumentEntriesFlushed,
                            instrumentTotalEntries,
                            currentChunkIndex,
                            currentChunkTotal,
                            chunkPages,
                            chunkEntries,
                            state,
                            elapsed);
                    }
                }
            }
        }

        _log.LogInformation("Spool [{Label}] FlushAll complete: {Pages:N0} pages, {Entries:N0} entries across {Instruments} instruments.",
            _label, flushedPages, flushedEntries, _spools.Count);
    }

    private void EmitFlushProgress(
        Action<FlushProgress>? onProgress,
        string instrument,
        int instrumentsCompleted,
        int instrumentsTotal,
        long pagesFlushed,
        long pagesTotal,
        long entriesFlushed,
        long entriesTotal,
        long instrumentPagesFlushed,
        long instrumentPagesTotal,
        long instrumentEntriesFlushed,
        long instrumentEntriesTotal,
        int chunkIndex,
        int chunkTotal,
        int chunkPages,
        long chunkEntries,
        string state,
        TimeSpan activeChunkElapsed)
    {
        if (onProgress is null)
            return;

        try
        {
            onProgress(new FlushProgress
            {
                Label = _label,
                Instrument = instrument,
                InstrumentIndex = Math.Min(instrumentsTotal, instrumentsCompleted + 1),
                InstrumentsCompleted = instrumentsCompleted,
                InstrumentsTotal = instrumentsTotal,
                PagesFlushed = pagesFlushed,
                PagesTotal = pagesTotal,
                EntriesFlushed = entriesFlushed,
                EntriesTotal = entriesTotal,
                InstrumentPagesFlushed = instrumentPagesFlushed,
                InstrumentPagesTotal = instrumentPagesTotal,
                InstrumentEntriesFlushed = instrumentEntriesFlushed,
                InstrumentEntriesTotal = instrumentEntriesTotal,
                ChunkIndex = chunkIndex,
                ChunkTotal = chunkTotal,
                ChunkPages = chunkPages,
                ChunkEntries = chunkEntries,
                State = state,
                ActiveChunkElapsedSeconds = Math.Round(activeChunkElapsed.TotalSeconds, 1),
                UpdatedAtUtc = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Spool [{Label}] progress callback failed.", _label);
        }
    }

    private void FlushWithHeartbeat(Action flush, Action heartbeat, TimeSpan heartbeatInterval, bool enabled)
    {
        if (!enabled || heartbeatInterval <= TimeSpan.Zero)
        {
            flush();
            return;
        }

        using var cts = new CancellationTokenSource();
        var heartbeatTask = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(heartbeatInterval);
                while (await timer.WaitForNextTickAsync(cts.Token))
                {
                    try
                    {
                        heartbeat();
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "Spool [{Label}] heartbeat callback failed.", _label);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        try
        {
            flush();
        }
        finally
        {
            cts.Cancel();
            try
            {
                heartbeatTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Start per-instrument consumer tasks that live-tail their spool files.
    /// Returns a task that completes when all consumers have drained.
    /// </summary>
    public Task StartConsumerAsync(int batchSize, CancellationToken ct)
    {
        return Task.Run(() => RunConsumerCoordinator(batchSize, ct), ct);
    }

    private void RunConsumerCoordinator(int batchSize, CancellationToken ct)
    {
        var consumerTasks = new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            lock (_spoolsLock)
            {
                foreach (var (instrument, spool) in _spools)
                {
                    if (!consumerTasks.ContainsKey(instrument))
                    {
                        var instr = instrument;
                        var sp = spool;
                        consumerTasks[instrument] = Task.Run(() =>
                            RunInstrumentConsumer(instr, sp, batchSize, ct), ct);
                        _log.LogDebug("Spool [{Label}] started consumer for {Instrument}.", _label, instrument);
                    }
                }
            }

            if (_completed)
            {
                lock (_spoolsLock)
                {
                    foreach (var (instrument, spool) in _spools)
                    {
                        if (!consumerTasks.ContainsKey(instrument))
                        {
                            var instr = instrument;
                            var sp = spool;
                            consumerTasks[instrument] = Task.Run(() =>
                                RunInstrumentConsumer(instr, sp, batchSize, ct), ct);
                        }
                    }
                }
                break;
            }

            Thread.Sleep(50);
        }

        Task.WaitAll(consumerTasks.Values.ToArray(), ct);
        _log.LogInformation("Spool [{Label}] all {Count} consumers finished.", _label, consumerTasks.Count);
    }

    private void RunInstrumentConsumer(string instrument, InstrumentSpool spool,
                                       int batchSize, CancellationToken ct)
    {
        using var readStream = new FileStream(spool.Path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, bufferSize: 1024 * 1024, useAsync: false);

        var header = new byte[8];
        var batch = new List<(string SongId, IReadOnlyList<T> Entries)>(batchSize);
        long pagesConsumed = 0;
        long entriesConsumed = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            long available = Interlocked.Read(ref spool.FlushedPosition) - readStream.Position;

            if (available <= 0)
            {
                if (_completed && readStream.Position >= Interlocked.Read(ref spool.FlushedPosition))
                {
                    if (batch.Count > 0)
                    {
                        _flush(instrument, batch);
                        entriesConsumed += batch.Sum(b => b.Entries.Count);
                        pagesConsumed += batch.Count;
                        batch.Clear();
                    }
                    break;
                }

                Thread.Sleep(1);
                continue;
            }

            var (songId, entries) = _deserialize(readStream, header);
            batch.Add((songId, entries));

            if (batch.Count >= batchSize)
            {
                _flush(instrument, batch);
                entriesConsumed += batch.Sum(b => b.Entries.Count);
                pagesConsumed += batch.Count;
                batch.Clear();
            }
        }

        _log.LogInformation("Spool [{Label}/{Instrument}] consumer finished: {Pages:N0} pages, {Entries:N0} entries.",
            _label, instrument, pagesConsumed, entriesConsumed);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        if (!_completed)
        {
            lock (_spoolsLock)
            {
                foreach (var spool in _spools.Values)
                {
                    try { spool.WriteStream.Dispose(); }
                    catch (Exception ex) { _log.LogDebug(ex, "Failed to close spool file {Path} during dispose.", spool.Path); }
                }
            }
        }
        TryDeleteSpoolDir();
        return ValueTask.CompletedTask;
    }

    private void TryDeleteSpoolDir()
    {
        try { if (Directory.Exists(_spoolDir)) Directory.Delete(_spoolDir, true); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to delete spool directory {Path}", _spoolDir); }
    }

    /// <summary>
    /// Delete any leftover spool directories from previous runs.
    /// </summary>
    public static void CleanupStaleFiles(ILogger log)
    {
        try
        {
            var staleDirs = Directory.GetDirectories(System.IO.Path.GetTempPath(), "fst_scrape_*");
            foreach (var dir in staleDirs)
            {
                Directory.Delete(dir, true);
                log.LogInformation("Deleted stale spool directory: {Path}", dir);
            }

            var staleFiles = Directory.GetFiles(System.IO.Path.GetTempPath(), "fst_scrape_*.bin");
            foreach (var file in staleFiles)
            {
                File.Delete(file);
                log.LogInformation("Deleted stale spool file: {Path}", file);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to clean up stale spool files.");
        }
    }

    // ── Static binary helpers (shared by all serializers) ────────

    public static void WriteString(MemoryStream buf, byte[] header, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        buf.Write(header, 0, 4);
        buf.Write(bytes);
    }

    public static void WriteNullableString(MemoryStream buf, byte[] header, string? value)
    {
        if (value is null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header, 0);
            buf.Write(header, 0, 4);
        }
        else
        {
            WriteString(buf, header, value);
        }
    }

    public static void WriteInt32(MemoryStream buf, byte[] header, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(header, value);
        buf.Write(header, 0, 4);
    }

    public static void WriteNullableInt32(MemoryStream buf, byte[] header, int? value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(header, value ?? -1);
        buf.Write(header, 0, 4);
    }

    public static string ReadString(Stream stream, byte[] header)
    {
        ReadExact(stream, header.AsSpan(0, 4));
        int len = BinaryPrimitives.ReadInt32LittleEndian(header);
        var buf = new byte[len];
        ReadExact(stream, buf);
        return Encoding.UTF8.GetString(buf);
    }

    public static string? ReadNullableString(Stream stream, byte[] header)
    {
        ReadExact(stream, header.AsSpan(0, 4));
        int len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len == 0) return null;
        var buf = new byte[len];
        ReadExact(stream, buf);
        return Encoding.UTF8.GetString(buf);
    }

    public static int ReadInt32(Stream stream, byte[] header)
    {
        ReadExact(stream, header.AsSpan(0, 4));
        return BinaryPrimitives.ReadInt32LittleEndian(header);
    }

    public static int? ReadNullableInt32(Stream stream, byte[] header)
    {
        ReadExact(stream, header.AsSpan(0, 4));
        int val = BinaryPrimitives.ReadInt32LittleEndian(header);
        return val == -1 ? null : val;
    }

    public static double ReadDouble(Stream stream, byte[] header)
    {
        ReadExact(stream, header.AsSpan(0, 8));
        return BinaryPrimitives.ReadDoubleLittleEndian(header);
    }

    public static void ReadExact(Stream stream, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer[offset..]);
            if (read == 0) throw new EndOfStreamException("Unexpected end of spool file.");
            offset += read;
        }
    }
}
