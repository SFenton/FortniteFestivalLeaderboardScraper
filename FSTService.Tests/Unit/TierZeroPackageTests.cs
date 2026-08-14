using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using FSTService.Scraping;
using FSTService.Scraping.Replay;

namespace FSTService.Tests.Unit;

public sealed class TierZeroPackageTests
{
    [Fact]
    public async Task SealIsDeterministicAcrossInputOrderAndCulture()
    {
        using var first = new PackageDirectory("deterministic-first");
        using var second = new PackageDirectory("deterministic-second");
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var firstManifest = await CreateSealedPackageAsync(
                first.Path,
                reverseInputs: false);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var secondManifest = await CreateSealedPackageAsync(
                second.Path,
                reverseInputs: true);

            Assert.Equal(
                firstManifest.PackageRootHash,
                secondManifest.PackageRootHash);
            Assert.Equal(
                await File.ReadAllBytesAsync(
                    System.IO.Path.Combine(
                        first.Path,
                        TierZeroEvidenceFormat.ManifestFileName)),
                await File.ReadAllBytesAsync(
                    System.IO.Path.Combine(
                        second.Path,
                        TierZeroEvidenceFormat.ManifestFileName)));
            Assert.Equal(
                await File.ReadAllBytesAsync(
                    System.IO.Path.Combine(
                        first.Path,
                        TierZeroEvidenceFormat.ChecksumFileName)),
                await File.ReadAllBytesAsync(
                    System.IO.Path.Combine(
                        second.Path,
                        TierZeroEvidenceFormat.ChecksumFileName)));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public async Task SealAndVerifierPreserveExactMetadataWithoutArtifactContent()
    {
        using var directory = new PackageDirectory("exact-metadata");
        var manifest = await CreateSealedPackageAsync(directory.Path);
        var result = await TierZeroPackageVerifier.VerifyAsync(
            directory.Path,
            Expected(manifest));

        Assert.True(result.IsValid, string.Join(
            Environment.NewLine,
            result.Failures.Select(static failure => failure.Message)));
        Assert.NotNull(result.Manifest);
        Assert.Equal(TierZeroEvidenceFormat.FormatId, manifest.FormatId);
        Assert.Equal(TierZeroEvidenceFormat.ManifestVersion, manifest.ManifestVersion);
        Assert.Equal(TierZeroPackageStatus.Sealed, manifest.Status);
        Assert.Null(manifest.Error);
        Assert.True(TierZeroCanonicalJson.IsSha256(manifest.PackageRootHash));
        Assert.Equal(
            TierZeroCanonicalJson.ComputeManifestRootHash(manifest),
            TierZeroCanonicalJson.ComputeManifestRootHash(
                manifest with { PackageRootHash = Hash("ignored-root-field") }));
        Assert.Equal(1296, manifest.Source.ScrapeId);
        Assert.Equal(61, manifest.Source.PublicationId);
        Assert.Equal(
            FixedCreatedAt.AddMinutes(-10),
            manifest.Source.SourceCutUtc);
        Assert.Equal(
            "festival-catalog-2026-08-14",
            manifest.Source.Catalog.Identity);
        Assert.Equal(new string('a', 40), manifest.Build.GitCommit);
        Assert.Equal($"sha256:{Hash("image")}", manifest.Build.OciImageDigest);
        Assert.Equal(new string('b', 40), manifest.Build.OciImageRevision);
        Assert.Equal("1.0.196", manifest.Build.ServiceVersion);
        Assert.Equal(17, manifest.Database.MajorVersion);
        Assert.Equal(
            ["btree_gin@1.3", "pg_trgm@1.6"],
            manifest.Database.Extensions);
        Assert.Equal(
            ["Scraper:PageConcurrency", "Scraper:SequentialScrape"],
            manifest.Configuration.Keys);
        Assert.Single(manifest.SummaryReferences.ScopeManifests);
        Assert.Single(manifest.SummaryReferences.ScopeFingerprints);
        Assert.Single(manifest.SummaryReferences.PhaseOutcomes);
        Assert.Single(manifest.SummaryReferences.PhaseTimings);
        Assert.Equal(
            ["capture", "catalog"],
            manifest.ParentRootHashes.Select(
                static parent => parent.LogicalParent));
        Assert.Equal(1, manifest.Attempt);
        Assert.Equal("tier0-test-producer", manifest.ProducerIdentity);
        Assert.Equal(FixedCreatedAt, manifest.CreatedAtUtc);
        Assert.Equal(FixedSealedAt, manifest.SealedAtUtc);
        Assert.Equal(
            TierZeroEvidenceFormat.ChecksumFileName,
            manifest.ChecksumManifest.Path);
        Assert.Equal(
            ArtifactSpecs.Count,
            manifest.ChecksumManifest.EntryCount);
        Assert.True(TierZeroCanonicalJson.IsSha256(
            manifest.ChecksumManifest.Sha256));
        Assert.True(TierZeroCanonicalJson.IsSha256(
            manifest.StateSha256));
        Assert.Equal(PhaseProgressCatalog.All.Count, manifest.PhasePlan.Phases.Count);
        Assert.Equal(
            PhaseProgressCatalog.All.Select(static phase => phase.Id),
            manifest.PhasePlan.Phases.Select(static phase => phase.Id));

        var counts = Assert.Single(
            manifest.Artifacts,
            static artifact => artifact.Path == "data/counts.csv");
        Assert.Equal("score-evidence", counts.LogicalOwner);
        Assert.Equal("text/csv", counts.MediaType);
        Assert.Equal(3, counts.SchemaVersion);
        Assert.Equal(long.MaxValue, counts.RowCount);
        Assert.Equal(counts.CompressedBytes, counts.UncompressedBytes);
        Assert.Equal(
            [new TierZeroArtifactRange("score", "1", "999999")],
            counts.Ranges);

        var empty = Assert.Single(
            manifest.Artifacts,
            static artifact => artifact.Path == "data/empty.bin");
        Assert.Equal(0, empty.RowCount);
        Assert.Equal(0, empty.CompressedBytes);
        Assert.Equal(0, empty.UncompressedBytes);
        Assert.Equal(
            TierZeroCanonicalJson.Sha256Hex([]),
            empty.Sha256);

        var manifestText = await File.ReadAllTextAsync(
            System.IO.Path.Combine(
                directory.Path,
                TierZeroEvidenceFormat.ManifestFileName));
        Assert.DoesNotContain("artifact-secret-sentinel", manifestText);
        Assert.DoesNotContain("ConnectionStrings", manifestText);
        Assert.DoesNotContain("Host=postgres", manifestText);
    }

    [Fact]
    public async Task NormalizedDuplicateArtifactPathsAreRejected()
    {
        using var directory = new PackageDirectory("duplicate-path");
        var writer = await TierZeroPackageWriter.CreateAsync(
            directory.Path,
            CreateDraft());
        await writer.AddArtifactAsync(
            Registration("data\\scores.csv", rowCount: 1),
            "first"u8.ToArray());

        var exception = await Assert.ThrowsAsync<TierZeroPackageException>(
            () => writer.AddArtifactAsync(
                Registration("data/scores.csv", rowCount: 1),
                "second"u8.ToArray()));

        Assert.Equal(TierZeroPackageError.DuplicateArtifactPath, exception.Error);
        var caseVariant = await Assert.ThrowsAsync<TierZeroPackageException>(
            () => writer.AddArtifactAsync(
                Registration("Data/SCORES.csv", rowCount: 1),
                "third"u8.ToArray()));
        Assert.Equal(
            TierZeroPackageError.DuplicateArtifactPath,
            caseVariant.Error);
        var ancestorCaseVariant =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => writer.AddArtifactAsync(
                    Registration("Data/other.json", rowCount: 1),
                    "fourth"u8.ToArray()));
        Assert.Equal(
            TierZeroPackageError.DuplicateArtifactPath,
            ancestorCaseVariant.Error);

        using var namespaceDirectory =
            new PackageDirectory("namespace-collision");
        var namespaceWriter = await TierZeroPackageWriter.CreateAsync(
            namespaceDirectory.Path,
            CreateDraft());
        await namespaceWriter.AddArtifactAsync(
            Registration("namespace", 1),
            "file"u8.ToArray());
        var fileDirectoryCollision =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => namespaceWriter.AddArtifactAsync(
                    Registration("namespace/child.json", 1),
                    "child"u8.ToArray()));
        Assert.Equal(
            TierZeroPackageError.DuplicateArtifactPath,
            fileDirectoryCollision.Error);
    }

    [Fact]
    public async Task SealIsAtomicAndPreventsMutationOrOverwrite()
    {
        using var directory = new PackageDirectory("atomic-seal");
        var writer = await CreateWriterWithAllArtifactsAsync(directory.Path);
        await writer.SealAsync(FixedSealedAt);

        Assert.Empty(Directory.EnumerateFiles(
            directory.Path,
            "*.partial-*",
            SearchOption.AllDirectories));
        var sealedException = await Assert.ThrowsAsync<TierZeroPackageException>(
            () => writer.AddArtifactAsync(
                Registration("data/late.json", rowCount: 1),
                "{}"u8.ToArray()));
        Assert.Equal(TierZeroPackageError.PackageAlreadySealed, sealedException.Error);
        var overwriteException = await Assert.ThrowsAsync<TierZeroPackageException>(
            () => writer.SealAsync(FixedSealedAt));
        Assert.Equal(TierZeroPackageError.PackageAlreadySealed, overwriteException.Error);
    }

    [Fact]
    public async Task SealCanonicalizesAndHashesExactStateJournalBytes()
    {
        using var directory = new PackageDirectory("exact-state-bytes");
        var writer = await CreateWriterWithAllArtifactsAsync(directory.Path);
        var statePath = StatePath(directory.Path);
        await File.WriteAllTextAsync(
            statePath,
            "\n" + await File.ReadAllTextAsync(statePath));

        var manifest = await writer.SealAsync(FixedSealedAt);
        var stateBytes = await File.ReadAllBytesAsync(statePath);
        var state = TierZeroCanonicalJson.Deserialize<TierZeroPackageState>(
            stateBytes);

        Assert.Equal(
            manifest.StateSha256,
            TierZeroCanonicalJson.Sha256Hex(stateBytes));
        Assert.Equal(
            TierZeroCanonicalJson.Serialize(state),
            stateBytes);
        Assert.True((await TierZeroPackageVerifier.VerifyAsync(
            directory.Path)).IsValid);
    }

    [Fact]
    public void ManifestWriteFailuresDistinguishCollisionFromOtherIo()
    {
        using var directory = new PackageDirectory(
            "manifest-write-classification");
        Directory.CreateDirectory(directory.Path);
        var manifestPath = ManifestPath(directory.Path);
        var writeFailure =
            TierZeroPackageWriter.ClassifyManifestWriteFailure(
                directory.Path,
                manifestPath,
                new IOException("synthetic write failure"));
        File.WriteAllText(manifestPath, "{}");
        var collision =
            TierZeroPackageWriter.ClassifyManifestWriteFailure(
                directory.Path,
                manifestPath,
                new IOException("synthetic destination collision"));

        Assert.Equal(
            TierZeroPackageError.PackageWriteFailed,
            writeFailure.Error);
        Assert.Equal(
            TierZeroPackageError.PackageAlreadySealed,
            collision.Error);
    }

    [Fact]
    public async Task ConcurrentSealersCannotOverwriteCommittedChecksums()
    {
        using var directory = new PackageDirectory("concurrent-seal");
        var first = await CreateWriterWithAllArtifactsAsync(directory.Path);
        var second = await TierZeroPackageWriter.ResumeAsync(
            directory.Path,
            ExpectedResume());

        var outcomes = await Task.WhenAll(
            CaptureSealAsync(first),
            CaptureSealAsync(second));

        Assert.Single(outcomes, static outcome => outcome.Manifest is not null);
        var failure = Assert.Single(
            outcomes,
            static outcome => outcome.Exception is not null);
        Assert.Equal(
            TierZeroPackageError.PackageAlreadySealed,
            Assert.IsType<TierZeroPackageException>(
                failure.Exception).Error);
        Assert.True((await TierZeroPackageVerifier.VerifyAsync(
            directory.Path,
            Expected(outcomes.Single(
                static outcome => outcome.Manifest is not null).Manifest!)))
            .IsValid);
    }

    [Fact]
    public async Task ArtifactIsRolledBackWhenJournalCommitFails()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var directory = new PackageDirectory("journal-rollback");
        var writer = await TierZeroPackageWriter.CreateAsync(
            directory.Path,
            CreateDraft());
        var dataDirectory = System.IO.Path.Combine(directory.Path, "data");
        Directory.CreateDirectory(dataDirectory);
        var rootMode = File.GetUnixFileMode(directory.Path);
        try
        {
            File.SetUnixFileMode(
                directory.Path,
                UnixFileMode.UserRead |
                UnixFileMode.UserExecute);
            var exception = await Record.ExceptionAsync(
                () => writer.AddArtifactAsync(
                    Registration("data/uncommitted.json", 1),
                    "{}"u8.ToArray()));
            Assert.True(
                exception is UnauthorizedAccessException or IOException,
                exception?.ToString());
        }
        finally
        {
            File.SetUnixFileMode(directory.Path, rootMode);
        }

        Assert.False(File.Exists(System.IO.Path.Combine(
            dataDirectory,
            "uncommitted.json")));
    }

    [Fact]
    public async Task SealRejectsUntrackedFinalFiles()
    {
        using var directory = new PackageDirectory("seal-extra-file");
        var writer = await CreateWriterWithAllArtifactsAsync(directory.Path);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(directory.Path, "untracked.json"),
            "{}");

        var exception = await Assert.ThrowsAsync<TierZeroPackageException>(
            () => writer.SealAsync(FixedSealedAt));

        Assert.Equal(
            TierZeroPackageError.ResumeArtifactMismatch,
            exception.Error);
        Assert.False(File.Exists(ManifestPath(directory.Path)));
    }

    [Fact]
    public async Task SealAndVerifierRejectUntrackedEmptyDirectories()
    {
        using var unsealed = new PackageDirectory("seal-extra-directory");
        var writer = await CreateWriterWithAllArtifactsAsync(unsealed.Path);
        Directory.CreateDirectory(System.IO.Path.Combine(
            unsealed.Path,
            "untracked-empty"));

        var sealException =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => writer.SealAsync(FixedSealedAt));

        Assert.Equal(
            TierZeroPackageError.ResumeArtifactMismatch,
            sealException.Error);

        using var sealedPackage =
            new PackageDirectory("verify-extra-directory");
        await CreateSealedPackageAsync(sealedPackage.Path);
        Directory.CreateDirectory(System.IO.Path.Combine(
            sealedPackage.Path,
            "untracked-empty"));

        AssertFailure(
            await TierZeroPackageVerifier.VerifyAsync(sealedPackage.Path),
            TierZeroVerificationFailureKind.ExtraDirectory);
    }

    [Fact]
    public async Task WriterRejectsInvalidLifecycleOperations()
    {
        using var missing = new PackageDirectory("resume-missing");
        var missingException = await Assert.ThrowsAsync<TierZeroPackageException>(
            () => TierZeroPackageWriter.ResumeAsync(
                missing.Path,
                ExpectedResume()));
        Assert.Equal(TierZeroPackageError.PackageNotFound, missingException.Error);

        using var noState = new PackageDirectory("resume-no-state");
        Directory.CreateDirectory(noState.Path);
        var noStateException = await Assert.ThrowsAsync<TierZeroPackageException>(
            () => TierZeroPackageWriter.ResumeAsync(
                noState.Path,
                ExpectedResume()));
        Assert.Equal(
            TierZeroPackageError.PackageNotResumable,
            noStateException.Error);

        using var directory = new PackageDirectory("invalid-lifecycle");
        var writer = await TierZeroPackageWriter.CreateAsync(
            directory.Path,
            CreateDraft());
        Assert.Equal(
            System.IO.Path.GetFullPath(directory.Path),
            writer.RootPath);
        Assert.False(writer.IsSealed);
        Assert.Empty(writer.Artifacts);

        var unreadable = await Assert.ThrowsAsync<ArgumentException>(
            () => writer.AddArtifactAsync(
                Registration("data/unreadable.json", 1),
                new UnreadableStream()));
        Assert.Equal("content", unreadable.ParamName);
        await Assert.ThrowsAsync<IOException>(
            () => writer.AddArtifactAsync(
                Registration("data/throwing.json", 1),
                new ThrowingReadStream()));
        Assert.Empty(Directory.EnumerateFiles(
            directory.Path,
            "*.partial-*",
            SearchOption.AllDirectories));

        var reserved = await Assert.ThrowsAsync<TierZeroPackageException>(
            () => writer.AddArtifactAsync(
                Registration(TierZeroEvidenceFormat.ManifestFileName, 1),
                "{}"u8.ToArray()));
        Assert.Equal(TierZeroPackageError.ReservedPath, reserved.Error);
        var reservedNamespace =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => writer.AddArtifactAsync(
                    Registration("manifest.json/payload.bin", 1),
                    "payload"u8.ToArray()));
        Assert.Equal(
            TierZeroPackageError.ReservedPath,
            reservedNamespace.Error);

        var existingPath = System.IO.Path.Combine(
            directory.Path,
            "data",
            "existing.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(existingPath)!);
        await File.WriteAllTextAsync(existingPath, "{}");
        var existing = await Assert.ThrowsAsync<TierZeroPackageException>(
            () => writer.AddArtifactAsync(
                Registration("data/existing.json", 1),
                "{}"u8.ToArray()));
        Assert.Equal(TierZeroPackageError.ArtifactAlreadyExists, existing.Error);

        var invalidStatus = await Assert.ThrowsAsync<TierZeroPackageException>(
            () => writer.SealAsync(
                FixedSealedAt,
                TierZeroPackageStatus.Draft));
        var failedWithoutError =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => writer.SealAsync(
                    FixedSealedAt,
                    TierZeroPackageStatus.Failed));
        var sealedWithError =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => writer.SealAsync(
                    FixedSealedAt,
                    TierZeroPackageStatus.Sealed,
                    "unexpected"));

        Assert.Equal(TierZeroPackageError.InvalidManifest, invalidStatus.Error);
        Assert.Equal(
            TierZeroPackageError.InvalidManifest,
            failedWithoutError.Error);
        Assert.Equal(
            TierZeroPackageError.InvalidManifest,
            sealedWithError.Error);
    }

    [Fact]
    public async Task ActiveWriterRejectsMissingOrChangedState()
    {
        using var missing = new PackageDirectory(
            "active-writer-missing-state");
        var missingWriter = await TierZeroPackageWriter.CreateAsync(
            missing.Path,
            CreateDraft());
        File.Delete(StatePath(missing.Path));
        var missingException =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => missingWriter.AddArtifactAsync(
                    Registration("data/new.json", 1),
                    "{}"u8.ToArray()));
        Assert.Equal(
            TierZeroPackageError.PackageNotResumable,
            missingException.Error);

        using var changed = new PackageDirectory(
            "active-writer-changed-state");
        var changedWriter = await TierZeroPackageWriter.CreateAsync(
            changed.Path,
            CreateDraft());
        var changedState = await ReadStateAsync(changed.Path);
        await File.WriteAllBytesAsync(
            StatePath(changed.Path),
            TierZeroCanonicalJson.Serialize(
                changedState with
                {
                    Draft = changedState.Draft with
                    {
                        ProducerIdentity = "different-producer",
                    },
                }));
        var changedException =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => changedWriter.MarkInterruptedAsync(
                    "interrupted"));
        Assert.Equal(
            TierZeroPackageError.ResumeIdentityMismatch,
            changedException.Error);
    }

    [Fact]
    public async Task OversizedStateIsRejectedBeforePackageCreation()
    {
        using var directory = new PackageDirectory(
            "oversized-state");
        var draft = CreateDraft();
        var keys = Enumerable.Range(0, 180_000)
            .Select(static index =>
                $"Scraper:SafeSetting{index:D6}")
            .ToArray();

        var exception =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroPackageWriter.CreateAsync(
                    directory.Path,
                    draft with
                    {
                        Configuration =
                            draft.Configuration with
                            {
                                Keys = keys,
                            },
                    }));

        Assert.Equal(
            TierZeroPackageError.InvalidMetadata,
            exception.Error);
    }

    [Fact]
    public void ModelValidationRejectsInvalidProducerMetadata()
    {
        var draft = CreateDraft();
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with { Source = null! }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with { Attempt = 0 }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with { PackageId = "https://private.example/package" }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with { ProducerIdentity = "Bearer producer" }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                Source = draft.Source with { ScrapeId = -1 },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                Source = draft.Source with
                {
                    SourceCutUtc = default(DateTimeOffset),
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                Source = draft.Source with
                {
                    Catalog = draft.Source.Catalog with
                    {
                        Identity = "",
                    },
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                Source = draft.Source with
                {
                    Catalog = draft.Source.Catalog with
                    {
                        ContentSha256 = "invalid",
                    },
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                Build = draft.Build with { GitCommit = "invalid" },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                Build = draft.Build with { OciImageDigest = "invalid" },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                Build = draft.Build with { OciImageDigest = null! },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                Build = draft.Build with { ServiceVersion = "" },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                Database = draft.Database with { MajorVersion = 0 },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                Database = draft.Database with
                {
                    Extensions = ["pg_trgm", "pg_trgm"],
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                Database = draft.Database with
                {
                    SchemaFingerprint = "invalid",
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                Configuration = draft.Configuration with
                {
                    Algorithm = "",
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                Configuration = draft.Configuration with
                {
                    Keys = [],
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                Configuration = draft.Configuration with
                {
                    Keys = ["Api:Token"],
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                Configuration = draft.Configuration with
                {
                    Keys = ["duplicate", "duplicate"],
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                Configuration = draft.Configuration with
                {
                    ValuesSha256 = "invalid",
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                SummaryReferences = draft.SummaryReferences with
                {
                    ScopeManifests = null!,
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                SummaryReferences = draft.SummaryReferences with
                {
                    ScopeManifests =
                    [
                        draft.SummaryReferences.ScopeManifests[0] with
                        {
                            RecordCount = -1,
                        },
                    ],
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                SummaryReferences = draft.SummaryReferences with
                {
                    ScopeManifests =
                    [
                        draft.SummaryReferences.ScopeManifests[0],
                        draft.SummaryReferences.ScopeManifests[0],
                    ],
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                SummaryReferences = draft.SummaryReferences with
                {
                    ScopeManifests = [null!],
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with { ParentRootHashes = [] }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                ParentRootHashes = [null!],
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with
            {
                ParentRootHashes =
                [
                    draft.ParentRootHashes[0],
                    draft.ParentRootHashes[0],
                ],
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeDraft(
            draft with { CreatedAtUtc = default }));
    }

    [Fact]
    public void ArtifactRegistrationValidationRejectsInvalidMetadata()
    {
        var valid = Registration("data/artifact.json", 1);
        AssertInvalid(() => TierZeroPackageModel.NormalizeRegistration(
            valid with { LogicalOwner = "" }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeRegistration(
            valid with { MediaType = "" }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeRegistration(
            valid with { SchemaVersion = 0 }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeRegistration(
            valid with { RowCount = -1 }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeRegistration(
            valid with { UncompressedBytes = -1 }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeRegistration(
            valid with
            {
                Ranges =
                [
                    new TierZeroArtifactRange("score", null, null),
                ],
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeRegistration(
            valid with { Ranges = [null!] }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeRegistration(
            valid with
            {
                Ranges =
                [
                    new TierZeroArtifactRange("score", "1", "2"),
                    new TierZeroArtifactRange("score", "3", "4"),
                ],
            }));
    }

    [Fact]
    public async Task ManifestAndStateValidationRejectInvalidTerminalMetadata()
    {
        using var directory = new PackageDirectory("invalid-terminal-metadata");
        var manifest = await CreateSealedPackageAsync(directory.Path);
        AssertInvalid(() => TierZeroPackageModel.NormalizeManifest(
            manifest with { FormatId = "unsupported" }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeManifest(
            manifest with { PhasePlan = null! }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeManifest(
            manifest with { SealedAtUtc = manifest.CreatedAtUtc.AddSeconds(-1) }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeManifest(
            manifest with { Status = TierZeroPackageStatus.Draft }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeManifest(
            manifest with
            {
                Status = TierZeroPackageStatus.Failed,
                Error = null,
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeManifest(
            manifest with { Error = "unexpected" }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeManifest(
            manifest with
            {
                Status = TierZeroPackageStatus.Failed,
                Error = "Bearer credential",
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeManifest(
            manifest with
            {
                ChecksumManifest = manifest.ChecksumManifest with
                {
                    Path = "wrong.txt",
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeManifest(
            manifest with
            {
                ChecksumManifest = manifest.ChecksumManifest with
                {
                    EntryCount = -1,
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeManifest(
            manifest with { PackageRootHash = "invalid" }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeManifest(
            manifest with { StateSha256 = "invalid" }));

        var state = TierZeroCanonicalJson.Deserialize<TierZeroPackageState>(
            await File.ReadAllBytesAsync(
                System.IO.Path.Combine(
                    directory.Path,
                    TierZeroEvidenceFormat.StateFileName)));
        AssertInvalid(() => TierZeroPackageModel.NormalizeState(
            state with { Draft = null! }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeState(
            state with
            {
                Status = TierZeroPackageStatus.Interrupted,
                Error = null,
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeState(
            state with { Error = "unexpected" }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeState(
            state with
            {
                Status = TierZeroPackageStatus.Interrupted,
                Error = "https://private.example",
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeState(
            state with
            {
                Status = TierZeroPackageStatus.Interrupted,
                Error = "interrupted",
                InterruptedAtUtc = default(DateTimeOffset),
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeState(
            state with
            {
                Status = TierZeroPackageStatus.Interrupted,
                Error = "interrupted",
                InterruptedAtUtc = null,
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeState(
            state with
            {
                InterruptedAtUtc = FixedCreatedAt,
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeState(
            state with
            {
                PhasePlan = state.PhasePlan with
                {
                    Phases = null!,
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeState(
            state with
            {
                PhasePlan = state.PhasePlan with
                {
                    Phases = [null!],
                },
            }));
        var firstPhase = state.PhasePlan.Phases[0];
        AssertInvalid(() => TierZeroPackageModel.NormalizeState(
            state with
            {
                PhasePlan = state.PhasePlan with
                {
                    Phases =
                    [
                        firstPhase with { Ordinal = 0 },
                        .. state.PhasePlan.Phases.Skip(1),
                    ],
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeState(
            state with
            {
                PhasePlan = state.PhasePlan with
                {
                    Phases =
                    [
                        firstPhase,
                        firstPhase,
                    ],
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeState(
            state with
            {
                PhasePlan = state.PhasePlan with
                {
                    Phases =
                    [
                        firstPhase,
                        state.PhasePlan.Phases[1] with
                        {
                            Ordinal = firstPhase.Ordinal,
                        },
                    ],
                },
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeState(
            state with { Artifacts = [null!] }));
        var firstArtifact = state.Artifacts[0];
        AssertInvalid(() => TierZeroPackageModel.NormalizeState(
            state with
            {
                Artifacts =
                [
                    firstArtifact with
                    {
                        Path = TierZeroEvidenceFormat.ManifestFileName,
                    },
                ],
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeState(
            state with
            {
                Artifacts =
                [
                    firstArtifact with { CompressedBytes = -1 },
                ],
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeState(
            state with
            {
                PendingArtifact = new TierZeroPendingArtifact(
                    firstArtifact with { Path = "pending/new.json" },
                    $"wrong/path.partial-123-{new string('a', 32)}"),
            }));
        AssertInvalid(() => TierZeroPackageModel.NormalizeState(
            state with
            {
                Status = TierZeroPackageStatus.Interrupted,
                Error = "interrupted",
                InterruptedAtUtc = FixedCreatedAt,
                PendingArtifact = new TierZeroPendingArtifact(
                    firstArtifact with { Path = "pending/new.json" },
                    $"pending/new.json.partial-123-{new string('a', 32)}"),
            }));
        var namespaceCollision =
            Assert.Throws<TierZeroPackageException>(() =>
                TierZeroPackageModel.NormalizeState(
                    state with
                    {
                        Artifacts =
                        [
                            firstArtifact with
                            {
                                Path = "namespace",
                            },
                        ],
                        PendingArtifact =
                            new TierZeroPendingArtifact(
                                firstArtifact with
                                {
                                    Path =
                                        "namespace/child.json",
                                },
                                $"namespace/child.json.partial-123-{new string('a', 32)}"),
                    }));
        Assert.Equal(
            TierZeroPackageError.DuplicateArtifactPath,
            namespaceCollision.Error);
    }

    [Fact]
    public async Task InterruptedPackageResumesOnlyWithMatchingImmutableIdentity()
    {
        using var directory = new PackageDirectory("resume");
        var writer = await TierZeroPackageWriter.CreateAsync(
            directory.Path,
            CreateDraft());
        var first = ArtifactSpecs[0];
        await writer.AddArtifactAsync(first.Registration, first.Content);
        await writer.MarkInterruptedAsync(
            "synthetic interruption",
            FixedCreatedAt.AddMinutes(2));
        var orphanedTemporary = System.IO.Path.Combine(
            directory.Path,
            $"orphan.partial-123-{new string('a', 32)}");
        await File.WriteAllTextAsync(
            orphanedTemporary,
            "interrupted write");

        var mismatch = ExpectedResume() with
        {
            ParentRootHashes =
            [
                new TierZeroParentRootHash("capture", Hash("wrong")),
            ],
        };
        var mismatchException = await Assert.ThrowsAsync<TierZeroPackageException>(
            () => TierZeroPackageWriter.ResumeAsync(
                directory.Path,
                mismatch));
        Assert.Equal(
            TierZeroPackageError.ResumeIdentityMismatch,
            mismatchException.Error);
        foreach (var mismatchIdentity in new[]
                 {
                     ExpectedResume() with
                     {
                         ConfigurationValuesSha256 = Hash("wrong-config"),
                     },
                     ExpectedResume() with
                     {
                         DatabaseSchemaFingerprint = Hash("wrong-schema"),
                     },
                     ExpectedResume() with
                     {
                         PhasePlanVersion = "fst.scrape-plan.v999",
                     },
                 })
        {
            var identityException =
                await Assert.ThrowsAsync<TierZeroPackageException>(
                    () => TierZeroPackageWriter.ResumeAsync(
                        directory.Path,
                        mismatchIdentity));
            Assert.Equal(
                TierZeroPackageError.ResumeIdentityMismatch,
                identityException.Error);
        }

        var resumeExpectations = ExpectedResume();
        var resumed = await TierZeroPackageWriter.ResumeAsync(
            directory.Path,
            resumeExpectations with
            {
                ParentRootHashes = resumeExpectations.ParentRootHashes
                    .Select(static parent => parent with
                    {
                        Sha256 = parent.Sha256.ToUpperInvariant(),
                    })
                    .ToArray(),
                ConfigurationValuesSha256 =
                    resumeExpectations.ConfigurationValuesSha256
                        .ToUpperInvariant(),
                DatabaseSchemaFingerprint =
                    resumeExpectations.DatabaseSchemaFingerprint
                        .ToUpperInvariant(),
            });
        Assert.False(File.Exists(orphanedTemporary));
        foreach (var artifact in ArtifactSpecs.Skip(1))
            await resumed.AddArtifactAsync(artifact.Registration, artifact.Content);
        var manifest = await resumed.SealAsync(FixedSealedAt);

        Assert.Equal(TierZeroPackageStatus.Sealed, manifest.Status);
        Assert.Equal(ArtifactSpecs.Count, manifest.Artifacts.Count);
        var verification = Expected(manifest);
        Assert.True((await TierZeroPackageVerifier.VerifyAsync(
            directory.Path,
            verification with
            {
                ParentRootHashes = verification.ParentRootHashes!
                    .Select(static parent => parent with
                    {
                        Sha256 = parent.Sha256.ToUpperInvariant(),
                    })
                    .ToArray(),
                ConfigurationValuesSha256 =
                    verification.ConfigurationValuesSha256!
                        .ToUpperInvariant(),
                DatabaseSchemaFingerprint =
                    verification.DatabaseSchemaFingerprint!
                        .ToUpperInvariant(),
            })).IsValid);
    }

    [Fact]
    public async Task ResumeRemovesEmptyDirectoriesLeftBeforeArtifactWrite()
    {
        using var directory = new PackageDirectory(
            "resume-empty-transaction-directory");
        await TierZeroPackageWriter.CreateAsync(
            directory.Path,
            CreateDraft());
        var empty = System.IO.Path.Combine(
            directory.Path,
            "data",
            "nested");
        Directory.CreateDirectory(empty);

        var resumed = await TierZeroPackageWriter.ResumeAsync(
            directory.Path,
            ExpectedResume());

        Assert.Empty(resumed.Artifacts);
        Assert.False(Directory.Exists(empty));
        Assert.False(Directory.Exists(
            System.IO.Path.GetDirectoryName(empty)!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ResumeCommitsPendingArtifactFromTemporaryOrFinalFile(
        bool movedToFinal,
        bool retainedTemporary)
    {
        using var directory = new PackageDirectory(
            movedToFinal
                ? "pending-final"
                : "pending-temporary");
        await TierZeroPackageWriter.CreateAsync(
            directory.Path,
            CreateDraft());
        var content = "pending artifact"u8.ToArray();
        var descriptor = PendingDescriptor(content);
        var temporaryRelative =
            $"data/pending.json.partial-123-{new string('a', 32)}";
        var temporaryPath = System.IO.Path.Combine(
            directory.Path,
            temporaryRelative.Replace(
                '/',
                System.IO.Path.DirectorySeparatorChar));
        var finalPath = System.IO.Path.Combine(
            directory.Path,
            descriptor.Path.Replace(
                '/',
                System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(
            System.IO.Path.GetDirectoryName(temporaryPath)!);
        if (movedToFinal)
            await File.WriteAllBytesAsync(finalPath, content);
        if (!movedToFinal || retainedTemporary)
            await File.WriteAllBytesAsync(temporaryPath, content);
        await WritePendingStateAsync(
            directory.Path,
            descriptor,
            temporaryRelative);

        var resumed = await TierZeroPackageWriter.ResumeAsync(
            directory.Path,
            ExpectedResume());

        Assert.Single(resumed.Artifacts);
        Assert.Equal(descriptor, resumed.Artifacts[0]);
        Assert.True(File.Exists(finalPath));
        Assert.False(File.Exists(temporaryPath));
        var state = await ReadStateAsync(directory.Path);
        Assert.Null(state.PendingArtifact);
        Assert.Equal(
            TierZeroCanonicalJson.Serialize(descriptor),
            TierZeroCanonicalJson.Serialize(
                Assert.Single(state.Artifacts)));
    }

    [Fact]
    public async Task ResumeRejectsMissingOrChangedPendingArtifact()
    {
        using var missing = new PackageDirectory("pending-missing");
        await TierZeroPackageWriter.CreateAsync(
            missing.Path,
            CreateDraft());
        var content = "pending artifact"u8.ToArray();
        var descriptor = PendingDescriptor(content);
        var temporaryRelative =
            $"data/pending.json.partial-123-{new string('b', 32)}";
        await WritePendingStateAsync(
            missing.Path,
            descriptor,
            temporaryRelative);
        AssertResumeFailure(
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroPackageWriter.ResumeAsync(
                    missing.Path,
                    ExpectedResume())),
            TierZeroPackageError.ResumeArtifactMismatch);

        using var changed = new PackageDirectory("pending-changed");
        await TierZeroPackageWriter.CreateAsync(
            changed.Path,
            CreateDraft());
        var temporaryPath = System.IO.Path.Combine(
            changed.Path,
            temporaryRelative.Replace(
                '/',
                System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(
            System.IO.Path.GetDirectoryName(temporaryPath)!);
        await File.WriteAllTextAsync(temporaryPath, "changed");
        await WritePendingStateAsync(
            changed.Path,
            descriptor,
            temporaryRelative);
        AssertResumeFailure(
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroPackageWriter.ResumeAsync(
                    changed.Path,
                    ExpectedResume())),
            TierZeroPackageError.ResumeArtifactMismatch);
    }

    [Fact]
    public async Task ResumeRejectsChangedCurrentPhaseDescriptor()
    {
        using var directory = new PackageDirectory(
            "resume-changed-phase-descriptor");
        await TierZeroPackageWriter.CreateAsync(
            directory.Path,
            CreateDraft());
        var state = await ReadStateAsync(directory.Path);
        var changedPhase = state.PhasePlan.Phases[0] with
        {
            Label = "Changed phase label",
        };
        await File.WriteAllBytesAsync(
            StatePath(directory.Path),
            TierZeroCanonicalJson.Serialize(
                state with
                {
                    PhasePlan = state.PhasePlan with
                    {
                        Phases =
                        [
                            changedPhase,
                            .. state.PhasePlan.Phases.Skip(1),
                        ],
                    },
                }));

        var exception =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroPackageWriter.ResumeAsync(
                    directory.Path,
                    ExpectedResume()));

        Assert.Equal(
            TierZeroPackageError.ResumeIdentityMismatch,
            exception.Error);
    }

    [Fact]
    public async Task ResumeRejectsMissingModifiedUntrackedAndInvalidState()
    {
        using var missing = new PackageDirectory("resume-missing-artifact");
        await CreateInterruptedWithFirstArtifactAsync(missing.Path);
        File.Delete(System.IO.Path.Combine(
            missing.Path,
            TierZeroPackagePath.Normalize(
                ArtifactSpecs[0].Registration.Path).Replace(
                '/',
                System.IO.Path.DirectorySeparatorChar)));
        AssertResumeFailure(
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroPackageWriter.ResumeAsync(
                    missing.Path,
                    ExpectedResume())),
            TierZeroPackageError.ResumeArtifactMismatch);

        using var modified = new PackageDirectory("resume-modified-artifact");
        await CreateInterruptedWithFirstArtifactAsync(modified.Path);
        var modifiedPath = System.IO.Path.Combine(
            modified.Path,
            TierZeroPackagePath.Normalize(
                ArtifactSpecs[0].Registration.Path).Replace(
                '/',
                System.IO.Path.DirectorySeparatorChar));
        var original = await File.ReadAllBytesAsync(modifiedPath);
        original[0] ^= 0x01;
        await File.WriteAllBytesAsync(modifiedPath, original);
        AssertResumeFailure(
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroPackageWriter.ResumeAsync(
                    modified.Path,
                    ExpectedResume())),
            TierZeroPackageError.ResumeArtifactMismatch);

        using var extra = new PackageDirectory("resume-extra-artifact");
        await CreateInterruptedWithFirstArtifactAsync(extra.Path);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(extra.Path, "untracked.json"),
            "{}");
        AssertResumeFailure(
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroPackageWriter.ResumeAsync(
                    extra.Path,
                    ExpectedResume())),
            TierZeroPackageError.ResumeArtifactMismatch);

        using var emptyState = new PackageDirectory("resume-empty-state");
        await CreateInterruptedWithFirstArtifactAsync(emptyState.Path);
        await File.WriteAllBytesAsync(
            System.IO.Path.Combine(
                emptyState.Path,
                TierZeroEvidenceFormat.StateFileName),
            []);
        AssertResumeFailure(
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroPackageWriter.ResumeAsync(
                    emptyState.Path,
                    ExpectedResume())),
            TierZeroPackageError.PackageNotResumable);

        using var invalidState = new PackageDirectory("resume-invalid-state");
        await CreateInterruptedWithFirstArtifactAsync(invalidState.Path);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(
                invalidState.Path,
                TierZeroEvidenceFormat.StateFileName),
            "{");
        AssertResumeFailure(
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroPackageWriter.ResumeAsync(
                    invalidState.Path,
                    ExpectedResume())),
            TierZeroPackageError.PackageNotResumable);

        using var terminalState = new PackageDirectory("resume-terminal-state");
        await TierZeroPackageWriter.CreateAsync(
            terminalState.Path,
            CreateDraft());
        var terminalStatePath = System.IO.Path.Combine(
            terminalState.Path,
            TierZeroEvidenceFormat.StateFileName);
        await File.WriteAllTextAsync(
            terminalStatePath,
            (await File.ReadAllTextAsync(terminalStatePath)).Replace(
                "\"status\":\"draft\"",
                "\"status\":\"sealed\"",
                StringComparison.Ordinal));
        AssertResumeFailure(
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroPackageWriter.ResumeAsync(
                    terminalState.Path,
                    ExpectedResume())),
            TierZeroPackageError.PackageNotResumable);
    }

    [Fact]
    public async Task NewAttemptNeverOverwritesPriorSealedOutput()
    {
        using var first = new PackageDirectory("attempt-one");
        using var second = new PackageDirectory("attempt-two");
        var firstManifest = await CreateSealedPackageAsync(first.Path);

        var samePath = await Assert.ThrowsAsync<TierZeroPackageException>(
            () => TierZeroPackageWriter.CreateAsync(
                first.Path,
                CreateDraft() with { Attempt = 2 }));
        Assert.Equal(TierZeroPackageError.PackageAlreadyExists, samePath.Error);
        var sealedResume = await Assert.ThrowsAsync<TierZeroPackageException>(
            () => TierZeroPackageWriter.ResumeAsync(
                first.Path,
                ExpectedResume()));
        Assert.Equal(
            TierZeroPackageError.PackageAlreadySealed,
            sealedResume.Error);

        var secondManifest = await CreateSealedPackageAsync(
            second.Path,
            CreateDraft() with { Attempt = 2 });
        Assert.Equal(1, firstManifest.Attempt);
        Assert.Equal(2, secondManifest.Attempt);
        Assert.Equal(
            firstManifest.PackageRootHash,
            (await ReadManifestAsync(first.Path)).PackageRootHash);
    }

    [Fact]
    public async Task SealedResumeDoesNotRepairMissingOrCorruptLock()
    {
        using var missing = new PackageDirectory(
            "sealed-missing-lock");
        await CreateSealedPackageAsync(missing.Path);
        var missingLock = System.IO.Path.Combine(
            missing.Path,
            TierZeroEvidenceFormat.LockFileName);
        File.Delete(missingLock);

        var missingException =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroPackageWriter.ResumeAsync(
                    missing.Path,
                    ExpectedResume()));

        Assert.Equal(
            TierZeroPackageError.PackageAlreadySealed,
            missingException.Error);
        Assert.False(File.Exists(missingLock));
        AssertFailure(
            await TierZeroPackageVerifier.VerifyAsync(missing.Path),
            TierZeroVerificationFailureKind.LockMissing);

        using var corrupt = new PackageDirectory(
            "sealed-corrupt-lock");
        await CreateSealedPackageAsync(corrupt.Path);
        var corruptLock = System.IO.Path.Combine(
            corrupt.Path,
            TierZeroEvidenceFormat.LockFileName);
        await File.WriteAllTextAsync(corruptLock, "corrupt");

        var corruptException =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroPackageWriter.ResumeAsync(
                    corrupt.Path,
                    ExpectedResume()));

        Assert.Equal(
            TierZeroPackageError.PackageAlreadySealed,
            corruptException.Error);
        Assert.Equal(
            "corrupt",
            await File.ReadAllTextAsync(corruptLock));
        AssertFailure(
            await TierZeroPackageVerifier.VerifyAsync(corrupt.Path),
            TierZeroVerificationFailureKind.LockMismatch);

        using var unsealed = new PackageDirectory(
            "unsealed-missing-lock");
        await TierZeroPackageWriter.CreateAsync(
            unsealed.Path,
            CreateDraft());
        File.Delete(System.IO.Path.Combine(
            unsealed.Path,
            TierZeroEvidenceFormat.LockFileName));
        var unsealedException =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroPackageWriter.ResumeAsync(
                    unsealed.Path,
                    ExpectedResume()));
        Assert.Equal(
            TierZeroPackageError.PackageNotResumable,
            unsealedException.Error);

        using var unsealedCorrupt = new PackageDirectory(
            "unsealed-corrupt-lock");
        await TierZeroPackageWriter.CreateAsync(
            unsealedCorrupt.Path,
            CreateDraft());
        var unsealedCorruptLock = System.IO.Path.Combine(
            unsealedCorrupt.Path,
            TierZeroEvidenceFormat.LockFileName);
        await File.WriteAllTextAsync(
            unsealedCorruptLock,
            "corrupt");
        var unsealedCorruptException =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroPackageWriter.ResumeAsync(
                    unsealedCorrupt.Path,
                    ExpectedResume()));
        Assert.Equal(
            TierZeroPackageError.ResumeArtifactMismatch,
            unsealedCorruptException.Error);
        Assert.Equal(
            "corrupt",
            await File.ReadAllTextAsync(unsealedCorruptLock));
    }

    [Fact]
    public async Task FailedPackageCanBeSealedWithVisibleError()
    {
        using var directory = new PackageDirectory("failed-seal");
        var writer = await CreateWriterWithAllArtifactsAsync(directory.Path);
        var manifest = await writer.SealAsync(
            FixedSealedAt,
            TierZeroPackageStatus.Failed,
            "synthetic phase failure");

        Assert.Equal(TierZeroPackageStatus.Failed, manifest.Status);
        Assert.Equal("synthetic phase failure", manifest.Error);
        Assert.True((await TierZeroPackageVerifier.VerifyAsync(directory.Path)).IsValid);
    }

    [Fact]
    public async Task PackageErrorsRejectSecretOrEndpointMaterial()
    {
        using var interrupted = new PackageDirectory("secret-interruption");
        var interruptedWriter = await TierZeroPackageWriter.CreateAsync(
            interrupted.Path,
            CreateDraft());
        var interruptedException =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => interruptedWriter.MarkInterruptedAsync(
                    "Bearer credential"));

        using var failed = new PackageDirectory("secret-failed-seal");
        var failedWriter = await CreateWriterWithAllArtifactsAsync(failed.Path);
        var failedException =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => failedWriter.SealAsync(
                    FixedSealedAt,
                    TierZeroPackageStatus.Failed,
                    "Host=postgres"));
        using var endpoint = new PackageDirectory(
            "endpoint-failed-seal");
        var endpointWriter =
            await CreateWriterWithAllArtifactsAsync(endpoint.Path);
        var endpointException =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => endpointWriter.SealAsync(
                    FixedSealedAt,
                    TierZeroPackageStatus.Failed,
                    "request to db.internal.example failed"));
        var connectionStringException =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => endpointWriter.MarkInterruptedAsync(
                    "Host = postgres;Database = fst;User ID = admin"));
        using var range = new PackageDirectory("secret-range");
        var rangeWriter = await TierZeroPackageWriter.CreateAsync(
            range.Path,
            CreateDraft());
        var rangeException =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => rangeWriter.AddArtifactAsync(
                    new TierZeroArtifactRegistration(
                        "safe-owner",
                        "data/range.json",
                        "application/json",
                        1,
                        1,
                        2,
                        [new TierZeroArtifactRange(
                            "source",
                            "https://private.example",
                            null)]),
                    "{}"u8.ToArray()));

        Assert.Equal(
            TierZeroPackageError.InvalidMetadata,
            interruptedException.Error);
        Assert.Equal(
            TierZeroPackageError.InvalidMetadata,
            failedException.Error);
        Assert.Equal(
            TierZeroPackageError.InvalidMetadata,
            endpointException.Error);
        Assert.Equal(
            TierZeroPackageError.InvalidMetadata,
            connectionStringException.Error);
        Assert.Equal(
            TierZeroPackageError.InvalidMetadata,
            rangeException.Error);
    }

    [Fact]
    public async Task SealRejectsSummaryReferenceThatDoesNotMatchArtifact()
    {
        using var directory = new PackageDirectory("summary-mismatch");
        var draft = CreateDraft();
        var mismatched = draft.SummaryReferences.ScopeManifests[0] with
        {
            Sha256 = Hash("wrong-summary"),
        };
        var writer = await TierZeroPackageWriter.CreateAsync(
            directory.Path,
            draft with
            {
                SummaryReferences = draft.SummaryReferences with
                {
                    ScopeManifests = [mismatched],
                },
            });
        foreach (var artifact in ArtifactSpecs)
            await writer.AddArtifactAsync(artifact.Registration, artifact.Content);

        var exception = await Assert.ThrowsAsync<TierZeroPackageException>(
            () => writer.SealAsync(FixedSealedAt));

        Assert.Equal(
            TierZeroPackageError.SummaryReferenceMismatch,
            exception.Error);
    }

    [Fact]
    public async Task VerifierDetectsModifiedMissingExtraAndChecksumCorruption()
    {
        await AssertVerificationFailureAsync(
            "modified",
            TierZeroVerificationFailureKind.ArtifactHashMismatch,
            root => File.WriteAllText(
                System.IO.Path.Combine(root, "data", "counts.csv"),
                "modified"));
        await AssertVerificationFailureAsync(
            "missing",
            TierZeroVerificationFailureKind.MissingFile,
            root => File.Delete(
                System.IO.Path.Combine(root, "data", "counts.csv")));
        await AssertVerificationFailureAsync(
            "extra",
            TierZeroVerificationFailureKind.ExtraFile,
            root => File.WriteAllText(
                System.IO.Path.Combine(root, "extra.txt"),
                "extra"));
        await AssertVerificationFailureAsync(
            "checksum",
            TierZeroVerificationFailureKind.ChecksumHashMismatch,
            root => File.AppendAllText(
                System.IO.Path.Combine(
                    root,
                    TierZeroEvidenceFormat.ChecksumFileName),
                "corrupt"));
        await AssertVerificationFailureAsync(
            "state-missing",
            TierZeroVerificationFailureKind.StateMissing,
            root => File.Delete(
                System.IO.Path.Combine(
                    root,
                    TierZeroEvidenceFormat.StateFileName)));
        await AssertVerificationFailureAsync(
            "state-mismatch",
            TierZeroVerificationFailureKind.StateMismatch,
            root =>
            {
                var statePath = System.IO.Path.Combine(
                    root,
                    TierZeroEvidenceFormat.StateFileName);
                File.WriteAllText(
                    statePath,
                    File.ReadAllText(statePath).Replace(
                        "tier0-test-producer",
                        "tier0-other-producer",
                        StringComparison.Ordinal));
            });
    }

    [Fact]
    public async Task FinalInventoryDetectsChangesAndEnumerationFailures()
    {
        using var changed = new PackageDirectory(
            "final-inventory-change");
        await CreateSealedPackageAsync(changed.Path);
        var changedInventory =
            TierZeroPackageFileEnumerator.Enumerate(changed.Path);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(changed.Path, "late-file"),
            "late");
        var changedFailures =
            new List<TierZeroVerificationFailure>();
        TierZeroPackageVerifier.VerifyStableFinalInventory(
            changed.Path,
            changedInventory,
            changedFailures);
        Assert.Contains(
            changedFailures,
            static failure =>
                failure.Kind ==
                TierZeroVerificationFailureKind.PackageChangedDuringVerification);

        using var linked = new PackageDirectory(
            "final-inventory-link");
        using var outside = new PackageDirectory(
            "final-inventory-link-outside");
        await CreateSealedPackageAsync(linked.Path);
        Directory.CreateDirectory(outside.Path);
        var linkedInventory =
            TierZeroPackageFileEnumerator.Enumerate(linked.Path);
        var data = System.IO.Path.Combine(linked.Path, "data");
        var movedData = System.IO.Path.Combine(
            linked.Path,
            "data-original");
        Directory.Move(data, movedData);
        try
        {
            Directory.CreateSymbolicLink(data, outside.Path);
        }
        catch (Exception unsupportedException) when (
            unsupportedException is UnauthorizedAccessException or
            PlatformNotSupportedException or
            IOException)
        {
            return;
        }
        var linkedFailures =
            new List<TierZeroVerificationFailure>();
        TierZeroPackageVerifier.VerifyStableFinalInventory(
            linked.Path,
            linkedInventory,
            linkedFailures);
        Assert.Contains(
            linkedFailures,
            static failure =>
                failure.Kind ==
                TierZeroVerificationFailureKind.PackageChangedDuringVerification);

        if (!OperatingSystem.IsWindows())
        {
            using var unreadable = new PackageDirectory(
                "final-inventory-unreadable");
            await CreateSealedPackageAsync(unreadable.Path);
            var unreadableInventory =
                TierZeroPackageFileEnumerator.Enumerate(unreadable.Path);
            var mode = File.GetUnixFileMode(unreadable.Path);
            try
            {
                File.SetUnixFileMode(
                    unreadable.Path,
                    UnixFileMode.None);
                var unreadableFailures =
                    new List<TierZeroVerificationFailure>();
                TierZeroPackageVerifier.VerifyStableFinalInventory(
                    unreadable.Path,
                    unreadableInventory,
                    unreadableFailures);
                Assert.Contains(
                    unreadableFailures,
                    static failure =>
                        failure.Kind ==
                        TierZeroVerificationFailureKind.PackageChangedDuringVerification);
            }
            finally
            {
                File.SetUnixFileMode(unreadable.Path, mode);
            }
        }
    }

    [Fact]
    public async Task VerifierRejectsResignedNonDraftStateJournal()
    {
        using var directory = new PackageDirectory(
            "verify-state-lifecycle");
        var manifest = await CreateSealedPackageAsync(directory.Path);
        var state = await ReadStateAsync(directory.Path);
        var stateBytes = TierZeroCanonicalJson.Serialize(
            state with
            {
                Status = TierZeroPackageStatus.Interrupted,
                Error = "synthetic interruption",
                InterruptedAtUtc = FixedCreatedAt.AddMinutes(1),
            });
        await File.WriteAllBytesAsync(
            StatePath(directory.Path),
            stateBytes);
        await File.WriteAllBytesAsync(
            ManifestPath(directory.Path),
            TierZeroCanonicalJson.SerializeSealedManifest(
                manifest with
                {
                        StateSha256 =
                            TierZeroCanonicalJson.Sha256Hex(stateBytes),
                }));

        AssertFailure(
            await TierZeroPackageVerifier.VerifyAsync(directory.Path),
            TierZeroVerificationFailureKind.StateMismatch);

        using var noncanonical = new PackageDirectory(
            "verify-state-noncanonical");
        var noncanonicalManifest =
            await CreateSealedPackageAsync(noncanonical.Path);
        var canonicalState = await File.ReadAllBytesAsync(
            StatePath(noncanonical.Path));
        var noncanonicalState =
            new byte[canonicalState.Length + 1];
        noncanonicalState[0] = (byte)' ';
        canonicalState.CopyTo(noncanonicalState, 1);
        await File.WriteAllBytesAsync(
            StatePath(noncanonical.Path),
            noncanonicalState);
        await File.WriteAllBytesAsync(
            ManifestPath(noncanonical.Path),
            TierZeroCanonicalJson.SerializeSealedManifest(
                noncanonicalManifest with
                {
                    StateSha256 =
                        TierZeroCanonicalJson.Sha256Hex(
                            noncanonicalState),
                }));
        AssertFailure(
            await TierZeroPackageVerifier.VerifyAsync(
                noncanonical.Path),
            TierZeroVerificationFailureKind.StateMismatch);
    }

    [Fact]
    public async Task VerifierDetectsManifestRootAndDuplicatePathCorruption()
    {
        using var rootMismatch = new PackageDirectory("root-mismatch");
        await CreateSealedPackageAsync(rootMismatch.Path);
        var manifest = await ReadManifestAsync(rootMismatch.Path);
        await File.WriteAllBytesAsync(
            ManifestPath(rootMismatch.Path),
            TierZeroCanonicalJson.Serialize(
                manifest with { ProducerIdentity = "tampered-producer" }));
        var rootResult = await TierZeroPackageVerifier.VerifyAsync(rootMismatch.Path);
        Assert.Contains(
            rootResult.Failures,
            static failure =>
                failure.Kind == TierZeroVerificationFailureKind.RootHashMismatch);

        using var duplicate = new PackageDirectory("manifest-duplicate");
        await CreateSealedPackageAsync(duplicate.Path);
        var duplicateManifest = await ReadManifestAsync(duplicate.Path);
        var original = duplicateManifest.Artifacts[0];
        var duplicated = original with
        {
            Path = original.Path.Replace('/', '\\'),
        };
        await File.WriteAllBytesAsync(
            ManifestPath(duplicate.Path),
            TierZeroCanonicalJson.SerializeSealedManifest(
                duplicateManifest with
                {
                    Artifacts =
                    [
                        .. duplicateManifest.Artifacts,
                        duplicated,
                    ],
                }));
        var duplicateResult = await TierZeroPackageVerifier.VerifyAsync(
            duplicate.Path);
        Assert.Contains(
            duplicateResult.Failures,
            static failure =>
                failure.Kind == TierZeroVerificationFailureKind.DuplicatePath);

        using var namespaceCollision = new PackageDirectory(
            "manifest-namespace-collision");
        await CreateSealedPackageAsync(namespaceCollision.Path);
        var namespaceManifest = await ReadManifestAsync(
            namespaceCollision.Path);
        await File.WriteAllBytesAsync(
            ManifestPath(namespaceCollision.Path),
            TierZeroCanonicalJson.SerializeSealedManifest(
                namespaceManifest with
                {
                    Artifacts =
                    [
                        namespaceManifest.Artifacts[0] with
                        {
                            Path = "namespace",
                        },
                        namespaceManifest.Artifacts[1] with
                        {
                            Path = "namespace/child.json",
                        },
                        .. namespaceManifest.Artifacts.Skip(2),
                    ],
                }));
        AssertFailure(
            await TierZeroPackageVerifier.VerifyAsync(
                namespaceCollision.Path),
            TierZeroVerificationFailureKind.DuplicatePath);
    }

    [Fact]
    public async Task VerifierReturnsTypedFailuresForMalformedMetadataAndChecksumPaths()
    {
        using var malformed = new PackageDirectory("malformed-manifest");
        await CreateSealedPackageAsync(malformed.Path);
        await File.WriteAllTextAsync(ManifestPath(malformed.Path), "{}");
        var malformedResult = await TierZeroPackageVerifier.VerifyAsync(
            malformed.Path);
        AssertFailure(
            malformedResult,
            TierZeroVerificationFailureKind.UnsupportedFormat);

        using var nullDigest = new PackageDirectory("null-oci-digest");
        await CreateSealedPackageAsync(nullDigest.Path);
        var nullDigestNode = JsonNode.Parse(
            await File.ReadAllTextAsync(
                ManifestPath(nullDigest.Path)))!.AsObject();
        nullDigestNode["build"]!["ociImageDigest"] = null;
        await File.WriteAllTextAsync(
            ManifestPath(nullDigest.Path),
            nullDigestNode.ToJsonString());
        AssertFailure(
            await TierZeroPackageVerifier.VerifyAsync(nullDigest.Path),
            TierZeroVerificationFailureKind.InvalidManifest);

        using var checksum = new PackageDirectory("checksum-traversal");
        var manifest = await CreateSealedPackageAsync(checksum.Path);
        var checksumBytes = Encoding.UTF8.GetBytes(
            $"{Hash("malicious")}  ../escape.json\n");
        await File.WriteAllBytesAsync(
            System.IO.Path.Combine(
                checksum.Path,
                TierZeroEvidenceFormat.ChecksumFileName),
            checksumBytes);
        await File.WriteAllBytesAsync(
            ManifestPath(checksum.Path),
            TierZeroCanonicalJson.SerializeSealedManifest(
                manifest with
                {
                    ChecksumManifest = manifest.ChecksumManifest with
                    {
                        EntryCount = 1,
                        Sha256 = TierZeroCanonicalJson.Sha256Hex(
                            checksumBytes),
                    },
                }));

        var checksumResult = await TierZeroPackageVerifier.VerifyAsync(
            checksum.Path);
        AssertFailure(
            checksumResult,
            TierZeroVerificationFailureKind.ChecksumContentMismatch);
    }

    [Fact]
    public async Task VerifierReturnsTypedFailuresForMissingAndNoncanonicalSystemFiles()
    {
        using var missing = new PackageDirectory("verify-package-missing");
        AssertFailure(
            await TierZeroPackageVerifier.VerifyAsync(missing.Path),
            TierZeroVerificationFailureKind.PackageNotFound);

        using var noManifest = new PackageDirectory("verify-manifest-missing");
        Directory.CreateDirectory(noManifest.Path);
        AssertFailure(
            await TierZeroPackageVerifier.VerifyAsync(noManifest.Path),
            TierZeroVerificationFailureKind.ManifestMissing);

        await AssertVerificationFailureAsync(
            "manifest-empty",
            TierZeroVerificationFailureKind.ManifestUnreadable,
            root => File.WriteAllBytes(ManifestPath(root), []));
        await AssertVerificationFailureAsync(
            "manifest-json",
            TierZeroVerificationFailureKind.ManifestUnreadable,
            root => File.WriteAllText(ManifestPath(root), "{"));
        await AssertVerificationFailureAsync(
            "manifest-noncanonical",
            TierZeroVerificationFailureKind.ManifestNotCanonical,
            root =>
            {
                var path = ManifestPath(root);
                File.WriteAllBytes(
                    path,
                    [.. " "u8.ToArray(), .. File.ReadAllBytes(path)]);
            });
        await AssertVerificationFailureAsync(
            "checksum-missing",
            TierZeroVerificationFailureKind.ChecksumMissing,
            root => File.Delete(System.IO.Path.Combine(
                root,
                TierZeroEvidenceFormat.ChecksumFileName)));
        await AssertVerificationFailureAsync(
            "checksum-oversized",
            TierZeroVerificationFailureKind.ChecksumUnreadable,
            root =>
            {
                using var stream = new FileStream(
                    System.IO.Path.Combine(
                        root,
                        TierZeroEvidenceFormat.ChecksumFileName),
                    FileMode.Open,
                    FileAccess.Write);
                stream.SetLength(16L * 1024 * 1024 + 1);
            });
        await AssertVerificationFailureAsync(
            "state-empty",
            TierZeroVerificationFailureKind.StateMismatch,
            root => File.WriteAllBytes(
                System.IO.Path.Combine(
                    root,
                    TierZeroEvidenceFormat.StateFileName),
                []));
        await AssertVerificationFailureAsync(
            "state-json",
            TierZeroVerificationFailureKind.StateMismatch,
            root => File.WriteAllText(
                System.IO.Path.Combine(
                    root,
                    TierZeroEvidenceFormat.StateFileName),
                "{"));
        await AssertVerificationFailureAsync(
            "state-invalid-metadata",
            TierZeroVerificationFailureKind.StateMismatch,
            root =>
            {
                var statePath = System.IO.Path.Combine(
                    root,
                    TierZeroEvidenceFormat.StateFileName);
                var json = File.ReadAllText(statePath)
                    .Replace(
                        "\"status\":\"draft\"",
                        "\"status\":\"interrupted\"",
                        StringComparison.Ordinal)
                    .Replace(
                        "\"updatedAtUtc\"",
                        "\"error\":\"https://private.example\",\"updatedAtUtc\"",
                        StringComparison.Ordinal);
                File.WriteAllText(statePath, json);
            });
        await AssertVerificationFailureAsync(
            "lock-missing",
            TierZeroVerificationFailureKind.LockMissing,
            root => File.Delete(System.IO.Path.Combine(
                root,
                TierZeroEvidenceFormat.LockFileName)));
        await AssertVerificationFailureAsync(
            "lock-mismatch",
            TierZeroVerificationFailureKind.LockMismatch,
            root => File.WriteAllText(
                System.IO.Path.Combine(
                root,
                TierZeroEvidenceFormat.LockFileName),
                "not-empty"));
    }

    [Fact]
    public async Task VerifierReturnsTypedFailuresForUnreadableFilesOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        await AssertUnreadableFailureAsync(
            "manifest-unreadable",
            TierZeroEvidenceFormat.ManifestFileName,
            TierZeroVerificationFailureKind.ManifestUnreadable);
        await AssertUnreadableFailureAsync(
            "checksum-unreadable",
            TierZeroEvidenceFormat.ChecksumFileName,
            TierZeroVerificationFailureKind.ChecksumUnreadable);
        await AssertUnreadableFailureAsync(
            "artifact-unreadable",
            "data/counts.csv",
            TierZeroVerificationFailureKind.ArtifactHashMismatch);
        await AssertUnreadableFailureAsync(
            "state-unreadable",
            TierZeroEvidenceFormat.StateFileName,
            TierZeroVerificationFailureKind.StateMismatch);

        using var package = new PackageDirectory("package-unreadable");
        await CreateSealedPackageAsync(package.Path);
        var packageMode = File.GetUnixFileMode(package.Path);
        try
        {
            File.SetUnixFileMode(package.Path, UnixFileMode.None);
            AssertFailure(
                await TierZeroPackageVerifier.VerifyAsync(package.Path),
                TierZeroVerificationFailureKind.ManifestUnreadable);
        }
        finally
        {
            File.SetUnixFileMode(package.Path, packageMode);
        }
    }

    [Fact]
    public async Task VerifierRejectsChecksumEncodingOrderingAndEntryMismatches()
    {
        await AssertResignedChecksumFailureAsync(
            "checksum-utf8",
            [0xff],
            1,
            TierZeroVerificationFailureKind.ChecksumUnreadable);
        await AssertResignedChecksumFailureAsync(
            "checksum-no-newline",
            Encoding.UTF8.GetBytes(
                $"{Hash("line")}  data/counts.csv"),
            1,
            TierZeroVerificationFailureKind.ChecksumContentMismatch);
        await AssertResignedChecksumFailureAsync(
            "checksum-malformed",
            Encoding.UTF8.GetBytes("malformed\n"),
            1,
            TierZeroVerificationFailureKind.ChecksumContentMismatch);
        await AssertResignedChecksumFailureAsync(
            "checksum-duplicate",
            Encoding.UTF8.GetBytes(
                $"{Hash("line")}  data/counts.csv\n" +
                $"{Hash("line")}  data/counts.csv\n"),
            2,
            TierZeroVerificationFailureKind.ChecksumContentMismatch);
        await AssertResignedChecksumFailureAsync(
            "checksum-order",
            Encoding.UTF8.GetBytes(
                $"{Hash("line")}  summaries/scope-manifests.json\n" +
                $"{Hash("line")}  data/counts.csv\n"),
            2,
            TierZeroVerificationFailureKind.ChecksumContentMismatch);
        await AssertMutatedCanonicalChecksumFailureAsync(
            "checksum-blank-line",
            bytes =>
            [
                .. bytes.AsSpan(0, bytes.Length / 2).ToArray(),
                (byte)'\n',
                .. bytes.AsSpan(bytes.Length / 2).ToArray(),
            ]);
        await AssertMutatedCanonicalChecksumFailureAsync(
            "checksum-backslash",
            bytes =>
            {
                var text = Encoding.UTF8.GetString(bytes);
                var slash = text.IndexOf('/');
                Assert.True(slash >= 0);
                return Encoding.UTF8.GetBytes(
                    text[..slash] + "\\" + text[(slash + 1)..]);
            });

        using var count = new PackageDirectory("checksum-count");
        var manifest = await CreateSealedPackageAsync(count.Path);
        await File.WriteAllBytesAsync(
            ManifestPath(count.Path),
            TierZeroCanonicalJson.SerializeSealedManifest(
                manifest with
                {
                    ChecksumManifest = manifest.ChecksumManifest with
                    {
                        EntryCount =
                            manifest.ChecksumManifest.EntryCount + 1,
                    },
                }));
        AssertFailure(
            await TierZeroPackageVerifier.VerifyAsync(count.Path),
            TierZeroVerificationFailureKind.ChecksumContentMismatch);

        using var entry = new PackageDirectory("checksum-entry");
        var entryManifest = await CreateSealedPackageAsync(entry.Path);
        var lines = entryManifest.Artifacts
            .OrderBy(static artifact => artifact.Path, StringComparer.Ordinal)
            .Select((artifact, index) =>
                $"{(index == 0 ? Hash("wrong-entry") : artifact.Sha256)}  {artifact.Path}\n");
        var entryBytes = Encoding.UTF8.GetBytes(string.Concat(lines));
        await File.WriteAllBytesAsync(
            System.IO.Path.Combine(
                entry.Path,
                TierZeroEvidenceFormat.ChecksumFileName),
            entryBytes);
        await File.WriteAllBytesAsync(
            ManifestPath(entry.Path),
            TierZeroCanonicalJson.SerializeSealedManifest(
                entryManifest with
                {
                    ChecksumManifest = entryManifest.ChecksumManifest with
                    {
                        Sha256 = TierZeroCanonicalJson.Sha256Hex(entryBytes),
                    },
                }));
        AssertFailure(
            await TierZeroPackageVerifier.VerifyAsync(entry.Path),
            TierZeroVerificationFailureKind.ChecksumContentMismatch);
    }

    [Fact]
    public async Task VerifierDetectsInvalidArtifactPathAndSummaryReference()
    {
        using var invalidPath = new PackageDirectory("invalid-artifact-path");
        var manifest = await CreateSealedPackageAsync(invalidPath.Path);
        await File.WriteAllBytesAsync(
            ManifestPath(invalidPath.Path),
            TierZeroCanonicalJson.SerializeSealedManifest(
                manifest with
                {
                    Artifacts =
                    [
                        manifest.Artifacts[0] with
                        {
                            Path = "../escape.json",
                        },
                        .. manifest.Artifacts.Skip(1),
                    ],
                }));
        AssertFailure(
            await TierZeroPackageVerifier.VerifyAsync(invalidPath.Path),
            TierZeroVerificationFailureKind.InvalidPath);

        using var summary = new PackageDirectory("verify-summary-mismatch");
        var summaryManifest = await CreateSealedPackageAsync(summary.Path);
        await File.WriteAllBytesAsync(
            ManifestPath(summary.Path),
            TierZeroCanonicalJson.SerializeSealedManifest(
                summaryManifest with
                {
                    SummaryReferences =
                        summaryManifest.SummaryReferences with
                        {
                            ScopeManifests =
                            [
                                summaryManifest.SummaryReferences
                                    .ScopeManifests[0] with
                                {
                                    Sha256 = Hash("wrong-summary"),
                                },
                            ],
                        },
                }));
        AssertFailure(
            await TierZeroPackageVerifier.VerifyAsync(summary.Path),
            TierZeroVerificationFailureKind.SummaryReferenceMismatch);

        using var nullArtifact = new PackageDirectory("null-artifact");
        await CreateSealedPackageAsync(nullArtifact.Path);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(
            ManifestPath(nullArtifact.Path)))!.AsObject();
        node["artifacts"]!.AsArray()[0] = null;
        await File.WriteAllTextAsync(
            ManifestPath(nullArtifact.Path),
            node.ToJsonString());
        AssertFailure(
            await TierZeroPackageVerifier.VerifyAsync(nullArtifact.Path),
            TierZeroVerificationFailureKind.InvalidManifest);
    }

    [Fact]
    public async Task VerifierRejectsNoncanonicalPhysicalPathAlias()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var directory = new PackageDirectory("filesystem-duplicate");
        await CreateSealedPackageAsync(directory.Path);
        var declared = System.IO.Path.Combine(
            directory.Path,
            "data",
            "counts.csv");
        var content = await File.ReadAllBytesAsync(declared);
        File.Delete(declared);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(directory.Path, "data\\counts.csv"),
            Encoding.UTF8.GetString(content));

        var result = await TierZeroPackageVerifier.VerifyAsync(directory.Path);

        AssertFailure(result, TierZeroVerificationFailureKind.InvalidPath);
    }

    [Fact]
    public async Task VerifierRejectsNoncanonicalUnicodePhysicalPath()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var directory = new PackageDirectory(
            "filesystem-unicode-normalization");
        await CreateSealedPackageAsync(directory.Path);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(
                directory.Path,
                "e\u0301vidence.json"),
            "{}");
        AssertFailure(
            await TierZeroPackageVerifier.VerifyAsync(directory.Path),
            TierZeroVerificationFailureKind.InvalidPath);
    }

    [Fact]
    public async Task VerifierDetectsParentConfigurationSchemaAndPhaseMismatches()
    {
        using var directory = new PackageDirectory("expectations");
        var manifest = await CreateSealedPackageAsync(directory.Path);

        var parent = await TierZeroPackageVerifier.VerifyAsync(
            directory.Path,
            Expected(manifest) with
            {
                ParentRootHashes =
                [
                    new TierZeroParentRootHash("capture", Hash("wrong")),
                ],
            });
        var configuration = await TierZeroPackageVerifier.VerifyAsync(
            directory.Path,
            Expected(manifest) with
            {
                ConfigurationValuesSha256 = Hash("wrong-config"),
            });
        var schema = await TierZeroPackageVerifier.VerifyAsync(
            directory.Path,
            Expected(manifest) with
            {
                DatabaseSchemaFingerprint = Hash("wrong-schema"),
            });
        var phase = await TierZeroPackageVerifier.VerifyAsync(
            directory.Path,
            Expected(manifest) with
            {
                PhasePlanVersion = "fst.scrape-plan.v999",
            });
        var invalidParent = await TierZeroPackageVerifier.VerifyAsync(
            directory.Path,
            Expected(manifest) with
            {
                ParentRootHashes =
                [
                    new TierZeroParentRootHash("capture", "invalid"),
                ],
            });

        AssertFailure(parent, TierZeroVerificationFailureKind.ParentMismatch);
        AssertFailure(
            configuration,
            TierZeroVerificationFailureKind.ConfigurationMismatch);
        AssertFailure(schema, TierZeroVerificationFailureKind.SchemaMismatch);
        AssertFailure(phase, TierZeroVerificationFailureKind.PhasePlanMismatch);
        AssertFailure(
            invalidParent,
            TierZeroVerificationFailureKind.ParentMismatch);
    }

    [Fact]
    public async Task VerifierDetectsChangedStablePhaseDescriptor()
    {
        using var directory = new PackageDirectory("phase-descriptor");
        var manifest = await CreateSealedPackageAsync(directory.Path);
        var changed = manifest.PhasePlan.Phases[0] with
        {
            Label = "Changed label",
        };
        await File.WriteAllBytesAsync(
            ManifestPath(directory.Path),
            TierZeroCanonicalJson.SerializeSealedManifest(
                manifest with
                {
                    PhasePlan = manifest.PhasePlan with
                    {
                        Phases =
                        [
                            changed,
                            .. manifest.PhasePlan.Phases.Skip(1),
                        ],
                    },
                }));

        var result = await TierZeroPackageVerifier.VerifyAsync(
            directory.Path,
            Expected(manifest));

        AssertFailure(result, TierZeroVerificationFailureKind.PhasePlanMismatch);
    }

    [Fact]
    public async Task VerifierDistinguishesUnsealedPackage()
    {
        using var directory = new PackageDirectory("unsealed");
        var writer = await TierZeroPackageWriter.CreateAsync(
            directory.Path,
            CreateDraft());
        await writer.MarkInterruptedAsync(
            "interrupted",
            FixedCreatedAt.AddMinutes(1));

        var result = await TierZeroPackageVerifier.VerifyAsync(directory.Path);

        Assert.False(result.IsValid);
        AssertFailure(result, TierZeroVerificationFailureKind.UnsealedPackage);
    }

    [Fact]
    public async Task SymbolicLinkEscapeIsRejectedWhenSupported()
    {
        using var directory = new PackageDirectory("symlink");
        using var outside = new PackageDirectory("symlink-outside");
        var writer = await TierZeroPackageWriter.CreateAsync(
            directory.Path,
            CreateDraft());
        Directory.CreateDirectory(outside.Path);
        var link = System.IO.Path.Combine(directory.Path, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside.Path);
        }
        catch (Exception unsupportedException) when (
            unsupportedException is UnauthorizedAccessException or
            PlatformNotSupportedException or
            IOException)
        {
            return;
        }

        var exception = await Assert.ThrowsAsync<TierZeroPackageException>(
            () => writer.AddArtifactAsync(
                Registration("linked/escape.json", rowCount: 1),
                "{}"u8.ToArray()));

        Assert.Equal(TierZeroPackageError.SymbolicLinkDetected, exception.Error);
        Assert.False(File.Exists(
            System.IO.Path.Combine(outside.Path, "escape.json")));
    }

    [Fact]
    public async Task PackageRootRejectsSymbolicLinkAncestorWhenSupported()
    {
        using var container = new PackageDirectory("symlink-ancestor");
        using var outside = new PackageDirectory("symlink-ancestor-outside");
        Directory.CreateDirectory(container.Path);
        Directory.CreateDirectory(outside.Path);
        var link = System.IO.Path.Combine(container.Path, "linked-root");
        try
        {
            Directory.CreateSymbolicLink(link, outside.Path);
        }
        catch (Exception unsupportedException) when (
            unsupportedException is UnauthorizedAccessException or
            PlatformNotSupportedException or
            IOException)
        {
            return;
        }

        try
        {
            var exception = await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroPackageWriter.CreateAsync(
                    System.IO.Path.Combine(link, "package"),
                    CreateDraft()));
            Assert.Equal(
                TierZeroPackageError.SymbolicLinkDetected,
                exception.Error);
            Assert.False(Directory.Exists(
                System.IO.Path.Combine(outside.Path, "package")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task WriterRejectsDanglingLockSymlinkWithoutCreatingTarget()
    {
        using var directory = new PackageDirectory(
            "dangling-lock-symlink");
        using var outside = new PackageDirectory(
            "dangling-lock-target");
        Directory.CreateDirectory(directory.Path);
        Directory.CreateDirectory(outside.Path);
        var target = System.IO.Path.Combine(
            outside.Path,
            "missing-lock-target");
        var lockPath = System.IO.Path.Combine(
            directory.Path,
            TierZeroEvidenceFormat.LockFileName);
        try
        {
            File.CreateSymbolicLink(lockPath, target);
        }
        catch (Exception unsupportedException) when (
            unsupportedException is UnauthorizedAccessException or
            PlatformNotSupportedException or
            IOException)
        {
            return;
        }

        var exception =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroPackageWriter.CreateAsync(
                    directory.Path,
                    CreateDraft()));

        Assert.Equal(
            TierZeroPackageError.SymbolicLinkDetected,
            exception.Error);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task VerifierRejectsArtifactReplacedBySymbolicLinkWhenSupported()
    {
        using var directory = new PackageDirectory("verify-symlink");
        using var outside = new PackageDirectory("verify-symlink-outside");
        await CreateSealedPackageAsync(directory.Path);
        Directory.CreateDirectory(outside.Path);
        var artifactPath = System.IO.Path.Combine(
            directory.Path,
            "data",
            "counts.csv");
        var outsidePath = System.IO.Path.Combine(
            outside.Path,
            "counts.csv");
        File.Copy(artifactPath, outsidePath);
        File.Delete(artifactPath);
        try
        {
            File.CreateSymbolicLink(artifactPath, outsidePath);
        }
        catch (Exception unsupportedException) when (
            unsupportedException is UnauthorizedAccessException or
            PlatformNotSupportedException or
            IOException)
        {
            return;
        }

        var result = await TierZeroPackageVerifier.VerifyAsync(directory.Path);

        AssertFailure(
            result,
            TierZeroVerificationFailureKind.SymbolicLinkDetected);
    }

    [Fact]
    public async Task RegularFileReaderRejectsReplacementAfterSnapshot()
    {
        using var directory = new PackageDirectory(
            "snapshot-replacement");
        using var outside = new PackageDirectory(
            "snapshot-replacement-outside");
        Directory.CreateDirectory(directory.Path);
        Directory.CreateDirectory(outside.Path);
        var path = System.IO.Path.Combine(
            directory.Path,
            "artifact.bin");
        var outsidePath = System.IO.Path.Combine(
            outside.Path,
            "artifact.bin");
        await File.WriteAllTextAsync(path, "original");
        await File.WriteAllTextAsync(outsidePath, "original");
        var snapshot = TierZeroRegularFile.Inspect(path);
        File.Delete(path);
        try
        {
            File.CreateSymbolicLink(path, outsidePath);
        }
        catch (Exception unsupportedException) when (
            unsupportedException is UnauthorizedAccessException or
            PlatformNotSupportedException or
            IOException)
        {
            return;
        }

        var exception =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroRegularFile.HashAsync(
                    path,
                    snapshot,
                    CancellationToken.None));

        Assert.Equal(
            TierZeroPackageError.SymbolicLinkDetected,
            exception.Error);
    }

    [Fact]
    public async Task RegularFileIoRejectsReplacedAncestorDirectory()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var directory = new PackageDirectory(
            "ancestor-replacement");
        using var outside = new PackageDirectory(
            "ancestor-replacement-outside");
        var parent = System.IO.Path.Combine(
            directory.Path,
            "data");
        var movedParent = System.IO.Path.Combine(
            directory.Path,
            "data-original");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(outside.Path);
        var path = System.IO.Path.Combine(parent, "artifact.bin");
        await File.WriteAllTextAsync(path, "original");
        var snapshot = TierZeroRegularFile.Inspect(path);
        Directory.Move(parent, movedParent);
        try
        {
            Directory.CreateSymbolicLink(parent, outside.Path);
        }
        catch (Exception unsupportedException) when (
            unsupportedException is UnauthorizedAccessException or
            PlatformNotSupportedException or
            IOException)
        {
            return;
        }

        var readException =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroRegularFile.HashAsync(
                    path,
                    snapshot,
                    CancellationToken.None));
        var output = System.IO.Path.Combine(
            parent,
            "output.partial-123-" + new string('a', 32));
        var writeException = Assert.Throws<TierZeroPackageException>(
            () => TierZeroRegularFile.CreateNewWrite(
                output,
                1024));
        var directoryException =
            Assert.Throws<TierZeroPackageException>(
                () => TierZeroRegularFile.CreateDirectoryUnderRoot(
                    directory.Path,
                    "data/nested"));

        Assert.Equal(
            TierZeroPackageError.SymbolicLinkDetected,
            readException.Error);
        Assert.Equal(
            TierZeroPackageError.SymbolicLinkDetected,
            writeException.Error);
        Assert.Equal(
            TierZeroPackageError.SymbolicLinkDetected,
            directoryException.Error);
        Assert.False(File.Exists(System.IO.Path.Combine(
            outside.Path,
            System.IO.Path.GetFileName(output))));
        Assert.False(Directory.Exists(System.IO.Path.Combine(
            outside.Path,
            "nested")));
    }

    [Fact]
    public async Task VerifierRejectsFifoWithoutBlocking()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var directory = new PackageDirectory("verify-fifo");
        await CreateSealedPackageAsync(directory.Path);
        var artifactPath = System.IO.Path.Combine(
            directory.Path,
            "data",
            "counts.csv");
        File.Delete(artifactPath);
        using (var process = Process.Start(new ProcessStartInfo
               {
                   FileName = "mkfifo",
                   ArgumentList = { artifactPath },
                   RedirectStandardError = true,
                   UseShellExecute = false,
               })!)
        {
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await TierZeroPackageVerifier.VerifyAsync(directory.Path);
        stopwatch.Stop();

        AssertFailure(result, TierZeroVerificationFailureKind.InvalidPath);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WriterRejectsFifoLockWithoutBlocking()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var directory = new PackageDirectory("fifo-lock");
        Directory.CreateDirectory(directory.Path);
        var lockPath = System.IO.Path.Combine(
            directory.Path,
            TierZeroEvidenceFormat.LockFileName);
        using (var process = Process.Start(new ProcessStartInfo
               {
                   FileName = "mkfifo",
                   ArgumentList = { lockPath },
                   RedirectStandardError = true,
                   UseShellExecute = false,
               })!)
        {
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }

        var stopwatch = Stopwatch.StartNew();
        var exception =
            await Assert.ThrowsAsync<TierZeroPackageException>(
                () => TierZeroPackageWriter.CreateAsync(
                    directory.Path,
                    CreateDraft()));
        stopwatch.Stop();

        Assert.Equal(TierZeroPackageError.InvalidPath, exception.Error);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SyntheticFixtureNeverUsesProductionPathsNetworkOrDatabase()
    {
        using var directory = new PackageDirectory("bounded-fixture");
        await CreateSealedPackageAsync(directory.Path);

        var expectedRoot =
            Environment.GetEnvironmentVariable("FST_TIER0_FIXTURE_OUTPUT")
            ?? AppContext.BaseDirectory;
        Assert.StartsWith(
            System.IO.Path.GetFullPath(expectedRoot),
            System.IO.Path.GetFullPath(directory.Path),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        Assert.All(
            Directory.EnumerateFiles(
                directory.Path,
                "*",
                SearchOption.AllDirectories),
            file => Assert.StartsWith(
                System.IO.Path.GetFullPath(directory.Path),
                System.IO.Path.GetFullPath(file),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal));
    }

    private static readonly DateTimeOffset FixedCreatedAt =
        new(2026, 8, 14, 8, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset FixedSealedAt =
        FixedCreatedAt.AddMinutes(5);

    private static readonly IReadOnlyList<ArtifactSpec> ArtifactSpecs =
        CreateArtifactSpecs();

    private static async Task<TierZeroEvidenceManifest> CreateSealedPackageAsync(
        string root,
        bool reverseInputs = false) =>
        await CreateSealedPackageAsync(root, CreateDraft(reverseInputs), reverseInputs);

    private static async Task<TierZeroEvidenceManifest> CreateSealedPackageAsync(
        string root,
        TierZeroPackageDraft draft,
        bool reverseInputs = false)
    {
        var writer = await TierZeroPackageWriter.CreateAsync(root, draft);
        var artifacts = reverseInputs
            ? ArtifactSpecs.Reverse()
            : ArtifactSpecs;
        foreach (var artifact in artifacts)
            await writer.AddArtifactAsync(artifact.Registration, artifact.Content);
        return await writer.SealAsync(FixedSealedAt);
    }

    private static async Task<TierZeroPackageWriter> CreateWriterWithAllArtifactsAsync(
        string root)
    {
        var writer = await TierZeroPackageWriter.CreateAsync(
            root,
            CreateDraft());
        foreach (var artifact in ArtifactSpecs)
            await writer.AddArtifactAsync(artifact.Registration, artifact.Content);
        return writer;
    }

    private static async Task<SealOutcome> CaptureSealAsync(
        TierZeroPackageWriter writer)
    {
        try
        {
            return new SealOutcome(
                await writer.SealAsync(FixedSealedAt),
                null);
        }
        catch (Exception exception)
        {
            return new SealOutcome(null, exception);
        }
    }

    private static async Task CreateInterruptedWithFirstArtifactAsync(
        string root)
    {
        var writer = await TierZeroPackageWriter.CreateAsync(
            root,
            CreateDraft());
        await writer.AddArtifactAsync(
            ArtifactSpecs[0].Registration,
            ArtifactSpecs[0].Content);
        await writer.MarkInterruptedAsync(
            "synthetic interruption",
            FixedCreatedAt.AddMinutes(1));
    }

    private static TierZeroPackageDraft CreateDraft(bool reverseInputs = false)
    {
        var configValues = reverseInputs
            ? new Dictionary<string, string?>
            {
                ["Scraper:SequentialScrape"] = "false",
                ["Scraper:PageConcurrency"] = "32",
            }
            : new Dictionary<string, string?>
            {
                ["Scraper:PageConcurrency"] = "32",
                ["Scraper:SequentialScrape"] = "false",
            };
        var references = References();
        var parents = new[]
        {
            new TierZeroParentRootHash("capture", Hash("capture-parent")),
            new TierZeroParentRootHash("catalog", Hash("catalog-parent")),
        };

        return new TierZeroPackageDraft(
            "tier0-synthetic-package",
            new TierZeroSourceIdentity(
                1296,
                61,
                FixedCreatedAt.AddMinutes(-10),
                new TierZeroCatalogIdentity(
                    "festival-catalog-2026-08-14",
                    Hash("catalog"))),
            new TierZeroBuildIdentity(
                new string('a', 40),
                $"sha256:{Hash("image")}",
                new string('b', 40),
                "1.0.196"),
            new TierZeroDatabaseIdentity(
                17,
                reverseInputs
                    ? ["pg_trgm@1.6", "btree_gin@1.3"]
                    : ["btree_gin@1.3", "pg_trgm@1.6"],
                Hash("schema")),
            TierZeroConfigurationFingerprinter.Create(
                configValues,
                reverseInputs
                    ? ["Scraper:SequentialScrape", "Scraper:PageConcurrency"]
                    : ["Scraper:PageConcurrency", "Scraper:SequentialScrape"]),
            reverseInputs
                ? new TierZeroSummaryReferences(
                    references.ScopeManifests.Reverse().ToArray(),
                    references.ScopeFingerprints.Reverse().ToArray(),
                    references.PhaseOutcomes.Reverse().ToArray(),
                    references.PhaseTimings.Reverse().ToArray())
                : references,
            reverseInputs ? parents.Reverse().ToArray() : parents,
            1,
            "tier0-test-producer",
            FixedCreatedAt);
    }

    private static TierZeroSummaryReferences References() =>
        new(
            [Reference("scope-manifest", "summaries/scope-manifests.json")],
            [Reference("scope-fingerprint", "summaries/scope-fingerprints.json")],
            [Reference("phase-outcome", "summaries/phase-outcomes.json")],
            [Reference("phase-timing", "summaries/phase-timings.json")]);

    private static TierZeroSummaryReference Reference(
        string owner,
        string path)
    {
        var artifact = Assert.Single(
            ArtifactSpecs,
            candidate => TierZeroPackagePath.Normalize(
                candidate.Registration.Path) == path);
        return new TierZeroSummaryReference(
            owner,
            path,
            TierZeroCanonicalJson.Sha256Hex(artifact.Content),
            artifact.Registration.RowCount);
    }

    private static IReadOnlyList<ArtifactSpec> CreateArtifactSpecs() =>
    [
        JsonArtifact(
            "scope-manifest",
            "summaries/scope-manifests.json",
            """{"scopeCount":2}""",
            2),
        JsonArtifact(
            "scope-fingerprint",
            "summaries/scope-fingerprints.json",
            """{"fingerprintCount":2}""",
            2),
        JsonArtifact(
            "phase-outcome",
            "summaries/phase-outcomes.json",
            """{"outcomeCount":28}""",
            28),
        JsonArtifact(
            "phase-timing",
            "summaries/phase-timings.json",
            """{"timingCount":28}""",
            28),
        new ArtifactSpec(
            new TierZeroArtifactRegistration(
                "score-evidence",
                "data\\counts.csv",
                "text/csv",
                3,
                long.MaxValue,
                Encoding.UTF8.GetByteCount(
                    "score\n1\n999999\nartifact-secret-sentinel"),
                [new TierZeroArtifactRange("score", "1", "999999")]),
            Encoding.UTF8.GetBytes(
                "score\n1\n999999\nartifact-secret-sentinel")),
        new ArtifactSpec(
            new TierZeroArtifactRegistration(
                "empty-boundary",
                "data/empty.bin",
                "application/octet-stream",
                1,
                0,
                0),
            []),
    ];

    private static ArtifactSpec JsonArtifact(
        string owner,
        string path,
        string json,
        long rowCount)
    {
        var content = Encoding.UTF8.GetBytes(json);
        return new ArtifactSpec(
            new TierZeroArtifactRegistration(
                owner,
                path,
                "application/json",
                1,
                rowCount,
                content.LongLength),
            content);
    }

    private static TierZeroArtifactRegistration Registration(
        string path,
        long rowCount) =>
        new(
            "test",
            path,
            "application/json",
            1,
            rowCount,
            0);

    private static TierZeroResumeExpectations ExpectedResume()
    {
        var draft = CreateDraft();
        return new TierZeroResumeExpectations(
            draft.PackageId,
            draft.Attempt,
            draft.ProducerIdentity,
            draft.ParentRootHashes,
            draft.Configuration.ValuesSha256,
            draft.Database.SchemaFingerprint,
            PhaseProgressCatalog.OperationId,
            PhaseProgressCatalog.PlanVersion);
    }

    private static TierZeroVerificationExpectations Expected(
        TierZeroEvidenceManifest manifest) =>
        new(
            manifest.ParentRootHashes,
            manifest.Configuration.ValuesSha256,
            manifest.Database.SchemaFingerprint,
            manifest.PhasePlan.Id,
            manifest.PhasePlan.Version);

    private static async Task<TierZeroEvidenceManifest> ReadManifestAsync(
        string root) =>
        TierZeroCanonicalJson.Deserialize<TierZeroEvidenceManifest>(
            await File.ReadAllBytesAsync(ManifestPath(root)));

    private static TierZeroArtifactDescriptor PendingDescriptor(
        byte[] content) =>
        new(
            "pending-test",
            "data/pending.json",
            "application/json",
            1,
            1,
            [],
            content.LongLength,
            content.LongLength,
            TierZeroCanonicalJson.Sha256Hex(content));

    private static async Task WritePendingStateAsync(
        string root,
        TierZeroArtifactDescriptor descriptor,
        string temporaryRelative)
    {
        var state = await ReadStateAsync(root);
        await File.WriteAllBytesAsync(
            StatePath(root),
            TierZeroCanonicalJson.Serialize(
                state with
                {
                    PendingArtifact = new TierZeroPendingArtifact(
                        descriptor,
                        temporaryRelative),
                }));
    }

    private static async Task<TierZeroPackageState> ReadStateAsync(
        string root) =>
        TierZeroCanonicalJson.Deserialize<TierZeroPackageState>(
            await File.ReadAllBytesAsync(StatePath(root)));

    private static string ManifestPath(string root) =>
        System.IO.Path.Combine(
            root,
            TierZeroEvidenceFormat.ManifestFileName);

    private static string StatePath(string root) =>
        System.IO.Path.Combine(
            root,
            TierZeroEvidenceFormat.StateFileName);

    private static async Task AssertVerificationFailureAsync(
        string name,
        TierZeroVerificationFailureKind expected,
        Action<string> mutate)
    {
        using var directory = new PackageDirectory($"verify-{name}");
        await CreateSealedPackageAsync(directory.Path);
        mutate(directory.Path);

        var result = await TierZeroPackageVerifier.VerifyAsync(directory.Path);

        AssertFailure(result, expected);
    }

    private static async Task AssertResignedChecksumFailureAsync(
        string name,
        byte[] checksumBytes,
        int entryCount,
        TierZeroVerificationFailureKind expected)
    {
        using var directory = new PackageDirectory(name);
        var manifest = await CreateSealedPackageAsync(directory.Path);
        await File.WriteAllBytesAsync(
            System.IO.Path.Combine(
                directory.Path,
                TierZeroEvidenceFormat.ChecksumFileName),
            checksumBytes);
        await File.WriteAllBytesAsync(
            ManifestPath(directory.Path),
            TierZeroCanonicalJson.SerializeSealedManifest(
                manifest with
                {
                    ChecksumManifest = manifest.ChecksumManifest with
                    {
                        EntryCount = entryCount,
                        Sha256 = TierZeroCanonicalJson.Sha256Hex(
                            checksumBytes),
                    },
                }));

        AssertFailure(
            await TierZeroPackageVerifier.VerifyAsync(directory.Path),
            expected);
    }

    private static async Task AssertMutatedCanonicalChecksumFailureAsync(
        string name,
        Func<byte[], byte[]> mutate)
    {
        using var directory = new PackageDirectory(name);
        var manifest = await CreateSealedPackageAsync(directory.Path);
        var checksumBytes = mutate(
            TierZeroPackageWriter.CreateChecksumManifest(
                manifest.Artifacts));
        await File.WriteAllBytesAsync(
            System.IO.Path.Combine(
                directory.Path,
                TierZeroEvidenceFormat.ChecksumFileName),
            checksumBytes);
        await File.WriteAllBytesAsync(
            ManifestPath(directory.Path),
            TierZeroCanonicalJson.SerializeSealedManifest(
                manifest with
                {
                    ChecksumManifest = manifest.ChecksumManifest with
                    {
                        Sha256 = TierZeroCanonicalJson.Sha256Hex(
                            checksumBytes),
                    },
                }));

        AssertFailure(
            await TierZeroPackageVerifier.VerifyAsync(directory.Path),
            TierZeroVerificationFailureKind.ChecksumContentMismatch);
    }

    private static async Task AssertUnreadableFailureAsync(
        string name,
        string relativePath,
        TierZeroVerificationFailureKind expected)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        using var directory = new PackageDirectory(name);
        await CreateSealedPackageAsync(directory.Path);
        var path = System.IO.Path.Combine(
            directory.Path,
            relativePath.Replace(
                '/',
                System.IO.Path.DirectorySeparatorChar));
        var originalMode = File.GetUnixFileMode(path);
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.None);
            AssertFailure(
                await TierZeroPackageVerifier.VerifyAsync(directory.Path),
                expected);
        }
        finally
        {
            File.SetUnixFileMode(path, originalMode);
        }
    }

    private static void AssertFailure(
        TierZeroVerificationResult result,
        TierZeroVerificationFailureKind expected)
    {
        Assert.False(result.IsValid);
        Assert.Contains(
            result.Failures,
            failure => failure.Kind == expected);
    }

    private static void AssertInvalid(Action action)
    {
        var exception = Assert.Throws<TierZeroPackageException>(action);
        Assert.Equal(TierZeroPackageError.InvalidMetadata, exception.Error);
    }

    private static void AssertResumeFailure(
        TierZeroPackageException exception,
        TierZeroPackageError expected) =>
        Assert.Equal(expected, exception.Error);

    private static string Hash(string value) =>
        TierZeroCanonicalJson.Sha256Hex(Encoding.UTF8.GetBytes(value));

    private sealed record ArtifactSpec(
        TierZeroArtifactRegistration Registration,
        byte[] Content);

    private sealed record SealOutcome(
        TierZeroEvidenceManifest? Manifest,
        Exception? Exception);

    private sealed class UnreadableStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("synthetic read failure");
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class PackageDirectory : IDisposable
    {
        private readonly bool _preserve;

        public PackageDirectory(string name)
        {
            var preserved = name == "bounded-fixture"
                ? Environment.GetEnvironmentVariable(
                    "FST_TIER0_FIXTURE_OUTPUT")
                : null;
            if (!string.IsNullOrWhiteSpace(preserved))
            {
                Path = System.IO.Path.GetFullPath(preserved);
                var workingRoot = FindRepositoryRoot();
                var comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!Path.StartsWith(
                        System.IO.Path.TrimEndingDirectorySeparator(
                            workingRoot) +
                        System.IO.Path.DirectorySeparatorChar,
                        comparison))
                {
                    throw new InvalidOperationException(
                        "Preserved Tier-0 test fixtures must remain under the repository working directory.");
                }
                _preserve = true;
            }
            else
            {
                Path = System.IO.Path.Combine(
                    AppContext.BaseDirectory,
                    "tier0-test-artifacts",
                    $"{name}-{Guid.NewGuid():N}");
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!_preserve && Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(System.IO.Path.Combine(
                        current.FullName,
                        "FortniteFestivalLeaderboardScraper.sln")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }

            throw new InvalidOperationException(
                "Could not locate repository root for preserved Tier-0 fixture.");
        }
    }
}
