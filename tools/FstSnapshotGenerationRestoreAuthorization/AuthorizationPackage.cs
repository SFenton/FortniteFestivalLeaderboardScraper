using System.Diagnostics;
using System.Reflection;
using System.Text;
using FstSnapshotGenerationDrop;

namespace FstSnapshotGenerationRestoreAuthorization;

public static class AuthorizationPackage
{
    private static readonly string[] PayloadPaths =
    [
        "restore-tool.py",
        "postgres-snapshot-generation-archive.py",
        "source-manifest.json",
        "pinned-to-base.patch",
        "base-to-final.patch",
        "test-evidence/manifest.json",
        "test-evidence/results.json",
    ];

    public static RestoreToolRepairPackageManifest Prepare(
        string dropPlanPath,
        string dropReportPath,
        string originalBundlePath,
        string validatorBaseToolPath,
        string pinnedToBaseDiffPath,
        string baseToFinalDiffPath,
        string sourceManifestPath,
        string testEvidenceManifestPath,
        string testResultsPath,
        string expectedDropPlanDigest,
        string expectedDropOperationId,
        string outputPath)
    {
        var plan =
            DropEvidenceValidator.ReadStrict<
                SnapshotGenerationDropPlan>(
                dropPlanPath);
        plan.Validate();
        if (plan.PlanDigest != expectedDropPlanDigest
            || plan.DropOperationId !=
                expectedDropOperationId)
        {
            throw new InvalidDataException(
                "Drop plan identity differs from expected.");
        }
        var report = ReadDropReport(
            dropReportPath);
        if (report.DropOperationId !=
                plan.DropOperationId
            || report.PlanDigest != plan.PlanDigest
            || report.Status != "dropped"
            || report.Action is not (
                "drop" or "confirm"))
        {
            throw new InvalidDataException(
                "Committed drop report differs from its plan.");
        }
        var bundleManifest =
            DropEvidenceValidator.ValidateRecoveryBundle(
                originalBundlePath);
        var bundleManifestPath = Path.Combine(
            originalBundlePath,
            "bundle-manifest.json");
        if (DropEvidenceValidator.Sha256File(
                bundleManifestPath)
            != plan.RecoveryBundleManifestSha256)
        {
            throw new InvalidDataException(
                "Original bundle manifest differs from the drop plan.");
        }
        var pinnedTool = Path.Combine(
            originalBundlePath,
            "restore-tool.py");
        var archiveHelper = Path.Combine(
            originalBundlePath,
            "postgres-snapshot-generation-archive.py");
        var pinnedToolSha =
            DropEvidenceValidator.Sha256File(
                pinnedTool);
        if (pinnedToolSha != plan.RestoreToolSha256)
        {
            throw new InvalidDataException(
                "Original bundled restore tool differs from the drop plan.");
        }
        if (DropEvidenceValidator.Sha256File(
                validatorBaseToolPath)
            != RestoreToolAuthorizationContract
                .ValidatorBaseToolSha256)
        {
            throw new InvalidDataException(
                "Validator-base tool differs from the reviewed hash.");
        }

        var repository = FindRepositoryRoot();
        if (!string.IsNullOrEmpty(
                RunGit(
                    repository,
                    "status",
                    "--porcelain",
                    "--untracked-files=all")))
        {
            throw new InvalidOperationException(
                "Live repair packages require a clean committed repository.");
        }
        var finalTool = Path.Combine(
            repository,
            "tools",
            "postgres-snapshot-generation-restore.py");
        var finalToolSha =
            DropEvidenceValidator.Sha256File(
                finalTool);
        var helperSha =
            DropEvidenceValidator.Sha256File(
                archiveHelper);
        var authorizerSha =
            DropEvidenceValidator.Sha256File(
                Assembly.GetExecutingAssembly().Location);
        var repositoryCommit = RunGit(
            repository,
            "rev-parse",
            "HEAD");
        var repositoryTreeId = RunGit(
            repository,
            "rev-parse",
            "HEAD^{tree}");
        ValidateHex(
            repositoryCommit,
            40,
            "Repository commit");
        ValidateHex(
            repositoryTreeId,
            40,
            "Repository tree");

        var output = Path.GetFullPath(outputPath);
        if (Directory.Exists(output)
            || File.Exists(output))
        {
            throw new IOException(
                $"Repair package output already exists: {output}");
        }
        Directory.CreateDirectory(output);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                output,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }
        try
        {
            var testEvidenceDirectory =
                Path.Combine(
                    output,
                    "test-evidence");
            Directory.CreateDirectory(
                testEvidenceDirectory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    testEvidenceDirectory,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }
            var sources = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["restore-tool.py"] = finalTool,
                ["postgres-snapshot-generation-archive.py"] =
                    archiveHelper,
                ["source-manifest.json"] =
                    sourceManifestPath,
                ["pinned-to-base.patch"] =
                    pinnedToBaseDiffPath,
                ["base-to-final.patch"] =
                    baseToFinalDiffPath,
                ["test-evidence/manifest.json"] =
                    testEvidenceManifestPath,
                ["test-evidence/results.json"] =
                    testResultsPath,
            };
            foreach (var item in sources)
            {
                ValidateRegularInput(item.Value);
                File.Copy(
                    item.Value,
                    Path.Combine(output, item.Key));
            }
            var files = PayloadPaths
                .Select(path =>
                {
                    var full = Path.Combine(
                        output,
                        path);
                    return new RepairPackageFile(
                        path,
                        DropEvidenceValidator
                            .Sha256File(full),
                        new FileInfo(full).Length);
                })
                .ToArray();
            var manifest =
                new RestoreToolRepairPackageManifest(
                    1,
                    RestoreToolAuthorizationContract
                        .RepairPackageToolId,
                    "accepted",
                    DateTimeOffset.UtcNow,
                    plan.DropOperationId!,
                    plan.PlanDigest!,
                    DropEvidenceValidator.Sha256File(
                        dropPlanPath),
                    DropEvidenceValidator.Sha256File(
                        dropReportPath),
                    plan.RecoveryBundleManifestSha256,
                    pinnedToolSha,
                    RestoreToolAuthorizationContract
                        .ValidatorBaseToolSha256,
                    finalToolSha,
                    helperSha,
                    authorizerSha,
                    repositoryCommit,
                    repositoryTreeId,
                    DropEvidenceValidator.Sha256File(
                        pinnedToBaseDiffPath),
                    DropEvidenceValidator.Sha256File(
                        baseToFinalDiffPath),
                    DropEvidenceValidator.Sha256File(
                        sourceManifestPath),
                    DropEvidenceValidator.Sha256File(
                        testEvidenceManifestPath),
                    files);
            var manifestPath = Path.Combine(
                output,
                "repair-manifest.json");
            DropEvidenceValidator.WriteNewCanonical(
                manifestPath,
                manifest);
            var checksumPaths = PayloadPaths
                .Append("repair-manifest.json")
                .Order(StringComparer.Ordinal)
                .ToArray();
            File.WriteAllText(
                Path.Combine(output, "SHA256SUMS"),
                string.Concat(
                    checksumPaths.Select(path =>
                        $"{DropEvidenceValidator.Sha256File(Path.Combine(output, path))}  {path}\n")),
                new UTF8Encoding(false));
            MakeReadOnly(output);
            _ = Validate(output);
            _ = bundleManifest;
            return manifest;
        }
        catch
        {
            if (Directory.Exists(output))
            {
                MakeWritable(output);
                Directory.Delete(
                    output,
                    recursive: true);
            }
            throw;
        }
    }

    public static RestoreToolRepairPackageManifest
        Validate(string packagePath)
    {
        var root = Path.GetFullPath(packagePath);
        var checksumPath = Path.Combine(
            root,
            "SHA256SUMS");
        var manifestPath = Path.Combine(
            root,
            "repair-manifest.json");
        if (!Directory.Exists(root)
            || !File.Exists(checksumPath)
            || !File.Exists(manifestPath))
        {
            throw new InvalidDataException(
                "Repair package is incomplete.");
        }
        var expected = File.ReadAllLines(
                checksumPath)
            .Select(line => line.Split(
                "  ",
                2,
                StringSplitOptions.None))
            .ToDictionary(
                parts => parts.Length == 2
                    ? parts[1]
                    : throw new InvalidDataException(
                        "Repair checksum line is invalid."),
                parts => parts[0],
                StringComparer.Ordinal);
        var observed = Directory
            .EnumerateFiles(
                root,
                "*",
                SearchOption.AllDirectories)
            .Where(path =>
                Path.GetFullPath(path) !=
                    Path.GetFullPath(checksumPath))
            .ToDictionary(
                path => Path.GetRelativePath(root, path)
                    .Replace(
                        Path.DirectorySeparatorChar,
                        '/'),
                DropEvidenceValidator.Sha256File,
                StringComparer.Ordinal);
        var required = PayloadPaths
            .Append("repair-manifest.json")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!expected.Keys
                .Order(StringComparer.Ordinal)
                .SequenceEqual(required)
            || expected.Count != observed.Count
            || expected.Any(item =>
                !observed.TryGetValue(
                    item.Key,
                    out var digest)
                || digest != item.Value))
        {
            throw new InvalidDataException(
                "Repair package checksum set differs.");
        }
        foreach (var path in Directory
                     .EnumerateFileSystemEntries(
                         root,
                         "*",
                         SearchOption.AllDirectories))
        {
            FileSystemInfo info =
                Directory.Exists(path)
                    ? new DirectoryInfo(path)
                    : new FileInfo(path);
            if (!string.IsNullOrEmpty(
                    info.LinkTarget))
            {
                throw new InvalidDataException(
                    "Repair packages cannot contain symbolic links.");
            }
            if (!OperatingSystem.IsWindows()
                && info is FileInfo
                && (File.GetUnixFileMode(path)
                    & (UnixFileMode.UserWrite
                       | UnixFileMode.GroupWrite
                       | UnixFileMode.OtherWrite)) != 0)
            {
                throw new InvalidDataException(
                    "Repair package files must be read-only.");
            }
        }
        var manifest =
            DropEvidenceValidator.ReadStrict<
                RestoreToolRepairPackageManifest>(
                manifestPath);
        if (manifest.SchemaVersion != 1
            || manifest.ToolId !=
                RestoreToolAuthorizationContract
                    .RepairPackageToolId
            || manifest.Status != "accepted"
            || manifest.ValidatorBaseToolSha256 !=
                RestoreToolAuthorizationContract
                    .ValidatorBaseToolSha256
            || manifest.AuthorizedRestoreToolSha256 !=
                expected["restore-tool.py"]
            || manifest.AuthorizedArchiveHelperSha256 !=
                expected[
                    "postgres-snapshot-generation-archive.py"]
            || manifest.PinnedToBaseDiffSha256 !=
                expected["pinned-to-base.patch"]
            || manifest.BaseToFinalDiffSha256 !=
                expected["base-to-final.patch"]
            || manifest.SourceManifestSha256 !=
                expected["source-manifest.json"]
            || manifest.TestEvidenceManifestSha256 !=
                expected["test-evidence/manifest.json"]
            || manifest.Files.Count !=
                PayloadPaths.Length
            || !manifest.Files
                .Select(file => file.Path)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(
                    PayloadPaths
                        .Order(StringComparer.Ordinal))
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
                "Repair package manifest is invalid.");
        }
        return manifest;
    }

    public static SnapshotGenerationDropExecutionReport
        ReadDropReport(string path)
    {
        var report =
            DropEvidenceValidator.ReadStrict<
                SnapshotGenerationDropExecutionReport>(
                path);
        var expected = report.Seal();
        if (report.ReportSha256 is null
            || report.ReportSha256 !=
                expected.ReportSha256)
        {
            throw new InvalidDataException(
                "Drop report digest is invalid.");
        }
        return report;
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

    public static string AuthorizerSha256() =>
        DropEvidenceValidator.Sha256File(
            Assembly.GetExecutingAssembly().Location);

    private static void ValidateRegularInput(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException(
                "Repair input was not found.",
                full);
        var info = new FileInfo(full);
        if (!string.IsNullOrEmpty(info.LinkTarget))
        {
            throw new InvalidDataException(
                "Repair inputs cannot be symbolic links.");
        }
    }

    private static void ValidateHex(
        string value,
        int length,
        string label)
    {
        if (value.Length != length
            || value.Any(character =>
                character is not (
                    >= '0' and <= '9'
                    or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                $"{label} is invalid.");
        }
    }

    private static string RunGit(
        string repository,
        params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Git did not start.");
        var output = process.StandardOutput
            .ReadToEnd();
        var error = process.StandardError
            .ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git failed: {error}");
        }
        return output.Trim();
    }

    private static void MakeReadOnly(string root)
    {
        if (OperatingSystem.IsWindows())
            return;
        foreach (var file in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(
                file,
                UnixFileMode.UserRead
                | UnixFileMode.GroupRead);
        }
        foreach (var directory in Directory
                     .EnumerateDirectories(
                         root,
                         "*",
                         SearchOption.AllDirectories)
                     .OrderByDescending(
                         path => path.Length))
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute);
        }
        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute);
    }

    private static void MakeWritable(string root)
    {
        if (OperatingSystem.IsWindows()
            || !Directory.Exists(root))
        {
            return;
        }
        foreach (var directory in Directory
                     .EnumerateDirectories(
                         root,
                         "*",
                         SearchOption.AllDirectories)
                     .Prepend(root))
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }
        foreach (var file in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(
                file,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite);
        }
    }
}
