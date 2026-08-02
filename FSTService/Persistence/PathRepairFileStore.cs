using System.Security.Cryptography;
using System.Text.Json;
using FSTService.Scraping;

namespace FSTService.Persistence;

internal static class PathRepairFileStore
{
    internal static string ResolveNewJsonOutputPath(
        string dataDirectory,
        string requestedPath)
    {
        var (dataRoot, fullPath) = ResolveUnderDataDirectory(
            dataDirectory,
            requestedPath);
        if (!Path.GetExtension(fullPath).Equals(
                ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Path-repair output files must use the .json extension.",
                nameof(requestedPath));
        }

        EnsureNoSymbolicLinks(dataRoot, fullPath, includeCandidate: true);
        if (File.Exists(fullPath) ||
            Directory.Exists(fullPath) ||
            GetLinkTarget(fullPath) is not null)
        {
            throw new ArgumentException(
                "Path-repair output path must not already exist.",
                nameof(requestedPath));
        }

        return fullPath;
    }

    internal static string ResolveExistingJsonInputPath(
        string dataDirectory,
        string requestedPath)
    {
        var (dataRoot, fullPath) = ResolveUnderDataDirectory(
            dataDirectory,
            requestedPath);
        if (!Path.GetExtension(fullPath).Equals(
                ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Path-repair input files must use the .json extension.",
                nameof(requestedPath));
        }

        EnsureNoSymbolicLinks(dataRoot, fullPath, includeCandidate: true);
        var file = new FileInfo(fullPath);
        if (!file.Exists ||
            (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            file.LinkTarget is not null)
        {
            throw new ArgumentException(
                "Path-repair input path must identify a regular non-symbolic-link file.",
                nameof(requestedPath));
        }

        return fullPath;
    }

    internal static async Task<(string FullPath, string Sha256)> WriteNewJsonAsync<T>(
        string dataDirectory,
        string requestedPath,
        T value,
        JsonSerializerOptions options,
        CancellationToken ct)
    {
        var fullPath = ResolveNewJsonOutputPath(
            dataDirectory,
            requestedPath);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(
                "Path-repair output path has no parent directory.",
                nameof(requestedPath));
        Directory.CreateDirectory(parent);

        var dataRoot = Path.GetFullPath(dataDirectory);
        EnsureNoSymbolicLinks(dataRoot, parent, includeCandidate: true);
        fullPath = ResolveNewJsonOutputPath(dataRoot, fullPath);

        var payload = JsonSerializer.SerializeToUtf8Bytes(value, options);
        var digest = Convert.ToHexStringLower(SHA256.HashData(payload));
        var temporaryPath = Path.Combine(
            parent,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.staging");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: false);
            return (fullPath, digest);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
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
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    private static (string DataRoot, string FullPath) ResolveUnderDataDirectory(
        string dataDirectory,
        string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Configured data directory cannot be blank.", nameof(dataDirectory));
        if (string.IsNullOrWhiteSpace(requestedPath))
            throw new ArgumentException("Path-repair file path cannot be blank.", nameof(requestedPath));

        var dataRoot = Path.GetFullPath(dataDirectory);
        var fullPath = Path.GetFullPath(
            Path.IsPathRooted(requestedPath)
                ? requestedPath
                : Path.Combine(dataRoot, requestedPath));
        if (!PathArtifactResolver.IsWithin(dataRoot, fullPath) ||
            fullPath.Equals(
                Path.TrimEndingDirectorySeparator(dataRoot),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Path-repair file path must remain below the configured data directory.",
                nameof(requestedPath));
        }

        return (dataRoot, fullPath);
    }

    private static void EnsureNoSymbolicLinks(
        string dataRoot,
        string candidate,
        bool includeCandidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(dataRoot));
        var current = includeCandidate
            ? Path.GetFullPath(candidate)
            : Path.GetDirectoryName(Path.GetFullPath(candidate));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        while (current is not null &&
               !current.Equals(normalizedRoot, comparison))
        {
            if (GetLinkTarget(current) is not null)
            {
                throw new ArgumentException(
                    $"Path-repair paths cannot contain symbolic link '{current}'.",
                    nameof(candidate));
            }

            if (File.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.Directory) == 0 &&
                !current.Equals(Path.GetFullPath(candidate), comparison))
            {
                throw new ArgumentException(
                    $"Path-repair path component '{current}' is not a directory.",
                    nameof(candidate));
            }

            current = Path.GetDirectoryName(current);
        }

        if (current is null)
        {
            throw new ArgumentException(
                "Path-repair path has no configured data-directory ancestor.",
                nameof(candidate));
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
