using System.Text;
using System.Text.RegularExpressions;

namespace FSTService.Scraping.Replay;

public enum TierZeroPackageError
{
    InvalidPath,
    ReservedPath,
    PathEscapesPackage,
    SymbolicLinkDetected,
    DuplicateArtifactPath,
    PackageAlreadyExists,
    PackageAlreadySealed,
    PackageNotFound,
    PackageLockUnavailable,
    PackageWriteFailed,
    PackageNotResumable,
    ResumeIdentityMismatch,
    ResumeArtifactMismatch,
    InvalidManifest,
    InvalidMetadata,
    SummaryReferenceMismatch,
    ArtifactAlreadyExists,
}

public sealed class TierZeroPackageException : InvalidOperationException
{
    public TierZeroPackageException(
        TierZeroPackageError error,
        string message,
        string? logicalPath = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
        LogicalPath = logicalPath;
    }

    public TierZeroPackageError Error { get; }
    public string? LogicalPath { get; }
}

public static partial class TierZeroPackagePath
{
    private static readonly char[] CrossPlatformInvalidCharacters =
        [':', '*', '?', '"', '<', '>', '|'];

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Any(char.IsControl))
        {
            throw Invalid(path, "Package-relative artifact paths must be non-empty printable strings.");
        }

        var slashed = path
            .Replace('\\', '/')
            .Normalize(NormalizationForm.FormC);
        if (slashed.StartsWith("/", StringComparison.Ordinal) ||
            slashed.StartsWith("//", StringComparison.Ordinal) ||
            WindowsDrivePathRegex().IsMatch(slashed))
        {
            throw Invalid(path, "Package artifact paths cannot be absolute.");
        }

        var segments = new List<string>();
        foreach (var segment in slashed.Split(
                     '/',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                throw Invalid(
                    path,
                    "Package artifact paths cannot traverse parent directories.");
            }
            if (string.IsNullOrWhiteSpace(segment) ||
                segment.IndexOfAny(CrossPlatformInvalidCharacters) >= 0 ||
                segment.EndsWith(".", StringComparison.Ordinal) ||
                segment.EndsWith(" ", StringComparison.Ordinal) ||
                IsWindowsDeviceName(segment))
            {
                throw Invalid(
                    path,
                    $"Package artifact path segment '{segment}' is not cross-platform safe.");
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
            throw Invalid(path, "Package artifact path resolves to an empty path.");

        return string.Join('/', segments);
    }

    internal static bool IsReserved(string normalizedPath)
    {
        var firstSegment = normalizedPath.Split('/', 2)[0];
        return
        string.Equals(
            firstSegment,
            TierZeroEvidenceFormat.ManifestFileName,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            firstSegment,
            TierZeroEvidenceFormat.ChecksumFileName,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            firstSegment,
            TierZeroEvidenceFormat.StateFileName,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            firstSegment,
            TierZeroEvidenceFormat.LockFileName,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static string ResolveUnderRoot(
        string packageRoot,
        string normalizedPath)
    {
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(packageRoot));
        var candidate = Path.GetFullPath(
            Path.Combine(
                root,
                normalizedPath.Replace(
                    '/',
                    Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(
                root + Path.DirectorySeparatorChar,
                comparison))
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.PathEscapesPackage,
                $"Resolved artifact path escapes package root '{root}'.",
                normalizedPath);
        }

        return candidate;
    }

    internal static void EnsureNoSymbolicLinks(
        string packageRoot,
        string candidate,
        bool includeCandidate)
    {
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(packageRoot));
        EnsureNoSymbolicLinkAncestors(root);
        var current = includeCandidate
            ? Path.GetFullPath(candidate)
            : Path.GetDirectoryName(Path.GetFullPath(candidate))
              ?? throw new TierZeroPackageException(
                  TierZeroPackageError.PathEscapesPackage,
                  "Artifact path has no package-root ancestor.");
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        while (!current.Equals(root, comparison))
        {
            if (!current.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    comparison))
            {
                throw new TierZeroPackageException(
                    TierZeroPackageError.PathEscapesPackage,
                    $"Artifact path '{candidate}' escapes package root '{root}'.");
            }

            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new TierZeroPackageException(
                        TierZeroPackageError.SymbolicLinkDetected,
                        $"Package path contains symbolic link '{current}'.");
                }
            }

            current = Path.GetDirectoryName(current)
                ?? throw new TierZeroPackageException(
                    TierZeroPackageError.PathEscapesPackage,
                    "Artifact path has no package-root ancestor.");
        }

        if (Directory.Exists(root) &&
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.SymbolicLinkDetected,
                $"Package root '{root}' cannot be a symbolic link.");
        }
    }

    internal static void EnsureNoSymbolicLinkAncestors(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath)
            ?? throw new TierZeroPackageException(
                TierZeroPackageError.PathEscapesPackage,
                $"Path has no filesystem root: {path}");
        var current = pathRoot;
        foreach (var segment in fullPath[pathRoot.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;
            if ((File.GetAttributes(current) &
                 FileAttributes.ReparsePoint) != 0)
            {
                throw new TierZeroPackageException(
                    TierZeroPackageError.SymbolicLinkDetected,
                    $"Package path contains symbolic-link ancestor '{current}'.");
            }
        }
    }

    internal static string NormalizePhysicalRelativePath(string path)
    {
        if (!OperatingSystem.IsWindows() &&
            path.Contains('\\'))
        {
            throw Invalid(
                path,
                "Physical package filenames cannot contain backslash aliases.");
        }

        var slashed = path.Replace(
            Path.DirectorySeparatorChar,
            '/');
        if (Path.AltDirectorySeparatorChar !=
            Path.DirectorySeparatorChar)
        {
            slashed = slashed.Replace(
                Path.AltDirectorySeparatorChar,
                '/');
        }
        var normalizedSlashed =
            slashed.Normalize(NormalizationForm.FormC);
        var normalized = Normalize(normalizedSlashed);
        var physicalCanonical = OperatingSystem.IsMacOS()
            ? normalizedSlashed
            : slashed;
        if (!string.Equals(
                normalized,
                physicalCanonical,
                StringComparison.Ordinal))
        {
            throw Invalid(
                path,
                "Physical package path is not in canonical relative form.");
        }

        return normalized;
    }

    internal static void ValidatePortableNamespace(
        IEnumerable<string> normalizedPaths)
    {
        var root = new PortablePathNode("");
        foreach (var path in normalizedPaths)
        {
            var current = root;
            foreach (var segment in path.Split('/'))
            {
                if (current.IsFile)
                {
                    throw NamespaceCollision(path);
                }

                if (current.Children.TryGetValue(
                        segment,
                        out var existing))
                {
                    if (!string.Equals(
                            existing.CanonicalSegment,
                            segment,
                            StringComparison.Ordinal))
                    {
                        throw NamespaceCollision(path);
                    }
                    current = existing;
                }
                else
                {
                    var child = new PortablePathNode(segment);
                    current.Children.Add(segment, child);
                    current = child;
                }
            }

            if (current.IsFile || current.Children.Count > 0)
                throw NamespaceCollision(path);
            current.IsFile = true;
        }
    }

    internal static bool IsTemporaryPath(string normalizedPath) =>
        PartialFileRegex().IsMatch(normalizedPath);

    private static TierZeroPackageException Invalid(
        string path,
        string message) =>
        new(
            TierZeroPackageError.InvalidPath,
            message,
            path);

    private static bool IsWindowsDeviceName(string segment)
    {
        var stem = segment.Split('.', 2)[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               (stem[3] is >= '1' and <= '9' or
                   '\u00b9' or '\u00b2' or '\u00b3');
    }

    private static TierZeroPackageException NamespaceCollision(string path) =>
        new(
            TierZeroPackageError.DuplicateArtifactPath,
            $"Package artifact path collides in the portable namespace: {path}",
            path);

    private sealed class PortablePathNode(string canonicalSegment)
    {
        internal string CanonicalSegment { get; } = canonicalSegment;
        internal Dictionary<string, PortablePathNode> Children { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        internal bool IsFile { get; set; }
    }

    [GeneratedRegex("^[A-Za-z]:/", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsDrivePathRegex();

    [GeneratedRegex(
        @"\.partial-[0-9]+-[0-9a-f]{32}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PartialFileRegex();
}
