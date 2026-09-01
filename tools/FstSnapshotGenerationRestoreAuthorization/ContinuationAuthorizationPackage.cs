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
            string serviceRuntimeIsolationEvidencePath,
            string historicalServiceBuildEvidencePath,
            string stabilizedServiceBuildEvidencePath,
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
        var historicalBridge =
            QuarantineEvidenceValidator
                .ValidateShopDailyInventoryRolloverBridge(
                    baselineRouteManifestPath,
                    postRestoreRouteManifestPath);
        var stabilized =
            QuarantineEvidenceValidator
                .ValidateDetailedRouteParity(
                    postRestoreRouteManifestPath,
                    candidateRouteManifestPath);
        QuarantineEvidenceValidator
            .ValidateStabilizedShopRefresh(
                postRestoreRouteManifestPath,
                candidateRouteManifestPath,
                historicalBridge
                    .StabilizedShopLastUpdatedUtc);
        RestoreContinuationContract
            .ValidateSharedMiddleCapture(
                historicalBridge,
                stabilized);
        var restoreScope =
            BuildRestoreScopeIsolationEvidence(
                planRoot,
                reportRoot,
                restorePlanPath,
                restoreReportPath);
        var serviceRuntimeIsolationEvidenceBytes =
            ReadRegularInputBytes(
                serviceRuntimeIsolationEvidencePath);
        var historicalServiceBuildEvidenceBytes =
            ReadRegularInputBytes(
                historicalServiceBuildEvidencePath);
        var stabilizedServiceBuildEvidenceBytes =
            ReadRegularInputBytes(
                stabilizedServiceBuildEvidencePath);
        var serviceRuntimeIsolation =
            ValidateServiceRuntimeIsolationEvidence(
                repository,
                serviceRuntimeIsolationEvidenceBytes,
                historicalServiceBuildEvidenceBytes,
                stabilizedServiceBuildEvidenceBytes,
                historicalBridge,
                stabilized,
                restoreScope);
        var band = stabilized.Routes.Single(
            route => route.Name == "band-export");
        var player = stabilized.Routes.Single(
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
            ContinuationPackage.WriteNewBytes(
                Path.Combine(
                    continuationOutput,
                    "historical-service-build-evidence.json"),
                historicalServiceBuildEvidenceBytes);
            ContinuationPackage.WriteNewBytes(
                Path.Combine(
                    continuationOutput,
                    "stabilized-service-build-evidence.json"),
                stabilizedServiceBuildEvidenceBytes);
            ContinuationPackage.WriteNewCanonical(
                Path.Combine(
                    continuationOutput,
                    "service-runtime-isolation.json"),
                serviceRuntimeIsolation);
            var serviceRuntimeIsolationFileSha256 =
                ContinuationPackage.Sha256File(
                    Path.Combine(
                        continuationOutput,
                        "service-runtime-isolation.json"));
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
                    historicalBridge,
                    stabilized,
                    restoreScope,
                    serviceRuntimeIsolationFileSha256,
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
                    stabilized
                        .RouteSemanticEvidenceSha256,
                    RestoreContinuationContract
                        .TemporalBridgePredicateId,
                    QuarantineJson.Sha256(
                        historicalBridge),
                    restoreScope.EvidenceSha256!,
                    Path.Combine(
                        continuationOutput,
                        "service-runtime-isolation.json"),
                    serviceRuntimeIsolationFileSha256,
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
                        postRestoreRouteManifestPath),
                    ContinuationPackage.Sha256File(
                        postRestoreRouteManifestPath),
                    ContinuationPackage.Sha256File(
                        Path.Combine(
                            Path.GetDirectoryName(
                                postRestoreRouteManifestPath)!,
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
                    stabilized.Parity.PublicationId,
                    stabilized.Parity.PublishedScrapeId,
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

    private static RestoreScopeIsolationEvidence
        BuildRestoreScopeIsolationEvidence(
            JsonElement plan,
            JsonElement report,
            string planPath,
            string reportPath)
    {
        var target = plan.GetProperty("target");
        var childSchema = RequireString(
            target,
            "childSchema");
        var childRelation = RequireString(
            target,
            "childRelation");
        var selected = plan
            .GetProperty("selectedTocEntries")
            .EnumerateArray()
            .Select(item =>
                item.GetString()
                ?? throw new InvalidDataException(
                    "Selected TOC entry is null."))
            .ToArray();
        var executed = plan
            .GetProperty("executedTocEntries")
            .EnumerateArray()
            .Select(item =>
                item.GetString()
                ?? throw new InvalidDataException(
                    "Executed TOC entry is null."))
            .ToArray();
        var tableToken =
            $" TABLE {childSchema} {childRelation} ";
        var tableDataToken =
            $" TABLE DATA {childSchema} {childRelation} ";
        var exactTarget =
            executed.Length == 2
            && executed.Count(entry =>
                entry.Contains(
                    tableToken,
                    StringComparison.Ordinal)) == 1
            && executed.Count(entry =>
                entry.Contains(
                    tableDataToken,
                    StringComparison.Ordinal)) == 1
            && executed.All(entry =>
                entry.Contains(
                    childRelation,
                    StringComparison.Ordinal))
            && executed.All(entry =>
                !entry.Contains(
                    "item_shop",
                    StringComparison.OrdinalIgnoreCase)
                && !entry.Contains(
                    "song_metadata",
                    StringComparison.OrdinalIgnoreCase));
        var repositoryIndexes =
            selected.Length == 4
            && executed.Length == 2
            && selected.All(entry =>
                entry.Contains(
                    childRelation,
                    StringComparison.Ordinal))
            && selected.Except(
                    executed,
                    StringComparer.Ordinal)
                .Count() == 2;
        var rowFingerprint =
            report.GetProperty("rowFingerprint");
        var rowCount =
            target.GetProperty("rowCount")
                .GetInt64();
        var rowFingerprintSha256 =
            RequireString(
                target,
                "rowFingerprintSha256");
        if (!exactTarget
            || !repositoryIndexes
            || RequireString(
                    report,
                    "status") != "restored"
            || RequireString(
                    report,
                    "commitOutcome") != "committed"
            || rowFingerprint
                    .GetProperty("rowCount")
                    .GetInt64() != rowCount
            || RequireString(
                    rowFingerprint,
                    "sha256") !=
                rowFingerprintSha256)
        {
            throw new InvalidDataException(
                "Restore evidence does not prove the exact isolated child write scope.");
        }
        return new RestoreScopeIsolationEvidence(
            RequireString(
                plan,
                "restoreOperationId"),
            RequireString(
                plan,
                "dropOperationId"),
            RequireString(
                plan,
                "planDigest"),
            ContinuationPackage.Sha256File(
                planPath),
            ContinuationPackage.Sha256File(
                reportPath),
            childSchema,
            childRelation,
            RequireString(
                target,
                "instrument"),
            target.GetProperty("snapshotId")
                .GetInt64(),
            rowCount,
            rowFingerprintSha256,
            selected.Length,
            executed.Length,
            RestoreContinuationContract.Sha256(
                executed),
            exactTarget,
            repositoryIndexes,
            true).Seal();
    }

    private static ServiceRuntimeIsolationEvidence
        ValidateServiceRuntimeIsolationEvidence(
            string repository,
            ReadOnlySpan<byte> evidenceBytes,
            ReadOnlySpan<byte>
                historicalBuildEvidenceBytes,
            ReadOnlySpan<byte>
                stabilizedBuildEvidenceBytes,
            ShopDailyInventoryRolloverEvidence
                historicalBridge,
            DetailedRouteParityEvidence stabilized,
            RestoreScopeIsolationEvidence
                restoreScope)
    {
        var evidence =
            ContinuationPackage.ReadStrict<
                ServiceRuntimeIsolationEvidence>(
                evidenceBytes);
        var historicalBuild =
            ContinuationPackage.ReadStrict<
                ServiceImageBuildEvidence>(
                historicalBuildEvidenceBytes);
        var stabilizedBuild =
            ContinuationPackage.ReadStrict<
                ServiceImageBuildEvidence>(
                stabilizedBuildEvidenceBytes);
        var expectedPaths = new[]
        {
            "FSTService/Api/ShopCacheService.cs",
            "FSTService/Api/SongEndpoints.cs",
            "FSTService/Persistence/MetaDatabase.cs",
            "FSTService/Scraping/ItemShopService.cs",
            "FSTService/Scraping/ShopUrlHelper.cs",
        };
        var sources = evidence.ShopSourceFiles
            .ToDictionary(
                item => item.Path,
                item => item.Sha256,
                StringComparer.Ordinal);
        if (evidence.SchemaVersion != 1
            || evidence.ToolId !=
                RestoreContinuationContract
                    .ServiceRuntimeIsolationToolId
            || evidence.Status != "accepted"
            || evidence.CompletedAtUtc.Offset !=
                TimeSpan.Zero
            || evidence
                    .HistoricalServiceIdentityConfirmedAtUtc
                    .Offset != TimeSpan.Zero
            || evidence
                    .StabilizedServiceIdentityConfirmedAtUtc
                    .Offset != TimeSpan.Zero
            || (evidence.EvidenceSha256 is not null
                && evidence.Seal().EvidenceSha256 !=
                    evidence.EvidenceSha256)
            || evidence
                    .HistoricalBaselineRouteManifestSha256 !=
                historicalBridge
                    .HistoricalBaselineManifestSha256
            || evidence
                    .StabilizedBaselineRouteManifestSha256 !=
                stabilized.Parity
                    .BaselineManifestSha256
            || evidence.HistoricalBuildEvidenceSha256 !=
                ContinuationPackage.Sha256Bytes(
                    historicalBuildEvidenceBytes)
            || evidence.StabilizedBuildEvidenceSha256 !=
                ContinuationPackage.Sha256Bytes(
                    stabilizedBuildEvidenceBytes)
            || evidence.RestoreWritesItemShopState !=
                !restoreScope
                    .ItemShopStateOutsideRestoreScope
            || evidence.ShopReadsLeaderboardSnapshotState
            || sources.Count != expectedPaths.Length
            || !sources.Keys
                .Order(StringComparer.Ordinal)
                .SequenceEqual(
                    expectedPaths.Order(
                        StringComparer.Ordinal)))
        {
            throw new InvalidDataException(
                "Service runtime isolation evidence is invalid.");
        }
        ValidateHex(
            evidence
                .HistoricalShopSourceRepositoryCommit,
            40,
            "Historical shop source repository commit");
        ValidateHex(
            evidence.HistoricalServiceImageSha256,
            64,
            "Historical service image");
        ValidateHex(
            evidence.HistoricalServiceDllSha256,
            64,
            "Historical service DLL");
        ValidateHex(
            evidence.HistoricalBuildEvidenceSha256,
            64,
            "Historical build evidence");
        ValidateHex(
            evidence.StabilizedServiceImageSha256,
            64,
            "Stabilized service image");
        ValidateHex(
            evidence.StabilizedServiceDllSha256,
            64,
            "Stabilized service DLL");
        ValidateHex(
            evidence.StabilizedRepositoryCommit,
            40,
            "Stabilized repository commit");
        ValidateHex(
            evidence.StabilizedBuildEvidenceSha256,
            64,
            "Stabilized build evidence");
        ValidateServiceImageBuildEvidence(
            historicalBuild,
            "historical-baseline",
            evidence.HistoricalServiceImageSha256,
            evidence.HistoricalServiceDllSha256,
            evidence
                .HistoricalShopSourceRepositoryCommit,
            expectedRepositoryCommit: null,
            expectedWorktreeClean: false);
        ValidateServiceImageBuildEvidence(
            stabilizedBuild,
            "stabilized",
            evidence.StabilizedServiceImageSha256,
            evidence.StabilizedServiceDllSha256,
            evidence
                .HistoricalShopSourceRepositoryCommit,
            evidence.StabilizedRepositoryCommit,
            expectedWorktreeClean: true);
        if (historicalBuild.BuiltAtUtc >=
                historicalBridge
                    .HistoricalBaselineCapturedAtUtc
            || evidence
                    .HistoricalServiceIdentityConfirmedAtUtc >=
                historicalBridge
                    .HistoricalBaselineCapturedAtUtc
            || evidence
                    .HistoricalServiceIdentityConfirmedAtUtc <
                historicalBuild.BuiltAtUtc
            || stabilizedBuild.BuiltAtUtc >=
                historicalBridge
                    .HistoricalCandidateCapturedAtUtc
            || evidence
                    .StabilizedServiceIdentityConfirmedAtUtc >=
                historicalBridge
                    .HistoricalCandidateCapturedAtUtc
            || evidence
                    .StabilizedServiceIdentityConfirmedAtUtc <
                stabilizedBuild.BuiltAtUtc)
        {
            throw new InvalidDataException(
                "Service runtime timing does not bracket the authenticated captures.");
        }
        foreach (var path in expectedPaths)
        {
            var expected = sources[path];
            if (expected !=
                    ContinuationPackage.Sha256File(
                        Path.Combine(
                            repository,
                            path))
                || expected !=
                    Sha256GitFile(
                        repository,
                        evidence
                            .HistoricalShopSourceRepositoryCommit,
                        path)
                || expected !=
                    Sha256GitFile(
                        repository,
                        evidence
                            .StabilizedRepositoryCommit,
                        path))
            {
                throw new InvalidDataException(
                    "Shop source evidence differs from the reviewed repository.");
            }
        }
        ValidateShopSourceIsolation(repository);
        return evidence.Seal();
    }

    public static void ValidateServiceImageBuildEvidence(
        ServiceImageBuildEvidence evidence,
        string expectedRole,
        string expectedImageSha256,
        string expectedServiceDllSha256,
        string expectedRepositoryBaseCommit,
        string? expectedRepositoryCommit,
        bool expectedWorktreeClean)
    {
        if (evidence.SchemaVersion != 1
            || evidence.ToolId !=
                RestoreContinuationContract
                    .ServiceImageBuildEvidenceToolId
            || evidence.Status != "accepted"
            || evidence.Role != expectedRole
            || evidence.ImageSha256 !=
                expectedImageSha256
            || evidence.ServiceDllSha256 !=
                expectedServiceDllSha256
            || evidence.RepositoryBaseCommit !=
                expectedRepositoryBaseCommit
            || evidence.RepositoryCommit !=
                expectedRepositoryCommit
            || evidence.WorktreeClean !=
                expectedWorktreeClean
            || evidence.BuiltAtUtc.Offset !=
                TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "Service image build evidence differs from runtime identity.");
        }
        ValidateHex(
            evidence.ImageSha256,
            64,
            "Service build image");
        ValidateHex(
            evidence.ServiceDllSha256,
            64,
            "Service build DLL");
        ValidateHex(
            evidence.RepositoryBaseCommit,
            40,
            "Service build base commit");
        if (evidence.RepositoryCommit is not null)
        {
            ValidateHex(
                evidence.RepositoryCommit,
                40,
                "Service build commit");
        }
        ValidateHex(
            evidence.BuildRequestSha256,
            64,
            "Service build request evidence");
        ValidateHex(
            evidence.BuildResultSha256,
            64,
            "Service build result evidence");
    }

    private static string Sha256GitFile(
        string repository,
        string commit,
        string path)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("show");
        start.ArgumentList.Add($"{commit}:{path}");
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Git did not start.");
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        var error =
            process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git failed: {error}");
        }
        return Convert.ToHexString(
                System.Security.Cryptography.SHA256
                    .HashData(output.ToArray()))
            .ToLowerInvariant();
    }

    private static void ValidateShopSourceIsolation(
        string repository)
    {
        var cache = File.ReadAllText(
            Path.Combine(
                repository,
                "FSTService/Api/ShopCacheService.cs"));
        var service = File.ReadAllText(
            Path.Combine(
                repository,
                "FSTService/Scraping/ItemShopService.cs"));
        var helper = File.ReadAllText(
            Path.Combine(
                repository,
                "FSTService/Scraping/ShopUrlHelper.cs"));
        foreach (var source in new[]
                 {
                     cache,
                     service,
                     helper,
                 })
        {
            if (source.Contains(
                    "leaderboard_entries_snapshot",
                    StringComparison.OrdinalIgnoreCase)
                || source.Contains(
                    "SnapshotGeneration",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Shop runtime source depends on snapshot-generation state.");
            }
        }

        var endpoints = File.ReadAllText(
            Path.Combine(
                repository,
                "FSTService/Api/SongEndpoints.cs"));
        var endpointStart = endpoints.IndexOf(
            "app.MapGet(\"/api/shop\"",
            StringComparison.Ordinal);
        var endpointEnd = endpoints.IndexOf(
            "});",
            endpointStart,
            StringComparison.Ordinal);
        if (endpointStart < 0
            || endpointEnd < endpointStart
            || endpoints[endpointStart..endpointEnd]
                .Contains(
                    "snapshot",
                    StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Shop endpoint source is not independently bounded.");
        }

        var database = File.ReadAllText(
            Path.Combine(
                repository,
                "FSTService/Persistence/MetaDatabase.cs"));
        var databaseStart = database.IndexOf(
            "public void SaveItemShopTracks",
            StringComparison.Ordinal);
        var loadStart = databaseStart < 0
            ? -1
            : database.IndexOf(
                "public (HashSet<string> InShop",
                databaseStart,
                StringComparison.Ordinal);
        var databaseEnd = loadStart < 0
            ? -1
            : database.IndexOf(
                "\n    public ",
                loadStart,
                StringComparison.Ordinal);
        if (databaseStart < 0
            || loadStart < databaseStart
            || databaseEnd < databaseStart)
        {
            throw new InvalidDataException(
                "Item-shop persistence source is not independently bounded.");
        }
        var databaseScope =
            database[databaseStart..databaseEnd];
        if (!databaseScope.Contains(
                "item_shop_tracks",
                StringComparison.Ordinal)
            || databaseScope.Contains(
                "leaderboard_entries_snapshot",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Item-shop persistence source depends on snapshot-generation state.");
        }
    }

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

    private static byte[] ReadRegularInputBytes(
        string path)
    {
        ValidateRegularInput(path);
        return File.ReadAllBytes(path);
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
