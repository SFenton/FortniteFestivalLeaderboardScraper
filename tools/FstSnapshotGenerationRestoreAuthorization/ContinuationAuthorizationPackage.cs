using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FstSnapshotGenerationDrop;
using FstSnapshotGenerationRestoreContinuation;
using FstSnapshotGenerationQuarantine;

namespace FstSnapshotGenerationRestoreAuthorization;

public static class ContinuationAuthorizationPackage
{
    public static RestoreContinuationPackageManifest
        Prepare(
            string restorePlanPath,
            string restoreReportPath,
            string predecessorRepairPackagePath,
            string recoveryBundlePath,
            string baselineRouteManifestPath,
            string postRestoreRouteManifestPath,
            string candidateRouteManifestPath,
            string predecessorToContinuationDiffPath,
            string sourceManifestPath,
            string testEvidenceManifestPath,
            string testResultsPath,
            string expectedPlanDigest,
            string expectedRestoreOperationId,
            string outputPath)
    {
        var repository =
            AuthorizationPackage.FindRepositoryRoot();
        if (!string.IsNullOrEmpty(
                RunGit(
                    repository,
                    "status",
                    "--porcelain",
                    "--untracked-files=all")))
        {
            throw new InvalidOperationException(
                "Live continuation packages require a clean committed repository.");
        }
        using var plan =
            ContinuationPackage.ReadJson(
                restorePlanPath);
        using var report =
            ContinuationPackage.ReadJson(
                restoreReportPath);
        var planRoot = plan.RootElement;
        var reportRoot = report.RootElement;
        var restoreOperationId = RequireString(
            planRoot,
            "restoreOperationId");
        var dropOperationId = RequireString(
            planRoot,
            "dropOperationId");
        var planDigest = RequireString(
            planRoot,
            "planDigest");
        if (restoreOperationId !=
                expectedRestoreOperationId
            || planDigest != expectedPlanDigest
            || RequireString(
                    reportRoot,
                    "restoreOperationId") !=
                restoreOperationId
            || RequireString(
                    reportRoot,
                    "dropOperationId") !=
                dropOperationId
            || RequireString(
                    reportRoot,
                    "planDigest") !=
                planDigest
            || RequireString(
                    reportRoot,
                    "action") != "restore"
            || RequireString(
                    reportRoot,
                    "status") != "restored"
            || RequireString(
                    reportRoot,
                    "commitOutcome") != "committed")
        {
            throw new InvalidDataException(
                "Committed restore report differs from its plan.");
        }
        var planRepository =
            planRoot.GetProperty("repository");
        var planAuthorization =
            planRoot.GetProperty(
                "restoreToolAuthorization");
        var predecessorToolSha256 =
            RequireString(
                planRepository,
                "toolSha256");
        var predecessorAuthorizationId =
            RequireString(
                planAuthorization,
                "authorizationId");
        var predecessorPackageManifestSha256 =
            RequireString(
                planAuthorization,
                "repairPackageManifestSha256");
        var predecessorPackage =
            AuthorizationPackage.Validate(
                predecessorRepairPackagePath);
        var predecessorManifestPath = Path.Combine(
            predecessorRepairPackagePath,
            "repair-manifest.json");
        if (predecessorPackage.AuthorizedRestoreToolSha256 !=
                predecessorToolSha256
            || ContinuationPackage.Sha256File(
                    predecessorManifestPath) !=
                predecessorPackageManifestSha256)
        {
            throw new InvalidDataException(
                "Predecessor H5 package differs from the restore plan.");
        }
        var recoveryBundleManifestSha256 =
            RequireString(
                planRoot,
                "recoveryBundleManifestSha256");
        _ = DropEvidenceValidator
            .ValidateRecoveryBundle(
                recoveryBundlePath);
        if (ContinuationPackage.Sha256File(
                Path.Combine(
                    recoveryBundlePath,
                    "bundle-manifest.json")) !=
            recoveryBundleManifestSha256)
        {
            throw new InvalidDataException(
                "Recovery bundle differs from the restore plan.");
        }

        ValidateCaptureChecksums(
            baselineRouteManifestPath);
        ValidateCaptureChecksums(
            postRestoreRouteManifestPath);
        ValidateCaptureChecksums(
            candidateRouteManifestPath);
        var postRestore =
            QuarantineEvidenceValidator
                .ValidateDetailedRouteParity(
                    baselineRouteManifestPath,
                    postRestoreRouteManifestPath);
        var repeated =
            QuarantineEvidenceValidator
                .ValidateDetailedRouteParity(
                    baselineRouteManifestPath,
                    candidateRouteManifestPath);
        if (postRestore.Parity.PublicationId !=
                repeated.Parity.PublicationId
            || postRestore.Parity.PublishedScrapeId !=
                repeated.Parity.PublishedScrapeId)
        {
            throw new InvalidDataException(
                "Continuation route captures use different publications.");
        }
        var band = repeated.Routes.Single(
            route => route.Name == "band-export");
        var player = repeated.Routes.Single(
            route => route.Name == "player-export");
        var continuationOutput = Path.GetFullPath(
            outputPath);
        if (Directory.Exists(continuationOutput)
            || File.Exists(continuationOutput))
        {
            throw new IOException(
                $"Continuation package output already exists: {continuationOutput}");
        }
        var runtimeSource = Path.Combine(
            repository,
            "tools",
            "FstSnapshotGenerationRestoreContinuation",
            "bin",
            "Release",
            "net9.0");
        var runtimeSources =
            ContinuationPackage.PayloadPaths
                .Where(path =>
                    path.StartsWith(
                        "runtime/",
                        StringComparison.Ordinal))
                .ToDictionary(
                    path => path,
                    path => Path.Combine(
                        runtimeSource,
                        Path.GetFileName(path)),
                    StringComparer.Ordinal);
        foreach (var source in runtimeSources.Values)
            ValidateRegularInput(source);
        var continuationToolSha256 =
            ContinuationPackage.Sha256File(
                runtimeSources[
                    "runtime/FstSnapshotGenerationRestoreContinuation.dll"]);
        var evidenceAssemblySha256 =
            ContinuationPackage.Sha256File(
                runtimeSources[
                    "runtime/FstSnapshotGenerationEvidence.dll"]);
        var routeParityReferenceSourceSha256 =
            ContinuationPackage.Sha256File(
                Path.Combine(
                    repository,
                    "tools",
                    "FstSnapshotGenerationQuarantine",
                    "EvidenceValidator.cs"));
        var authorizerSha256 =
            ContinuationPackage.Sha256File(
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

        Directory.CreateDirectory(
            continuationOutput);
        try
        {
            Directory.CreateDirectory(
                Path.Combine(
                    continuationOutput,
                    "runtime"));
            Directory.CreateDirectory(
                Path.Combine(
                    continuationOutput,
                    "test-evidence"));
            foreach (var item in runtimeSources)
            {
                File.Copy(
                    item.Value,
                    Path.Combine(
                        continuationOutput,
                        item.Key));
            }
            var sources =
                new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["predecessor-to-continuation.diff"] =
                        predecessorToContinuationDiffPath,
                    ["source-manifest.json"] =
                        sourceManifestPath,
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
                    Path.Combine(
                        continuationOutput,
                        item.Key));
            }
            var preflight =
                new RestoreContinuationPreflightReport(
                    RestoreContinuationContract
                        .SchemaVersion,
                    RestoreContinuationContract.ToolId,
                    "accepted",
                    DateTimeOffset.UtcNow,
                    QuarantineEvidenceValidator
                        .RouteParityAlgorithmId,
                    routeParityReferenceSourceSha256,
                    evidenceAssemblySha256,
                    postRestore,
                    repeated,
                    band.BaselineSemanticSha256,
                    player.BaselineSemanticSha256)
                .Seal();
            var preflightPath = Path.Combine(
                continuationOutput,
                "route-parity-preflight.json");
            ContinuationPackage.WriteNewCanonical(
                preflightPath,
                preflight);
            var files =
                ContinuationPackage.PayloadPaths
                    .Select(path =>
                    {
                        var full = Path.Combine(
                            continuationOutput,
                            path);
                        return new
                            RestoreContinuationPackageFile(
                                path,
                                ContinuationPackage
                                    .Sha256File(full),
                                new FileInfo(full).Length);
                    })
                    .ToArray();
            var manifest =
                new RestoreContinuationPackageManifest(
                    RestoreContinuationContract
                        .SchemaVersion,
                    RestoreContinuationContract
                        .PackageToolId,
                    "accepted",
                    DateTimeOffset.UtcNow,
                    restoreOperationId,
                    dropOperationId,
                    planDigest,
                    Path.GetFullPath(
                        restorePlanPath),
                    ContinuationPackage.Sha256File(
                        restorePlanPath),
                    Path.GetFullPath(
                        restoreReportPath),
                    ContinuationPackage.Sha256File(
                        restoreReportPath),
                    predecessorAuthorizationId,
                    predecessorToolSha256,
                    Path.GetFullPath(
                        predecessorRepairPackagePath),
                    predecessorPackageManifestSha256,
                    Path.GetFullPath(
                        recoveryBundlePath),
                    recoveryBundleManifestSha256,
                    continuationToolSha256,
                    evidenceAssemblySha256,
                    routeParityReferenceSourceSha256,
                    authorizerSha256,
                    repositoryCommit,
                    repositoryTreeId,
                    ContinuationPackage.Sha256File(
                        predecessorToContinuationDiffPath),
                    ContinuationPackage.Sha256File(
                        sourceManifestPath),
                    ContinuationPackage.Sha256File(
                        testEvidenceManifestPath),
                    QuarantineEvidenceValidator
                        .RouteParityAlgorithmId,
                    ContinuationPackage.Sha256File(
                        preflightPath),
                    Path.GetFullPath(
                        baselineRouteManifestPath),
                    ContinuationPackage.Sha256File(
                        baselineRouteManifestPath),
                    ContinuationPackage.Sha256File(
                        Path.Combine(
                            Path.GetDirectoryName(
                                baselineRouteManifestPath)!,
                            "SHA256SUMS")),
                    Path.GetFullPath(
                        candidateRouteManifestPath),
                    ContinuationPackage.Sha256File(
                        candidateRouteManifestPath),
                    ContinuationPackage.Sha256File(
                        Path.Combine(
                            Path.GetDirectoryName(
                                candidateRouteManifestPath)!,
                            "SHA256SUMS")),
                    repeated.Parity.PublicationId,
                    repeated.Parity.PublishedScrapeId,
                    files);
            var manifestPath = Path.Combine(
                continuationOutput,
                "continuation-manifest.json");
            ContinuationPackage.WriteNewCanonical(
                manifestPath,
                manifest);
            var checksumPaths =
                ContinuationPackage.PayloadPaths
                    .Append(
                        "continuation-manifest.json")
                    .Order(StringComparer.Ordinal)
                    .ToArray();
            File.WriteAllText(
                Path.Combine(
                    continuationOutput,
                    "SHA256SUMS"),
                string.Concat(
                    checksumPaths.Select(path =>
                        $"{ContinuationPackage.Sha256File(Path.Combine(continuationOutput, path))}  {path}\n")),
                new UTF8Encoding(false));
            MakeReadOnly(continuationOutput);
            _ = ContinuationPackage.Validate(
                continuationOutput);
            return manifest;
        }
        catch
        {
            if (Directory.Exists(
                    continuationOutput))
            {
                MakeWritable(continuationOutput);
                Directory.Delete(
                    continuationOutput,
                    recursive: true);
            }
            throw;
        }
    }

    public static void MakeReadOnly(string root)
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

    public static void MakeWritable(string root)
    {
        if (OperatingSystem.IsWindows())
            return;
        if (Directory.Exists(root))
        {
            File.SetUnixFileMode(
                root,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }
        foreach (var directory in Directory
                     .EnumerateDirectories(
                         root,
                         "*",
                         SearchOption.AllDirectories))
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

    private static string RequireString(
        JsonElement value,
        string propertyName) =>
        value.GetProperty(propertyName)
            .GetString()
        ?? throw new InvalidDataException(
            $"{propertyName} is null.");

    private static void ValidateRegularInput(
        string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException(
                "Continuation input was not found.",
                full);
        var info = new FileInfo(full);
        if (!string.IsNullOrEmpty(
                info.LinkTarget))
        {
            throw new InvalidDataException(
                "Continuation inputs cannot be symbolic links.");
        }
    }

    private static void ValidateCaptureChecksums(
        string manifestPath)
    {
        var root = Path.GetDirectoryName(
            Path.GetFullPath(manifestPath))
            ?? throw new InvalidDataException(
                "Route manifest has no directory.");
        var checksumPath = Path.Combine(
            root,
            "SHA256SUMS");
        ValidateRegularInput(checksumPath);
        var expected =
            ContinuationPackage.ReadChecksums(
                checksumPath);
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
                    "Route capture evidence cannot contain symbolic links.");
            }
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
                path => Path.GetRelativePath(
                        root,
                        path)
                    .Replace(
                        Path.DirectorySeparatorChar,
                        '/'),
                ContinuationPackage.Sha256File,
                StringComparer.Ordinal);
        if (expected.Count != observed.Count
            || expected.Any(item =>
                !observed.TryGetValue(
                    item.Key,
                    out var digest)
                || digest != item.Value))
        {
            throw new InvalidDataException(
                "Route capture checksum inventory differs.");
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
        var output =
            process.StandardOutput.ReadToEnd();
        var error =
            process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git failed: {error}");
        }
        return output.Trim();
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
}
