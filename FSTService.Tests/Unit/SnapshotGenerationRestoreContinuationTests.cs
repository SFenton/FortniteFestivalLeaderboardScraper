using System.Text;
using System.Text.Json;
using FstSnapshotGenerationRestoreContinuation;
using FstSnapshotGenerationQuarantine;

namespace FSTService.Tests.Unit;

public sealed class SnapshotGenerationRestoreContinuationTests
{
    [Fact]
    public void ContinuationAuthorizationIdIsDeterministic()
    {
        var request = Request();
        var first =
            RestoreContinuationContract
                .DeriveAuthorizationId(
                    request,
                    new string('f', 64));
        var repeated =
            RestoreContinuationContract
                .DeriveAuthorizationId(
                    request,
                    new string('f', 64));
        var changed =
            RestoreContinuationContract
                .DeriveAuthorizationId(
                    request with
                    {
                        CandidateRouteManifestSha256 =
                            new string('e', 64),
                    },
                    new string('f', 64));
        var bridgeChanged =
            RestoreContinuationContract
                .DeriveAuthorizationId(
                    request with
                    {
                        TemporalBridgeEvidenceSha256 =
                            new string('d', 64),
                    },
                    new string('f', 64));

        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.Equal(first, repeated);
        Assert.NotEqual(first, changed);
        Assert.NotEqual(first, bridgeChanged);
    }

    [Fact]
    public void ContinuationToolHasOnlyEvidenceSurface()
    {
        var repository =
            ContinuationPackage.FindRepositoryRoot();
        var directory = Path.Combine(
            repository,
            "tools",
            "FstSnapshotGenerationRestoreContinuation");
        var source = string.Join(
            "\n",
            Directory.EnumerateFiles(
                    directory,
                    "*.cs",
                    SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        var project = File.ReadAllText(
            Path.Combine(
                directory,
                "FstSnapshotGenerationRestoreContinuation.csproj"));
        var wrapper = File.ReadAllText(
            Path.Combine(
                repository,
                "tools",
                "postgres-snapshot-generation-restore-continuation.sh"));
        var dependencyGraph = File.ReadAllText(
            Path.Combine(
                directory,
                "bin",
                "Release",
                "net9.0",
                "FstSnapshotGenerationRestoreContinuation.deps.json"));

        Assert.Contains(
            "\"confirm\" =>",
            source);
        Assert.Contains(
            "\"attest\" =>",
            source);
        Assert.Contains(
            "\"finalize\" =>",
            source);
        foreach (var prohibited in new[]
                 {
                     "\"plan\" =>",
                     "\"restore\" =>",
                     "\"execute\" =>",
                     "pg_restore",
                     "restore-list",
                     "ATTACH PARTITION",
                     "fst_restore_snapshot_generation(",
                     "\"docker\"",
                     "--relation",
                     "--schema",
                     "--instrument",
                     "--snapshot",
                     "authorize-continuation-tool",
                     "allowed-route",
                     "bridge-route",
                     "temporal-route",
                 })
        {
            Assert.DoesNotContain(
                prohibited,
                source,
                StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(
            "FstSnapshotGenerationDrop",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Docker.DotNet",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "FstSnapshotGenerationEvidence",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            " docker ",
            wrapper,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "FST_SNAPSHOT_RESTORE_CONTINUATION_BINARY_SHA256",
            wrapper,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Docker.DotNet",
            dependencyGraph,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"FSTService/",
            dependencyGraph,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"FstSnapshotGenerationDrop/",
            dependencyGraph,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceRuntimeIsolationEvidenceIsCanonicalAndSealed()
    {
        var evidence =
            new ServiceRuntimeIsolationEvidence(
                1,
                RestoreContinuationContract
                    .ServiceRuntimeIsolationToolId,
                "accepted",
                DateTimeOffset.Parse(
                    "2026-09-01T04:00:00Z"),
                new string('1', 64),
                new string('2', 64),
                new string('3', 40),
                new string('4', 64),
                new string('5', 64),
                new string('6', 64),
                DateTimeOffset.Parse(
                    "2026-08-31T21:18:05Z"),
                new string('7', 64),
                new string('8', 64),
                new string('9', 40),
                new string('a', 64),
                DateTimeOffset.Parse(
                    "2026-09-01T00:03:47Z"),
                [
                    new ServiceRuntimeSourceFile(
                        "FSTService/Api/ShopCacheService.cs",
                        new string('b', 64)),
                ],
                false,
                false);

        var sealedEvidence = evidence.Seal();

        Assert.Matches(
            "^[0-9a-f]{64}$",
            sealedEvidence.EvidenceSha256!);
        Assert.Equal(
            sealedEvidence,
            sealedEvidence.Seal());
    }

    [Fact]
    public void SharedMiddleCaptureMismatchIsRejected()
    {
        var bridge = TemporalBridge(
            historicalCandidateSha256:
                new string('1', 64));
        var stabilized =
            new DetailedRouteParityEvidence(
                new RouteParityEvidence(
                    "/evidence/post-restore/manifest.json",
                    new string('2', 64),
                    "/evidence/repeat/manifest.json",
                    new string('3', 64),
                    165,
                    1336,
                    55,
                    true,
                    true,
                    0),
                QuarantineEvidenceValidator
                    .RouteParityAlgorithmId,
                true,
                new string('4', 64),
                []);

        Assert.Throws<InvalidDataException>(
            () => RestoreContinuationContract
                .ValidateSharedMiddleCapture(
                    bridge,
                    stabilized));
    }

    [Fact]
    public void ContinuationPackageRequiresExactReadOnlySet()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            $"continuation-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            foreach (var relative in
                     ContinuationPackage.PayloadPaths
                         .Where(relative =>
                             relative is not
                                 "historical-service-build-evidence.json"
                             and not
                                 "stabilized-service-build-evidence.json"
                             and not
                                 "service-runtime-isolation.json"))
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(path)!);
                File.WriteAllText(
                    path,
                    relative,
                    new UTF8Encoding(false));
            }
            var historicalBuild =
                new ServiceImageBuildEvidence(
                    1,
                    RestoreContinuationContract
                        .ServiceImageBuildEvidenceToolId,
                    "accepted",
                    "historical-baseline",
                    DateTimeOffset.Parse(
                        "2026-08-31T21:17:04Z"),
                    new string('1', 64),
                    new string('2', 64),
                    new string('3', 40),
                    null,
                    false,
                    new string('4', 64),
                    new string('5', 64));
            var stabilizedBuild =
                new ServiceImageBuildEvidence(
                    1,
                    RestoreContinuationContract
                        .ServiceImageBuildEvidenceToolId,
                    "accepted",
                    "stabilized",
                    DateTimeOffset.Parse(
                        "2026-09-01T00:02:42Z"),
                    new string('6', 64),
                    new string('7', 64),
                    new string('3', 40),
                    new string('8', 40),
                    true,
                    new string('9', 64),
                    new string('a', 64));
            ContinuationPackage.WriteNewCanonical(
                Path.Combine(
                    root,
                    "historical-service-build-evidence.json"),
                historicalBuild);
            ContinuationPackage.WriteNewCanonical(
                Path.Combine(
                    root,
                    "stabilized-service-build-evidence.json"),
                stabilizedBuild);
            var sourceFiles = Enumerable.Range(0, 5)
                .Select(index =>
                    new ServiceRuntimeSourceFile(
                        $"source-{index}.cs",
                        new string(
                            (char)('b' + index),
                            64)))
                .ToArray();
            var runtime =
                new ServiceRuntimeIsolationEvidence(
                    1,
                    RestoreContinuationContract
                        .ServiceRuntimeIsolationToolId,
                    "accepted",
                    DateTimeOffset.Parse(
                        "2026-09-01T04:00:00Z"),
                    new string('4', 64),
                    new string('e', 64),
                    new string('3', 40),
                    historicalBuild.ImageSha256,
                    historicalBuild.ServiceDllSha256,
                    ContinuationPackage.Sha256File(
                        Path.Combine(
                            root,
                            "historical-service-build-evidence.json")),
                    DateTimeOffset.Parse(
                        "2026-08-31T21:18:05Z"),
                    stabilizedBuild.ImageSha256,
                    stabilizedBuild.ServiceDllSha256,
                    stabilizedBuild.RepositoryCommit!,
                    ContinuationPackage.Sha256File(
                        Path.Combine(
                            root,
                            "stabilized-service-build-evidence.json")),
                    DateTimeOffset.Parse(
                        "2026-09-01T00:03:47Z"),
                    sourceFiles,
                    false,
                    false).Seal();
            ContinuationPackage.WriteNewCanonical(
                Path.Combine(
                    root,
                    "service-runtime-isolation.json"),
                runtime);
            var files =
                ContinuationPackage.PayloadPaths
                    .Select(relative =>
                    {
                        var path = Path.Combine(
                            root,
                            relative);
                        return new
                            RestoreContinuationPackageFile(
                                relative,
                                ContinuationPackage
                                    .Sha256File(path),
                                new FileInfo(path).Length);
                    })
                    .ToArray();
            var manifest = Manifest(files);
            ContinuationPackage.WriteNewCanonical(
                Path.Combine(
                    root,
                    "continuation-manifest.json"),
                manifest);
            var paths =
                ContinuationPackage.PayloadPaths
                    .Append(
                        "continuation-manifest.json")
                    .Order(StringComparer.Ordinal);
            File.WriteAllText(
                Path.Combine(root, "SHA256SUMS"),
                string.Concat(
                    paths.Select(relative =>
                        $"{ContinuationPackage.Sha256File(Path.Combine(root, relative))}  {relative}\n")),
                new UTF8Encoding(false));
            MakeReadOnly(root);

            var validated =
                ContinuationPackage.Validate(root);

            Assert.Equal(
                manifest.RestoreOperationId,
                validated.RestoreOperationId);
            MakeWritable(root);
            File.WriteAllText(
                Path.Combine(root, "unexpected.txt"),
                "unexpected");
            MakeReadOnly(root);
            Assert.Throws<InvalidDataException>(
                () => ContinuationPackage
                    .Validate(root));
            MakeWritable(root);
            File.Delete(
                Path.Combine(
                    root,
                    "unexpected.txt"));
            WriteCanonicalReplacing(
                Path.Combine(
                    root,
                    "historical-service-build-evidence.json"),
                historicalBuild with
                {
                    ImageSha256 =
                        new string('f', 64),
                });
            var tamperedFiles =
                ContinuationPackage.PayloadPaths
                    .Select(relative =>
                    {
                        var path = Path.Combine(
                            root,
                            relative);
                        return new
                            RestoreContinuationPackageFile(
                                relative,
                                ContinuationPackage
                                    .Sha256File(path),
                                new FileInfo(path).Length);
                    })
                    .ToArray();
            WriteCanonicalReplacing(
                Path.Combine(
                    root,
                    "continuation-manifest.json"),
                manifest with
                {
                    Files = tamperedFiles,
                });
            File.WriteAllText(
                Path.Combine(root, "SHA256SUMS"),
                string.Concat(
                    paths.Select(relative =>
                        $"{ContinuationPackage.Sha256File(Path.Combine(root, relative))}  {relative}\n")),
                new UTF8Encoding(false));
            MakeReadOnly(root);
            Assert.Throws<InvalidDataException>(
                () => ContinuationPackage
                    .Validate(root));
        }
        finally
        {
            MakeWritable(root);
            if (Directory.Exists(root))
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
        }
    }

    [Fact]
    public void LiveRestoreParityFixtureContainsHashesOnly()
    {
        var repository =
            ContinuationPackage.FindRepositoryRoot();
        using var fixture =
            JsonDocument.Parse(
                File.ReadAllBytes(
                    Path.Combine(
                        repository,
                        "tools",
                        "testdata",
                        "snapshot-generation-live-restore-parity",
                        "fixture-manifest.json")));
        var root = fixture.RootElement;
        var safety =
            root.GetProperty("safetyReview");

        Assert.Equal(
            0,
            safety.GetProperty("exportBodies")
                .GetInt32());
        Assert.Equal(
            0,
            safety.GetProperty("entryNames")
                .GetInt32());
        Assert.Equal(
            "6e84def5c11574cfbfee8803fd61ea13cad160e62d61f216b66b64c611bc966d",
            root.GetProperty("routes")
                .GetProperty("band-export")
                .GetProperty("semanticSha256")
                .GetString());
        Assert.Equal(
            "b2ce01a0f0f5e9e60e844d485622ff8862bb41ba34551813f04a8e9d4ca0f04b",
            root.GetProperty("routes")
                .GetProperty("player-export")
                .GetProperty("semanticSha256")
                .GetString());
    }

    private static RestoreContinuationAuthorizationRequest
        Request() =>
        new(
            new string('1', 32),
            new string('2', 32),
            new string('3', 32),
            new string('4', 64),
            new string('5', 64),
            new string('6', 64),
            new string('7', 64),
            new string('8', 64),
            new string('9', 64),
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            new string('e', 64),
            QuarantineEvidenceValidator
                .RouteParityAlgorithmId,
            new string('f', 64),
            new string('8', 64),
            RestoreContinuationContract
                .TemporalBridgePredicateId,
            new string('9', 64),
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            new string('0', 64),
            new string('1', 64),
            new string('2', 64),
            new string('3', 64),
            165,
            1336,
            new string('4', 40),
            new string('5', 40),
            new string('6', 64),
            new string('7', 64),
            new string('8', 64),
            "post_restore_route_parity",
            "reviewed continuation",
            "operator",
            "reviewer",
            "approval",
            JsonDocument.Parse(
                """{"validated":true}""")
                .RootElement.Clone());

    private static ShopDailyInventoryRolloverEvidence
        TemporalBridge(
            string historicalCandidateSha256) =>
        new(
            RestoreContinuationContract
                .TemporalBridgePredicateId,
            "/evidence/post-drop/manifest.json",
            new string('0', 64),
            "/evidence/post-restore/manifest.json",
            historicalCandidateSha256,
            DateTimeOffset.Parse(
                "2026-08-31T21:20:46Z"),
            DateTimeOffset.Parse(
                "2026-09-01T01:31:36Z"),
            DateTimeOffset.Parse(
                "2026-08-31T21:18:02Z"),
            DateTimeOffset.Parse(
                "2026-09-01T00:03:44Z"),
            165,
            1336,
            55,
            1,
            "shop:semantic-json",
            new string('5', 64),
            new string('6', 64),
            new string('7', 64),
            new string('8', 64),
            new string('9', 64),
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            117,
            117,
            100,
            100,
            17,
            100,
            0,
            0,
            0,
            0,
            0,
            710,
            217,
            0,
            0);

    private static RestoreContinuationPackageManifest
        Manifest(
            IReadOnlyList<
                RestoreContinuationPackageFile> files) =>
        new(
            RestoreContinuationContract
                .SchemaVersion,
            RestoreContinuationContract
                .PackageToolId,
            "accepted",
            DateTimeOffset.UtcNow,
            new string('1', 32),
            new string('2', 32),
            new string('3', 64),
            "/evidence/restore-plan.json",
            new string('4', 64),
            "/evidence/restore-report.json",
            new string('5', 64),
            new string('6', 32),
            new string('7', 64),
            "/evidence/repair-package-v5",
            new string('8', 64),
            "/evidence/recovery-bundle-v2",
            new string('9', 64),
            files.Single(file =>
                file.Path ==
                "runtime/FstSnapshotGenerationRestoreContinuation.dll")
                .Sha256,
            files.Single(file =>
                file.Path ==
                "runtime/FstSnapshotGenerationEvidence.dll")
                .Sha256,
            new string('a', 64),
            new string('b', 64),
            new string('c', 40),
            new string('d', 40),
            files.Single(file =>
                file.Path ==
                "predecessor-to-continuation.diff")
                .Sha256,
            files.Single(file =>
                file.Path ==
                "source-manifest.json")
                .Sha256,
            files.Single(file =>
                file.Path ==
                "test-evidence/manifest.json")
                .Sha256,
            QuarantineEvidenceValidator
                .RouteParityAlgorithmId,
            files.Single(file =>
                file.Path ==
                "route-parity-preflight.json")
                .Sha256,
            new string('1', 64),
            RestoreContinuationContract
                .TemporalBridgePredicateId,
            new string('2', 64),
            new string('3', 64),
            "/evidence/continuation-package/service-runtime-isolation.json",
            files.Single(file =>
                file.Path ==
                "service-runtime-isolation.json")
                .Sha256,
            "/evidence/routes-post-drop/manifest.json",
            new string('4', 64),
            new string('5', 64),
            "/evidence/routes-post-drop/manifest.json",
            new string('e', 64),
            new string('f', 64),
            "/evidence/routes-post-restore-repeat/manifest.json",
            new string('0', 64),
            new string('1', 64),
            165,
            1336,
            files);

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
    }

    private static void MakeWritable(string root)
    {
        if (OperatingSystem.IsWindows())
            return;
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

    private static void WriteCanonicalReplacing<T>(
        string path,
        T value)
    {
        var canonical =
            QuarantineJson.Canonical(value);
        var bytes = new byte[
            canonical.Length + 1];
        canonical.CopyTo(bytes, 0);
        bytes[^1] = (byte)'\n';
        File.WriteAllBytes(path, bytes);
    }
}
