using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace FSTService.Scraping.Replay;

internal readonly record struct CaptureJsonLinesMeasurement(
    int RecordCount,
    long Bytes,
    string Sha256);

internal static class CapturePackageJsonLines
{
    private const int ReadBufferBytes = 64 * 1024;
    private static readonly byte[] NewLine = [(byte)'\n'];

    internal static CaptureJsonLinesMeasurement Measure<T>(
        IReadOnlyList<T> rows,
        int maximumRecords,
        long maximumBytes,
        int maximumRecordBytes,
        string description)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ValidateCount(rows.Count, maximumRecords, description);

        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        long bytes = 0;
        foreach (var row in rows)
        {
            if (row is null)
            {
                Invalid(
                    CapturePackageFailureKind.InvalidMetadata,
                    $"{description} records cannot be null.");
            }

            var line = TierZeroCanonicalJson.Serialize(row);
            if (line.Length == 0 ||
                line.Length > maximumRecordBytes)
            {
                Invalid(
                    CapturePackageFailureKind.RecordLimitExceeded,
                    $"{description} contains a record outside its byte limit.");
            }

            try
            {
                bytes = checked(bytes + line.LongLength + 1);
            }
            catch (OverflowException exception)
            {
                throw new CapturePackageException(
                    CapturePackageFailureKind.RecordLimitExceeded,
                    $"{description} byte count overflowed.",
                    exception);
            }
            if (bytes > maximumBytes)
            {
                Invalid(
                    CapturePackageFailureKind.RecordLimitExceeded,
                    $"{description} exceeds its byte limit.");
            }

            hash.AppendData(line);
            hash.AppendData(NewLine);
        }

        return new CaptureJsonLinesMeasurement(
            rows.Count,
            bytes,
            Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant());
    }

    internal static Stream OpenReadStream<T>(
        IReadOnlyList<T> rows,
        int maximumRecordBytes,
        string description) =>
        new CanonicalJsonLinesStream<T>(
            rows,
            maximumRecordBytes,
            description);

    internal static async Task<CaptureJsonLinesMeasurement> ReadAsync<T>(
        Stream stream,
        long expectedBytes,
        int expectedCount,
        int maximumRecords,
        long maximumBytes,
        int maximumRecordBytes,
        string description,
        Action<ReadOnlyMemory<byte>>? preDeserialize,
        Action<T, int, long, int, string> onRecord,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onRecord);
        if (!stream.CanRead)
        {
            throw new ArgumentException(
                "JSONL stream must be readable.",
                nameof(stream));
        }

        ValidateCount(expectedCount, maximumRecords, description);
        if (expectedBytes <= 0 ||
            expectedBytes > maximumBytes ||
            expectedCount >
            expectedBytes /
            CapturePackageFormat.MinimumCanonicalJsonLineBytes)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                $"{description} count and byte bounds are invalid.");
        }

        var readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
        var lineBuffer = new ArrayBufferWriter<byte>(
            Math.Min(maximumRecordBytes, 4 * 1024));
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        long totalBytes = 0;
        long lineOffset = 0;
        var recordCount = 0;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(
                    readBuffer.AsMemory(0, ReadBufferBytes),
                    cancellationToken);
                if (read == 0)
                    break;

                hash.AppendData(readBuffer, 0, read);
                for (var index = 0; index < read; index++)
                {
                    totalBytes++;
                    if (totalBytes > expectedBytes ||
                        totalBytes > maximumBytes)
                    {
                        Invalid(
                            CapturePackageFailureKind.RecordLimitExceeded,
                            $"{description} exceeds its declared byte bound.");
                    }

                    var value = readBuffer[index];
                    if (value != (byte)'\n')
                    {
                        if (lineBuffer.WrittenCount >=
                            maximumRecordBytes)
                        {
                            Invalid(
                                CapturePackageFailureKind.RecordLimitExceeded,
                                $"{description} contains an oversized record.");
                        }
                        lineBuffer.GetSpan(1)[0] = value;
                        lineBuffer.Advance(1);
                        continue;
                    }

                    if (lineBuffer.WrittenCount == 0)
                    {
                        Invalid(
                            CapturePackageFailureKind.NonCanonicalJson,
                            $"{description} contains a blank record.");
                    }
                    if (recordCount >= expectedCount)
                    {
                        Invalid(
                            CapturePackageFailureKind.AggregateMismatch,
                            $"{description} contains more records than declared.");
                    }

                    var line = lineBuffer.WrittenMemory;
                    preDeserialize?.Invoke(line);
                    T parsed;
                    try
                    {
                        parsed = TierZeroCanonicalJson.Deserialize<T>(
                            line.Span);
                    }
                    catch (JsonException exception)
                    {
                        throw new CapturePackageException(
                            CapturePackageFailureKind.InvalidMetadata,
                            $"{description} contains invalid JSON.",
                            exception);
                    }

                    if (!line.Span.SequenceEqual(
                            TierZeroCanonicalJson.Serialize(parsed!)))
                    {
                        Invalid(
                            CapturePackageFailureKind.NonCanonicalJson,
                            $"{description} contains noncanonical JSON.");
                    }

                    onRecord(
                        parsed,
                        recordCount,
                        lineOffset,
                        line.Length,
                        TierZeroCanonicalJson.Sha256Hex(line.Span));
                    recordCount++;
                    lineBuffer.Clear();
                    lineOffset = totalBytes;
                }
            }

            if (lineBuffer.WrittenCount != 0)
            {
                Invalid(
                    CapturePackageFailureKind.NonCanonicalJson,
                    $"{description} must end with a newline.");
            }
            if (totalBytes != expectedBytes ||
                recordCount != expectedCount)
            {
                Invalid(
                    CapturePackageFailureKind.AggregateMismatch,
                    $"{description} count or byte total does not match its descriptor.");
            }

            return new CaptureJsonLinesMeasurement(
                recordCount,
                totalBytes,
                Convert.ToHexString(hash.GetHashAndReset())
                    .ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private static void ValidateCount(
        int count,
        int maximumRecords,
        string description)
    {
        if (count <= 0 ||
            count > maximumRecords)
        {
            Invalid(
                CapturePackageFailureKind.RecordLimitExceeded,
                $"{description} record count is outside its supported bounds.");
        }
    }

    private static void Invalid(
        CapturePackageFailureKind kind,
        string message) =>
        throw new CapturePackageException(kind, message);

    private sealed class CanonicalJsonLinesStream<T> : Stream
    {
        private readonly IReadOnlyList<T> _rows;
        private readonly int _maximumRecordBytes;
        private readonly string _description;
        private byte[]? _current;
        private int _currentOffset;
        private int _rowIndex;

        internal CanonicalJsonLinesStream(
            IReadOnlyList<T> rows,
            int maximumRecordBytes,
            string description)
        {
            _rows = rows;
            _maximumRecordBytes = maximumRecordBytes;
            _description = description;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length =>
            throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0)
                return 0;

            var written = 0;
            while (written < buffer.Length)
            {
                if (!EnsureCurrent())
                    break;

                var available = _current!.Length - _currentOffset;
                var copy = Math.Min(
                    available,
                    buffer.Length - written);
                _current.AsSpan(_currentOffset, copy)
                    .CopyTo(buffer[written..]);
                _currentOffset += copy;
                written += copy;
                if (_currentOffset == _current.Length)
                {
                    _current = null;
                    _currentOffset = 0;
                    _rowIndex++;
                }
            }
            return written;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(
            long offset,
            SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();

        private bool EnsureCurrent()
        {
            if (_current is not null)
                return true;
            if (_rowIndex >= _rows.Count)
                return false;

            var row = _rows[_rowIndex];
            if (row is null)
            {
                Invalid(
                    CapturePackageFailureKind.InvalidMetadata,
                    $"{_description} records cannot be null.");
            }
            var line = TierZeroCanonicalJson.Serialize(row);
            if (line.Length == 0 ||
                line.Length > _maximumRecordBytes)
            {
                Invalid(
                    CapturePackageFailureKind.RecordLimitExceeded,
                    $"{_description} contains a record outside its byte limit.");
            }

            _current = new byte[line.Length + 1];
            line.CopyTo(_current, 0);
            _current[^1] = (byte)'\n';
            return true;
        }
    }
}
