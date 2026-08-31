using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FstSnapshotGenerationQuarantine;

namespace FSTService.Tests.Unit;

public sealed class SnapshotGenerationQuarantineToolTests
    : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"fst-quarantine-tool-{Guid.NewGuid():N}");

    public SnapshotGenerationQuarantineToolTests() =>
        Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ArchivePackageRequiresAuthenticChecksumsAndProof()
    {
        var package = CreateArchivePackage();
        var proof = Path.Combine(
            package,
            "proofs",
            "test-proof",
            "proof-manifest.json");

        var evidence =
            QuarantineEvidenceValidator.ValidateArchivePackage(
                package,
                proof);

        Assert.Equal(9, evidence.CycleId);
        Assert.Equal(
            "Solo_PeripheralCymbals",
            evidence.Instrument);
        Assert.Equal(1314, evidence.SnapshotId);
        Assert.Equal(8627, evidence.RowCount);

        File.AppendAllText(
            Path.Combine(package, "archive.custom"),
            "tampered");
        Assert.Throws<InvalidDataException>(
            () =>
                QuarantineEvidenceValidator
                    .ValidateArchivePackage(
                        package,
                        proof));
    }

    [Fact]
    public void ArchivePackageBindsManifestCatalogDigestToCatalogBytes()
    {
        var package = CreateArchivePackage();
        var proofDirectory = Path.Combine(
            package,
            "proofs",
            "test-proof");
        var proofPath = Path.Combine(
            proofDirectory,
            "proof-manifest.json");
        var manifestPath = Path.Combine(
            package,
            "manifest.json");
        var manifest = JsonNode.Parse(
            File.ReadAllText(manifestPath))!
            .AsObject();
        manifest["catalog"]!["sha256"] =
            new string('0', 64);
        File.WriteAllText(
            manifestPath,
            manifest.ToJsonString());
        WriteChecksumFile(
            package,
            [
                "archive.custom",
                "archive.toc",
                "catalog.json",
                "manifest.json",
            ]);
        var proof = JsonNode.Parse(
            File.ReadAllText(proofPath))!
            .AsObject();
        proof["packageManifestSha256"] =
            Sha256(manifestPath);
        File.WriteAllText(
            proofPath,
            proof.ToJsonString());
        WriteChecksumFile(
            proofDirectory,
            [
                "cleanup.json",
                "container-evidence.json",
                "proof-manifest.json",
                "restored-catalog.json",
            ]);

        var failure = Assert.Throws<InvalidDataException>(
            () => QuarantineEvidenceValidator
                .ValidateArchivePackage(
                    package,
                    proofPath));
        Assert.Contains(
            "catalog",
            failure.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourceEvidenceRequiresCompleteChecksummedScrape()
    {
        var directory = Path.Combine(_root, "source");
        Directory.CreateDirectory(directory);
        var summaryPath = Path.Combine(
            directory,
            "summary.json");
        WriteJson(
            summaryPath,
            new
            {
                scrape = new
                {
                    id = "1400",
                    status = "completed",
                    failed_at = "",
                    failure_phase = "",
                    failure_message = "",
                    best_effort_failure_count = "0",
                    published_scrape_id = "1400",
                    songs_scraped = "710",
                    total_entries = "123456",
                },
                scopeTotals = new
                {
                    scopes = 6390L,
                    entries = 123456L,
                    missingPublishedScrapeId = 0L,
                    missingReportedEntries = 0L,
                    missingReportedPages = 0L,
                    incompleteScopes = 0L,
                    targetSourceScopes = 6390L,
                    targetSeenScopes = 6390L,
                    fingerprintSnapshotCompleteForTarget = true,
                },
                publishedSources = new
                {
                    scopes = 6390L,
                    rows = 123456L,
                    incompleteScopes = 0L,
                },
                writerFailures = new
                {
                    scopes = 0L,
                    pages = 0L,
                    rows = 0L,
                },
                phaseOutcomes = new
                {
                    phases = 20L,
                    criticalFailures = 0L,
                    bestEffortFailures = 0L,
                },
                phaseTimings = new
                {
                    failed = 0L,
                },
                scopeManifests = new
                {
                    incomplete = 0L,
                    parseFailures = 0L,
                    retryExhausted = 0L,
                },
            });
        var requiredFiles = new[]
        {
            "scrape-publication.csv",
            "scope-summary.csv",
            "scope-fingerprints.csv",
            "scope-manifests.csv",
            "published-scope-sources.csv",
            "phase-outcomes.csv",
            "phase-timings.csv",
            "writer-failures.csv",
        };
        foreach (var name in requiredFiles)
        {
            File.WriteAllText(
                Path.Combine(directory, name),
                "header\n",
                new UTF8Encoding(false));
        }
        var manifestPath = Path.Combine(
            directory,
            "manifest.json");
        WriteJson(
            manifestPath,
            new
            {
                files = new[] { "summary.json" }
                    .Concat(requiredFiles)
                    .Select(name =>
                    {
                        var path = Path.Combine(
                            directory,
                            name);
                        return new
                        {
                            path = name,
                            bytes =
                                new FileInfo(path).Length,
                            sha256 = Sha256(path),
                        };
                    })
                    .ToArray(),
            });

        var evidence =
            QuarantineEvidenceValidator.ValidateSourceEvidence(
                manifestPath);

        Assert.Equal(1400, evidence.ScrapeId);
        Assert.Equal(6390, evidence.ScopeCount);

        File.AppendAllText(summaryPath, " ");
        Assert.Throws<InvalidDataException>(
            () =>
                QuarantineEvidenceValidator
                    .ValidateSourceEvidence(
                        manifestPath));
    }

    [Fact]
    public void RouteParityAllowsOnlyConfiguredVolatileJson()
    {
        var baseline = CreateRouteCapture(
            "baseline",
            generatedAt: "2026-08-30T00:00:00Z");
        var candidate = CreateRouteCapture(
            "candidate",
            generatedAt: "2026-08-30T00:01:00Z");

        var parity =
            QuarantineEvidenceValidator.ValidateRouteParity(
                baseline,
                candidate);

        Assert.Equal(55, parity.RouteCount);
        Assert.True(parity.StatusParity);
        Assert.True(parity.SemanticJsonParity);

        var changedPath = Path.Combine(
            Path.GetDirectoryName(candidate)!,
            "normalized",
            "route-17.json");
        WriteJson(
            changedPath,
            new
            {
                value = 999,
                generatedAt =
                    "2026-08-30T00:02:00Z",
            });
        Assert.Throws<InvalidDataException>(
            () =>
                QuarantineEvidenceValidator
                    .ValidateRouteParity(
                        baseline,
                        candidate));
    }

    [Fact]
    public void RouteParityComparesZipEntryContents()
    {
        var baseline = CreateRouteCapture(
            "zip-baseline",
            generatedAt: "2026-08-30T00:00:00Z");
        var candidate = CreateRouteCapture(
            "zip-candidate",
            generatedAt: "2026-08-30T00:00:00Z");
        ReplaceRouteWithZip(baseline, "same-content");
        ReplaceRouteWithZip(candidate, "same-content");

        var parity =
            QuarantineEvidenceValidator.ValidateRouteParity(
                    baseline,
                    candidate);

        Assert.Equal(0, parity.DifferenceCount);
        ReplaceRouteWithZip(candidate, "changed-content");
        Assert.Throws<InvalidDataException>(
            () =>
                    QuarantineEvidenceValidator
                        .ValidateRouteParity(
                            baseline,
                            candidate));
    }

    [Fact]
    public void ExecutorHasNoDockerOrDropCommandSurface()
    {
        var repository = FindRepositoryRoot();
        var directory = Path.Combine(
            repository,
            "tools",
            "FstSnapshotGenerationQuarantine");
        var sharedDirectory = Path.Combine(
            repository,
            "tools",
            "FstSnapshotGenerationEvidence");
        var source = string.Join(
            "\n",
            new[]
                {
                    directory,
                    sharedDirectory,
                }
                .SelectMany(path =>
                    Directory.EnumerateFiles(
                        path,
                        "*.cs",
                        SearchOption.AllDirectories))
                .Select(File.ReadAllText));
        var wrapper = File.ReadAllText(
            Path.Combine(
                repository,
                "tools",
                "postgres-snapshot-generation-quarantine.sh"));

        Assert.DoesNotContain(
            "\"docker\"",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            " docker ",
            wrapper,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "\"drop\" =>",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "\"delete\" =>",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "\"truncate\" =>",
            source,
            StringComparison.OrdinalIgnoreCase);
    }

    private string CreateArchivePackage()
    {
        var package = Path.Combine(_root, "archive");
        Directory.CreateDirectory(package);
        File.WriteAllText(
            Path.Combine(package, "archive.custom"),
            "archive");
        File.WriteAllText(
            Path.Combine(package, "archive.toc"),
            "toc");
        WriteJson(
            Path.Combine(package, "catalog.json"),
            new { catalog = true });
        var fingerprint = new
        {
            algorithm =
                "sha256-copy-to-jsonb-text-ordered-snapshot_id-song_id-instrument-account_id-v1",
            sha256 = new string('8', 64),
            rowCount = 8627L,
            streamBytes = 12345L,
        };
        var sourceFence = new
        {
            capturedAtUtc =
                "2026-08-30T00:00:00Z",
            catalogPhysicalSha256 =
                new string('9', 64),
            logicalCatalogSha256 =
                new string('a', 64),
            heapBytes = 100L,
            indexBytes = 200L,
            mutationCounters = new
            {
                inserts = 8627L,
                updates = 0L,
                removals = 0L,
            },
            preflightSha256 =
                new string('b', 64),
            rowFingerprint = fingerprint,
            targetOid = "500",
            targetRelfilenode = "500",
            totalBytes = 300L,
        };
        var manifestPath = Path.Combine(
            package,
            "manifest.json");
        WriteJson(
            manifestPath,
            new
            {
                toolId =
                    "fst.snapshot-generation-archive-only.v1",
                schemaVersion = 1,
                archiveOnly = true,
                status = "accepted",
                archive = new
                {
                    sha256 = Sha256(
                        Path.Combine(
                            package,
                            "archive.custom")),
                },
                catalog = new
                {
                    sha256 = Sha256(
                        Path.Combine(
                            package,
                            "catalog.json")),
                },
                cycle = new
                {
                    cycleId = 9L,
                    triggerScrapeId = 1329L,
                    triggerPublicationId = 148L,
                    plannerVersion = 3,
                    configVersion = 1,
                    status = "observed",
                    reportOnly = true,
                    oracleAgreement = true,
                    blockedCount = 0,
                    candidateCount = 99,
                    candidateIdentityHash =
                        new string('c', 64),
                    observationHash =
                        new string('d', 64),
                },
                target = new
                {
                    observationId = 1875L,
                    instrument =
                        "Solo_PeripheralCymbals",
                    snapshotId = 1314L,
                    rootSchema = "public",
                    rootRelation =
                        "leaderboard_entries_snapshot_pro_cymbals",
                    rootOid = 400L,
                    childSchema = "public",
                    childRelation =
                        "leaderboard_entries_snapshot_pro_cymbals_s1314",
                    childOid = 500L,
                    childRelfilenode = 500L,
                    stableChildIdentityHash =
                        new string('e', 64),
                    stableConfigSchemaHash =
                        new string('f', 64),
                },
                rowFingerprint = fingerprint,
                sourceFenceBefore = sourceFence,
                sourceFenceAfter = sourceFence with
                {
                    capturedAtUtc =
                        "2026-08-30T00:01:00Z",
                },
                sourceIdentity = new
                {
                    database = new
                    {
                        database = "fstservice",
                        databaseOid = "16384",
                        systemIdentifier =
                            "7623196498058817570",
                        serverVersionNum = 170009,
                    },
                },
            });
        WriteChecksumFile(
            package,
            [
                "archive.custom",
                "archive.toc",
                "catalog.json",
                "manifest.json",
            ]);

        var proof = Path.Combine(
            package,
            "proofs",
            "test-proof");
        Directory.CreateDirectory(proof);
        WriteJson(
            Path.Combine(proof, "cleanup.json"),
            new { cleanup = true });
        WriteJson(
            Path.Combine(
                proof,
                "container-evidence.json"),
            new { container = true });
        WriteJson(
            Path.Combine(
                proof,
                "restored-catalog.json"),
            new { catalog = true });
        WriteJson(
            Path.Combine(proof, "proof-manifest.json"),
            new
            {
                toolId =
                    "fst.snapshot-generation-archive-only.v1",
                schemaVersion = 1,
                status = "accepted",
                archiveOnly = true,
                networkMode = "none",
                publishedPorts = 0,
                packageManifestSha256 =
                    Sha256(manifestPath),
                archiveSha256 = Sha256(
                    Path.Combine(
                        package,
                        "archive.custom")),
                cleanup = new
                {
                    containerAbsenceProven = true,
                    containerRemoved = true,
                    ownedVolumesRemoved = true,
                    pgdataRemoved = true,
                    scratchRemoved = true,
                },
                validation = new
                {
                    rowFingerprint = fingerprint,
                    expectedLogicalCatalogSha256 =
                        new string('1', 64),
                    restoredLogicalCatalogSha256 =
                        new string('1', 64),
                },
            });
        WriteChecksumFile(
            proof,
            [
                "cleanup.json",
                "container-evidence.json",
                "proof-manifest.json",
                "restored-catalog.json",
            ]);
        return package;
    }

    private string CreateRouteCapture(
        string name,
        string generatedAt)
    {
        var directory = Path.Combine(_root, name);
        var normalized = Path.Combine(
            directory,
            "normalized");
        var raw = Path.Combine(directory, "raw");
        Directory.CreateDirectory(normalized);
        Directory.CreateDirectory(raw);
        var entries = Enumerable.Range(1, 55)
            .Select(index =>
            {
                var routeName = $"route-{index:D2}";
                var rawPath = Path.Combine(
                    raw,
                    $"{routeName}.body");
                var normalizedPath = Path.Combine(
                    normalized,
                    $"{routeName}.json");
                WriteJson(
                    rawPath,
                    new
                    {
                        value = index - 1,
                        generatedAt,
                    });
                WriteJson(
                    normalizedPath,
                    new
                    {
                        value = index - 1,
                        generatedAt,
                    });
                return new
                {
                    method = "GET",
                    name = routeName,
                    path = $"/api/test/{index}",
                    status = 200,
                    curlExit = 0,
                    isJson = true,
                    semanticSha256 =
                        Sha256(normalizedPath),
                    rawSha256 = Sha256(rawPath),
                    bytes = new FileInfo(rawPath).Length,
                    contentType =
                        "application/json; charset=utf-8",
                };
            })
            .ToArray();
        var manifest = Path.Combine(
            directory,
            "manifest.json");
        WriteJson(
            manifest,
            new
            {
                publicationId = 500L,
                publishedScrapeId = 1400L,
                routeCount = 55,
                entries,
            });
        return manifest;
    }

    private static void ReplaceRouteWithZip(
        string manifestPath,
        string content)
    {
        var directory = Path.GetDirectoryName(manifestPath)!;
        var rawPath = Path.Combine(
            directory,
            "raw",
            "route-55.body");
        using (var file = new FileStream(
                   rawPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        using (var archive = new ZipArchive(
                   file,
                   ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("export/data.txt");
            using var writer = new StreamWriter(
                entry.Open(),
                new UTF8Encoding(false));
            writer.Write(content);
        }
        File.Delete(
            Path.Combine(
                directory,
                "normalized",
                "route-55.json"));

        var root = JsonNode.Parse(
            File.ReadAllText(manifestPath))!.AsObject();
        var entries = root["entries"]!.AsArray();
        var entryNode = entries
            .Select(node => node!.AsObject())
            .Single(node =>
                node["name"]!.GetValue<string>()
                    == "route-55");
        entryNode["isJson"] = false;
        entryNode["semanticSha256"] = null;
        entryNode["rawSha256"] = Sha256(rawPath);
        entryNode["bytes"] = new FileInfo(rawPath).Length;
        entryNode["contentType"] = "application/zip";
        File.WriteAllText(
            manifestPath,
            root.ToJsonString(
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                }),
            new UTF8Encoding(false));
    }

    private static void WriteChecksumFile(
        string directory,
        IReadOnlyCollection<string> names)
    {
        var content = string.Concat(
            names.Order(StringComparer.Ordinal)
                .Select(name =>
                    $"{Sha256(Path.Combine(directory, name))}  {name}\n"));
        File.WriteAllText(
            Path.Combine(directory, "SHA256SUMS"),
            content,
            new UTF8Encoding(false));
    }

    private static void WriteJson(
        string path,
        object value)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                }),
            new UTF8Encoding(false));
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(
                SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    private static string FindRepositoryRoot()
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
}
