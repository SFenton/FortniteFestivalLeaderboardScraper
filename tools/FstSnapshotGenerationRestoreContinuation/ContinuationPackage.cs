using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FstSnapshotGenerationEvidence;
using FstSnapshotGenerationQuarantine;

namespace FstSnapshotGenerationRestoreContinuation;

public static class ContinuationPackage
{
    public static readonly string[] PayloadPaths =
    [
        "runtime/FstSnapshotGenerationRestoreContinuation.dll",
        "runtime/FstSnapshotGenerationRestoreContinuation.deps.json",
        "runtime/FstSnapshotGenerationRestoreContinuation.runtimeconfig.json",
        "runtime/FstSnapshotGenerationEvidence.dll",
        "runtime/Microsoft.Extensions.DependencyInjection.Abstractions.dll",
        "runtime/Microsoft.Extensions.Logging.Abstractions.dll",
        "runtime/Npgsql.dll",
        "predecessor-to-continuation.diff",
        "source-manifest.json",
        "test-evidence/manifest.json",
        "test-evidence/results.json",
        "route-parity-preflight.json",
    ];

    private static readonly JsonSerializerOptions Strict =
        CreateStrictOptions();

    public static RestoreContinuationPackageManifest
        Validate(string packagePath)
    {
        var root = Path.GetFullPath(packagePath);
        var checksumPath = Path.Combine(
            root,
            "SHA256SUMS");
        var manifestPath = Path.Combine(
            root,
            "continuation-manifest.json");
        if (!Directory.Exists(root)
            || !File.Exists(checksumPath)
            || !File.Exists(manifestPath))
        {
            throw new InvalidDataException(
                "Continuation package is incomplete.");
        }
        RequireNoSymlink(root, root);
        var expected = ReadChecksums(checksumPath);
        var required = PayloadPaths
            .Append("continuation-manifest.json")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!expected.Keys
                .Order(StringComparer.Ordinal)
                .SequenceEqual(required))
        {
            throw new InvalidDataException(
                "Continuation package checksum inventory differs.");
        }
        var observed = Directory
            .EnumerateFiles(
                root,
                "*",
                SearchOption.AllDirectories)
            .Where(path =>
                !string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(checksumPath),
                    StringComparison.Ordinal))
            .ToDictionary(
                path => Path.GetRelativePath(root, path)
                    .Replace(
                        Path.DirectorySeparatorChar,
                        '/'),
                Sha256File,
                StringComparer.Ordinal);
        if (expected.Count != observed.Count
            || expected.Any(item =>
                !observed.TryGetValue(
                    item.Key,
                    out var digest)
                || digest != item.Value))
        {
            throw new InvalidDataException(
                "Continuation package file set differs.");
        }
        foreach (var path in Directory
                     .EnumerateFileSystemEntries(
                         root,
                         "*",
                         SearchOption.AllDirectories))
        {
            RequireNoSymlink(path, root);
            if (!OperatingSystem.IsWindows()
                && File.Exists(path)
                && (File.GetUnixFileMode(path)
                    & (UnixFileMode.UserWrite
                       | UnixFileMode.GroupWrite
                       | UnixFileMode.OtherWrite)) != 0)
            {
                throw new InvalidDataException(
                    "Continuation package files must be read-only.");
            }
        }
        var manifest =
            ReadStrict<RestoreContinuationPackageManifest>(
                manifestPath);
        if (manifest.SchemaVersion !=
                RestoreContinuationContract.SchemaVersion
            || manifest.ToolId !=
                RestoreContinuationContract.PackageToolId
            || manifest.Status != "accepted"
            || manifest.RouteParityAlgorithmId !=
                QuarantineEvidenceValidator
                    .RouteParityAlgorithmId
            || manifest.AuthorizedContinuationToolSha256 !=
                expected[
                    "runtime/FstSnapshotGenerationRestoreContinuation.dll"]
            || manifest.AuthorizedEvidenceAssemblySha256 !=
                expected[
                    "runtime/FstSnapshotGenerationEvidence.dll"]
            || manifest.PredecessorToContinuationDiffSha256 !=
                expected[
                    "predecessor-to-continuation.diff"]
            || manifest.SourceManifestSha256 !=
                expected["source-manifest.json"]
            || manifest.TestEvidenceManifestSha256 !=
                expected["test-evidence/manifest.json"]
            || manifest.RouteParityPreflightSha256 !=
                expected["route-parity-preflight.json"]
            || manifest.Files.Count != PayloadPaths.Length
            || !manifest.Files
                .Select(file => file.Path)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(
                    PayloadPaths.Order(StringComparer.Ordinal))
            || manifest.Files.Any(file =>
                !expected.TryGetValue(
                    file.Path,
                    out var digest)
                || digest != file.Sha256
                || new FileInfo(
                    Path.Combine(root, file.Path))
                    .Length != file.Bytes))
        {
            throw new InvalidDataException(
                "Continuation package manifest is invalid.");
        }
        return manifest;
    }

    public static T ReadStrict<T>(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(
                   stream,
                   Strict)
               ?? throw new InvalidDataException(
                   $"JSON file is empty: {path}");
    }

    public static JsonDocument ReadJson(string path) =>
        JsonDocument.Parse(
            File.ReadAllBytes(path),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling =
                    JsonCommentHandling.Disallow,
                MaxDepth = 128,
            });

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(
                SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    public static string CurrentToolSha256() =>
        Sha256File(
            Assembly.GetExecutingAssembly().Location);

    public static string CurrentEvidenceAssemblySha256() =>
        Sha256File(
            typeof(QuarantineEvidenceValidator)
                .Assembly.Location);

    public static void WriteNewCanonical<T>(
        string path,
        T value)
    {
        var bytes =
            SnapshotGenerationCanonicalJson.Serialize(
                value);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        stream.Write(bytes);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    public static IReadOnlyDictionary<string, string>
        ReadChecksums(string path)
    {
        var result = new Dictionary<string, string>(
            StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split(
                "  ",
                2,
                StringSplitOptions.None);
            if (parts.Length != 2
                || parts[0].Length != 64
                || parts[0].Any(character =>
                    character is not (
                        >= '0' and <= '9'
                        or >= 'a' and <= 'f'))
                || string.IsNullOrWhiteSpace(
                    parts[1])
                || Path.IsPathRooted(parts[1])
                || parts[1].Split(
                        '/',
                        StringSplitOptions.None)
                    .Any(segment =>
                        segment is "" or "." or "..")
                || !result.TryAdd(
                    parts[1],
                    parts[0]))
            {
                throw new InvalidDataException(
                    "Continuation checksum line is invalid.");
            }
        }
        return result;
    }

    public static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(
            AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        current.FullName,
                        "FortniteFestivalLeaderboardScraper.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException(
            "Repository root was not found.");
    }

    private static JsonSerializerOptions
        CreateStrictOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition =
                JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling =
                JsonUnmappedMemberHandling.Disallow,
        };

    private static void RequireNoSymlink(
        string path,
        string root)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(
                Path.GetFullPath(root)
                    .TrimEnd(
                        Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            && !string.Equals(
                full,
                Path.GetFullPath(root),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Continuation package path escaped its root.");
        }
        FileSystemInfo info = Directory.Exists(full)
            ? new DirectoryInfo(full)
            : new FileInfo(full);
        if (!string.IsNullOrEmpty(info.LinkTarget))
        {
            throw new InvalidDataException(
                "Continuation packages cannot contain symbolic links.");
        }
    }
}

public sealed class ContinuationEvidencePaths
{
    private readonly string _root;

    public ContinuationEvidencePaths(string root)
    {
        _root = Path.GetFullPath(root);
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException(
                _root);
        }
        var required = Path.GetFullPath(
            "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence");
        if (!_root.StartsWith(
                required
                + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            && !string.Equals(
                _root,
                required,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Continuation evidence root must remain on the FST evidence drive.");
        }
    }

    public string ResolveInputFile(string path)
    {
        var full = Resolve(path);
        if (!File.Exists(full))
            throw new FileNotFoundException(
                "Continuation input file was not found.",
                full);
        RequireNoLinks(full);
        return full;
    }

    public string ResolveInputDirectory(string path)
    {
        var full = Resolve(path);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException(full);
        RequireNoLinks(full);
        return full;
    }

    public string ResolveNewFile(string path)
    {
        var full = Resolve(path);
        if (File.Exists(full)
            || Directory.Exists(full))
        {
            throw new IOException(
                $"Continuation output already exists: {full}");
        }
        var parent = Path.GetDirectoryName(full)
            ?? throw new InvalidDataException(
                "Continuation output has no parent.");
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException(parent);
        RequireNoLinks(parent);
        return full;
    }

    private string Resolve(string path)
    {
        var full = Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(_root, path));
        if (!full.StartsWith(
                _root.TrimEnd(
                    Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            && !string.Equals(
                full,
                _root,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Continuation path escaped its evidence root.");
        }
        return full;
    }

    private void RequireNoLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (current.StartsWith(
                   _root,
                   StringComparison.Ordinal))
        {
            FileSystemInfo info =
                Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);
            if (!string.IsNullOrEmpty(
                    info.LinkTarget))
            {
                throw new InvalidDataException(
                    "Continuation paths cannot contain symbolic links.");
            }
            if (string.Equals(
                    current,
                    _root,
                    StringComparison.Ordinal))
            {
                break;
            }
            current = Path.GetDirectoryName(current)
                ?? "";
        }
    }
}
