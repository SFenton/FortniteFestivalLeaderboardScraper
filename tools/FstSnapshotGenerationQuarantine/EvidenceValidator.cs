using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FstSnapshotGenerationQuarantine;

public sealed class QuarantineEvidencePaths
{
    public const string RequiredEvidenceBase =
        "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence";
    public const string EnvironmentName =
        "FST_SNAPSHOT_QUARANTINE_EVIDENCE_ROOT";

    private readonly string _root;

    public QuarantineEvidencePaths(string configuredRoot)
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

    public static QuarantineEvidencePaths FromEnvironment() =>
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

    public string ResolveNewOutputFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            throw new IOException(
                $"Evidence output already exists: {fullPath}");
        }

        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                $"Evidence output has no parent: {fullPath}");
        var resolvedParent = ResolveExistingDirectory(parent);
        EnsureUnder(resolvedParent, _root, "Evidence output");
        RejectSymbolicLinkComponents(fullPath, allowMissingLeaf: true);
        return Path.Combine(
            resolvedParent,
            Path.GetFileName(fullPath));
    }

    private static string ResolveExistingFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(
                "Evidence file was not found.",
                fullPath);
        RejectSymbolicLinkComponents(fullPath, allowMissingLeaf: false);
        return fullPath;
    }

    private static string ResolveExistingDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException(fullPath);
        RejectSymbolicLinkComponents(fullPath, allowMissingLeaf: false);
        return fullPath.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static void RejectSymbolicLinkComponents(
        string path,
        bool allowMissingLeaf)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException(
                $"Path has no filesystem root: {path}");
        var current = root;
        var segments = fullPath[root.Length..].Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!File.Exists(current)
                && !Directory.Exists(current))
            {
                if (allowMissingLeaf
                    && index == segments.Length - 1)
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

public static class QuarantineEvidenceValidator
{
    public const string RouteParityAlgorithmId =
        "fst.route-parity.canonical-zip.v1";

    private static readonly Regex ChecksumLine = new(
        @"^(?<hash>[0-9a-f]{64})  (?<path>[A-Za-z0-9][A-Za-z0-9._/-]*)$",
        RegexOptions.CultureInvariant
        | RegexOptions.Compiled);
    private static readonly Regex ExportTimestampSuffix =
        new(
            @"-\d{8}-\d{6}(?=\.xlsx$)",
            RegexOptions.CultureInvariant
            | RegexOptions.Compiled
            | RegexOptions.IgnoreCase);
    private static readonly Regex OfficeCorePropertyPath =
        new(
            @"^package/services/metadata/core-properties/[0-9a-f]+\.psmdcp$",
            RegexOptions.CultureInvariant
            | RegexOptions.Compiled
            | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> VolatileRouteKeys =
        new(
            [
                "generatedAt",
                "lastUpdated",
            ],
            StringComparer.Ordinal);

    public static ArchivePackageEvidence ValidateArchivePackage(
        string packagePath,
        string proofManifestPath)
    {
        var package = Path.GetFullPath(packagePath);
        var proofManifest = Path.GetFullPath(proofManifestPath);
        if (!Directory.Exists(package))
            throw new DirectoryNotFoundException(package);
        if (!File.Exists(proofManifest))
            throw new FileNotFoundException(
                "Archive proof manifest was not found.",
                proofManifest);
        EnsurePathBelow(
            proofManifest,
            Path.Combine(package, "proofs"),
            "Archive proof manifest");

        var packageChecksums = ValidateChecksumFile(
            package,
            Path.Combine(package, "SHA256SUMS"),
            [
                "archive.custom",
                "archive.toc",
                "catalog.json",
                "manifest.json",
            ]);
        var manifestPath = Path.Combine(package, "manifest.json");
        using var manifest = JsonDocument.Parse(
            File.ReadAllBytes(manifestPath));
        var root = manifest.RootElement;
        RequireString(root, "toolId",
            "fst.snapshot-generation-archive-only.v1");
        RequireInt32(root, "schemaVersion", 1);
        RequireBoolean(root, "archiveOnly", true);
        RequireString(root, "status", "accepted");

        var archive = RequireObject(root, "archive");
        var archiveSha = RequireSha256(archive, "sha256");
        if (!string.Equals(
                archiveSha,
                packageChecksums["archive.custom"],
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Archive digest differs from SHA256SUMS.");
        }
        var catalog = RequireObject(root, "catalog");
        var catalogSha = RequireSha256(
            catalog,
            "sha256");
        if (!string.Equals(
                catalogSha,
                packageChecksums["catalog.json"],
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Archive manifest catalog digest differs from catalog.json.");
        }

        var cycle = RequireObject(root, "cycle");
        RequireInt32(cycle, "plannerVersion", 3);
        RequireInt32(cycle, "configVersion", 1);
        RequireString(cycle, "status", "observed");
        RequireBoolean(cycle, "reportOnly", true);
        RequireBoolean(cycle, "oracleAgreement", true);
        RequireInt32(cycle, "blockedCount", 0);
        if (RequireInt32(cycle, "candidateCount") <= 0)
        {
            throw new InvalidDataException(
                "Archive cycle has no candidates.");
        }

        var target = RequireObject(root, "target");
        var rowFingerprint = RequireObject(
            root,
            "rowFingerprint");
        var sourceFenceBefore = RequireObject(
            root,
            "sourceFenceBefore");
        var sourceFenceAfter = RequireObject(
            root,
            "sourceFenceAfter");
        foreach (var field in new[]
                 {
                     "catalogPhysicalSha256",
                     "logicalCatalogSha256",
                     "preflightSha256",
                     "targetOid",
                     "targetRelfilenode",
                 })
        {
            RequireString(
                sourceFenceAfter,
                field,
                RequireString(sourceFenceBefore, field));
        }
        foreach (var field in new[]
                 {
                     "heapBytes",
                     "indexBytes",
                     "totalBytes",
                 })
        {
            RequireInt64(
                sourceFenceAfter,
                field,
                RequireInt64(sourceFenceBefore, field));
        }
        RequireEquivalentJson(
            RequireObject(sourceFenceBefore, "mutationCounters"),
            RequireObject(sourceFenceAfter, "mutationCounters"),
            "Archive source mutation counters differ.");
        RequireEquivalentJson(
            RequireObject(sourceFenceBefore, "rowFingerprint"),
            RequireObject(sourceFenceAfter, "rowFingerprint"),
            "Archive source row fingerprints differ.");

        var sourceIdentity = RequireObject(
            root,
            "sourceIdentity");
        var sourceDatabase = RequireObject(
            sourceIdentity,
            "database");
        var packageManifestSha =
            packageChecksums["manifest.json"];

        var proofDirectory = Path.GetDirectoryName(
            proofManifest)
            ?? throw new InvalidDataException(
                "Proof manifest has no parent.");
        var proofChecksums = ValidateChecksumFile(
            proofDirectory,
            Path.Combine(proofDirectory, "SHA256SUMS"),
            [
                "cleanup.json",
                "container-evidence.json",
                "proof-manifest.json",
                "restored-catalog.json",
            ]);
        if (!string.Equals(
                proofChecksums["proof-manifest.json"],
                Sha256File(proofManifest),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Proof manifest checksum differs from SHA256SUMS.");
        }

        using var proof = JsonDocument.Parse(
            File.ReadAllBytes(proofManifest));
        var proofRoot = proof.RootElement;
        RequireString(proofRoot, "toolId",
            "fst.snapshot-generation-archive-only.v1");
        RequireInt32(proofRoot, "schemaVersion", 1);
        RequireString(proofRoot, "status", "accepted");
        RequireBoolean(proofRoot, "archiveOnly", true);
        RequireString(proofRoot, "networkMode", "none");
        RequireInt32(proofRoot, "publishedPorts", 0);
        RequireString(
            proofRoot,
            "packageManifestSha256",
            packageManifestSha);
        RequireString(
            proofRoot,
            "archiveSha256",
            archiveSha);
        var cleanup = RequireObject(proofRoot, "cleanup");
        foreach (var property in new[]
                 {
                     "containerAbsenceProven",
                     "containerRemoved",
                     "ownedVolumesRemoved",
                     "pgdataRemoved",
                     "scratchRemoved",
                 })
        {
            RequireBoolean(cleanup, property, true);
        }
        var validation = RequireObject(
            proofRoot,
            "validation");
        var proofFingerprint = RequireObject(
            validation,
            "rowFingerprint");
        RequireString(
            proofFingerprint,
            "sha256",
            RequireSha256(rowFingerprint, "sha256"));
        RequireInt64(
            proofFingerprint,
            "rowCount",
            RequireInt64(rowFingerprint, "rowCount"));
        var expectedLogical = RequireSha256(
            validation,
            "expectedLogicalCatalogSha256");
        RequireString(
            validation,
            "restoredLogicalCatalogSha256",
            expectedLogical);

        return new ArchivePackageEvidence(
            PackagePath: package,
            PackageManifestSha256: packageManifestSha,
            ArchiveSha256: archiveSha,
            ProofManifestPath: proofManifest,
            ProofManifestSha256:
                proofChecksums["proof-manifest.json"],
            CycleId: RequireInt64(cycle, "cycleId"),
            TriggerScrapeId:
                RequireInt64(cycle, "triggerScrapeId"),
            TriggerPublicationId:
                RequireInt64(cycle, "triggerPublicationId"),
            CandidateIdentityHash:
                RequireSha256(
                    cycle,
                    "candidateIdentityHash"),
            ObservationHash:
                RequireSha256(cycle, "observationHash"),
            ObservationId:
                RequireInt64(target, "observationId"),
            Instrument: RequireString(target, "instrument"),
            SnapshotId: RequireInt64(target, "snapshotId"),
            RootSchema: RequireString(target, "rootSchema"),
            RootRelation:
                RequireString(target, "rootRelation"),
            RootOid: RequireInt64(target, "rootOid"),
            ChildSchema:
                RequireString(target, "childSchema"),
            ChildRelation:
                RequireString(target, "childRelation"),
            ChildOid: RequireInt64(target, "childOid"),
            ChildRelfilenode:
                RequireInt64(target, "childRelfilenode"),
            StableChildIdentityHash:
                RequireSha256(
                    target,
                    "stableChildIdentityHash"),
            StableConfigSchemaHash:
                RequireSha256(
                    target,
                    "stableConfigSchemaHash"),
            RowCount:
                RequireInt64(rowFingerprint, "rowCount"),
            RowFingerprintSha256:
                RequireSha256(rowFingerprint, "sha256"),
            LogicalCatalogSha256:
                RequireSha256(
                    sourceFenceAfter,
                    "logicalCatalogSha256"),
            TotalBytes:
                RequireInt64(sourceFenceAfter, "totalBytes"),
            DatabaseName:
                RequireString(sourceDatabase, "database"),
            DatabaseOid:
                ParseInt64StringOrNumber(
                    sourceDatabase,
                    "databaseOid"),
            SystemIdentifier:
                RequireString(
                    sourceDatabase,
                    "systemIdentifier"),
            ServerVersionNum:
                RequireInt32(
                    sourceDatabase,
                    "serverVersionNum"));
    }

    public static SourceScrapeEvidence ValidateSourceEvidence(
        string manifestPath)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var directory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new InvalidDataException(
                "Source evidence manifest has no parent.");
        using var manifest = JsonDocument.Parse(
            File.ReadAllBytes(fullManifestPath));
        var root = manifest.RootElement;
        var files = RequireArray(root, "files");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files.EnumerateArray())
        {
            var relative = RequireString(file, "path");
            ValidateRelativePath(relative);
            if (!seen.Add(relative))
            {
                throw new InvalidDataException(
                    $"Source evidence path is duplicated: {relative}");
            }
            var path = Path.GetFullPath(
                Path.Combine(directory, relative));
            EnsurePathBelow(
                path,
                directory,
                "Source evidence file");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "Source evidence file was not found.",
                    path);
            }
            RequireRegularFileWithoutSymlinks(
                path,
                directory);
            var expectedBytes = RequireInt64(file, "bytes");
            if (new FileInfo(path).Length != expectedBytes)
            {
                throw new InvalidDataException(
                    $"Source evidence size differs: {relative}");
            }
            var expectedHash = RequireSha256(file, "sha256");
            if (!string.Equals(
                    expectedHash,
                    Sha256File(path),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Source evidence checksum differs: {relative}");
            }
        }

        if (!seen.Contains("summary.json"))
        {
            throw new InvalidDataException(
                "Source evidence manifest omits summary.json.");
        }
        var requiredEvidenceFiles = new[]
        {
            "summary.json",
            "scrape-publication.csv",
            "scope-summary.csv",
            "scope-fingerprints.csv",
            "scope-manifests.csv",
            "published-scope-sources.csv",
            "phase-outcomes.csv",
            "phase-timings.csv",
            "writer-failures.csv",
        };
        var missingFiles = requiredEvidenceFiles
            .Where(required => !seen.Contains(required))
            .ToArray();
        if (missingFiles.Length > 0)
        {
            throw new InvalidDataException(
                "Source evidence manifest omits required files: "
                + string.Join(", ", missingFiles));
        }
        using var summary = JsonDocument.Parse(
            File.ReadAllBytes(
                Path.Combine(directory, "summary.json")));
        var summaryRoot = summary.RootElement;
        var scrape = RequireObject(summaryRoot, "scrape");
        RequireString(scrape, "status", "completed");
        RequireInt32Flexible(
            scrape,
            "best_effort_failure_count",
            0);
        if (!string.IsNullOrEmpty(
                GetOptionalString(scrape, "failed_at"))
            || !string.IsNullOrEmpty(
                GetOptionalString(scrape, "failure_phase"))
            || !string.IsNullOrEmpty(
                GetOptionalString(scrape, "failure_message")))
        {
            throw new InvalidDataException(
                "Source scrape contains terminal failure evidence.");
        }

        var scopeTotals = RequireObject(
            summaryRoot,
            "scopeTotals");
        RequireInt64(scopeTotals, "missingPublishedScrapeId", 0);
        RequireInt64(scopeTotals, "missingReportedEntries", 0);
        RequireInt64(scopeTotals, "missingReportedPages", 0);
        RequireInt64(scopeTotals, "incompleteScopes", 0);
        RequireBoolean(
            scopeTotals,
            "fingerprintSnapshotCompleteForTarget",
            true);
        var scopes = RequireInt64(scopeTotals, "scopes");
        RequireInt64(
            scopeTotals,
            "targetSourceScopes",
            scopes);
        RequireInt64(
            scopeTotals,
            "targetSeenScopes",
            scopes);

        var publishedSources = RequireObject(
            summaryRoot,
            "publishedSources");
        RequireInt64(
            publishedSources,
            "incompleteScopes",
            0);
        RequireInt64(
            publishedSources,
            "scopes",
            scopes);
        var publishedRows = RequireInt64(
            publishedSources,
            "rows");
        if (publishedRows <= 0)
        {
            throw new InvalidDataException(
                "Source evidence has no published rows.");
        }
        RequireInt64(
            scopeTotals,
            "entries",
            publishedRows);

        var writerFailures = RequireObject(
            summaryRoot,
            "writerFailures");
        RequireInt64(writerFailures, "scopes", 0);
        RequireInt64(writerFailures, "pages", 0);
        RequireInt64(writerFailures, "rows", 0);
        var phaseOutcomes = RequireObject(
            summaryRoot,
            "phaseOutcomes");
        if (RequireInt64(phaseOutcomes, "phases") <= 0)
        {
            throw new InvalidDataException(
                "Source evidence contains no terminal phase outcomes.");
        }
        RequireInt64(
            phaseOutcomes,
            "criticalFailures",
            0);
        RequireInt64(
            phaseOutcomes,
            "bestEffortFailures",
            0);
        var phaseTimings = RequireObject(
            summaryRoot,
            "phaseTimings");
        RequireInt64(phaseTimings, "failed", 0);
        var scopeManifests = RequireObject(
            summaryRoot,
            "scopeManifests");
        RequireInt64(scopeManifests, "incomplete", 0);
        RequireInt64(scopeManifests, "parseFailures", 0);
        RequireInt64(scopeManifests, "retryExhausted", 0);

        var scrapeId = ParseInt64StringOrNumber(scrape, "id");
        var publishedScrapeId =
            ParseInt64StringOrNumber(
                scrape,
                "published_scrape_id");
        if (scrapeId != publishedScrapeId)
        {
            throw new InvalidDataException(
                "Source evidence scrape is not its published scrape.");
        }
        var songCount = RequireInt32Flexible(
            scrape,
            "songs_scraped");
        if (songCount <= 0
            || scopes != checked((long)songCount * 9))
        {
            throw new InvalidDataException(
                "Source evidence does not contain all nine instrument scopes per song.");
        }

        return new SourceScrapeEvidence(
            ManifestPath: fullManifestPath,
            ManifestSha256:
                Sha256File(fullManifestPath),
            ScrapeId: scrapeId,
            PublishedScrapeId: publishedScrapeId,
            SongCount: songCount,
            TotalEntries:
                ParseInt64StringOrNumber(
                    scrape,
                    "total_entries"),
            ScopeCount: scopes,
            PublishedScopeCount:
                RequireInt64(publishedSources, "scopes"),
            PublishedRowCount: publishedRows);
    }

    public static RouteParityEvidence ValidateRouteParity(
        string baselineManifestPath,
        string candidateManifestPath) =>
        ValidateDetailedRouteParity(
            baselineManifestPath,
            candidateManifestPath).Parity;

    public static DetailedRouteParityEvidence
        ValidateDetailedRouteParity(
            string baselineManifestPath,
            string candidateManifestPath)
    {
        var baselinePath = Path.GetFullPath(
            baselineManifestPath);
        var candidatePath = Path.GetFullPath(
            candidateManifestPath);
        using var baseline = JsonDocument.Parse(
            File.ReadAllBytes(baselinePath));
        using var candidate = JsonDocument.Parse(
            File.ReadAllBytes(candidatePath));
        var left = ReadRouteCapture(
            baseline.RootElement,
            baselinePath);
        var right = ReadRouteCapture(
            candidate.RootElement,
            candidatePath);

        if (left.PublicationId != right.PublicationId
            || left.PublishedScrapeId !=
                right.PublishedScrapeId)
        {
            throw new InvalidDataException(
                "Route captures use different publications.");
        }
        if (left.Entries.Count != 55
            || right.Entries.Count != 55
            || !left.Entries.Keys.ToHashSet(
                    StringComparer.Ordinal)
                .SetEquals(right.Entries.Keys))
        {
            throw new InvalidDataException(
                "Route captures do not contain the same 55-route contract.");
        }

        var differences = new List<string>();
        var semanticEvidence =
            new List<RouteSemanticComparisonEvidence>();
        foreach (var name in left.Entries.Keys
                     .Order(StringComparer.Ordinal))
        {
            var baselineEntry = left.Entries[name];
            var candidateEntry = right.Entries[name];
            var baselineDirectory =
                Path.GetDirectoryName(baselinePath)!;
            var candidateDirectory =
                Path.GetDirectoryName(candidatePath)!;
            var baselineBody = LoadRouteBody(
                baselineDirectory,
                name,
                baselineEntry);
            var candidateBody = LoadRouteBody(
                candidateDirectory,
                name,
                candidateEntry);
            if (baselineEntry.CurlExit != 0
                || candidateEntry.CurlExit != 0)
            {
                differences.Add($"{name}:curl");
            }
            if (!string.Equals(
                    baselineEntry.Method,
                    candidateEntry.Method,
                    StringComparison.Ordinal)
                || !string.Equals(
                    baselineEntry.Path,
                    candidateEntry.Path,
                    StringComparison.Ordinal)
                || baselineEntry.Status !=
                    candidateEntry.Status
                || !string.Equals(
                    baselineEntry.ContentType,
                    candidateEntry.ContentType,
                    StringComparison.OrdinalIgnoreCase))
            {
                differences.Add($"{name}:contract");
            }
            if (baselineEntry.Status is >= 200 and < 300
                && (baselineBody.Length == 0
                    || candidateBody.Length == 0))
            {
                differences.Add($"{name}:empty-success-body");
            }
            if (baselineEntry.IsJson !=
                candidateEntry.IsJson)
            {
                differences.Add($"{name}:content-kind");
                continue;
            }
            if (!baselineEntry.IsJson)
            {
                var baselineSemantic =
                    NonJsonSemanticHash(
                        name,
                        baselineEntry.ContentType,
                        baselineBody);
                var candidateSemantic =
                    NonJsonSemanticHash(
                        name,
                        candidateEntry.ContentType,
                        candidateBody);
                if (!string.Equals(
                        baselineSemantic,
                        candidateSemantic,
                        StringComparison.Ordinal))
                {
                    differences.Add(
                        $"{name}:semantic-binary");
                }
                semanticEvidence.Add(
                    new RouteSemanticComparisonEvidence(
                        name,
                        UsesCanonicalZip(
                            name,
                            baselineEntry.ContentType)
                            ? "zip-canonical"
                            : "raw",
                        baselineBody.LongLength,
                        candidateBody.LongLength,
                        baselineEntry.RawSha256,
                        candidateEntry.RawSha256,
                        baselineSemantic,
                        candidateSemantic));
                continue;
            }

            var baselineJson = NormalizedRouteJson(
                baselineDirectory,
                name,
                baselineEntry.SemanticSha256,
                baselineBody);
            var candidateJson = NormalizedRouteJson(
                candidateDirectory,
                name,
                candidateEntry.SemanticSha256,
                candidateBody);
            if (!baselineJson.AsSpan().SequenceEqual(
                    candidateJson))
            {
                differences.Add($"{name}:semantic-json");
            }
            semanticEvidence.Add(
                new RouteSemanticComparisonEvidence(
                    name,
                    "json-normalized",
                    baselineBody.LongLength,
                    candidateBody.LongLength,
                    baselineEntry.RawSha256,
                    candidateEntry.RawSha256,
                    Sha256Bytes(baselineJson),
                    Sha256Bytes(candidateJson)));
        }

        if (differences.Count > 0)
        {
            throw new InvalidDataException(
                "Route parity differs: "
                + string.Join(", ", differences));
        }

        var parity = new RouteParityEvidence(
            BaselineManifestPath: baselinePath,
            BaselineManifestSha256:
                Sha256File(baselinePath),
            CandidateManifestPath: candidatePath,
            CandidateManifestSha256:
                Sha256File(candidatePath),
            PublicationId: left.PublicationId,
            PublishedScrapeId:
                left.PublishedScrapeId,
            RouteCount: 55,
            StatusParity: true,
            SemanticJsonParity: true,
            DifferenceCount: 0);
        return new DetailedRouteParityEvidence(
            parity,
            RouteParityAlgorithmId,
            true,
            QuarantineJson.Sha256(semanticEvidence),
            semanticEvidence);
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

    private static IReadOnlyDictionary<string, string>
        ValidateChecksumFile(
            string directory,
            string checksumPath,
            IReadOnlyCollection<string> requiredNames)
    {
        if (!File.Exists(checksumPath))
        {
            throw new FileNotFoundException(
                "SHA256SUMS was not found.",
                checksumPath);
        }

        var checksums = new Dictionary<string, string>(
            StringComparer.Ordinal);
        foreach (var line in File.ReadLines(checksumPath))
        {
            var match = ChecksumLine.Match(line);
            if (!match.Success)
            {
                throw new InvalidDataException(
                    $"Invalid checksum line: {line}");
            }
            var relative = match.Groups["path"].Value;
            ValidateRelativePath(relative);
            if (!checksums.TryAdd(
                    relative,
                    match.Groups["hash"].Value))
            {
                throw new InvalidDataException(
                    $"Duplicate checksum path: {relative}");
            }
        }

        if (!checksums.Keys.ToHashSet(
                StringComparer.Ordinal)
            .SetEquals(requiredNames))
        {
            throw new InvalidDataException(
                "Checksum inventory differs from the required package files.");
        }

        foreach (var (relative, expected) in checksums)
        {
            var path = Path.GetFullPath(
                Path.Combine(directory, relative));
            EnsurePathBelow(
                path,
                directory,
                "Checksummed file");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "Checksummed file was not found.",
                    path);
            }
            RequireRegularFileWithoutSymlinks(
                path,
                directory);
            if (!string.Equals(
                    expected,
                    Sha256File(path),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Checksum mismatch: {relative}");
            }
        }

        return checksums;
    }

    private static RouteCapture ReadRouteCapture(
        JsonElement root,
        string path)
    {
        var publicationId = RequireInt64(
            root,
            "publicationId");
        var publishedScrapeId = RequireInt64(
            root,
            "publishedScrapeId");
        RequireInt32(root, "routeCount", 55);
        var entries = new Dictionary<string, RouteEntry>(
            StringComparer.Ordinal);
        foreach (var entry in RequireArray(
                     root,
                     "entries").EnumerateArray())
        {
            var name = RequireString(entry, "name");
            if (!entries.TryAdd(
                    name,
                    new RouteEntry(
                        RequireString(entry, "method"),
                        RequireString(entry, "path"),
                        RequireInt32(entry, "status"),
                        RequireInt32(entry, "curlExit"),
                        RequireBoolean(entry, "isJson"),
                        GetOptionalString(
                            entry,
                            "semanticSha256"),
                        RequireSha256(entry, "rawSha256"),
                        RequireInt64(entry, "bytes"),
                        GetOptionalString(
                            entry,
                            "contentType") ?? "")))
            {
                throw new InvalidDataException(
                    $"Route capture has duplicate entry {name}: {path}");
            }
        }

        if (entries.Count != 55)
        {
            throw new InvalidDataException(
                $"Route capture does not have 55 unique entries: {path}");
        }
        return new RouteCapture(
            publicationId,
            publishedScrapeId,
            entries);
    }

    private static byte[] NormalizedRouteJson(
        string captureDirectory,
        string name,
        string? expectedSha256,
        byte[] rawBody)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)
            || expectedSha256.Length != 64)
        {
            throw new InvalidDataException(
                $"JSON route {name} has no semantic SHA-256.");
        }
        var path = Path.GetFullPath(
            Path.Combine(
                captureDirectory,
                "normalized",
                $"{name}.json"));
        EnsurePathBelow(
            path,
            captureDirectory,
            "Normalized route body");
        RequireRegularFileWithoutSymlinks(
            path,
            captureDirectory);
        if (!string.Equals(
                Sha256File(path),
                expectedSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Normalized route checksum differs: {name}");
        }
        using var document = JsonDocument.Parse(rawBody);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteNormalizedJson(
                writer,
                document.RootElement);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] LoadRouteBody(
        string captureDirectory,
        string name,
        RouteEntry entry)
    {
        var path = Path.GetFullPath(
            Path.Combine(
                captureDirectory,
                "raw",
                $"{name}.body"));
        EnsurePathBelow(
            path,
            captureDirectory,
            "Raw route body");
        RequireRegularFileWithoutSymlinks(
            path,
            captureDirectory);
        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength != entry.Bytes)
        {
            throw new InvalidDataException(
                $"Raw route size differs: {name}");
        }
        if (!string.Equals(
                Sha256Bytes(bytes),
                entry.RawSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Raw route checksum differs: {name}");
        }
        return bytes;
    }

    private static string NonJsonSemanticHash(
        string name,
        string contentType,
        byte[] body)
    {
        if (!UsesCanonicalZip(name, contentType))
        {
            return Sha256Bytes(body);
        }

        return CanonicalZipHash(body, depth: 0);
    }

    private static bool UsesCanonicalZip(
        string name,
        string contentType) =>
        name.EndsWith(
            "-export",
            StringComparison.Ordinal)
        || contentType.Contains(
            "zip",
            StringComparison.OrdinalIgnoreCase);

    private static string CanonicalZipHash(
        byte[] body,
        int depth)
    {
        if (depth > 3)
        {
            throw new InvalidDataException(
                "Nested export ZIP depth exceeds three.");
        }
        using var archive = new ZipArchive(
            new MemoryStream(body, writable: false),
            ZipArchiveMode.Read,
            leaveOpen: false);
        if (archive.Entries.Count is 0 or > 10_000)
        {
            throw new InvalidDataException(
                "Export ZIP entry count is invalid.");
        }
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        long expandedBytes = 0;
        var entries = archive.Entries
            .Where(entry =>
                !(depth > 0
                  && OfficeCorePropertyPath.IsMatch(
                      entry.FullName)))
            .Select(entry => new
            {
                Entry = entry,
                Name = depth == 0
                    ? ExportTimestampSuffix.Replace(
                        entry.FullName,
                        "")
                    : entry.FullName,
            })
            .OrderBy(
                item => item.Name,
                StringComparer.Ordinal)
            .ToArray();
        if (entries.Select(item => item.Name)
            .Distinct(StringComparer.Ordinal)
            .Count() != entries.Length)
        {
            throw new InvalidDataException(
                "Export ZIP names collide after volatile-name normalization.");
        }
        foreach (var item in entries)
        {
            var entry = item.Entry;
            expandedBytes = checked(
                expandedBytes + entry.Length);
            if (expandedBytes > 512L * 1024 * 1024)
            {
                throw new InvalidDataException(
                    "Export ZIP expanded content exceeds 512 MiB.");
            }
            hash.AppendData(
                Encoding.UTF8.GetBytes(item.Name));
            hash.AppendData([0]);
            using var stream = entry.Open();
            using var memory = new MemoryStream(
                checked((int)Math.Min(
                    entry.Length,
                    int.MaxValue)));
            stream.CopyTo(memory);
            var entryBytes = memory.ToArray();
            byte[] semanticBytes;
            if ((item.Name.EndsWith(
                        ".xlsx",
                        StringComparison.OrdinalIgnoreCase)
                    || item.Name.EndsWith(
                        ".zip",
                        StringComparison.OrdinalIgnoreCase))
                && LooksLikeZip(entryBytes))
            {
                semanticBytes = Encoding.ASCII.GetBytes(
                    CanonicalZipHash(
                        entryBytes,
                        depth + 1));
            }
            else if (depth > 0
                     && string.Equals(
                         item.Name,
                         "_rels/.rels",
                         StringComparison.Ordinal))
            {
                semanticBytes =
                    NormalizeOfficeRootRelationships(
                        entryBytes);
            }
            else
            {
                semanticBytes = entryBytes;
            }
            hash.AppendData(
                Encoding.UTF8.GetBytes(
                    semanticBytes.LongLength.ToString(
                        System.Globalization
                            .CultureInfo.InvariantCulture)));
            hash.AppendData([0]);
            hash.AppendData(semanticBytes);
            hash.AppendData([0]);
        }
        return Convert.ToHexString(
                hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static bool LooksLikeZip(
        ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4
        && bytes[0] == (byte)'P'
        && bytes[1] == (byte)'K'
        && bytes[2] is 3 or 5 or 7
        && bytes[3] is 4 or 6 or 8;

    private static byte[] NormalizeOfficeRootRelationships(
        byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes)
            .TrimStart('\uFEFF');
        var document = XDocument.Parse(
            text,
            LoadOptions.None);
        var root = document.Root
            ?? throw new InvalidDataException(
                "Office relationship XML has no root.");
        var rows = root.Elements()
            .Select(element => new
            {
                Type =
                    (string?)element.Attribute("Type")
                    ?? "",
                Target =
                    (string?)element.Attribute("Target")
                    ?? "",
                TargetMode =
                    (string?)element.Attribute("TargetMode")
                    ?? "",
            })
            .Where(row =>
                !row.Type.EndsWith(
                    "/metadata/core-properties",
                    StringComparison.Ordinal))
            .Select(row =>
                $"{row.Type}\u001f{row.Target}\u001f{row.TargetMode}")
            .Order(StringComparer.Ordinal);
        return Encoding.UTF8.GetBytes(
            string.Join("\n", rows));
    }

    private static string Sha256Bytes(
        ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();

    private static void WriteNormalizedJson(
        Utf8JsonWriter writer,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element
                             .EnumerateObject()
                             .Where(property =>
                                 !VolatileRouteKeys.Contains(
                                     property.Name))
                             .OrderBy(
                                 property => property.Name,
                                 StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteNormalizedJson(
                        writer,
                        property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteNormalizedJson(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(
                    element.GetRawText(),
                    skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException(
                    $"Unsupported route JSON kind {element.ValueKind}.");
        }
    }

    private static void RequireEquivalentJson(
        JsonElement left,
        JsonElement right,
        string message)
    {
        var leftBytes = JsonSerializer.SerializeToUtf8Bytes(left);
        var rightBytes =
            JsonSerializer.SerializeToUtf8Bytes(right);
        if (!leftBytes.AsSpan().SequenceEqual(rightBytes))
            throw new InvalidDataException(message);
    }

    private static void ValidateRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)
            || Path.IsPathRooted(relative)
            || relative.Contains('\\', StringComparison.Ordinal)
            || relative.Split('/').Any(segment =>
                segment is "" or "." or ".."))
        {
            throw new InvalidDataException(
                $"Unsafe evidence relative path: {relative}");
        }
    }

    private static void EnsurePathBelow(
        string path,
        string root,
        string label)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(
            normalizedRoot,
            normalizedPath);
        if (relative == ".")
            return;
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{label} escapes {normalizedRoot}: {normalizedPath}");
        }
    }

    private static void RequireRegularFileWithoutSymlinks(
        string path,
        string root)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        EnsurePathBelow(
            normalizedPath,
            normalizedRoot,
            "Evidence file");
        var relative = Path.GetRelativePath(
            normalizedRoot,
            normalizedPath);
        var current = normalizedRoot;
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (!string.IsNullOrEmpty(info.LinkTarget))
            {
                throw new InvalidDataException(
                    $"Evidence paths cannot contain symbolic links: {current}");
            }
        }
        if (!File.Exists(normalizedPath)
            || Directory.Exists(normalizedPath))
        {
            throw new InvalidDataException(
                $"Evidence path is not a regular file: {normalizedPath}");
        }
    }

    private static JsonElement RequireObject(
        JsonElement parent,
        string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Required JSON object is missing: {name}");
        }
        return value;
    }

    private static JsonElement RequireArray(
        JsonElement parent,
        string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Required JSON array is missing: {name}");
        }
        return value;
    }

    private static string RequireString(
        JsonElement parent,
        string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"Required JSON string is missing: {name}");
        }
        return value.GetString()!;
    }

    private static void RequireString(
        JsonElement parent,
        string name,
        string expected)
    {
        var actual = RequireString(parent, name);
        if (!string.Equals(
                actual,
                expected,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"JSON value {name} differs: {actual}");
        }
    }

    private static string? GetOptionalString(
        JsonElement parent,
        string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null
                or JsonValueKind.Undefined)
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
    }

    private static string RequireSha256(
        JsonElement parent,
        string name)
    {
        var value = RequireString(parent, name);
        if (value.Length != 64
            || value.Any(character =>
                character is not (
                    >= '0' and <= '9'
                    or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                $"JSON value {name} is not a lowercase SHA-256.");
        }
        return value;
    }

    private static bool RequireBoolean(
        JsonElement parent,
        string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind is not (
                JsonValueKind.True
                or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"Required JSON boolean is missing: {name}");
        }
        return value.GetBoolean();
    }

    private static void RequireBoolean(
        JsonElement parent,
        string name,
        bool expected)
    {
        if (RequireBoolean(parent, name) != expected)
        {
            throw new InvalidDataException(
                $"JSON boolean {name} differs.");
        }
    }

    private static int RequireInt32(
        JsonElement parent,
        string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException(
                $"Required JSON integer is missing: {name}");
        }
        return result;
    }

    private static void RequireInt32(
        JsonElement parent,
        string name,
        int expected)
    {
        if (RequireInt32(parent, name) != expected)
        {
            throw new InvalidDataException(
                $"JSON integer {name} differs.");
        }
    }

    private static int RequireInt32Flexible(
        JsonElement parent,
        string name)
    {
        var value = ParseInt64StringOrNumber(parent, name);
        return checked((int)value);
    }

    private static void RequireInt32Flexible(
        JsonElement parent,
        string name,
        int expected)
    {
        if (RequireInt32Flexible(parent, name) != expected)
        {
            throw new InvalidDataException(
                $"JSON integer {name} differs.");
        }
    }

    private static long RequireInt64(
        JsonElement parent,
        string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || !value.TryGetInt64(out var result))
        {
            throw new InvalidDataException(
                $"Required JSON long is missing: {name}");
        }
        return result;
    }

    private static void RequireInt64(
        JsonElement parent,
        string name,
        long expected)
    {
        if (RequireInt64(parent, name) != expected)
        {
            throw new InvalidDataException(
                $"JSON long {name} differs.");
        }
    }

    private static long ParseInt64StringOrNumber(
        JsonElement parent,
        string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw new InvalidDataException(
                $"Required JSON value is missing: {name}");
        }
        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number))
        {
            return number;
        }
        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out number))
        {
            return number;
        }
        throw new InvalidDataException(
            $"JSON value {name} is not an integer.");
    }

    private sealed record RouteCapture(
        long PublicationId,
        long PublishedScrapeId,
        IReadOnlyDictionary<string, RouteEntry> Entries);

    private sealed record RouteEntry(
        string Method,
        string Path,
        int Status,
        int CurlExit,
        bool IsJson,
        string? SemanticSha256,
        string RawSha256,
        long Bytes,
        string ContentType);
}
