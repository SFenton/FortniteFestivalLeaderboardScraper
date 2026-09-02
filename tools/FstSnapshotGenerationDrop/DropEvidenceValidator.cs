using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FstSnapshotGenerationQuarantine;

namespace FstSnapshotGenerationDrop;

public sealed class DropEvidencePaths
{
    public const string RequiredEvidenceBase =
        "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence";
    public const string EnvironmentName =
        "FST_SNAPSHOT_DROP_EVIDENCE_ROOT";

    private readonly string _root;

    public DropEvidencePaths(string configuredRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            throw new InvalidOperationException(
                $"{EnvironmentName} must name a directory below the FST evidence root.");
        }
        var requiredBase = ResolveExistingDirectory(
            RequiredEvidenceBase);
        _root = ResolveExistingDirectory(configuredRoot);
        EnsureUnder(_root, requiredBase, "Configured evidence root");
    }

    public static DropEvidencePaths FromEnvironment() =>
        new(
            Environment.GetEnvironmentVariable(EnvironmentName)
            ?? "");

    public string ResolveInputFile(string path)
    {
        var resolved = ResolveExistingFile(path);
        EnsureUnder(resolved, _root, "Evidence input");
        return resolved;
    }

    public string ResolveInputDirectory(string path)
    {
        var resolved = ResolveExistingDirectory(path);
        EnsureUnder(resolved, _root, "Evidence input");
        return resolved;
    }

    public string ResolveNewFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full) || Directory.Exists(full))
            throw new IOException(
                $"Evidence output already exists: {full}");
        var parent = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException(
                $"Evidence output has no parent: {full}");
        var resolvedParent = ResolveExistingDirectory(parent);
        EnsureUnder(resolvedParent, _root, "Evidence output");
        RejectSymbolicLinks(full, allowMissingLeaf: true);
        return Path.Combine(
            resolvedParent,
            Path.GetFileName(full));
    }

    public string ResolveNewDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full) || Directory.Exists(full))
            throw new IOException(
                $"Recovery bundle already exists: {full}");
        var parent = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException(
                $"Recovery bundle has no parent: {full}");
        var resolvedParent = ResolveExistingDirectory(parent);
        EnsureUnder(resolvedParent, _root, "Recovery bundle");
        RejectSymbolicLinks(full, allowMissingLeaf: true);
        return Path.Combine(
            resolvedParent,
            Path.GetFileName(full));
    }

    private static string ResolveExistingFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException(
                "Evidence file was not found.",
                full);
        RejectSymbolicLinks(full, allowMissingLeaf: false);
        return full;
    }

    private static string ResolveExistingDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException(full);
        RejectSymbolicLinks(full, allowMissingLeaf: false);
        return full.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static void RejectSymbolicLinks(
        string path,
        bool allowMissingLeaf)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)
            ?? throw new InvalidOperationException(
                $"Path has no filesystem root: {path}");
        var current = root;
        var parts = full[root.Length..].Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            if (!File.Exists(current)
                && !Directory.Exists(current))
            {
                if (allowMissingLeaf
                    && index == parts.Length - 1)
                {
                    return;
                }
                throw new FileNotFoundException(
                    "Evidence path component was not found.",
                    current);
            }
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (!string.IsNullOrEmpty(info.LinkTarget))
            {
                throw new InvalidOperationException(
                    $"Evidence paths cannot contain symbolic links: {current}");
            }
        }
    }

    private static void EnsureUnder(
        string path,
        string root,
        string label)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".")
            return;
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label} must remain below {root}: {path}");
        }
    }
}

public static class DropEvidenceValidator
{
    public static T ReadStrict<T>(string path)
    {
        var value = JsonSerializer.Deserialize<T>(
            File.ReadAllBytes(path),
            DropJson.Strict);
        return value
            ?? throw new InvalidDataException(
                $"Evidence file is empty: {path}");
    }

    public static SnapshotGenerationQuarantinePlan
        ReadQuarantinePlan(string path)
    {
        var plan = ReadStrict<
            SnapshotGenerationQuarantinePlan>(path);
        plan.Validate();
        return plan;
    }

    public static SnapshotGenerationQuarantineExecutionReport
        ReadExecutionReport(string path)
    {
        var report = ReadStrict<
            SnapshotGenerationQuarantineExecutionReport>(path);
        var expected = report.Seal();
        if (string.IsNullOrWhiteSpace(report.ReportSha256)
            || !string.Equals(
                report.ReportSha256,
                expected.ReportSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Execution report digest is invalid: {path}");
        }
        return report;
    }

    public static SnapshotGenerationQuarantineAttestationReport
        ReadQuarantineAttestation(string path)
    {
        var report = ReadStrict<
            SnapshotGenerationQuarantineAttestationReport>(path);
        var expected = report.Seal();
        if (string.IsNullOrWhiteSpace(report.ReportSha256)
            || !string.Equals(
                report.ReportSha256,
                expected.ReportSha256,
                StringComparison.Ordinal)
            || report.Parity.RouteCount != 55
            || report.Parity.DifferenceCount != 0
            || !report.Parity.StatusParity
            || !report.Parity.SemanticJsonParity)
        {
            throw new InvalidDataException(
                $"Quarantine attestation is invalid: {path}");
        }
        return report;
    }

    public static SnapshotGenerationHealthEvidence
        ReadHealthEvidence(string path)
    {
        var evidence =
            ReadStrict<SnapshotGenerationHealthEvidence>(
                path);
        evidence.Validate();
        return evidence;
    }

    public static SnapshotGenerationArchiveSemanticEvidence
        ReadArchiveSemanticEvidence(
            ArchivePackageEvidence archive)
    {
        var catalogPath = Path.Combine(
            archive.PackagePath,
            "catalog.json");
        var manifestPath = Path.Combine(
            archive.PackagePath,
            "manifest.json");
        var catalogSha = Sha256File(catalogPath);
        using var manifest = JsonDocument.Parse(
            File.ReadAllBytes(manifestPath));
        if (manifest.RootElement
                .GetProperty("catalog")
                .GetProperty("sha256")
                .GetString() != catalogSha)
        {
            throw new InvalidDataException(
                "Archive catalog checksum differs from its manifest.");
        }
        using var catalog = JsonDocument.Parse(
            File.ReadAllBytes(catalogPath));
        var relations = catalog.RootElement
            .GetProperty("physicalCatalog")
            .EnumerateArray()
            .ToArray();
        foreach (var relation in relations)
        {
            var relationName = relation
                .GetProperty("name")
                .GetString()
                ?? throw new InvalidDataException(
                    "Archive catalog relation name is invalid.");
            var relationOid =
                ReadCanonicalCatalogInteger(
                    relation.GetProperty("oid"),
                    $"{relationName}.oid");
            _ = ReadCanonicalCatalogInteger(
                relation.GetProperty("relfilenode"),
                $"{relationName}.relfilenode",
                allowZero: true);
            foreach (var index in relation
                         .GetProperty("indexes")
                         .EnumerateArray())
            {
                var indexName = index
                    .GetProperty("indexName")
                    .GetString()
                    ?? throw new InvalidDataException(
                        "Archive catalog index name is invalid.");
                if (index.TryGetProperty(
                        "tableOid",
                        out var tableOid)
                    && ReadCanonicalCatalogInteger(
                        tableOid,
                        $"{indexName}.tableOid")
                    != relationOid)
                {
                    throw new InvalidDataException(
                        $"Archive index {indexName} table OID differs from its relation.");
                }
                _ = ReadCanonicalCatalogInteger(
                    index.GetProperty("indexOid"),
                    $"{indexName}.indexOid");
                _ = ReadCanonicalCatalogInteger(
                    index.GetProperty(
                        "indexRelfilenode"),
                    $"{indexName}.indexRelfilenode",
                    allowZero: true);
                if (index.TryGetProperty(
                        "parentIndexOid",
                        out var parentIndexOid)
                    && parentIndexOid.ValueKind !=
                        JsonValueKind.Null)
                {
                    _ = ReadCanonicalCatalogInteger(
                        parentIndexOid,
                        $"{indexName}.parentIndexOid");
                }
            }
        }
        var child = RequireSingleRelation(
            relations,
            archive.ChildOid,
            archive.ChildRelation);
        var root = RequireSingleRelation(
            relations,
            archive.RootOid,
            archive.RootRelation);
        if (ReadCanonicalCatalogInteger(
                child.GetProperty("relfilenode"),
                $"{archive.ChildRelation}.relfilenode")
            != archive.ChildRelfilenode)
        {
            throw new InvalidDataException(
                "Archive child relfilenode differs from its manifest.");
        }
        var top = relations.SingleOrDefault(
            relation =>
                relation.GetProperty("name")
                    .GetString() ==
                    "leaderboard_entries_snapshot");
        if (top.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException(
                "Archive catalog lacks the snapshot parent.");
        }
        var rootIndexes = root.GetProperty("indexes")
            .EnumerateArray()
            .ToDictionary(
                index =>
                    ReadCanonicalCatalogInteger(
                        index.GetProperty("indexOid"),
                        $"{archive.RootRelation}.indexOid"));
        var topIndexes = top.GetProperty("indexes")
            .EnumerateArray()
            .ToDictionary(
                index =>
                    ReadCanonicalCatalogInteger(
                        index.GetProperty("indexOid"),
                        "leaderboard_entries_snapshot.indexOid"));
        var indexes = child.GetProperty("indexes")
            .EnumerateArray()
            .Select(index => BuildSemanticIndex(
                index,
                rootIndexes,
                topIndexes))
            .OrderBy(index => index.Role,
                StringComparer.Ordinal)
            .ToArray();
        if (indexes.Length != 2
            || indexes.Select(index => index.Role)
                .Distinct(StringComparer.Ordinal)
                .Count() != 2)
        {
            throw new InvalidDataException(
                "Archive semantic index inventory is not exact.");
        }
        var constraints = child.GetProperty("constraints")
            .EnumerateArray()
            .Select(constraint => new
            {
                Type = constraint.GetProperty("type")
                    .GetString(),
                Validated = constraint
                    .GetProperty("validated")
                    .GetBoolean(),
                Definition = NormalizeWhitespace(
                    constraint
                        .GetProperty("definition")
                        .GetString()!),
            })
            .ToArray();
        if (constraints.Length != 1
            || constraints[0].Type != "p"
            || !constraints[0].Validated
            || constraints[0].Definition !=
                "PRIMARY KEY (snapshot_id, song_id, instrument, account_id)")
        {
            throw new InvalidDataException(
                "Archive primary-key constraint shape is unsupported.");
        }
        var relationSemantic = new
        {
            ProjectionVersion = 1,
            archive.Instrument,
            archive.SnapshotId,
            archive.RootSchema,
            archive.RootRelation,
            archive.RootOid,
            archive.ChildSchema,
            archive.ChildRelation,
            archive.ChildOid,
            archive.ChildRelfilenode,
            PartitionBound =
                child.GetProperty("partitionBound")
                    .GetString(),
            RelationKind =
                child.GetProperty("relationKind")
                    .GetString(),
            PersistenceKind =
                child.GetProperty("persistenceKind")
                    .GetString(),
            AccessMethod =
                child.GetProperty("accessMethod")
                    .GetString(),
            Tablespace =
                child.GetProperty("tablespace")
                    .GetString(),
            RelationOptions =
                child.GetProperty("relationOptions")
                    .Clone(),
            Columns = child.GetProperty("columns").Clone(),
            Constraints = constraints,
            Indexes = indexes,
        };
        var logicalIndexes = indexes.Select(index => new
        {
            index.Role,
            index.Primary,
            index.Unique,
            index.Valid,
            index.Ready,
            index.AccessMethod,
            index.TablespaceName,
            index.KeyColumns,
            index.SortDirections,
            index.NullsOrder,
            index.Opclasses,
            index.Collations,
            index.Expressions,
            index.Predicate,
            index.ParentRootIndexOid,
            index.ParentTopIndexOid,
            index.ParentTopRole,
        }).ToArray();
        var physicalIndexes = indexes.Select(index => new
        {
            index.Role,
            index.IndexOid,
            index.IndexRelfilenode,
            index.ParentRootIndexOid,
            index.ParentTopIndexOid,
        }).ToArray();
        return new SnapshotGenerationArchiveSemanticEvidence(
            ProjectionVersion: 1,
            CatalogSha256: catalogSha,
            SemanticCatalogSha256:
                DropJson.Sha256(relationSemantic),
            LogicalIndexShapeSha256:
                DropJson.Sha256(logicalIndexes),
            PhysicalIndexInventorySha256:
                DropJson.Sha256(physicalIndexes),
            Indexes: indexes);
    }

    public static DateTimeOffset ReadProofCompletedAt(
        string proofManifestPath)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(proofManifestPath));
        var root = document.RootElement;
        if (!root.TryGetProperty(
                "completedAtUtc",
                out var completed)
            || completed.ValueKind !=
                JsonValueKind.String
            || !completed.TryGetDateTimeOffset(
                out var parsed))
        {
            throw new InvalidDataException(
                "Proof manifest has no valid completedAtUtc.");
        }
        return parsed;
    }

    public static DateTimeOffset ReadRouteCapturedAt(
        string manifestPath)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(manifestPath));
        if (!document.RootElement.TryGetProperty(
                "capturedAtUtc",
                out var captured)
            || captured.ValueKind !=
                JsonValueKind.String
            || !captured.TryGetDateTimeOffset(
                out var parsed))
        {
            throw new InvalidDataException(
                "Route manifest has no valid capturedAtUtc.");
        }
        return parsed;
    }

    public static void ValidateQuarantineEvidence(
        SnapshotGenerationQuarantinePlan plan,
        SnapshotGenerationQuarantineExecutionReport
            quarantineReport,
        SnapshotGenerationQuarantineAttestationReport
            quarantinedAttestation,
        SnapshotGenerationQuarantineAttestationReport
            soakAttestation)
    {
        RequireExecution(
            plan,
            quarantineReport,
            "quarantine",
            "quarantined");
        RequireAttestation(
            plan,
            quarantinedAttestation,
            "quarantined");
        RequireAttestation(
            plan,
            soakAttestation,
            "soak");
        if (quarantinedAttestation.Parity
                .BaselineManifestSha256 !=
            plan.PreQuarantineParity
                .CandidateManifestSha256)
        {
            throw new InvalidDataException(
                "Quarantined attestation is not chained to the plan capture.");
        }
        if (quarantinedAttestation.Parity.PublicationId !=
                plan.Archive.TriggerPublicationId
            || quarantinedAttestation.Parity.PublishedScrapeId !=
                plan.Archive.TriggerScrapeId)
        {
            throw new InvalidDataException(
                "Quarantined attestation does not use the plan publication.");
        }
    }

    public static void ValidateRehearsalEvidence(
        SnapshotGenerationQuarantinePlan plan,
        SnapshotGenerationQuarantineExecutionReport
            quarantineReport,
        SnapshotGenerationQuarantineAttestationReport
            quarantinedAttestation,
        SnapshotGenerationQuarantineAttestationReport
            soakAttestation,
        SnapshotGenerationQuarantineExecutionReport
            reattachReport,
        SnapshotGenerationQuarantineAttestationReport
            reattachedAttestation)
    {
        ValidateQuarantineEvidence(
            plan,
            quarantineReport,
            quarantinedAttestation,
            soakAttestation);
        RequireExecution(
            plan,
            reattachReport,
            "reattach",
            "reattached");
        RequireAttestation(
            plan,
            reattachedAttestation,
            "reattached");
        if (quarantinedAttestation.Parity.PublicationId ==
                soakAttestation.Parity.PublicationId
            || quarantinedAttestation.Parity.PublishedScrapeId ==
                soakAttestation.Parity.PublishedScrapeId
            || reattachedAttestation.Parity.PublicationId !=
                soakAttestation.Parity.PublicationId
            || reattachedAttestation.Parity.PublishedScrapeId !=
                soakAttestation.Parity.PublishedScrapeId
            || reattachedAttestation.Parity
                .BaselineManifestSha256 !=
                soakAttestation.Parity
                    .CandidateManifestSha256)
        {
            throw new InvalidDataException(
                "Q1 rehearsal does not span one publication rotation.");
        }
    }

    public static void ValidateMatchingTargets(
        SnapshotGenerationQuarantinePlan rehearsal,
        SnapshotGenerationQuarantinePlan active)
    {
        if (rehearsal.OperationId == active.OperationId
            || rehearsal.Archive.Instrument !=
                active.Archive.Instrument
            || rehearsal.Archive.SnapshotId !=
                active.Archive.SnapshotId
            || rehearsal.Archive.RootRelation !=
                active.Archive.RootRelation
            || rehearsal.Archive.RootOid !=
                active.Archive.RootOid
            || rehearsal.Archive.ChildRelation !=
                active.Archive.ChildRelation
            || rehearsal.Archive.ChildOid !=
                active.Archive.ChildOid
            || rehearsal.Archive.ChildRelfilenode !=
                active.Archive.ChildRelfilenode
            || rehearsal.Archive.RowCount !=
                active.Archive.RowCount
            || rehearsal.Archive.TotalBytes !=
                active.Archive.TotalBytes
            || rehearsal.Archive.RowFingerprintSha256 !=
                active.Archive.RowFingerprintSha256
            || rehearsal.Archive
                    .StableChildIdentityHash !=
                active.Archive
                    .StableChildIdentityHash
            || rehearsal.Archive.DatabaseName !=
                active.Archive.DatabaseName
            || rehearsal.Archive.DatabaseOid !=
                active.Archive.DatabaseOid
            || rehearsal.Archive.SystemIdentifier !=
                active.Archive.SystemIdentifier
            || rehearsal.Archive.ServerVersionNum !=
                active.Archive.ServerVersionNum)
        {
            throw new InvalidDataException(
                "Q1 rehearsal and Q2 active quarantine do not bind the same archived physical child.");
        }
    }

    public static void ValidateMatchingSemantics(
        SnapshotGenerationArchiveSemanticEvidence
            rehearsal,
        SnapshotGenerationArchiveSemanticEvidence
            active)
    {
        if (rehearsal.ProjectionVersion != 1
            || active.ProjectionVersion != 1
            || rehearsal.SemanticCatalogSha256 !=
                active.SemanticCatalogSha256
            || rehearsal.LogicalIndexShapeSha256 !=
                active.LogicalIndexShapeSha256
            || rehearsal.PhysicalIndexInventorySha256 !=
                active.PhysicalIndexInventorySha256)
        {
            throw new InvalidDataException(
                "Q1 rehearsal and Q2 active archives differ semantically or physically.");
        }
    }

    public static string CreateRecoveryBundle(
        string destination,
        string archivePackage,
        string rehearsalArchivePackage,
        IReadOnlyDictionary<string, string> evidenceFiles,
        string dropBinaryPath,
        string restoreToolPath,
        string archiveToolPath,
        long physicalBytes,
        long reserveBytes)
    {
        if (reserveBytes < 0)
            throw new ArgumentOutOfRangeException(
                nameof(reserveBytes));
        var sourceRoot = Path.GetFullPath(
            archivePackage)
            .TrimEnd(Path.DirectorySeparatorChar);
        var destinationRoot = Path.GetFullPath(
            destination)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (IsBelow(destinationRoot, sourceRoot)
            || IsBelow(sourceRoot, destinationRoot))
        {
            throw new InvalidOperationException(
                "Recovery bundle and archive package cannot overlap.");
        }
        var sourceArchivePath = Path.Combine(
            sourceRoot,
            "archive.custom");
        if (!File.Exists(sourceArchivePath))
        {
            throw new FileNotFoundException(
                "Archive payload was not found.",
                sourceArchivePath);
        }
        var archiveBytes =
            new FileInfo(sourceArchivePath).Length;
        var required = Math.Max(
            checked(
                2 * physicalBytes
                + archiveBytes
                + 1024L * 1024 * 1024),
            2L * 1024 * 1024 * 1024);
        var copyBytes = DirectoryBytes(sourceRoot);
        var rehearsalRoot = Path.GetFullPath(
            rehearsalArchivePackage)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(
                rehearsalRoot,
                sourceRoot,
                StringComparison.Ordinal))
        {
            copyBytes = checked(
                copyBytes
                + DirectoryBytes(rehearsalRoot));
        }
        foreach (var item in evidenceFiles)
        {
            var parent = Path.GetFullPath(
                Path.GetDirectoryName(item.Value)!);
            if (ShouldCopyEvidenceDirectory(
                    item.Key,
                    parent)
                && (IsBelow(destinationRoot, parent)
                    || IsBelow(parent, destinationRoot)))
            {
                throw new InvalidOperationException(
                    $"Recovery bundle and {item.Key} evidence cannot overlap.");
            }
        }
        copyBytes = checked(
            copyBytes
            + evidenceFiles.Sum(item =>
                {
                    var path =
                        Path.GetFullPath(item.Value);
                    var parent =
                        Path.GetDirectoryName(path)!;
                    return ShouldCopyEvidenceDirectory(
                            item.Key,
                            parent)
                        ? DirectoryBytes(parent)
                        : new FileInfo(path).Length;
                })
            + new FileInfo(dropBinaryPath).Length
            + new FileInfo(restoreToolPath).Length
            + new FileInfo(archiveToolPath).Length);
        var drive = DriveInfo.GetDrives()
            .Where(item => destinationRoot.StartsWith(
                item.RootDirectory.FullName,
                StringComparison.Ordinal))
            .OrderByDescending(item =>
                item.RootDirectory.FullName.Length)
            .FirstOrDefault()
            ?? throw new IOException(
                "Recovery bundle filesystem could not be resolved.");
        var capacityMeasuredAt =
            DateTimeOffset.UtcNow;
        var availableBeforeCopy =
            drive.AvailableFreeSpace;
        if (availableBeforeCopy <
            checked(required + reserveBytes + copyBytes))
        {
            throw new IOException(
                "Insufficient capacity for the pinned recovery bundle and restore reserve.");
        }
        Directory.CreateDirectory(destination);
        try
        {
            CopyDirectory(
                archivePackage,
                Path.Combine(destination, "archive"));
            if (!string.Equals(
                    rehearsalRoot,
                    sourceRoot,
                    StringComparison.Ordinal))
            {
                if (IsBelow(
                        destinationRoot,
                        rehearsalRoot)
                    || IsBelow(
                        rehearsalRoot,
                        destinationRoot))
                {
                    throw new InvalidOperationException(
                        "Recovery bundle and rehearsal archive cannot overlap.");
                }
                CopyDirectory(
                    rehearsalArchivePackage,
                    Path.Combine(
                        destination,
                        "rehearsal-archive"));
            }
            var evidenceDirectory =
                Path.Combine(destination, "evidence");
            Directory.CreateDirectory(evidenceDirectory);
            foreach (var item in evidenceFiles
                         .OrderBy(
                             static item => item.Key,
                             StringComparer.Ordinal))
            {
                var parent =
                    Path.GetDirectoryName(item.Value)!;
                if (ShouldCopyEvidenceDirectory(
                        item.Key,
                        parent))
                {
                    CopyDirectory(
                        parent,
                        Path.Combine(
                            evidenceDirectory,
                            item.Key));
                }
                else
                {
                    CopyFile(
                        item.Value,
                        Path.Combine(
                            evidenceDirectory,
                            $"{item.Key}.json"));
                }
            }
            CopyFile(
                dropBinaryPath,
                Path.Combine(
                    destination,
                    "drop-binary"));
            CopyFile(
                restoreToolPath,
                Path.Combine(
                    destination,
                    "restore-tool.py"));
            CopyFile(
                archiveToolPath,
                Path.Combine(
                    destination,
                    "postgres-snapshot-generation-archive.py"));

            var archivePath = Path.Combine(
                destination,
                "archive",
                "archive.custom");
            if (new FileInfo(archivePath).Length !=
                archiveBytes)
            {
                throw new IOException(
                    "Pinned archive size differs from source.");
            }

            var files = Directory
                .EnumerateFiles(
                    destination,
                    "*",
                    SearchOption.AllDirectories)
                .Select(path => new RecoveryBundleFile(
                    Path.GetRelativePath(
                            destination,
                            path)
                        .Replace(
                            Path.DirectorySeparatorChar,
                            '/'),
                    new FileInfo(path).Length,
                    Sha256File(path)))
                .OrderBy(
                    static item => item.Path,
                    StringComparer.Ordinal)
                .ToArray();
            var availableAfterCopy =
                new DriveInfo(drive.Name).AvailableFreeSpace;
            if (availableAfterCopy <
                checked(required + reserveBytes))
            {
                throw new IOException(
                    "Recovery bundle copy consumed the reserved restore capacity.");
            }
            var manifest = new RecoveryBundleManifest(
                SchemaVersion: 1,
                ToolId:
                    "fst.snapshot-generation-drop-recovery.v1",
                CreatedAtUtc: DateTimeOffset.UtcNow,
                FilesystemRoot:
                    drive.RootDirectory.FullName,
                CapacityMeasuredAtUtc:
                    capacityMeasuredAt,
                AvailableBeforeCopyBytes:
                    availableBeforeCopy,
                AvailableAfterCopyBytes:
                    availableAfterCopy,
                BundleCopyBytes: copyBytes,
                PhysicalBytes: physicalBytes,
                ArchiveBytes: archiveBytes,
                RequiredCapacityBytes: required,
                CapacityReserveBytes: reserveBytes,
                Files: files);
            var manifestPath = Path.Combine(
                destination,
                "bundle-manifest.json");
            WriteNewCanonical(manifestPath, manifest);
            var manifestSha = Sha256File(manifestPath);
            var checksumLines = Directory
                .EnumerateFiles(
                    destination,
                    "*",
                    SearchOption.AllDirectories)
                .Where(path =>
                    !string.Equals(
                        Path.GetFullPath(path),
                        Path.GetFullPath(
                            Path.Combine(
                                destination,
                                "SHA256SUMS")),
                        StringComparison.Ordinal))
                .Select(path =>
                    $"{Sha256File(path)}  "
                    + Path.GetRelativePath(destination, path)
                        .Replace(
                            Path.DirectorySeparatorChar,
                            '/'))
                .Order(StringComparer.Ordinal);
            WriteNewBytes(
                Path.Combine(destination, "SHA256SUMS"),
                Encoding.UTF8.GetBytes(
                    string.Join('\n', checksumLines)
                    + "\n"));
            SealBundle(destination);
            ValidateRecoveryBundle(destination);
            return manifestSha;
        }
        catch
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
            throw;
        }
    }

    public static RecoveryBundleManifest ValidateRecoveryBundle(
        string directory)
    {
        var manifestPath = Path.Combine(
            directory,
            "bundle-manifest.json");
        var checksumPath = Path.Combine(
            directory,
            "SHA256SUMS");
        if (!File.Exists(manifestPath)
            || !File.Exists(checksumPath))
        {
            throw new InvalidDataException(
                "Recovery bundle manifest or checksum file is missing.");
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     directory,
                     "*",
                     SearchOption.AllDirectories))
        {
            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            if (!string.IsNullOrEmpty(info.LinkTarget))
            {
                throw new InvalidDataException(
                    $"Recovery bundle contains a symbolic link: {path}");
            }
            if (!OperatingSystem.IsWindows()
                && info is FileInfo
                && (File.GetUnixFileMode(path)
                    & (UnixFileMode.UserWrite
                       | UnixFileMode.GroupWrite
                       | UnixFileMode.OtherWrite)) != 0)
            {
                throw new InvalidDataException(
                    $"Recovery bundle file is writable: {path}");
            }
        }
        var expected = File.ReadAllLines(checksumPath)
            .Select(line => line.Split(
                "  ",
                2,
                StringSplitOptions.None))
            .ToDictionary(
                parts => parts.Length == 2
                    ? parts[1]
                    : throw new InvalidDataException(
                        "Recovery bundle checksum line is invalid."),
                parts => parts[0],
                StringComparer.Ordinal);
        var observed = Directory
            .EnumerateFiles(
                directory,
                "*",
                SearchOption.AllDirectories)
            .Where(path =>
                !string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(checksumPath),
                    StringComparison.Ordinal))
            .ToDictionary(
                path => Path.GetRelativePath(directory, path)
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
                || !string.Equals(
                    digest,
                    item.Value,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Recovery bundle checksum inventory differs.");
        }
        var manifest =
            ReadStrict<RecoveryBundleManifest>(
                manifestPath);
        if (manifest.SchemaVersion != 1
            || manifest.ToolId !=
                "fst.snapshot-generation-drop-recovery.v1"
            || string.IsNullOrWhiteSpace(
                manifest.FilesystemRoot)
            || manifest.CapacityMeasuredAtUtc >
                manifest.CreatedAtUtc
            || manifest.AvailableBeforeCopyBytes <= 0
            || manifest.AvailableAfterCopyBytes <= 0
            || manifest.BundleCopyBytes <= 0
            || manifest.AvailableAfterCopyBytes <
                checked(
                    manifest.RequiredCapacityBytes
                    + manifest.CapacityReserveBytes)
            || manifest.RequiredCapacityBytes !=
                Math.Max(
                    checked(
                        2 * manifest.PhysicalBytes
                        + manifest.ArchiveBytes
                        + 1024L * 1024 * 1024),
                    2L * 1024 * 1024 * 1024)
            || manifest.Files.Count == 0
            || manifest.Files.Count !=
                observed.Count - 1
            || manifest.Files.Any(item =>
                !observed.TryGetValue(
                    item.Path,
                    out var digest)
                || digest != item.Sha256
                || new FileInfo(
                    Path.Combine(
                        directory,
                        item.Path))
                    .Length != item.Bytes))
        {
            throw new InvalidDataException(
                "Recovery bundle manifest is invalid.");
        }
        return manifest;
    }

    public static string Sha256File(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return Convert.ToHexString(
                SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    public static void WriteNewCanonical<T>(
        string path,
        T value)
    {
        var bytes = DropJson.Canonical(value);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    private static void RequireExecution(
        SnapshotGenerationQuarantinePlan plan,
        SnapshotGenerationQuarantineExecutionReport report,
        string action,
        string status)
    {
        if (report.OperationId != plan.OperationId
            || report.PlanDigest != plan.PlanDigest
            || report.Action != action
            || report.Status != status
            || report.Instrument != plan.Archive.Instrument
            || report.SnapshotId != plan.Archive.SnapshotId
            || report.ChildOid != plan.Archive.ChildOid
            || report.ChildRelfilenode !=
                plan.Archive.ChildRelfilenode
            || report.RowCount != plan.Archive.RowCount
            || report.RowFingerprintSha256 !=
                plan.Archive.RowFingerprintSha256)
        {
            throw new InvalidDataException(
                $"Quarantine {action} report does not match its sealed plan.");
        }
    }

    private static void RequireAttestation(
        SnapshotGenerationQuarantinePlan plan,
        SnapshotGenerationQuarantineAttestationReport report,
        string stage)
    {
        if (report.OperationId != plan.OperationId
            || report.PlanDigest != plan.PlanDigest
            || report.Stage != stage
            || report.Parity.RouteCount != 55
            || report.Parity.DifferenceCount != 0
            || !report.Parity.StatusParity
            || !report.Parity.SemanticJsonParity)
        {
            throw new InvalidDataException(
                $"Quarantine {stage} attestation does not match its sealed plan.");
        }
    }

    private static JsonElement RequireSingleRelation(
        IReadOnlyList<JsonElement> relations,
        long expectedOid,
        string expectedName)
    {
        var matches = relations.Where(relation =>
                ReadCanonicalCatalogInteger(
                    relation.GetProperty("oid"),
                    $"{expectedName}.oid")
                    == expectedOid
                && relation.GetProperty("name")
                    .GetString() == expectedName)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"Archive relation {expectedName}/{expectedOid} is not unique.");
    }

    private static SnapshotGenerationSemanticIndexEvidence
        BuildSemanticIndex(
            JsonElement index,
            IReadOnlyDictionary<long, JsonElement>
                rootIndexes,
            IReadOnlyDictionary<long, JsonElement>
                topIndexes)
    {
        var role = index.GetProperty("isPrimary")
            .GetBoolean()
            ? "pk"
            : "score";
        var columns = index.GetProperty("columnNames")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
        var expectedColumns = role == "pk"
            ? new[]
            {
                "snapshot_id",
                "song_id",
                "instrument",
                "account_id",
            }
            : new[]
            {
                "snapshot_id",
                "song_id",
                "instrument",
                "score",
            };
        var definition = NormalizeWhitespace(
            index.GetProperty("definition")
                .GetString()!);
        var expectedSuffix = role == "pk"
            ? "USING btree (snapshot_id, song_id, instrument, account_id)"
            : "USING btree (snapshot_id, song_id, instrument, score DESC)";
        var primary = index.GetProperty("isPrimary")
            .GetBoolean();
        var unique = index.GetProperty("isUnique")
            .GetBoolean();
        if (!columns.SequenceEqual(
                expectedColumns,
                StringComparer.Ordinal)
            || index.GetProperty("accessMethod")
                .GetString() != "btree"
            || index.GetProperty("tablespaceName")
                .GetString() != "pg_default"
            || !index.GetProperty("isValid")
                .GetBoolean()
            || !index.GetProperty("isReady")
                .GetBoolean()
            || primary != (role == "pk")
            || unique != (role == "pk")
            || !definition.EndsWith(
                expectedSuffix,
                StringComparison.Ordinal)
            || definition.Contains(
                " WHERE ",
                StringComparison.OrdinalIgnoreCase)
            || definition.Contains(
                " INCLUDE ",
                StringComparison.OrdinalIgnoreCase)
            || definition.Contains(
                " COLLATE ",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Archive {role} index shape is unsupported.");
        }
        var parentRootOid = index
            .GetProperty("parentIndexOid");
        var parentRootIndexOid =
            ReadCanonicalCatalogInteger(
                parentRootOid,
                $"{role}.parentIndexOid");
        if (!rootIndexes.TryGetValue(
                parentRootIndexOid,
                out var rootIndex)
            || !rootIndex.TryGetProperty(
                "parentIndexOid",
                out var parentTopProperty))
        {
            throw new InvalidDataException(
                $"Archive {role} index parent chain is missing.");
        }
        var parentTopOid =
            ReadCanonicalCatalogInteger(
                parentTopProperty,
                $"{role}.parentTopIndexOid");
        if (!topIndexes.TryGetValue(
                parentTopOid,
                out var topIndex))
        {
            throw new InvalidDataException(
                $"Archive {role} top index is missing.");
        }
        var topName = topIndex
            .GetProperty("indexName")
            .GetString();
        var topRole = topName switch
        {
            "leaderboard_entries_snapshot_pkey" =>
                "pk",
            "ix_les_snapshot_song_score" =>
                "score",
            _ => throw new InvalidDataException(
                $"Archive {role} top index is unsupported."),
        };
        if (topRole != role)
        {
            throw new InvalidDataException(
                $"Archive {role} index is attached to another top role.");
        }
        ValidateOptionalIndexMetadata(
            index,
            rootIndex,
            topIndex,
            role);
        var sortDirections = role == "pk"
            ? new[] { "asc", "asc", "asc", "asc" }
            : new[] { "asc", "asc", "asc", "desc" };
        var nullsOrder = role == "pk"
            ? new[] { "last", "last", "last", "last" }
            : new[] { "last", "last", "last", "first" };
        return new SnapshotGenerationSemanticIndexEvidence(
            Role: role,
            IndexOid:
                ReadCanonicalCatalogInteger(
                    index.GetProperty("indexOid"),
                    $"{role}.indexOid"),
            IndexRelfilenode:
                ReadCanonicalCatalogInteger(
                    index.GetProperty(
                        "indexRelfilenode"),
                    $"{role}.indexRelfilenode"),
            Primary: primary,
            Unique: unique,
            Valid: true,
            Ready: true,
            AccessMethod: "btree",
            TablespaceName: "pg_default",
            KeyColumns: columns,
            SortDirections: sortDirections,
            NullsOrder: nullsOrder,
            Opclasses: columns.Select(
                    _ => "default")
                .ToArray(),
            Collations: columns.Select(
                    _ => "default")
                .ToArray(),
            Expressions: null,
            Predicate: null,
            ParentRootIndexOid: parentRootIndexOid,
            ParentTopIndexOid: parentTopOid,
            ParentTopRole: topRole);
    }

    private static void ValidateOptionalIndexMetadata(
        JsonElement child,
        JsonElement root,
        JsonElement top,
        string role)
    {
        var expectedOptions = role == "pk"
            ? new[] { 0, 0, 0, 0 }
            : new[] { 0, 0, 0, 3 };
        // The archive/proof contract is pinned to PostgreSQL 17.
        var expectedOpclasses = role == "pk"
            ? new long[] { 3124, 3126, 3126, 3126 }
            : new long[] { 3124, 3126, 3126, 1978 };
        var expectedCollations = role == "pk"
            ? new long[] { 0, 100, 100, 100 }
            : new long[] { 0, 100, 100, 0 };
        foreach (var relation in new[]
                 {
                     child,
                     root,
                     top,
                 })
        {
            if (relation.TryGetProperty(
                    "indNKeyAtts",
                    out var keyCount)
                && (
                    keyCount.ValueKind !=
                        JsonValueKind.Number
                    || !keyCount.TryGetInt32(
                        out var keyCountValue)
                    || keyCountValue != 4))
            {
                throw new InvalidDataException(
                    $"Archive {role} index key count is unsupported.");
            }
            if (relation.TryGetProperty(
                    "indNAtts",
                    out var totalCount)
                && (
                    totalCount.ValueKind !=
                        JsonValueKind.Number
                    || !totalCount.TryGetInt32(
                        out var totalCountValue)
                    || totalCountValue != 4))
            {
                throw new InvalidDataException(
                    $"Archive {role} index INCLUDE shape is unsupported.");
            }
            if (relation.TryGetProperty(
                    "keyAttnums",
                    out var keyAttnums)
                && (keyAttnums.GetArrayLength() != 4
                    || keyAttnums
                        .EnumerateArray()
                        .Any(item =>
                            item.ValueKind !=
                                JsonValueKind.Number
                            || !item.TryGetInt32(
                                out var keyAttribute)
                            || keyAttribute <= 0)))
            {
                throw new InvalidDataException(
                    $"Archive {role} index key attributes are unsupported.");
            }
            if (relation.TryGetProperty(
                    "opclassOids",
                    out var opclasses)
                && !opclasses
                    .EnumerateArray()
                    .Select((item, index) =>
                        ReadCanonicalCatalogInteger(
                            item,
                            $"{role}.opclassOids[{index}]"))
                    .SequenceEqual(expectedOpclasses))
            {
                throw new InvalidDataException(
                    $"Archive {role} index opclasses are unsupported.");
            }
            if (relation.TryGetProperty(
                    "collationOids",
                    out var collations)
                && !collations
                    .EnumerateArray()
                    .Select((item, index) =>
                        ReadCanonicalCatalogInteger(
                            item,
                            $"{role}.collationOids[{index}]",
                            allowZero: true))
                    .SequenceEqual(expectedCollations))
            {
                throw new InvalidDataException(
                    $"Archive {role} index collations are unsupported.");
            }
            if (relation.TryGetProperty(
                    "indOptions",
                    out var options)
                && !options
                    .EnumerateArray()
                    .Select(item =>
                        item.ValueKind ==
                            JsonValueKind.Number
                        && item.TryGetInt32(
                            out var option)
                            ? option
                            : int.MinValue)
                    .SequenceEqual(expectedOptions))
            {
                throw new InvalidDataException(
                    $"Archive {role} index ordering options are unsupported.");
            }
            if (HasNonemptyOptionalText(
                    relation,
                    "expressions")
                || HasNonemptyOptionalText(
                    relation,
                    "predicate")
                || (
                    relation.TryGetProperty(
                        "relationOptions",
                        out var relationOptions)
                    && relationOptions.GetArrayLength() != 0))
            {
                throw new InvalidDataException(
                    $"Archive {role} index expression, predicate, or relation options are unsupported.");
            }
        }
        foreach (var propertyName in new[]
                 {
                     "indNKeyAtts",
                     "indNAtts",
                     "keyAttnums",
                     "indOptions",
                 })
        {
            var present = new[]
            {
                child.TryGetProperty(
                    propertyName,
                    out var childValue),
                root.TryGetProperty(
                    propertyName,
                    out var rootValue),
                top.TryGetProperty(
                    propertyName,
                    out var topValue),
            };
            if (present.All(value => !value))
                continue;
            if (!present.All(value => value)
                || childValue.GetRawText() !=
                    rootValue.GetRawText()
                || childValue.GetRawText() !=
                    topValue.GetRawText())
            {
                throw new InvalidDataException(
                    $"Archive {role} index {propertyName} differs in its attachment chain.");
            }
        }
        foreach (var (propertyName, allowZero) in new[]
                 {
                     ("opclassOids", false),
                     ("collationOids", true),
                 })
        {
            var present = new[]
            {
                child.TryGetProperty(
                    propertyName,
                    out var childValue),
                root.TryGetProperty(
                    propertyName,
                    out var rootValue),
                top.TryGetProperty(
                    propertyName,
                    out var topValue),
            };
            if (present.All(value => !value))
                continue;
            if (!present.All(value => value))
            {
                throw new InvalidDataException(
                    $"Archive {role} index {propertyName} is incomplete in its attachment chain.");
            }
            var childValues = childValue
                .EnumerateArray()
                .Select((item, index) =>
                    ReadCanonicalCatalogInteger(
                        item,
                        $"{role}.{propertyName}[{index}]",
                        allowZero))
                .ToArray();
            var rootValues = rootValue
                .EnumerateArray()
                .Select((item, index) =>
                    ReadCanonicalCatalogInteger(
                        item,
                        $"{role}.{propertyName}[{index}]",
                        allowZero))
                .ToArray();
            var topValues = topValue
                .EnumerateArray()
                .Select((item, index) =>
                    ReadCanonicalCatalogInteger(
                        item,
                        $"{role}.{propertyName}[{index}]",
                        allowZero))
                .ToArray();
            if (!childValues.SequenceEqual(rootValues)
                || !childValues.SequenceEqual(topValues))
            {
                throw new InvalidDataException(
                    $"Archive {role} index {propertyName} differs in its attachment chain.");
            }
        }
    }

    private static long ReadCanonicalCatalogInteger(
        JsonElement value,
        string fieldName,
        bool allowZero = false)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.Number =>
                value.GetRawText(),
            JsonValueKind.String =>
                value.GetString(),
            _ => null,
        };
        if (string.IsNullOrEmpty(text)
            || (
                text.Length > 1
                && text[0] == '0')
            || text.Any(character =>
                character is < '0' or > '9')
            || !ulong.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed)
            || parsed > uint.MaxValue
            || (!allowZero && parsed == 0))
        {
            throw new InvalidDataException(
                $"Archive catalog field {fieldName} is not a canonical {(allowZero ? "unsigned" : "positive")} decimal integer.");
        }
        return (long)parsed;
    }

    private static bool HasNonemptyOptionalText(
        JsonElement value,
        string propertyName) =>
        value.TryGetProperty(
            propertyName,
            out var property)
        && property.ValueKind is not (
            JsonValueKind.Null
            or JsonValueKind.Undefined)
        && !string.IsNullOrWhiteSpace(
            property.GetString());

    private static string NormalizeWhitespace(
        string value) =>
        string.Join(
            ' ',
            value.Split(
                (char[]?)null,
                StringSplitOptions
                    .RemoveEmptyEntries));

    private static void CopyDirectory(
        string source,
        string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            var info = new DirectoryInfo(directory);
            if (!string.IsNullOrEmpty(info.LinkTarget))
            {
                throw new InvalidDataException(
                    $"Recovery input contains a symbolic link: {directory}");
            }
            Directory.CreateDirectory(
                Path.Combine(
                    destination,
                    Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            CopyFile(
                file,
                Path.Combine(
                    destination,
                    Path.GetRelativePath(source, file)));
        }
    }

    private static bool ShouldCopyEvidenceDirectory(
        string key,
        string parent) =>
        key.EndsWith(
            "-proof",
            StringComparison.Ordinal)
        || Directory.Exists(
            Path.Combine(parent, "raw"))
        || File.Exists(
            Path.Combine(parent, "summary.json"));

    private static void CopyFile(
        string source,
        string destination)
    {
        var info = new FileInfo(source);
        if (!info.Exists || !string.IsNullOrEmpty(info.LinkTarget))
        {
            throw new InvalidDataException(
                $"Recovery input is missing or symbolic: {source}");
        }
        Directory.CreateDirectory(
            Path.GetDirectoryName(destination)!);
        using (var input = new FileStream(
                   source,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        using (var output = new FileStream(
                   destination,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   1024 * 1024,
                   FileOptions.WriteThrough))
        {
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        if (Sha256File(source) != Sha256File(destination))
        {
            throw new IOException(
                $"Recovery copy checksum differs: {source}");
        }
    }

    private static void WriteNewBytes(
        string path,
        byte[] bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static bool IsBelow(
        string path,
        string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "."
            || (!Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal));
    }

    private static long DirectoryBytes(string path) =>
        Directory.EnumerateFiles(
                path,
                "*",
                SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);

    private static void SealBundle(string directory)
    {
        if (OperatingSystem.IsWindows())
            return;
        foreach (var file in Directory.EnumerateFiles(
                     directory,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(
                file,
                UnixFileMode.UserRead
                | UnixFileMode.GroupRead);
        }
    }
}

public sealed record RecoveryBundleManifest(
    int SchemaVersion,
    string ToolId,
    DateTimeOffset CreatedAtUtc,
    string FilesystemRoot,
    DateTimeOffset CapacityMeasuredAtUtc,
    long AvailableBeforeCopyBytes,
    long AvailableAfterCopyBytes,
    long BundleCopyBytes,
    long PhysicalBytes,
    long ArchiveBytes,
    long RequiredCapacityBytes,
    long CapacityReserveBytes,
    IReadOnlyList<RecoveryBundleFile> Files);

public sealed record RecoveryBundleFile(
    string Path,
    long Bytes,
    string Sha256);
