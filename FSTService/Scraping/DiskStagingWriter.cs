using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using FSTService.Persistence;

namespace FSTService.Scraping;

/// <summary>
/// Bounded channel-based writer that stages precomputed cache entries to a
/// temporary binary file on disk, then bulk-loads them into PostgreSQL.
/// This prevents the precomputer from holding all entries in RAM at once.
///
/// Format per record: [4B keyLen][keyUTF8][4B jsonLen][jsonBytes][4B etagLen][etagUTF8]
/// </summary>
public sealed class DiskStagingWriter : IAsyncDisposable
{
    private readonly Channel<(string Key, byte[] Json, string ETag)> _channel;
    private readonly string _stagingPath;
    private readonly Task _drainTask;
    private readonly ILogger<DiskStagingWriter> _log;
    private long _recordCount;

    public DiskStagingWriter(ILogger<DiskStagingWriter> log, int channelCapacity = 500)
    {
        _log = log;
        _stagingPath = Path.Combine(Path.GetTempPath(), $"fst_precomp_{Guid.NewGuid():N}.bin");
        _channel = Channel.CreateBounded<(string Key, byte[] Json, string ETag)>(
            new BoundedChannelOptions(channelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            });
        _drainTask = Task.Run(DrainAsync);
    }

    /// <summary>Number of records staged so far.</summary>
    public long RecordCount => Interlocked.Read(ref _recordCount);

    /// <summary>
    /// Write a cache entry to the staging channel.
    /// Called from parallel producer threads; blocks if the channel is full (backpressure).
    /// </summary>
    public void Write(string key, byte[] json, string etag)
    {
        // TryWrite is lock-free when capacity is available.
        // If the channel is full, fall back to a blocking wait.
        if (!_channel.Writer.TryWrite((key, json, etag)))
        {
            _channel.Writer.WriteAsync((key, json, etag)).AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Signal that no more entries will be written.
    /// Must be called after all producer phases complete.
    /// </summary>
    public void Complete() => _channel.Writer.Complete();

    /// <summary>
    /// Wait for the drain task to finish writing all buffered entries to disk.
    /// </summary>
    public Task WaitForDrainAsync() => _drainTask;

    /// <summary>
    /// Stream the staging file into PostgreSQL via bulk UPSERT, then delete the file.
    /// </summary>
    public void FlushToPostgres(IMetaDatabase metaDb)
    {
        if (!File.Exists(_stagingPath) || RecordCount == 0)
        {
            _log.LogInformation("No staged records to flush.");
            return;
        }

        _log.LogInformation("Flushing {Count:N0} staged records from {Path} to PostgreSQL...",
            RecordCount, Path.GetFileName(_stagingPath));

        metaDb.BulkSetCachedResponses(ReadStagingFile());

        _log.LogInformation("Flush complete. Deleting staging file.");
        TryDeleteStagingFile();
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { await _drainTask.ConfigureAwait(false); }
        catch (ChannelClosedException) { }
        TryDeleteStagingFile();
    }

    // ── Private ─────────────────────────────────────────────────

    private async Task DrainAsync()
    {
        var header = new byte[4];
        await using var fs = new FileStream(_stagingPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 1024 * 1024, useAsync: false);

        await foreach (var (key, json, etag) in _channel.Reader.ReadAllAsync())
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            var etagBytes = Encoding.UTF8.GetBytes(etag);

            BinaryPrimitives.WriteInt32LittleEndian(header, keyBytes.Length);
            fs.Write(header);
            fs.Write(keyBytes);

            BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
            fs.Write(header);
            fs.Write(json);

            BinaryPrimitives.WriteInt32LittleEndian(header, etagBytes.Length);
            fs.Write(header);
            fs.Write(etagBytes);

            Interlocked.Increment(ref _recordCount);
        }

        fs.Flush();
    }

    private IEnumerable<(string Key, byte[] Json, string ETag)> ReadStagingFile()
    {
        using var fs = new FileStream(_stagingPath, FileMode.Open, FileAccess.Read,
            FileShare.None, bufferSize: 1024 * 1024, useAsync: false);
        var header = new byte[4];

        while (fs.Position < fs.Length)
        {
            ReadExact(fs, header);
            int keyLen = BinaryPrimitives.ReadInt32LittleEndian(header);
            var keyBuf = new byte[keyLen];
            ReadExact(fs, keyBuf);

            ReadExact(fs, header);
            int jsonLen = BinaryPrimitives.ReadInt32LittleEndian(header);
            var jsonBuf = new byte[jsonLen];
            ReadExact(fs, jsonBuf);

            ReadExact(fs, header);
            int etagLen = BinaryPrimitives.ReadInt32LittleEndian(header);
            var etagBuf = new byte[etagLen];
            ReadExact(fs, etagBuf);

            yield return (Encoding.UTF8.GetString(keyBuf), jsonBuf, Encoding.UTF8.GetString(etagBuf));
        }
    }

    private static void ReadExact(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0) throw new EndOfStreamException("Unexpected end of staging file.");
            offset += read;
        }
    }

    private void TryDeleteStagingFile()
    {
        try { if (File.Exists(_stagingPath)) File.Delete(_stagingPath); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to delete staging file {Path}", _stagingPath); }
    }
}
