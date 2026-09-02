using System.Text;
using System.Text.Json;
using FstSnapshotGenerationDrop;
using FstSnapshotGenerationRestoreAuthorization;
using FstSnapshotGenerationRestoreContinuation;

namespace FSTService.Tests.Unit;

public sealed class SnapshotGenerationRestoreAuthorizationTests
{
    [Fact]
    public void AuthorizationIdIsDeterministicAndEvidenceBound()
    {
        var first =
            RestoreToolAuthorizationContract
                .DeriveAuthorizationId(
                    Request(),
                    new string('2', 64));
        var repeated =
            RestoreToolAuthorizationContract
                .DeriveAuthorizationId(
                    Request(),
                    new string('2', 64));
        var changed =
            RestoreToolAuthorizationContract
                .DeriveAuthorizationId(
                    Request(),
                    new string('3', 64));

        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.Equal(first, repeated);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void AuthorizerHasOnlyNarrowNonRestoreSurface()
    {
        var repository =
            AuthorizationPackage.FindRepositoryRoot();
        var directory = Path.Combine(
            repository,
            "tools",
            "FstSnapshotGenerationRestoreAuthorization");
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
                "FstSnapshotGenerationRestoreAuthorization.csproj"));
        var wrapper = File.ReadAllText(
            Path.Combine(
                repository,
                "tools",
                "postgres-snapshot-generation-restore-authorize.sh"));
        var restoreSource = File.ReadAllText(
            Path.Combine(
                repository,
                "tools",
                "postgres-snapshot-generation-restore.py"));

        Assert.Contains(
            "\"prepare-repair-package\"",
            source);
        Assert.Contains(
            "\"authorize-repair-tool\"",
            source);
        Assert.Contains(
            "\"confirm-repair-tool\"",
            source);
        Assert.Contains(
            "\"prepare-continuation-package\"",
            source);
        Assert.Contains(
            "\"authorize-continuation-tool\"",
            source);
        Assert.Contains(
            "\"confirm-continuation-tool\"",
            source);
        Assert.DoesNotContain(
            "\"restore\" =>",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"docker\"",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Docker.DotNet",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "--relation",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "--schema",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "--instrument",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "--snapshot",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "authorized-tool-sha",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"final-tool\"",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "authorized-continuation-tool-sha",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"FstSnapshotGenerationRestoreContinuation\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"status\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"--porcelain\"",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            " docker ",
            wrapper,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "authorize-repair-tool",
            restoreSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceBuildEvidenceRejectsIdentityTamper()
    {
        var evidence =
            new ServiceImageBuildEvidence(
                1,
                RestoreContinuationContract
                    .ServiceImageBuildEvidenceToolId,
                "accepted",
                "stabilized",
                DateTimeOffset.Parse(
                    "2026-09-01T00:02:42Z"),
                new string('1', 64),
                new string('2', 64),
                new string('3', 40),
                new string('4', 40),
                true,
                new string('5', 64),
                new string('6', 64));

        ContinuationAuthorizationPackage
            .ValidateServiceImageBuildEvidence(
                evidence,
                "stabilized",
                new string('1', 64),
                new string('2', 64),
                new string('3', 40),
                new string('4', 40),
                true);

        Assert.Throws<InvalidDataException>(
            () => ContinuationAuthorizationPackage
                .ValidateServiceImageBuildEvidence(
                    evidence with
                    {
                        ImageSha256 =
                            new string('7', 64),
                    },
                    "stabilized",
                    new string('1', 64),
                    new string('2', 64),
                    new string('3', 40),
                    new string('4', 40),
                    true));
        Assert.Throws<InvalidDataException>(
            () => ContinuationAuthorizationPackage
                .ValidateServiceImageBuildEvidence(
                    evidence with
                    {
                        WorktreeClean = false,
                    },
                    "stabilized",
                    new string('1', 64),
                    new string('2', 64),
                    new string('3', 40),
                    new string('4', 40),
                    true));
    }

    [Fact]
    public void RestoreScopeEvidenceAcceptsExactArchiveRolesOnly()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            $"restore-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var planPath = Path.Combine(
            root,
            "plan.json");
        var reportPath = Path.Combine(
            root,
            "report.json");
        try
        {
            var plan = new
            {
                restoreOperationId =
                    new string('1', 32),
                dropOperationId =
                    new string('2', 32),
                planDigest =
                    new string('3', 64),
                archivedIndexNames = new
                {
                    pk = "sgqi_operation_pk",
                    score = "sgqi_operation_score",
                },
                selectedTocEntries = new[]
                {
                    "1; 1 10 TABLE public child_relation fst",
                    "2; 0 10 TABLE DATA public child_relation fst",
                    "3; 2 11 CONSTRAINT public child_relation sgqi_operation_pk fst",
                    "4; 1 12 INDEX public sgqi_operation_score fst",
                },
                executedTocEntries = new[]
                {
                    "1; 1 10 TABLE public child_relation fst",
                    "2; 0 10 TABLE DATA public child_relation fst",
                },
                target = new
                {
                    childSchema = "public",
                    childRelation =
                        "child_relation",
                    instrument =
                        "Solo_PeripheralCymbals",
                    snapshotId = 1314L,
                    rowCount = 8627L,
                    rowFingerprintSha256 =
                        new string('4', 64),
                },
            };
            var report = new
            {
                status = "restored",
                commitOutcome = "committed",
                rowFingerprint = new
                {
                    rowCount = 8627L,
                    sha256 =
                        new string('4', 64),
                },
            };
            File.WriteAllText(
                planPath,
                JsonSerializer.Serialize(plan));
            File.WriteAllText(
                reportPath,
                JsonSerializer.Serialize(report));
            using var planDocument =
                JsonDocument.Parse(
                    File.ReadAllBytes(planPath));
            using var reportDocument =
                JsonDocument.Parse(
                    File.ReadAllBytes(reportPath));

            var evidence =
                ContinuationAuthorizationPackage
                    .BuildRestoreScopeIsolationEvidence(
                        planDocument.RootElement,
                        reportDocument.RootElement,
                        planPath,
                        reportPath);

            Assert.True(
                evidence
                    .ExactTargetTableAndDataOnly);
            Assert.True(
                evidence
                    .RepositoryOwnedIndexesCreatedSeparately);

            var invalidPlan = JsonSerializer
                .Serialize(plan)
                .Replace(
                    "INDEX public sgqi_operation_score",
                    "INDEX public unrelated_score",
                    StringComparison.Ordinal);
            File.WriteAllText(
                planPath,
                invalidPlan);
            using var invalidDocument =
                JsonDocument.Parse(
                    File.ReadAllBytes(planPath));
            Assert.Throws<InvalidDataException>(
                () => ContinuationAuthorizationPackage
                    .BuildRestoreScopeIsolationEvidence(
                        invalidDocument.RootElement,
                        reportDocument.RootElement,
                        planPath,
                        reportPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
        }
    }

    [Fact]
    public void RepairPackageRequiresExactReadOnlyFileSet()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            $"restore-repair-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(
            Path.Combine(root, "test-evidence"));
        var payloads = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            ["restore-tool.py"] = "restore",
            ["postgres-snapshot-generation-archive.py"] =
                "archive",
            ["source-manifest.json"] = "{}",
            ["pinned-to-base.patch"] = "old-to-base",
            ["base-to-final.patch"] = "base-to-final",
            ["test-evidence/manifest.json"] = "{}",
            ["test-evidence/results.json"] = "{}",
        };
        try
        {
            foreach (var item in payloads)
            {
                File.WriteAllText(
                    Path.Combine(root, item.Key),
                    item.Value,
                    new UTF8Encoding(false));
            }
            var files = payloads.Keys
                .Order(StringComparer.Ordinal)
                .Select(path =>
                {
                    var full = Path.Combine(root, path);
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
                    new string('1', 32),
                    new string('2', 64),
                    new string('3', 64),
                    new string('4', 64),
                    new string('5', 64),
                    new string('6', 64),
                    RestoreToolAuthorizationContract
                        .ValidatorBaseToolSha256,
                    files.Single(file =>
                        file.Path ==
                        "restore-tool.py").Sha256,
                    files.Single(file =>
                        file.Path ==
                        "postgres-snapshot-generation-archive.py").Sha256,
                    new string('7', 64),
                    new string('8', 40),
                    new string('9', 40),
                    files.Single(file =>
                        file.Path ==
                        "pinned-to-base.patch").Sha256,
                    files.Single(file =>
                        file.Path ==
                        "base-to-final.patch").Sha256,
                    files.Single(file =>
                        file.Path ==
                        "source-manifest.json").Sha256,
                    files.Single(file =>
                        file.Path ==
                        "test-evidence/manifest.json").Sha256,
                    files);
            DropEvidenceValidator.WriteNewCanonical(
                Path.Combine(
                    root,
                    "repair-manifest.json"),
                manifest);
            var checksumPaths = payloads.Keys
                .Append("repair-manifest.json")
                .Order(StringComparer.Ordinal);
            File.WriteAllText(
                Path.Combine(root, "SHA256SUMS"),
                string.Concat(
                    checksumPaths.Select(path =>
                        $"{DropEvidenceValidator.Sha256File(Path.Combine(root, path))}  {path}\n")),
                new UTF8Encoding(false));
            MakeFilesReadOnly(root);

            var validated =
                AuthorizationPackage.Validate(root);

            Assert.Equal(
                manifest.AuthorizedRestoreToolSha256,
                validated
                    .AuthorizedRestoreToolSha256);
            MakeFilesWritable(root);
            File.WriteAllText(
                Path.Combine(root, "unexpected.txt"),
                "unexpected");
            MakeFilesReadOnly(root);
            Assert.Throws<InvalidDataException>(
                () => AuthorizationPackage
                    .Validate(root));
        }
        finally
        {
            MakeFilesWritable(root);
            if (Directory.Exists(root))
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
        }
    }

    private static void MakeFilesReadOnly(string root)
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

    private static RestoreToolAuthorizationRequest
        Request() =>
        new(
            new string('1', 32),
            new string('2', 64),
            new string('3', 64),
            new string('4', 64),
            RestoreToolAuthorizationContract
                .ValidatorBaseToolSha256,
            new string('5', 64),
            new string('6', 64),
            new string('7', 64),
            new string('8', 64),
            new string('9', 40),
            new string('a', 40),
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            new string('e', 64),
            "reason",
            "reason text",
            "operator",
            "reviewer",
            "approval",
            JsonDocument.Parse("{}")
                .RootElement.Clone());

    private static void MakeFilesWritable(string root)
    {
        if (OperatingSystem.IsWindows()
            || !Directory.Exists(root))
        {
            return;
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
