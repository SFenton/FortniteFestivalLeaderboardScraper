using System.Buffers.Binary;
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
    private volatile bool _completed;

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
    public void FlushAll(int maxBatchPages = 0, Action<string, int, int>? onInstrumentFlush = null)
    {
        if (!_completed)
            throw new InvalidOperationException("Call Complete() before FlushAll().");

        long totalPages = 0;
        long totalEntries = 0;

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
                    totalEntries += entries.Count;
                    instrumentEntries += entries.Count;
                    totalPages++;
                    instrumentPages++;

                    // Flush chunk when we hit the page limit
                    if (maxBatchPages > 0 && batch.Count >= maxBatchPages)
                    {
                        chunkIndex++;
                        long chunkEntries = batch.Sum(b => b.Entries.Count);
                        _log.LogInformation(
                            "Spool [{Label}/{Instrument}] chunk {Chunk}: {Pages:N0} pages, {Entries:N0} entries...",
                            _label, instrument, chunkIndex, batch.Count, chunkEntries);
                        _flush(instrument, batch);
                        batch = new List<(string SongId, IReadOnlyList<T> Entries)>();
                    }
                }

                // Flush remaining pages
                if (batch.Count > 0)
                {
                    if (chunkIndex > 0)
                    {
                        chunkIndex++;
                        long chunkEntries = batch.Sum(b => b.Entries.Count);
                        _log.LogInformation(
                            "Spool [{Label}/{Instrument}] chunk {Chunk} (final): {Pages:N0} pages, {Entries:N0} entries...",
                            _label, instrument, chunkIndex, batch.Count, chunkEntries);
                    }
                    _flush(instrument, batch);
                }

                _log.LogInformation("Spool [{Label}/{Instrument}] flushed: {Pages:N0} pages, {Entries:N0} entries{Chunks}.",
                    _label, instrument, instrumentPages, instrumentEntries,
                    chunkIndex > 0 ? $" in {chunkIndex} chunks" : "");

                instrumentIndex++;
            }
        }

        _log.LogInformation("Spool [{Label}] FlushAll complete: {Pages:N0} pages, {Entries:N0} entries across {Instruments} instruments.",
            _label, totalPages, totalEntries, _spools.Count);
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
        if (!_completed)
        {
            lock (_spoolsLock)
            {
                foreach (var spool in _spools.Values)
                    spool.WriteStream.Dispose();
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
