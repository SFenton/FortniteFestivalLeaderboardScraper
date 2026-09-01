using System.Text;
using System.Text.Json;
using FstSnapshotGenerationDrop;
using FstSnapshotGenerationRestoreAuthorization;

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
