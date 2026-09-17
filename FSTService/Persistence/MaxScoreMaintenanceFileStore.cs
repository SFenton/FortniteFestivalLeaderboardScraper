using System.Security.Cryptography;
using System.Text.Json;
using FSTService.Scraping;

namespace FSTService.Persistence;

internal static class MaxScoreMaintenanceFileStore
{
    internal static async Task<MaxScoreMaintenanceStageRequest>
        LoadStageRequestAsync(
            string dataDirectory,
            string requestedPath,
            CancellationToken ct)
    {
        var path = ResolveExistingJsonInputPath(
            dataDirectory,
            requestedPath,
            MaxScoreMaintenanceManifest.MaximumManifestBytes);
        var payload = await File.ReadAllBytesAsync(path, ct);
        MaxScoreMaintenanceStageRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<
                MaxScoreMaintenanceStageRequest>(
                payload,
                MaxScoreMaintenanceJson.Strict);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                "Max-score maintenance stage request is not valid strict JSON.",
                nameof(requestedPath),
                ex);
        }

        var normalized = (request
                ?? throw new ArgumentException(
                    "Max-score maintenance stage request cannot be JSON null.",
                    nameof(requestedPath)))
            .ValidateAndNormalize();
        var canonical = normalized.SerializeCanonical();
        if (!payload.AsSpan().SequenceEqual(canonical))
        {
            throw new ArgumentException(
                "Max-score maintenance stage request must use the canonical versioned JSON encoding.",
                nameof(requestedPath));
        }

        return normalized;
    }

    internal static async Task<MaxScoreMaintenanceManifest>
        LoadManifestAsync(
            string dataDirectory,
            string requestedPath,
            CancellationToken ct)
    {
        var path = ResolveExistingJsonInputPath(
            dataDirectory,
            requestedPath,
            MaxScoreMaintenanceManifest.MaximumManifestBytes);
        var payload = await File.ReadAllBytesAsync(path, ct);
        MaxScoreMaintenanceManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<
                MaxScoreMaintenanceManifest>(
                payload,
                MaxScoreMaintenanceJson.Strict);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                "Max-score maintenance manifest is not valid strict JSON.",
                nameof(requestedPath),
                ex);
        }

        var normalized = (manifest
                ?? throw new ArgumentException(
                    "Max-score maintenance manifest cannot be JSON null.",
                    nameof(requestedPath)))
            .ValidateAndNormalize();
        var canonical = normalized.SerializeCanonical();
        if (!payload.AsSpan().SequenceEqual(canonical))
        {
            throw new ArgumentException(
                "Max-score maintenance manifest must use the canonical versioned JSON encoding.",
                nameof(requestedPath));
        }

        return normalized;
    }

    internal static async Task<MaxScoreMaintenanceRollbackSnapshot>
        LoadRollbackSnapshotAsync(
            string dataDirectory,
            string requestedPath,
            CancellationToken ct)
        => (await LoadCanonicalRollbackSnapshotAsync(
            dataDirectory,
            requestedPath,
            ct)).Snapshot;

    internal static async Task<MaxScoreMaintenanceApplyReport>
        LoadApplyReportAsync(
            string dataDirectory,
            string requestedPath,
            CancellationToken ct)
    {
        var path = ResolveExistingJsonInputPath(
            dataDirectory,
            requestedPath,
            MaxScoreMaintenanceManifest.MaximumManifestBytes);
        var payload = await File.ReadAllBytesAsync(path, ct);
        try
        {
            return (JsonSerializer.Deserialize<
                        MaxScoreMaintenanceApplyReport>(
                        payload,
                        MaxScoreMaintenanceJson.Strict)
                    ?? throw new ArgumentException(
                        "Max-score maintenance apply report cannot be JSON null.",
                        nameof(requestedPath)))
                .ValidateContract();
        }
        catch (Exception ex) when (
            ex is JsonException
                or ArgumentException
                or InvalidOperationException)
        {
            throw new ArgumentException(
                $"Max-score maintenance apply report must be strict version {MaxScoreMaintenanceApplyReport.CurrentReportVersion} JSON.",
                nameof(requestedPath),
                ex);
        }
    }

    internal static async Task<MaxScoreMaintenanceRollbackReport>
        LoadRollbackReportAsync(
            string dataDirectory,
            string requestedPath,
            CancellationToken ct)
    {
        var path = ResolveExistingJsonInputPath(
            dataDirectory,
            requestedPath,
            MaxScoreMaintenanceManifest.MaximumManifestBytes);
        var payload = await File.ReadAllBytesAsync(path, ct);
        try
        {
            return (JsonSerializer.Deserialize<
                        MaxScoreMaintenanceRollbackReport>(
                        payload,
                        MaxScoreMaintenanceJson.Strict)
                    ?? throw new ArgumentException(
                        "Max-score maintenance rollback report cannot be JSON null.",
                        nameof(requestedPath)))
                .ValidateContract();
        }
        catch (Exception ex) when (
            ex is JsonException
                or ArgumentException
                or InvalidOperationException)
        {
            throw new ArgumentException(
                $"Max-score maintenance rollback report must be strict version {MaxScoreMaintenanceRollbackReport.CurrentReportVersion} JSON.",
                nameof(requestedPath),
                ex);
        }
    }

    internal static async Task<(
            string FullPath,
            string Sha256,
            MaxScoreMaintenanceRollbackSnapshot Snapshot,
            byte[] CanonicalBytes)>
        LoadCanonicalRollbackSnapshotAsync(
            string dataDirectory,
            string requestedPath,
            CancellationToken ct)
    {
        var path = ResolveExistingJsonInputPath(
            dataDirectory,
            requestedPath,
            MaxScoreMaintenanceManifest.MaximumManifestBytes);
        var payload = await File.ReadAllBytesAsync(path, ct);
        MaxScoreMaintenanceRollbackSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<
                MaxScoreMaintenanceRollbackSnapshot>(
                payload,
                MaxScoreMaintenanceJson.Strict);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                "Max-score maintenance rollback snapshot is not valid strict JSON.",
                nameof(requestedPath),
                ex);
        }

        var normalized = (snapshot
                ?? throw new ArgumentException(
                    "Max-score maintenance rollback snapshot cannot be JSON null.",
                    nameof(requestedPath)))
            .ValidateAndNormalize();
        var canonical = normalized.SerializeCanonical();
        if (!payload.AsSpan().SequenceEqual(canonical))
        {
            throw new ArgumentException(
                "Max-score maintenance rollback snapshot must use the canonical versioned JSON encoding.",
                nameof(requestedPath));
        }

        return (
            path,
            Convert.ToHexStringLower(SHA256.HashData(payload)),
            normalized,
            canonical);
    }

    internal static async Task<MaxScoreMaintenanceRollbackSnapshot>
        ValidateCanonicalRollbackSnapshotAsync(
            string dataDirectory,
            string requestedPath,
            string expectedSha256,
            MaxScoreMaintenanceRollbackSnapshot expectedSnapshot,
            CancellationToken ct)
    {
        expectedSha256 =
            MaxScoreMaintenanceManifest.NormalizeSha256(
                expectedSha256,
                nameof(expectedSha256));
        var loaded = await LoadCanonicalRollbackSnapshotAsync(
            dataDirectory,
            requestedPath,
            ct);
        var expectedBytes =
            expectedSnapshot.ValidateAndNormalize()
                .SerializeCanonical();
        if (!string.Equals(
                loaded.Sha256,
                expectedSha256,
                StringComparison.Ordinal)
            || !loaded.CanonicalBytes.AsSpan()
                .SequenceEqual(expectedBytes))
        {
            throw new InvalidOperationException(
                "Persisted rollback snapshot does not match its checkpointed SHA-256 and maintenance identity.");
        }

        return loaded.Snapshot;
    }

    internal static async Task<(string FullPath, string Sha256)>
        WriteCanonicalManifestAsync(
            string dataDirectory,
            string requestedPath,
            MaxScoreMaintenanceManifest manifest,
            CancellationToken ct)
        => await WriteNewBytesAsync(
            dataDirectory,
            requestedPath,
            manifest.SerializeCanonical(),
            ct);

    internal static async Task<(string FullPath, string Sha256)>
        WriteCanonicalRollbackSnapshotAsync(
            string dataDirectory,
            string requestedPath,
            MaxScoreMaintenanceRollbackSnapshot snapshot,
            CancellationToken ct)
    {
        var payload = snapshot.SerializeCanonical();
        return await WriteOrValidateBytesAsync(
            dataDirectory,
            requestedPath,
            payload,
            ct);
    }

    internal static async Task<(string FullPath, string Sha256)>
        WriteNewReportAsync<T>(
            string dataDirectory,
            string requestedPath,
            T report,
            CancellationToken ct)
    {
        ValidateReportContract(report);
        return await WriteNewBytesAsync(
            dataDirectory,
            requestedPath,
            JsonSerializer.SerializeToUtf8Bytes(
                report,
                MaxScoreMaintenanceJson.Report),
            ct);
    }

    internal static NewReportReservation ReserveNewReport(
        string dataDirectory,
        string requestedPath)
    {
        var fullPath = ResolveNewJsonOutputPath(
            dataDirectory,
            requestedPath);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(
                "Max-score maintenance report output has no parent directory.",
                nameof(requestedPath));
        Directory.CreateDirectory(parent);
        var (dataRoot, _) = ResolveUnderDataDirectory(
            dataDirectory,
            requestedPath);
        EnsureNoSymbolicLinks(dataRoot, fullPath);
        try
        {
            return new NewReportReservation(
                fullPath,
                new FileStream(
                    fullPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    16 * 1024,
                    FileOptions.Asynchronous
                    | FileOptions.WriteThrough));
        }
        catch (IOException ex)
        {
            throw new ArgumentException(
                "Max-score maintenance report output could not be reserved as a new file.",
                nameof(requestedPath),
                ex);
        }
    }

    internal static string ResolveNewJsonOutputPath(
        string dataDirectory,
        string requestedPath)
    {
        var (dataRoot, fullPath) = ResolveUnderDataDirectory(
            dataDirectory,
            requestedPath);
        RequireJsonExtension(fullPath, nameof(requestedPath));
        EnsureNoSymbolicLinks(dataRoot, fullPath);
        if (File.Exists(fullPath)
            || Directory.Exists(fullPath)
            || GetLinkTarget(fullPath) is not null)
        {
            throw new ArgumentException(
                "Max-score maintenance output path must not already exist.",
                nameof(requestedPath));
        }

        return fullPath;
    }

    internal static string ResolveExistingJsonInputPath(
        string dataDirectory,
        string requestedPath,
        long maximumBytes)
    {
        var (dataRoot, fullPath) = ResolveUnderDataDirectory(
            dataDirectory,
            requestedPath);
        RequireJsonExtension(fullPath, nameof(requestedPath));
        EnsureNoSymbolicLinks(dataRoot, fullPath);
        var file = new FileInfo(fullPath);
        if (!file.Exists
            || file.LinkTarget is not null
            || (file.Attributes
                & (FileAttributes.Directory | FileAttributes.ReparsePoint))
                != 0)
        {
            throw new ArgumentException(
                "Max-score maintenance input must be a regular non-symbolic-link JSON file.",
                nameof(requestedPath));
        }
        if (file.Length is <= 0 || file.Length > maximumBytes)
        {
            throw new ArgumentException(
                $"Max-score maintenance input must be between 1 and {maximumBytes:N0} bytes.",
                nameof(requestedPath));
        }

        return fullPath;
    }

    internal static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, ct));
    }

    private static async Task<(string FullPath, string Sha256)>
        WriteNewBytesAsync(
            string dataDirectory,
            string requestedPath,
            byte[] payload,
            CancellationToken ct)
    {
        var fullPath = ResolveNewJsonOutputPath(
            dataDirectory,
            requestedPath);
        return await WriteBytesAsync(fullPath, payload, ct);
    }

    private static void ValidateReportContract<T>(
        T report)
    {
        if (report is MaxScoreMaintenancePlanReport planReport)
            planReport.ValidateContract();
        else if (report is MaxScoreMaintenanceApplyReport applyReport)
            applyReport.ValidateContract();
        else if (report is
                 MaxScoreMaintenanceRollbackReport rollbackReport)
            rollbackReport.ValidateContract();
    }

    internal sealed class NewReportReservation
        : IAsyncDisposable
    {
        private FileStream? _stream;
        private bool _written;

        internal NewReportReservation(
            string fullPath,
            FileStream stream)
        {
            FullPath = fullPath;
            _stream = stream;
        }

        internal string FullPath { get; }

        internal async Task WriteAsync<T>(
            T report,
            CancellationToken ct)
        {
            ValidateReportContract(report);
            var stream = _stream
                ?? throw new InvalidOperationException(
                    "The reserved max-score report has already been written or disposed.");
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                report,
                MaxScoreMaintenanceJson.Report);
            stream.SetLength(0);
            stream.Position = 0;
            await stream.WriteAsync(payload, ct);
            await stream.FlushAsync(ct);
            stream.Flush(flushToDisk: true);
            await stream.DisposeAsync();
            _stream = null;
            _written = true;
        }

        public async ValueTask DisposeAsync()
        {
            var stream = Interlocked.Exchange(
                ref _stream,
                null);
            if (stream is not null)
                await stream.DisposeAsync();
            if (!_written)
            {
                try
                {
                    File.Delete(FullPath);
                }
                catch
                {
                }
            }
        }
    }

    private static async Task<(string FullPath, string Sha256)>
        WriteOrValidateBytesAsync(
            string dataDirectory,
            string requestedPath,
            byte[] payload,
            CancellationToken ct)
    {
        var (dataRoot, fullPath) = ResolveUnderDataDirectory(
            dataDirectory,
            requestedPath);
        RequireJsonExtension(fullPath, nameof(requestedPath));
        EnsureNoSymbolicLinks(dataRoot, fullPath);
        if (File.Exists(fullPath))
        {
            var existing = await File.ReadAllBytesAsync(fullPath, ct);
            if (!existing.AsSpan().SequenceEqual(payload))
            {
                throw new InvalidOperationException(
                    "Existing rollback snapshot does not match the resumable maintenance identity.");
            }

            return (
                fullPath,
                Convert.ToHexStringLower(SHA256.HashData(existing)));
        }
        if (Directory.Exists(fullPath)
            || GetLinkTarget(fullPath) is not null)
        {
            throw new ArgumentException(
                "Rollback output path is not a regular file target.",
                nameof(requestedPath));
        }

        return await WriteBytesAsync(fullPath, payload, ct);
    }

    private static async Task<(string FullPath, string Sha256)>
        WriteBytesAsync(
            string fullPath,
            byte[] payload,
            CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(
                "Max-score maintenance output has no parent directory.",
                nameof(fullPath));
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(
            parent,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.staging");
        try
        {
            await using (var stream = new FileStream(
                staging,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, fullPath, overwrite: false);
        }
        finally
        {
            try
            {
                if (File.Exists(staging))
                    File.Delete(staging);
            }
            catch
            {
            }
        }

        return (
            fullPath,
            Convert.ToHexStringLower(SHA256.HashData(payload)));
    }

    private static (string DataRoot, string FullPath)
        ResolveUnderDataDirectory(
            string dataDirectory,
            string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException(
                "Configured data directory cannot be blank.",
                nameof(dataDirectory));
        }
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            throw new ArgumentException(
                "Max-score maintenance file path cannot be blank.",
                nameof(requestedPath));
        }

        var dataRoot = Path.GetFullPath(dataDirectory);
        var fullPath = Path.GetFullPath(
            Path.IsPathRooted(requestedPath)
                ? requestedPath
                : Path.Combine(dataRoot, requestedPath));
        if (!PathArtifactResolver.IsWithin(dataRoot, fullPath)
            || string.Equals(
                Path.TrimEndingDirectorySeparator(dataRoot),
                fullPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Max-score maintenance files must remain below the configured data directory.",
                nameof(requestedPath));
        }

        return (dataRoot, fullPath);
    }

    private static void EnsureNoSymbolicLinks(
        string dataRoot,
        string candidate)
    {
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(dataRoot));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        for (var current = Path.GetFullPath(candidate);
             current is not null
             && !string.Equals(current, root, comparison);
             current = Path.GetDirectoryName(current))
        {
            if (GetLinkTarget(current) is not null)
            {
                throw new ArgumentException(
                    $"Max-score maintenance path cannot contain symbolic link '{current}'.",
                    nameof(candidate));
            }
            if (File.Exists(current)
                && !string.Equals(
                    current,
                    Path.GetFullPath(candidate),
                    comparison)
                && (File.GetAttributes(current)
                    & FileAttributes.Directory) == 0)
            {
                throw new ArgumentException(
                    $"Max-score maintenance path component '{current}' is not a directory.",
                    nameof(candidate));
            }
        }
    }

    private static void RequireJsonExtension(
        string fullPath,
        string parameterName)
    {
        if (!Path.GetExtension(fullPath).Equals(
                ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Max-score maintenance files must use the .json extension.",
                parameterName);
        }
    }

    private static string? GetLinkTarget(string path)
    {
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        return info.LinkTarget;
    }
}
